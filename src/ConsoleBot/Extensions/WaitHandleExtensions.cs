using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ConsoleBot.Extensions;

public static class WaitHandleExtensions
{
    public static Task AsTask(this WaitHandle handle)
    {
        return AsTask(handle, Timeout.InfiniteTimeSpan);
    }

    public static Task AsTask(this WaitHandle handle, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<object>();
        var registration = ThreadPool.RegisterWaitForSingleObject(handle, (state, timedOut) =>
        {
            var localTcs = (TaskCompletionSource<object>)state;
            if (timedOut)
                localTcs.TrySetCanceled();
            else
                localTcs.TrySetResult(null);
        }, tcs, timeout, executeOnlyOnce: true);
        tcs.Task.ContinueWith((_, state) => ((RegisteredWaitHandle)state).Unregister(null), registration, TaskScheduler.Default);
        return tcs.Task;
    }
}
