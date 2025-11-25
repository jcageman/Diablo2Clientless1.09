using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Items.Containers;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;
using D2NG.Core.D2GS.Packet.Outgoing;
using D2NG.Core.D2GS.Players;
using D2NG.Core.Extensions;
using D2NG.Core.MCP;
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

    private Thread pingThread, chickenThread;

    private DateTime lastTeleport;

    private DateTime LastUsedHealthPotionTime = DateTime.Now;

    private DateTime LastUsedManaPotionTime = DateTime.Now;

    private Character selectedCharacter;
    protected Func<Item, Task> ItemDroppedHandler { get; set; }

    protected Func<Item, Task> ItemOwnedHandler { get; set; }

    internal Game(GameServer gameServer)
    {
        _gameServer = gameServer;

        _gameServer.OnReceivedPacketEvent(InComingPacket.GameLoading, _ =>
        {
            if (IsInGame())
            {
                Log.Information($"{selectedCharacter?.Name}: Game loading..., About to drop");
                LeaveGame().Wait();
            }
        }
        );
        _gameServer.OnReceivedPacketEvent(InComingPacket.GameFlags, p => Initialize(new GameFlags(p)));
        _gameServer.OnReceivedPacketEvent(InComingPacket.LoadAct, p => Data.Act.LoadActData(new ActDataPacket(p)));
        _gameServer.OnReceivedPacketEvent(InComingPacket.MapReveal, p => Data.Act.HandleMapRevealPacket(new MapRevealPacket(p)));
        _gameServer.OnReceivedPacketEvent(InComingPacket.AssignLevelWarp, p => Data.Act.AddWarp(new AssignLevelWarpPacket(p)));
        _gameServer.OnReceivedPacketEvent(InComingPacket.GameHandshake, p => new GameHandshakePacket(p));
        _gameServer.OnReceivedPacketEvent(InComingPacket.NPCHit, p => Data.Act.UpdateNPCOnHit(new NpcHitPacket(p)));
        _gameServer.OnReceivedPacketEvent(InComingPacket.EntityMove, p => Data.EntityMove(new EntityMovePacket(p)));
        _gameServer.OnReceivedPacketEvent(InComingPacket.AssignPlayer, p => Data.PlayerAssign(new AssignPlayerPacket(p)));
        _gameServer.OnReceivedPacketEvent(InComingPacket.ReassignPlayer, p => { var packet = new ReassignPlayerPacket(p); Data.ReassignPlayer(packet.UnitType, packet.UnitId, packet.Location); });
        _gameServer.OnReceivedPacketEvent(InComingPacket.PartyAutomapInfo, p => { var packet = new PartyAutomapInfoPacket(p); Data.ReassignPlayer(EntityType.Player, packet.Id, packet.Location); });
        _gameServer.OnReceivedPacketEvent(InComingPacket.AddExperienceByte, p => Data.AddExperience(new AddExpPacket(p)));
        _gameServer.OnReceivedPacketEvent(InComingPacket.AddExperienceWord, p => Data.AddExperience(new AddExpPacket(p)));
        _gameServer.OnReceivedPacketEvent(InComingPacket.AddExperienceDword, p => Data.AddExperience(new AddExpPacket(p)));
        _gameServer.OnReceivedPacketEvent(InComingPacket.AddAttributeByte, p => Data.SetAttribute(new BaseAttributePacket(p)));
        _gameServer.OnReceivedPacketEvent(InComingPacket.AddAttributeWord, p => Data.SetAttribute(new BaseAttributePacket(p)));
        _gameServer.OnReceivedPacketEvent(InComingPacket.AddAttributeDword, p => Data.SetAttribute(new BaseAttributePacket(p)));
        _gameServer.OnReceivedPacketEvent(InComingPacket.UpdateItemOSkill, p => Data.SetItemSkill(new SetItemSkillPacket(p)));
        _gameServer.OnReceivedPacketEvent(InComingPacket.UpdateItemSkill, p => Data.SetItemSkill(new SetItemSkillPacket(p)));
        _gameServer.OnReceivedPacketEvent(InComingPacket.SetSkill, p => Data.SetActiveSkill(new SetActiveSkillPacket(p)));
        _gameServer.OnReceivedPacketEvent(InComingPacket.QuestInfo, p => new QuestInfoPacket(p));
        _gameServer.OnReceivedPacketEvent(InComingPacket.GameQuestInfo, p => new GameQuestInfoPacket(p));
        _gameServer.OnReceivedPacketEvent(InComingPacket.ObjectState, p => Data.Act.UpdateObjectState(new ObjectStatePacket(p)));
        _gameServer.OnReceivedPacketEvent(InComingPacket.NPCState, p => Data.Act.UpdateNPCState(new NpcStatePacket(p)));
        _gameServer.OnReceivedPacketEvent(InComingPacket.NPCMove, p => { var packet = new NPCMovePacket(p); Data.Act.UpdateNPCLocation(packet.EntityId, packet.Location); });
        _gameServer.OnReceivedPacketEvent(InComingPacket.NPCStop, p => { var packet = new NPCStopPacket(p); Data.Act.UpdateNPCLocation(packet.EntityId, packet.Location); });
        _gameServer.OnReceivedPacketEvent(InComingPacket.AssignNPC2, p => Data.Act.AddNPC(new AssignNpcPacket(p)));
        _gameServer.OnReceivedPacketEvent(InComingPacket.AssignNPC1, p => Data.Act.AddNPC(new AssignNpcPacket(p)));
        _gameServer.OnReceivedPacketEvent(InComingPacket.TownPortalState, p => Data.Act.UpdateTownPortal(new TownPortalStatePacket(p)));
        _gameServer.OnReceivedPacketEvent(InComingPacket.PortalOwner, p => Data.Act.UpdateTownPortalOwner(new PortalOwnerPacket(p)));
        _gameServer.OnReceivedPacketEvent(InComingPacket.AssignObject, p => Data.Act.AddWorldObject(new AssignObjectPacket(p).AsWorldObject()));
        _gameServer.OnReceivedPacketEvent(InComingPacket.RemoveObject, p => Data.RemoveObject(new RemoveObjectPacket(p)));
        _gameServer.OnReceivedPacketEvent(InComingPacket.PlayerInGame, p => Data.PlayerJoin(new PlayerInGamePacket(p)));
        _gameServer.OnReceivedPacketEvent(InComingPacket.PlayerLeftGame, p => Data.PlayerLeave(new PlayerLeftGamePacket(p)));
        _gameServer.OnReceivedPacketEvent(InComingPacket.BaseSkillLevels, p => Data.SetSkills(new BaseSkillLevelsPacket(p)));
        _gameServer.OnReceivedPacketEvent(InComingPacket.LifeManaUpdatePot, p => Data.UpdateSelf(new LifeManaPotUpdatePacket(p)));
        _gameServer.OnReceivedPacketEvent(InComingPacket.LifeManaUpdate, p => Data.UpdateSelf(new LifeManaUpdatePacket(p)));
        _gameServer.OnReceivedPacketEvent(InComingPacket.WorldItemAction, p =>
            {
                var itemPacket = new ParseItemPacket(p);
                Data.ItemUpdate(itemPacket);
                ItemDroppedHandler?.Invoke(itemPacket.Item);
            });
        _gameServer.OnReceivedPacketEvent(InComingPacket.OwnedItemAction, p =>
            {
                var itemPacket = new ParseItemPacket(p);
                Data.ItemUpdate(itemPacket);
                ItemOwnedHandler?.Invoke(itemPacket.Item);
            });
        _gameServer.OnReceivedPacketEvent(InComingPacket.UseStackableItem, p => Data.UseStackableItem(new UseStackableItemPacket(p)));
        _gameServer.OnReceivedPacketEvent(InComingPacket.UpdateItemStats, p => Data.UpdateItemStats(new UpdateItemStatsPacket(p)));
        _gameServer.OnReceivedPacketEvent(InComingPacket.WaypointMenu, p => Data.UpdateWaypointInfo(new WaypointMenuPacket(p)));
        _gameServer.OnReceivedPacketEvent(InComingPacket.WalkVerify, p => CheckAndPreventDesync(new WalkVerifyPacket(p)));
        _gameServer.OnReceivedPacketEvent(InComingPacket.AddEntityEffect, p => Data.AddEntityEffect(new AddEntityEffectPacket(p)));
        _gameServer.OnReceivedPacketEvent(InComingPacket.AddEntityEffect2, p => Data.AddEntityEffect2(new AddEntityEffectPacket2(p)));
        _gameServer.OnReceivedPacketEvent(InComingPacket.UpdateEntityEffects, p => Data.UpdateEntityEffects(new UpdateEntityEffectsPacket(p)));
        _gameServer.OnReceivedPacketEvent(InComingPacket.CorpseAssign, p => Data.PlayerCorpseAssign(new CorpseAssignPacket(p)));
        _gameServer.OnReceivedPacketEvent(InComingPacket.PlayerStop, p => Data.PlayerStop(new PlayerStopPacket(p)));
        _gameServer.OnReceivedPacketEvent(InComingPacket.AssignMerc, p => Data.AssignMerc(new AssignMercPacket(p)));
        _gameServer.OnReceivedPacketEvent(InComingPacket.AllyPartyInfo, p => Data.UpdatePlayerPartyInfo(new AllyPartyInfoPacket(p)));
        _gameServer.OnReceivedPacketEvent(InComingPacket.PetAction, p => Data.UpdateSummonInfo(new PetActionPacket(p)));
        
    }

    public void OnWorldItemEvent(Func<Item, Task> handler)
        => ItemDroppedHandler = handler;
    public void OnOwnedItemEvent(Func<Item, Task> handler)
        => ItemOwnedHandler = handler;
    private void Initialize(GameFlags packet)
    {
        Data = new GameData(packet, selectedCharacter);
        _gameServer.Ping();
        chickenThread = new Thread(ChickenAndLifeManaThread) { Name = "GameClient Chicken Thread", IsBackground = true };
        chickenThread.Start();
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

        Log.Information("Leaving game");
        try
        {
            await _gameServer.LeaveGame();
        }
        finally
        {
            pingThread.Join();
            chickenThread.Join();
            await Task.Delay(1000);
        }
    }

    public bool TakeWaypoint(WorldObject worldObject, Waypoint waypoint)
    {
        if (!EntityConstants.WayPointEntityCodes.Contains(worldObject.Code))
        {
            throw new InvalidOperationException($"cannot take waypoint using worldObject which does belong to one of the waypoint entity codes, but has code { worldObject.Code} ");
        }

        var waypointMenuEvent = _gameServer.GetResetEventOfType(InComingPacket.WaypointMenu);
        Data.Me.LastSelectedWaypointId = 0;
        _gameServer.SendPacket(new InteractWithEntityPacket(worldObject));
        if(!waypointMenuEvent.WaitOne(2000))
        {
            return false;
        }

        if (!Data.Me.AllowedWaypoints.Contains(waypoint))
        {
            throw new InvalidOperationException($"cannot take waypoint {waypoint}, since character does not have it yet. List of available waypoints: {Data.Me.AllowedWaypoints}");
        }

        if (Data.Me.LastSelectedWaypointId == 0)
        {
            return false;
        }

        _gameServer.SendPacket(new TakeWaypointPacket(Data.Me.LastSelectedWaypointId, waypoint));
        return true;
    }
    public Self Me { get => Data.Me; }

    public List<Player> Players { get => Data.Players.ToList(); }

    public IReadOnlyDictionary<uint, Item> Items { get => Data.Items; }

    public Act Act { get => Data.Act.Act; }

    public Character ClientCharacter { get => Data.ClientCharacter; }

    public Area Area { get => Data.Act.Area; }

    public Difficulty Difficulty { get => Data.Flags.Difficulty; }

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

    public WarpData GetNearestWarp()
    {
        return Warps.Values.SelectMany(l => l).OrderBy(w => w.Location.Distance(Me.Location)).ThenByDescending(w => w.EntityId).FirstOrDefault();
    }

    public void RemoveItemFromContainer(Item item)
    {
        _gameServer.SendPacket(new RemoveItemFromContainerPacket(item));
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
        return buttonAction.WaitOne(2000);
    }

    public bool ActivateTomeOfIdentify(Item item)
    {
        if (item.Name != ItemName.TomeofIdentify)
        {
            throw new InvalidOperationException($"incorrect item type '{item.Name}', expected TomeofIdentify");
        }
        var stackableItem = _gameServer.GetResetEventOfType(InComingPacket.UseStackableItem);
        _gameServer.SendPacket(new ActivateBufferItemPacket(Me, item));
        return stackableItem.WaitOne(2000);
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

    public bool UseRightHandSkillOnLocation(Skill skill, Point location)
    {
        if (!ChangeSkill(skill, Hand.Right))
        {
            return false;
        }

        _gameServer.SendPacket(new RightSkillOnLocationPacket(location));
        return true;
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

    public async Task<bool> TeleportToLocationAsync(Point point)
    {
        if(Me.Location.Distance(point)<10)
        {
            return true;
        }

        var reAssignPlayer = _gameServer.GetResetEventOfType(InComingPacket.ReassignPlayer);
        UseRightHandSkillOnLocation(Skill.Teleport, point);
        return await reAssignPlayer.AsTask(TimeSpan.FromMilliseconds(200)) && Me.Location.Distance(point) < 10;
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
                return false;
            }
        }

        return true;
    }

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
        var entityEffect = _gameServer.GetResetEventOfType(InComingPacket.AddEntityEffect);
        _gameServer.SendPacket(new RightSkillOnUnitPacket(entity));
        return entityEffect.WaitOne(100);
    }

    public async Task<bool> CreateTownPortal()
    {
        var existingTownPortalId = GetEntityByCode(EntityCode.TownPortal).FirstOrDefault(t => t.TownPortalOwnerId == Me.Id)?.Id;
        var portalOwner = _gameServer.GetResetEventOfType(InComingPacket.PortalOwner);
        var cts = new CancellationTokenSource();
        cts.CancelAfter(500);
        UseRightHandSkillOnLocation(Skill.BookOfTownportal, Me.Location);
        do
        {
            if (await portalOwner.AsTask(TimeSpan.FromMilliseconds(500)))
            {
                var townPortal = GetEntityByCode(EntityCode.TownPortal).FirstOrDefault(t => t.TownPortalOwnerId == Me.Id);
                return townPortal != null && existingTownPortalId != townPortal.Id;
            };
            portalOwner = _gameServer.GetResetEventOfType(InComingPacket.PortalOwner);
        } while (!cts.IsCancellationRequested);
        
        var townPortalNearby = GetEntityByCode(EntityCode.TownPortal).FirstOrDefault(t => t.TownPortalOwnerId == Me.Id);
        return townPortalNearby != null && existingTownPortalId != townPortalNearby.Id;
    }

    public void InsertItemIntoContainer(Item item, Point location, ItemContainer container)
    {
        if (container == ItemContainer.Stash && location.Y > 7)
        {
            _gameServer.SendPacket(new InsertItemIntoContainerPacket(item, new Point(location.X, (ushort)(location.Y - 8)), ItemContainer.Stash2));
        }
        _gameServer.SendPacket(new InsertItemIntoContainerPacket(item, location, container));
    }

    public void ClickButton(ClickType clickType)
    {
        _gameServer.SendPacket(new ClickButtonPacket(clickType, 0));
    }

    public void MoveGoldToStash(int goldAmount)
    {
        _gameServer.SendPacket(new ClickButtonPacket(ClickType.MoveGoldFromInventoryToStash, goldAmount));
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
            Thread.Sleep((int)(120 / Data.WalkingSpeedMultiplier));
        }
        else
        {
            _gameServer.SendPacket(new RunToLocationPacket(location));
            Thread.Sleep((int)(distance * 80 / Data.WalkingSpeedMultiplier));
        }
        Me.Location = location;
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
            await Task.Delay((int)(120 / Data.WalkingSpeedMultiplier));
        }
        else
        {
            _gameServer.SendPacket(new RunToLocationPacket(location));
            await Task.Delay((int)(distance * 80 / Data.WalkingSpeedMultiplier));
        }
        Me.Location = location;
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
        await Task.Delay((int)(distance * 80 / Data.WalkingSpeedMultiplier));
        Me.Location = item.Location;
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
            Thread.Sleep((int)(120 / Data.WalkingSpeedMultiplier));
        }
        else
        {
            _gameServer.SendPacket(new RunToEntityPacket(entity));
            Thread.Sleep((int)(distance * 80 / Data.WalkingSpeedMultiplier));
        }
        Me.Location = entity.Location;
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
            await Task.Delay((int)(120 / Data.WalkingSpeedMultiplier));
        }
        else
        {
            _gameServer.SendPacket(new RunToEntityPacket(entity));
            await Task.Delay((int)(distance * 80 / Data.WalkingSpeedMultiplier));
        }
        Me.Location = entity.Location;
        return true;
    }

    public bool InteractWithNPC(Entity entity)
    {
        var npcInfoPacket = _gameServer.GetResetEventOfType(InComingPacket.NPCInfo);
        _gameServer.SendPacket(new MakeEntityMovePacket(Me, entity));
        Thread.Sleep(50);
        _gameServer.SendPacket(new InteractWithEntityPacket(entity));
        return npcInfoPacket.WaitOne(1500);
    }

    public bool PickupBody(Player player)
    {
        var corpseAssignPacket = _gameServer.GetResetEventOfType(InComingPacket.CorpseAssign);
        _gameServer.SendPacket(new InteractWithEntityPacket(player.Id, EntityType.Player));
        return corpseAssignPacket.WaitOne(1000);
    }

    public bool OpenStash(Entity stash)
    {
        var buttonActionPacket = _gameServer.GetResetEventOfType(InComingPacket.ButtonAction);
        _gameServer.SendPacket(new InteractWithEntityPacket(stash));
        return buttonActionPacket.WaitOne(1500);
    }

    public bool TakeWarp(WarpData warpData)
    {
        var reassignPlayerPacket = _gameServer.GetResetEventOfType(InComingPacket.ReassignPlayer);
        _gameServer.SendPacket(new InteractWithEntityPacket(warpData.EntityId, EntityType.Doorway));
        return reassignPlayerPacket.WaitOne(500);
    }

    public void InteractWithPlayer(Player player)
    {
        _gameServer.SendPacket(new InteractWithEntityPacket(player.Id, EntityType.Player));
    }

    public void InteractWithEntity(Entity entity)
    {
        _gameServer.SendPacket(new InteractWithEntityPacket(entity));
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
        _gameServer.SendPacket(new EntityActionPacket(entity, actionType));
    }

    private void RemoveNPCItems()
    {
        var npcItems = Data.Items.Where((p) => p.Value.IsInMerchantTab());
        foreach (var npcItem in npcItems)
        {
            Data.Items.TryRemove(npcItem.Key, out var value);
        }
    }

    public void IdentifyItems(Entity entity)
    {
        var nPCTransactionPacket = _gameServer.GetResetEventOfType(InComingPacket.NPCTransaction);
        _gameServer.SendPacket(new IdentifyItemsPacket(entity));
        nPCTransactionPacket.WaitOne(200);
    }

    public void IdentifyItem(Item bookOfIdentify, Item itemToIdentify)
    {
        var updateItemStats = _gameServer.GetResetEventOfType(InComingPacket.UpdateItemStats);
        _gameServer.SendPacket(new IdentifyItemPacket(bookOfIdentify, itemToIdentify));
        updateItemStats.WaitOne(200);
    }

    public void ResurrectMerc(Entity entity)
    {
        var assignMercPacket = _gameServer.GetResetEventOfType(InComingPacket.AssignMerc);
        _gameServer.SendPacket(new ResurrectMercPacket(entity));
        assignMercPacket.WaitOne(200);
    }

    public void RepairItems(Entity entity)
    {
        var nPCTransactionPacket = _gameServer.GetResetEventOfType(InComingPacket.NPCTransaction);
        _gameServer.SendPacket(new RepairPacket(entity));
        nPCTransactionPacket.WaitOne(200);
    }
    public void RequestUpdate(uint id)
    {
        _gameServer.SendPacket(new RequestUpdatePacket(id));
        Thread.Sleep(400);
    }

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
        return worldItemAction.WaitOne(2000);
    }

    public void SellItem(Entity entity, Item item)
    {
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
            while (IsInGame())
            {
                _gameServer.Ping();
                var pongPacket = _gameServer.GetResetEventOfType(InComingPacket.Pong);
                pongPacket.WaitOne(1000);
                Thread.Sleep(5000);
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
        var revpotion = Inventory.Items.FirstOrDefault(i => i.Classification == ClassificationType.RejuvenationPotion);
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

    private void ChickenAndLifeManaThread()
    {
        try
        {
            while (IsInGame())
            {
                if (Me == null || Area == Area.None)
                {
                    Thread.Sleep(50);
                    continue;
                }

                if (IsInTown())
                {
                    Thread.Sleep(50);
                    continue;
                }

                if (Me.Effects.ContainsKey(EntityEffect.Playerbody))
                {
                    Log.Information($"Leaving game since {Me.Name} has died");
                    LeaveGame().Wait();
                    break;
                }

                if (Me.GetLifeFraction() < 0.2 || (Me.Life < 200 && Me.MaxLife > 600))
                {
                    Log.Information($"{Me.Name} Leaving game due to life being {Me.Life} of max {Me.MaxLife}");
                    LeaveGame().Wait();
                    break;
                }

                if (Data.Flags.Hardcore && (Me.GetLifeFraction() < 0.4 || (Me.Life < 300 && Me.MaxLife > 600)))
                {
                    Log.Information($"{Me.Name} Leaving game due to life being {Me.Life} of max {Me.MaxLife}");
                    LeaveGame().Wait();
                    break;
                }

                if (Me.GetLifeFraction() < 0.9 && DateTime.Now.Subtract(LastUsedHealthPotionTime) > TimeSpan.FromSeconds(10))
                {
                    if (!UseHealthPotions())
                    {
                        UseRejuvenationPotion();
                    }
                }

                if (Me.GetLifeFraction() < 0.7 && DateTime.Now.Subtract(LastUsedHealthPotionTime) > TimeSpan.FromSeconds(2))
                {
                    if (!UseHealthPotions())
                    {
                        UseRejuvenationPotion();
                    }
                }

                if (Data.Flags.Hardcore && Me.GetLifeFraction() < 0.5 && DateTime.Now.Subtract(LastUsedHealthPotionTime) > TimeSpan.FromSeconds(0.1))
                {
                    if (!UseRejuvenationPotion())
                    {
                        Log.Information($"{Me.Name} Leaving game due out of rev potions");
                        LeaveGame().Wait();
                        break;
                    }
                }

                if (Me.GetLifeFraction() < 0.3 && DateTime.Now.Subtract(LastUsedHealthPotionTime) > TimeSpan.FromSeconds(0.7))
                {
                    if (!UseRejuvenationPotion() && !UseHealthPotions())
                    {
                        Log.Information($"{Me.Name} Leaving game due out of health/rev potions");
                        LeaveGame().Wait();
                        break;
                    }
                }

                if (Me.Mana < 40 && (Me.Attributes[D2GS.Players.Attribute.Level] > 40 || Me.Mana < 10) && DateTime.Now.Subtract(LastUsedManaPotionTime) > TimeSpan.FromSeconds(3))
                {
                    if (!UseManaPotion())
                    {
                        Log.Information($"{Me.Name} Leaving game due out of mana potions");
                        LeaveGame().Wait();
                        break;
                    }
                }

                Thread.Sleep(50);
            }
        }
        catch (Exception)
        {
        }
    }
}
