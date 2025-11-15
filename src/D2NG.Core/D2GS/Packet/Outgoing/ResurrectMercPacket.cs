using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Players;
using System;

namespace D2NG.Core.D2GS.Packet.Outgoing;

internal class ResurrectMercPacket : D2gsPacket
{
    public ResurrectMercPacket(Entity entity) :
        base(
            BuildPacket(
                (byte)OutGoingPacket.ResurrectMerc,
                BitConverter.GetBytes(entity.Id)
            )
        )
    {
    }
}
