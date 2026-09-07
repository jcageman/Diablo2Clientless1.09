using ConsoleBot.Bots.Types.Cows;
using ConsoleBot.Helpers;

namespace ConsoleBot.Tests;

/// <summary>
/// The walking party takes one cluster at a time and has to walk to each, so the order it takes
/// them in is worth pinning down.
///
/// This models one walker who can already see every cluster. The real party is three or four
/// claiming in parallel, discovering clusters as they go, which is where the cost of skipping a
/// position shows up. Whether route order or nearest-first travels less is therefore not something
/// this fixture can answer - it measured nearest-first as the tidier of the two, while the live
/// logs measured the reverse. Only the ordering contract below is asserted here.
/// </summary>
public class RouteDisciplineTests
{
    private const int Size = 400;

    private static bool[][] OpenField(int size)
    {
        var grid = new bool[size][];
        for (var y = 0; y < size; y++)
        {
            grid[y] = new bool[size];
            for (var x = 0; x < size; x++)
            {
                grid[y][x] = true;
            }
        }

        return grid;
    }

    /// <summary>Clusters scattered by a fixed seed, so the measurement repeats exactly.</summary>
    private static List<Point> ScatteredClusters(int count, int seed)
    {
        var random = new Random(seed);
        var points = new List<Point>();
        // Bounded: at this density the field cannot always fit the full count, and rejection
        // sampling would otherwise spin forever looking for room that is not there.
        for (var attempt = 0; attempt < 20000 && points.Count < count; attempt++)
        {
            var candidate = new Point((ushort)random.Next(10, Size - 10), (ushort)random.Next(10, Size - 10));
            // Keep them further apart than the registry's own radius so each stays a distinct cluster.
            if (points.All(p => p.Distance(candidate) > ClusterRegistry.ClusterRadius + 2))
            {
                points.Add(candidate);
            }
        }

        return points;
    }

    private static List<Point> Sweep()
        => SweepRoute.Build(OpenField(Size), band: 40, step: 40)
            .Select(p => new Point((ushort)p.X, (ushort)p.Y))
            .ToList();

    private sealed record Walk(double Distance, int Doublebacks, int Turns);

    /// <summary>
    /// Claims every cluster the way the party does, walking to each in turn, and reports how far it
    /// travelled and how often it reversed.
    /// </summary>
    private static Walk WalkTheLevel(int seed)
    {
        var sweep = Sweep();
        var registry = new ClusterRegistry();
        foreach (var point in ScatteredClusters(40, seed))
        {
            registry.FindOrRegister(point, ClusterKind.Cow, out _);
        }

        registry.SetSweep(sweep);

        var at = sweep[0];
        var previous = at;
        double travelled = 0;
        var doublebacks = 0;
        var turns = 0;
        var legs = new List<Point>();

        while (true)
        {
            var sweepIndex = NearestSweepIndex(sweep, at);
            var cluster = registry.ClaimNearest(at, ClusterKind.Cow, sweepIndex);
            if (cluster == null)
            {
                break;
            }

            travelled += at.Distance(cluster.Location);
            legs.Add(cluster.Location);

            if (legs.Count >= 2)
            {
                turns++;
                if (TurnDegrees(previous, at, cluster.Location) > 120)
                {
                    doublebacks++;
                }
            }

            previous = at;
            at = cluster.Location;
            registry.Release(cluster);
        }

        return new Walk(travelled, doublebacks, turns);
    }

    private static int NearestSweepIndex(List<Point> sweep, Point location)
    {
        var best = 0;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < sweep.Count; i++)
        {
            var distance = sweep[i].Distance(location);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }

    private static double TurnDegrees(Point a, Point b, Point c)
    {
        double firstX = b.X - a.X, firstY = b.Y - a.Y;
        double secondX = c.X - b.X, secondY = c.Y - b.Y;
        var firstLength = Math.Sqrt((firstX * firstX) + (firstY * firstY));
        var secondLength = Math.Sqrt((secondX * secondX) + (secondY * secondY));
        if (firstLength == 0 || secondLength == 0)
        {
            return 0;
        }

        var cosine = ((firstX * secondX) + (firstY * secondY)) / (firstLength * secondLength);
        return Math.Acos(Math.Clamp(cosine, -1, 1)) * 180 / Math.PI;
    }

    [Fact]
    public void Clusters_are_taken_in_broadly_increasing_route_order()
    {
        // The contract the walking party depends on: work the route forwards, so the group advances
        // as one line and nobody is sent back across ground already swept.
        var sweep = Sweep();
        var registry = new ClusterRegistry();
        foreach (var point in ScatteredClusters(40, seed: 1))
        {
            registry.FindOrRegister(point, ClusterKind.Cow, out _);
        }

        registry.SetSweep(sweep);

        var at = sweep[0];
        var taken = new List<int>();
        while (true)
        {
            var cluster = registry.ClaimNearest(at, ClusterKind.Cow, NearestSweepIndex(sweep, at));
            if (cluster == null)
            {
                break;
            }

            taken.Add(cluster.SweepIndex);
            at = cluster.Location;
            registry.Release(cluster);
        }

        var backwards = taken.Zip(taken.Skip(1)).Count(pair => pair.Second < pair.First);
        Assert.True(backwards <= 1, $"went backwards along the route {backwards} times: {string.Join(",", taken)}");
    }
}
