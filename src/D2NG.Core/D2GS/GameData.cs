using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Items.Containers;
using D2NG.Core.D2GS.Motion;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;
using D2NG.Core.D2GS.Players;
using D2NG.Core.D2GS.Quest;
using D2NG.Core.MCP;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Action = D2NG.Core.D2GS.Items.Action;

namespace D2NG.Core.D2GS;

internal class GameData
{
    internal GameData(GameFlags gameFlags, Character clientCharacter, MonsterMotionSettings motion = null, ClientRules rules = null)
    {
        ArgumentNullException.ThrowIfNull(clientCharacter);

        Flags = gameFlags;
        Rules = rules ?? ClientRules.Latest;
        _cursorNeedsLift = Rules.CursorNeedsLift;
        _outstandingLifts = Rules.OutstandingLifts;
        _quiverCountFollowsServer = Rules.QuiverCountFollowsServer;
        _carriedCountFollowsServer = Rules.CarriedCountFollowsServer;
        _experienceTotalSets = Rules.ExperienceTotalSets;
        _socketingClearsCursor = Rules.SocketingClearsCursor;
        motion ??= new MonsterMotionSettings();
        Act = new ActData { Motion = new MotionContext(motion, ChasedUnitLocation), Resolver = new AreaResolver(motion.Clock, () => Me?.Location), Rules = Rules };
        ClientCharacter = clientCharacter;
        _walkingSpeedMultiplier = new Lazy<double>(() =>
        {
            var walkingSpeedIncreasedMultiplier = 1.0;
            var speedItems = this.Items.Where(i => i.Value.Action == Action.Equip && i.Value.EntityType == EntityType.Player && i.Value.PlayerId == Me?.Id).Select(i => i.Value.GetValueOfStatType(StatType.FasterRunWalk));
            if (speedItems.Any())
            {
                walkingSpeedIncreasedMultiplier += speedItems.Aggregate((agg, frw) => agg + frw) / (double)100;
            }

            var increasedSpeedSkill = Me.Skills.GetValueOrDefault(Skill.IncreasedSpeed, 0);
            if (increasedSpeedSkill > 0)
            {
                walkingSpeedIncreasedMultiplier += (13 + 4 * increasedSpeedSkill) / (double)100;
            }

            var vigorSkill = Me.Skills.GetValueOrDefault(Skill.Vigor, 0);
            if (vigorSkill > 0)
            {
                walkingSpeedIncreasedMultiplier += (13 + 2.5 * vigorSkill) / 100;
            }

            Serilog.Log.Information("Walking speed multiplier resolved to {Multiplier} from {ItemCount} equipped items",
                walkingSpeedIncreasedMultiplier, speedItems.Count());
            return walkingSpeedIncreasedMultiplier;
        });
    }

    private readonly Lazy<double> _walkingSpeedMultiplier;

    internal ClientRules Rules { get; }

    private readonly bool _cursorNeedsLift;

    private readonly bool _outstandingLifts;

    private readonly bool _quiverCountFollowsServer;

    private readonly bool _carriedCountFollowsServer;

    private readonly bool _experienceTotalSets;

    private readonly bool _socketingClearsCursor;

    /// <summary>The last packet in which the server stated where this character is since the act load; null until the first.</summary>
    internal PositionConfirmation PositionConfirmation => Act.Resolver.Current.Confirmation;

    /// <summary>
    /// Records a position the server stated for this character, with where the client held itself just before.
    /// The join's 0x59 and first 0x95 carry 0,0, which is no position.
    /// </summary>
    internal void ConfirmPosition(Point location, InComingPacket packet)
    {
        if (Me != null && location != null && (location.X != 0 || location.Y != 0))
        {
            Act.Resolver.Confirmed(location, packet);
        }
    }

    internal void ConfirmPosition(EntityType type, uint id, Point location, InComingPacket packet)
    {
        if (type == EntityType.Player && Me?.Id == id)
        {
            ConfirmPosition(location, packet);
        }
    }

