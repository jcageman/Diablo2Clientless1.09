using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Exceptions;
using D2NG.Core.D2GS.Helpers;
using D2NG.Core.D2GS.Objects;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace D2NG.Core.D2GS.Packet.Incoming
{
    public class AssignNpcPacket : D2gsPacket
    {
        public AssignNpcPacket(D2gsPacket packet) : base(packet.Raw)
        {
            var reader = new BitReader(packet.Raw);
            var id = (InComingPacket)reader.ReadByte();
            if (InComingPacket.AssignNPC2 != id && InComingPacket.AssignNPC1 != id)
            {
                throw new D2GSPacketException("Invalid Packet Id");
            }
            EntityId = reader.ReadUInt32();
            UniqueCode = (NPCCode)reader.ReadUInt16();
            Location = new Point(reader.ReadUInt16(), reader.ReadUInt16());
            LifePercentage = reader.ReadByte();
            if(id == InComingPacket.AssignNPC2)
            {
                reader.ReadByte(); // unknown
                IsBoss = reader.ReadBit();
                IsBossMinion = reader.ReadBit();
                IsChampion = reader.ReadBit();
                for (var i = 0; i < 5; ++i)
                {
                    reader.ReadBit(); // unknown
                }

                for (var i = 0; i < 23; ++i)
                {
                    reader.ReadByte(); // unknown
                }

                for (var i = 0; i < 9; ++i)
                {
                    var property = (MonsterEnchantment)reader.ReadByte();
                    if (property != MonsterEnchantment.None)
                    {
                        MonsterEnchantments.Add(property);
                    }
                }
            }
        }
        public uint EntityId { get; }
        public NPCCode UniqueCode { get; }
        public Point Location { get; }
        public bool IsBoss { get; } = false;
        public bool IsBossMinion { get; } = false;
        public bool IsChampion { get; } = false;
        public byte LifePercentage { get; }
        public HashSet<MonsterEnchantment> MonsterEnchantments { get; } = new HashSet<MonsterEnchantment>();
    }
}
