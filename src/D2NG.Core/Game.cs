using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Items.Containers;
using D2NG.Core.D2GS.Motion;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;
using D2NG.Core.D2GS.Packet.Outgoing;
using D2NG.Core.D2GS.Players;
using D2NG.Core.D2GS.Quest;
using D2NG.Core.Extensions;
using D2NG.Core.MCP;
using D2NG.Core.Tracing;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace D2NG.Core;

public class Game
{
    private readonly GameServer _gameServer;

    private GameData Data { get; set; }

    private Thread pingThread;

    private volatile bool _leavingGame;

    private DateTime lastTeleport;

    public DateTime LastUsedHealthPotionTime { get; private set; } = DateTime.Now;

    public DateTime LastUsedManaPotionTime { get; private set; } = DateTime.Now;

    private Character selectedCharacter;

    private readonly SaleLog _saleLog = new();

    protected Func<Item, Task> ItemDroppedHandler { get; set; }

    protected Func<Item, Task> ItemOwnedHandler { get; set; }

    /// <param name="clock">Times monster motion; null is the system clock. A replay passes its record clock.</param>
    /// <param name="rules">How packets are interpreted; null is <see cref="ClientRules.Latest"/>, as the live bot runs. A replay passes the recording's.</param>
    internal Game(GameServer gameServer, TimeProvider clock = null, ClientRules rules = null)
    {
        _gameServer = gameServer;
        _monsterMotion = new MonsterMotionSettings { Clock = clock };
        Rules = rules ?? ClientRules.Latest;

        _gameServer.OnReceivedPacketEvent(InComingPacket.GameLoading, _ =>
        {
            if (IsInGame())
            {
                Log.Information($"{selectedCharacter?.Name}: Game loading..., About to drop");
                _gameServer.LogRecentPackets($"{selectedCharacter?.Name} got game loading mid-game");
                LeaveGame().Wait();
            }
        }
        );
        _gameServer.OnCoreReceivedPacketEvent(InComingPacket.GameFlags, p => Data = new GameData(new GameFlags(p), selectedCharacter, _monsterMotion, Rules));
        _gameServer.OnReceivedPacketEvent(InComingPacket.GameFlags, _ => StartPinging());
        _gameServer.OnCoreReceivedPacketEvent(InComingPacket.LoadAct, p => { Data.Act.LoadActData(new ActDataPacket(p)); Data.ActLoaded(); });
        // 0x61 plays an act's closing cinematic and leaves the character busy until the client says it
        // has finished; without the answer every later portal in the game was refused.
        _gameServer.OnReceivedPacketEvent(InComingPacket.Recv61, _ => _gameServer.SendPacket(new TurnOffBusyPacket()));
        _gameServer.OnCoreReceivedPacketEvent(InComingPacket.MapReveal, p => Data.MapReveal(new MapRevealPacket(p)));
        _gameServer.OnCoreReceivedPacketEvent(InComingPacket.AssignLevelWarp, p => Data.Act.AddWarp(new AssignLevelWarpPacket(p)));
#pragma warning disable CA1806 // Do not ignore method results
        _gameServer.OnCoreReceivedPacketEvent(InComingPacket.GameHandshake, p => new GameHandshakePacket(p));
#pragma warning restore CA1806 // Do not ignore method results
        _gameServer.OnCoreReceivedPacketEvent(InComingPacket.NPCHit, p => Data.Act.UpdateNPCOnHit(new NpcHitPacket(p)));
        _gameServer.OnCoreReceivedPacketEvent(InComingPacket.EntityMove, p => Data.EntityMove(new EntityMovePacket(p)));
        _gameServer.OnCoreReceivedPacketEvent(InComingPacket.AssignPlayer, p => Data.PlayerAssign(new AssignPlayerPacket(p)));
        _gameServer.OnCoreReceivedPacketEvent(InComingPacket.ReassignPlayer, p => { var packet = new ReassignPlayerPacket(p); Data.ConfirmPosition(packet.UnitType, packet.UnitId, packet.Location, InComingPacket.ReassignPlayer); Data.ReassignPlayer(packet.UnitType, packet.UnitId, packet.Location); if (packet.UnitType == EntityType.Player && packet.UnitId == Data.Me?.Id) { ServerConfirmed(packet.Location); } });
        _gameServer.OnCoreReceivedPacketEvent(InComingPacket.ReassignPlayer, p => { var packet = new ReassignPlayerPacket(p); if (packet.UnitType == EntityType.Player) { _playerReassigns.AddOrUpdate(packet.UnitId, 1, (_, count) => count + 1); } });
        _gameServer.OnCoreReceivedPacketEvent(InComingPacket.PartyAutomapInfo, p => { var packet = new PartyAutomapInfoPacket(p); Data.ReassignPlayer(EntityType.Player, packet.Id, packet.Location); });
        _gameServer.OnCoreReceivedPacketEvent(InComingPacket.AddExperienceByte, p => Data.AddExperience(new AddExpPacket(p)));
        _gameServer.OnCoreReceivedPacketEvent(InComingPacket.AddExperienceWord, p => Data.AddExperience(new AddExpPacket(p)));
        _gameServer.OnCoreReceivedPacketEvent(InComingPacket.AddExperienceDword, p => Data.AddExperience(new AddExpPacket(p)));
        _gameServer.OnCoreReceivedPacketEvent(InComingPacket.AddAttributeByte, p => Data.SetAttribute(new BaseAttributePacket(p)));
        _gameServer.OnCoreReceivedPacketEvent(InComingPacket.AddAttributeWord, p => Data.SetAttribute(new BaseAttributePacket(p)));
        _gameServer.OnCoreReceivedPacketEvent(InComingPacket.AddAttributeDword, p => Data.SetAttribute(new BaseAttributePacket(p)));
        _gameServer.OnCoreReceivedPacketEvent(InComingPacket.UpdateItemOSkill, p => Data.SetItemSkill(new SetItemSkillPacket(p)));
        _gameServer.OnCoreReceivedPacketEvent(InComingPacket.UpdateItemSkill, p => Data.SetItemSkill(new SetItemSkillPacket(p)));
        _gameServer.OnCoreReceivedPacketEvent(InComingPacket.SetSkill, p => Data.SetActiveSkill(new SetActiveSkillPacket(p)));
        _gameServer.OnCoreReceivedPacketEvent(InComingPacket.QuestInfo, p => Data.UpdateQuests(new QuestInfoPacket(p)));
        _gameServer.OnCoreReceivedPacketEvent(InComingPacket.GameQuestInfo, p => Data.UpdateQuests(new GameQuestInfoPacket(p)));
        _gameServer.OnCoreReceivedPacketEvent(InComingPacket.ObjectState, p => Data.Act.UpdateObjectState(new ObjectStatePacket(p)));
        _gameServer.OnCoreReceivedPacketEvent(InComingPacket.NPCState, p => Data.Act.UpdateNPCState(new NpcStatePacket(p)));
        _gameServer.OnCoreReceivedPacketEvent(InComingPacket.NPCMove, p => { var packet = new NPCMovePacket(p); Data.Act.UpdateNPCLocation(packet.EntityId, packet.Location); });
        _gameServer.OnCoreReceivedPacketEvent(InComingPacket.NPCStop, p => Data.Act.UpdateNPCStop(new NPCStopPacket(p)));
        _gameServer.OnCoreReceivedPacketEvent(InComingPacket.AssignNPC2, p => Data.Act.AddNPC(new AssignNpcPacket(p)));
        _gameServer.OnCoreReceivedPacketEvent(InComingPacket.AssignNPC1, p => Data.Act.AddNPC(new AssignNpcPacket(p)));
        _gameServer.OnCoreReceivedPacketEvent(InComingPacket.TownPortalState, p => Data.Act.UpdateTownPortal(new TownPortalStatePacket(p)));
        _gameServer.OnCoreReceivedPacketEvent(InComingPacket.PortalOwner, p => Data.Act.UpdateTownPortalOwner(new PortalOwnerPacket(p)));
        _gameServer.OnCoreReceivedPacketEvent(InComingPacket.AssignObject, p => Data.Act.AddWorldObject(new AssignObjectPacket(p).AsWorldObject()));
        _gameServer.OnCoreReceivedPacketEvent(InComingPacket.RemoveObject, p => Data.RemoveObject(new RemoveObjectPacket(p)));
        _gameServer.OnCoreReceivedPacketEvent(InComingPacket.PlayerInGame, p => Data.PlayerJoin(new PlayerInGamePacket(p)));
        _gameServer.OnCoreReceivedPacketEvent(InComingPacket.PlayerLeftGame, p => Data.PlayerLeave(new PlayerLeftGamePacket(p)));
        _gameServer.OnCoreReceivedPacketEvent(InComingPacket.BaseSkillLevels, p => Data.SetSkills(new BaseSkillLevelsPacket(p)));
        _gameServer.OnCoreReceivedPacketEvent(InComingPacket.LifeManaUpdatePot, p => Data.UpdateSelf(new LifeManaPotUpdatePacket(p)));
        _gameServer.OnCoreReceivedPacketEvent(InComingPacket.LifeManaUpdate, p => Data.UpdateSelf(new LifeManaUpdatePacket(p)));
        _gameServer.OnCoreReceivedPacketEvent(InComingPacket.WorldItemAction, p =>
            {
                var itemPacket = new ParseItemPacket(p);
                Data.ItemUpdate(itemPacket);
                _lastItem = itemPacket.Item;
            });
        _gameServer.OnReceivedPacketEvent(InComingPacket.WorldItemAction, _ => ItemDroppedHandler?.Invoke(_lastItem));
        _gameServer.OnCoreReceivedPacketEvent(InComingPacket.OwnedItemAction, p =>
            {
                var itemPacket = new ParseItemPacket(p);
                Data.ItemUpdate(itemPacket);
                _lastItem = itemPacket.Item;
            });
        _gameServer.OnReceivedPacketEvent(InComingPacket.OwnedItemAction, _ => ItemOwnedHandler?.Invoke(_lastItem));
        _gameServer.OnCoreReceivedPacketEvent(InComingPacket.UseStackableItem, p => Data.UseStackableItem(new UseStackableItemPacket(p)));
        _gameServer.OnCoreReceivedPacketEvent(InComingPacket.UpdateItemStats, p => Data.UpdateItemStats(new UpdateItemStatsPacket(p)));
        _gameServer.OnCoreReceivedPacketEvent(InComingPacket.WaypointMenu, p => Data.UpdateWaypointInfo(new WaypointMenuPacket(p)));
        _gameServer.OnCoreReceivedPacketEvent(InComingPacket.WalkVerify, p => { var packet = new WalkVerifyPacket(p); Data.ConfirmPosition(packet.Location, InComingPacket.WalkVerify); _lastWalkVerify = packet; });
        _gameServer.OnReceivedPacketEvent(InComingPacket.WalkVerify, _ => CheckAndPreventDesync(_lastWalkVerify));
        _gameServer.OnCoreReceivedPacketEvent(InComingPacket.AddEntityEffect, p => Data.AddEntityEffect(new AddEntityEffectPacket(p)));
        _gameServer.OnCoreReceivedPacketEvent(InComingPacket.AddEntityEffect2, p => Data.AddEntityEffect2(new AddEntityEffectPacket2(p)));
        _gameServer.OnCoreReceivedPacketEvent(InComingPacket.UpdateEntityEffects, p => Data.UpdateEntityEffects(new UpdateEntityEffectsPacket(p)));
        _gameServer.OnCoreReceivedPacketEvent(InComingPacket.CorpseAssign, p => Data.PlayerCorpseAssign(new CorpseAssignPacket(p)));
        _gameServer.OnCoreReceivedPacketEvent(InComingPacket.PlayerStop, p => Data.PlayerStop(new PlayerStopPacket(p)));
        _gameServer.OnCoreReceivedPacketEvent(InComingPacket.AssignMerc, p => Data.AssignMerc(new AssignMercPacket(p)));
        _gameServer.OnCoreReceivedPacketEvent(InComingPacket.AllyPartyInfo, p => Data.UpdatePlayerPartyInfo(new AllyPartyInfoPacket(p)));
        _gameServer.OnCoreReceivedPacketEvent(InComingPacket.AssignPlayerToParty, p => Data.AssignPlayerToParty(new AssignPlayerToPartyPacket(p)));
        _gameServer.OnCoreReceivedPacketEvent(InComingPacket.NPCInfo, p => Data.UpdateNpcMessages(new NpcInfoPacket(p)));
        _gameServer.OnCoreReceivedPacketEvent(InComingPacket.PetAction, p => Data.UpdateSummonInfo(new PetActionPacket(p)));
        _gameServer.OnReceivedPacketEvent(InComingPacket.NPCTransaction, p =>
            _saleLog.Answered(new NpcTransactionPacket(p), Data.Flags.Difficulty, Me?.Attributes.GetValueOrDefault(D2GS.Players.Attribute.Level, 0) ?? 0));
        _gameServer.OnReceivedPacketEvent(InComingPacket.NPCAttack, NoteAttackOnMe);
        foreach (var cast in new[] { OutGoingPacket.ShiftLeftClick, OutGoingPacket.LeftClickUnit, OutGoingPacket.ShiftLeftClickUnit, OutGoingPacket.ShiftLeftClickHold, OutGoingPacket.LeftClickHoldUnit, OutGoingPacket.ShiftLeftClickHoldUnit })
        {
            _gameServer.OnSentPacketEvent(cast, _ => NoteSpellSent(Hand.Left));
        }

        foreach (var cast in new[] { OutGoingPacket.RightSkillOnLocation, OutGoingPacket.RightSkillOnUnit, OutGoingPacket.ShiftRightClickUnit, OutGoingPacket.RightSkillRepeatOnLocation, OutGoingPacket.RightClickHoldUnit, OutGoingPacket.ShiftRightClickHoldUnit })
        {
            _gameServer.OnSentPacketEvent(cast, _ => NoteSpellSent(Hand.Right));
        }
        RegisterMonsterMotion();
    }

