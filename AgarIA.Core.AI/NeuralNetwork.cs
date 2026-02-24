using System.Numerics.Tensors;

namespace AgarIA.Core.AI;

public class NeuralNetwork
{
    private const int InputSize = 67;
    private const int HiddenSize = 64;
    private const int OutputSize = 6;

    public int HiddenLayers { get; }

    public static int GenomeSizeForLayers(int hiddenLayers) => hiddenLayers switch
    {
        1 => (InputSize * HiddenSize + HiddenSize) + (HiddenSize * OutputSize + OutputSize),
        2 => (InputSize * HiddenSize + HiddenSize) + (HiddenSize * HiddenSize + HiddenSize) + (HiddenSize * OutputSize + OutputSize),
        _ => throw new ArgumentException($"Unsupported hiddenLayers: {hiddenLayers}")
    };

    // Transposed weights: [outputNeuron * inputSize + inputNeuron] for cache-friendly dot products
    private readonly double[] _wIH = new double[HiddenSize * InputSize];
    private readonly double[] _bH1 = new double[HiddenSize];
    private readonly double[] _wHH = new double[HiddenSize * HiddenSize];
    private readonly double[] _bH2 = new double[HiddenSize];
    private readonly double[] _wHO = new double[OutputSize * HiddenSize];
    private readonly double[] _bO = new double[OutputSize];
    private double[] _originalGenome;

    public NeuralNetwork(int hiddenLayers = 2)
    {
        if (hiddenLayers is not (1 or 2))
            throw new ArgumentException($"Unsupported hiddenLayers: {hiddenLayers}");
        HiddenLayers = hiddenLayers;
    }

    /// <summary>
    /// Single forward pass for this network instance.
    /// </summary>
    public double[] Forward(double[] input)
    {
        var hidden1 = new double[HiddenSize];
        var output = new double[OutputSize];

        // Layer 1
        for (int h = 0; h < HiddenSize; h++)
            hidden1[h] = _bH1[h] + TensorPrimitives.Dot<double>(input, _wIH.AsSpan(h * InputSize, InputSize));
        TensorPrimitives.Tanh<double>(hidden1, hidden1);

        double[] lastHidden;
        if (HiddenLayers == 2)
        {
            var hidden2 = new double[HiddenSize];
            for (int h2 = 0; h2 < HiddenSize; h2++)
                hidden2[h2] = _bH2[h2] + TensorPrimitives.Dot<double>(hidden1, _wHH.AsSpan(h2 * HiddenSize, HiddenSize));
            TensorPrimitives.Tanh<double>(hidden2, hidden2);
            lastHidden = hidden2;
        }
        else
        {
            lastHidden = hidden1;
        }

        // Output layer
        for (int o = 0; o < OutputSize; o++)
            output[o] = _bO[o] + TensorPrimitives.Dot<double>(lastHidden, _wHO.AsSpan(o * HiddenSize, HiddenSize));

        return output;
    }

    [ThreadStatic] private static double[] t_h1;
    [ThreadStatic] private static double[] t_h2;

    /// <summary>
    /// Batched forward pass: runs multiple inputs through multiple networks in one call.
    /// inputs: flat array [batchSize * InputSize], networks: array of NeuralNetwork (one per bot).
    /// outputs: flat array [batchSize * OutputSize], pre-allocated by caller.
    /// </summary>
    public static void BatchForward(double[] inputs, NeuralNetwork[] networks, double[] outputs, int batchSize)
    {
        Parallel.For(0, batchSize, () =>
        {
            t_h1 ??= new double[HiddenSize];
            t_h2 ??= new double[HiddenSize];
            return (h1: t_h1, h2: t_h2);
        }, (b, _, buffers) =>
        {
            var nn = networks[b];
            var input = inputs.AsSpan(b * InputSize, InputSize);
            var h1 = buffers.h1;

            // Layer 1: input -> hidden1
            for (int h = 0; h < HiddenSize; h++)
                h1[h] = nn._bH1[h] + TensorPrimitives.Dot<double>(input, nn._wIH.AsSpan(h * InputSize, InputSize));
            TensorPrimitives.Tanh<double>(h1, h1);

            Span<double> lastHidden;
            if (nn.HiddenLayers == 2)
            {
                var h2 = buffers.h2;
                for (int h2i = 0; h2i < HiddenSize; h2i++)
                    h2[h2i] = nn._bH2[h2i] + TensorPrimitives.Dot<double>(h1, nn._wHH.AsSpan(h2i * HiddenSize, HiddenSize));
                TensorPrimitives.Tanh<double>(h2, h2);
                lastHidden = h2;
            }
            else
            {
                lastHidden = h1;
            }

            // Output layer
            var outSpan = outputs.AsSpan(b * OutputSize, OutputSize);
            for (int o = 0; o < OutputSize; o++)
                outSpan[o] = nn._bO[o] + TensorPrimitives.Dot<double>(lastHidden, nn._wHO.AsSpan(o * HiddenSize, HiddenSize));

            return buffers;
        }, _ => { });
    }

    public double[] GetGenome() => _originalGenome;

    public void SetGenome(double[] genome)
    {
        _originalGenome = genome;
        int idx = 0;

        // Original genome layout: weightsIH[i,h] stored as i-major
        // Transposed: _wIH[h * InputSize + i]
        for (int i = 0; i < InputSize; i++)
            for (int h = 0; h < HiddenSize; h++)
                _wIH[h * InputSize + i] = genome[idx++];

        for (int h = 0; h < HiddenSize; h++)
            _bH1[h] = genome[idx++];

        if (HiddenLayers == 2)
        {
            for (int h1 = 0; h1 < HiddenSize; h1++)
                for (int h2 = 0; h2 < HiddenSize; h2++)
                    _wHH[h2 * HiddenSize + h1] = genome[idx++];

            for (int h = 0; h < HiddenSize; h++)
                _bH2[h] = genome[idx++];
        }

        for (int h = 0; h < HiddenSize; h++)
            for (int o = 0; o < OutputSize; o++)
                _wHO[o * HiddenSize + h] = genome[idx++];

        for (int o = 0; o < OutputSize; o++)
            _bO[o] = genome[idx++];
    }
}
