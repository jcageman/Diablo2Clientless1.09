using ConsoleBot.Attack;
using ConsoleBot.Bots;
using ConsoleBot.Bots.Types;
using ConsoleBot.Clients.ExternalMessagingClient;
using ConsoleBot.Mule;
using ConsoleBot.TownManagement;
using D2NG.Navigation.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;
using System;
using System.IO;
using System.Threading.Tasks;

namespace ConsoleBot
{
    internal sealed class Program
    {
        private readonly IBotFactory _botConfigurationFactory;

        public Program(IBotFactory botConfigurationFactory)
        {
            _botConfigurationFactory = botConfigurationFactory;
        }
        private static async Task<int> Main(string[] args)
        {
            var services = ConfigureServices(args);
            var serviceProvider = services.BuildServiceProvider();

            return await serviceProvider.GetService<Program>().Run(serviceProvider);
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

            if(!File.Exists(config["config"]))
            {
                Console.WriteLine("Non-existing file in config parameter");
                throw new InvalidProgramException("Non-existing file in config parameter");
            }

            builder.AddJsonFile(config["config"], optional: true, reloadOnChange: true);

            if (config["muleconfig"] != null)
            {
                if (!File.Exists(config["muleconfig"]))
                {
                    Console.WriteLine("Non-existing file in muleconfig parameter");
                    throw new InvalidProgramException("Non-existing file in muleconfig parameter");
                }
                builder.AddJsonFile(config["muleconfig"], optional: true, reloadOnChange: true);
            }

            config = builder.Build();

            IServiceCollection services = new ServiceCollection();

            services.AddSingleton<Program>();
            services.AddOptions();
            services.AddOptions<MuleConfiguration>()
                .Bind(config.GetSection("mule"))
                .ValidateDataAnnotations();
            services.AddOptions<BotConfiguration>()
                .Bind(config.GetSection("bot"))
                .ValidateDataAnnotations();
            if(config.GetSection("externalMessaging").Exists())
            {
                services.AddOptions<ExternalMessagingConfiguration>()
                    .Bind(config.GetSection("externalMessaging"))
                    .ValidateDataAnnotations();
                services.AddSingleton<IExternalMessagingClient, ExternalMessagingClient>();
            }
            else
            {
                services.AddSingleton<IExternalMessagingClient, DummyMessagingClient>();
            }
            services.AddSingleton<IMuleService, MuleService>();
            services.AddSingleton<ITownManagementService, TownManagementService>();
            services.AddSingleton<IAttackService, AttackService>();
            services.AddHttpClient();
            services.AddMemoryCache();
            services.RegisterBotServices(config);
            services.RegisterNavigationServices(config);

            var logfileName = config.GetSection("bot")["logFile"];
            if(string.IsNullOrEmpty(logfileName))
            {
                Console.WriteLine("Missing logFile parameter in bot config");
                throw new InvalidProgramException("Missing logFile parameter in bot config");
            }
            File.Delete(logfileName);
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
                .WriteTo.Console(restrictedToMinimumLevel: LogEventLevel.Information)
                .WriteTo.File(logfileName,
                  restrictedToMinimumLevel: LogEventLevel.Information,
                  rollOnFileSizeLimit: true,
                  fileSizeLimitBytes: 20_000_000)
                .CreateLogger();
            services.AddLogging(configure => configure.AddSerilog());
            return services;
        }

        private async Task<int> Run(ServiceProvider serviceProvider)
        {
            while(true)
            {
                try
                {
                    var botConfiguration = serviceProvider.GetRequiredService<IOptions<BotConfiguration>>();
                    var botInstance = _botConfigurationFactory.CreateBot(botConfiguration.Value.BotType);
                    await botInstance.Run();
                }
                catch (Exception e)
                {
                    var externalClient = serviceProvider.GetRequiredService<IExternalMessagingClient>();
                    Log.Information($"Bot crashed with exception {e.Message}, stack: {e.StackTrace}, restarting");
                    await externalClient.SendMessage($"Bot crashed with exception {e.Message}, restarting");
                    await Task.Delay(TimeSpan.FromSeconds(30));
                }
            }

        }
    }
}
