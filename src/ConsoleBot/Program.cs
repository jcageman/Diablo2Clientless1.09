using ConsoleBot.Attack;
using ConsoleBot.Bots;
using ConsoleBot.Bots.Types;
using ConsoleBot.Clients.ExternalMessagingClient;
using ConsoleBot.Mule;
using ConsoleBot.TownManagement;
using D2NG.Navigation.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;
using System;
using System.IO;
using System.Threading.Tasks;

var hostBuilder = Host.CreateApplicationBuilder(args);

hostBuilder.Configuration.AddCommandLine(args);
var config = hostBuilder.Configuration;
if (config["config"] == null)
{
    Console.WriteLine("Missing config parameter");
    throw new InvalidProgramException("Missing config parameter");
}

if (!File.Exists(config["config"]))
{
    Console.WriteLine("Non-existing file in config parameter");
    throw new InvalidProgramException("Non-existing file in config parameter");
}

hostBuilder.Configuration.AddJsonFile(config["config"], optional: true, reloadOnChange: true);

if (config["muleconfig"] != null)
{
    if (!File.Exists(config["muleconfig"]))
    {
        Console.WriteLine("Non-existing file in muleconfig parameter");
        throw new InvalidProgramException("Non-existing file in muleconfig parameter");
    }
    hostBuilder.Configuration.AddJsonFile(config["muleconfig"], optional: true, reloadOnChange: true);
}

hostBuilder.Services.AddOptions();
hostBuilder.Services.AddOptions<MuleConfiguration>()
    .Bind(config.GetSection("mule"))
    .ValidateDataAnnotations();
hostBuilder.Services.AddOptions<BotConfiguration>()
    .Bind(config.GetSection("bot"))
    .ValidateDataAnnotations();
if (config.GetSection("externalMessaging").Exists())
{
    hostBuilder.Services.AddOptions<ExternalMessagingConfiguration>()
        .Bind(config.GetSection("externalMessaging"))
        .ValidateDataAnnotations();
    hostBuilder.Services.AddSingleton<IExternalMessagingClient, ExternalMessagingClient>();
}
else
{
    hostBuilder.Services.AddSingleton<IExternalMessagingClient, DummyMessagingClient>();
}
hostBuilder.Services.AddSingleton<IMuleService, MuleService>();
hostBuilder.Services.AddSingleton<ITownManagementService, TownManagementService>();
hostBuilder.Services.AddSingleton<IAttackService, AttackService>();
hostBuilder.Services.AddHttpClient();
hostBuilder.Services.AddMemoryCache();
hostBuilder.Services.RegisterBotServices(config);
hostBuilder.Services.RegisterNavigationServices(config);

var logfileName = config.GetSection("bot")["logFile"];
if (string.IsNullOrEmpty(logfileName))
{
    Console.WriteLine("Missing logFile parameter in bot config");
    throw new InvalidProgramException("Missing logFile parameter in bot config");
}
File.Delete(logfileName);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
    .WriteTo.Console()
    .WriteTo.File(
        logfileName,
        fileSizeLimitBytes: 20_000_000,
        rollOnFileSizeLimit: true
    )
    .CreateLogger();

hostBuilder.Logging.ClearProviders();
hostBuilder.Logging.AddSerilog();
hostBuilder.Logging.SetMinimumLevel(LogLevel.Information);
hostBuilder.Services.AddSingleton<IBotFactory, BotFactory>();

var host = hostBuilder.Build();
var botFactory = host.Services.GetRequiredService<IBotFactory>();

while (true)
{
    try
    {
        var botConfiguration = host.Services.GetRequiredService<IOptions<BotConfiguration>>();
        var botInstance = botFactory.CreateBot(botConfiguration.Value.BotType);
        await botInstance.Run();
    }
    catch (Exception e)
    {
        var externalClient = host.Services.GetRequiredService<IExternalMessagingClient>();
        Log.Logger.Information("Bot crashed with exception {Message}, stack: {StackTrace}, restarting", e.Message, e.StackTrace);
        await externalClient.SendMessage($"Bot crashed with exception {e.Message}, restarting");
        await Task.Delay(TimeSpan.FromSeconds(30));
    }
}
