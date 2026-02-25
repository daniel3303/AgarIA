using AgarIA.Core.Data.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AgarIA.Core.AI;

public class PPOTrainer
{
    private readonly ILogger _logger;
    private readonly string _savePath;
    private readonly PPOSettings _hp;
    private readonly object _lock = new();

    private readonly List<Transition> _buffer = new();
    private AdamOptimizer _optimizer;
    private DateTime _lastSaveTime = DateTime.MinValue;

    // Training stats
    private int _totalUpdates;
    private float _avgReward;
    private float _lastPolicyLoss;
    private float _lastValueLoss;
    private float _lastEntropy;

    private static readonly float Log2Pi = MathF.Log(2f * MathF.PI);
    private static readonly Random Rng = new();

    private struct Transition
    {
        public float[] State;
        public float MoveXAction;
        public float MoveYAction;
        public bool SplitAction;
        public float Reward;
        public float ValueEstimate;
        public float LogProb;
        public bool Done;
    }

    public PPOTrainer(ILogger logger, string savePath, PPOSettings hp, int paramCount)
    {
        _logger = logger;
        _savePath = savePath;
        _hp = hp;
        _optimizer = new AdamOptimizer(paramCount, hp.LearningRate, hp.MaxGradNorm);
    }

    public void AddTransition(float[] state, float moveX, float moveY, bool split,
        float reward, float valueEstimate, float logProb)
    {
        lock (_lock)
        {
            _buffer.Add(new Transition
            {
                State = state,
                MoveXAction = moveX,
                MoveYAction = moveY,
                SplitAction = split,
                Reward = reward,
                ValueEstimate = valueEstimate,
                LogProb = logProb,
                Done = false
            });
        }
    }

    public void AddTerminal(float[] state, float reward, float valueEstimate, float logProb)
    {
        lock (_lock)
        {
            _buffer.Add(new Transition
            {
                State = state,
                MoveXAction = 0, MoveYAction = 0, SplitAction = false,
                Reward = reward,
                ValueEstimate = valueEstimate,
                LogProb = logProb,
                Done = true
            });
        }
    }

    public bool ShouldTrain()
    {
        lock (_lock)
            return _buffer.Count >= _hp.BufferSize;
    }

