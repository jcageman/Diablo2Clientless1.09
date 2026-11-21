using D2NG.Core.D2GS.Exceptions;
using Serilog;
using Serilog.Events;

namespace D2NG.Core.D2GS.Packet.Incoming;

internal class AddExpPacket
{
    public AddExpPacket(D2gsPacket packet)
    {
        var reader = new PacketReader(packet.Raw);
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
                IsTotal = true;
                break;
            default:
                throw new D2GSPacketException($"Invalid Packet Id {id}");
        }
        if (Log.IsEnabled(LogEventLevel.Verbose))
        {
            Log.Verbose($"(0x{id,2:X2}) Add Experience:\n" +
                $"\tExperience: {Experience}");
        }
    }

    public uint Experience { get; internal set; }

    /// <summary>0x1C: the lifetime total, not a gain. Internal: the parser's public shape stays the pre-rewrite one.</summary>
    internal bool IsTotal { get; }
}