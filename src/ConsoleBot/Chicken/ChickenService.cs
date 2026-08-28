using D2NG.Core;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Packet;
using Serilog;
using System;
using System.Threading;

namespace ConsoleBot.Chicken;

/// <summary>
/// Keeps a character alive: drinks potions and abandons the game when life or mana runs out. This
/// used to live in <c>D2NG.Core.Game</c> as a hardcoded polling thread; it sits here so the
/// thresholds can be configured per character.
/// </summary>
/// <remarks>
/// Decisions are made on the packet listener thread the moment a life, mana or attribute update is
/// decoded, so nothing waits on the thread pool. Leaving the game is handed to a dedicated
/// high-priority thread instead, because leaving blocks on packets that only the listener thread can
/// process - doing it inline deadlocks, which is the bug the old in-Core version had.
/// </remarks>
public static class ChickenService
{
    public static void Attach(Client client, ChickenConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(client);
        new ChickenWatch(client, configuration ?? new ChickenConfiguration()).Start();
    }

    private sealed class ChickenWatch
    {
        private readonly Client _client;
        private readonly ChickenConfiguration _config;
        private readonly object _leaveGate = new();
        private bool _leaveSignalled;

        private int _previousLife;
        private int _previousMaxLife;
        private int _previousMaxMana;

        // Set when a maximum rose without any life being lost - Battle Orders on 1.09. Until real
        // damage arrives the fraction is meaningless, so fraction-based rules stay parked.
        private bool _awaitingDamage;
        private volatile bool _leaving;

        public ChickenWatch(Client client, ChickenConfiguration config)
        {
            _client = client;
            _config = config;
        }

        public void Start()
        {
            _client.OnReceivedPacketEvent(InComingPacket.LifeManaUpdate, _ => Evaluate());
            _client.OnReceivedPacketEvent(InComingPacket.LifeManaUpdatePot, _ => Evaluate());
            _client.OnReceivedPacketEvent(InComingPacket.AddAttributeByte, _ => Evaluate());
            _client.OnReceivedPacketEvent(InComingPacket.AddAttributeWord, _ => Evaluate());
            _client.OnReceivedPacketEvent(InComingPacket.AddAttributeDword, _ => Evaluate());

            new Thread(LeaveAndBackstopLoop)
            {
                Name = "Chicken Thread",
                IsBackground = true,
                Priority = ThreadPriority.Highest
            }.Start();
        }

        /// <summary>
        /// Waits for a leave request and doubles as the backstop: if life packets stop arriving the
        /// timeout still re-evaluates, so a stalled connection cannot leave the character unguarded.
        /// </summary>
        private void LeaveAndBackstopLoop()
        {
            while (true)
            {
                bool leave;
                lock (_leaveGate)
                {
                    if (!_leaveSignalled)
                    {
                        Monitor.Wait(_leaveGate, TimeSpan.FromMilliseconds(_config.BackstopIntervalMs));
                    }

                    leave = _leaveSignalled;
                    _leaveSignalled = false;
                }

                if (leave)
                {
                    LeaveGame();
                }
                else
                {
                    Evaluate();
                }
            }
        }

        private void LeaveGame()
        {
            try
            {
                _client.Game.LeaveGame().Wait();
            }
            catch (Exception e)
            {
                Log.Error(e, "Leaving game after chicken failed");
            }
            finally
            {
                _leaving = false;
            }
        }

        private void Evaluate()
        {
            var game = _client.Game;
            try
            {
                if (_leaving || !game.IsInGame())
                {
                    return;
                }

                var me = game.Me;
                if (me == null || game.Area == Area.None || game.IsInTown())
                {
                    return;
                }

                if (me.Effects.ContainsKey(EntityEffect.Playerbody))
                {
                    RequestLeave($"{me.Name} has died");
                    return;
                }

                var life = me.Life;
                var maxLife = me.MaxLife;
                var maximumRose = maxLife > _previousMaxLife || me.MaxMana > _previousMaxMana;
                var lostLife = life < _previousLife;

                _previousLife = life;
                _previousMaxLife = maxLife;
                _previousMaxMana = me.MaxMana;

                if (lostLife)
                {
                    _awaitingDamage = false;
                }
                else if (maximumRose)
                {
                    // Battle Orders. On 1.09 it raises the maximum without raising the current
                    // value, so nudge the bar once and then stay quiet until something actually
                    // hits us - chasing the new maximum would drain the belt for nothing.
                    _awaitingDamage = true;
                    if (PotionIntervalElapsed(game))
                    {
                        game.UseHealthPotions();
                    }

                    return;
                }

                // An absolute floor cannot be moved by a maximum changing, so it is always honoured.
                if (_config.LifeChickenAbsolute > 0 && life <= _config.LifeChickenAbsolute)
                {
                    RequestLeave($"{me.Name} at {life} life, below absolute floor {_config.LifeChickenAbsolute}");
                    return;
                }

                if (_awaitingDamage || maxLife <= 0)
                {
                    return;
                }

                var lifeFraction = (double)life / maxLife;
                if (lifeFraction <= _config.LifeChickenPercent)
                {
                    RequestLeave($"{me.Name} at {life} of {maxLife} life ({lifeFraction:P0})");
                    return;
                }

                var canDrink = PotionIntervalElapsed(game);
                if (lifeFraction < _config.UseRejuvenationPercent)
                {
                    if (canDrink && !game.UseRejuvenationPotion() && !game.UseHealthPotions() && _config.LeaveWhenOutOfHealthPotions)
                    {
                        RequestLeave($"{me.Name} out of healing and rejuvenation potions at {life} of {maxLife} life");
                    }

                    return;
                }

                if (lifeFraction < _config.UseHealthPotionPercent && canDrink)
                {
                    game.UseHealthPotions();
                    return;
                }

                if (me.MaxMana > 0
                    && (double)me.Mana / me.MaxMana < _config.UseManaPotionPercent
                    && canDrink
                    && !game.UseManaPotion()
                    && _config.LeaveWhenOutOfManaPotions)
                {
                    RequestLeave($"{me.Name} out of mana potions at {me.Mana} of {me.MaxMana} mana");
                }
            }
            catch (Exception e)
            {
                // Protection we cannot trust is worse than none, so stop playing loudly rather than
                // fight on with broken logic.
                Log.Fatal(e, "Chicken evaluation failed for {Character}, leaving game", game.Me?.Name);
                RequestLeave("chicken evaluation failed");
            }
        }

        private void RequestLeave(string reason)
        {
            if (_leaving)
            {
                return;
            }

            _leaving = true;
            Log.Information("Leaving game: {Reason}", reason);
            lock (_leaveGate)
            {
                _leaveSignalled = true;
                Monitor.Pulse(_leaveGate);
            }
        }

        private bool PotionIntervalElapsed(Game game)
        {
            var interval = TimeSpan.FromMilliseconds(_config.MinPotionIntervalMs);
            return DateTime.Now - game.LastUsedHealthPotionTime > interval
                && DateTime.Now - game.LastUsedManaPotionTime > interval;
        }
    }
}
