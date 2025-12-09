using ConsoleBot.Bots.Types.Assist;
using ConsoleBot.Bots.Types.Baal;
using ConsoleBot.Bots.Types.Cows;
using ConsoleBot.Bots.Types.CS;
using ConsoleBot.Bots.Types.Cuber;
using ConsoleBot.Bots.Types.Mephisto;
using ConsoleBot.Bots.Types.Pindle;
using ConsoleBot.Bots.Types.Travincal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ConsoleBot.Bots.Types;

public static class BotTypeExtensions
{
    public static void RegisterBotServices(this IServiceCollection services, IConfigurationRoot config)
    {
        services.AddSingleton<IBotInstance, MephistoBot>();
        services.AddSingleton<IBotInstance, TravincalBot>();
        services.AddSingleton<IBotInstance, TestBot>();
        services.AddSingleton<IBotInstance, CowBot>();
        services.AddSingleton<IBotInstance, CSBot>();
        services.AddSingleton<IBotInstance, AssistBot>();
        services.AddSingleton<IBotInstance, PindleBot>();
        services.AddSingleton<IBotInstance, BaalBot>();
        services.AddSingleton<IBotInstance, CubeBot>();
        services.AddSingleton<IBotFactory, BotFactory>();
        services.AddOptions<CowConfiguration>()
            .Bind(config.GetSection("bot").GetSection("cows"));
        services.AddOptions<MephistoConfiguration>()
            .Bind(config.GetSection("bot").GetSection("mephisto"));
        services.AddOptions<TravincalConfiguration>()
            .Bind(config.GetSection("bot").GetSection("travincal"));
        services.AddOptions<CsConfiguration>()
            .Bind(config.GetSection("bot").GetSection("cs"));
        services.AddOptions<PindleConfiguration>()
            .Bind(config.GetSection("bot").GetSection("pindle"));
        services.AddOptions<BaalConfiguration>()
            .Bind(config.GetSection("bot").GetSection("baal"));
        services.AddOptions<CubeConfiguration>()
            .Bind(config.GetSection("bot").GetSection("cube"));
        services.AddOptions<AssistConfiguration>()
            .Bind(config.GetSection("bot").GetSection("assist"));
    }
}
