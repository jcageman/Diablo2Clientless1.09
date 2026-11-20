using D2NG.Core;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.Tracing;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.Trace;

/// <summary>
/// A real client's trace carries no client actions: the state the bot's <see cref="Game"/> methods set
/// beside a send - which item is lifted, used or sold, a forgotten waypoint selection, the merchant's
/// items dropped, the position assumed after a move - is derived here from the packets the client
/// sent, at the moment the bot's method would set it. Used only for traces whose source is the sniffer.
/// </summary>
internal sealed class SnifferClientActions(Game game, bool assumeMoves = true)
{
    private const uint RefreshGamble = 0x04;
    private const double MoveMillisecondsPerUnit = 80;
    private const double ShortMoveMilliseconds = 120;

    /// <summary>Every class walks at velocity 6 and runs at 9 in 1.09's charstats; the bot only runs, a real client also walks.</summary>
    private const double WalkVelocity = 6;

    private const double RunVelocity = 9;

    private readonly Game _game = game;
    private (TimeSpan Due, uint Location)? _move;

    public Dictionary<ClientAction, long> Derived { get; } = [];

    /// <summary>
    /// The bot's sleeps are timed by the system timer, whose ticks are about 15.6 ms apart, while records are stamped
    /// with the high resolution clock: its assumed arrival can land up to a tick before the computed time.
    /// </summary>
    private static readonly TimeSpan TimerTick = TimeSpan.FromMilliseconds(16);

    /// <summary>Applies a move whose assumed arrival has come; called before every record.</summary>
    public void Reached(TimeSpan elapsed)
    {
        if (_move is { } move && move.Due <= elapsed + TimerTick)
        {
            _move = null;
            Apply(ClientAction.Move, move.Location);
        }
    }

    /// <summary>
    /// The server placing the client's own player (a portal, a waypoint, a teleport) ends a move under way: a real
    /// client that clicked a portal on its way somewhere never arrives there. The bot waits out its moves, so its
    /// traces never meet this.
    /// </summary>
    public void Received(byte[] packet)
    {
        if (_move != null && packet.Length >= 6 && packet[0] == (byte)InComingPacket.ReassignPlayer && packet[1] == (byte)EntityType.Player
            && _game.Me is { } me && U32(packet, 2) == me.Id)
        {
            _move = null;
        }

        if (packet.Length >= 6 && packet[0] == (byte)InComingPacket.ClearCursor && packet[1] == (byte)EntityType.Player && _game.Me is { } self && U32(packet, 2) == self.Id)
        {
            _lifted = null;
            Apply(ClientAction.ClearCursor, 0);
        }
    }

    private readonly HashSet<uint> _sold = [];
    private uint? _lifted;

    /// <summary>
    /// An item taken out of a container that this client did not ask to lift is gone, not on its cursor: used up by a
    /// right click, sold, handed to an NPC or an orifice for a quest, transmuted. The bot marks each of those cases
    /// where its own methods cause them (a use, a sale, the items it expects taken); a real client's trace shows every
    /// lift it asked for, so here any other removal is one. Applied before the server's packet, where the bot's
    /// mark would be.
    /// </summary>
    public void BeforeReceived(byte[] packet)
    {
        if (packet.Length >= 8 && packet[0] is 0x9C or 0x9D && packet[1] == (byte)Core.D2GS.Items.Action.RemoveFromContainer && U32(packet, 4) is var item && item != _lifted && !_sold.Contains(item))
        {
            Apply(ClientAction.Use, item);
        }
    }

    /// <summary>What the bot applies before it sends the same packet.</summary>
    public void BeforeSent(byte[] packet)
    {
        if (packet.Length == 0)
        {
            return;
        }

        switch ((OutGoingPacket)packet[0])
        {
            case OutGoingPacket.RemoveItemFromBuffer when packet.Length >= 5:
                _lifted = U32(packet, 1);
                Apply(ClientAction.Lift, _lifted.Value);
                break;
            case OutGoingPacket.SellItem when packet.Length >= 9:
                _sold.Add(U32(packet, 5));
                Apply(ClientAction.Sell, U32(packet, 5));
                break;
            case OutGoingPacket.EntityInteract when packet.Length >= 9 && IsWaypoint(U32(packet, 5), (EntityType)U32(packet, 1)):
                Apply(ClientAction.ForgetWaypointSelection, 0);
                break;
            case OutGoingPacket.EntityAction when packet.Length >= 5 && U32(packet, 1) == RefreshGamble:
                Apply(ClientAction.RemoveMerchantItems, 0);
                break;
        }
    }

