using System.Numerics.Tensors;

namespace AgarIA.Core.AI;

public class NeuralNetwork
{
    public const int InputSize = 161;
    private const int OutputSize = 6;

    public int[] HiddenSizes { get; }

    public static int GenomeSizeForLayers(int[] hiddenSizes)
    {
        int size = 0;
        int prevSize = InputSize;
        foreach (var hs in hiddenSizes)
        {
            size += prevSize * hs + hs; // weights + biases
            prevSize = hs;
        }
        size += prevSize * OutputSize + OutputSize; // last hidden -> output
        return size;
    }

    // weights[i] = transposed weight matrix for layer transition i
    // biases[i] = bias vector for layer i
    private readonly float[][] _weights;
    private readonly float[][] _biases;
    private readonly int[] _layerInputSizes;
    private double[] _originalGenome;

    public NeuralNetwork(int[] hiddenSizes)
    {
        if (hiddenSizes == null || hiddenSizes.Length < 1)
            throw new ArgumentException("At least one hidden layer is required");

        HiddenSizes = hiddenSizes;
        int layerCount = hiddenSizes.Length + 1; // hidden layers + output layer
        _weights = new float[layerCount][];
        _biases = new float[layerCount][];
        _layerInputSizes = new int[layerCount];

        int prevSize = InputSize;
        for (int i = 0; i < hiddenSizes.Length; i++)
        {
            _layerInputSizes[i] = prevSize;
            _weights[i] = new float[hiddenSizes[i] * prevSize];
            _biases[i] = new float[hiddenSizes[i]];
            prevSize = hiddenSizes[i];
        }

        // Output layer
        int lastIdx = hiddenSizes.Length;
        _layerInputSizes[lastIdx] = prevSize;
        _weights[lastIdx] = new float[OutputSize * prevSize];
        _biases[lastIdx] = new float[OutputSize];
    }

    [ThreadStatic] private static float[] t_buf1;
    [ThreadStatic] private static float[] t_buf2;

    public static int MaxHiddenSize(int[][] allConfigs)
    {
        int max = 0;
        foreach (var cfg in allConfigs)
            foreach (var s in cfg)
                if (s > max) max = s;
        return max;
    }

    public static void BatchForward(float[] inputs, NeuralNetwork[] networks, float[] outputs, int batchSize)
    {
        // Compute max buffer size needed across all networks in this batch
        int maxBuf = 0;
        for (int b = 0; b < batchSize; b++)
        {
            var nn = networks[b];
            if (nn == null) continue;
            foreach (var hs in nn.HiddenSizes)
                if (hs > maxBuf) maxBuf = hs;
        }
        int bufSize = Math.Max(maxBuf, OutputSize);

        Parallel.For(0, batchSize, () =>
        {
            if (t_buf1 == null || t_buf1.Length < bufSize)
                t_buf1 = new float[bufSize];
            if (t_buf2 == null || t_buf2.Length < bufSize)
                t_buf2 = new float[bufSize];
            return (t_buf1, t_buf2);
        }, (b, _, bufs) =>
        {
            var nn = networks[b];
            if (nn == null) return bufs;

            var (buf1, buf2) = bufs;
            var inputSpan = inputs.AsSpan(b * InputSize, InputSize);
            ReadOnlySpan<float> current = inputSpan;

            // Forward through hidden layers with tanh activation
            for (int layer = 0; layer < nn.HiddenSizes.Length; layer++)
            {
                var hs = nn.HiddenSizes[layer];
                var inSize = nn._layerInputSizes[layer];
                var target = (layer % 2 == 0) ? buf1 : buf2;

                for (int h = 0; h < hs; h++)
                    target[h] = nn._biases[layer][h] + TensorPrimitives.Dot<float>(current, nn._weights[layer].AsSpan(h * inSize, inSize));
                TensorPrimitives.Tanh<float>(target.AsSpan(0, hs), target.AsSpan(0, hs));

                current = target.AsSpan(0, hs);
            }

            // Output layer (no activation)
            var outIdx = nn.HiddenSizes.Length;
            var outInSize = nn._layerInputSizes[outIdx];
            var outSpan = outputs.AsSpan(b * OutputSize, OutputSize);
            for (int o = 0; o < OutputSize; o++)
                outSpan[o] = nn._biases[outIdx][o] + TensorPrimitives.Dot<float>(current, nn._weights[outIdx].AsSpan(o * outInSize, outInSize));

            return bufs;
        }, _ => { });
    }

    public double[] GetGenome() => _originalGenome;

    public void SetGenome(double[] genome)
    {
        _originalGenome = genome;
        int idx = 0;

        for (int layer = 0; layer <= HiddenSizes.Length; layer++)
        {
            var inSize = _layerInputSizes[layer];
            var outSize = layer < HiddenSizes.Length ? HiddenSizes[layer] : OutputSize;

            // Weights: stored transposed [outNeuron * inSize + inNeuron]
            for (int i = 0; i < inSize; i++)
                for (int o = 0; o < outSize; o++)
                    _weights[layer][o * inSize + i] = (float)genome[idx++];

            // Biases
            for (int o = 0; o < outSize; o++)
                _biases[layer][o] = (float)genome[idx++];
        }
    }
}
