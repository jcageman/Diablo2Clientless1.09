using D2NG.Core.D2GS.Helpers;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Outgoing;
using Serilog;
using System;

namespace PacketSniffer
{
    public static class OutgoingPackets
    {
        public static void HandleOutgoingPacket(byte[] bytes)
        {
            if (!Enum.IsDefined(typeof(OutGoingPacket), bytes[0]))
            {
                Log.Information($"Send unknown D2GS packet of type: 0x{(byte)bytes[0],2:X2} with data {bytes.ToPrintString()}");
                return;
            }

            var packetType = (OutGoingPacket)bytes[0];
            switch (packetType)
            {
                case OutGoingPacket.Run:
                    var runToLocationPacket = new RunToLocationPacket(bytes);
                    Log.Information($"RunToLocationPacket -> {runToLocationPacket.GetLocation()}");
                    break;
                case OutGoingPacket.BuyItem:
                    var buyItemPacket = new BuyItemPacket(bytes);
                    Log.Information($"BuyItem -> {buyItemPacket.GetItemId()} with data {bytes.ToPrintString()}");
                    break;
                case OutGoingPacket.SellItem:
                    var sellItemPacket = new SellItemPacket(bytes);
                    Log.Information($"SellItem -> {sellItemPacket.GetItemId()}");
                    break;
                case OutGoingPacket.EntityInteract:
                    var interactWithEntityPacket = new InteractWithEntityPacket(bytes);
                    Log.Information($"EntityInteract -> ID: {interactWithEntityPacket.GetEntityId()} Type: {interactWithEntityPacket.GetEntityType()}");
                    Log.Debug($"{bytes.ToPrintString()}");
                    break;
                case OutGoingPacket.Walk:
                case OutGoingPacket.WalkToUnit:
                case OutGoingPacket.RunToUnit:
                case OutGoingPacket.ShiftLeftClick:
                case OutGoingPacket.LeftClickUnit:
                case OutGoingPacket.ShiftLeftClickUnit:
                case OutGoingPacket.ShiftLeftClickHold:
                case OutGoingPacket.LeftClickHoldUnit:
                case OutGoingPacket.ShiftLeftClickHoldUnit:
                case OutGoingPacket.RightSkillOnLocation:
                case OutGoingPacket.RightSkillOnUnit:
                case OutGoingPacket.ShiftRightClickUnit:
                case OutGoingPacket.RightSkillRepeatOnLocation:
                case OutGoingPacket.RightClickHoldUnit:
                case OutGoingPacket.ShiftRightClickHoldUnit:
                case OutGoingPacket.OverheadChat:
                case OutGoingPacket.Chat:
                case OutGoingPacket.PickItem:
                case OutGoingPacket.DropItem:
                case OutGoingPacket.InsertItemToBuffer:
                case OutGoingPacket.RemoveItemFromBuffer:
                case OutGoingPacket.EquipItem:
                case OutGoingPacket.Swap2HItem:
                case OutGoingPacket.RemoveBodyItem:
                case OutGoingPacket.SwapCursorWithBody:
                case OutGoingPacket.Swap1HItemWith2HItem:
                case OutGoingPacket.SwapCursorAndBufferItem:
                case OutGoingPacket.ActivateBufferItem:
                case OutGoingPacket.StackItems:
                case OutGoingPacket.UnstackItems:
                case OutGoingPacket.ItemToBelt:
                case OutGoingPacket.ItemFromBelt:
                case OutGoingPacket.SwapBeltItem:
                case OutGoingPacket.UseBeltItem:
                case OutGoingPacket.IdentifyItem:
                case OutGoingPacket.SocketItem:
                case OutGoingPacket.ScrollToBook:
                case OutGoingPacket.ItemToCube:
                case OutGoingPacket.InitiateEntityChat:
                case OutGoingPacket.TerminateEntityChat:
                case OutGoingPacket.QuestMessage:
                case OutGoingPacket.IdentifyItems:
                case OutGoingPacket.Repair:
                case OutGoingPacket.HireMerc:
                case OutGoingPacket.IdentifyFromGamble:
                case OutGoingPacket.EntityAction:
                case OutGoingPacket.PurchaseLife:
                case OutGoingPacket.AddStatPoint:
                case OutGoingPacket.AddSkillPoint:
                case OutGoingPacket.SelectSkill:
                case OutGoingPacket.HighlightDoor:
                case OutGoingPacket.ActivateInifussScroll:
                case OutGoingPacket.PlayAudio:
                case OutGoingPacket.RequestQuestData:
                case OutGoingPacket.Resurrect:
                case OutGoingPacket.InsertHoradricStaff:
                case OutGoingPacket.HaveMercInteract:
                case OutGoingPacket.MoveMerc:
                case OutGoingPacket.TurnOffBusy:
                case OutGoingPacket.TakeWaypoint:
                case OutGoingPacket.RequestEntityUpdate:
                case OutGoingPacket.Transmute:
                case OutGoingPacket.PlayNPCMessage:
                case OutGoingPacket.ClickButton:
                case OutGoingPacket.DropGold:
                case OutGoingPacket.BindHotkey:
                case OutGoingPacket.QuestComplete:
                case OutGoingPacket.MakeEntityMove:
                case OutGoingPacket.SetPlayerRelation:
                case OutGoingPacket.InvitePlayer:
                case OutGoingPacket.UpdatePlayerLocation:
                case OutGoingPacket.SwapWeapons:
                case OutGoingPacket.DropOrEquipMercItem:
                case OutGoingPacket.ResurrectMerc:
                case OutGoingPacket.SendItemToBelt:
                case OutGoingPacket.GameLogon:
                case OutGoingPacket.LeaveGame:
                case OutGoingPacket.Startup:
                case OutGoingPacket.JoinGame:
                case OutGoingPacket.Ping:
                    Log.Information($"Outgoing packet {packetType}");
                    Log.Debug($"{bytes.ToPrintString()}");
                    break;
            }
        }
    }
}
