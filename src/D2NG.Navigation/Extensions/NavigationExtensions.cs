using D2NG.Navigation.Services.MapApi;
using D2NG.Navigation.Services.Pathing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace D2NG.Navigation.Extensions
{
    public static class NavigationExtensions
    {
        public static void RegisterNavigationServices(this IServiceCollection services, IConfigurationRoot config)
        {
            services.AddOptions<MapConfiguration>()
                .Bind(config.GetSection("map"))
                .ValidateDataAnnotations();
            services.AddSingleton<IMapApiService, MapApiService>();
            services.AddSingleton<IPathingService, PathingService>();
        }
    }
}
