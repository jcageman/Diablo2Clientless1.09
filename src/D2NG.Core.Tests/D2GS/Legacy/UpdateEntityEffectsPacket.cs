using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Exceptions;

using D2NG.Core.D2GS.Objects;
using System.Collections.Generic;

using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;

namespace D2NG.Core.Tests.D2GS.Legacy;

public class UpdateEntityEffectsPacket : D2gsPacket
{
    public UpdateEntityEffectsPacket(D2gsPacket packet) : base(packet.Raw)
    {
        var reader = new LegacyBitReader(packet.Raw);
        if ((InComingPacket)reader.ReadByte() != InComingPacket.UpdateEntityEffects)
        {
            throw new D2GSPacketException("Expected Packet Type Not Found");
        }
        EntityType = (EntityType)reader.ReadByte();
        EntityId = reader.ReadUInt32();
        for (int i = 0; i < 160; i++)
        {
            if(reader.ReadBit())
            {
                EntityEffects.Add((EntityEffect)i);
            }
        }
    }

    public EntityType EntityType { get; }
    public uint EntityId { get; }
    public HashSet<EntityEffect> EntityEffects { get; } = [];
}
