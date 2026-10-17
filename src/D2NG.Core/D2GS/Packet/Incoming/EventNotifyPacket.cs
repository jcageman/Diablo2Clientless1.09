using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Exceptions;
using System;
using System.IO;
using System.Text;

namespace D2NG.Core.D2GS.Packet.Incoming;

public class EventNotifyPacket : D2gsPacket
{
    private const int NameLength = 16;

    public uint EntityId { get; }

    public byte Action { get; }
    public EventType EventType { get; }

    public PlayerRelationType PlayerRelationType { get; }

    /// <summary>
    /// For a player joining or leaving: the character, and the account it is played from. Both are
    /// fixed 16-byte fields that keep leftovers of earlier names after their terminator.
    /// </summary>
    public string CharacterName { get; }

    public string AccountName { get; }

    public EventNotifyPacket(D2gsPacket packet) : base(packet.Raw)
    {
        var reader = new BinaryReader(new MemoryStream(packet.Raw), Encoding.ASCII);
        var id = reader.ReadByte();
        if ((InComingPacket)id != InComingPacket.EventMessage)
        {
            throw new D2GSPacketException($"Invalid Packet Id {id}");
        }

        EventType = (EventType)reader.ReadByte();
        Action = reader.ReadByte();
        EntityId = reader.ReadUInt32();
        PlayerRelationType = (PlayerRelationType)reader.ReadByte();
        CharacterName = ReadName(packet.Raw, 8);
        AccountName = ReadName(packet.Raw, 8 + NameLength);
    }

    private static string ReadName(byte[] raw, int offset)
    {
        if (raw.Length < offset + NameLength)
        {
            return null;
        }

        var end = Array.IndexOf(raw, (byte)0, offset, NameLength);
        return Encoding.ASCII.GetString(raw, offset, (end < 0 ? offset + NameLength : end) - offset);
    }
}
