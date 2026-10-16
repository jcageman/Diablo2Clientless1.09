using D2NG.Core.D2GS.Players;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Timers;

namespace ConsoleBot.Helpers;

/// <summary>
/// Casts the wanted spells in turn, one cast per interval. Each spell used to run on its own timer,
/// so Static Field and Nova together sent ten casts a second - more than twice what a sorceress can
/// cast - and switched the right hand skill from two threads at once.
/// </summary>
public sealed class CastRotation : IDisposable
{
    private readonly System.Timers.Timer _timer;
    private readonly List<Spell> _spells = [];
    private readonly Action<Skill> _cast;
    private int _next;
    private int _casting;

    public CastRotation(TimeSpan interval, Action<Skill> cast)
    {
        _cast = cast;
        _timer = new System.Timers.Timer(interval.TotalMilliseconds) { AutoReset = true };
        _timer.Elapsed += (_, _) => Tick();
    }

    public Spell Add(Skill skill)
    {
        var spell = new Spell(this, skill);
        _spells.Add(spell);
        return spell;
    }

    private void Tick()
    {
        if (Interlocked.Exchange(ref _casting, 1) == 1)
        {
            return;
        }

        try
        {
            var wanted = _spells.Where(s => s.IsRunning()).ToList();
            if (wanted.Count > 0)
            {
                _cast(wanted[_next++ % wanted.Count].Skill);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _casting, 0);
        }
    }

    private void Update()
    {
        if (_spells.Any(s => s.IsRunning()))
        {
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    public void Dispose() => _timer.Dispose();

    public sealed class Spell
    {
        private readonly CastRotation _rotation;
        private volatile bool _wanted;

        internal Spell(CastRotation rotation, Skill skill)
        {
            _rotation = rotation;
            Skill = skill;
        }

        public Skill Skill { get; }

        public void Start()
        {
            _wanted = true;
            _rotation.Update();
        }

        public void Stop()
        {
            _wanted = false;
            _rotation.Update();
        }

        public bool IsRunning() => _wanted;
    }
}
