using Serilog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace D2NG.Core.D2GS;

/// <summary>
/// The game server takes five connections per address in about fifteen seconds and closes any more at
/// once (FF FF instead of A7 01): a sixth was refused 10 and 12.5 seconds after five others, got in at 15
/// one time out of two, and three together all got in at 17. Every bot process on this machine shares
/// the address, and a mule joining its game twelve seconds before a party's five joins was enough for a
/// refusal, so the connections are counted in a file all of them share rather than per bot. Each game
/// server counts its own connections, so there is one file per server address: bots playing on another
/// game server do not hold up a party.
/// </summary>
public static class GameServerConnectionGate
{
    public const int ConnectionsPerWindow = 5;

    public static readonly TimeSpan Window = TimeSpan.FromSeconds(17);

    /// <summary>Waits until a connection to <paramref name="server"/> fits in the window, then counts this one.</summary>
    public static async Task WaitForSlot(string who, IPAddress server)
    {
        var ledgerPath = Path.Combine(Path.GetTempPath(), $"d2ng-gameserver-connections-{server.ToString().Replace(':', '-')}.txt");
        var waitedSince = DateTime.UtcNow;
        while (true)
        {
            var wait = TryTakeSlot(ledgerPath, DateTime.UtcNow);
            if (wait <= TimeSpan.Zero)
            {
                var waited = DateTime.UtcNow - waitedSince;
                if (waited > TimeSpan.FromMilliseconds(500))
                {
                    Log.Information("{Who} waited {Seconds:F1}s for a game server connection slot", who, waited.TotalSeconds);
                }

                return;
            }

            await Task.Delay(wait);
        }
    }

    /// <returns>Zero when the slot was taken, otherwise how long until the oldest connection leaves the window.</returns>
    internal static TimeSpan TryTakeSlot(string ledgerPath, DateTime now)
    {
        FileStream ledger;
        try
        {
            ledger = new FileStream(ledgerPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            // Another process holds the ledger for the few milliseconds it takes to update it.
            return TimeSpan.FromMilliseconds(20);
        }

        using (ledger)
        {
            var recent = ReadRecent(ledger, now);
            if (recent.Count >= ConnectionsPerWindow)
            {
                return recent[recent.Count - ConnectionsPerWindow] + Window - now + TimeSpan.FromMilliseconds(50);
            }

            recent.Add(now);
            ledger.SetLength(0);
            using var writer = new StreamWriter(ledger);
            foreach (var at in recent)
            {
                writer.WriteLine(at.Ticks.ToString(CultureInfo.InvariantCulture));
            }

            return TimeSpan.Zero;
        }
    }

    private static List<DateTime> ReadRecent(FileStream ledger, DateTime now)
    {
        using var reader = new StreamReader(ledger, leaveOpen: true);
        var recent = new List<DateTime>();
        string line;
        while ((line = reader.ReadLine()) != null)
        {
            if (long.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks))
            {
                var at = new DateTime(ticks, DateTimeKind.Utc);
                if (now - at < Window && at <= now)
                {
                    recent.Add(at);
                }
            }
        }

        ledger.Position = 0;
        return recent.OrderBy(t => t).ToList();
    }
}
