using D2NG.Core.D2GS.Enums;
using D2NG.Core.D2GS.Items;
using D2NG.Core.D2GS.Objects;
using D2NG.Core.D2GS.Packet.Incoming;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace D2NG.Core.D2GS;

/// <summary>
/// Writes one "Sold item" line per sale: the item as sold, its packet, and the gold the vendor paid.
///
/// Sales go out 30ms apart without waiting, so the gold is not read off the gold attribute but off
/// the vendor's answers: each 0x2A carries the gold on person after its transaction, and the
/// difference to the answer before it is what that sale paid. The first sale of a visit starts the
/// chain from the gold attribute, which is settled because nothing is in flight.
/// </summary>
internal sealed class SaleLog
{
    private static readonly TimeSpan ChainTimeout = TimeSpan.FromSeconds(2);

    private readonly Lock _lock = new();
    private readonly Dictionary<uint, PendingSale> _pending = [];
    private int? _gold;
    private DateTime _lastAnswer = DateTime.MinValue;

    private sealed record PendingSale(Item Item, NPCCode? Npc, DateTime Sent);

    public void Sent(Item item, Entity npc, int goldOnPerson)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            foreach (var stale in _pending.Where(p => now - p.Value.Sent > ChainTimeout).Select(p => p.Key).ToList())
            {
                _pending.Remove(stale);
            }

            if (_pending.Count == 0 && now - _lastAnswer > ChainTimeout)
            {
                _gold = goldOnPerson;
            }

            _pending[item.Id] = new PendingSale(item, (npc as WorldObject)?.NPCCode, now);
        }
    }

    public void Answered(NpcTransactionPacket packet, Difficulty difficulty, int characterLevel)
    {
        PendingSale sale;
        int? before;
        lock (_lock)
        {
            before = _gold;
            _gold = packet.Gold;
            _lastAnswer = DateTime.UtcNow;
            if (!_pending.Remove(packet.ItemId, out sale))
            {
                return;
            }
        }

        var item = sale.Item;
        var record = new
        {
            npc = sale.Npc?.ToString(),
            difficulty = difficulty.ToString(),
            characterLevel,
            result = packet.Result,
            gold = before.HasValue ? packet.Gold - before.Value : (int?)null,
            goldAfter = packet.Gold,
            code = item.Type,
            name = item.Name.ToString(),
            quality = item.Quality.ToString(),
            level = item.Level,
            identified = item.IsIdentified,
            ethereal = item.Ethereal,
            sockets = item.Sockets,
            socketed = item.SocketedItems.Values.Select(s => s.Type).ToList(),
            durability = item.Durability,
            maxDurability = item.MaximumDurability,
            defense = item.Defense,
            amount = item.Amount,
            prefix = item.Prefix,
            suffix = item.Suffix,
            prefixes = item.Prefixes,
            suffixes = item.Suffixes,
            uniqueCode = item.UniqueCode,
            setCode = item.SetCode,
            superiority = item.Superiority.ToString(),
            runeword = item.IsRuneword,
            stats = item.Properties.Values.Select(p => new
            {
                id = p.StatId,
                type = p.Type.ToString(),
                value = p.Value,
                min = p.MinimumValue,
                max = p.MaximumValue,
                length = p.Length,
                perLevel = p.PerLevel,
                skill = (int)p.Skill,
                skillLevel = p.SkillLevel,
                charges = p.Charges,
                maxCharges = p.MaximumCharges
            }).ToList(),
            packet = item.Packet == null ? null : Convert.ToHexString(item.Packet),
            socketedPackets = item.SocketedItems.Values.Where(s => s.Packet != null).Select(s => Convert.ToHexString(s.Packet)).ToList()
        };

        Log.Information("Sold item {Sale:l}", JsonSerializer.Serialize(record));
    }
}
