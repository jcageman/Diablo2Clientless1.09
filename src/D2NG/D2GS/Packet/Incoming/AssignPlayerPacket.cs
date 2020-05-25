using D2NG.Extensions;
using Serilog;
using System.IO;
using System.Text;

namespace D2NG.D2GS.Packet
{
    internal class AssignPlayerPacket : D2gsPacket
    {
        public AssignPlayerPacket(D2gsPacket packet) : base(packet.Raw)
        {
            var reader = new BinaryReader(new MemoryStream(packet.Raw), Encoding.ASCII);
            if (reader.ReadByte() != 0x59)
            {
                throw new D2GSPacketException("Expected Packet Type Not Found");
            }
            Id = reader.ReadUInt32();
            Class = (CharacterClass)reader.ReadByte();
            Name = reader.ReadNullTerminatedString();
            Location = new Point(reader.ReadUInt16(), reader.ReadUInt16());
            Log.Verbose($"(0x{packet.Raw[0], 2:X2}) Assigning Player:\n" +
                        $"\tName: {Name}\n" +
                        $"\tClass: {Class}\n" +
                        $"\tId: {Id}\n" +
                        $"\tLocation: {Location}\n");
        }

        public Point Location { get; }
        public uint Id { get; }
        public CharacterClass Class { get; }
        public string Name { get; }
    }
}