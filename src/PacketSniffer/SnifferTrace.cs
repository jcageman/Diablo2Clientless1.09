using D2NG.Core.Capture;
using PacketDotNet;
using Serilog;
using System;

namespace PacketSniffer;

/// <summary>
/// Writes everything captured to trace files in the bot's format (source sniffer), beside the text log.
/// Each TCP direction is put back in order by sequence number before it is decoded, which the text
/// log's decoding never did.
/// </summary>
internal sealed class SnifferTrace : IDisposable
{
    private readonly TcpCapture _capture;
    private readonly CaptureOptions _options;

    public SnifferTrace(string root, CaptureOptions options)
    {
        _options = options;
        _capture = new TcpCapture(new CaptureSession(new CaptureTraceWriter(root))
        {
            Report = line => Log.Information("Trace: {Line}", line),
        });
    }

    public CaptureStats Stats => _capture.Session.Stats;

    public void Accept(IPPacket ip, TcpPacket tcp, DateTime capturedUtc)
    {
        if (!TryProtocol(tcp, out var protocol, out var fromServer))
        {
            return;
        }

        var server = fromServer ? $"{ip.SourceAddress}:{tcp.SourcePort}" : $"{ip.DestinationAddress}:{tcp.DestinationPort}";
        var client = fromServer ? $"{ip.DestinationAddress}:{tcp.DestinationPort}" : $"{ip.SourceAddress}:{tcp.SourcePort}";
        _capture.Accept(new CaptureConnection(client, server), protocol, fromServer, tcp.SequenceNumber, tcp.Synchronize, tcp.Acknowledgment, tcp.Finished, tcp.Reset, tcp.PayloadData ?? [], capturedUtc);
    }

    public void Dispose()
    {
        _capture.Dispose();
        var stats = Stats;
        Log.Information("Trace: {Game} game files ({MidGame} joined mid-game), {Realm} realm files, {Gaps} gaps, {BadFrames} undecodable frames, {Failures} files that failed, {Segments} segments that could not be traced, {Clamped} records whose capture time ran backwards",
            stats.GameFiles, stats.MidGameFiles, stats.RealmFiles, stats.Gaps, stats.BadFrames, stats.FileFailures, _capture.Failures, stats.ClampedRecords);
    }

    private bool TryProtocol(TcpPacket tcp, out CapturedProtocol protocol, out bool fromServer)
    {
        foreach (var (port, candidate) in new[] { (_options.D2gsPort, CapturedProtocol.D2gs), (_options.BncsPort, CapturedProtocol.Bncs), (_options.McpPort, CapturedProtocol.Mcp) })
        {
            if (tcp.SourcePort == port || tcp.DestinationPort == port)
            {
                protocol = candidate;
                fromServer = tcp.SourcePort == port;
                return true;
            }
        }

        protocol = default;
        fromServer = false;
        return false;
    }
}