    public void Train(ActorCriticNetwork network)
    {
        Transition[] data;
        lock (_lock)
        {
            if (_buffer.Count < _hp.BufferSize) return;
            data = _buffer.Take(_hp.BufferSize).ToArray();
            _buffer.RemoveRange(0, _hp.BufferSize);
        }

        int n = data.Length;

        // 1. Compute advantages using GAE-lambda
        var advantages = new float[n];
        var returns = new float[n];

        // Bootstrap value for last state (0 if terminal)
        float lastValue = data[n - 1].Done ? 0f : data[n - 1].ValueEstimate;

        float gae = 0;
        for (int t = n - 1; t >= 0; t--)
        {
            float nextValue = (t == n - 1) ? lastValue : (data[t + 1].Done ? 0 : data[t + 1].ValueEstimate);
            float delta = data[t].Reward + _hp.Gamma * nextValue * (data[t].Done ? 0 : 1) - data[t].ValueEstimate;
            gae = delta + _hp.Gamma * _hp.Lambda * (data[t].Done ? 0 : 1) * gae;
            advantages[t] = gae;
            returns[t] = gae + data[t].ValueEstimate;
        }

        // 2. Normalize advantages
        float advMean = 0, advVar = 0;
        for (int i = 0; i < n; i++) advMean += advantages[i];
        advMean /= n;
        for (int i = 0; i < n; i++) advVar += (advantages[i] - advMean) * (advantages[i] - advMean);
        advVar /= n;
        float advStd = MathF.Sqrt(advVar + 1e-8f);
        for (int i = 0; i < n; i++) advantages[i] = (advantages[i] - advMean) / advStd;

        // 3. PPO epochs
        var indices = Enumerable.Range(0, n).ToArray();
        var parameters = network.GetParameters();
        float totalPolicyLoss = 0, totalValueLoss = 0, totalEntropy = 0;
        int updateCount = 0;

        for (int epoch = 0; epoch < _hp.Epochs; epoch++)
        {
            Shuffle(indices);

            for (int start = 0; start + _hp.MinibatchSize <= n; start += _hp.MinibatchSize)
            {
                var grads = new float[parameters.Length];

                for (int mb = 0; mb < _hp.MinibatchSize; mb++)
                {
                    int idx = indices[start + mb];
                    var trans = data[idx];

                    var (policy, value, preActs, postActs) = network.ForwardFull(trans.State);

                    // Compute new log prob
                    float newLogProb = ComputeLogProb(policy, network.LogStd,
                        trans.MoveXAction, trans.MoveYAction, trans.SplitAction);

                    float ratio = MathF.Exp(newLogProb - trans.LogProb);
                    float adv = advantages[idx];

                    // Clipped surrogate loss
                    float surr1 = ratio * adv;
                    float surr2 = Math.Clamp(ratio, 1f - _hp.ClipEpsilon, 1f + _hp.ClipEpsilon) * adv;
                    float policyLoss = -MathF.Min(surr1, surr2);

                    // Value loss
                    float valueLoss = 0.5f * (value - returns[idx]) * (value - returns[idx]);

                    // Entropy bonus
                    float entropy = ComputeEntropy(policy, network.LogStd);

                    float totalLoss = policyLoss + _hp.ValueCoeff * valueLoss - _hp.EntropyCoeff * entropy;

                    totalPolicyLoss += policyLoss;
                    totalValueLoss += valueLoss;
                    totalEntropy += entropy;

                    // Compute gradients of loss w.r.t. policy outputs and value
                    // dLoss/dPolicy = d(policyLoss)/dPolicy - entropyCoeff * d(entropy)/dPolicy
                    var dPolicy = ComputePolicyGradient(policy, network.LogStd,
                        trans.MoveXAction, trans.MoveYAction, trans.SplitAction,
                        trans.LogProb, adv);

                    // dLoss/dValue = valueCoeff * (value - return)
                    float dValue = _hp.ValueCoeff * (value - returns[idx]);

                    // Backprop through network
                    var sampleGrads = network.Backward(trans.State, dPolicy, dValue, preActs, postActs);

                    // Add logStd gradients
                    var logStdGrads = ComputeLogStdGradient(policy, network.LogStd,
                        trans.MoveXAction, trans.MoveYAction, trans.LogProb, adv);
                    int logStdOffset = parameters.Length - 2;
                    sampleGrads[logStdOffset] += logStdGrads[0];
                    sampleGrads[logStdOffset + 1] += logStdGrads[1];

                    // Accumulate
                    for (int i = 0; i < grads.Length; i++)
                        grads[i] += sampleGrads[i];
                }

                // Average gradients
                float scale = 1f / _hp.MinibatchSize;
                for (int i = 0; i < grads.Length; i++)
                    grads[i] *= scale;

                // Adam update
                _optimizer.Update(parameters, grads);
                network.SetParameters(parameters);

                updateCount++;
            }
        }

        if (updateCount > 0)
        {
            _totalUpdates += updateCount;
            totalPolicyLoss /= updateCount * _hp.MinibatchSize;
            totalValueLoss /= updateCount * _hp.MinibatchSize;
            totalEntropy /= updateCount * _hp.MinibatchSize;

            _lastPolicyLoss = totalPolicyLoss;
            _lastValueLoss = totalValueLoss;
            _lastEntropy = totalEntropy;

            // Compute avg reward for stats
            float sumReward = 0;
            for (int i = 0; i < n; i++) sumReward += data[i].Reward;
            _avgReward = sumReward / n;
        }

        _logger.LogInformation("PPO update: {Updates} steps, avgReward={AvgReward:F3}, policyLoss={PolicyLoss:F4}, valueLoss={ValueLoss:F4}, entropy={Entropy:F4}",
            updateCount, _avgReward, _lastPolicyLoss, _lastValueLoss, _lastEntropy);
    }

    public static float ComputeLogProb(float[] policy, float[] logStd,
        float moveX, float moveY, bool split)
    {
        // Continuous: moveX, moveY
        float muX = MathF.Tanh(policy[0]);
        float muY = MathF.Tanh(policy[1]);
        float stdX = MathF.Exp(logStd[0]);
        float stdY = MathF.Exp(logStd[1]);

        float logpX = -0.5f * ((moveX - muX) / stdX) * ((moveX - muX) / stdX) - logStd[0] - 0.5f * Log2Pi;
        float logpY = -0.5f * ((moveY - muY) / stdY) * ((moveY - muY) / stdY) - logStd[1] - 0.5f * Log2Pi;

        // Discrete: split (Bernoulli)
        float splitProb = Sigmoid(policy[2]);
        float logpSplit = split ? MathF.Log(splitProb + 1e-8f) : MathF.Log(1f - splitProb + 1e-8f);

        return logpX + logpY + logpSplit;
    }

