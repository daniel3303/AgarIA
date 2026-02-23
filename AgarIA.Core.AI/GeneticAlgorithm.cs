using Equibles.Core.AutoWiring;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AgarIA.Core.AI;

[Service]
public class GeneticAlgorithm
{
    private readonly List<(double[] Genome, double Fitness)> _pool = new();
    private readonly object _lock = new();
    private readonly Random _random = new();
    private readonly ILogger<GeneticAlgorithm> _logger;
    private readonly string _savePath = "ai_genomes.json";
    private DateTime _lastSave = DateTime.UtcNow;

    private const int PoolCapacity = 50;
    private const double MutationRate = 0.10;
    private const double MutationSigma = 0.3;
    private const int TournamentSize = 3;
    private const double CrossoverRate = 0.7;
    private const double FitnessDecayRate = 0.95;

    public GeneticAlgorithm(ILogger<GeneticAlgorithm> logger)
    {
        _logger = logger;
        Load();
    }

    public double[] GetGenome()
    {
        lock (_lock)
        {
            if (_pool.Count < TournamentSize)
                return RandomGenome();

            var parent1 = TournamentSelect();
            var parent2 = TournamentSelect();

            var child = _random.NextDouble() < CrossoverRate
                ? Crossover(parent1, parent2)
                : (double[])parent1.Clone();

            return Mutate(child);
        }
    }

    public void ReportFitness(double[] genome, double fitness)
    {
        lock (_lock)
        {
            // Decay all existing fitnesses so legacy genomes lose dominance over time
            for (int i = 0; i < _pool.Count; i++)
                _pool[i] = (_pool[i].Genome, _pool[i].Fitness * FitnessDecayRate);

            _pool.Add((genome, fitness));

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
            }

            if ((DateTime.UtcNow - _lastSave).TotalSeconds > 60)
            {
                Save();
                _lastSave = DateTime.UtcNow;
            }
        }
    }

    private double[] RandomGenome()
    {
        var genome = new double[NeuralNetwork.GenomeSize];
        for (int i = 0; i < genome.Length; i++)
            genome[i] = (_random.NextDouble() * 2 - 1);
        return genome;
    }

    private double[] TournamentSelect()
    {
        var best = _pool[_random.Next(_pool.Count)];
        for (int i = 1; i < TournamentSize; i++)
        {
            var candidate = _pool[_random.Next(_pool.Count)];
            if (candidate.Fitness > best.Fitness)
                best = candidate;
        }
        return best.Genome;
    }

    private double[] Crossover(double[] a, double[] b)
    {
        var child = new double[a.Length];
        for (int i = 0; i < child.Length; i++)
            child[i] = _random.NextDouble() < 0.5 ? a[i] : b[i];
        return child;
    }

    private double[] Mutate(double[] genome)
    {
        var child = new double[genome.Length];
        Array.Copy(genome, child, genome.Length);

        for (int i = 0; i < child.Length; i++)
        {
            if (_random.NextDouble() < MutationRate)
                child[i] += GaussianRandom() * MutationSigma;
        }

        return child;
    }

    private double GaussianRandom()
    {
        double u1 = 1.0 - _random.NextDouble();
        double u2 = 1.0 - _random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }

    public void Save()
    {
        lock (_lock)
        {
            try
            {
                var snapshot = _pool.ToList();
                var json = JsonConvert.SerializeObject(snapshot.Select(p => new { p.Genome, p.Fitness }));
                File.WriteAllText(_savePath, json);
                _logger.LogInformation("Saved genome pool with {Count} entries", _pool.Count);
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
                _pool.Add((entry.Genome, entry.Fitness));

            _logger.LogInformation("Loaded genome pool with {Count} entries", _pool.Count);
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
    }
}
