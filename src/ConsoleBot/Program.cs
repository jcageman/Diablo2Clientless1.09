using ConsoleBot.Attack;
using ConsoleBot.Bots;
using ConsoleBot.Bots.Types;
using ConsoleBot.Clients.ExternalMessagingClient;
using ConsoleBot.Helpers;
using ConsoleBot.Mule;
using D2NG.Pickit;
using ConsoleBot.TownManagement;
using D2NG.Navigation.Extensions;
using D2NG.Navigation.Services.MapApi;
using ConsoleBot.Exceptions;
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

if (config["pickitconfig"] == null)
{
    Console.WriteLine("Missing pickitconfig parameter");
    throw new InvalidProgramException("Missing pickitconfig parameter");
}

if (!File.Exists(config["config"]))
{
    Console.WriteLine("Non-existing file in config parameter");
    throw new InvalidProgramException("Non-existing file in config parameter");
}

if (!File.Exists(config["pickitconfig"]))
{
    Console.WriteLine("Non-existing file in pickitconfig parameter");
    throw new InvalidProgramException("Non-existing file in pickitconfig parameter");
}

hostBuilder.Configuration.AddJsonFile(config["config"], optional: true, reloadOnChange: true);
hostBuilder.Configuration.AddJsonFile(config["pickitconfig"], optional: false, reloadOnChange: true);

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
hostBuilder.Services.AddOptions<PickitConfiguration>()
    .Bind(config.GetSection("pickit"))
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

var logLevelName = config.GetSection("bot")["logLevel"] ?? config["logLevel"];
var minimumLevel = LogEventLevel.Information;
if (!string.IsNullOrEmpty(logLevelName) && !Enum.TryParse(logLevelName, ignoreCase: true, out minimumLevel))
{
    Console.WriteLine($"Invalid logLevel '{logLevelName}', expected one of: {string.Join(", ", Enum.GetNames<LogEventLevel>())}");
    throw new InvalidProgramException("Invalid logLevel parameter in bot config");
}

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Is(minimumLevel)
    .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
    .WriteTo.Console()
    .WriteTo.File(
        logfileName,
        fileSizeLimitBytes: 20_000_000,
        rollOnFileSizeLimit: true
    )
    .CreateLogger();

// Optional second log holding one line per item the pickit judged, so the main log stays readable.
var pickitLogFile = config.GetSection("bot")["pickitLogFile"];
if (!string.IsNullOrEmpty(pickitLogFile))
{
    File.Delete(pickitLogFile);
    PickitAudit.Configure(new LoggerConfiguration()
        .MinimumLevel.Information()
        .WriteTo.File(
            pickitLogFile,
            fileSizeLimitBytes: 20_000_000,
            rollOnFileSizeLimit: true)
        .CreateLogger());
    Log.Logger.Information("Writing pickit decisions to {PickitLogFile}", pickitLogFile);
}

hostBuilder.Logging.ClearProviders();
hostBuilder.Logging.AddSerilog();
hostBuilder.Logging.SetMinimumLevel(minimumLevel switch
{
    LogEventLevel.Verbose => LogLevel.Trace,
    LogEventLevel.Debug => LogLevel.Debug,
    LogEventLevel.Warning => LogLevel.Warning,
    LogEventLevel.Error => LogLevel.Error,
    LogEventLevel.Fatal => LogLevel.Critical,
    _ => LogLevel.Information,
});
hostBuilder.Services.AddSingleton<IBotFactory, BotFactory>();

var host = hostBuilder.Build();
var botFactory = host.Services.GetRequiredService<IBotFactory>();
D2NG.Pickit.Pickit.Configure(host.Services.GetRequiredService<IOptions<PickitConfiguration>>().Value);

// The bot cannot path anywhere without the map API, so check it before logging on rather than
// creating games that abort the moment they need a path.
var mapApiUrl = host.Services.GetRequiredService<IOptions<MapConfiguration>>().Value.ApiUrl;
if (!await host.Services.GetRequiredService<IMapApiService>().IsAvailable())
{
    await StopWithFatalError(
        host,
        $"Map API at {mapApiUrl} is not reachable. Start the map server (d2mapapi) first, or correct map:apiUrl in the config. Not starting the bot.",
        exception: null);
    return 1;
}

Log.Logger.Information("Map API at {MapApiUrl} is reachable", mapApiUrl);

while (true)
{
    try
    {
        var botConfiguration = host.Services.GetRequiredService<IOptions<BotConfiguration>>();
        var botInstance = botFactory.CreateBot(botConfiguration.Value.BotType);
        await botInstance.Run();
    }
    catch (MapServerUnavailableException e)
    {
        // Restarting cannot help: every game would abort the same way.
        await StopWithFatalError(
            host,
            $"Map API at {mapApiUrl} went away while running. Start the map server (d2mapapi) and run the bot again. Stopping instead of restarting.",
            e);
        return 1;
    }
    catch (Exception e)
    {
        var externalClient = host.Services.GetRequiredService<IExternalMessagingClient>();
        Log.Logger.Error(e, "Bot crashed with exception {Message}, restarting", e.Message);
        await externalClient.SendMessage($"Bot crashed with exception {e.Message}, restarting");
        await Task.Delay(TimeSpan.FromSeconds(30));
    }
}

static async Task StopWithFatalError(IHost host, string message, Exception exception)
{
    if (exception == null)
    {
        Log.Logger.Fatal("{Message}", message);
    }
    else
    {
        Log.Logger.Fatal(exception, "{Message}", message);
    }

    Console.WriteLine(message);
    await host.Services.GetRequiredService<IExternalMessagingClient>().SendMessage(message);
    await Log.CloseAndFlushAsync();
}
