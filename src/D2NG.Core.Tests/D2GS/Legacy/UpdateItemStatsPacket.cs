using D2NG.Core.D2GS.Exceptions;
using System.IO;
using System.Text;

using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;

namespace D2NG.Core.Tests.D2GS.Legacy;

public class UpdateItemStatsPacket : D2gsPacket
{
    public UpdateItemStatsPacket(D2gsPacket packet) : base(packet.Raw)
    {
        var reader = new BinaryReader(new MemoryStream(packet.Raw), Encoding.ASCII);
        var id = reader.ReadByte();
        if (InComingPacket.UpdateItemStats != (InComingPacket)id)
        {
            throw new D2GSPacketException($"Invalid Packet Id {id}");
        }

        ItemId = reader.ReadUInt32();
        reader.ReadByte();
        UpdateType = reader.ReadByte();
        Amount = reader.ReadUInt16();
        reader.ReadUInt16();
        reader.Close();
    }
    public uint ItemId { get; }

    public uint UpdateType { get; }

    public uint Amount { get; }
}
