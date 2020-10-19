using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Exceptions;
using D2NG.Core.Extensions;
using Serilog;
using System.IO;
using System.Text;

namespace D2NG.Core.D2GS.Packet.Incoming
{
    public class CorpseAssignPacket : D2gsPacket
    {
        public CorpseAssignPacket(D2gsPacket packet) : base(packet.Raw)
        {
            var reader = new BinaryReader(new MemoryStream(packet.Raw), Encoding.ASCII);
            if ((InComingPacket)reader.ReadByte() != InComingPacket.CorpseAssign)
            {
                throw new D2GSPacketException("Expected Packet Type Not Found");
            }
            CorpseAdded = reader.ReadBoolean();
            PlayerId = reader.ReadUInt32();
            CorpseId = reader.ReadUInt32();
        }

        public bool CorpseAdded { get; }
        public uint PlayerId { get; }
        public uint CorpseId { get; }
    }
}