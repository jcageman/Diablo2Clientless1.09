using D2NG.Core.Tracing;
using D2NG.Trace.Query;
using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using Xunit;

namespace D2NG.Trace.Tests;

public class MalformedTraceTests
{
    private static byte[] ValidHeader() => TraceFormat.Header(TraceSource.Bot, DateTime.UtcNow, "client 1", "game-1");

    private static byte[] With(Action<byte[]> change)
    {
        var header = ValidHeader();
        change(header);
        return header;
    }

    public static TheoryData<string, byte[]> Malformed()
    {
        var labelLengthOffset = 18;
        var gameLengthOffset = 18 + 2 + "client 1".Length;
        return new TheoryData<string, byte[]>
        {
            { "empty", [] },
            { "magic only", [.. "D2TR"u8] },
            { "other magic", With(h => h[0] = (byte)'X') },
            { "header length below the fixed fields", With(h => BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(6), 10)) },
            { "header length past the end", With(h => BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(6), 500)) },
            { "start time out of range", With(h => BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(10), long.MaxValue)) },
            { "label past the header", With(h => BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(labelLengthOffset), ushort.MaxValue)) },
            { "label over the game length", With(h => { BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(labelLengthOffset), (ushort)("client 1".Length + "game-1".Length + 1)); BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(6), (ushort)(gameLengthOffset + 2 + "game-1".Length + 2)); }) },
            { "game past the header", With(h => BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(gameLengthOffset), 200)) },
        };
    }

    [Theory]
    [MemberData(nameof(Malformed))]
    public void AMalformedHeaderThrowsATraceFormatException(string what, byte[] file)
    {
        Assert.NotNull(what);
        Assert.Throws<TraceFormatException>(() => new TraceReader(new MemoryStream(file)));
    }

    [Fact]
    public void AnUnreadableFileIsSkippedAndTheRestOfTheGameFolderOpens()
    {
        using var root = new TempRoot();
        using var recorder = root.Recorder();
        var channel = recorder.OpenGame("game-1", "client 1");
        channel.Write(TraceRecordKind.D2gsReceived, new byte[] { 0x01 });
        channel.Close();
        recorder.FlushNow();
        var folder = Path.GetDirectoryName(channel.Path);
        var empty = Path.Combine(folder, "client 2.d2trace");
        File.WriteAllBytes(empty, []);
        var broken = Path.Combine(folder, "client 3.d2trace");
        File.WriteAllBytes(broken, With(h => BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(18), ushort.MaxValue)));

        var game = GameTrace.Open(folder);

        Assert.Equal(["client 1"], game.Labels);
        Assert.Equal([empty, broken], game.Skipped.Select(s => s.Path));
    }

    [Fact]
    public void AGameFolderOfOnlyUnreadableFilesSaysWhy()
    {
        using var root = new TempRoot();
        var folder = Path.Combine(root.Path, "20260101", "game-1");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "client 1.d2trace"), []);

        var error = Assert.ThrowsAny<InvalidDataException>(() => GameTrace.Open(folder));
        Assert.Contains("no readable", error.Message);
    }
}