    public static float ComputeEntropy(float[] policy, float[] logStd)
    {
        // Gaussian entropy: 0.5 * ln(2*pi*e*sigma^2) = 0.5 + 0.5*ln(2*pi) + logStd
        float entropyX = 0.5f + 0.5f * Log2Pi + logStd[0];
        float entropyY = 0.5f + 0.5f * Log2Pi + logStd[1];

        // Bernoulli entropy: -p*ln(p) - (1-p)*ln(1-p)
        float p = Sigmoid(policy[2]);
        float entropySplit = -(p * MathF.Log(p + 1e-8f) + (1f - p) * MathF.Log(1f - p + 1e-8f));

        return entropyX + entropyY + entropySplit;
    }

    /// <summary>Sample actions given raw policy outputs.</summary>
    public static (float moveX, float moveY, bool split) SampleActions(float[] policy, float[] logStd, Random rng)
    {
        float muX = MathF.Tanh(policy[0]);
        float muY = MathF.Tanh(policy[1]);
        float stdX = MathF.Exp(logStd[0]);
        float stdY = MathF.Exp(logStd[1]);

        float moveX = muX + stdX * SampleNormal(rng);
        float moveY = muY + stdY * SampleNormal(rng);

        float splitProb = Sigmoid(policy[2]);
        bool split = rng.NextDouble() < splitProb;

        return (moveX, moveY, split);
    }

    private float[] ComputePolicyGradient(float[] policy, float[] logStd,
        float moveX, float moveY, bool split, float oldLogProb, float advantage)
    {
        // We need d(clippedLoss)/d(policyOutputs)
        // clippedLoss = -min(r*A, clip(r)*A)
        // Using score function estimator via ratio gradient

        float newLogProb = ComputeLogProb(policy, logStd, moveX, moveY, split);
        float ratio = MathF.Exp(newLogProb - oldLogProb);
        float clipped = Math.Clamp(ratio, 1f - _hp.ClipEpsilon, 1f + _hp.ClipEpsilon);

        // Use clipped or unclipped based on which is smaller
        float useRatio;
        if (advantage >= 0)
            useRatio = ratio < clipped ? ratio : 0; // clip if ratio > 1+eps
        else
            useRatio = ratio > clipped ? ratio : 0; // clip if ratio < 1-eps

        if (useRatio == 0)
            useRatio = 0; // gradient is zero when clipped
        else
            useRatio = 1; // pass through the gradient

        // d(-ratio*adv)/d(logprob) * d(logprob)/d(policy)
        float dLogProb = -useRatio * ratio * advantage;

        var dPolicy = new float[PolicyOutputSize];

        // d(logProb)/d(policy[0]) : logpX depends on tanh(policy[0])
        float muX = MathF.Tanh(policy[0]);
        float stdX = MathF.Exp(logStd[0]);
        float dtanhX = 1f - muX * muX; // d(tanh)/d(x)
        dPolicy[0] = dLogProb * ((moveX - muX) / (stdX * stdX)) * dtanhX;

        float muY = MathF.Tanh(policy[1]);
        float stdY = MathF.Exp(logStd[1]);
        float dtanhY = 1f - muY * muY;
        dPolicy[1] = dLogProb * ((moveY - muY) / (stdY * stdY)) * dtanhY;

        // d(logProb)/d(policy[2]) : Bernoulli
        float p = Sigmoid(policy[2]);
        float dSigmoid = p * (1f - p); // d(sigmoid)/d(x)
        float dLogPdP = split ? (1f / (p + 1e-8f)) : (-1f / (1f - p + 1e-8f));
        dPolicy[2] = dLogProb * dLogPdP * dSigmoid;

        // Entropy gradient contribution: -entropyCoeff * d(entropy)/d(policy)
        // Only split logit affects entropy (Gaussian entropy only depends on logStd)
        float dEntropydP = -(MathF.Log(p + 1e-8f) + 1f - MathF.Log(1f - p + 1e-8f) - 1f); // = ln((1-p)/p)
        dPolicy[2] += -_hp.EntropyCoeff * dEntropydP * dSigmoid;

        return dPolicy;
    }

