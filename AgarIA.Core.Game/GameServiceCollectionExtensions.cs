using Equibles.Core.AutoWiring;
using Microsoft.Extensions.DependencyInjection;

namespace AgarIA.Core.Game;

public static class GameServiceCollectionExtensions
{
    public static IServiceCollection AddGame(this IServiceCollection services)
    {
        services.AutoWireServicesFrom<GameAssembly>();
        services.AddSingleton<CollisionManager>();
        services.AddSingleton<GameEngine>();
        services.AddHostedService(sp => sp.GetRequiredService<GameEngine>());
        return services;
    }
}
