using System.Collections.Generic;

namespace SekhemaHelper
{
    // Longest-weighted path over the layered DAG (player's current room -> boss in the last layer).
    // Forward edges: room (L,r) connects to rooms (L+1, t) for t in r.NextConnections.
    //
    // Ranking is LEXICOGRAPHIC: (1) total reward-weight along the path, then (2) total connectivity
    // (sum of each room's forward-exit count) as a strict tie-breaker. So connectivity steers toward
    // rooms that keep more options open ONLY among reward-equal routes (e.g. unrevealed areas where
    // every room is base weight) — it never overrides a real reward difference. This fixes the case
    // where two rooms feed the SAME desired next room and the worse-reward one won on a flat
    // connectivity bonus (docs/re-findings-sekhema.md §4.7.11).
    public static class PathFinder
    {
        private const double RewardEps = 1e-6;

        public static List<(int layer, int room)> FindBestPath(
            SekhemaFloor floor,
            IReadOnlyDictionary<(int, int), double> weights)
        {
            var result = new List<(int, int)>();
            if (floor == null || floor.Layers.Count == 0)
                return result;

            // Start the route from the player's CURRENT room (the already-walked part is fixed). Before
            // the first choice (PlayerLayer < 0) no room is fixed yet, so EVERY room of layer 0 is a
            // valid entry point and must be seeded — seeding only (0,0) would structurally exclude the
            // other start rooms from the search and force the route through the top room regardless of
            // weight.
            int startL = floor.PlayerLayer >= 0 ? floor.PlayerLayer : 0;
            int startR = floor.PlayerRoom >= 0 ? floor.PlayerRoom : -1;
            if (startL >= floor.Layers.Count)
            {
                startL = 0;
                startR = -1;
            }

            // dpR/dpC = best (reward, connectivity) accumulated reaching room r of layer L;
            // from[L][r] = predecessor room idx.
            var dpR = new Dictionary<(int, int), double>();
            var dpC = new Dictionary<(int, int), long>();
            var from = new Dictionary<(int, int), int>();

            if (startR >= 0 && startR < floor.Layers[startL].Count)
            {
                // Player is in a known room: the route is fixed from there.
                dpR[(startL, startR)] = W(weights, startL, startR);
                dpC[(startL, startR)] = Conn(floor, startL, startR);
            }
            else
            {
                // No choice made yet: seed all rooms of the start layer as candidate entry points.
                for (int r = 0; r < floor.Layers[startL].Count; r++)
                {
                    dpR[(startL, r)] = W(weights, startL, r);
                    dpC[(startL, r)] = Conn(floor, startL, r);
                }
            }

            for (int l = startL + 1; l < floor.Layers.Count; l++)
            {
                var prev = floor.Layers[l - 1];
                for (int s = 0; s < prev.Count; s++)
                {
                    if (!dpR.TryGetValue((l - 1, s), out var baseR))
                        continue;
                    long baseC = dpC[(l - 1, s)];
                    foreach (var t in prev[s].NextConnections)
                    {
                        if (t < 0 || t >= floor.Layers[l].Count)
                            continue;
                        double candR = baseR + W(weights, l, t);
                        long candC = baseC + Conn(floor, l, t);
                        if (!dpR.ContainsKey((l, t)) || Better(candR, candC, dpR[(l, t)], dpC[(l, t)]))
                        {
                            dpR[(l, t)] = candR;
                            dpC[(l, t)] = candC;
                            from[(l, t)] = s;
                        }
                    }
                }
            }

            // Pick the best-scoring reachable room in the deepest layer that has any reached room.
            int lastLayer = -1;
            for (int l = floor.Layers.Count - 1; l >= 0; l--)
            {
                bool any = false;
                for (int r = 0; r < floor.Layers[l].Count; r++)
                    if (dpR.ContainsKey((l, r))) { any = true; break; }
                if (any) { lastLayer = l; break; }
            }
            if (lastLayer < 0)
                return result;

            int bestRoom = -1;
            double bestR = double.MinValue;
            long bestC = long.MinValue;
            for (int r = 0; r < floor.Layers[lastLayer].Count; r++)
                if (dpR.TryGetValue((lastLayer, r), out var cr) &&
                    (bestRoom < 0 || Better(cr, dpC[(lastLayer, r)], bestR, bestC)))
                {
                    bestR = cr;
                    bestC = dpC[(lastLayer, r)];
                    bestRoom = r;
                }
            if (bestRoom < 0)
                return result;

            // Reconstruct back to the start room (the player's current position).
            int cl = lastLayer, cr2 = bestRoom;
            while (cl >= startL)
            {
                result.Add((cl, cr2));
                if (cl <= startL || !from.TryGetValue((cl, cr2), out var ps))
                    break;
                cr2 = ps;
                cl--;
            }
            result.Reverse();
            return result;
        }

        // Lexicographic: higher reward wins; on a reward tie, higher connectivity wins.
        private static bool Better(double rNew, long cNew, double rOld, long cOld)
        {
            if (rNew > rOld + RewardEps) return true;
            if (rNew < rOld - RewardEps) return false;
            return cNew > cOld;
        }

        private static double W(IReadOnlyDictionary<(int, int), double> weights, int l, int r) =>
            weights.TryGetValue((l, r), out var w) ? w : 0;

        // A room's own connectivity = how many forward exits it opens (its NextConnections count).
        private static long Conn(SekhemaFloor floor, int l, int r) =>
            r >= 0 && r < floor.Layers[l].Count ? floor.Layers[l][r].NextConnections.Count : 0;
    }
}