    private void NoteAttackOnMe(D2gsPacket packet)
    {
        if (packet.Raw.Length < NPCAttackPacket.Length || Data?.Me is not { } me)
        {
            return;
        }

        var attack = new NPCAttackPacket(packet);
        if (attack.TargetType == EntityType.Player && attack.TargetId == me.Id)
        {
            Interlocked.Exchange(ref _attackedAt, Clock.GetTimestamp());
        }
    }

    /// <summary>When a monster last aimed an attack at this character, on <see cref="Clock"/>; 0 before any.</summary>
    private long _attackedAt;

    /// <summary>A monster aimed an attack at this character within <see cref="UnderAttackWindow"/>.</summary>
    public bool UnderAttack
    {
        get
        {
            var at = Interlocked.Read(ref _attackedAt);
            return at != 0 && Clock.GetElapsedTime(at) < UnderAttackWindow;
        }
    }

    private static readonly TimeSpan UnderAttackWindow = TimeSpan.FromSeconds(1);

    private readonly MonsterMotionSettings _monsterMotion;

    /// <summary>The client rules this game interprets packets and client actions under.</summary>
    public ClientRules Rules { get; }

    /// <summary>What a monster's Location returns. Every mode is tracked all the time, so this can change mid-game.</summary>
    public MonsterMotion MonsterMotion
    {
        get => _monsterMotion.Mode;
        set => _monsterMotion.Mode = value;
    }

    /// <summary><see cref="MonsterMotion"/> as applied: Simulated reads Legacy while there is no speed data.</summary>
    public MonsterMotion EffectiveMonsterMotion => _monsterMotion.EffectiveMode;

    /// <summary>The clock monster motion is timed by: the system's monotonic clock live, record time in a replay.</summary>
    public TimeProvider Clock => _monsterMotion.Clock;

    internal MonsterMotionSettings MonsterMotionSettings => _monsterMotion;

    /// <summary>Registered after the legacy position handlers, which stay as they were.</summary>
    private void RegisterMonsterMotion()
    {
        _monsterMotion.LoadSpeeds();
        foreach (var type in new[] { InComingPacket.NPCMove, InComingPacket.NPCMoveToTarget, InComingPacket.NPCAction, InComingPacket.NPCAttack, InComingPacket.NPCStop, InComingPacket.NPCState, InComingPacket.ReassignPlayer })
        {
            _gameServer.OnCoreReceivedPacketEvent(type, ApplyMonsterMotion);
        }
    }

    private void ApplyMonsterMotion(D2gsPacket packet)
    {
        try
        {
            Data?.Act.ApplyMotion(MotionUpdate.Read(packet.Raw));
        }
        catch (Exception e)
        {
            MotionFailures.Report(e);
        }
    }

    public void OnWorldItemEvent(Func<Item, Task> handler)
        => ItemDroppedHandler = handler;
    public void OnOwnedItemEvent(Func<Item, Task> handler)
        => ItemOwnedHandler = handler;
    /// <summary>The item the last 0x9C or 0x9D carried, for the bot's item handlers that run after the core ones.</summary>
    private Item _lastItem;

    private WalkVerifyPacket _lastWalkVerify;

    private void StartPinging()
    {
        _gameServer.Ping();
        _leavingGame = false;
        pingThread = new Thread(PingThread) { Name = "GameClient Ping Thread", IsBackground = true };
        pingThread.Start();
    }

    public void SelectCharacter(Character character)
    {
        selectedCharacter = character;
    }

    public bool IsInGame()
    {
        return _gameServer.IsInGame();
    }

    public async Task LeaveGame()
    {
        if(!IsInGame())
        {
            return;
        }

        Log.Debug("Leaving game");
        try
        {
            await _gameServer.LeaveGame();
        }
        finally
        {
            // The ping loop sleeps five seconds between pings, and joining it cost a mean 2.5s on
            // every teardown. Waking it is free.
            _leavingGame = true;
            pingThread.Join();
        }
    }

