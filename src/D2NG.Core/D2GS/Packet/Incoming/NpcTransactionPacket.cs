using D2NG.Core.D2GS.Exceptions;

namespace D2NG.Core.D2GS.Packet.Incoming;

/// <summary>
/// The server's answer to a buy, sell, repair or identify at a vendor. <see cref="Gold"/> is the gold
/// on person after the transaction, so two answers in a row price whatever happened between them.
/// </summary>
public class NpcTransactionPacket : D2gsPacket
{
    public const byte SellTransaction = 3;
    public const byte Success = 1;

    public NpcTransactionPacket(D2gsPacket packet) : base(packet.Raw)
    {
        var reader = new PacketReader(packet.Raw);
        var id = reader.ReadByte();
        if (InComingPacket.NPCTransaction != (InComingPacket)id)
        {
            throw new D2GSPacketException($"Invalid Packet Id {id}");
        }

        TransactionType = reader.ReadByte();
        Result = reader.ReadByte();
        reader.ReadUInt32();
        ItemId = reader.ReadUInt32();
        Gold = reader.ReadInt32();
    }

    public byte TransactionType { get; }

    public byte Result { get; }

    /// <summary>The item sold or bought; 0xFFFFFFFF when the server refused.</summary>
    public uint ItemId { get; }

    public int Gold { get; }
}
