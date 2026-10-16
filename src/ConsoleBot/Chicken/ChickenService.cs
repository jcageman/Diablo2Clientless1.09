using D2NG.Core;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Packet;
using D2NG.Core.D2GS.Packet.Incoming;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Players;
using ConsoleBot.Helpers;
using Serilog;
using System;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

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
    private static readonly ConditionalWeakTable<Client, ChickenWatch> Watches = new();

    /// <param name="stayForCorpse">
    /// Stay in the game when the character has died rather than leave: the server keeps 75% of the
    /// experience lost on the corpse for as long as the game lasts, and gives it back to whoever picks it
    /// up. Only for party bots that can bring the character back to its body; see <see cref="DeathLocation"/>.
    /// </param>
    public static void Attach(Client client, ChickenConfiguration configuration, bool stayForCorpse = false)
    {
        ArgumentNullException.ThrowIfNull(client);
        var watch = new ChickenWatch(client, configuration ?? new ChickenConfiguration(), stayForCorpse);
        Watches.AddOrUpdate(client, watch);
        watch.Start();
    }

    /// <summary>Where the character died in the current game when it stayed for its corpse; null when it has not.</summary>
    public static Point DeathLocation(Client client) => Watches.TryGetValue(client, out var watch) ? watch.DeathLocation : null;

    /// <summary>
    /// For a barbarian about to whirl: drinks a rejuvenation first when life is under the rejuvenation
    /// line, and says whether to hold the whirl for it. Nothing can be drunk once the whirl has started.
    /// </summary>
    public static bool HoldForRejuvenation(Client client) => Watches.TryGetValue(client, out var watch) && watch.HoldForRejuvenation();

    /// <summary>Back to normal chickening, after the corpse was picked up or at the end of the game.</summary>
    public static void ClearDeath(Client client)
    {
        if (Watches.TryGetValue(client, out var watch))
        {
            watch.DeathLocation = null;
        }
    }

    private sealed class ChickenWatch
    {
        private readonly Client _client;
        private readonly ChickenConfiguration _config;
        private readonly object _leaveGate = new();

        /// <summary>How long after being hit potions and chickening stay armed.</summary>
        private static readonly TimeSpan UnharmedGracePeriod = TimeSpan.FromSeconds(3);
        private bool _leaveSignalled;

        private int _previousLife;
        private DateTime _previousLifeAt = DateTime.MinValue;

        /// <summary>
        /// Iron Maiden returned 2116 of a barbarian's 3684 life in 128ms; with 1682 left he drank a
        /// rejuvenation and read 0 life 440ms later. A hit this recent that is as big as what is left
        /// is one hit from death, and no potion beats the next one.
        /// </summary>
        private static readonly TimeSpan KillingBurstWindow = TimeSpan.FromMilliseconds(500);
        private int _previousMaxLife;
        private object _loggedLifeAboveMaximumFor;
        private object _loggedManaAboveMaximumFor;
        private DateTime _lastDamageAt = DateTime.MinValue;
        private int _previousMaxMana;

        // Set when a maximum rose without any life being lost - Battle Orders on 1.09. Until real
        // damage arrives the fraction is meaningless, so fraction-based rules stay parked.
        private bool _awaitingDamage;
        private volatile bool _leaving;

        private DateTime _lastRejuvenationAt = DateTime.MinValue;

        private int _maxLifeBeforeRise;

        private DateTime _maxLifeRoseAt = DateTime.MinValue;

        private static readonly TimeSpan BuffedMaximumGrace = TimeSpan.FromSeconds(10);

        private static readonly TimeSpan RejuvenationGap = TimeSpan.FromMilliseconds(500);

        private static readonly TimeSpan RespawnDelay = TimeSpan.FromMilliseconds(500);

        private readonly bool _stayForCorpse;

        public volatile Point DeathLocation;

        public ChickenWatch(Client client, ChickenConfiguration config, bool stayForCorpse)
        {
            _client = client;
            _config = config;
            _stayForCorpse = stayForCorpse;
        }

        public void Start()
        {
            _client.OnReceivedPacketEvent(InComingPacket.LifeManaUpdate, _ => Evaluate());
            _client.OnReceivedPacketEvent(InComingPacket.LifeManaUpdatePot, _ => Evaluate());
            _client.OnReceivedPacketEvent(InComingPacket.AddAttributeByte, _ => Evaluate());
            _client.OnReceivedPacketEvent(InComingPacket.AddAttributeWord, _ => Evaluate());
            _client.OnReceivedPacketEvent(InComingPacket.AddAttributeDword, _ => Evaluate());
            _client.OnReceivedPacketEvent(InComingPacket.CorpseAssign, p => ReportCorpse(new CorpseAssignPacket(p)));
            // The end of a whirl is a state update, not a life packet: without these the drink that was
            // held for it waited for the next hit.
            _client.OnReceivedPacketEvent(InComingPacket.UpdateEntityEffects, p => EvaluateIfMine(new UpdateEntityEffectsPacket(p).EntityId));
            _client.OnReceivedPacketEvent(InComingPacket.AddEntityEffect, p => EvaluateIfMine(new AddEntityEffectPacket(p).EntityId));
            _client.OnReceivedPacketEvent(InComingPacket.AddEntityEffect2, p => EvaluateIfMine(new AddEntityEffectPacket2(p).EntityId));

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

        private readonly object _evaluateGate = new();

        private void EvaluateIfMine(uint entityId)
        {
            if (entityId == _client.Game.Me?.Id)
            {
                Evaluate();
            }
        }

        public bool HoldForRejuvenation()
        {
            lock (_evaluateGate)
            {
                var game = _client.Game;
                var me = game.Me;
                if (me == null || me.MaxLife <= 0 || _leaving || DeathLocation != null
                    || (double)me.Life / me.MaxLife >= _config.UseRejuvenationPercent
                    || RejuvenationStock(game) == 0)
                {
                    return false;
                }

                if (DateTime.Now - _lastRejuvenationAt <= RejuvenationGap)
                {
                    return true;
                }

                if (game.UseRejuvenationPotion())
                {
                    _lastRejuvenationAt = DateTime.Now;
                }

                return true;
            }
        }

        /// <summary>
        /// One evaluation at a time: the listener thread and the backstop both call in, and both
        /// passed the rejuvenation gap together - two full rejuvenations 18ms apart at one life.
        /// </summary>
        private void Evaluate()
        {
            lock (_evaluateGate)
            {
                EvaluateLocked();
            }
        }

        private void EvaluateLocked()
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

                if (DeathLocation != null)
                {
                    GuardCorpseRun(me);
                    return;
                }

                if (me.Effects.ContainsKey(EntityEffect.Playerbody))
                {
                    RequestLeave($"{me.Name} has died");
                    return;
                }

                var life = me.Life;
                var maxLife = me.MaxLife;
                if (life <= 0 && _previousLife > 0)
                {
                    _zeroLifeAt = DateTime.Now;
                }

                var maximumRose = maxLife > _previousMaxLife || me.MaxMana > _previousMaxMana;
                var lostLife = life < _previousLife;
                var loss = _previousLife - life;
                var sincePrevious = DateTime.Now - _previousLifeAt;
                if (maxLife > _previousMaxLife && _previousMaxLife > 0)
                {
                    _maxLifeBeforeRise = _previousMaxLife;
                    _maxLifeRoseAt = DateTime.Now;
                }

                if (_loggedLifeAboveMaximumFor != me && maxLife > 0 && life > maxLife * 1.05)
                {
                    _loggedLifeAboveMaximumFor = me;
                    Log.Information("{Character} life {Life} above computed maximum {MaxLife}: {Breakdown}", me.Name, life, maxLife, me.MaxLifeBreakdown);
                }

                if (_loggedManaAboveMaximumFor != me && me.MaxMana > 0 && me.Mana > me.MaxMana * 1.05)
                {
                    _loggedManaAboveMaximumFor = me;
                    Log.Information("{Character} mana {Mana} above computed maximum {MaxMana}: {Breakdown}", me.Name, me.Mana, me.MaxMana, me.MaxManaBreakdown);
                }

                _previousLife = life;
                _previousLifeAt = DateTime.Now;
                _previousMaxLife = maxLife;
                _previousMaxMana = me.MaxMana;

                if (lostLife)
                {
                    _awaitingDamage = false;
                    _lastDamageAt = DateTime.Now;
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

                // Nothing in town damages you, so nothing in town should drink or leave. Game.Area is
                // the only town test available here and it lags every transition, so a portal into town
                // left this drinking at 88% life beside the vendor until the belt was empty. Recent
                // damage is the one signal that cannot be stale.
                if (DateTime.Now - _lastDamageAt > UnharmedGracePeriod)
                {
                    return;
                }

                // An absolute floor cannot be moved by a maximum changing, so it is always honoured.
                if (_config.LifeChickenAbsolute > 0 && life <= _config.LifeChickenAbsolute)
                {
                    RequestLeave($"{me.Name} at {life} life, below absolute floor {_config.LifeChickenAbsolute}");
                    return;
                }

                if (lostLife && loss >= life && sincePrevious <= KillingBurstWindow)
                {
                    RequestLeave($"{me.Name} lost {loss} in {(int)sincePrevious.TotalMilliseconds}ms with {life} of {maxLife} left, one more hit like it kills");
                    return;
                }

                if (_awaitingDamage || maxLife <= 0)
                {
                    return;
                }

                // Leaving is judged against the maximum before Battle Orders for a few seconds after
                // it: the shouts doubled the taxi's maximum while Storm Casters at the star had her
                // at 59%, and against the new maximum that read 30% and chickened her. Drinking is
                // not: judged the same way, a taxi at 563 of 1795 drank a healing potion instead of
                // a rejuvenation and chickened two seconds later. The absolute floor above guards
                // those seconds.
                var leaveOf = DateTime.Now - _maxLifeRoseAt < BuffedMaximumGrace ? Math.Min(maxLife, _maxLifeBeforeRise) : maxLife;
                var lifeFraction = (double)life / maxLife;
                if ((double)life / leaveOf <= _config.LifeChickenPercent)
                {
                    RequestLeave($"{me.Name} at {life} of {maxLife} life ({lifeFraction:P0})");
                    return;
                }

                // Leaving still works mid-whirl and is judged here; a drink would only be ignored and
                // start the rejuvenation gap, so it waits for the state update that ends the whirl.
                // Under Iron Maiden every hit of the rest of the whirl comes back doubled with no potion
                // possible: seven of eight rejuvenations drunk like that ended in a leave at no life.
                if (ClassHelpers.IsWhirling(me))
                {
                    if (lifeFraction < _config.UseRejuvenationPercent && me.Effects.ContainsKey(EntityEffect.Ironmaiden))
                    {
                        RequestLeave($"{me.Name} whirling under Iron Maiden at {life} of {maxLife} life, below the rejuvenation line with no potion possible");
                    }

                    return;
                }

                var canDrink = PotionIntervalElapsed(game);
                if (lifeFraction < _config.UseRejuvenationPercent)
                {
                    // A rejuvenation heals at once, so it does not wait out the healing potion
                    // interval: the taxi drank a healing potion at 46%, sat locked out
                    // for a second and a half while she fell from 769 to 471, and chickened with
                    // fifteen rejuvenations in her inventory. The short gap lets the new life value
                    // arrive before a second one is spent.
                    if (game.Inventory.Items.Any(i => i.Classification == ClassificationType.RejuvenationPotion))
                    {
                        if (DateTime.Now - _lastRejuvenationAt > RejuvenationGap && game.UseRejuvenationPotion())
                        {
                            _lastRejuvenationAt = DateTime.Now;
                        }

                        return;
                    }

                    if (canDrink && !game.UseHealthPotions() && _config.LeaveWhenOutOfHealthPotions)
                    {
                        RequestLeave($"{me.Name} out of healing and rejuvenation potions at {life} of {maxLife} life");
                    }

                    return;
                }

                if (lifeFraction < _config.UseHealthPotionPercent && canDrink)
                {
                    // A drink that finds nothing to drink is worth saying out loud: the character
                    // otherwise sits below the threshold looking like it is healing and never does.
                    if (!game.UseHealthPotions())
                    {
                        // With the counts attached, an empty belt tells apart from a belt that was
                        // never filled because there was no inventory room to buy into.
                        Log.Warning("{Character} has no health potions to drink at {Life} of {MaxLife} life, belt {Belt}, inventory {Inventory}, {FreeCells} free cells",
                            me.Name, life, maxLife,
                            game.Belt.NumOfHealthPotions(),
                            game.Inventory.Items.Count(i => i.Classification == ClassificationType.HealthPotion),
                            game.Inventory.FreeCellCount());
                    }

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

        private DateTime _leaveRequestedAt = DateTime.MinValue;
        private DateTime _zeroLifeAt = DateTime.MinValue;

        /// <summary>
        /// The server assigns a corpse when the death animation ends, and that is the only certain sign
        /// of a death: twelve of thirteen leaves at 0 life lost no experience. When it comes before any
        /// leave - a barbarian whirling read no life for 1.7s and was dead before the 0 arrived - the
        /// leave can only throw away the experience still on the body.
        /// </summary>
        private void ReportCorpse(CorpseAssignPacket packet)
        {
            var game = _client.Game;
            var me = game.Me;
            if (me == null || packet.PlayerId != me.Id || !packet.CorpseAdded)
            {
                return;
            }

            var now = DateTime.Now;
            // A body from an earlier death is assigned right after joining, when the area can still lag;
            // a death always comes with damage just taken.
            if (_stayForCorpse && !_leaving && game.IsInGame() && game.Area != Area.None && !game.IsInTown()
                && now - _lastDamageAt < UnharmedGracePeriod)
            {
                DeathLocation = me.Location;
                Log.Warning("{Character} died at {Location} in {Area}, staying in the game for the corpse", me.Name, me.Location, game.Area);
                Task.Delay(RespawnDelay).ContinueWith(_ =>
                {
                    if (game.IsInGame())
                    {
                        game.Resurrect();
                    }
                });
            }

            Log.Warning("{Character} corpse {Corpse} assigned in {Area} at {Location}, in game {InGame}, leaving {Leaving}, {SinceLeave} since the leave request, {SinceZero} since life read 0",
                me.Name, packet.CorpseId, _client.Game.Area, me.Location, _client.Game.IsInGame(), _leaving,
                _leaveRequestedAt == DateTime.MinValue ? "never" : $"{(int)(now - _leaveRequestedAt).TotalMilliseconds}ms",
                _zeroLifeAt == DateTime.MinValue ? "never" : $"{(int)(now - _zeroLifeAt).TotalMilliseconds}ms");
        }

        /// <summary>
        /// Back at the body without its gear there is no belt to drink from, so leaving is all that is
        /// left: the experience still on the corpse costs far less than a second death. Only the
        /// percentage counts here, since a character without its gear can sit below the absolute floor
        /// at full life.
        /// </summary>
        private void GuardCorpseRun(Self me)
        {
            var life = me.Life;
            var maxLife = me.MaxLife;
            if (life <= 0 || maxLife <= 0)
            {
                return;
            }

            if ((double)life / maxLife <= _config.LifeChickenPercent)
            {
                RequestLeave($"{me.Name} at {life} of {maxLife} life on the way back to its corpse");
            }
        }

        private void RequestLeave(string reason)
        {
            if (_leaving)
            {
                return;
            }

            _leaving = true;
            _leaveRequestedAt = DateTime.Now;
            Log.Information("Leaving game: {Reason}", reason);
            lock (_leaveGate)
            {
                _leaveSignalled = true;
                Monitor.Pulse(_leaveGate);
            }
        }

        private static int RejuvenationStock(Game game) =>
            game.Belt.NumOfRejuvenationPotions() + game.Inventory.Items.Count(i => i.Classification == ClassificationType.RejuvenationPotion);

        private bool PotionIntervalElapsed(Game game)
        {
            var interval = TimeSpan.FromMilliseconds(_config.MinPotionIntervalMs);
            return DateTime.Now - game.LastUsedHealthPotionTime > interval
                && DateTime.Now - game.LastUsedManaPotionTime > interval;
        }
    }
}
