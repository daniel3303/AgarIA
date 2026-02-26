using Equibles.Core.AutoWiring;
using Microsoft.Extensions.DependencyInjection;

namespace AgarIA.Core.Game;

public static class GameServiceCollectionExtensions
{
    public static IServiceCollection AddGame(this IServiceCollection services)
    {
        services.AutoWireServicesFrom<GameAssembly>();
        services.AddSingleton<SharedGrids>();
        services.AddSingleton<CollisionManager>();
        services.AddSingleton<HeuristicPlayerController>();
        services.AddSingleton<GameEngine>();
        services.AddHostedService(sp => sp.GetRequiredService<GameEngine>());
        services.AddSingleton<ExternalAiPlayerManager>();
        services.AddSingleton<IExternalAiPlayerManager>(sp => sp.GetRequiredService<ExternalAiPlayerManager>());
        services.AddSingleton<AIPlayerController>();
        services.AddSingleton<IAIController>(sp => sp.GetRequiredService<AIPlayerController>());
        return services;
    }
}
