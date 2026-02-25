using System.Numerics.Tensors;

namespace AgarIA.Core.AI;

public class ActorCriticNetwork
{
    public const int InputSize = 151;
    public const int PolicyOutputSize = 3; // moveX mean, moveY mean, split logit
    public const int ValueOutputSize = 1;

    public int[] HiddenSizes { get; }

    // Shared hidden layers
    private readonly float[][] _weights;   // transposed: [outNeuron * inSize + inNeuron]
    private readonly float[][] _biases;
    private readonly int[] _layerInputSizes;

    // Policy head: lastHidden -> 3
    private float[] _policyWeights;
    private float[] _policyBiases;

    // Value head: lastHidden -> 1
    private float[] _valueWeights;
    private float[] _valueBiases;

    // Learnable log-std for continuous actions (moveX, moveY)
    public float[] LogStd { get; private set; } = new float[2];

    private int _lastHiddenSize;

    public ActorCriticNetwork(int[] hiddenSizes)
    {
        if (hiddenSizes == null || hiddenSizes.Length < 1)
            throw new ArgumentException("At least one hidden layer is required");

        HiddenSizes = hiddenSizes;
        _weights = new float[hiddenSizes.Length][];
        _biases = new float[hiddenSizes.Length][];
        _layerInputSizes = new int[hiddenSizes.Length];

        int prevSize = InputSize;
        for (int i = 0; i < hiddenSizes.Length; i++)
        {
            _layerInputSizes[i] = prevSize;
            _weights[i] = new float[hiddenSizes[i] * prevSize];
            _biases[i] = new float[hiddenSizes[i]];
            prevSize = hiddenSizes[i];
        }

        _lastHiddenSize = prevSize;

        // Policy head
        _policyWeights = new float[PolicyOutputSize * prevSize];
        _policyBiases = new float[PolicyOutputSize];

        // Value head
        _valueWeights = new float[ValueOutputSize * prevSize];
        _valueBiases = new float[ValueOutputSize];

        // Initialize log std to ln(0.5)
        LogStd[0] = LogStd[1] = -0.693f;

        // Xavier initialization
        InitializeWeights();
    }

    private void InitializeWeights()
    {
        var rng = new Random(42);
        for (int layer = 0; layer < HiddenSizes.Length; layer++)
        {
            float scale = MathF.Sqrt(2f / (_layerInputSizes[layer] + HiddenSizes[layer]));
            for (int i = 0; i < _weights[layer].Length; i++)
                _weights[layer][i] = (float)(rng.NextDouble() * 2 - 1) * scale;
        }

        float pScale = MathF.Sqrt(2f / (_lastHiddenSize + PolicyOutputSize));
        for (int i = 0; i < _policyWeights.Length; i++)
            _policyWeights[i] = (float)(rng.NextDouble() * 2 - 1) * pScale;

        // Value head: small init
        float vScale = MathF.Sqrt(2f / (_lastHiddenSize + 1)) * 0.1f;
        for (int i = 0; i < _valueWeights.Length; i++)
            _valueWeights[i] = (float)(rng.NextDouble() * 2 - 1) * vScale;
    }

    public static int ParameterCount(int[] hiddenSizes)
    {
        int count = 0;
        int prev = InputSize;
        foreach (var hs in hiddenSizes)
        {
            count += prev * hs + hs; // weights + biases
            prev = hs;
        }
        count += PolicyOutputSize * prev + PolicyOutputSize; // policy head
        count += ValueOutputSize * prev + ValueOutputSize;   // value head
        count += 2; // logStd
        return count;
    }

    /// <summary>Fast inference: returns policy outputs only (3 floats).</summary>
    public float[] ForwardPolicy(float[] input)
    {
        var hidden = ForwardHidden(input);
        var policy = new float[PolicyOutputSize];
        for (int o = 0; o < PolicyOutputSize; o++)
            policy[o] = _policyBiases[o] + TensorPrimitives.Dot<float>(hidden, _policyWeights.AsSpan(o * _lastHiddenSize, _lastHiddenSize));
        return policy;
    }

    /// <summary>Full forward: returns policy, value, and cached layer activations for backprop.</summary>
    public (float[] policy, float value, float[][] preActivations, float[][] postActivations) ForwardFull(float[] input)
    {
        var preActs = new float[HiddenSizes.Length][];
        var postActs = new float[HiddenSizes.Length][];

        ReadOnlySpan<float> current = input;
        for (int layer = 0; layer < HiddenSizes.Length; layer++)
        {
            var hs = HiddenSizes[layer];
            var inSize = _layerInputSizes[layer];
            var pre = new float[hs];
            var post = new float[hs];

            for (int h = 0; h < hs; h++)
                pre[h] = _biases[layer][h] + TensorPrimitives.Dot<float>(current, _weights[layer].AsSpan(h * inSize, inSize));
            TensorPrimitives.Tanh<float>(pre, post);

            preActs[layer] = pre;
            postActs[layer] = post;
            current = post;
        }

        // Policy head
        var policy = new float[PolicyOutputSize];
        for (int o = 0; o < PolicyOutputSize; o++)
            policy[o] = _policyBiases[o] + TensorPrimitives.Dot<float>(current, _policyWeights.AsSpan(o * _lastHiddenSize, _lastHiddenSize));

        // Value head
        float value = _valueBiases[0] + TensorPrimitives.Dot<float>(current, _valueWeights.AsSpan(0, _lastHiddenSize));

        return (policy, value, preActs, postActs);
    }