    /// <summary>Where a monster's chase target is as far as this client knows; a monster target gives its last known position.</summary>
    private Point ChasedUnitLocation(EntityType type, uint id)
    {
        if (type == EntityType.NPC)
        {
            return Act.WorldObjects.TryGetValue((id, EntityType.NPC), out var npc) ? npc.LastKnownLocation : null;
        }

        if (Me is { } me && me.Id == id)
        {
            return me.Location;
        }

        var players = Players;
        for (var i = 0; i < players.Count; i++)
        {
            if (players[i] is { } player && player.Id == id)
            {
                return player.Location;
            }
        }

        return null;
    }

    public Character ClientCharacter { get; }
    public GameFlags Flags { get; }
    public ActData Act { get; }
    public QuestState Quests { get; } = new();
    public Self Me { get; private set; }

    public Container Stash { get; } = new Stash();

    public Container Inventory { get; } = new Inventory();

    public Container Cube { get; } = new Cube();

    public Belt Belt { get; internal set; } = new Belt();

    public Item CursorItem { get; internal set; }

    /// <summary>
    /// The item this client asked to lift out of a container and has not put down yet. A completed trade
    /// removes every item from both players' trade grids with the same packet a lift gets, so only the
    /// asked-for item goes to the cursor.
    /// </summary>
    internal uint? LiftRequested { get; set; }

    /// <summary>How many lifts <see cref="ClientRule.OutstandingLifts"/> keeps waiting for their removal; the oldest goes first.</summary>
    internal const int MaxLiftsOutstanding = 16;

    private readonly List<uint> _liftsOutstanding = [];

    /// <summary>
    /// Under <see cref="ClientRule.OutstandingLifts"/>, every lift not answered yet, oldest first: a lift answered after
    /// the caller gave up and lifted another item still puts its item on the cursor, where the server holds it. The server
    /// handles a client's packets in order and answers an accepted lift by taking the item out of its container, so a lift
    /// stops counting once the server is seen to be past it: its own item leaves its container or is put down or dropped
    /// (that lift and every older one are done - an older one with no answer by then was refused), or any item reaches the
    /// cursor (every lift is done - an older one was refused, a newer one finds the cursor full).
    /// </summary>
    internal IReadOnlyList<uint> LiftsOutstanding
    {
        get
        {
            lock (_liftsOutstanding)
            {
                return [.. _liftsOutstanding];
            }
        }
    }

    /// <summary>The client asked to lift an item out of a container (<see cref="ClientAction.Lift"/>).</summary>
    internal void Lifted(uint id)
    {
        LiftRequested = id;
        if (!_outstandingLifts)
        {
            return;
        }

        lock (_liftsOutstanding)
        {
            _liftsOutstanding.Remove(id);
            _liftsOutstanding.Add(id);
            if (_liftsOutstanding.Count > MaxLiftsOutstanding)
            {
                _liftsOutstanding.RemoveAt(0);
            }
        }
    }

    private bool LiftAsked(uint id)
    {
        if (!_outstandingLifts)
        {
            return LiftRequested == id;
        }

        lock (_liftsOutstanding)
        {
            return _liftsOutstanding.Contains(id);
        }
    }

    /// <summary>The server dealt with this item's lift - took it out, put it down or dropped it: that lift and every older one are done.</summary>
    private void LiftSettled(uint id)
    {
        if (!_outstandingLifts)
        {
            return;
        }

        lock (_liftsOutstanding)
        {
            var index = _liftsOutstanding.IndexOf(id);
            if (index >= 0)
            {
                _liftsOutstanding.RemoveRange(0, index + 1);
            }
        }
    }

    /// <summary>An item reached this client's cursor: under <see cref="ClientRule.OutstandingLifts"/> no lift is outstanding any more.</summary>
    private void CursorFilled()
    {
        if (!_outstandingLifts)
        {
            return;
        }

        lock (_liftsOutstanding)
        {
            _liftsOutstanding.Clear();
        }
    }