    private float[] ComputeLogStdGradient(float[] policy, float[] logStd,
        float moveX, float moveY, float oldLogProb, float advantage)
    {
        float newLogProb = ComputeLogProb(policy, logStd, moveX, moveY, false);
        float ratio = MathF.Exp(newLogProb - oldLogProb);
        float clipped = Math.Clamp(ratio, 1f - _hp.ClipEpsilon, 1f + _hp.ClipEpsilon);

        float useRatio;
        if (advantage >= 0)
            useRatio = ratio <= clipped ? ratio : 0;
        else
            useRatio = ratio >= clipped ? ratio : 0;

        float dLogProb = useRatio == 0 ? 0 : -ratio * advantage;

        var grads = new float[2];

        // d(logpX)/d(logStdX) = (moveX - muX)^2 / sigma^2 - 1
        float muX = MathF.Tanh(policy[0]);
        float stdX = MathF.Exp(logStd[0]);
        float diffX = moveX - muX;
        grads[0] = dLogProb * (diffX * diffX / (stdX * stdX) - 1f);

        float muY = MathF.Tanh(policy[1]);
        float stdY = MathF.Exp(logStd[1]);
        float diffY = moveY - muY;
        grads[1] = dLogProb * (diffY * diffY / (stdY * stdY) - 1f);

        // Entropy gradient: d(entropy)/d(logStd) = 1 for each Gaussian dim
        grads[0] += -_hp.EntropyCoeff * 1f;
        grads[1] += -_hp.EntropyCoeff * 1f;

        return grads;
    }

    private static float Sigmoid(float x) => 1f / (1f + MathF.Exp(-Math.Clamp(x, -20f, 20f)));

    private static float SampleNormal(Random rng)
    {
        // Box-Muller transform
        double u1 = 1.0 - rng.NextDouble();
        double u2 = rng.NextDouble();
        return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
    }

    private static void Shuffle(int[] arr)
    {
        for (int i = arr.Length - 1; i > 0; i--)
        {
            int j = Rng.Next(i + 1);
            (arr[i], arr[j]) = (arr[j], arr[i]);
        }
    }

    public object GetStats() => new
    {
        totalUpdates = _totalUpdates,
        avgReward = _avgReward,
        policyLoss = _lastPolicyLoss,
        valueLoss = _lastValueLoss,
        entropy = _lastEntropy,
        bufferFill = _buffer.Count
    };

    public void Save(ActorCriticNetwork network)
    {
        try
        {
            var state = new PPOSaveState
            {
                NetworkParameters = network.GetParameters(),
                HiddenSizes = network.HiddenSizes,
                OptimizerState = _optimizer.GetState(),
                TotalUpdates = _totalUpdates,
                AvgReward = _avgReward
            };
            var json = JsonConvert.SerializeObject(state);
            File.WriteAllText(_savePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save PPO policy to {Path}", _savePath);
        }
    }

    public bool Load(ActorCriticNetwork network)
    {
        try
        {
            if (!File.Exists(_savePath)) return false;
            var json = File.ReadAllText(_savePath);
            var state = JsonConvert.DeserializeObject<PPOSaveState>(json);
            if (state?.NetworkParameters == null) return false;

            // Verify compatible architecture
            if (state.NetworkParameters.Length != ActorCriticNetwork.ParameterCount(state.HiddenSizes))
                return false;
            if (!state.HiddenSizes.SequenceEqual(network.HiddenSizes))
                return false;

            network.SetParameters(state.NetworkParameters);
            if (state.OptimizerState != null)
                _optimizer.SetState(state.OptimizerState);
            _totalUpdates = state.TotalUpdates;
            _avgReward = state.AvgReward;

            _logger.LogInformation("Loaded PPO policy from {Path}: {Updates} updates, avgReward={Reward:F3}",
                _savePath, _totalUpdates, _avgReward);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load PPO policy from {Path}", _savePath);
            return false;
        }
    }

    public void Reset(int newParamCount)
    {
        lock (_lock)
        {
            _buffer.Clear();
            _optimizer = new AdamOptimizer(newParamCount, _hp.LearningRate, _hp.MaxGradNorm);
            _totalUpdates = 0;
            _avgReward = 0;
            _lastPolicyLoss = 0;
            _lastValueLoss = 0;
            _lastEntropy = 0;
        }

        try { if (File.Exists(_savePath)) File.Delete(_savePath); } catch { }
    }

    private class PPOSaveState
    {
        public float[] NetworkParameters { get; set; }
        public int[] HiddenSizes { get; set; }
        public AdamOptimizer.AdamState OptimizerState { get; set; }
        public int TotalUpdates { get; set; }
        public float AvgReward { get; set; }
    }

    private const int PolicyOutputSize = ActorCriticNetwork.PolicyOutputSize;
}