    /// <summary>Batched policy-only forward for all bots of a tier.</summary>
    [ThreadStatic] private static float[] t_buf1;
    [ThreadStatic] private static float[] t_buf2;

    public void BatchForwardPolicy(float[] inputs, float[] outputs, int batchSize)
    {
        int maxBuf = 0;
        foreach (var hs in HiddenSizes)
            if (hs > maxBuf) maxBuf = hs;
        int bufSize = Math.Max(maxBuf, PolicyOutputSize);

        Parallel.For(0, batchSize, () =>
        {
            if (t_buf1 == null || t_buf1.Length < bufSize)
                t_buf1 = new float[bufSize];
            if (t_buf2 == null || t_buf2.Length < bufSize)
                t_buf2 = new float[bufSize];
            return (t_buf1, t_buf2);
        }, (b, _, bufs) =>
        {
            var (buf1, buf2) = bufs;
            var inputSpan = inputs.AsSpan(b * InputSize, InputSize);
            ReadOnlySpan<float> current = inputSpan;

            for (int layer = 0; layer < HiddenSizes.Length; layer++)
            {
                var hs = HiddenSizes[layer];
                var inSize = _layerInputSizes[layer];
                var target = (layer % 2 == 0) ? buf1 : buf2;

                for (int h = 0; h < hs; h++)
                    target[h] = _biases[layer][h] + TensorPrimitives.Dot<float>(current, _weights[layer].AsSpan(h * inSize, inSize));
                TensorPrimitives.Tanh<float>(target.AsSpan(0, hs), target.AsSpan(0, hs));

                current = target.AsSpan(0, hs);
            }

            // Policy head
            var outSpan = outputs.AsSpan(b * PolicyOutputSize, PolicyOutputSize);
            for (int o = 0; o < PolicyOutputSize; o++)
                outSpan[o] = _policyBiases[o] + TensorPrimitives.Dot<float>(current, _policyWeights.AsSpan(o * _lastHiddenSize, _lastHiddenSize));

            return bufs;
        }, _ => { });
    }

    private float[] ForwardHidden(float[] input)
    {
        ReadOnlySpan<float> current = input;
        float[] output = null;

        for (int layer = 0; layer < HiddenSizes.Length; layer++)
        {
            var hs = HiddenSizes[layer];
            var inSize = _layerInputSizes[layer];
            output = new float[hs];

            for (int h = 0; h < hs; h++)
                output[h] = _biases[layer][h] + TensorPrimitives.Dot<float>(current, _weights[layer].AsSpan(h * inSize, inSize));
            TensorPrimitives.Tanh<float>(output, output);
            current = output;
        }

        return output ?? new float[_lastHiddenSize];
    }