    /// <summary>
    /// Opens the waypoint menu, which also activates a waypoint the character did not have yet, and closes
    /// it again without travelling. Null when the menu never opened.
    /// </summary>
    public IReadOnlyCollection<Waypoint> ReadWaypoints(WorldObject worldObject)
    {
        var waypointMenuEvent = _gameServer.GetResetEventOfType(InComingPacket.WaypointMenu);
        _gameServer.SendPacket(new InteractWithEntityPacket(worldObject));
        if (!waypointMenuEvent.WaitOne(2000))
        {
            return null;
        }

        _gameServer.SendPacket(new TakeWaypointPacket(worldObject.Id, 0u));
        return [.. Data.Me.AllowedWaypoints];
    }

    public bool TakeWaypoint(WorldObject worldObject, Waypoint waypoint)
    {
        if (!EntityConstants.WayPointEntityCodes.Contains(worldObject.Code))
        {
            throw new InvalidOperationException($"cannot take waypoint using worldObject which does belong to one of the waypoint entity codes, but has code { worldObject.Code} ");
        }

        var waypointMenuEvent = _gameServer.GetResetEventOfType(InComingPacket.WaypointMenu);
        Apply(ClientAction.ForgetWaypointSelection);
        _gameServer.SendPacket(new InteractWithEntityPacket(worldObject));
        if(!waypointMenuEvent.WaitOne(2000))
        {
            return false;
        }

        // An open waypoint menu makes the server treat the character as busy, and a busy character is
        // refused by every portal and waypoint for the rest of the game. Level 0 closes it without
        // travelling (D2MOO PlrMsg.cpp, Rcv0x49_TakeOrCloseWp).
        if (!Data.Me.AllowedWaypoints.Contains(waypoint))
        {
            _gameServer.SendPacket(new TakeWaypointPacket(worldObject.Id, 0u));
            throw new InvalidOperationException($"cannot take waypoint {waypoint}, since character does not have it yet. List of available waypoints: {string.Join(", ", Data.Me.AllowedWaypoints)}");
        }

        if (Data.Me.LastSelectedWaypointId == 0)
        {
            _gameServer.SendPacket(new TakeWaypointPacket(worldObject.Id, 0u));
            return false;
        }

        _gameServer.SendPacket(new TakeWaypointPacket(Data.Me.LastSelectedWaypointId, waypoint));
        return true;
    }
    public Self Me { get => Data?.Me; }

    public List<Player> Players { get => Data.Players.ToList(); }

    public IReadOnlyDictionary<uint, Item> Items { get => Data.Items; }

    public Act Act { get => Data.Act.Act; }

    public Character ClientCharacter { get => Data.ClientCharacter; }

    /// <summary>
    /// Area this character is standing in, resolved from the revealed tile it occupies. Falls back to the
    /// last area a reveal packet named, which is what this used to report always - and which lies while
    /// a neighbouring level is coming into view.
    /// </summary>
    public Area Area
    {
        get
        {
            var byPosition = Data.Me != null ? Data.Act.AreaAtPosition(Data.Me.Location) : Area.None;
            return byPosition != Area.None ? byPosition : Data.Act.Area;
        }
    }

    /// <summary>
    /// The area of the revealed tile holding the character's position; right after an act load, until a tile
    /// holds a position set after the load or the server states one, the town the act loaded into
    /// (<see cref="ResolvedAreaIsActLoad"/>). Null when neither applies: then the client does not know where it
    /// is, where <see cref="Area"/> answers the act's town.
    /// </summary>
    public Area? ResolvedArea => Resolution?.Area;

    /// <summary>
    /// <see cref="ResolvedArea"/> is the act load's town, not a tile's. A load names the act's town wherever the
    /// character arrives - a waypoint into Travincal loads Kurast Docks - so this says nothing about being in town.
    /// </summary>
    public bool ResolvedAreaIsActLoad => Resolution?.Held == true;

    /// <summary>The last area <see cref="ResolvedArea"/> named; null before the first.</summary>
    public Area? LastResolvedArea => Resolution?.LastKnown;

    public AreaState AreaState => ResolvedArea == null ? AreaState.Unknown : AreaState.Resolved;

    /// <summary>Zero while <see cref="ResolvedArea"/> is known; how long it has been unknown otherwise.</summary>
    public TimeSpan AreaUnknownFor
    {
        get
        {
            if (Resolution is not { } current)
            {
                return TimeSpan.Zero;
            }

            return current.Area != null ? TimeSpan.Zero : Data.Act.Resolver.Clock.GetElapsedTime(current.ChangedAt);
        }
    }

    /// <summary>
    /// The revealed tile's area holding <see cref="ServerConfirmedLocation"/>; null when the server has stated no
    /// position since the last act load or no tile revealed to this client holds it. See <see cref="ServerConfirmedAge"/>.
    /// </summary>
    public Area? ServerConfirmedArea => Resolution?.ServerArea;

    /// <summary>
    /// <see cref="ResolvedArea"/> when it resolves, from a tile (<see cref="AreaSource.Believed"/>) or the act load's
    /// hold (<see cref="AreaSource.ActLoad"/>); otherwise <see cref="ServerConfirmedArea"/>, with
    /// <see cref="AreaSource.ServerConfirmed"/> saying the client's own position is outside its revealed map.
    /// </summary>
    public (Area? Area, AreaSource Source) KnownArea
    {
        get
        {
            var current = Resolution;
            return current?.Area is { } believed ? (believed, current.Held ? AreaSource.ActLoad : AreaSource.Believed)
                : current?.ServerArea is { } server ? (server, AreaSource.ServerConfirmed)
                : (null, AreaSource.Unknown);
        }
    }

    /// <summary>
    /// The believed and the server-confirmed position both resolve, one in a town and the other not: the client
    /// thinks it crossed and the server does not, or the other way round. Neither town helper answers true then.
    /// </summary>
    public bool ServerDisagreesOnTown
        => Resolution is { Area: { } believed, ServerArea: { } server } && believed.IsTown() != server.IsTown();

    /// <summary>
    /// The server's last position since the act load is in a town (<see cref="ServerConfirmedArea"/>), and the
    /// believed position is not in a field tile. False without a server position: the client's word alone,
    /// the act load's hold included, never counts.
    /// </summary>
    public bool IsInTownConfirmed() => TownByServer(Resolution, town: true);

    /// <summary>
    /// The server's last position since the act load is outside the towns, and the believed position is not in a
    /// town tile. False without a server position. Neither helper true means unknown, not the other answer.
    /// </summary>
    public bool IsOutOfTownConfirmed() => TownByServer(Resolution, town: false);

    private static bool TownByServer(AreaResolution current, bool town)
        => current?.ServerArea is { } server && server.IsTown() == town
            && (current.Area is not { } believed || current.Held || believed.IsTown() == town);

    /// <summary>
    /// Where the server last said this character is (0x15, 0x96, 0x95, 0x18, 0x0D, 0x0F) since the act load; null
    /// before the first. <see cref="Self"/>'s location is where the client believes it is, which moves on every
    /// step it sends without the server's say. This, <see cref="ServerConfirmedAge"/>, <see cref="ServerConfirmedArea"/>
    /// and <see cref="ServerHoldsPosition"/> come from one snapshot and reset together at an act load.
    /// </summary>
    public Point ServerConfirmedLocation => PositionConfirmation?.Location;

    /// <summary>How long ago <see cref="ServerConfirmedLocation"/> arrived; MaxValue before the first.</summary>
    public TimeSpan ServerConfirmedAge
        => PositionConfirmation is { } confirmation ? Clock.GetElapsedTime(confirmation.Timestamp) : TimeSpan.MaxValue;

    /// <summary>Subtiles between where the client believes it is and <see cref="ServerConfirmedLocation"/>; null when either is missing.</summary>
    public double? ServerConfirmedDistance
        => PositionConfirmation?.Location is { } confirmed && Me?.Location is { } believed ? believed.Distance(confirmed) : null;

    /// <summary>
    /// Set while the client believes itself more than <see cref="AreaResolver.HoldMoving"/> from a point where, over the
    /// last <see cref="AreaResolver.HoldSpan"/>, the server stated the own position at least
    /// <see cref="AreaResolver.HoldStatements"/> times within a few subtiles, each time finding the client that far away
    /// and once at least <see cref="AreaResolver.HoldBelievedDistance"/>, in a run of such statements that has lasted the
    /// span: the server is refusing the client's movement, and every step the client takes is on its own word. Set for
    /// the whole episode, the same <see cref="ServerHeldPosition.Since"/> throughout, including the moments a snap puts
    /// the client back on the held point (<see cref="ServerHeldPosition.ClientIsOnHeldPoint"/>). Read against the clock:
    /// it clears <see cref="AreaResolver.HoldSpan"/> after the last statement that found the client far away, and at once
    /// on a statement elsewhere, an act load or leaving the game. Null otherwise.
    /// </summary>
    public ServerHeldPosition ServerHoldsPosition
        => Resolution?.ServerHold is { } hold && hold.HoldsAt(Clock.GetTimestamp()) ? hold : null;