    /// <summary>What the bot applies after it sent the same packet: the merchant's items once a chat ends, and a move's arrival, assumed once the move should have taken.</summary>
    public void AfterSent(byte[] packet, TimeSpan elapsed)
    {
        if (packet.Length == 0)
        {
            return;
        }

        switch ((OutGoingPacket)packet[0])
        {
            case OutGoingPacket.TerminateEntityChat:
                Apply(ClientAction.RemoveMerchantItems, 0);
                break;
            case OutGoingPacket.Run or OutGoingPacket.Walk when packet.Length >= 5:
                MoveTo(new Point(U16(packet, 1), U16(packet, 3)), elapsed, shortMove: false, walk: packet[0] == (byte)OutGoingPacket.Walk);
                break;
            case OutGoingPacket.UpdatePlayerLocation when packet.Length >= 5:
                MoveTo(new Point(U16(packet, 1), U16(packet, 3)), elapsed, shortMove: true, walk: false);
                break;
            case OutGoingPacket.RunToUnit or OutGoingPacket.WalkToUnit when packet.Length >= 9:
                if (UnitLocation(_game, (EntityType)U32(packet, 1), U32(packet, 5)) is { } location)
                {
                    MoveTo(location, elapsed, shortMove: false, walk: packet[0] == (byte)OutGoingPacket.WalkToUnit);
                }

                break;
        }
    }

    /// <summary>
    /// A real client sends the next run as it nears the last one's end, many a second, where the bot waits out
    /// each move; so a move still under way when the next is sent counts as arrived, and the timing applies to the last.
    /// </summary>
    private void MoveTo(Point target, TimeSpan elapsed, bool shortMove, bool walk)
    {
        if (!assumeMoves)
        {
            return;
        }

        if (_move is { } under)
        {
            _move = null;
            Apply(ClientAction.Move, under.Location);
        }

        var speed = Speed();
        var from = _game.Me?.Location;
        var perUnit = walk ? MoveMillisecondsPerUnit * RunVelocity / WalkVelocity : MoveMillisecondsPerUnit;
        var milliseconds = shortMove ? ShortMoveMilliseconds / speed : from == null ? 0 : from.Distance(target) * perUnit / speed;
        _move = (elapsed + TimeSpan.FromMilliseconds((int)milliseconds), ClientActions.Pack(target));
    }

    private double Speed()
    {
        try
        {
            var speed = _game.AssumedMovementSpeed;
            return speed > 0 ? speed : 1;
        }
        catch (Exception e) when (e is NullReferenceException or InvalidOperationException)
        {
            return 1;
        }
    }

    /// <summary>Where the client's game last put a unit; null when it does not know the unit.</summary>
    internal static Point UnitLocation(Game game, EntityType type, uint id)
    {
        try
        {
            return type switch
            {
                EntityType.Item => game.Items.TryGetValue(id, out var item) ? item.Location : null,
                EntityType.Player => game.Me?.Id == id ? game.Me.Location : game.Players.FirstOrDefault(p => p.Id == id)?.Location,
                _ => game.WorldObjects.TryGetValue((id, type), out var unit) ? unit.Location : null,
            };
        }
        catch (NullReferenceException)
        {
            return null;
        }
    }

    private bool IsWaypoint(uint id, EntityType type)
    {
        try
        {
            return _game.WorldObjects.TryGetValue((id, type), out var unit) && EntityConstants.WayPointEntityCodes.Contains(unit.Code);
        }
        catch (NullReferenceException)
        {
            return false;
        }
    }

    private void Apply(ClientAction action, uint argument)
    {
        if (_game.Me == null && action is ClientAction.ForgetWaypointSelection or ClientAction.Move)
        {
            return;
        }

        try
        {
            _game.Apply(action, argument);
        }
        catch (NullReferenceException)
        {
            return;
        }

        Derived[action] = Derived.GetValueOrDefault(action) + 1;
    }

    private static uint U32(byte[] payload, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset));

    private static ushort U16(byte[] payload, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(offset));
}
