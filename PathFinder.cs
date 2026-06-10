using System.Collections.Generic;

namespace SekhemaHelper
{
    // Longest-weighted path over the layered DAG (start layer 0 -> boss last layer).
    // Forward edges: room (L,r) connects to rooms (L+1, t) for t in r.NextConnections.
    public static class PathFinder
    {
        public static List<(int layer, int room)> FindBestPath(
            SekhemaFloor floor,
            IReadOnlyDictionary<(int, int), double> weights)
        {
            var result = new List<(int, int)>();
            if (floor == null || floor.Layers.Count == 0)
                return result;

            // Start the route from the player's CURRENT room (the already-walked part is fixed), not
            // from the floor entrance. Falls back to (0,0) before the first choice.
            int startL = floor.PlayerLayer >= 0 ? floor.PlayerLayer : 0;
            int startR = floor.PlayerRoom >= 0 ? floor.PlayerRoom : 0;
            if (startL >= floor.Layers.Count || startR < 0 || startR >= floor.Layers[startL].Count)
            {
                startL = 0;
                startR = 0;
            }

            // dp[L][r] = best accumulated weight reaching room r of layer L; from[L][r] = predecessor room idx.
            var dp = new Dictionary<(int, int), double>();
            var from = new Dictionary<(int, int), int>();

            dp[(startL, startR)] = W(weights, startL, startR);

            for (int l = startL + 1; l < floor.Layers.Count; l++)
            {
                var prev = floor.Layers[l - 1];
                for (int s = 0; s < prev.Count; s++)
                {
                    if (!dp.TryGetValue((l - 1, s), out var baseCost))
                        continue;
                    foreach (var t in prev[s].NextConnections)
                    {
                        if (t < 0 || t >= floor.Layers[l].Count)
                            continue;
                        double cand = baseCost + W(weights, l, t);
                        if (!dp.TryGetValue((l, t), out var cur) || cand > cur)
                        {
                            dp[(l, t)] = cand;
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
                    if (dp.ContainsKey((l, r))) { any = true; break; }
                if (any) { lastLayer = l; break; }
            }
            if (lastLayer < 0)
                return result;

            int bestRoom = -1;
            double bestCost = double.MinValue;
            for (int r = 0; r < floor.Layers[lastLayer].Count; r++)
                if (dp.TryGetValue((lastLayer, r), out var c) && c > bestCost)
                {
                    bestCost = c;
                    bestRoom = r;
                }
            if (bestRoom < 0)
                return result;

            // Reconstruct back to the start room (the player's current position).
            int cl = lastLayer, cr = bestRoom;
            while (cl >= startL)
            {
                result.Add((cl, cr));
                if (cl <= startL || !from.TryGetValue((cl, cr), out var ps))
                    break;
                cr = ps;
                cl--;
            }
            result.Reverse();
            return result;
        }

        private static double W(IReadOnlyDictionary<(int, int), double> weights, int l, int r) =>
            weights.TryGetValue((l, r), out var w) ? w : 0;
    }
}
