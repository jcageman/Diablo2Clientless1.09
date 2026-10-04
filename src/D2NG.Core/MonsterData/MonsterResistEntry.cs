using D2NG.Core.D2GS.Enums;
using System;

namespace D2NG.Core.MonsterData;

/// <summary>
/// One row of monstats: the base resistances of a monster class on each difficulty, before any
/// boss modifiers the individual monster spawned with.
/// </summary>
public sealed class MonsterResistEntry
{
    public int ClassId { get; set; }

    /// <summary>The monstats Class string, for example "OblivionKnight". Diagnostics only.</summary>
    public string Class { get; set; }

    public int[] Physical { get; set; } = new int[3];
    public int[] Magic { get; set; } = new int[3];
    public int[] Fire { get; set; } = new int[3];
    public int[] Lightning { get; set; } = new int[3];
    public int[] Cold { get; set; } = new int[3];
    public int[] Poison { get; set; } = new int[3];

    public int Base(ResistType type, Difficulty difficulty)
    {
        var values = type switch
        {
            ResistType.Physical => Physical,
            ResistType.Magic => Magic,
            ResistType.Fire => Fire,
            ResistType.Lightning => Lightning,
            ResistType.Cold => Cold,
            ResistType.Poison => Poison,
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };

        return values[(int)difficulty];
    }
}
