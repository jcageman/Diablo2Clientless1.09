using D2NG.Core.Extensions;
using D2NG.Core.MCP.Packet;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace D2NG.Core.MCP;

internal class McpEvent : IDisposable
{
    private readonly ManualResetEvent _event = new(false);

    public McpPacket _packet;

    public void Reset()
    {
        _event.Reset();
        _packet = null;
    }

    public async Task<McpPacket> WaitForPacket(int millisecondsTimeout)
    {
        await _event.AsTask(TimeSpan.FromMilliseconds(millisecondsTimeout));
        return _packet!;
    }

    public void Set(McpPacket packet)
    {
        _packet = packet;
        _event.Set();
    }

    public void Dispose()
    {
        _event.Dispose();
    }
}
