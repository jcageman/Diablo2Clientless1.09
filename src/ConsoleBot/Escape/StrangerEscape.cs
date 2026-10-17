using ConsoleBot.Clients.ExternalMessagingClient;
using D2NG.Core;
using D2NG.Core.BNCS.Packet;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ConsoleBot.Escape;

/// <summary>
/// Stops the bot once someone outside <see cref="KnownAccounts"/> notices it. A whisper lets the bot
/// finish its run and stop instead of starting the next one; a stranger joining the game makes every
/// client leave at once. Either way the process ends, and nothing starts it again.
/// </summary>
public static class StrangerEscape
{
    private static KnownAccounts _knownAccounts = new([]);
    private static IExternalMessagingClient _messaging;
    private static readonly List<Client> _clients = [];
    private static readonly HashSet<Client> _shortLived = [];
    private static readonly object _lock = new();
    private static int _stopRequested;
    private static int _fleeRequested;

    /// <summary>Finish what is underway, then stop. Set by a whisper and by a stranger joining.</summary>
    public static bool StopRequested => Volatile.Read(ref _stopRequested) == 1;

    /// <summary>A stranger joined: the clients have been told to leave, abandon whatever is underway.</summary>
    public static bool FleeRequested => Volatile.Read(ref _fleeRequested) == 1;

    public static string Reason { get; private set; }

    public static void Configure(KnownAccounts knownAccounts, IExternalMessagingClient messaging)
    {
        _knownAccounts = knownAccounts;
        _messaging = messaging;
    }

    /// <summary>
    /// Watches a client for strangers. A mule client is <paramref name="shortLived"/>: it gets a new
    /// client every visit, and the ones already gone are dropped instead of piling up.
    /// </summary>
    public static void Watch(Client client, bool shortLived = false)
    {
        lock (_lock)
        {
            if (_clients.Contains(client))
            {
                return;
            }

            _clients.RemoveAll(c => _shortLived.Contains(c) && !c.Game.IsInGame() && !c.IsRealmConnected());
            _shortLived.RemoveWhere(c => !_clients.Contains(c));
            _clients.Add(client);
            if (shortLived)
            {
                _shortLived.Add(client);
            }
        }

        client.OnReceivedPacketEvent(Sid.CHATEVENT, packet => OnChatEvent(client, new ChatEventPacket(packet.Raw)));
        client.OnReceivedPacketEvent(InComingPacket.EventMessage, packet => OnEventMessage(client, new EventNotifyPacket(packet)));
    }

    private static void OnChatEvent(Client client, ChatEventPacket packet)
    {
        if (packet.Eid != Eid.WHISPER)
        {
            return;
        }

        var account = KnownAccounts.AccountOfSender(packet.Username);
        if (_knownAccounts.IsKnown(account))
        {
            return;
        }

        RequestStop(
            $"{client.LoggedInUserName()} was whispered by {packet.Username}: \"{packet.Text}\"",
            "Finishing the run, then stopping the bot without muling.",
            flee: false);
    }

    private static void OnEventMessage(Client client, EventNotifyPacket packet)
    {
        if (packet.EventType != EventType.JoinedTheGame || string.IsNullOrEmpty(packet.AccountName) || _knownAccounts.IsKnown(packet.AccountName))
        {
            return;
        }

        RequestStop(
            $"{packet.CharacterName}*{packet.AccountName} joined the game of {client.LoggedInUserName()} ({client.Game.Me?.Name})",
            "Left the game, stopping the bot.",
            flee: true);
    }

    private static void RequestStop(string what, string action, bool flee)
    {
        var first = Interlocked.Exchange(ref _stopRequested, 1) == 0;
        var firstFlee = flee && Interlocked.Exchange(ref _fleeRequested, 1) == 0;
        if (!first && !firstFlee)
        {
            Log.Warning("Stranger: {What}", what);
            return;
        }

        if (first)
        {
            Reason = what;
        }

        Log.Fatal("Stranger: {What}. {Action}", what, action);
        // Packet handlers run on the listener thread, which the leave and the message both wait on.
        _ = Task.Run(async () =>
        {
            if (firstFlee)
            {
                await LeaveAllGames();
            }

            if (_messaging != null)
            {
                try
                {
                    await _messaging.SendMessage($"Stranger: {what}. {action}");
                }
                catch (Exception e)
                {
                    Log.Warning(e, "Could not send the stranger message");
                }
            }
        });
    }

    private static async Task LeaveAllGames()
    {
        List<Client> clients;
        lock (_lock)
        {
            clients = _clients.Where(c => c.Game.IsInGame()).ToList();
        }

        await Task.WhenAll(clients.Select(async c =>
        {
            try
            {
                await c.Game.LeaveGame();
            }
            catch (Exception e)
            {
                Log.Warning(e, "{Account} could not leave its game", c.LoggedInUserName());
            }
        }));
    }
}