    /// <summary>How long ago the episode of <see cref="ServerHoldsPosition"/> began (its first statement); zero when it is not holding.</summary>
    public TimeSpan ServerHeldFor => ServerHoldsPosition is { } hold ? Clock.GetElapsedTime(hold.Since) : TimeSpan.Zero;

    internal PositionConfirmation PositionConfirmation => Resolution?.Confirmation;

    /// <summary>The resolver's answer while in a game; null once the client has left it, though <see cref="Data"/> stays until the next game.</summary>
    private AreaResolution Resolution => _gameServer.LeftGame ? null : Data?.Act.Resolver.Current;

    public Difficulty Difficulty { get => Data.Flags.Difficulty; }

    /// <summary>
    /// Quest progress of this character and of the game, kept up to date from the 0x9C and 0x9D
    /// pushes. Call <see cref="RequestQuestData"/> to force a refresh.
    /// </summary>
    public QuestState Quests { get => Data.Quests; }

    public uint MapId { get => Data.Act.MapId; }

    public IReadOnlyDictionary<(uint, EntityType), WorldObject> WorldObjects => Data.Act.WorldObjects;

    public IReadOnlyDictionary<uint, List<WarpData>> Warps => Data.Act.Warps;

    public Container Stash { get => Data.Stash; }

    public Container Inventory { get => Data.Inventory; }

    public Container Cube { get => Data.Cube; }

    public Belt Belt { get => Data.Belt; }

    public Item CursorItem { get => Data.CursorItem; }

    public List<WorldObject> GetEntityByCode(EntityCode entityCode)
    {
        if (Data.Act.WorldObjectsByEntityCode.TryGetValue(entityCode, out var entities))
        {
            return entities.ToList();
        }

        return [];
    }

    public List<WorldObject> GetNPCsByCode(NPCCode npcCode)
    {
        return WorldObjects.Values.Where(o => o.NPCCode == npcCode).ToList();
    }

    public void LogPacketsSince(string reason, DateTime since) => _gameServer.LogPacketsSince(reason, since);

    public WarpData GetNearestWarp()
    {
        return Warps.Values.SelectMany(l => l).OrderBy(w => w.Location.Distance(Me.Location)).ThenByDescending(w => w.EntityId).FirstOrDefault();
    }

    public void RemoveItemFromContainer(Item item)
    {
        Apply(ClientAction.Lift, item.Id);
        _gameServer.SendPacket(new RemoveItemFromContainerPacket(item));
    }

    /// <summary>Drops the item on the cursor into the cube without opening it.</summary>
    public void PutCursorItemInCube(Item item, Item cube)
    {
        _gameServer.SendPacket(new ItemToCubePacket(item, cube));
    }

    /// <summary>Puts the item on the cursor into a free socket of <paramref name="target"/>.</summary>
    public void SocketCursorItem(Item item, Item target)
    {
        _gameServer.SendPacket(new SocketItemPacket(item, target));
    }

    public void RemoveItemFromBelt(Item item)
    {
        _gameServer.SendPacket(new RemoveItemFromBeltPacket(item));
    }

    public bool ActivateCube(Item item)
    {
        if (item.Name != ItemName.HoradricCube)
        {
            throw new InvalidOperationException($"incorrect item type '{item.Name}', expected HoradricCube");
        }
        var buttonAction = _gameServer.GetResetEventOfType(InComingPacket.ButtonAction);
        _gameServer.SendPacket(new ActivateBufferItemPacket(Me, item));
        return WaitForPacket(buttonAction, 2000, "ClickButton");
    }

    public bool ActivateTomeOfIdentify(Item item)
    {
        if (item.Name != ItemName.TomeofIdentify)
        {
            throw new InvalidOperationException($"incorrect item type '{item.Name}', expected TomeofIdentify");
        }
        var stackableItem = _gameServer.GetResetEventOfType(InComingPacket.UseStackableItem);
        _gameServer.SendPacket(new ActivateBufferItemPacket(Me, item));
        return WaitForPacket(stackableItem, 2000, "UseStackableItem");
    }

    /// <summary>
    /// Right-clicks an inventory item - a quest reward that only counts once used, such as the potion of
    /// life, a book of skill or a scroll of resistance.
    /// </summary>
    public void ActivateInventoryItem(Item item)
    {
        Apply(ClientAction.Use, item.Id);
        _gameServer.SendPacket(new ActivateBufferItemPacket(Me, item));
    }

    /// <summary>
    /// Quest items an NPC may take when a quest message is said: the server deletes them with the same removal
    /// a lift gets and nothing after it, so without this the client believes the item is on its cursor - and a
    /// character that handed Charsi the malus could pick nothing up afterwards.
    /// </summary>
    public void ExpectItemsTaken(IEnumerable<Item> items)
    {
        foreach (var item in items)
        {
            Apply(ClientAction.Use, item.Id);
        }
    }

    internal void Apply(ClientAction action, uint argument = 0) => _gameServer.ApplyClientAction(Data, action, argument);

    internal (string Client, string Game) TraceIdentity => (_gameServer.TraceIdentity.Client ?? Me?.Name, _gameServer.TraceIdentity.Game);

    private void AssumeAt(Point location)
    {
        Apply(ClientAction.Move, ClientActions.Pack(location));
        // Walking only assumes the step succeeded. A character the server stopped - blocked at a corner - got no more walk
        // verifies, and the client walked it on for two minutes on its own: the orb sorceress "followed" the leader 110 yards
        // away and stood there idle, seeing none of the monsters he fought. Moved well away from the last position the
        // server confirmed and no confirmation for a while: ask for the real one.
        if (_serverConfirmedAt != DateTime.MinValue && DateTime.Now - _serverConfirmedAt > PositionConfirmWait
            && _serverConfirmed != null && location.Distance(_serverConfirmed) > PositionDrift
            && DateTime.Now - _positionRequestedAt > PositionConfirmWait)
        {
            _positionRequestedAt = DateTime.Now;
            _gameServer.SendPacket(new RequestUpdatePacket(Data.Me.Id));
        }
    }

    // Long and wide on purpose: the server's answer interrupts a walk in progress, and at 3 s and 8 yards the party took 38 s
    // to reach the Pandemonium Fortress gate and stopped 30 short of it.
    private static readonly TimeSpan PositionConfirmWait = TimeSpan.FromSeconds(10);
    private const double PositionDrift = 20;
    private Point _serverConfirmed;
    private DateTime _serverConfirmedAt = DateTime.MinValue;
    private DateTime _positionRequestedAt = DateTime.MinValue;

    private void ServerConfirmed(Point location)
    {
        _serverConfirmed = location;
        _serverConfirmedAt = DateTime.Now;
    }

    public bool UsePotion(Item item)
    {
        if (item.Classification != ClassificationType.HealthPotion && item.Classification != ClassificationType.ManaPotion && item.Classification != ClassificationType.RejuvenationPotion)
        {
            throw new InvalidOperationException($"incorrect item type '{item.Name}', expected Potion");
        }
        var useStackableItem = _gameServer.GetResetEventOfType(InComingPacket.UseStackableItem);
        _gameServer.SendPacket(new ActivateBufferItemPacket(Me, item));
        return useStackableItem.WaitOne(50);
    }

    /// <summary>
    /// Held from selecting the right hand skill until its cast is sent. Casting threads and the
    /// teleporting loop used to switch the skill under each other, and a cast then went out as
    /// whichever skill the other thread had just selected.
    /// </summary>
    private readonly Lock _rightHand = new();

    public bool UseRightHandSkillOnLocation(Skill skill, Point location)
    {
        lock (_rightHand)
        {
            if (!ChangeSkill(skill, Hand.Right))
            {
                return false;
            }

            _gameServer.SendPacket(new RightSkillOnLocationPacket(location));
            return true;
        }
    }

    public bool LeftHandSkillHoldOnEntity(Skill skill, Entity entity)
    {
        if (!ChangeSkill(skill, Hand.Left))
        {
            return false;
        }

        _gameServer.SendPacket(new LeftSkillHoldOnUnitPacket(entity));
        return true;
    }

    public bool TeleportToLocation(Point point)
    {
        if (Me.Location.Distance(point) < 10)
        {
            return true;
        }

        var reAssignPlayer = _gameServer.GetResetEventOfType(InComingPacket.ReassignPlayer);
        UseRightHandSkillOnLocation(Skill.Teleport, point);
        return reAssignPlayer.WaitOne(200) && Me.Location.Distance(point) < 10;
    }

