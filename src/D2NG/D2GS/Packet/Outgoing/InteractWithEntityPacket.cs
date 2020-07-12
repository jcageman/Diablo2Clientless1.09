using System;

namespace D2NG.D2GS.Packet.Outgoing
{
    internal class InteractWithEntityPacket : D2gsPacket
    {
        public InteractWithEntityPacket(uint entityId, Objects.EntityType entitytype) :
            base(
                BuildPacket(
                    (byte)OutGoingPacket.EntityInteract,
                    BitConverter.GetBytes((uint)entitytype),
                    BitConverter.GetBytes(entityId)
                )
            )
        {
        }

        public InteractWithEntityPacket(Entity entity) :
            base(
                BuildPacket(
                    (byte)OutGoingPacket.EntityInteract,
                    BitConverter.GetBytes((uint)entity.Type),
                    BitConverter.GetBytes(entity.Id)
                )
            )
        {
        }

        public InteractWithEntityPacket(byte[] packet) : base(packet)
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
