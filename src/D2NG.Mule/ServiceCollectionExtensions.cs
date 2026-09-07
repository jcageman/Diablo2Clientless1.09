using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace D2NG.Mule;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMuleStore(this IServiceCollection services, string connectionString)
    {
        services.AddMarten(connectionString);
        services.AddSingleton<IMuleRepository, MuleRepository>();
        return services;
    }
}
