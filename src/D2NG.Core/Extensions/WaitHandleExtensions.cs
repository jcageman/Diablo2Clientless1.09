using System;
using System.Threading;
using System.Threading.Tasks;

namespace D2NG.Core.Extensions
{
    public static class WaitHandleExtensions
    {
        public static Task AsTask(this WaitHandle handle)
        {
            return handle.AsTask(Timeout.InfiniteTimeSpan);
        }

        public static Task<bool> AsTask(this WaitHandle handle, TimeSpan timeout)
        {
            var tcs = new TaskCompletionSource<bool>();
            var registration = ThreadPool.RegisterWaitForSingleObject(handle, (state, timedOut) =>
            {
                var localTcs = (TaskCompletionSource<bool>)state;
                if (timedOut)
                    localTcs.TrySetResult(false);
                else
                    localTcs.TrySetResult(true);
            }, tcs, timeout, executeOnlyOnce: true);
            tcs.Task.ContinueWith((_, state) => ((RegisteredWaitHandle)state).Unregister(null), registration, TaskScheduler.Default);
            return tcs.Task;
        }
    }
}
