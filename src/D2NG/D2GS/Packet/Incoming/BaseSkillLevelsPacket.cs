using D2NG.D2GS.Players;
using Serilog;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using D2NG.D2GS.Exceptions;

namespace D2NG.D2GS.Packet.Incoming
{
    internal class BaseSkillLevelsPacket : D2gsPacket
    {
        internal BaseSkillLevelsPacket(D2gsPacket packet) : base(packet.Raw)
        {
            var reader = new BinaryReader(new MemoryStream(packet.Raw), Encoding.ASCII);
            var id = reader.ReadByte();
            if ((InComingPacket)id != InComingPacket.BaseSkillLevels)
            {
                throw new D2GSPacketException($"Invalid Packet Id {id}");
            }
            var count = reader.ReadByte();
            PlayerId = reader.ReadUInt32();

            for (int i = 0; i < count; i++)
            {
                Skills[(Skill)reader.ReadUInt16()] = reader.ReadByte();
            }
            reader.Close();

            Log.Verbose($"(0x{packet.Raw[0],2:X2}) Base Skill Levels:\n" +
                string.Join("\n", Skills
                            .OrderBy(s => s.Key.ToString())
                            .Select(s => $"\t{s.Key} : {s.Value}")));
        }

        public ConcurrentDictionary<Skill, int> Skills { get; } = new ConcurrentDictionary<Skill, int>();
        public uint PlayerId { get; }
    }
}