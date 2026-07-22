using ConsoleBot.Bots;
using System;

namespace ConsoleBot.Pickit;

public static class PickitThresholdScaling
{
    private static PickitThresholdScalingConfiguration _configuration = new();

    public static void Configure(PickitThresholdScalingConfiguration configuration)
    {
        _configuration = configuration ?? new PickitThresholdScalingConfiguration();
    }

    public static int Min(PickitItemType itemType, int baseValue)
    {
        if (baseValue <= 0)
        {
            return baseValue;
        }

        var percent = ResolvePercent(itemType);
        var scaledValue = (int)Math.Floor(baseValue * percent / 100.0);
        return Math.Max(0, scaledValue);
    }

    private static int ResolvePercent(PickitItemType itemType)
    {
        var configuredPercent = itemType switch
        {
            PickitItemType.Ring => _configuration.RingPercent,
            PickitItemType.Gloves => _configuration.GlovesPercent,
            PickitItemType.Boots => _configuration.BootsPercent,
            PickitItemType.Helms => _configuration.HelmsPercent,
            PickitItemType.Armors => _configuration.ArmorsPercent,
            PickitItemType.Amulets => _configuration.AmuletsPercent,
            PickitItemType.Shields => _configuration.ShieldsPercent,
            PickitItemType.Weapons => _configuration.WeaponsPercent,
            PickitItemType.Belts => _configuration.BeltsPercent,
            _ => _configuration.DefaultPercent
        };

        return Math.Clamp(configuredPercent, 0, 300);
    }
}
