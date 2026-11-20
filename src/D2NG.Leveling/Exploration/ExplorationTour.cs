namespace D2NG.Leveling.Exploration;

/// <summary>
/// The points a party walks through to see a whole area: one walkable point per grid cell of the
/// given spacing, only those reachable on foot from the start, each next one the nearest by walking
/// distance. Nearest in a straight line sends the party through a wall's far side and back down the
/// corridor it came along.
/// </summary>
public static class ExplorationTour
{
    /// <param name="walkable">Rows of the area's collision map, true where a character can stand.</param>
    /// <param name="start">Where the tour begins, in the same row/column coordinates.</param>
    public static List<(int Column, int Row)> Plan(IReadOnlyList<bool[]> walkable, (int Column, int Row) start, int spacing)
    {
        var reachable = Reachable(walkable, start);
        if (reachable == null)
        {
            return [];
        }

        var stops = new List<(int Column, int Row)>();
        var rows = walkable.Count;
        for (var cellRow = 0; cellRow < rows; cellRow += spacing)
        {
            var columns = walkable.Max(row => row.Length);
            for (var cellColumn = 0; cellColumn < columns; cellColumn += spacing)
            {
                if (CellPoint(reachable, cellColumn, cellRow, spacing) is { } point)
                {
                    stops.Add(point);
                }
            }
        }

        var tour = new List<(int Column, int Row)>();
        var here = NearestWalkable(walkable, start)!.Value;
        while (stops.Count > 0)
        {
            var steps = WalkingDistances(reachable, here);
            var next = stops.MinBy(stop => steps[stop.Row][stop.Column] is var walked && walked >= 0 ? walked : int.MaxValue);
            stops.Remove(next);
            tour.Add(next);
            here = next;
        }

        return tour;
    }

    /// <summary>The reachable point nearest the cell's centre, or null when nothing in the cell is reachable.</summary>
    private static (int Column, int Row)? CellPoint(bool[][] reachable, int cellColumn, int cellRow, int spacing)
    {
        var centre = (Column: cellColumn + spacing / 2, Row: cellRow + spacing / 2);
        (int Column, int Row)? best = null;
        var bestDistance = double.MaxValue;
        for (var row = cellRow; row < Math.Min(cellRow + spacing, reachable.Length); row++)
        {
            for (var column = cellColumn; column < Math.Min(cellColumn + spacing, reachable[row].Length); column++)
            {
                if (!reachable[row][column])
                {
                    continue;
                }

                var distance = Distance((column, row), centre);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = (column, row);
                }
            }
        }

        return best;
    }

    private static bool[][]? Reachable(IReadOnlyList<bool[]> walkable, (int Column, int Row) start)
    {
        if (start.Row < 0 || start.Row >= walkable.Count || start.Column < 0 || start.Column >= walkable[start.Row].Length)
        {
            return null;
        }

        var seen = walkable.Select(row => new bool[row.Length]).ToArray();
        var origin = NearestWalkable(walkable, start);
        if (origin == null)
        {
            return null;
        }

        var queue = new Queue<(int Column, int Row)>();
        queue.Enqueue(origin.Value);
        seen[origin.Value.Row][origin.Value.Column] = true;
        while (queue.Count > 0)
        {
            var (column, row) = queue.Dequeue();
            foreach (var (dc, dr) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                var (c, r) = (column + dc, row + dr);
                if (r >= 0 && r < walkable.Count && c >= 0 && c < walkable[r].Length && walkable[r][c] && !seen[r][c])
                {
                    seen[r][c] = true;
                    queue.Enqueue((c, r));
                }
            }
        }

        return seen;
    }

    private static int[][] WalkingDistances(bool[][] reachable, (int Column, int Row) from)
    {
        var steps = reachable.Select(row => Enumerable.Repeat(-1, row.Length).ToArray()).ToArray();
        var queue = new Queue<(int Column, int Row)>();
        steps[from.Row][from.Column] = 0;
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            var (column, row) = queue.Dequeue();
            foreach (var (dc, dr) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                var (c, r) = (column + dc, row + dr);
                if (r >= 0 && r < reachable.Length && c >= 0 && c < reachable[r].Length && reachable[r][c] && steps[r][c] < 0)
                {
                    steps[r][c] = steps[row][column] + 1;
                    queue.Enqueue((c, r));
                }
            }
        }

        return steps;
    }

    /// <summary>Where a character stands is often a tile the map marks unwalkable, such as a waypoint.</summary>
    private static (int Column, int Row)? NearestWalkable(IReadOnlyList<bool[]> walkable, (int Column, int Row) start)
    {
        for (var radius = 0; radius < 20; radius++)
        {
            for (var row = start.Row - radius; row <= start.Row + radius; row++)
            {
                for (var column = start.Column - radius; column <= start.Column + radius; column++)
                {
                    if (row >= 0 && row < walkable.Count && column >= 0 && column < walkable[row].Length && walkable[row][column])
                    {
                        return (column, row);
                    }
                }
            }
        }

        return null;
    }

    private static double Distance((int Column, int Row) a, (int Column, int Row) b)
        => Math.Sqrt(Math.Pow(a.Column - b.Column, 2) + Math.Pow(a.Row - b.Row, 2));
}
