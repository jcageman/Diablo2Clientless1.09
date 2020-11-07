using System;
using System.Timers;

namespace ConsoleBot.Helpers
{
    public class ExecuteAtInterval : IDisposable
    {
        private readonly Timer _timer;

        public ExecuteAtInterval(ElapsedEventHandler eventHandler, TimeSpan interval)
        {
            _timer = new Timer(interval.TotalMilliseconds);
            _timer.AutoReset = true;
            _timer.Elapsed += eventHandler;
        }

        public void Dispose()
        {
            ((IDisposable)_timer).Dispose();
        }

        public void Start()
        {
            _timer.Start();
        }

        public void Stop()
        {
            _timer.Stop();
        }
    }
}
