using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AgarIA.Core.AI;

public class GeneticAlgorithm
{
    private readonly List<(double[] Genome, double Fitness, double Sigma)> _pool = new();
    private readonly object _lock = new();
    private readonly Random _random = new();
    private readonly ILogger<GeneticAlgorithm> _logger;
    private readonly string _savePath;
    private int _genomeSize;
    private DateTime _lastSave = DateTime.UtcNow;
    private DateTime _lastDecay = DateTime.UtcNow;
    private int _elitesRemaining;

    private const int PoolCapacity = 64;
    private const double BaseMutationRate = 0.10;
    private const int BaseGenomeSize = 10_374;
    private const double MinMutationRate = 0.01;
    private const double MaxMutationRate = 0.15;
    private const double DefaultSigma = 0.3;
    private const double MinSigma = 0.005;
    private const double MaxSigma = 1.0;
    private const int EliteCount = 2;
    private const int TournamentSize = 12;
    private const double CrossoverRate = 0.7;
    private const double FitnessDecayRate = 0.95;
    private const int DecayIntervalSeconds = 30;

    public GeneticAlgorithm(ILogger<GeneticAlgorithm> logger, string savePath, int genomeSize)
    {
        _logger = logger;
        _savePath = savePath;
        _genomeSize = genomeSize;
        Load();
    }

    public double[] GetGenome()
    {
        lock (_lock)
        {
            if (_pool.Count < TournamentSize)
                return RandomGenome();

            // Elitism: return clones of top genomes unmutated
            if (_elitesRemaining > 0)
            {
                var sorted = _pool.OrderByDescending(p => p.Fitness).ToList();
                var eliteIndex = EliteCount - _elitesRemaining;
                _elitesRemaining--;
                return (double[])sorted[eliteIndex].Genome.Clone();
            }

            var parent1Entry = TournamentSelectEntry();
            var parent2Entry = TournamentSelectEntry();

            double childSigma = (parent1Entry.Sigma + parent2Entry.Sigma) / 2.0;
            double tau = 1.0 / Math.Sqrt(_genomeSize);
            childSigma *= Math.Exp(tau * GaussianRandom());
            childSigma = Math.Clamp(childSigma, MinSigma, MaxSigma);

            var child = _random.NextDouble() < CrossoverRate
                ? BlockCrossover(parent1Entry.Genome, parent2Entry.Genome)
                : (double[])parent1Entry.Genome.Clone();

            return Mutate(child, childSigma);
        }
    }

    public void ReportFitness(double[] genome, double fitness)
    {
        if (double.IsNaN(fitness) || double.IsInfinity(fitness)) return;

        lock (_lock)
        {
            if ((DateTime.UtcNow - _lastDecay).TotalSeconds >= DecayIntervalSeconds)
            {
                for (int i = 0; i < _pool.Count; i++)
                    _pool[i] = (_pool[i].Genome, _pool[i].Fitness * FitnessDecayRate, _pool[i].Sigma);
                _lastDecay = DateTime.UtcNow;
            }

            var existingIdx = _pool.FindIndex(p => ReferenceEquals(p.Genome, genome));
            if (existingIdx >= 0)
            {
                if (fitness > _pool[existingIdx].Fitness)
                    _pool[existingIdx] = (genome, fitness, _pool[existingIdx].Sigma);
            }
            else
            {
                _pool.Add((genome, fitness, DefaultSigma));
            }

            bool poolChanged = false;
            if (_pool.Count > PoolCapacity)
            {
                int worstIdx = 0;
                double worstFitness = _pool[0].Fitness;
                for (int i = 1; i < _pool.Count; i++)
                {
                    if (_pool[i].Fitness < worstFitness)
                    {
                        worstFitness = _pool[i].Fitness;
                        worstIdx = i;
                    }
                }
                _pool.RemoveAt(worstIdx);
                poolChanged = true;
            }

            if (poolChanged || existingIdx < 0)
                _elitesRemaining = EliteCount;

            if ((DateTime.UtcNow - _lastSave).TotalSeconds > 60)
            {
                Save();
                _lastSave = DateTime.UtcNow;
            }
        }
    }

