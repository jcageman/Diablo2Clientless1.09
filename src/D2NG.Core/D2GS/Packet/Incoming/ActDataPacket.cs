using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Exceptions;
using System.IO;
using System.Text;

namespace D2NG.Core.D2GS.Packet.Incoming
{
    internal class ActDataPacket : D2gsPacket
    {
        public ActDataPacket(D2gsPacket packet) : base(packet.Raw)
        {
            var reader = new BinaryReader(new MemoryStream(packet.Raw), Encoding.ASCII);
            var id = reader.ReadByte();
            if ((InComingPacket)id != InComingPacket.LoadAct)
            {
                throw new D2GSPacketException($"Invalid Packet Id {id}");
            }
            Act = (Act.Act)reader.ReadByte();
            MapId = reader.ReadUInt32();
            Area = (Area)reader.ReadUInt16();
            _ = reader.ReadUInt32();
            reader.Close();
        }

        public Act.Act Act { get; }
        public uint MapId { get; }
        public Area Area { get; }
    }
}