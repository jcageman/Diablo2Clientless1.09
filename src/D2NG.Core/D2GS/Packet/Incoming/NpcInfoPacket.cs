using D2NG.Core.D2GS.Exceptions;
using D2NG.Core.D2GS.Objects;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace D2NG.Core.D2GS.Packet.Incoming;

/// <summary>
/// What an NPC currently has to say, sent when the client walks up to one. The message ids it carries
/// are the ones a client may echo back in a quest message, which is how a reward gets claimed without
/// having to know any of those ids in advance.
/// </summary>
/// <remarks>
/// Captured shape: entity type, entity id, a count, an unknown word, then a dword message id, padded
/// to 40 bytes. Akara with the den of evil reward waiting offered 0x4C and the client sent exactly
/// that back; once claimed she offered nothing and the same fields read zero.
/// </remarks>
internal class NpcInfoPacket : D2gsPacket
{
    public NpcInfoPacket(D2gsPacket packet) : base(packet.Raw)
    {
        var reader = new BinaryReader(new MemoryStream(Raw), Encoding.ASCII);
        var id = reader.ReadByte();
        if ((InComingPacket)id != InComingPacket.NPCInfo)
        {
            throw new D2GSPacketException($"Invalid Packet Id {id}");
        }
        EntityType = (EntityType)reader.ReadByte();
        EntityId = reader.ReadUInt32();
        MessageCount = reader.ReadUInt16();
        Unknown = reader.ReadUInt16();

        // Only the ids that fit the captured layout are read; the rest of the packet is padding.
        while (reader.BaseStream.Position + 4 <= reader.BaseStream.Length && MessageIds.Count < MessageCount)
        {
            var messageId = reader.ReadUInt32();
            if (messageId != 0)
            {
                MessageIds.Add(messageId);
            }
        }
    }

    public EntityType EntityType { get; }

    public uint EntityId { get; }

    public ushort MessageCount { get; }

    public ushort Unknown { get; }

    /// <summary>Messages this NPC is offering, empty when it has nothing pending.</summary>
    public List<uint> MessageIds { get; } = [];
}