    private double[] RandomGenome()
    {
        var genome = new double[_genomeSize];
        for (int i = 0; i < genome.Length; i++)
            genome[i] = (_random.NextDouble() * 2 - 1);
        return genome;
    }

    private (double[] Genome, double Fitness, double Sigma) TournamentSelectEntry()
    {
        var best = _pool[_random.Next(_pool.Count)];
        for (int i = 1; i < TournamentSize; i++)
        {
            var candidate = _pool[_random.Next(_pool.Count)];
            if (candidate.Fitness > best.Fitness)
                best = candidate;
        }
        return best;
    }

    private double[] BlockCrossover(double[] a, double[] b)
    {
        var child = new double[a.Length];
        int numPoints = _random.Next(1, 4); // 1-3 crossover points
        var points = new List<int>();
        for (int i = 0; i < numPoints; i++)
            points.Add(_random.Next(1, a.Length));
        points.Sort();
        points.Add(a.Length);

        bool useA = _random.NextDouble() < 0.5;
        int start = 0;
        foreach (int point in points)
        {
            var source = useA ? a : b;
            Array.Copy(source, start, child, start, point - start);
            start = point;
            useA = !useA;
        }

        return child;
    }

    private double[] Mutate(double[] genome, double sigma)
    {
        double effectiveRate = BaseMutationRate * ((double)BaseGenomeSize / _genomeSize);
        effectiveRate = Math.Clamp(effectiveRate, MinMutationRate, MaxMutationRate);

        var child = new double[genome.Length];
        Array.Copy(genome, child, genome.Length);

        for (int i = 0; i < child.Length; i++)
        {
            if (_random.NextDouble() < effectiveRate)
                child[i] += GaussianRandom() * sigma;
        }

        return child;
    }

    private double GaussianRandom()
    {
        double u1 = 1.0 - _random.NextDouble();
        double u2 = 1.0 - _random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }

    public void ResetPool(int newGenomeSize)
    {
        lock (_lock)
        {
            _pool.Clear();
            _genomeSize = newGenomeSize;
            _elitesRemaining = 0;

            try
            {
                if (File.Exists(_savePath))
                {
                    File.Delete(_savePath);
                    _logger.LogWarning("Deleted genome file {Path} due to architecture change", _savePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete genome file {Path}", _savePath);
            }
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            try
            {
                var snapshot = _pool.ToList();
                var json = JsonConvert.SerializeObject(snapshot.Select(p => new { p.Genome, p.Fitness, p.Sigma }));
                File.WriteAllText(_savePath, json);
                _logger.LogInformation("Saved genome pool with {Count} entries to {Path}", _pool.Count, _savePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save genome pool");
            }
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_savePath)) return;

            var json = File.ReadAllText(_savePath);
            var loaded = JsonConvert.DeserializeObject<List<GenomeEntry>>(json);
            if (loaded == null) return;

            foreach (var entry in loaded)
            {
                if (entry.Genome?.Length != _genomeSize) continue;
                double sigma = entry.Sigma > 0 ? entry.Sigma : DefaultSigma;
                _pool.Add((entry.Genome, entry.Fitness, sigma));
            }

            _logger.LogInformation("Loaded genome pool with {Count} entries from {Path}", _pool.Count, _savePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load genome pool");
        }
    }

    public object GetStats()
    {
        lock (_lock)
        {
            if (_pool.Count == 0) return null;

            var sorted = _pool.Select(p => p.Fitness).OrderByDescending(f => f).ToList();
            var top10 = sorted.Take(10).ToList();
            var average = sorted.Average();
            var mid = sorted.Count / 2;
            var median = sorted.Count % 2 == 0
                ? (sorted[mid - 1] + sorted[mid]) / 2.0
                : sorted[mid];

            return new
            {
                top10,
                average,
                median,
                poolSize = sorted.Count
            };
        }
    }

    private class GenomeEntry
    {
        public double[] Genome { get; set; }
        public double Fitness { get; set; }
        public double Sigma { get; set; }
    }
}
