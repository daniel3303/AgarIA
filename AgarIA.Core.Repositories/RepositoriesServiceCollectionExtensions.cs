using Equibles.Core.AutoWiring;
using Microsoft.Extensions.DependencyInjection;

namespace AgarIA.Core.Repositories;

public static class RepositoriesServiceCollectionExtensions
{
    public static IServiceCollection AddRepositories(this IServiceCollection services)
    {
        services.AutoWireServicesFrom<RepositoriesAssembly>();
        services.AddSingleton<PlayerRepository>();
        services.AddSingleton<FoodRepository>();
        services.AddSingleton<ProjectileRepository>();
        return services;
    }
}
