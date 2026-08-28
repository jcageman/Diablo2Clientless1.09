using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Helpers;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;
using D2NG.Core.D2GS.Packet.Outgoing;
using D2NG.Core.D2GS.Quest;
using System.Collections.Generic;
using Xunit;

namespace D2NG.Core.Tests.D2GS;

/// <summary>
/// Fixtures are the raw bytes of a 1.09 capture of a character killing Andariel and travelling to
/// act 2, so these tests pin the decoding to observed wire data rather than to a documented layout.
/// </summary>
public class QuestStateTests
{
    // 0x9C pushed just before the kill: act 1 quest 6 is 0x201A, started and boss alive.
    private const string BeforeAndarielDied =
        "2806000000000001000c0000000000000000001a2000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000";

    // 0x9C pushed the moment she died: 0x201A -> 0x2019, in progress bit out, completed bit in.
    private const string AfterAndarielDied =
        "2806000000000001000c000000000000000000192000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000";

    // 0x9C pushed after Warriv moved the character to act 2: the act 1 outro word opened at 0x2001.
    private const string AfterTravellingToAct2 =
        "2806000000000001000c000000000000400000192001200000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000";

    // 0x9D for the same game: only bit 13 of act 1 quest 6, meaning Andariel is dead in this game.
    private const string GameWithAndarielDead =
        "29000000000000000000000000002000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000";

    // The den of evil walked through every state in one capture: offered by Akara, cleared, claimed,
    // then acknowledged - which is what pins down what each bit means.
    private const string DenOffered = "28060000000000000010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000";
    private const string DenCleared = "28060000000000000012200000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000";
    private const string DenClaimed = "28017900000000000001200000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000";
    private const string DenAcknowledged = "28060000000000000001300000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000";

    [Fact]
    public void DenOfEvilWalksThroughEveryQuestState()
    {
        var state = new QuestState();

        state.UpdateCharacter(ParseCharacter(DenOffered));
        Assert.Equal(0x0010, state.GetCharacterFlags(QuestId.DenOfEvil));
        Assert.False(state.IsComplete(QuestId.DenOfEvil));
        Assert.False(state.IsCreditedThisGame(QuestId.DenOfEvil));

        // Clearing the den credits the character but grants nothing yet.
        state.UpdateCharacter(ParseCharacter(DenCleared));
        Assert.Equal(0x2012, state.GetCharacterFlags(QuestId.DenOfEvil));
        Assert.True(state.IsCreditedThisGame(QuestId.DenOfEvil));
        Assert.True(state.IsAwaitingReward(QuestId.DenOfEvil));
        Assert.False(state.IsComplete(QuestId.DenOfEvil));

        // Talking to Akara pays out the skill point and completes the quest.
        state.UpdateCharacter(ParseCharacter(DenClaimed));
        Assert.Equal(0x2001, state.GetCharacterFlags(QuestId.DenOfEvil));
        Assert.True(state.IsComplete(QuestId.DenOfEvil));
        Assert.False(state.IsAwaitingReward(QuestId.DenOfEvil));
        Assert.False(state.IsAcknowledged(QuestId.DenOfEvil));

        // Closing the log entry adds the flag the character keeps into later games.
        state.UpdateCharacter(ParseCharacter(DenAcknowledged));
        Assert.Equal(0x3001, state.GetCharacterFlags(QuestId.DenOfEvil));
        Assert.True(state.IsAcknowledged(QuestId.DenOfEvil));
        Assert.True(state.IsComplete(QuestId.DenOfEvil));
    }

    [Fact]
    public void CreditIsVisibleBeforeCompletionForARushedKill()
    {
        // Andariel died but no NPC had been talked to yet: a rush must treat this as credited.
        var state = new QuestState();
        state.UpdateCharacter(ParseCharacter(BeforeAndarielDied));

        Assert.True(state.IsCreditedThisGame(QuestId.SistersToTheSlaughter));
        Assert.False(state.IsComplete(QuestId.SistersToTheSlaughter));
    }

    [Fact]
    public void AndarielKillFlipsQuestToComplete()
    {
        var state = new QuestState();
        state.UpdateCharacter(ParseCharacter(BeforeAndarielDied));

        Assert.False(state.IsComplete(QuestId.SistersToTheSlaughter));
        Assert.True(state.IsAwaitingReward(QuestId.SistersToTheSlaughter));
        Assert.Equal(0x201A, state.GetCharacterFlags(QuestId.SistersToTheSlaughter));

        state.UpdateCharacter(ParseCharacter(AfterAndarielDied));

        Assert.True(state.IsComplete(QuestId.SistersToTheSlaughter));
        Assert.False(state.IsAwaitingReward(QuestId.SistersToTheSlaughter));
        Assert.Equal(0x2019, state.GetCharacterFlags(QuestId.SistersToTheSlaughter));
    }