    /// <param name="minDistance">Closer than this the character is taken to be there already.</param>
    public async Task<bool> TeleportToLocationAsync(Point point, double minDistance = 10)
    {
        // After a chicken nothing answers: the taxi's escape went on waiting out every hop, and the
        // party fought on for eighteen seconds of a game she had already left.
        if (!IsInGame())
        {
            return false;
        }

        if (Me.Location.Distance(point) < minDistance)
        {
            return true;
        }

        // The server drops a cast that arrives while the character is still animating and carries out the first one after,
        // the move coming ~220ms later. A cast is sent again every 60ms in case the server was still busy, but a cast for
        // this point that reaches the server after the move is carried out too, as a teleport onto the spot just reached,
        // and the next hop waits out its whole animation. Recast until the move, one hop in nine paid that, about 4s a game
        // for the cow portal sorceress (Hell cows, 18 Nov 2026: 72 of her 657 hops in five games). So a hop only recasts
        // while no move can be due yet: the last recast leaves by 140ms, before the earliest move seen (216ms).
        // A hop just after another spell, which may still be animating for any time, or under attack, where every hit
        // can hold the character in its recovery, recasts until the move as before: an escape is only cast between two
        // hits by trying all the time, and four in a row cast into a pack's hits did not move her once. Decided once, at
        // the start: switching to it mid-hop would recast right across the move.
        // Waited on per player: the packet's event wakes on any player's reassign, a party member's teleport included.
        var selected = ChangeSkill(Skill.Teleport, Hand.Right);
        var continuous = !selected || UnderAttack || SpellMayBeAnimating;
        var me = Me.Id;
        var from = Me.Location;
        var before = ReassignsOf(me);
        var reassigned = _gameServer.GetResetEventOfType(InComingPacket.ReassignPlayer);
        var sent = System.Diagnostics.Stopwatch.StartNew();
        var lastCast = 0L;
        var casts = 0;
        while (IsInGame())
        {
            // Read after the event is armed, and the count is raised before the event is set: none is missed.
            var reassigns = ReassignsOf(me);
            if (reassigns != before)
            {
                // The answer to an update request leaves the character where it stood: not this hop's move.
                if (Me.Location.Distance(from) < 1)
                {
                    before = reassigns;
                    continue;
                }

                return Me.Location.Distance(point) < Math.Max(minDistance, 5);
            }

            // Waited from the last cast sent either way: one taken just before the end still moves after it.
            var elapsed = sent.ElapsedMilliseconds;
            var deadline = continuous ? Math.Max(TeleportReplyTimeoutMs, lastCast + TeleportMoveWithinMs) : lastCast + TeleportMoveWithinMs;
            if (casts > 0 && elapsed >= deadline)
            {
                return false;
            }

            var casting = continuous
                ? elapsed < TeleportReplyTimeoutMs
                : casts < LimitedHopCasts && elapsed <= LimitedCastWindowMs;
            if (casting && elapsed >= casts * TeleportRecastIntervalMs)
            {
                if (UseRightHandSkillOnLocation(Skill.Teleport, point))
                {
                    lastCast = sent.ElapsedMilliseconds;
                }

                casts++;
                continue;
            }

            var until = casting ? Math.Min(casts * TeleportRecastIntervalMs, deadline) : deadline;
            if (await reassigned.AsTask(TimeSpan.FromMilliseconds(Math.Max(1, until - elapsed))))
            {
                reassigned = _gameServer.GetResetEventOfType(InComingPacket.ReassignPlayer);
            }
        }

        return false;
    }

    private const int TeleportRecastIntervalMs = 60;

    /// <summary>Casts of a hop that recasts only while no move can be due: at 0, 60 and 120ms.</summary>
    private const int LimitedHopCasts = 3;

    /// <summary>A late thread does not send one of those casts after this: the earliest move seen came 216ms after a cast.</summary>
    private const int LimitedCastWindowMs = 140;

    /// <summary>How long after its last cast a hop waits for the move: ~220ms after the cast taken, with room.</summary>
    private const int TeleportMoveWithinMs = 400;

    /// <summary>A hop that recasts until the move stops recasting after this long, as every hop did before.</summary>
    private const int TeleportReplyTimeoutMs = 600;

    /// <summary>
    /// A cast of another skill was sent recently enough that its animation may still hold the server: 13 frames, the
    /// slowest cast rate, are 520ms.
    /// </summary>
    private bool SpellMayBeAnimating
    {
        get
        {
            var at = Interlocked.Read(ref _spellSentAt);
            return at != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(at) < SpellAnimationMax;
        }
    }

    private static readonly TimeSpan SpellAnimationMax = TimeSpan.FromMilliseconds(600);

    /// <summary>When a cast of a skill other than Teleport was last sent, as <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/>.</summary>
    private long _spellSentAt;

    private void NoteSpellSent(Hand hand)
    {
        if (Data?.Me is { } me && (!me.ActiveSkills.TryGetValue(hand, out var skill) || skill != Skill.Teleport))
        {
            Interlocked.Exchange(ref _spellSentAt, System.Diagnostics.Stopwatch.GetTimestamp());
        }
    }

    public bool ChangeSkill(Skill skill, Hand hand)
    {
        if (!Me.HasSkill(skill))
        {
            throw new InvalidOperationException($"cannot use {skill}, this character does not have the skill");
        }

        if (!Me.ActiveSkills.TryGetValue(hand, out var value) || value != skill)
        {
            _gameServer.SendPacket(new SelectSkillPacket(hand, skill));
            var count = 0;
            while ((!Me.ActiveSkills.TryGetValue(hand, out var newSkill) || newSkill != skill) && count < 10)
            {
                Thread.Sleep(10);
                count++;
            }

            if (!Me.ActiveSkills.TryGetValue(hand, out var check) || check != skill)
            {
                _unconfirmedSelects++;
                if (_unconfirmedSelects % UnconfirmedSelectReport == 0)
                {
                    Log.Warning("{Character}: {Skill} on the {Hand} hand not confirmed {Count} times in a row, the client still has {Current}",
                        Me.Name, skill, hand, _unconfirmedSelects, Me.ActiveSkills.TryGetValue(hand, out var current) ? current : null);
                }

                return false;
            }
        }

        _unconfirmedSelects = 0;
        return true;
    }

    private const int UnconfirmedSelectReport = 10;

    private int _unconfirmedSelects;

    public bool ShiftHoldLeftHandSkillOnLocation(Skill skill, Point location)
    {
        if (!ChangeSkill(skill, Hand.Left))
        {
            return false;
        }

        _gameServer.SendPacket(new ShiftLeftSkillHoldOnLocationPacket(location));
        return true;
    }

    public bool RepeatRightHandSkillOnLocation(Skill skill, Point location)
    {
        if (!ChangeSkill(skill, Hand.Right))
        {
            return false;
        }

        _gameServer.SendPacket(new RightSkillRepeatOnLocationPacket(location));
        return true;
    }

    public bool RepeatRightHandSkillOnEntity(Skill skill, Entity entity)
    {
        if (!ChangeSkill(skill, Hand.Right))
        {
            return false;
        }

        _gameServer.SendPacket(new RightSkillRepeatOnUnitPacket(entity));
        return true;
    }

    public bool UseRightHandSkillOnEntity(Skill skill, Entity entity)
    {
        if (!ChangeSkill(skill, Hand.Right))
        {
            return false;
        }

        _gameServer.SendPacket(new RightSkillOnUnitPacket(entity));
        return true;
    }

    public bool UseLeftHandSkillOnEntity(Skill skill, Entity entity)
    {
        if (!ChangeSkill(skill, Hand.Left))
        {
            return false;
        }

        _gameServer.SendPacket(new RightSkillOnUnitPacket(entity));
        return true;
    }

    public bool UseFindItem(Entity entity)
    {
        if (!ChangeSkill(Skill.FindItem, Hand.Right))
        {
            return false;
        }
        _gameServer.SendPacket(new RightSkillOnUnitPacket(entity));
        return true;
    }

    public async Task<bool> CreateTownPortal()
    {
        if (!IsInGame())
        {
            return false;
        }

        var existingTownPortalIds = GetEntityByCode(EntityCode.TownPortal).Where(t => t.TownPortalOwnerId == Me.Id).Select(t => t.Id).ToHashSet();
        var portalOwner = _gameServer.GetResetEventOfType(InComingPacket.PortalOwner);
        var cts = new CancellationTokenSource();
        cts.CancelAfter(500);
        UseRightHandSkillOnLocation(Skill.BookOfTownportal, Me.Location);
        do
        {
            if (await portalOwner.AsTask(TimeSpan.FromMilliseconds(500)))
            {
                var townPortal = OwnTownPortal();
                return townPortal != null && !existingTownPortalIds.Contains(townPortal.Id);
            };
            portalOwner = _gameServer.GetResetEventOfType(InComingPacket.PortalOwner);
        } while (!cts.IsCancellationRequested);
        
        var townPortalNearby = OwnTownPortal();
        return townPortalNearby != null && !existingTownPortalIds.Contains(townPortalNearby.Id);
    }

