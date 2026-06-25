#nullable enable
namespace SekhemaHelper
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;

    // A* over the nibble-encoded walkability grid. Copied from Radar/Pathfinder.cs (plugins can't
    // reference each other) so the Death-crystal route follows the actual walkable path instead of
    // straight lines. 8-directional, corner-cutting prevention, Euclidean heuristic, path smoothing.
    // Named WalkablePathfinder to avoid colliding with SekhemaHelper's room-DAG PathFinder.
    public static class WalkablePathfinder
    {
        public const int DefaultMaxIterations = 1_000_000;
        public const float DefaultMaxDistance = 2500f;

        // How far to search for a walkable cell when an endpoint lands on non-walkable terrain. The
        // player often stands on decorative/ledge terrain the walkability grid marks non-walkable (e.g.
        // a collapsed Sanctum bridge); 75 was too small to snap across such a gap, so the player->first
        // crystal leg fell back to a straight line through walls. 300 reaches the connected floor.
        public const int DefaultSnapRadius = 300;

        private static readonly (int dx, int dy, float cost)[] Neighbors =
        {
            ( 0, -1, 1.0f),
            ( 1, -1, 1.41421356f),
            ( 1,  0, 1.0f),
            ( 1,  1, 1.41421356f),
            ( 0,  1, 1.0f),
            (-1,  1, 1.41421356f),
            (-1,  0, 1.0f),
            (-1, -1, 1.41421356f),
        };

        public static List<Vector2>? FindPath(
            byte[] walkableData,
            int bytesPerRow,
            Vector2 start,
            Vector2 end,
            HashSet<(int, int)>? doorOverrides = null,
            int maxIterations = DefaultMaxIterations)
        {
            return FindPath(
                walkableData,
                bytesPerRow,
                (int)Math.Round(start.X),
                (int)Math.Round(start.Y),
                (int)Math.Round(end.X),
                (int)Math.Round(end.Y),
                doorOverrides,
                maxIterations);
        }

        public static List<Vector2>? FindPath(
            byte[] walkableData,
            int bytesPerRow,
            int startX,
            int startY,
            int endX,
            int endY,
            HashSet<(int, int)>? doorOverrides = null,
            int maxIterations = DefaultMaxIterations)
        {
            if (!LineWalker.IsWalkable(walkableData, bytesPerRow, startX, startY, doorOverrides))
            {
                if (!TryFindNearestWalkable(
                        walkableData, bytesPerRow, startX, startY, doorOverrides,
                        out startX, out startY))
                {
                    return null;
                }
            }

            var straightLineDist = MathF.Sqrt(
                ((endX - startX) * (endX - startX)) +
                ((endY - startY) * (endY - startY)));
            if (straightLineDist > DefaultMaxDistance)
            {
                return null;
            }

            if (!LineWalker.IsWalkable(walkableData, bytesPerRow, endX, endY, doorOverrides))
            {
                if (!TryFindNearestWalkable(
                        walkableData, bytesPerRow, endX, endY, doorOverrides, out endX, out endY))
                {
                    return null;
                }
            }

            if (startX == endX && startY == endY)
            {
                return new List<Vector2> { new(startX, startY) };
            }

            var startKey = (startX, startY);
            var goalKey = (endX, endY);

            var openSet = new PriorityQueue<(int x, int y), float>();
            var cameFrom = new Dictionary<(int x, int y), (int x, int y)>();
            var gScore = new Dictionary<(int x, int y), float>();

            float Heuristic(int x, int y)
            {
                var dx = endX - x;
                var dy = endY - y;
                return MathF.Sqrt((dx * dx) + (dy * dy));
            }

            gScore[startKey] = 0f;
            openSet.Enqueue(startKey, Heuristic(startX, startY));

            var iterations = 0;

            while (openSet.Count > 0 && iterations < maxIterations)
            {
                iterations++;
                var current = openSet.Dequeue();

                if (current == goalKey)
                {
                    var rawPath = ReconstructPath(cameFrom, current, startKey);
                    return SmoothPath(walkableData, bytesPerRow, rawPath, doorOverrides);
                }

                var currentG = gScore[current];

                foreach (var (dx, dy, moveCost) in Neighbors)
                {
                    var nx = current.x + dx;
                    var ny = current.y + dy;

                    if (!LineWalker.IsWalkable(walkableData, bytesPerRow, nx, ny, doorOverrides))
                    {
                        continue;
                    }

                    if (dx != 0 && dy != 0)
                    {
                        if (!LineWalker.IsWalkable(walkableData, bytesPerRow, current.x + dx, current.y, doorOverrides) ||
                            !LineWalker.IsWalkable(walkableData, bytesPerRow, current.x, current.y + dy, doorOverrides))
                        {
                            continue;
                        }
                    }

                    var neighborKey = (nx, ny);
                    var tentativeG = currentG + moveCost;

                    if (!gScore.TryGetValue(neighborKey, out var existingG) ||
                        tentativeG < existingG)
                    {
                        cameFrom[neighborKey] = current;
                        gScore[neighborKey] = tentativeG;
                        var fScore = tentativeG + Heuristic(nx, ny);
                        openSet.Enqueue(neighborKey, fScore);
                    }
                }
            }

            return null;
        }

        private static bool TryFindNearestWalkable(
            byte[] walkableData,
            int bytesPerRow,
            int x,
            int y,
            HashSet<(int, int)>? doorOverrides,
            out int resultX,
            out int resultY,
            int maxRadius = DefaultSnapRadius)
        {
            if (LineWalker.IsWalkable(walkableData, bytesPerRow, x, y, doorOverrides))
            {
                resultX = x;
                resultY = y;
                return true;
            }

            for (var r = 1; r <= maxRadius; r++)
            {
                for (var dx = -r; dx <= r; dx++)
                {
                    if (LineWalker.IsWalkable(walkableData, bytesPerRow, x + dx, y - r, doorOverrides))
                    {
                        resultX = x + dx;
                        resultY = y - r;
                        return true;
                    }

                    if (LineWalker.IsWalkable(walkableData, bytesPerRow, x + dx, y + r, doorOverrides))
                    {
                        resultX = x + dx;
                        resultY = y + r;
                        return true;
                    }
                }

                for (var dy = -r + 1; dy <= r - 1; dy++)
                {
                    if (LineWalker.IsWalkable(walkableData, bytesPerRow, x - r, y + dy, doorOverrides))
                    {
                        resultX = x - r;
                        resultY = y + dy;
                        return true;
                    }

                    if (LineWalker.IsWalkable(walkableData, bytesPerRow, x + r, y + dy, doorOverrides))
                    {
                        resultX = x + r;
                        resultY = y + dy;
                        return true;
                    }
                }
            }

            resultX = 0;
            resultY = 0;
            return false;
        }

        private static List<Vector2> ReconstructPath(
            Dictionary<(int x, int y), (int x, int y)> cameFrom,
            (int x, int y) current,
            (int x, int y) start)
        {
            var path = new List<Vector2> { new(current.x, current.y) };

            while (current != start)
            {
                current = cameFrom[current];
                path.Add(new Vector2(current.x, current.y));
            }

            path.Reverse();
            return path;
        }

        internal static List<Vector2> SmoothPath(
            byte[] walkableData,
            int bytesPerRow,
            List<Vector2> rawPath,
            HashSet<(int, int)>? doorOverrides = null)
        {
            if (rawPath.Count <= 2)
            {
                return rawPath;
            }

            var result = new List<Vector2> { rawPath[0] };
            var currentIdx = 0;

            while (currentIdx < rawPath.Count - 1)
            {
                var farthest = currentIdx + 1;
                for (var i = rawPath.Count - 1; i > currentIdx; i--)
                {
                    var lineResult = LineWalker.CheckLine(
                        walkableData, bytesPerRow,
                        rawPath[currentIdx], rawPath[i],
                        doorOverrides);

                    if (lineResult.IsClear)
                    {
                        farthest = i;
                        break;
                    }
                }

                currentIdx = farthest;
                result.Add(rawPath[currentIdx]);
            }

            return result;
        }
    }
}