    /// <summary>
    /// Backprop through the network given upstream gradients from policy and value heads.
    /// Returns flat gradient array matching GetParameters() layout.
    /// </summary>
    public float[] Backward(float[] input, float[] dPolicy, float dValue,
        float[][] preActivations, float[][] postActivations)
    {
        int paramCount = ParameterCount(HiddenSizes);
        var grads = new float[paramCount];

        // Current position in grads (we'll fill from end to start, then reverse mapping)
        // Layout: shared weights/biases... | policy weights/biases | value weights/biases | logStd
        // We need to compute gradients w.r.t. each parameter

        var lastHidden = postActivations[^1];

        // Policy head gradients
        int offset = GetPolicyWeightsOffset();
        for (int o = 0; o < PolicyOutputSize; o++)
        {
            for (int h = 0; h < _lastHiddenSize; h++)
                grads[offset + o * _lastHiddenSize + h] = dPolicy[o] * lastHidden[h];
        }
        offset += PolicyOutputSize * _lastHiddenSize;
        for (int o = 0; o < PolicyOutputSize; o++)
            grads[offset + o] = dPolicy[o];

        // Value head gradients
        offset = GetValueWeightsOffset();
        for (int h = 0; h < _lastHiddenSize; h++)
            grads[offset + h] = dValue * lastHidden[h];
        offset += _lastHiddenSize;
        grads[offset] = dValue;

        // Backprop through last hidden into shared layers
        // dHidden = policy contribution + value contribution
        var dHidden = new float[_lastHiddenSize];
        for (int h = 0; h < _lastHiddenSize; h++)
        {
            float sum = 0;
            for (int o = 0; o < PolicyOutputSize; o++)
                sum += dPolicy[o] * _policyWeights[o * _lastHiddenSize + h];
            sum += dValue * _valueWeights[h];
            dHidden[h] = sum;
        }

        // Backprop through shared hidden layers (reverse order)
        var dUpstream = dHidden;
        int gradOffset = 0;
        // First compute offsets for each layer
        var layerOffsets = new int[HiddenSizes.Length];
        int off = 0;
        for (int layer = 0; layer < HiddenSizes.Length; layer++)
        {
            layerOffsets[layer] = off;
            off += _layerInputSizes[layer] * HiddenSizes[layer] + HiddenSizes[layer];
        }

        for (int layer = HiddenSizes.Length - 1; layer >= 0; layer--)
        {
            var hs = HiddenSizes[layer];
            var inSize = _layerInputSizes[layer];
            var pre = preActivations[layer];
            ReadOnlySpan<float> layerInput = layer == 0 ? input : postActivations[layer - 1];

            // dtanh = (1 - tanh^2) * dUpstream
            var dPre = new float[hs];
            for (int h = 0; h < hs; h++)
            {
                float tanhVal = MathF.Tanh(pre[h]);
                dPre[h] = dUpstream[h] * (1f - tanhVal * tanhVal);
            }

            // Weight gradients: dW = outer(dPre, layerInput)
            gradOffset = layerOffsets[layer];
            for (int h = 0; h < hs; h++)
                for (int j = 0; j < inSize; j++)
                    grads[gradOffset + h * inSize + j] = dPre[h] * layerInput[j];

            // Bias gradients
            gradOffset += hs * inSize;
            for (int h = 0; h < hs; h++)
                grads[gradOffset + h] = dPre[h];

            // Propagate to previous layer
            if (layer > 0)
            {
                var dNext = new float[inSize];
                for (int j = 0; j < inSize; j++)
                {
                    float sum = 0;
                    for (int h = 0; h < hs; h++)
                        sum += dPre[h] * _weights[layer][h * inSize + j];
                    dNext[j] = sum;
                }
                dUpstream = dNext;
            }
        }

        return grads;
    }

    private int GetPolicyWeightsOffset()
    {
        int offset = 0;
        for (int i = 0; i < HiddenSizes.Length; i++)
            offset += _layerInputSizes[i] * HiddenSizes[i] + HiddenSizes[i];
        return offset;
    }

    private int GetValueWeightsOffset()
    {
        return GetPolicyWeightsOffset() + PolicyOutputSize * _lastHiddenSize + PolicyOutputSize;
    }

    private int GetLogStdOffset()
    {
        return GetValueWeightsOffset() + _lastHiddenSize + ValueOutputSize;
    }

    public float[] GetParameters()
    {
        int count = ParameterCount(HiddenSizes);
        var p = new float[count];
        int idx = 0;

        // Shared layers
        for (int layer = 0; layer < HiddenSizes.Length; layer++)
        {
            _weights[layer].CopyTo(p, idx);
            idx += _weights[layer].Length;
            _biases[layer].CopyTo(p, idx);
            idx += _biases[layer].Length;
        }

        // Policy head
        _policyWeights.CopyTo(p, idx); idx += _policyWeights.Length;
        _policyBiases.CopyTo(p, idx); idx += _policyBiases.Length;

        // Value head
        _valueWeights.CopyTo(p, idx); idx += _valueWeights.Length;
        _valueBiases.CopyTo(p, idx); idx += _valueBiases.Length;

        // LogStd
        LogStd.CopyTo(p, idx);

        return p;
    }

    public void SetParameters(float[] p)
    {
        int idx = 0;

        for (int layer = 0; layer < HiddenSizes.Length; layer++)
        {
            Array.Copy(p, idx, _weights[layer], 0, _weights[layer].Length);
            idx += _weights[layer].Length;
            Array.Copy(p, idx, _biases[layer], 0, _biases[layer].Length);
            idx += _biases[layer].Length;
        }

        Array.Copy(p, idx, _policyWeights, 0, _policyWeights.Length); idx += _policyWeights.Length;
        Array.Copy(p, idx, _policyBiases, 0, _policyBiases.Length); idx += _policyBiases.Length;
        Array.Copy(p, idx, _valueWeights, 0, _valueWeights.Length); idx += _valueWeights.Length;
        Array.Copy(p, idx, _valueBiases, 0, _valueBiases.Length); idx += _valueBiases.Length;
        Array.Copy(p, idx, LogStd, 0, 2);
    }

    public ActorCriticNetwork Clone()
    {
        var clone = new ActorCriticNetwork(HiddenSizes);
        clone.SetParameters(GetParameters());
        return clone;
    }
}