    /// <summary>
    /// The newest of this character's portals. A recast after a late reply leaves the replaced portal in the
    /// records, and interacting with that one is ignored by the server.
    /// </summary>
    public WorldObject OwnTownPortal() =>
        GetEntityByCode(EntityCode.TownPortal).Where(t => t.TownPortalOwnerId == Me.Id).MaxBy(t => t.Id);

    public void InsertItemIntoContainer(Item item, Point location, ItemContainer container)
    {
        if (container == ItemContainer.Stash && location.Y > 7)
        {
            _gameServer.SendPacket(new InsertItemIntoContainerPacket(item, new Point(location.X, (ushort)(location.Y - 8)), ItemContainer.Stash2));
        }
        _gameServer.SendPacket(new InsertItemIntoContainerPacket(item, location, container));
    }

    /// <summary>
    /// Puts the cursor item down on top of <paramref name="target"/>, which the server then hands to
    /// the cursor: how a full container trades one item for another.
    /// </summary>
    public void SwapCursorItemWith(Item held, Item target)
    {
        _gameServer.SendPacket(new SwapCursorAndBufferItemPacket(held, target));
    }

    public void ClickButton(ClickType clickType)
    {
        _gameServer.SendPacket(new ClickButtonPacket(clickType, 0));
    }

    public void MoveGoldToStash(int goldAmount)
    {
        _gameServer.SendPacket(new ClickButtonPacket(ClickType.MoveGoldFromInventoryToStash, goldAmount));
    }

    /// <summary>
    /// Multiplies the speed the movement sleeps assume, on top of the character's own faster run and
    /// walk. Above 1 the bot waits less between hops than it believes the character needs; since
    /// MoveTo sets Me.Location optimistically, too high a value makes the bot think it has arrived
    /// while the character is still on its way.
    /// </summary>
    public double MovementSpeedFactor { get; set; } = 1.0;

    private double MovementSpeed => Data.WalkingSpeedMultiplier * MovementSpeedFactor;

    /// <summary>The speed the move methods time their assumed arrival with, for a replay that derives those moves.</summary>
    internal double AssumedMovementSpeed => MovementSpeed;


    /// <summary>
    /// Waits for a packet of this type, reporting when it never arrives. The reset events are keyed
    /// by packet type rather than by entity, so a wait can also return for something else entirely -
    /// where the caller can check the state it actually wanted, it should.
    /// </summary>
    private bool WaitForPacket(System.Threading.ManualResetEvent resetEvent, int milliseconds, string what)
    {
        if (resetEvent.WaitOne(milliseconds))
        {
            return true;
        }

        Log.Information("{Character}: {What} waited {Ms}ms and its packet never came", Me?.Name, what, milliseconds);
        return false;
    }

    private static readonly TimeSpan RunResendInterval = TimeSpan.FromMilliseconds(150);

    private Point _lastRunTarget;

    private DateTime _lastRunAt = DateTime.MinValue;

    private TimeSpan RunResendWait(Point location) =>
        location == _lastRunTarget ? RunResendInterval - (DateTime.Now - _lastRunAt) : TimeSpan.Zero;

    private void SendRun(Point location)
    {
        _gameServer.SendPacket(new RunToLocationPacket(location));
        _lastRunTarget = location;
        _lastRunAt = DateTime.Now;
    }

    /// <summary>
    /// A run whatever the distance, never the short-hop location update <see cref="MoveToAsync(Point)"/> sends under ten
    /// yards. Only a run counts as moving: a character just through a waypoint takes no shout until it has moved, and the
    /// update did not do it.
    /// </summary>
    public async Task<bool> RunToAsync(Point location)
    {
        var distance = Me.Location.Distance(location);
        if (distance > 20)
        {
            return false;
        }

        if (RunResendWait(location) is var wait && wait > TimeSpan.Zero)
        {
            await Task.Delay(wait);
        }

        SendRun(location);
        await Task.Delay((int)(Math.Max(distance, 2) * 80 / MovementSpeed));
        AssumeAt(location);
        return true;
    }

    public void MoveTo(ushort x, ushort y) => MoveTo(new Point(x, y));
    public bool MoveTo(Point location)
    {
        var distance = Me.Location.Distance(location);
        if (distance > 20)
        {
            return false;
        }

        if (distance < 10 && DateTime.Now.Subtract(lastTeleport) > TimeSpan.FromSeconds(5))
        {
            _gameServer.SendPacket(new UpdatePlayerLocationPacket(location));
            lastTeleport = DateTime.Now;
            Thread.Sleep((int)(120 / MovementSpeed));
        }
        else if (RunResendWait(location) is var wait && wait > TimeSpan.Zero)
        {
            Thread.Sleep(wait);
        }
        else
        {
            SendRun(location);
            Thread.Sleep((int)(distance * 80 / MovementSpeed));
        }
        AssumeAt(location);
        return true;
    }

    public async Task<bool> MoveToAsync(Point location)
    {
        var distance = Me.Location.Distance(location);
        if (distance > 20)
        {
            return false;
        }

        if (distance < 10 && DateTime.Now.Subtract(lastTeleport) > TimeSpan.FromSeconds(5))
        {
            _gameServer.SendPacket(new UpdatePlayerLocationPacket(location));
            lastTeleport = DateTime.Now;
            await Task.Delay((int)(120 / MovementSpeed));
        }
        else if (RunResendWait(location) is var wait && wait > TimeSpan.Zero)
        {
            await Task.Delay(wait);
        }
        else
        {
            SendRun(location);
            await Task.Delay((int)(distance * 80 / MovementSpeed));
        }
        AssumeAt(location);
        return true;
    }

    public async Task<bool> MoveToAsync(Item item)
    {
        if (!item.Ground)
        {
            return false;
        }

        var distance = Me.Location.Distance(item.Location);
        if (distance > 20)
        {
            return false;
        }
        _gameServer.SendPacket(new RunToEntityPacket(item));
        await Task.Delay((int)(distance * 80 / MovementSpeed));
        AssumeAt(item.Location);
        return true;
    }

    public bool MoveTo(Entity entity)
    {
        var distance = Me.Location.Distance(entity.Location);
        if (distance > 20)
        {
            return false;
        }

        if (distance < 10 && DateTime.Now.Subtract(lastTeleport) > TimeSpan.FromSeconds(5))
        {
            _gameServer.SendPacket(new UpdatePlayerLocationPacket(entity.Location));
            lastTeleport = DateTime.Now;
            Thread.Sleep((int)(120 / MovementSpeed));
        }
        else
        {
            _gameServer.SendPacket(new RunToEntityPacket(entity));
            Thread.Sleep((int)(distance * 80 / MovementSpeed));
        }
        AssumeAt(entity.Location);
        return true;
    }

    public async Task<bool> MoveToAsync(Entity entity)
    {
        var distance = Me.Location.Distance(entity.Location);
        if (distance > 20)
        {
            return false;
        }

        if (distance < 10 && DateTime.Now.Subtract(lastTeleport) > TimeSpan.FromSeconds(5))
        {
            _gameServer.SendPacket(new UpdatePlayerLocationPacket(entity.Location));
            lastTeleport = DateTime.Now;
            await Task.Delay((int)(120 / MovementSpeed));
        }
        else
        {
            _gameServer.SendPacket(new RunToEntityPacket(entity));
            await Task.Delay((int)(distance * 80 / MovementSpeed));
        }
        AssumeAt(entity.Location);
        return true;
    }

    public bool InteractWithNPC(Entity entity)
    {
        var npcInfoPacket = _gameServer.GetResetEventOfType(InComingPacket.NPCInfo);
        _gameServer.SendPacket(new MakeEntityMovePacket(Me, entity));
        Thread.Sleep(50);
        _gameServer.SendPacket(new InteractWithEntityPacket(entity));
        // An answer comes within 0.8s (median 0.28s over 273); one that does not come means she was not
        // reached, and waiting the old 1.5s for it held up every second shop visit before the retry.
        return npcInfoPacket.WaitOne(900);
    }

    public bool PickupBody(Player player)
    {
        var corpseAssignPacket = _gameServer.GetResetEventOfType(InComingPacket.CorpseAssign);
        _gameServer.SendPacket(new InteractWithEntityPacket(player.Id, EntityType.Player));
        return WaitForPacket(corpseAssignPacket, 1000, "PickupBody");
    }

    public bool OpenStash(Entity stash)
    {
        var buttonActionPacket = _gameServer.GetResetEventOfType(InComingPacket.ButtonAction);
        _gameServer.SendPacket(new InteractWithEntityPacket(stash));
        return WaitForPacket(buttonActionPacket, 1500, "ActivateCube");
    }

    public bool TakeWarp(WarpData warpData)
    {
        var reassignPlayerPacket = _gameServer.GetResetEventOfType(InComingPacket.ReassignPlayer);
        _gameServer.SendPacket(new InteractWithEntityPacket(warpData.EntityId, EntityType.Doorway));
        return WaitForPacket(reassignPlayerPacket, 500, "TakeWaypoint");
    }

