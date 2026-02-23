using AgarIA.Core.Game;
using Equibles.Core.AutoWiring;
using Microsoft.Extensions.DependencyInjection;

namespace AgarIA.Core.AI;

public static class AIServiceCollectionExtensions
{
    public static IServiceCollection AddAI(this IServiceCollection services)
    {
        services.AutoWireServicesFrom<AIAssembly>();
        services.AddSingleton<AIPlayerController>();
        services.AddSingleton<IAIController>(sp => sp.GetRequiredService<AIPlayerController>());
        return services;
    }
}