    /// <summary>The server emptied the cursor (<see cref="ClientAction.ClearCursor"/>): no lift is outstanding any more.</summary>
    internal void CursorCleared()
    {
        CursorItem = null;
        LiftRequested = null;
        lock (_liftsOutstanding)
        {
            _liftsOutstanding.Clear();
        }
    }

    /// <summary>An act load (0x03) closes any shop, and under <see cref="ClientRule.OutstandingLifts"/> ends every outstanding lift.</summary>
    internal void ActLoaded()
    {
        ShopOpen = false;
        if (!_outstandingLifts)
        {
            return;
        }

        lock (_liftsOutstanding)
        {
            _liftsOutstanding.Clear();
        }
    }

    /// <summary>
    /// Items this client sold and the server has not taken yet. A sale takes the item out of its
    /// container with the same packet a lift gets and nothing follows to say it is gone, so without
    /// this the last item sold sat on the cursor for the rest of the game.
    /// </summary>
    internal ConcurrentDictionary<uint, byte> SellRequested { get; } = new();

    /// <summary>
    /// A vendor's trade or gamble screen is open: opened by <see cref="ClientAction.OpenShop"/>, closed by
    /// <see cref="ClientAction.RemoveMerchantItems"/> (the end of the chat) and by an act load.
    /// </summary>
    internal bool ShopOpen { get; set; }

    /// <summary>
    /// This game has opened a shop with <see cref="ClientAction.OpenShop"/>, so listings arriving with none open are
    /// dropped. False in traces recorded before the action existed, whose bot kept them: their replay keeps them too.
    /// </summary>
    internal bool ShopTracked { get; set; }

    /// <summary>The trade screen was closed before the vendor's listing arrived: kept, it stays until the next vendor visit.</summary>
    private bool ListingWithoutShop(Item item) => item.Action == Action.AddToShop && ShopTracked && !ShopOpen;

    /// <summary>
    /// Items the client right-clicked to use - a potion of life, a book, a scroll. The server takes them out
    /// of their container (item action 5) and nothing follows, so like a sold item they are gone, not lifted.
    /// </summary>
    internal ConcurrentDictionary<uint, byte> UseRequested { get; } = new();

    public List<Player> Players { get; internal set; } = [];

    public ConcurrentDictionary<uint, Item> Items { get; private set; } = new ConcurrentDictionary<uint, Item>();

    internal double WalkingSpeedMultiplier
    {

        get { return _walkingSpeedMultiplier.Value; }
    }

    internal void AddExperience(AddExpPacket addExpPacket)
    {
        if (_experienceTotalSets && addExpPacket.IsTotal)
        {
            Me.Experience = addExpPacket.Experience;
            return;
        }

        Me.Experience += addExpPacket.Experience;
    }

    internal void PlayerAssign(AssignPlayerPacket packet)
    {
        if (packet.Name == ClientCharacter.Name && packet.Class == ClientCharacter.Class)
        {
            Me ??= NewSelf(new Self(packet));

            Me.Location = packet.Location;
            Me.Area = Act.Area;
            Me.Act = Act.Act;
        }

        var player = Players.Where(p => p.Id == packet.Id).FirstOrDefault();
        if (player == null)
        {
            Players.Add(new Player(packet));
        }
        else
        {
            player.Location = packet.Location;
        }
    }

    private Self NewSelf(Self self)
    {
        self.CarriedCharms = () => Inventory.Items.Where(i => i.Classification is ClassificationType.SmallCharm or ClassificationType.LargeCharm or ClassificationType.GrandCharm);
        self.LocationChanged = _ => Act.Resolver.Moved();
        return self;
    }

    internal void PlayerJoin(PlayerInGamePacket packet)
    {
        if (packet.Name == ClientCharacter.Name && packet.Class == ClientCharacter.Class && Me == null)
        {
            Me = NewSelf(new Self(packet));
        }

        var player = Players.Where(p => p.Id == packet.Id).FirstOrDefault();
        if (player == null)
        {
            Players.Add(new Player(packet));
        }
    }

