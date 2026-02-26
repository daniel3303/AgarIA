namespace AgarIA.Core.AI;

/// <summary>
/// Running observation normalizer using Welford's online algorithm.
/// Maintains per-feature running mean and variance for normalizing observations.
/// </summary>
public class RunningNormalizer
{
    private readonly int _size;
    private readonly double[] _mean;
    private readonly double[] _m2;
    private long _count;

    public RunningNormalizer(int size)
    {
        _size = size;
        _mean = new double[size];
        _m2 = new double[size];
    }

    public void Update(float[] observation)
    {
        _count++;
        for (int i = 0; i < _size; i++)
        {
            double delta = observation[i] - _mean[i];
            _mean[i] += delta / _count;
            double delta2 = observation[i] - _mean[i];
            _m2[i] += delta * delta2;
        }
    }

    public float[] Normalize(float[] observation)
    {
        if (_count < 2)
            return observation;

        var result = new float[_size];
        for (int i = 0; i < _size; i++)
        {
            double variance = _m2[i] / _count;
            double std = Math.Sqrt(variance + 1e-8);
            result[i] = Math.Clamp((float)((observation[i] - _mean[i]) / std), -10f, 10f);
        }
        return result;
    }

    public NormalizerState GetState() => new()
    {
        Mean = (double[])_mean.Clone(),
        M2 = (double[])_m2.Clone(),
        Count = _count
    };

    public void SetState(NormalizerState state)
    {
        if (state == null || state.Mean.Length != _size) return;
        Array.Copy(state.Mean, _mean, _size);
        Array.Copy(state.M2, _m2, _size);
        _count = state.Count;
    }

    public class NormalizerState
    {
        public double[] Mean { get; set; }
        public double[] M2 { get; set; }
        public long Count { get; set; }
    }
}
