using System;

namespace D2NG.Core.MonsterData;

/// <summary>Provenance of a generated resist table, so a stale one can be recognised.</summary>
public sealed class MonsterResistSource
{
    public string GameDirectory { get; set; }

    /// <summary>The archive the table was actually read from, after patch-first resolution.</summary>
    public string Archive { get; set; }

    public string File { get; set; }

    /// <summary>Hash of the raw monstats table, which changes the moment a realm patch does.</summary>
    public string Sha256 { get; set; }

    public int RecordCount { get; set; }
    public int RecordSize { get; set; }
    public int ResistOffset { get; set; }
    public DateTime ExtractedUtc { get; set; }

    public override string ToString()
        => $"{Archive}/{File} ({RecordCount} monsters, sha256 {Sha256?[..8]}, extracted {ExtractedUtc:u})";
}
