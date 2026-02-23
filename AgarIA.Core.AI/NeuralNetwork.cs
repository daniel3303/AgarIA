namespace AgarIA.Core.AI;

public class NeuralNetwork
{
    private const int InputSize = 119;
    private const int HiddenSize = 64;
    private const int OutputSize = 12;

    public static int GenomeSize =>
        (InputSize * HiddenSize + HiddenSize) +
        (HiddenSize * HiddenSize + HiddenSize) +
        (HiddenSize * OutputSize + OutputSize);

    private readonly double[,] _weightsIH1 = new double[InputSize, HiddenSize];
    private readonly double[] _biasH1 = new double[HiddenSize];
    private readonly double[,] _weightsH1H2 = new double[HiddenSize, HiddenSize];
    private readonly double[] _biasH2 = new double[HiddenSize];
    private readonly double[,] _weightsH2O = new double[HiddenSize, OutputSize];
    private readonly double[] _biasO = new double[OutputSize];
    private double[] _originalGenome;

    public double[] Forward(double[] input)
    {
        var hidden1 = new double[HiddenSize];
        for (int h = 0; h < HiddenSize; h++)
        {
            double sum = _biasH1[h];
            for (int i = 0; i < InputSize; i++)
                sum += input[i] * _weightsIH1[i, h];
            hidden1[h] = Math.Tanh(sum);
        }

        var hidden2 = new double[HiddenSize];
        for (int h2 = 0; h2 < HiddenSize; h2++)
        {
            double sum = _biasH2[h2];
            for (int h1 = 0; h1 < HiddenSize; h1++)
                sum += hidden1[h1] * _weightsH1H2[h1, h2];
            hidden2[h2] = Math.Tanh(sum);
        }

        var output = new double[OutputSize];
        for (int o = 0; o < OutputSize; o++)
        {
            double sum = _biasO[o];
            for (int h = 0; h < HiddenSize; h++)
                sum += hidden2[h] * _weightsH2O[h, o];
            output[o] = sum;
        }

        return output;
    }

    // Returns the original genome array reference for pool deduplication
    public double[] GetGenome() => _originalGenome;

    public void SetGenome(double[] genome)
    {
        _originalGenome = genome;
        int idx = 0;

        for (int i = 0; i < InputSize; i++)
            for (int h = 0; h < HiddenSize; h++)
                _weightsIH1[i, h] = genome[idx++];

        for (int h = 0; h < HiddenSize; h++)
            _biasH1[h] = genome[idx++];

        for (int h1 = 0; h1 < HiddenSize; h1++)
            for (int h2 = 0; h2 < HiddenSize; h2++)
                _weightsH1H2[h1, h2] = genome[idx++];

        for (int h = 0; h < HiddenSize; h++)
            _biasH2[h] = genome[idx++];

        for (int h = 0; h < HiddenSize; h++)
            for (int o = 0; o < OutputSize; o++)
                _weightsH2O[h, o] = genome[idx++];

        for (int o = 0; o < OutputSize; o++)
            _biasO[o] = genome[idx++];
    }
}
