using ConsoleBot.Clients.ExternalMessagingClient;
using ConsoleBot.Configurations;
using D2NG.Navigation.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Events;
using System;
using System.IO;
using System.Threading.Tasks;

namespace ConsoleBot
{
    internal class Program
    {
        private readonly IBotConfigurationFactory _botConfigurationFactory;

        public Program(IBotConfigurationFactory botConfigurationFactory)
        {
            _botConfigurationFactory = botConfigurationFactory;
        }
        private static async Task<int> Main(string[] args)
        {
            var services = ConfigureServices(args);
            var serviceProvider = services.BuildServiceProvider();

            return await serviceProvider.GetService<Program>().Run();
        }

        private static IServiceCollection ConfigureServices(string[] args)
        {
            var builder = new ConfigurationBuilder();
            builder.AddCommandLine(args);
            var config = builder.Build();
            if (config["config"] == null)
            {
                Console.WriteLine("Missing config parameter");
                throw new InvalidProgramException("Missing config parameter");
            }

            builder.AddJsonFile(config["config"], optional: true, reloadOnChange: true);
            config = builder.Build();

            IServiceCollection services = new ServiceCollection();

            services.AddSingleton<Program>();
            services.AddOptions();
            services.AddOptions<BotConfiguration>()
                .Bind(config.GetSection("bot"))
                .ValidateDataAnnotations();
            services.AddOptions<ExternalMessagingConfiguration>()
                .Bind(config.GetSection("externalMessaging"))
                .ValidateDataAnnotations();
            services.AddSingleton<IExternalMessagingClient, ExternalMessagingClient>();
            services.AddSingleton<IBotConfigurationFactory, BotConfigurationFactory>();
            services.AddHttpClient();
            services.AddMemoryCache();
            services.RegisterNavigationServices(config);

            var configFileName = config.GetSection("bot")["logFile"];
            File.Delete(configFileName);
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
                .WriteTo.Console(restrictedToMinimumLevel: LogEventLevel.Information)
                .WriteTo.File(configFileName,
                  restrictedToMinimumLevel: LogEventLevel.Information,
                  rollOnFileSizeLimit: true,
                  fileSizeLimitBytes: 20_000_000)
                .CreateLogger();
            services.AddLogging(configure => configure.AddSerilog());
            return services;
        }

        private async Task<int> Run()
        {
            var botConfiguration = _botConfigurationFactory.CreateConfiguration();
            return await botConfiguration.Run();
        }
    }
}
