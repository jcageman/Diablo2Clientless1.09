using D2NG.Core.BNCS.Packet;
using System;
using System.Threading;

namespace D2NG.Core.BNCS;

internal class BncsEvent : IDisposable
{
    private readonly ManualResetEvent _event = new(false);

    private BncsPacket _packet;

    public void Reset()
    {
        _event.Reset();
    }

    public BncsPacket WaitForPacket(int millisecondsTimeout)
    {
        bool result = _event.WaitOne(millisecondsTimeout);
        if (!result)
        {
            return null;
        }

        return _packet;
    }

    public void Set(BncsPacket packet)
    {
        _packet = packet;
        _event.Set();
    }

    public void Dispose()
    {
        _event.Dispose();
    }
}
