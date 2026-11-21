using D2NG.Core.D2GS.Exceptions;
using Serilog;
using Serilog.Events;
using System.Linq;

namespace D2NG.Core.D2GS.Packet.Incoming;

internal class GameQuestInfoPacket : D2gsPacket
{
    public GameQuestInfoPacket(D2gsPacket packet) : base(packet.Raw)
    {
        var reader = new PacketReader(Raw);
        var id = reader.ReadByte();
        if ((InComingPacket)id != InComingPacket.GameQuestInfo)
        {
            throw new D2GSPacketException($"Invalid Packet Id {id}");
        }
        for (int i = 0; i < 96; i++)
        {
            Quests[i] = reader.ReadByte();
        }

        if (Log.IsEnabled(LogEventLevel.Verbose))
        {
            Log.Verbose($"(0x{id,2:X2}) Game Quest Info:\n" +
                $"\tQuests: {Quests.Aggregate("", (s, i) => s + "," + $"{i,2:X2}")}");
        }
    }

    public byte[] Quests { get; } = new byte[96];
}
