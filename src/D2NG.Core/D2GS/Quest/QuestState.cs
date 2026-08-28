using System;
using System.Collections.Generic;
using System.Linq;

namespace D2NG.Core.D2GS.Quest;

/// <summary>
/// Quest progress of this character and of the game it is in, decoded from the 0x9C and 0x9D quest
/// packets. The bot reads this to work out what a character still needs, which is what makes a rush
/// resumable: the remaining work is derived from the flags rather than from a script position.
/// </summary>
/// <remarks>
/// Bit meanings come from live 1.09 captures of Andariel dying and of the den of evil being cleared
/// and claimed, which walked one word through every state: 0x0010 offered, 0x2012 cleared, 0x2001
/// claimed, 0x3001 acknowledged.
/// <list type="bullet">
/// <item>bit 0 - the quest is complete, which only happens once the reward NPC has been talked to.</item>
/// <item>bit 1 - the deed is done but the reward has not been collected yet.</item>
/// <item>bit 12 - completion the character keeps: present without bit 13 on characters that
/// finished the quest in an earlier game.</item>
/// <item>bit 13 - the deed happened in <em>this</em> game. This is the credit signal a rush waits
/// on, because a rushee gets it from the kill itself, long before it visits an NPC. Confirmed live:
/// a level one character standing in Andariel's level while somebody else killed her went to 0x201A,
/// credited and awaiting its reward, without landing a blow.</item>
/// <item>bit 15, on the game array only - the game's creator had already finished the quest. Nobody
/// in such a game can be credited, which is why a rush is hosted by the character being rushed.</item>
/// </list>
/// </remarks>
public class QuestState
{
    /// <summary>Number of words in the quest array, six acts of eight words each.</summary>
    public const int WordCount = 48;

    private const ushort CompletedBit = 0x0001;
    private const ushort InProgressBit = 0x0002;
    private const ushort AcknowledgedBit = 0x1000;
    private const ushort DoneThisGameBit = 0x2000;
    private const ushort HostCompletedBit = 0x8000;

    private readonly ushort[] _character = new ushort[WordCount];
    private readonly ushort[] _game = new ushort[WordCount];

    /// <summary>
    /// Raised after a quest packet changed one or more words, with the ids that moved. Lets the bot
    /// await a flag flip instead of polling after a kill or an NPC interaction.
    /// </summary>
    public event Action<IReadOnlyCollection<QuestId>> Changed;

    /// <summary>Whether this character has completed the quest.</summary>
    public bool IsComplete(QuestId quest) => (GetCharacterFlags(quest) & CompletedBit) != 0;

    /// <summary>Whether the deed is done but its reward has not been collected from the NPC yet.</summary>
    public bool IsAwaitingReward(QuestId quest) => (GetCharacterFlags(quest) & InProgressBit) != 0;

    /// <summary>
    /// Whether this character got credit for the deed in the current game, which the kill or the
    /// clear sets on its own. This is the signal a rush waits on after a boss dies: the completed
    /// flag needs an NPC visit that a rushee has usually not made yet.
    /// </summary>
    public bool IsCreditedThisGame(QuestId quest) => (GetCharacterFlags(quest) & DoneThisGameBit) != 0;

    /// <summary>
    /// Whether the character carries completion from an earlier game, as opposed to credit earned in
    /// this one.
    /// </summary>
    public bool IsAcknowledged(QuestId quest) => (GetCharacterFlags(quest) & AcknowledgedBit) != 0;

    /// <summary>
    /// What the game wide array says about a quest. Treat with care: it carries the quest state of
    /// whoever created the game rather than a log of what has died in it.
    /// </summary>
    /// <remarks>
    /// Measured behaviour: in a game hosted by a character that had finished the quest, the word read
    /// 0x8000 before anything was killed and never moved. In a game hosted by a fresh character, the
    /// array stayed empty even as the boss died and the watching character was credited. So this is
    /// not a kill signal - <see cref="IsCreditedThisGame"/> on the character is.
    /// </remarks>
    public bool IsCompletedInGame(QuestId quest) => (GetGameFlags(quest) & DoneThisGameBit) != 0;

    /// <summary>
    /// Whether the game was created by a character that had already finished this quest, which stops
    /// anyone in it from being credited: the quest is settled for that game before it starts. A rush
    /// has to be hosted by a character that still needs the quest.
    /// </summary>
    public bool IsSettledForGame(QuestId quest) => (GetGameFlags(quest) & HostCompletedBit) != 0;

    public ushort GetCharacterFlags(QuestId quest) => Read(_character, quest);

    public ushort GetGameFlags(QuestId quest) => Read(_game, quest);

    public ushort[] CharacterWords => [.. _character];

    public ushort[] GameWords => [.. _game];

    internal void UpdateCharacter(byte[] questBytes) => Update(_character, questBytes);

    internal void UpdateGame(byte[] questBytes) => Update(_game, questBytes);

    private void Update(ushort[] target, byte[] questBytes)
    {
        ArgumentNullException.ThrowIfNull(questBytes);

        var changed = new List<QuestId>();
        for (var word = 0; word < WordCount && (word * 2) + 1 < questBytes.Length; word++)
        {
            var value = (ushort)(questBytes[word * 2] | (questBytes[(word * 2) + 1] << 8));
            if (target[word] == value)
            {
                continue;
            }

            target[word] = value;
            if (Enum.IsDefined(typeof(QuestId), word))
            {
                changed.Add((QuestId)word);
            }
        }

        if (changed.Count != 0)
        {
            Changed?.Invoke(changed);
        }
    }

    private static ushort Read(ushort[] words, QuestId quest)
    {
        var index = (int)quest;
        return index >= 0 && index < WordCount ? words[index] : (ushort)0;
    }

    /// <summary>
    /// Compact dump of every non zero word, for logging a state or a transition without printing all
    /// 48 words.
    /// </summary>
    public string Describe(bool game = false)
    {
        var words = game ? _game : _character;
        var set = Enumerable.Range(0, WordCount)
            .Where(i => words[i] != 0)
            .Select(i => Enum.IsDefined(typeof(QuestId), i)
                ? $"{(QuestId)i}=0x{words[i]:X4}"
                : $"w{i:D2}=0x{words[i]:X4}");
        return string.Join(" ", set);
    }
}
