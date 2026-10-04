namespace D2NG.Core.ObjectData;

/// <summary>One shrine row of objects.txt: the object class id the server sends and what it spawns as.</summary>
public sealed class ShrineEntry
{
    /// <summary>The objects.txt record index, which is the object code in the assign object packet and the map api.</summary>
    public int ClassId { get; set; }

    /// <summary>The objects.txt Name, for example "Shrine" or "manashrine". Diagnostics only.</summary>
    public string Name { get; set; }

    public ShrineKind Kind { get; set; }
}
