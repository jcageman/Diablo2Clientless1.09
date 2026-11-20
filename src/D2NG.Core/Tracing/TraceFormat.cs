using System;
using System.Buffers.Binary;
using System.Text;

namespace D2NG.Core.Tracing;

public enum TraceSource : byte
{
    Bot = 0,
    Sniffer = 1,
}

public enum TraceRecordKind : byte
{
    D2gsReceived = 1,
    D2gsSent = 2,
    BncsReceived = 3,
    BncsSent = 4,
    McpReceived = 5,
    McpSent = 6,
    Digest = 16,
    Gap = 17,
    ClientAction = 18,

    /// <summary>
    /// The D2gsSent record right after it was given to this client without evidence: a text capture names no
    /// connection for what was sent, and its import had to guess. One payload byte, 0. Readers that do not know
    /// the kind skip it by its length.
    /// </summary>
    SentWithoutEvidence = 19,
}

/// <summary>
/// Client state a <see cref="Game"/> method sets on its own, which no packet the server sends would
/// reproduce in replay. Recorded with one u32 argument: an item id, or a position as x | y &lt;&lt; 16.
/// </summary>
public enum ClientAction : byte
{
    Lift = 1,
    Use = 2,
    Sell = 3,
    ForgetWaypointSelection = 4,
    RemoveMerchantItems = 5,
    Move = 6,

    /// <summary>
    /// The server emptied the client's cursor (0x42): the Horadric Staff put into the orifice leaves it that way. The
    /// bot never records it - its game does not handle 0x42 - so only a real client's replay derives it.
    /// </summary>
    ClearCursor = 7,

    /// <summary>
    /// The bot asked a vendor for a trade, gamble or refreshed gamble screen; the argument is the vendor's id. From the
    /// first one on, a listing that arrives while no shop is open is not kept. A trace without it keeps every listing,
    /// as the bot that recorded it did.
    /// </summary>
    OpenShop = 8,
}

/// <summary>
/// The .d2trace layout, little-endian throughout.
///
/// Header: magic "D2TR", u16 format version, u16 header length (the offset of the first record),
/// u8 source, u8 reserved, i64 start as UTC <see cref="DateTime.Ticks"/>, then two strings - client
/// label and game name - each a u16 byte count followed by UTF-8 - and, appended later, the game
/// server's id for the game in the same form (absent in older files), then, appended later again, u16 client
/// rules version (<see cref="RulesNotApplicable"/> for a real client's capture; a file without it was made
/// under rules 0), the recorder's build as a string (<see cref="BuildIdentity"/>) and i64 that build's UTC
/// ticks (0 unknown). A reader skips to the header length, so later versions can append header fields.
///
/// Record: u16 payload length, u8 kind, i64 elapsed since the file start in 100 ns ticks, payload.
/// Unknown kinds are skipped by their length.
/// </summary>
public static class TraceFormat
{
    public static ReadOnlySpan<byte> Magic => "D2TR"u8;

    public const ushort Version = 1;

    public const int RecordHeaderSize = 11;

    public const int MaxPayload = ushort.MaxValue;

    public const string Extension = ".d2trace";

    /// <summary>The rules version a real client's capture carries: a real client interprets packets by no rules of ours.</summary>
    public const ushort RulesNotApplicable = ushort.MaxValue;

    /// <param name="rulesVersion">Null: <see cref="ClientRules.LatestVersion"/> for the bot, <see cref="RulesNotApplicable"/> for a sniffer.</param>
    public static byte[] Header(TraceSource source, DateTime startUtc, string clientLabel, string gameName, string serverGame = null, ushort? rulesVersion = null)
    {
        var label = Encoding.UTF8.GetBytes(clientLabel ?? "");
        var game = Encoding.UTF8.GetBytes(gameName ?? "");
        var server = Encoding.UTF8.GetBytes(serverGame ?? "");
        var build = Encoding.UTF8.GetBytes(BuildIdentity.Version);
        var length = 18 + 2 + label.Length + 2 + game.Length + 2 + server.Length + 2 + 2 + build.Length + 8;
        var header = new byte[length];
        Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), Version);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), (ushort)length);
        header[8] = (byte)source;
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(10), startUtc.Ticks);
        var offset = 18;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(offset), (ushort)label.Length);
        label.CopyTo(header, offset + 2);
        offset += 2 + label.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(offset), (ushort)game.Length);
        game.CopyTo(header, offset + 2);
        offset += 2 + game.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(offset), (ushort)server.Length);
        server.CopyTo(header, offset + 2);
        offset += 2 + server.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(offset), rulesVersion ?? (source == TraceSource.Bot ? (ushort)ClientRules.LatestVersion : RulesNotApplicable));
        offset += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(offset), (ushort)build.Length);
        build.CopyTo(header, offset + 2);
        offset += 2 + build.Length;
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(offset), BuildIdentity.Utc?.Ticks ?? 0);
        return header;
    }

    public static void WriteRecordHeader(Span<byte> destination, TraceRecordKind kind, long elapsedTicks, int payloadLength)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(destination, (ushort)payloadLength);
        destination[2] = (byte)kind;
        BinaryPrimitives.WriteInt64LittleEndian(destination[3..], elapsedTicks);
    }
}
