using D2NG.Core.D2GS.Objects;
using System;

namespace D2NG.Core.D2GS.Packet.Outgoing
{
    internal class EntityActionPacket : D2gsPacket
    {
        public EntityActionPacket(Entity entity, TownFolkActionType actionType) :
            base(
                BuildPacket(
                    (byte)OutGoingPacket.EntityAction,
                    BitConverter.GetBytes((uint)actionType),
                    BitConverter.GetBytes(entity.Id),
                    BitConverter.GetBytes((uint)0x00)
                )
            )
        {
        }

        public EntityActionPacket(byte[] packet) : base(packet)
        {
        }

        public uint GetEntityType()
        {
            return BitConverter.ToUInt32(Raw, 1);
        }
        public uint GetEntityId()
        {
            return BitConverter.ToUInt32(Raw, 5);
        }
    }
}
