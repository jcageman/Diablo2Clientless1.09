using System;

namespace D2NG.Core.MCP;

/// <summary>
/// Flags describing what kind of character this is, as sent when creating one and as reported back
/// in the character list.
/// </summary>
/// <remarks>
/// Taken from 1.09 captures of five creations: a classic softcore character sends 0x0000, an
/// expansion softcore one 0x0020 and a hardcore expansion one 0x0024. The bits line up with the
/// flags byte <see cref="Character"/> already decodes out of the character list, so both directions
/// share one layout.
/// </remarks>
[Flags]
public enum CharacterFlags : ushort
{
    None = 0x0000,
    Hardcore = 0x0004,
    Expansion = 0x0020
}
