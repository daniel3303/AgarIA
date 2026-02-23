using Equibles.Core.AutoWiring;
using Microsoft.Extensions.DependencyInjection;

namespace AgarIA.Core.Data;

public static class DataServiceCollectionExtensions
{
    public static IServiceCollection AddData(this IServiceCollection services)
    {
        services.AutoWireServicesFrom<DataAssembly>();
        services.AddSingleton<Models.GameState>();
        return services;
    }
}