    [Fact]
    public void UnrelatedQuestsStayIncomplete()
    {
        var state = new QuestState();
        state.UpdateCharacter(ParseCharacter(AfterAndarielDied));

        // The character had talked to Akara about the den without clearing it, so the quest is known
        // but not complete - the case the planner must not mistake for done.
        Assert.Equal(0x000C, state.GetCharacterFlags(QuestId.DenOfEvil));
        Assert.False(state.IsComplete(QuestId.DenOfEvil));
        Assert.False(state.IsComplete(QuestId.TheSearchForCain));
        Assert.False(state.IsComplete(QuestId.RadamentsLair));
    }

    [Fact]
    public void ActOneOutroOpensAfterTravel()
    {
        var state = new QuestState();
        state.UpdateCharacter(ParseCharacter(AfterAndarielDied));
        Assert.Equal(0, state.GetCharacterFlags(QuestId.Act1Outro));

        state.UpdateCharacter(ParseCharacter(AfterTravellingToAct2));
        Assert.Equal(0x2001, state.GetCharacterFlags(QuestId.Act1Outro));
        Assert.True(state.IsComplete(QuestId.Act1Outro));
    }

    [Fact]
    public void GameArrayReportsBossDeadInThisGame()
    {
        var state = new QuestState();
        Assert.False(state.IsCompletedInGame(QuestId.SistersToTheSlaughter));

        state.UpdateGame(ParseGame(GameWithAndarielDead));

        Assert.True(state.IsCompletedInGame(QuestId.SistersToTheSlaughter));
        // The game array says nothing about whether this character got credit.
        Assert.False(state.IsComplete(QuestId.SistersToTheSlaughter));
        Assert.False(state.IsCompletedInGame(QuestId.TheSevenTombs));
    }

    [Fact]
    public void ChangedEventNamesTheQuestsThatMoved()
    {
        var state = new QuestState();
        state.UpdateCharacter(ParseCharacter(BeforeAndarielDied));

        var reported = new List<QuestId>();
        state.Changed += quests => reported.AddRange(quests);
        state.UpdateCharacter(ParseCharacter(AfterAndarielDied));

        Assert.Equal([QuestId.SistersToTheSlaughter], reported);
    }

    [Fact]
    public void QuestInfoPacketReadsUpdateTypeAndUnitGid()
    {
        // Opening an NPC quest tab pushes the state with update type 1 and the NPC as unit gid,
        // where a plain refresh uses type 6 and gid 0.
        var packet = new QuestInfoPacket(new D2gsPacket(
            "2801080000000001000c000000000000000000192000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000".StringToByteArray()));

        Assert.Equal(1, packet.UpdateType);
        Assert.Equal(8U, packet.UnitGid);
    }

    [Fact]
    public void ResurrectPacketIsASingleCommandByte()
    {
        Assert.Equal("41".StringToByteArray(), new ResurrectPacket().Raw);
    }

    [Fact]
    public void RequestQuestDataPacketIsASingleCommandByte()
    {
        Assert.Equal("40".StringToByteArray(), new RequestQuestDataPacket().Raw);
    }

    [Fact]
    public void EntityActionSelectsTravelOnWarrivLeavingActOne()
    {
        var warriv = new WorldObject(EntityType.NPC, 8, EntityCode.TownPortal, new Point(0, 0), EntityState.Alive, 0);
        var packet = new EntityActionPacket(warriv, TownFolkActionType.Quest, EntityConstants.TravelMenuActionByAct[Act.Act1]);

        Assert.Equal("38000000000800000028000000".StringToByteArray(), packet.Raw);
        Assert.Equal(0U, packet.GetActionType());
        Assert.Equal(8U, packet.GetEntityId());
    }

    [Fact]
    public void EntityActionSelectsTravelOnMeshifLeavingActTwo()
    {
        // The travel value is per NPC, not a shared constant: Meshif needs 0x4B where Warriv took 0x28.
        var meshif = new WorldObject(EntityType.NPC, 164, EntityCode.TownPortal, new Point(0, 0), EntityState.Alive, 0);
        var packet = new EntityActionPacket(meshif, TownFolkActionType.Quest, EntityConstants.TravelMenuActionByAct[Act.Act2]);

        Assert.Equal("3800000000a40000004b000000".StringToByteArray(), packet.Raw);
    }

    [Fact]
    public void EntityActionKeepsTradeShapeForTownFolk()
    {
        var akara = new WorldObject(EntityType.NPC, 12, EntityCode.TownPortal, new Point(0, 0), EntityState.Alive, 0);
        var packet = new EntityActionPacket(akara, TownFolkActionType.Trade);

        Assert.Equal("38010000000c00000000000000".StringToByteArray(), packet.Raw);
    }

    private static byte[] ParseCharacter(string hex)
        => new QuestInfoPacket(new D2gsPacket(hex.StringToByteArray())).Quests;

    private static byte[] ParseGame(string hex)
        => new GameQuestInfoPacket(new D2gsPacket(hex.StringToByteArray())).Quests;
}
