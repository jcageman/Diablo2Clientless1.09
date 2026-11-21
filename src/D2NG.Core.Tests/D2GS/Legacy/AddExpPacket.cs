using D2NG.Core.D2GS.Exceptions;
using Serilog;
using System.IO;
using System.Text;

using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;

namespace D2NG.Core.Tests.D2GS.Legacy;

internal class AddExpPacket
{
    public AddExpPacket(D2gsPacket packet)
    {
        var reader = new BinaryReader(new MemoryStream(packet.Raw), Encoding.ASCII);
        var id = reader.ReadByte();
        switch ((InComingPacket)id)
        {
            case InComingPacket.AddExperienceByte:
                Experience = reader.ReadByte();
                break;
            case InComingPacket.AddExperienceWord:
                Experience = reader.ReadUInt16();
                break;
            case InComingPacket.AddExperienceDword:
                Experience = reader.ReadUInt32();
                break;
            default:
                throw new D2GSPacketException($"Invalid Packet Id {id}");
        }
        reader.Close();
        Log.Verbose($"(0x{id,2:X2}) Add Experience:\n" +
            $"\tExperience: {Experience}");
    }

    public uint Experience { get; internal set; }
}
