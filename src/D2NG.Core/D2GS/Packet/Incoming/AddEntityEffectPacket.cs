using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Exceptions;
using D2NG.Core.D2GS.Objects;
using System.IO;
using System.Text;

namespace D2NG.Core.D2GS.Packet.Incoming;

public class AddEntityEffectPacket : D2gsPacket
{
    public AddEntityEffectPacket(D2gsPacket packet) : base(packet.Raw)
    {
        var reader = new BinaryReader(new MemoryStream(packet.Raw), Encoding.ASCII);
        if ((InComingPacket)reader.ReadByte() != InComingPacket.AddEntityEffect)
        {
            throw new D2GSPacketException("Expected Packet Type Not Found");
        }
        EntityType = (EntityType)reader.ReadByte();
        EntityId = reader.ReadUInt32();
        Effect = (EntityEffect)reader.ReadUInt32();
        ExtraData = reader.ReadUInt32();
    }

    public EntityType EntityType { get; }
    public uint EntityId { get; }
    public EntityEffect Effect { get; }
    public uint ExtraData { get; }
}