    public void InteractWithPlayer(Player player)
    {
        _gameServer.SendPacket(new InteractWithEntityPacket(player.Id, EntityType.Player));
    }

    /// <summary>
    /// Puts the item now on the cursor onto the character in the given slot.
    /// </summary>
    public void EquipItem(Item item, DirectoryType location)
    {
        _gameServer.SendPacket(new EquipItemPacket(item.Id, location));
    }

    /// <summary>
    /// Asks an NPC to play one of its dialogue lines. Open the chat with
    /// <see cref="InitiateEntityChat"/> first and close it afterwards.
    /// </summary>
    public void PlayNpcMessage(ushort messageId)
    {
        _gameServer.SendPacket(new PlayNpcMessagePacket(messageId));
    }

    /// <summary>
    /// Takes the item worn in the given slot off and onto the cursor. Worn items cannot be moved with the
    /// packet that moves items inside containers, which addresses them by id and ignores the body.
    /// </summary>
    public void RemoveBodyItem(DirectoryType location)
    {
        _gameServer.SendPacket(new RemoveBodyItemPacket(location));
    }

    /// <summary>
    /// Puts the horadric staff into the orifice of the real tal rasha tomb, which opens Duriel's lair for
    /// everyone in the game. The orifice refuses it when the game's seven tombs is already settled, so
    /// the game has to have been created by a character that has not finished the quest.
    /// </summary>
    public void InsertHoradricStaff(Entity orifice, Item staff)
    {
        _gameServer.SendPacket(new InsertHoradricStaffPacket(Data.Me.Id, orifice, staff));
    }

    public void InteractWithEntity(Entity entity)
    {
        _gameServer.SendPacket(new InteractWithEntityPacket(entity));
    }

    /// <summary>
    /// Messages an NPC last offered, which is what a quest message has to echo to claim a reward. Empty
    /// when the NPC has nothing pending, which is also how a claim is confirmed as already done.
    /// </summary>
    public IReadOnlyList<uint> GetOfferedMessages(Entity entity)
        => entity != null && Data.NpcMessages.TryGetValue(entity.Id, out var messages) ? messages : [];

    /// <summary>
    /// Says one of the things an NPC is offering, which is how quest rewards are collected. The id has
    /// to be one the server advertised through <see cref="GetOfferedMessages"/>.
    /// </summary>
    public void SendQuestMessage(Entity entity, uint messageId)
    {
        _gameServer.SendPacket(new QuestMessagePacket(entity.Id, messageId));
    }

    public void InitiateEntityChat(Entity entity)
    {
        _gameServer.SendPacket(new InitiateEntityChatPacket(entity));
    }

    public void TerminateEntityChat(Entity entity)
    {
        _gameServer.SendPacket(new TerminateEntityChatPacket(entity));
        RemoveNPCItems();
    }

    public void TownFolkAction(Entity entity, TownFolkActionType actionType)
    {
        if (actionType == TownFolkActionType.RefreshGamble)
        {
            RemoveNPCItems();
        }

        OpenShop(entity, actionType);
        _gameServer.SendPacket(new EntityActionPacket(entity, actionType));
    }

    /// <summary>Before the request goes out, so the listing it brings finds the shop open.</summary>
    private void OpenShop(Entity entity, TownFolkActionType actionType)
    {
        if (actionType is TownFolkActionType.Trade or TownFolkActionType.Gamble or TownFolkActionType.RefreshGamble)
        {
            Apply(ClientAction.OpenShop, entity.Id);
        }
    }

    /// <summary>
    /// Picks an entry of an NPC menu by its raw value, for menus that the
    /// <see cref="TownFolkActionType"/> shorthand does not cover - act travel in particular.
    /// </summary>
    public void TownFolkAction(Entity entity, TownFolkActionType actionType, uint actionData)
    {
        OpenShop(entity, actionType);
        _gameServer.SendPacket(new EntityActionPacket(entity, actionType, actionData));
    }

    /// <summary>
    /// Asks the travel NPC of the current act - Warriv in act 1, Meshif in act 2 - to move this
    /// character to the next act. The server answers with <c>UnloadActComplete</c> followed by
    /// <c>LoadActComplete</c>, so callers should wait on <see cref="Act"/> changing rather than
    /// assuming the travel took.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown for acts that have no NPC driven transition. Act 3 to 4 and act 4 to 5 are portals
    /// that open once their act boss is dead and have to be taken as warps.
    /// </exception>
    public void TravelWithNpc(Entity travelNpc)
    {
        if (!EntityConstants.TravelMenuActionByAct.TryGetValue(Act, out var travelAction))
        {
            throw new InvalidOperationException(
                $"There is no NPC driven travel out of {Act}, it has to be taken as a portal");
        }

        // Close the conversation first. The server moves the character to the next act without ending
        // its interaction with the NPC, and a character still interacting is busy, so every portal in
        // the new act was refused. The travel itself does not need the conversation open, and a
        // terminate sent after the act change names an NPC in another act and is ignored.
        TerminateEntityChat(travelNpc);
        TownFolkAction(travelNpc, TownFolkActionType.Quest, travelAction);
    }

    /// <summary>
    /// Resurrects this character in town after death. The corpse stays where it fell, so anything
    /// waiting on a corpse in an area must be settled before calling this.
    /// </summary>
    public void Resurrect()
    {
        _gameServer.SendPacket(new ResurrectPacket());
    }

    /// <summary>
    /// Requests a fresh push of the quest state for this character and the game.
    /// </summary>
    public void RequestQuestData()
    {
        _gameServer.SendPacket(new RequestQuestDataPacket());
    }

    /// <summary>
    /// Requests the quest state and waits for the reply, which ends with the game quest packet 0x29 and
    /// arrives within a few tens of milliseconds. Handlers run before the event is set, so the quest state
    /// is current on return. False when no reply came within <paramref name="timeout"/>.
    /// </summary>
    public bool RequestQuestDataAndWait(TimeSpan timeout)
    {
        var reply = _gameServer.GetResetEventOfType(InComingPacket.GameQuestInfo);
        _gameServer.SendPacket(new RequestQuestDataPacket());
        return reply.WaitOne(timeout);
    }

    /// <summary>
    /// Acknowledges a completed quest after the server sends its client-side completion event.
    /// </summary>
    public void CompleteQuest(QuestId quest)
    {
        _gameServer.SendPacket(new QuestCompletePacket(quest));
    }

    private void RemoveNPCItems() => Apply(ClientAction.RemoveMerchantItems);

    public void IdentifyItems(Entity entity)
    {
        var nPCTransactionPacket = _gameServer.GetResetEventOfType(InComingPacket.NPCTransaction);
        _gameServer.SendPacket(new IdentifyItemsPacket(entity));
        WaitForPacket(nPCTransactionPacket, 200, "IdentifyItemsAtCain");
    }

    public void IdentifyItem(Item bookOfIdentify, Item itemToIdentify)
    {
        var updateItemStats = _gameServer.GetResetEventOfType(InComingPacket.UpdateItemStats);
        _gameServer.SendPacket(new IdentifyItemPacket(bookOfIdentify, itemToIdentify));
        WaitForPacket(updateItemStats, 200, "IdentifyItem");
    }

    /// <summary>
    /// Asks for an item to be identified without waiting. The reset events are keyed by packet type
    /// rather than by item, so waiting on UpdateItemStats returns for whatever item the server
    /// happened to answer about first. A caller identifying several items should send them all and
    /// then wait on <see cref="Item.IsIdentified"/>, which is the state it actually cares about.
    /// </summary>
    public void SendIdentifyItem(Item bookOfIdentify, Item itemToIdentify)
    {
        _gameServer.SendPacket(new IdentifyItemPacket(bookOfIdentify, itemToIdentify));
    }

    public void ResurrectMerc(Entity entity)
    {
        var assignMercPacket = _gameServer.GetResetEventOfType(InComingPacket.AssignMerc);
        _gameServer.SendPacket(new ResurrectMercPacket(entity));
        WaitForPacket(assignMercPacket, 200, "ResurrectMerc");
    }

    public void RepairItems(Entity entity)
    {
        var nPCTransactionPacket = _gameServer.GetResetEventOfType(InComingPacket.NPCTransaction);
        _gameServer.SendPacket(new RepairPacket(entity));
        nPCTransactionPacket.WaitOne(200);
    }
    public void LogRecentPackets(string reason) => _gameServer.LogRecentPackets(reason);

    public void RequestUpdate(uint id)
    {
        _gameServer.SendPacket(new RequestUpdatePacket(id));
        Thread.Sleep(400);
    }

    /// <summary>
    /// Asks the server where a player is, without waiting. It answers with a reassign (0x15) of that player: over 2000
    /// requests in 15 games on 18 Nov 2026, 40 ms in the median and within 160 ms for 99 in 100. A player in another
    /// area, or one in a skill move such as a whirl, gets no answer.
    /// </summary>
    public void SendUpdateRequest(uint playerId) => _gameServer.SendPacket(new RequestUpdatePacket(playerId));

