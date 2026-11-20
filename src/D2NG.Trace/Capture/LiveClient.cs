using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Packet;
using System;

namespace D2NG.Trace.Capture;

/// <summary>
/// One captured client's game, kept current packet by packet while a capture is read - the same handlers
/// and derived client actions a replay of its trace uses - so what it sent can be judged against what it saw.
/// </summary>
internal sealed class LiveClient : IDisposable
{
    private readonly GameServer _server = new();
    private readonly ReplayClock _clock = new();
    private readonly SnifferClientActions _derived;
    private readonly DateTime _startUtc;

    public LiveClient(DateTime startUtc)
    {
        _startUtc = startUtc;
        Game = new Game(_server, _clock);
        // Attribution judges a client by where the server last put it: a position assumed from runs that were
        // given to the wrong client would pull more of that client's runs after them.
        _derived = new SnifferClientActions(Game, assumeMoves: false);
    }

    public Game Game { get; }

    public bool LoggedOn { get; private set; }

    /// <summary>The class byte of the logon: 0 amazon, 1 sorceress, 2 necromancer, 3 paladin, 4 barbarian.</summary>
    public int Class { get; private set; } = -1;

    public void Received(byte[] packet, DateTime atUtc)
    {
        Advance(atUtc);
        try
        {
            _derived.BeforeReceived(packet);
            _server.Replay(new D2gsPacket(packet), sent: false);
            _derived.Received(packet);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
        }
    }

    public void Sent(byte[] packet, DateTime atUtc)
    {
        var elapsed = Advance(atUtc);
        if (packet.Length > 13 && packet[0] == (byte)OutGoingPacket.GameLogon)
        {
            LoggedOn = true;
            Class = packet[7];
            Game.SelectCharacter(Replayer.CharacterFromLogon(packet));
        }

        try
        {
            _derived.BeforeSent(packet);
            _server.Replay(new D2gsPacket(packet), sent: true);
            _derived.AfterSent(packet, elapsed);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
        }
    }

    private TimeSpan Advance(DateTime atUtc)
    {
        var elapsed = atUtc - _startUtc;
        if (elapsed > _clock.Elapsed)
        {
            _clock.Elapsed = elapsed;
        }

        _derived.Reached(_clock.Elapsed);
        return _clock.Elapsed;
    }

    public void Dispose() => _server.Dispose();
}
