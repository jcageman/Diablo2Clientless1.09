using System.Collections.Generic;
using System.Linq;

namespace D2NG.Mule.Packing;

/// <summary>
/// What still fits on a mule: for each item height, the widest item that has a spot in the stash or the inventory.
/// One number per height because a stash can take a 1x4 charm and still refuse a 2x2.
/// </summary>
public static class FitProfile
{
    public const int MaxHeight = 4;
    public const int MaxWidth = 2;

    public static int[] Of(params OccupancyGrid[] grids)
    {
        var profile = new int[MaxHeight + 1];
        for (var height = 1; height <= MaxHeight; height++)
        {
            for (var width = 1; width <= MaxWidth; width++)
            {
                if (grids.Any(g => g.Fits(new Shape(width, height))))
                {
                    profile[height] = width;
                }
            }
        }
        return profile;
    }

    public static bool Fits(IReadOnlyList<int> profile, Shape shape)
    {
        if (profile == null || shape.Height >= profile.Count)
        {
            return true;
        }
        return profile[shape.Height] >= shape.Width;
    }
}