    internal void AssignMerc(AssignMercPacket packet)
    {
        var player = Players.Where(p => p.Id == packet.PlayerEntityId).FirstOrDefault();
        player?.MercId = packet.MercEntityId;

        if (packet.PlayerEntityId == Me.Id)
        {
            Me.MercId = packet.MercEntityId;
        }
    }

    /// <summary>The first player with the id, as Players.Where(...).FirstOrDefault() found it, without a closure per packet.</summary>
    private Player FindPlayer(uint id)
    {
        foreach (var player in Players)
        {
            if (player.Id == id)
            {
                return player;
            }
        }

        return null;
    }

    internal void PlayerLeave(PlayerLeftGamePacket packet)
    {
        Players.RemoveAll(p => p.Id == packet.Id);
    }

    internal void AddEntityEffect(AddEntityEffectPacket packet)
    {
        if (packet.EntityType == EntityType.Player)
        {
            var player = FindPlayer(packet.EntityId);
            player?.AddEffect(packet);

            if (packet.EntityId == Me.Id)
            {
                Me.AddEffect(packet);
            }
        }
        else
        {
            Act.WorldObjects[(packet.EntityId, packet.EntityType)].Effects.Add(packet.Effect);
        }
    }

    internal void AddEntityEffect2(AddEntityEffectPacket2 packet)
    {
        if (packet.EntityType == EntityType.Player)
        {
            var player = FindPlayer(packet.EntityId);
            player?.Effects.TryAdd(packet.Effect, packet.Effect);

            if (packet.EntityId == Me.Id)
            {
                Me.Effects.TryAdd(packet.Effect, packet.Effect);
            }
        }
        else
        {
            Act.WorldObjects[(packet.EntityId, packet.EntityType)].Effects.Add(packet.Effect);
        }
    }

    internal void UpdateEntityEffects(UpdateEntityEffectsPacket packet)
    {
        if (packet.EntityType == EntityType.Player)
        {
            var player = FindPlayer(packet.EntityId);
            player?.UpdateEffects(packet.EntityEffects);

            if (packet.EntityId == Me.Id)
            {
                Me.UpdateEffects(packet.EntityEffects);
            }
        }
        else
        {
            Act.WorldObjects[(packet.EntityId, packet.EntityType)].Effects = packet.EntityEffects;
        }
    }


    internal void PlayerCorpseAssign(CorpseAssignPacket packet)
    {
        var player = Players.Where(p => p.Id == packet.PlayerId).FirstOrDefault();
        uint? corpseId = packet.CorpseAdded ? packet.CorpseId : null;
        player?.CorpseId = corpseId;

        if (Me?.Id == packet.PlayerId)
        {
            Me.CorpseId = corpseId;
        }

        var corpse = Players.Where(p => p.Id == packet.CorpseId).FirstOrDefault();
        if (corpse != null && !packet.CorpseAdded)
        {
            Players.RemoveAll(p => p.Id == packet.CorpseId);
        }
    }

    internal void EntityMove(EntityMovePacket packet)
    {
        if (packet.UnitId == Me.Id)
        {
            ConfirmPosition(packet.CurrentLocation, InComingPacket.EntityMove);
            Me.Location = packet.MoveToLocation;
        }

        foreach (var player in Players)
        {
            if (packet.UnitId == player.Id)
            {
                player.Location = packet.MoveToLocation;
                break;
            }
        }
    }

    internal void PlayerStop(PlayerStopPacket packet)
    {
        if (packet.EntityType != EntityType.Player)
        {
            return;
        }

        if (packet.EntityId == Me.Id)
        {
            ConfirmPosition(packet.Location, InComingPacket.PlayerStop);
            Me.Location = packet.Location;
        }

        foreach (var player in Players)
        {
            if (packet.EntityId == player.Id)
            {
                player.Location = packet.Location;
                break;
            }
        }
    }

