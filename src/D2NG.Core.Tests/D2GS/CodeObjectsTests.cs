using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Act;
using D2NG.Core.D2GS.Objects;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace D2NG.Core.Tests.D2GS;

public class CodeObjectsTests
{
    /// <summary>ActData's index by code as it was: a ConcurrentBag per code, rebuilt from its own enumeration on every removal.</summary>
    private sealed class BagIndex
    {
        private readonly ConcurrentDictionary<EntityCode, ConcurrentBag<WorldObject>> _byCode = new();

        public void Add(WorldObject obj)
            => _byCode.AddOrUpdate(obj.Code, newCode => [obj], (existingCode, existingObjects) => { existingObjects.Add(obj); return existingObjects; });

        public void Remove(WorldObject value)
            => _byCode.AddOrUpdate(value.Code, newCode => [], (existingCode, existingObjects) => [.. existingObjects.Where(o => o.Id != value.Id || o.Type != value.Type)]);

        public List<WorldObject> Get(EntityCode code) => _byCode.TryGetValue(code, out var objects) ? objects.ToList() : null;
    }

    [Fact]
    public void GivesObjectsOutInTheOrderTheBagDidThroughAddsAndRemovals()
    {
        var random = new Random(7);
        var codes = new[] { (EntityCode)0, EntityCode.TownPortal, EntityCode.RedTownPortal };
        for (var round = 0; round < 200; round++)
        {
            var bags = new BagIndex();
            var index = new ConcurrentDictionary<EntityCode, CodeObjects>();
            var live = new List<WorldObject>();
            for (var step = 0; step < 300; step++)
            {
                if (live.Count == 0 || random.Next(3) > 0)
                {
                    var type = random.Next(4) == 0 ? EntityType.Object : EntityType.NPC;
                    var obj = new WorldObject(type, (uint)random.Next(40), codes[random.Next(codes.Length)], new Point(1, 1), EntityState.Alive, 0);
                    bags.Add(obj);
                    index.GetOrAdd(obj.Code, static _ => new CodeObjects()).Add(obj);
                    live.Add(obj);
                }
                else
                {
                    var removed = live[random.Next(live.Count)];
                    bags.Remove(removed);
                    index.GetOrAdd(removed.Code, static _ => new CodeObjects()).Remove(removed.Id, removed.Type);
                    live.RemoveAll(o => o.Id == removed.Id && o.Type == removed.Type && o.Code == removed.Code);
                }

                foreach (var code in codes)
                {
                    var expected = bags.Get(code);
                    var actual = index.TryGetValue(code, out var objects) ? objects.ToList() : null;
                    Assert.Equal(expected == null, actual == null);
                    if (expected != null)
                    {
                        Assert.Equal(expected.Count, actual.Count);
                        Assert.True(expected.Zip(actual).All(pair => ReferenceEquals(pair.First, pair.Second)), $"order differs in round {round} step {step} for {code}");
                    }
                }
            }
        }
    }
}