    /// <summary>
    /// <see cref="SendUpdateRequest"/>, then waits for the server's reassign of that player, at most
    /// <paramref name="timeout"/>. True once it is in, and the player's position is then the server's.
    /// </summary>
    public Task<bool> RequestUpdateAndWaitAsync(uint playerId, TimeSpan timeout) => RequestUpdatesAndWaitAsync([playerId], timeout);

    /// <summary>
    /// Sends every request at once, then waits until each player has been reassigned since, at most
    /// <paramref name="timeout"/>; true when all were. Counted per player rather than waited on with the packet's reset
    /// event, which is one per packet type: a teleport waiting on the same type would lose its wake-up.
    /// </summary>
    public async Task<bool> RequestUpdatesAndWaitAsync(IReadOnlyCollection<uint> playerIds, TimeSpan timeout)
    {
        var before = playerIds.Distinct().ToDictionary(id => id, ReassignsOf);
        foreach (var id in before.Keys)
        {
            SendUpdateRequest(id);
        }

        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            if (before.All(request => ReassignsOf(request.Key) != request.Value))
            {
                return true;
            }

            if (waited.Elapsed >= timeout)
            {
                return false;
            }

            await Task.Delay(UpdateAnswerPoll);
        }
    }

    private static readonly TimeSpan UpdateAnswerPoll = TimeSpan.FromMilliseconds(10);

    /// <summary>Reassigns (0x15) received per player, for <see cref="RequestUpdatesAndWaitAsync"/>.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, long> _playerReassigns = new();

    private long ReassignsOf(uint playerId) => _playerReassigns.GetValueOrDefault(playerId);

    public void PickupItem(Item item)
    {
        _gameServer.SendPacket(new PickupItemFromGroundPacket(item));
        Thread.Sleep(50);
    }

    public void DropItem(Item item)
    {
        _gameServer.SendPacket(new DropItemPacket(item));
    }

    public void UseBeltItem(Item item)
    {
        _gameServer.SendPacket(new UseBeltItem(item));
    }

    public bool PutItemInBelt(Item item)
    {
        if (item.Classification != ClassificationType.HealthPotion && item.Classification != ClassificationType.ManaPotion && item.Classification != ClassificationType.RejuvenationPotion)
        {
            throw new InvalidOperationException($"incorrect item type '{item.Name}', expected Potion");
        }
        
        var worldItemAction = _gameServer.GetResetEventOfType(InComingPacket.WorldItemAction);
        _gameServer.SendPacket(new SendItemToBeltPacket(item));
        return WaitForPacket(worldItemAction, 2000, "PutItemInBelt");
    }

    /// <summary>Spends one stat point; the server answers with the stat's new base value.</summary>
    public void AddStatPoint(D2GS.Players.Attribute stat) => _gameServer.SendPacket(new AddStatPointPacket(stat));

    /// <summary>Spends one skill point; the server answers with the skill's new base level (0x21).</summary>
    public void AddSkillPoint(Skill skill) => _gameServer.SendPacket(new AddSkillPointPacket(skill));

    /// <summary>Drops gold from the inventory as a pile at the character's feet, for a party member to pick up.</summary>
    public void DropGold(uint amount) => _gameServer.SendPacket(new DropGoldPacket(Me.Id, amount));

    public void SellItem(Entity entity, Item item)
    {
        Apply(ClientAction.Sell, item.Id);
        _saleLog.Sent(item, entity, Me.Attributes.GetValueOrDefault(D2GS.Players.Attribute.GoldOnPerson, 0));
        _gameServer.SendPacket(new SellItemPacket(entity, item));
        Thread.Sleep(30);
    }

    public void BuyItem(Entity entity, Item item, bool buyStack)
    {
        _gameServer.SendPacket(new BuyItemPacket(entity, item, buyStack, false));
        Thread.Sleep(30);
    }

    public void GambleItem(Entity entity, Item item)
    {
        _gameServer.SendPacket(new BuyItemPacket(entity, item, false, true));
        Thread.Sleep(30);
    }

    public void IdentifyGambleItem(Item item)
    {
        _gameServer.SendPacket(new IdentifyItemFromGamblePacket(item));
        Thread.Sleep(30);
    }

    internal void CheckAndPreventDesync(WalkVerifyPacket walkVerifyPacket)
    {
        ServerConfirmed(walkVerifyPacket.Location);
        if (walkVerifyPacket.Location.Distance(Data.Me.Location) > 5)
        {
            _gameServer.SendPacket(new RequestUpdatePacket(Data.Me.Id));
        }
    }

    public void SendInGameMessage(string message)
    {
        _gameServer.SendPacket(new SendChatMessagePacket(message));
    }

    public void InvitePlayer(Player player)
    {
        _gameServer.SendPacket(new PartyRequestPacket(PartyRequestType.InviteToParty, player));
    }

    /// <summary>Whether this character is in a party at all.</summary>
    public bool IsInParty() => Data.Me?.PartyId is { } mine && mine != AssignPlayerToPartyPacket.NoParty;

    /// <summary>
    /// Whether this character shares a party with the given player. Sending an invite proves nothing:
    /// only a matching party id on both sides does.
    /// </summary>
    public bool IsInPartyWith(Player player)
    {
        var mine = Data.Me?.PartyId;
        return player != null
            && mine.HasValue
            && mine.Value != AssignPlayerToPartyPacket.NoParty
            && player.PartyId == mine;
    }

    public void AcceptInvite(Player player)
    {
        _gameServer.SendPacket(new PartyRequestPacket(PartyRequestType.AcceptInvite, player));
    }

    public void AllowLootCorpse(Player player)
    {
        _gameServer.SendPacket(new SetPlayerRelationPacket(player));
    }

    private void PingThread()
    {
        try
        {
            while (IsInGame() && !_leavingGame)
            {
                _gameServer.Ping();
                var pongPacket = _gameServer.GetResetEventOfType(InComingPacket.Pong);
                pongPacket.WaitOne(1000);
                for (var slept = 0; slept < 5000 && !_leavingGame; slept += 100)
                {
                    Thread.Sleep(100);
                }
            }
        }
        catch (Exception)
        {
        }
    }
    public bool UseManaPotion()
    {
        var inventoryManaPotion = Inventory.Items.FirstOrDefault(i => i.Classification == ClassificationType.ManaPotion);
        if (inventoryManaPotion != null)
        {
            Log.Information($"{Me.Name} Using mana potion with mana at {Me.Mana} out of {Me.MaxMana}");
            LastUsedManaPotionTime = DateTime.Now;
            UsePotion(inventoryManaPotion);
            return true;
        }

        var manapotion = Belt.FirstOrDefaultManaPotion();
        if (manapotion != null)
        {
            Log.Information($"{Me.Name} Using mana potion with mana at {Me.Mana} out of {Me.MaxMana}");
            LastUsedManaPotionTime = DateTime.Now;
            UseBeltItem(manapotion);
            return true;
        }

        return false;
    }

    public bool UseHealthPotions(int amount = 1)
    {
        var inventoryHealthPotions = Inventory.Items
            .Where(i => i.Classification == ClassificationType.HealthPotion).Take(amount)
            .ToList();
        foreach (var healthPotion in inventoryHealthPotions)
        {
            LastUsedHealthPotionTime = DateTime.Now;
            UsePotion(healthPotion);
        }

        var healthPotions = Belt.TakeHealthPotions(amount - inventoryHealthPotions.Count);
        foreach (var healthPotion in healthPotions)
        {
            LastUsedHealthPotionTime = DateTime.Now;
            UseBeltItem(healthPotion);
        }

        var totalCount = healthPotions.Count + inventoryHealthPotions.Count;
        if (totalCount > 0)
        {
            Log.Information($"{Me.Name} Using {totalCount} health potions with health at {Me.Life} out of {Me.MaxLife}");
        }

        return totalCount == amount;
    }

    public bool UseRejuvenationPotion()
    {
        // The small ones first: the full ones are worth more and kept for last.
        var revpotion = Inventory.Items.Where(i => i.Classification == ClassificationType.RejuvenationPotion)
            .OrderBy(i => i.Name == ItemName.FullRejuvenationPotion).FirstOrDefault();
        if (revpotion != null)
        {
            Log.Information($"{Me.Name} Using Rejuvenation potion with life at {Me.Life} out of {Me.MaxLife}");
            LastUsedHealthPotionTime = DateTime.Now;
            LastUsedManaPotionTime = DateTime.Now;
            UsePotion(revpotion);
            return true;
        }
        else
        {
            return false;
        }
    }

    public bool IsInTown()
    {
        return Area == Area.RogueEncampment || Area == Area.LutGholein || Area == Area.KurastDocks || Area == Area.ThePandemoniumFortress || Area == Area.Harrogath;
    }

    public Item FindItemById(uint itemId)
    {
        return Data.Items.GetValueOrDefault(itemId);
    }

}
