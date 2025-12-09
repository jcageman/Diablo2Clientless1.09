using ConsoleBot.Bots.Types;
using ConsoleBot.Bots.Types.Cows;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Linq;

namespace ConsoleBot.Bots;

public class BotFactory : IBotFactory
{
    private readonly IServiceProvider _serviceProvider;

    public BotFactory(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }
    public IBotInstance CreateBot(string botType)
    {
        var botInstances = _serviceProvider.GetServices<IBotInstance>();


        var botNames = botInstances.Select(b => b.GetName()).ToList();
        var duplicateNames = botNames.GroupBy(b => b).Where(g => g.Count() > 1).ToList();
        if (duplicateNames.Count != 0)
        {
            var duplicateNamesStr = string.Join(", ", duplicateNames);
            throw new NotSupportedException($"One or more bots have been registered with an already existing name. Duplicate names: {duplicateNamesStr}");

        }
        foreach (var botInstance in botInstances)
        {
            if(botInstance.GetName() == botType)
            {
                return botInstance;
            }
        }

        var availableNames = string.Join(", ", botNames);
        throw new NotSupportedException($"{nameof(botType)} contains not supported type {botType}, it should be one of the following: {availableNames}");
    }
}
