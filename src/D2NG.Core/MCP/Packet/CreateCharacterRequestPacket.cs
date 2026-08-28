using D2NG.Core.D2GS.Enums;
using System;
using System.Text;

namespace D2NG.Core.MCP.Packet;

/// <summary>
/// Creates a character on the realm. Captured from a real 1.09 client as the class as a dword, the
/// character flags as a word, then the name as a null terminated string.
/// </summary>
public class CreateCharacterRequestPacket : McpPacket
{
    public CreateCharacterRequestPacket(string name, CharacterClass characterClass, CharacterFlags flags) :
        base(
            BuildPacket(
                Mcp.CHARCREATE,
                BitConverter.GetBytes((uint)characterClass),
                BitConverter.GetBytes((ushort)flags),
                Encoding.ASCII.GetBytes(name ?? throw new ArgumentNullException(nameof(name))),
                [0x00]
            )
        )
    {
    }
}
