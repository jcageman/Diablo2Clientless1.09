using System;
using System.Timers;

namespace ConsoleBot.Helpers
{
    public class ExecuteAtInterval : IDisposable
    {
        private readonly Timer _timer;

        public ExecuteAtInterval(ElapsedEventHandler eventHandler, TimeSpan interval)
        {
            _timer = new Timer(interval.TotalMilliseconds)
            {
                AutoReset = true
            };
            _timer.Elapsed += eventHandler;
        }

        public void Dispose()
        {
            ((IDisposable)_timer).Dispose();
            GC.SuppressFinalize(this);
        }

        public void Start()
        {
            _timer.Start();
        }

        public bool IsRunning()
        {
            return _timer.Enabled;
        }

        public void Stop()
        {
            _timer.Stop();
        }
    }
}
