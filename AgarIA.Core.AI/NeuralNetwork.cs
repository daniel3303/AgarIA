using System.Numerics.Tensors;

namespace AgarIA.Core.AI;

public class NeuralNetwork
{
    public const int InputSize = 161;
    private const int OutputSize = 6;
    private const int MaxHiddenSize = 128;

    public int HiddenSize { get; }

    public static int GenomeSizeForHidden(int hiddenSize) =>
        (InputSize * hiddenSize + hiddenSize) + (hiddenSize * OutputSize + OutputSize);

    // Transposed weights: [outputNeuron * inputSize + inputNeuron] for cache-friendly dot products
    private readonly double[] _wIH;
    private readonly double[] _bH1;
    private readonly double[] _wHO;
    private readonly double[] _bO;
    private double[] _originalGenome;

    public NeuralNetwork(int hiddenSize = 64)
    {
        if (hiddenSize < 1)
            throw new ArgumentException($"Unsupported hiddenSize: {hiddenSize}");
        HiddenSize = hiddenSize;
        _wIH = new double[hiddenSize * InputSize];
        _bH1 = new double[hiddenSize];
        _wHO = new double[OutputSize * hiddenSize];
        _bO = new double[OutputSize];
    }

    public double[] Forward(double[] input)
    {
        var hidden1 = new double[HiddenSize];
        var output = new double[OutputSize];

        for (int h = 0; h < HiddenSize; h++)
            hidden1[h] = _bH1[h] + TensorPrimitives.Dot<double>(input, _wIH.AsSpan(h * InputSize, InputSize));
        TensorPrimitives.Tanh<double>(hidden1, hidden1);

        for (int o = 0; o < OutputSize; o++)
            output[o] = _bO[o] + TensorPrimitives.Dot<double>(hidden1, _wHO.AsSpan(o * HiddenSize, HiddenSize));

        return output;
    }

    [ThreadStatic] private static double[] t_h1;

    public static void BatchForward(double[] inputs, NeuralNetwork[] networks, double[] outputs, int batchSize)
    {
        Parallel.For(0, batchSize, () =>
        {
            if (t_h1 == null || t_h1.Length < MaxHiddenSize)
                t_h1 = new double[MaxHiddenSize];
            return t_h1;
        }, (b, _, h1) =>
        {
            var nn = networks[b];
            if (nn == null) return h1;
            var input = inputs.AsSpan(b * InputSize, InputSize);
            var hs = nn.HiddenSize;

            for (int h = 0; h < hs; h++)
                h1[h] = nn._bH1[h] + TensorPrimitives.Dot<double>(input, nn._wIH.AsSpan(h * InputSize, InputSize));
            TensorPrimitives.Tanh<double>(h1.AsSpan(0, hs), h1.AsSpan(0, hs));

            var outSpan = outputs.AsSpan(b * OutputSize, OutputSize);
            for (int o = 0; o < OutputSize; o++)
                outSpan[o] = nn._bO[o] + TensorPrimitives.Dot<double>(h1.AsSpan(0, hs), nn._wHO.AsSpan(o * hs, hs));

            return h1;
        }, _ => { });
    }

    public double[] GetGenome() => _originalGenome;

    public void SetGenome(double[] genome)
    {
        _originalGenome = genome;
        int idx = 0;

        for (int i = 0; i < InputSize; i++)
            for (int h = 0; h < HiddenSize; h++)
                _wIH[h * InputSize + i] = genome[idx++];

        for (int h = 0; h < HiddenSize; h++)
            _bH1[h] = genome[idx++];

        for (int h = 0; h < HiddenSize; h++)
            for (int o = 0; o < OutputSize; o++)
                _wHO[o * HiddenSize + h] = genome[idx++];

        for (int o = 0; o < OutputSize; o++)
            _bO[o] = genome[idx++];
    }
}
