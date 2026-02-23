using Equibles.Core.AutoWiring;
using Microsoft.Extensions.DependencyInjection;

namespace AgarIA.Core.AI;

public static class AIServiceCollectionExtensions
{
    public static IServiceCollection AddAI(this IServiceCollection services)
    {
        services.AutoWireServicesFrom<AIAssembly>();
        services.AddSingleton<GeneticAlgorithm>();
        services.AddSingleton<AIPlayerController>();
        return services;
    }
}
