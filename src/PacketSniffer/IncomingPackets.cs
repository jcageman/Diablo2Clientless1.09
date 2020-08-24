using ConsoleBot.Pickit;
using D2NG.Core.D2GS.Helpers;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;
using Serilog;
using System;

namespace PacketSniffer
{
    public static class IncomingPackets
    {
        public static void HandleIncomingPacket(D2gsPacket eventArgs)
        {
            if (!Enum.IsDefined(typeof(InComingPacket), eventArgs.Type))
            {
                Log.Information($"Received unknown D2GS packet of type: 0x{(byte)eventArgs.Type,2:X2} with data {eventArgs.Raw.ToPrintString()}");
                return;
            }

            var incomingPacketType = (InComingPacket)eventArgs.Type;
            switch (incomingPacketType)
            {
                case InComingPacket.GameLoading:
                case InComingPacket.GameFlags:
                case InComingPacket.LoadSuccessful:
                case InComingPacket.LoadActComplete:
                case InComingPacket.UnloadActComplete:
                case InComingPacket.GameExitSuccess:
                case InComingPacket.MapHide:
                case InComingPacket.GameHandshake:
                case InComingPacket.PlayerStop:
                case InComingPacket.PlayerToTarget:
                case InComingPacket.ReportKill:
                case InComingPacket.Recv12:
                case InComingPacket.Recv13:
                case InComingPacket.Recv14:
                case InComingPacket.GoldToInventory:
                case InComingPacket.AddExperienceByte:
                case InComingPacket.AddExperienceWord:
                case InComingPacket.AddExperienceDword:
                case InComingPacket.UpdateItemOSkill:
                case InComingPacket.UpdateItemSkill:
                case InComingPacket.SetSkill:
                case InComingPacket.Recv24:
                case InComingPacket.Recv25:
                case InComingPacket.NPCInfo:
                case InComingPacket.NPCTransaction:
                case InComingPacket.PlaySound:
                case InComingPacket.UpdateItemStats:
                case InComingPacket.UseStackableItem:
                case InComingPacket.Recv40:
                case InComingPacket.ClearCursor:
                case InComingPacket.Recv45:
                case InComingPacket.Relator1:
                case InComingPacket.Relator2:
                case InComingPacket.UseSkillOnTarget:
                case InComingPacket.UseSkillOnPoint:
                case InComingPacket.MercForHire:
                case InComingPacket.StartMercList:
                case InComingPacket.Recv50:
                case InComingPacket.QuestLogInfo:
                case InComingPacket.PlayerSlotRefresh:
                case InComingPacket.Recv54:
                case InComingPacket.Recv58:
                case InComingPacket.Recv5E:
                case InComingPacket.Recv5F:
                case InComingPacket.Recv61:
                case InComingPacket.Recv62:
                case InComingPacket.WaypointMenu:
                case InComingPacket.PlayerKillCount:
                case InComingPacket.Recv66:
                case InComingPacket.NPCMove:
                case InComingPacket.NPCMoveToTarget:
                case InComingPacket.Recv6A:
                case InComingPacket.NPCAction:
                case InComingPacket.NPCAttack:
                case InComingPacket.NPCStop:
                case InComingPacket.Recv6E:
                case InComingPacket.Recv6F:
                case InComingPacket.Recv70:
                case InComingPacket.Recv71:
                case InComingPacket.Recv72:
                case InComingPacket.Recv73:
                case InComingPacket.PlayerCorpseAssign:
                case InComingPacket.PlayerPartyInfo:
                case InComingPacket.PlayerInProximity:
                case InComingPacket.ButtonAction:
                case InComingPacket.TradeAccepted:
                case InComingPacket.GoldInTrade:
                case InComingPacket.PetAction:
                case InComingPacket.AssignHotkey:
                case InComingPacket.UseScroll:
                case InComingPacket.SetItemState:
                case InComingPacket.Recv7E:
                case InComingPacket.AllyPartyInfo:
                case InComingPacket.AssignMerc:
                case InComingPacket.PortalOwner:
                case InComingPacket.Recv86:
                case InComingPacket.SpecialQuestEvent:
                case InComingPacket.NPCWantInteract:
                case InComingPacket.PlayerRelationship:
                case InComingPacket.RelationshipUpdate:
                case InComingPacket.AssignPlayerToParty:
                case InComingPacket.CorpseAssign:
                case InComingPacket.Pong:
                case InComingPacket.PartyAutomapInfo:
                case InComingPacket.Recv91:
                case InComingPacket.Recv92:
                case InComingPacket.Recv93:
                case InComingPacket.Recvad:
                case InComingPacket.Recvb1:
                case InComingPacket.BaseSkillLevels:
                case InComingPacket.WeaponSwitch:
                case InComingPacket.Recv98:
                case InComingPacket.SkillTriggered:
                case InComingPacket.Recv9A:
                case InComingPacket.Recv9B:
                case InComingPacket.MercAttributeByte:
                case InComingPacket.MercAttributeWord:
                case InComingPacket.MercAttributeDword:
                case InComingPacket.AddMercExperienceByte:
                case InComingPacket.AddMercExperienceWord:
                case InComingPacket.Recva3:
                case InComingPacket.Recva4:
                case InComingPacket.ChargeupActive:
                case InComingPacket.DelayedState:
                case InComingPacket.LeaveGameConfirmed:
                case InComingPacket.EndState:
                case InComingPacket.AddUnit:
                case InComingPacket.NPCHeal:
                case InComingPacket.QuestInfo:
                case InComingPacket.GameQuestInfo:
                    Log.Debug($"Received D2GS packet of type: {incomingPacketType} with data {eventArgs.Raw.ToPrintString()}");
                    break;
                case InComingPacket.MapReveal:
                    var mapRevealPacket = new MapRevealPacket(eventArgs);
                    Log.Information($"MapReveal -> At {mapRevealPacket.Area} Loc: {mapRevealPacket.X}, {mapRevealPacket.Y}");
                    break;
                case InComingPacket.NPCHit:
                    var npcHitPacket = new NpcHitPacket(eventArgs);
                    Log.Information($"NPC Hit -> EntityId: {npcHitPacket.EntityId} Type: {npcHitPacket.EntityType} LifePercentage: {npcHitPacket.LifePercentage} data {eventArgs.Raw.ToPrintString()}");
                    break;
                case InComingPacket.AssignLevelWarp:
                    var assignLevelWarpPacket = new AssignLevelWarpPacket(eventArgs);
                    Log.Information($"AssignLevelWarp -> EntityId: {assignLevelWarpPacket.EntityId} Loc: {assignLevelWarpPacket.Location} WarpId: {assignLevelWarpPacket.WarpId} data {eventArgs.Raw.ToPrintString()}");
                    break;
                case InComingPacket.LoadAct:
                    var loadActPacket = new ActDataPacket(eventArgs);
                    Log.Information($"LoadAct -> Act: {loadActPacket.Act} Area: {loadActPacket.Area} MapId: {loadActPacket.MapId}");
                    break;
                case InComingPacket.AddAttributeByte:
                case InComingPacket.AddAttributeWord:
                case InComingPacket.AddAttributeDword:
                    var addAttributePacket = new BaseAttributePacket(eventArgs);
                    Log.Information($"AddAttribute -> {addAttributePacket.Attribute} {addAttributePacket.Value}");
                    break;
                case InComingPacket.AssignNPC2:
                    var assignNpcPacket = new AssignNpcPacket(eventArgs);
                    Log.Information($"AssignNPC2 -> ID: {assignNpcPacket.EntityId} Unique Code: {assignNpcPacket.UniqueCode} Location: {assignNpcPacket.Location}");
                    break;
                case InComingPacket.AssignNPC1:
                    var assignTownNpcPacket = new AssignNpcPacket(eventArgs);
                    Log.Information($"AssignNPC1 -> ID: {assignTownNpcPacket.EntityId} Unique Code: {assignTownNpcPacket.UniqueCode} Location: {assignTownNpcPacket.Location}");
                    break;
                case InComingPacket.NPCState:
                    var npcStatePacket = new NpcStatePacket(eventArgs);
                    Log.Information($"NPCState -> ID: {npcStatePacket.EntityId} State: {npcStatePacket.EntityState} Location: {npcStatePacket.Location}");
                    break;
                case InComingPacket.ObjectState:
                    var objectStatePacket = new ObjectStatePacket(eventArgs);
                    Log.Debug($"ObjectState -> ID: {objectStatePacket.ObjectId} Type: {objectStatePacket.ObjectType} State: {objectStatePacket.State}");
                    break;
                case InComingPacket.RemoveObject:
                    var removeObjectPacket = new RemoveObjectPacket(eventArgs);
                    Log.Debug($"RemoveObject -> ID: {removeObjectPacket.EntityId} Type: {removeObjectPacket.EntityType}");
                    break;
                case InComingPacket.AssignObject:
                    var assignObjectPacket = new AssignObjectPacket(eventArgs);
                    Log.Debug($"AssignObject -> ID: {assignObjectPacket.EntityId} Code: {assignObjectPacket.ObjectCode} Type: {assignObjectPacket.EntityType} Location: {assignObjectPacket.Location} with data {eventArgs.Raw.ToPrintString()}");
                    break;
                case InComingPacket.ReassignPlayer:
                    var reassignPlayerPacket = new ReassignPlayerPacket(eventArgs);
                    Log.Information($"ReassignPlayer -> UnitType: {reassignPlayerPacket.UnitType} UnitId: {reassignPlayerPacket.UnitId} Location: {reassignPlayerPacket.Location}");
                    break;
                case InComingPacket.LifeManaUpdatePot:
                    var LifeManaUpdatePotPacket = new LifeManaPotUpdatePacket(eventArgs);
                    Log.Information($"LifeManaUpdatePot -> Life: {LifeManaUpdatePotPacket.Life} Mana: {LifeManaUpdatePotPacket.Mana} Location: {LifeManaUpdatePotPacket.Location}");
                    break;
                case InComingPacket.LifeManaUpdate:
                    var lifeManaUpdatePacket = new LifeManaUpdatePacket(eventArgs);
                    Log.Information($"LifeManaUpdate -> Life: {lifeManaUpdatePacket.Life} Mana: {lifeManaUpdatePacket.Mana} Location: {lifeManaUpdatePacket.Location}");
                    break;
                case InComingPacket.WorldItemAction:
                case InComingPacket.OwnedItemAction:
                    var parseItemPacket = new ParseItemPacket(eventArgs);
                    Log.Information($"{incomingPacketType} -> Id: {parseItemPacket.Item.Id} Action: {parseItemPacket.Item.Action} Container: {parseItemPacket.Item.Container} Quality: {parseItemPacket.Item.Quality} Name: {parseItemPacket.Item.Name} Type: {parseItemPacket.Item.Type} Location: {parseItemPacket.Item.Location} ShouldKeep: {Pickit.ShouldKeepItem(parseItemPacket.Item)}");
                    break;
                case InComingPacket.ReceiveChat:
                    var chatPacket = new ChatPacket(eventArgs);
                    Log.Information($"ChatPacket -> Character: {chatPacket.CharacterName} Message: {chatPacket.Message}");
                    break;
                case InComingPacket.WalkVerify:
                    var walkVerifyPacket = new WalkVerifyPacket(eventArgs);
                    Log.Information($"WalkVerify -> Location: {walkVerifyPacket.Location}");
                    break;
                case InComingPacket.PlayerInGame:
                    var playerInGamePacket = new PlayerInGamePacket(eventArgs);
                    Log.Information($"PlayerInGame -> Id: {playerInGamePacket.Id} Name: {playerInGamePacket.Name} Class: {playerInGamePacket.Class}");
                    break;
                case InComingPacket.PlayerLeftGame:
                    var playerLeftGamePacket = new PlayerLeftGamePacket(eventArgs);
                    Log.Information($"PlayerLeftGame -> Id: {playerLeftGamePacket.Id}");
                    break;
                case InComingPacket.TownPortalState:
                case InComingPacket.EntityMove:
                case InComingPacket.AssignPlayer:
                case InComingPacket.EventMessage:
                case InComingPacket.QuestItemState:
                case InComingPacket.WardenRequest:
                case InComingPacket.GameDropped:
                case InComingPacket.IPBan:
                case InComingPacket.AttributeUpdate:
                    Log.Information($"Received D2GS packet of type: {incomingPacketType} with data {eventArgs.Raw.ToPrintString()}");
                    break;
            }
        }
    }
}
