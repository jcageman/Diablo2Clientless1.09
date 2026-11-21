using D2NG.Core.D2GS.Exceptions;
using Serilog;
using Serilog.Events;
using System.Linq;

namespace D2NG.Core.D2GS.Packet.Incoming;

internal class QuestInfoPacket : D2gsPacket
{
    public QuestInfoPacket(D2gsPacket packet) : base(packet.Raw)
    {
        var reader = new PacketReader(Raw);
        var id = reader.ReadByte();
        if ((InComingPacket)id != InComingPacket.QuestInfo)
        {
            throw new D2GSPacketException("Invalid packet id found");
        }
        UpdateType = reader.ReadByte();
        UnitGid = reader.ReadUInt32();
        Timer = reader.ReadByte();
        for (int i = 0; i < 96; i++)
        {
            Quests[i] = reader.ReadByte();
        }

        if (Log.IsEnabled(LogEventLevel.Verbose))
        {
            Log.Verbose($"(0x{id,2:X2}) Player Quest Info:\n" +
                $"\tUpdate Type: {UpdateType}\n" +
                $"\tUnit Gid: {UnitGid}\n" +
                $"\tTimer: {Timer}\n" +
                $"\tQuests: {Quests.Aggregate("", (s, i) => s + "," + $"{i,2:X2}")}");
        }
    }

    public byte UpdateType { get; }
    public uint UnitGid { get; }
    public byte Timer { get; }

    public byte[] Quests { get; } = new byte[96];
}
