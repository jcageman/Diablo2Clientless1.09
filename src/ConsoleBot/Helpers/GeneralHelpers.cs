using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace ConsoleBot.Helpers
{
    public static class GeneralHelpers
    {
        public static bool TryWithTimeout(Func<int, bool> action, TimeSpan timeout)
        {
            bool success = false;
            TimeSpan elapsed = TimeSpan.Zero;
            int retryCount = 0;
            while ((!success) && (elapsed < timeout))
            {
                Stopwatch sw = new Stopwatch();
                sw.Start();
                success = action(retryCount);
                sw.Stop();
                elapsed += sw.Elapsed;
                retryCount++;
            }

            return success;
        }

        public static async Task<bool> TryWithTimeout(Func<int, Task<bool>> action, TimeSpan timeout)
        {
            bool success = false;
            TimeSpan elapsed = TimeSpan.Zero;
            int retryCount = 0;
            while ((!success) && (elapsed < timeout))
            {
                Stopwatch sw = new Stopwatch();
                sw.Start();
                success = await action(retryCount);
                sw.Stop();
                elapsed += sw.Elapsed;
                retryCount++;
            }

            return success;
        }
    }
}