    internal void ReassignPlayer(EntityType entityType, uint unitId, Point location)
    {
        if (entityType == EntityType.Player)
        {
            if (unitId == Me.Id)
            {
                Me.Location = location;
            }

            foreach (var player in Players)
            {
                if (unitId == player.Id)
                {
                    player.Location = location;
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Remembers what each NPC is currently offering, so a reward can be claimed by echoing the id the
    /// server just advertised rather than by knowing it in advance.
    /// </summary>
    internal void UpdateNpcMessages(NpcInfoPacket packet)
    {
        NpcMessages[packet.EntityId] = packet.MessageIds;
    }

    internal Dictionary<uint, List<uint>> NpcMessages { get; } = [];

    /// <summary>
    /// Passes a revealed tile on together with where the character is, so the area it names is only taken
    /// as the current one when it is the tile being stood in.
    /// </summary>
    internal void MapReveal(MapRevealPacket packet)
    {
        Act.HandleMapRevealPacket(packet, Me?.Location);
    }

    internal void AssignPlayerToParty(AssignPlayerToPartyPacket packet)
    {
        if (Me?.Id == packet.PlayerId)
        {
            Me.PartyId = packet.PartyId;
        }

        var player = Players.Find(p => p.Id == packet.PlayerId);
        player?.PartyId = packet.PartyId;
    }

    internal void UpdateQuests(QuestInfoPacket packet)
    {
        Quests.UpdateCharacter(packet.Quests);
    }

    internal void UpdateQuests(GameQuestInfoPacket packet)
    {
        Quests.UpdateGame(packet.Quests);
    }

    internal void UpdatePlayerPartyInfo(AllyPartyInfoPacket packet)
    {
        if (packet.EntityType == EntityType.Player)
        {
            if (packet.EntityId == Me.Id)
            {
                Me.Area = packet.Area;
                Me.Act = packet.Area.MapToAct();
                Me.LifePercentage = packet.LifePercentage;
            }

            foreach (var player in Players)
            {
                if (packet.EntityId == player.Id)
                {
                    player.Area = packet.Area;
                    player.Act = packet.Area.MapToAct();
                    player.LifePercentage = packet.LifePercentage;
                    break;
                }
            }
        }
    }

    internal void UpdateSummonInfo(PetActionPacket packet)
    {
        if (Me.Id == packet.PlayerId)
        {
            if (packet.AddingSummon)
            {
                Me.Summons.Add(new Summon { Id = packet.SummonId, NPCCode = packet.UniqueCode });
            }
            else
            {
                Me.Summons.RemoveAll(s => s.Id == packet.SummonId);
            }
        }

        foreach (var player in Players)
        {
            if (packet.PlayerId == player.Id)
            {
                if (packet.AddingSummon)
                {
                    player.Summons.Add(new Summon { Id = packet.SummonId, NPCCode = packet.UniqueCode });
                }
                else
                {
                    player.Summons.RemoveAll(s => s.Id == packet.SummonId);
                }
                break;
            }
        }
    }

    internal void SetAttribute(BaseAttributePacket baseAttribute)
        => Me.UpdateAttribute(baseAttribute.Attribute, baseAttribute.Value);

    internal void SetItemSkill(SetItemSkillPacket packet)
    {
        if (packet.UnitId == Me.Id)
        {
            Me.ItemSkills[packet.Skill] = packet.Amount;
            // 0x21 carries the hard points too, and is what the server sends after a skill point is spent.
            if (packet.HasBaseLevel)
            {
                Me.Skills[packet.Skill] = packet.BaseLevel;
            }
        }
    }

    internal void SetSkills(BaseSkillLevelsPacket packet)
    {
        if (packet.PlayerId == Me.Id)
        {
            foreach (var skill in packet.Skills)
            {
                Me.Skills[skill.Key] = skill.Value;
            }
        }
    }

    internal void SetActiveSkill(SetActiveSkillPacket packet)
    {
        if (packet.UnitGid == Me.Id)
        {
            Me.ActiveSkills[packet.Hand] = packet.Skill;
        }
    }

    internal void UpdateSelf(LifeManaPotUpdatePacket packet)
    {
        ConfirmPosition(packet.Location, InComingPacket.LifeManaUpdatePot);
        Me.Location = packet.Location;
        Me.Life = packet.Life;
        Me.Mana = packet.Mana;
        Me.Stamina = packet.Stamina;
    }

    internal void UpdateSelf(LifeManaUpdatePacket packet)
    {
        ConfirmPosition(packet.Location, InComingPacket.LifeManaUpdate);
        Me.Location = packet.Location;
        Me.Life = packet.Life;
        Me.Mana = packet.Mana;
        Me.Stamina = packet.Stamina;
    }

    internal void UpdateWaypointInfo(WaypointMenuPacket packet)
    {
        Me.LastSelectedWaypointId = packet.WaypointId;
        Me.AllowedWaypoints = [.. packet.AllowedWaypoints];
    }

    internal void ItemUpdate(ParseItemPacket packet)
    {
        ItemUpdate(packet.Item);
    }

    internal void ItemUpdate(Item item)
    {
        if (ListingWithoutShop(item))
        {
            return;
        }

        if (Items.TryGetValue(item.Id, out var previous) && previous != item && item.SocketedItems.IsEmpty)
        {
            foreach (var gem in previous.SocketedItems)
            {
                item.SocketedItems[gem.Key] = gem.Value;
            }
        }

        Items[item.Id] = item;
        switch (item.Action)
        {
            case Action.ItemInSocket:
                // The owner field of a socketed item's packet is the id of the item it sits in.
                if (Items.TryGetValue(item.PlayerId, out var parent))
                {
                    parent.SocketedItems[item.Id] = item;
                }

                // Put into a socket is put down: the lift that brought it to the cursor is settled like a drop's.
                if (_socketingClearsCursor && CursorItem?.Id == item.Id)
                {
                    CursorItem = null;
                    LiftSettled(item.Id);
                }

                break;
            case Action.Equip:

                if (item.EntityType == EntityType.Player)
                {
                    if(item.PlayerId == Me?.Id)
                    {
                        if (item.Classification == ClassificationType.Belt)
                        {
                            Belt.UpdateBeltRows(item.BeltRows);
                        }
                        Me.EquipItem(item);

                        // Equipping takes the item off the cursor; without this the client kept believing it
                        // held the item there and spent seconds trying to put away something already worn.
                        if (CursorItem?.Id == item.Id)
                        {
                            CursorItem = null;
                        }
                    }

                }
                break;
            case Action.Unequip:
                if (item.EntityType == EntityType.Player)
                {
                    if (item.PlayerId == Me?.Id)
                    {
                        if (item.Classification == ClassificationType.Belt)
                        {
                            Belt.UpdateBeltRows(1);
                        }

                        // A quiver shot empty is unequipped by the server and gone; it does not land on the
                        // cursor. Taken for held, it blocked an amazon without a spare quiver for minutes: every
                        // attempt to put it away went unanswered.
                        var emptied = Me.Equipment.Values.FirstOrDefault(worn => worn.Id == item.Id) is { } worn
                            && worn.Classification is ClassificationType.Arrows or ClassificationType.Bolts && worn.Amount == 0;
                        Me.UnequipItem(item);

                        // An item taken off the body lands on the cursor, exactly as one pulled out of a
                        // container does. Without this the cursor stays empty as far as the bot is
                        // concerned and anything waiting to place the item waits for ever.
                        if (!emptied)
                        {
                            CursorItem = item;
                            CursorFilled();
                        }
                    }
                }
                break;
            case Action.RefillFromInventory:
                if (item.EntityType == EntityType.Player && item.PlayerId == Me?.Id)
                {
                    // The unequip just before put the empty quiver on the cursor, where the bot then
                    // spent seven seconds a time trying to put away arrows that no longer existed.
                    if (CursorItem?.Name == item.Name)
                    {
                        CursorItem = null;
                    }

                    Inventory.Remove(item.Id);
                    Me.EquipItem(item);
                }
                break;
            case Action.PutInContainer:
                PutInContainer(item);
                break;
            case Action.SwapInContainer:
                SwapInContainer(item);
                break;
            case Action.RemoveFromContainer:
                RemoveFromContainer(item);
                break;
            case Action.PutInBelt:
                PutInContainer(item);
                break;
            case Action.RemoveFromBelt:
                Belt.Remove(item);
                break;
            case Action.ToCursor:
                CursorItem = item;
                CursorFilled();
                break;
            case Action.UpdateStats:
                UpdateItemStatsInContainer(item);
                break;
            case Action.DropToGround:
                if (CursorItem?.Id == item.Id)
                {
                    CursorItem = null;
                }

                LiftSettled(item.Id);
                break;
            default:
                // Do nothing because we don't know
                break;
        }
    }

    private void UpdateItemStatsInContainer(Item item)
    {
        var inventoryItem = Inventory.FindItemById(item.Id);
        if (inventoryItem != null)
        {
            Inventory.UpdateItem(inventoryItem, item);
        }

        var cubeItem = Cube.FindItemById(item.Id);
        if (cubeItem != null)
        {
            Cube.UpdateItem(cubeItem, item);
        }

        var stashItem = Stash.FindItemById(item.Id);
        if (stashItem != null)
        {
            Stash.UpdateItem(stashItem, item);
        }

        var beltItem = Belt.FindItemById(item.Id);
        if (beltItem != null)
        {
            Belt.UpdateItem(beltItem, item);
        }
    }

    private void RemoveFromContainer(Item item)
    {
        var lifted = !SellRequested.TryRemove(item.Id, out _) & !UseRequested.TryRemove(item.Id, out _);
        // Only an item this client asked to lift is on its cursor. Akara took the Scroll of Inifuss out of the inventory
        // and put the Key to the Cairn Stones in its place; read as lifted, the scroll stayed on the cursor for good and
        // every put-away was refused.
        var asked = !_cursorNeedsLift || LiftAsked(item.Id);
        switch (item.Container)
        {
            case ContainerType.Stash:
                if (Stash.Remove(item) && lifted && asked)
                {
                    CursorItem = item;
                }
                break;
            case ContainerType.Stash2:
                if (Stash.Remove(item) && lifted && asked)
                {
                    CursorItem = item;
                }
                break;
            case ContainerType.Belt:
                if (Belt.Remove(item) && lifted)
                {
                    CursorItem = item;
                }
                break;
            case ContainerType.Cube:
                if (Cube.Remove(item) && lifted && asked)
                {
                    CursorItem = item;
                }
                break;
            case ContainerType.Inventory:
                if (Inventory.Remove(item) && lifted && asked)
                {
                    CursorItem = item;
                }

                break;
            case ContainerType.ForTrade:
            case ContainerType.TradeOffer:
                // Lifted off the trade grid: the trade page is not modelled, but the item is on the cursor now.
                if (LiftAsked(item.Id))
                {
                    CursorItem = item;
                }
                break;
            default:
                // Do nothing we don't know how to handle this
                break;
        }

        if (ReferenceEquals(CursorItem, item))
        {
            CursorFilled();
        }
        else
        {
            LiftSettled(item.Id);
        }
    }

    /// <summary>
    /// The cursor item was put down on top of another one (a 0x1F swap): it lands in that item's
    /// place, and the item it displaced goes to the cursor. The server reports only the item that
    /// landed, so the displaced one is found by the cells they share.
    /// </summary>
    private void SwapInContainer(Item item)
    {
        var container = item.Container switch
        {
            ContainerType.Stash or ContainerType.Stash2 => Stash,
            ContainerType.Inventory => Inventory,
            ContainerType.Cube => Cube,
            _ => null
        };

        var displaced = container?.Items
            .Where(other => other.Id != item.Id && other.Container == item.Container && Overlaps(other, item))
            .ToList() ?? [];
        foreach (var other in displaced)
        {
            container.Remove(other);
        }

        PutInContainer(item);
        if (displaced.Count == 1)
        {
            CursorItem = displaced[0];
            CursorFilled();
        }
    }

    private static bool Overlaps(Item a, Item b)
    {
        return a.Location.X < b.Location.X + Math.Max((int)b.Width, 1)
            && b.Location.X < a.Location.X + Math.Max((int)a.Width, 1)
            && a.Location.Y < b.Location.Y + Math.Max((int)b.Height, 1)
            && b.Location.Y < a.Location.Y + Math.Max((int)a.Height, 1);
    }

    private void PutInContainer(Item item)
    {
        if (CursorItem?.Id == item.Id)
        {
            CursorItem = null;
        }

        if (LiftRequested == item.Id)
        {
            LiftRequested = null;
        }

        LiftSettled(item.Id);

        switch (item.Container)
        {
            case ContainerType.Stash:
                Stash.Add(item);
                break;
            case ContainerType.Stash2:
                Stash.Add(item);
                break;
            case ContainerType.Belt:
                if (Belt.Width <= item.Location.X)
                {
                    Belt.UpdateBeltRows((uint)item.Location.X + 1);
                }
                if (Belt.Height <= item.Location.Y)
                {
                    Belt.UpdateBeltRows((uint)item.Location.Y + 1);
                }
                Belt.Add(item);
                break;
            case ContainerType.Cube:
                Cube.Add(item);
                break;
            case ContainerType.Inventory:
                Inventory.Add(item);
                break;
            default:
                // Do nothing we don't know how to handle this
                break;
        }
    }

    internal void UseStackableItem(UseStackableItemPacket packet)
    {
        var item = Inventory.FindItemById(packet.ItemId);
        if(item != null &&
            ( item.Classification == ClassificationType.HealthPotion
            || item.Classification == ClassificationType.ManaPotion
            || item.Classification == ClassificationType.RejuvenationPotion))
        {
            Inventory.Remove(item);
        }
    }

    internal void UpdateItemStats(UpdateItemStatsPacket packet)
    {
        var item = Me.Equipment.Values.FirstOrDefault(i => i.Id == packet.ItemId);
        if(item != null)
        {
            if (packet.UpdateType == 70
               && (item.Classification == ClassificationType.Javelin || item.Classification == ClassificationType.AmazonJavelin
                   || (_quiverCountFollowsServer && item.Classification is ClassificationType.Arrows or ClassificationType.Bolts)))
            {
                item.Amount = packet.Amount;
            }

            return;
        }

        if (_carriedCountFollowsServer && packet.UpdateType == 70)
        {
            var carried = Inventory.FindItemById(packet.ItemId) ?? Cube.FindItemById(packet.ItemId) ?? Stash.FindItemById(packet.ItemId);
            if (carried != null)
            {
                carried.Amount = packet.Amount;
            }

            if (Items.TryGetValue(packet.ItemId, out var known) && known != carried && carried != null)
            {
                known.Amount = packet.Amount;
            }
        }
    }

    internal void RemoveObject(RemoveObjectPacket packet)
    {
        switch (packet.EntityType)
        {
            case EntityType.Doorway:
                break;
            case EntityType.Player:
                Act.RemoveWorldObject(packet.EntityId, packet.EntityType);
                foreach (var player in Players.Where(p => p.CorpseId == packet.EntityId).ToList())
                {
                    Players.RemoveAll(p => p.Id == packet.EntityId);
                    player.CorpseId = null;
                }
                break;
            case EntityType.NPC:
            case EntityType.Object:
                Act.RemoveWorldObject(packet.EntityId, packet.EntityType);
                break;
            case EntityType.Missiles:
                break;
            case EntityType.Item:
                Items.Remove(packet.EntityId, out var value2);
                break;
        }
    }
}
