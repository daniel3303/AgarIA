namespace AgarIA.Core.AI;

public class AdamOptimizer
{
    private readonly float _lr;
    private readonly float _beta1;
    private readonly float _beta2;
    private readonly float _epsilon;
    private readonly float _maxGradNorm;
    private float[] _m;
    private float[] _v;
    private int _step;

    public AdamOptimizer(int paramCount, float lr = 3e-4f, float maxGradNorm = 0.5f,
        float beta1 = 0.9f, float beta2 = 0.999f, float epsilon = 1e-8f)
    {
        _lr = lr;
        _beta1 = beta1;
        _beta2 = beta2;
        _epsilon = epsilon;
        _maxGradNorm = maxGradNorm;
        _m = new float[paramCount];
        _v = new float[paramCount];
        _step = 0;
    }

    public void Update(float[] parameters, float[] gradients)
    {
        // Gradient clipping by global norm
        float normSq = 0;
        for (int i = 0; i < gradients.Length; i++)
            normSq += gradients[i] * gradients[i];
        float norm = MathF.Sqrt(normSq + 1e-8f);
        float clipScale = norm > _maxGradNorm ? _maxGradNorm / norm : 1f;

        _step++;
        float bc1 = 1f - MathF.Pow(_beta1, _step);
        float bc2 = 1f - MathF.Pow(_beta2, _step);

        for (int i = 0; i < parameters.Length; i++)
        {
            float g = gradients[i] * clipScale;
            _m[i] = _beta1 * _m[i] + (1f - _beta1) * g;
            _v[i] = _beta2 * _v[i] + (1f - _beta2) * g * g;
            float mHat = _m[i] / bc1;
            float vHat = _v[i] / bc2;
            parameters[i] -= _lr * mHat / (MathF.Sqrt(vHat) + _epsilon);
        }
    }

    public AdamState GetState() => new()
    {
        M = (float[])_m.Clone(),
        V = (float[])_v.Clone(),
        Step = _step
    };

    public void SetState(AdamState state)
    {
        if (state.M.Length != _m.Length) return; // size mismatch, skip
        _m = (float[])state.M.Clone();
        _v = (float[])state.V.Clone();
        _step = state.Step;
    }

    public class AdamState
    {
        public float[] M { get; set; } = [];
        public float[] V { get; set; } = [];
        public int Step { get; set; }
    }
}
