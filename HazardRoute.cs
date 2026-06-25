namespace SekhemaHelper
{
    using GameHelper;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.RemoteObjects.States.InGameStateObjects;
    using GameHelper.Utils;
    using ImGuiNET;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Numerics;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    // Draws a "Death crystal" (HourglassLethal) collection route on the LARGE-map overlay. The drawing
    // path is copied 1:1 from RunecraftHelper.DrawMonolithMapLabels (the proven way to paint over the
    // in-game map): foreground draw list, large-map-only, Radar projection (DeltaInWorldToMapDelta +
    // UpdateLargeMapDetails) inlined with Radar's calibration baselines. A plain background draw list is
    // hidden under the game's map texture, so it must be the foreground list.
    //
    // Active vs collected: StateMachine state "deactivated" (0 => active). The awake-entity list keeps
    // collected crystals, and a Sekhema floor loads EVERY room's crystals at once, so the route is
    // restricted to active crystals in the player's room (single-linkage cluster).
    internal static class HazardRoute
    {
        private const string HazardPathSuffix = "Hazards/HourglassLethal";

        // Calibration constants copied from Radar (Radar.cs) so placement matches it.
        private const float LargeMapXBias = 0.6f;
        private const float LargeMapYBias = 0.3f;
        private const float LargeMapScaleBaseline = 0.187812f;
        private static readonly double CameraAngle = 38.7 * Math.PI / 180;

        private struct Crystal
        {
            public uint Id;
            public Vector2 Grid;     // grid position (routing + room clustering)
            public Vector2 Screen;   // projected large-map screen position
            public bool Active;      // StateMachine "deactivated" == 0
        }

        public static void Draw(SekhemaHelperSettings settings)
        {
            if (!settings.DrawHazardRoute)
                return;

            try
            {
                DrawInner(settings);
            }
            catch
            {
                // Never let a draw exception bubble into the plugin host.
            }
        }

        private static void DrawInner(SekhemaHelperSettings settings)
        {
            var gameUi = Core.States.InGameStateObject.GameUi;
            var largeMap = gameUi.LargeMap;
            if (largeMap == null || !largeMap.IsVisible || gameUi.WorldMapPanel.IsVisible)
                return;

            var area = Core.States.InGameStateObject.CurrentAreaInstance;
            if (area?.Player == null || !area.Player.TryGetComponent<Render>(out var playerRender))
                return;
            var trackingPos = new Vector2(playerRender.GridPosition.X, playerRender.GridPosition.Y);
            float trackingHeight = playerRender.TerrainHeight;

            // ---- Radar large-map projection (UpdateLargeMapDetails), 1:1 from Runecraft ----
            var baseRes = GameOffsets.Objects.UiElement.UiElementBaseFuncs.BaseResolution;
            double baseDiag = Math.Sqrt(((double)baseRes.X * baseRes.X) + ((double)baseRes.Y * baseRes.Y));
            double diag = baseDiag * largeMap.Size.Y / baseRes.Y;
            if (diag <= 0)
                return;
            float scale = largeMap.Zoom * LargeMapScaleBaseline;
            if (scale <= 0)
                return;
            float mapScale = 240f / scale;
            float cos = (float)(diag * Math.Cos(CameraAngle) / mapScale);
            float sin = (float)(diag * Math.Sin(CameraAngle) / mapScale);
            var center = largeMap.Center + largeMap.Shift + largeMap.DefaultShift;
            center.X += LargeMapXBias;
            center.Y += LargeMapYBias;

            // ---- Collect every crystal, projected (no filter yet — KeepPlayerRoom needs all of them) ----
            var all = new List<Crystal>();
            foreach (var kv in area.AwakeEntities)
            {
                var e = kv.Value;
                if (e == null || string.IsNullOrEmpty(e.Path) || !e.Path.EndsWith(HazardPathSuffix))
                    continue;
                if (!e.TryGetComponent<Render>(out var r))
                    continue;

                var grid = new Vector2(r.GridPosition.X, r.GridPosition.Y);
                var delta = grid - trackingPos;
                float deltaZ = (r.TerrainHeight - trackingHeight) / 10.86957f;
                var screen = center + new Vector2((delta.X - delta.Y) * cos, (deltaZ - (delta.X + delta.Y)) * sin);

                all.Add(new Crystal { Id = e.Id, Grid = grid, Screen = screen, Active = IsActive(e) });
            }

            var dl = ImGui.GetForegroundDrawList();
            var font = ImGui.GetFont();
            float fontPx = ImGui.GetFontSize();

            // Debug: label every crystal with its id (active = yellow, collected = grey).
            if (settings.DebugEnable)
            {
                var plate = ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg];
                plate.W = 0.55f;
                uint plateCol = ImGui.GetColorU32(plate);
                uint shadow = ImGuiHelper.Color(new Vector4(0f, 0f, 0f, 1f));
                uint act = ImGuiHelper.Color(new Vector4(1f, 0.9f, 0.2f, 1f));
                uint col = ImGuiHelper.Color(new Vector4(0.55f, 0.55f, 0.55f, 1f));
                foreach (var c in all)
                {
                    var text = c.Id.ToString();
                    var ts = ImGui.CalcTextSize(text);
                    var at = new Vector2(c.Screen.X - (ts.X * 0.5f), c.Screen.Y - (ts.Y * 0.5f) - 14f);
                    var pad = new Vector2(3f, 1f);
                    dl.AddRectFilled(at - pad, at + ts + pad, plateCol, 2f);
                    dl.AddText(font, fontPx, at + new Vector2(1f, 1f), shadow, text);
                    dl.AddText(font, fontPx, at, c.Active ? act : col, text);
                }
            }

            // ---- Route: only the player's CURRENT room, active crystals only ----
            var route = new List<Crystal>();

            // DEBUG override: force the route through an EXPLICIT crystal-id set (ignoring active/collected
            // state, the id-group room split AND the inside-room gate). Lets a known scenario be reproduced
            // by typing its crystal ids, to investigate a routing bug. Empty / non-debug = normal path.
            if (settings.DebugEnable && TryParseIds(settings.HazardDebugCrystalIds, out var wantIds))
            {
                foreach (var c in all)
                    if (wantIds.Contains(c.Id))
                        route.Add(c);
                if (route.Count == 0)
                    return;
            }
            else
            {
                // Determine the player's room from ALL crystals (including already-collected ones) by their
                // contiguous entity-id block (dumps: 199-205, 495-501, 665-671; live: 1432-1438 vs 932-938 —
                // rooms differ by ~500). Picking the room from ALL crystals (not just active) means that once
                // the player clears their room the route simply disappears instead of jumping to the next
                // room — it only re-appears when the player physically walks into that next id-block.
                var room = PlayerRoomByIdGroup(all, trackingPos, settings.HazardIdGroupGap);

                // Only draw while the player is actually INSIDE the crystal room, not merely near it. The
                // crystals span their room, so the room's crystal bounding box (+ margin) approximates the
                // room; when the player is in a different encounter (e.g. a Ritual room next door) the
                // crystal group is across a wall and the player falls outside that box, so the route hides.
                if (!PlayerInsideRoom(room, trackingPos, settings.HazardRoomMargin))
                    return;

                foreach (var c in room)
                    if (c.Active)
                        route.Add(c);
                if (route.Count == 0)
                    return;
            }

            uint lineColor = ImGuiHelper.Color(settings.HazardRouteColor);
            uint markerColor = ImGuiHelper.Color(settings.HazardMarkerColor);
            uint labelColor = ImGuiHelper.Color(new Vector4(0f, 0f, 0f, 1f));

            // Order the crystals AND build their leg paths together. Both use the SAME walkable A*
            // distances (computed once, memoized) so the visit order reflects real walking distance over
            // the terrain — not straight-line distance through walls. Cached/throttled (grid-space),
            // then re-projected every frame so it tracks map pan/zoom.
            MaybeRecomputeRoute(area, settings, trackingPos, route);
            var result = routeResult;
            var wpGrid = result.Wp;
            var legs = result.Legs;
            var legInfo = result.Info;

            // Project a grid position onto the large map, sampling terrain height per cell (like Radar's
            // path drawing) so the line hugs the floor.
            var gridHeightData = area.GridHeightData;
            Vector2 ProjGrid(Vector2 g)
            {
                float h = HeightAt(gridHeightData, (int)g.X, (int)g.Y, trackingHeight);
                var d = g - trackingPos;
                float dz = (h - trackingHeight) / 10.86957f;
                return center + new Vector2((d.X - d.Y) * cos, (dz - (d.X + d.Y)) * sin);
            }

            // DEBUG: paint the walkability grid around the player ("where can I walk"). Green = walkable.
            // Reveals why an A* leg degrades to a straight line: the player typically stands on a cell the
            // grid marks non-walkable, with the connected floor a gap away. Bounded box + step keeps it cheap.
            if (settings.DebugEnable && settings.HazardDebugDrawWalkable)
            {
                var wd = area.GridWalkableData;
                int bpr = area.TerrainMetadata.BytesPerRow;
                if (wd != null && wd.Length > 0 && bpr > 0)
                {
                    var doors = LineWalker.BuildDoorOverrideMap(area);
                    int radius = (int)settings.HazardDebugWalkableRadius;
                    // Adaptive step: the painted cell count stays ~constant as the radius grows (a bigger
                    // radius just paints coarser cells), so a huge area can't tank the frame rate.
                    int step = Math.Max(2, radius / 120);
                    uint cellCol = ImGuiHelper.Color(new Vector4(0.2f, 1f, 0.3f, 0.28f));
                    int px = (int)trackingPos.X, py = (int)trackingPos.Y;
                    // Cell half-size in screen px: project a step-sized delta so cells tile without gaps.
                    var o0 = ProjGrid(new Vector2(px, py));
                    var o1 = ProjGrid(new Vector2(px + step, py));
                    float half = Math.Max(1f, Vector2.Distance(o0, o1) * 0.6f);
                    var hs = new Vector2(half, half);
                    for (int gy = py - radius; gy <= py + radius; gy += step)
                        for (int gx = px - radius; gx <= px + radius; gx += step)
                        {
                            if (!LineWalker.IsWalkable(wd, bpr, gx, gy, doors))
                                continue;
                            var s = ProjGrid(new Vector2(gx, gy));
                            dl.AddRectFilled(s - hs, s + hs, cellCol);
                        }
                }
            }

            for (int k = 0; k < legs.Count; k++)
            {
                var leg = legs[k];
                if (leg.Count >= 2)
                {
                    var p0 = ProjGrid(leg[0]);
                    for (int j = 1; j < leg.Count; j++)
                    {
                        var p1 = ProjGrid(leg[j]);
                        dl.AddLine(p0, p1, lineColor, settings.HazardRouteThickness);
                        p0 = p1;
                    }
                }

                // Marker + visit number at the leg's destination crystal (projected from the same
                // waypoint so the line meets the marker exactly).
                var screen = ProjGrid(wpGrid[k]);
                dl.AddCircleFilled(screen, settings.HazardMarkerRadius, markerColor, 16);
                dl.AddCircle(screen, settings.HazardMarkerRadius, lineColor, 16, 1.5f);

                var txt = (k + 1).ToString();
                var ts = ImGui.CalcTextSize(txt);
                dl.AddText(screen - (ts * 0.5f), labelColor, txt);

                // Debug: label each leg with its straight-line span + A*/straight at the leg midpoint.
                if (settings.DebugEnable && leg.Count >= 2 && k < legInfo.Count)
                {
                    var mid = ProjGrid((leg[0] + leg[leg.Count - 1]) * 0.5f);
                    var info = legInfo[k];
                    var its = ImGui.CalcTextSize(info);
                    dl.AddText(font, fontPx, mid - (its * 0.5f) + new Vector2(1f, 1f), labelColor, info);
                    dl.AddText(font, fontPx, mid - (its * 0.5f), lineColor, info);
                }
            }
        }

        // Route building runs on a BACKGROUND thread (like Radar's pathfinding), so the render thread
        // never blocks on A*. The draw call snapshots the inputs, kicks off a Task when they change (but
        // no more than ~4x/sec), and every frame just re-projects the last completed result. Because A*
        // is off the render thread, it uses the FULL iteration budget — no per-leg cap to tune, no
        // straight-line fallback from running out of budget mid-room.
        private const long RouteRecomputeIntervalMs = 250; // throttle: launch a rebuild at most ~4x/sec
        private const int RouteMaxIterations = WalkablePathfinder.DefaultMaxIterations; // full budget (off-thread)

        // Legs whose straight-line span exceeds this stay straight (no A*). The crystals are already
        // clustered to the player's single room (PlayerRoomByIdGroup), so cross-room pathing isn't a
        // concern — the only real cap is A*'s own DefaultMaxDistance, beyond which FindPath returns null
        // and the leg degrades to a straight segment anyway.
        private const float RouteMaxLegDist = WalkablePathfinder.DefaultMaxDistance;
        private const int RouteMaxWalkableOrderCount = 24;  // above this, order by straight line (matrix is O(n^2))

        // The completed route (grid space), swapped in atomically by the background task and read by the
        // render thread. Projected to screen every frame so it tracks map pan/zoom.
        private sealed class RouteResult
        {
            public List<Vector2> Wp = new();              // ordered crystal grid positions (visit order)
            public List<List<Vector2>> Legs = new();      // per-leg walkable path (player->1, 1->2, ...)
            public List<string> Info = new();             // per-leg debug label (span + A*/straight)
            public int Count = -1;                        // crystal count this result was built for
        }

        private static volatile RouteResult routeResult = new();
        private static Task routeTask;
        private static long routeTick;
        private static string routeSig = string.Empty;

        // Computes and MEMOIZES walkable A* paths between route points, so the visit order and the drawn
        // legs share one set of terrain-aware distances (each unique pair is pathed at most once per
        // rebuild). Falls back to a straight segment (and its Euclidean length) when walkable routing is
        // off, the points are farther apart than a single room, or A* finds no path.
        private sealed class LegRouter
        {
            private readonly byte[] wd;
            private readonly int bpr;
            private readonly HashSet<(int, int)> doors;
            private readonly bool canWalk;
            private readonly Dictionary<(long, long), List<Vector2>> pathCache = new();
            private readonly Dictionary<(long, long), float> distCache = new();
            private readonly Dictionary<(long, long), bool> okCache = new();

            // Built from inputs already snapshotted on the render thread (no game-memory access here), so
            // the router is safe to use from the background task.
            public LegRouter(byte[] wd, int bpr, HashSet<(int, int)> doors, bool canWalk)
            {
                this.wd = wd;
                this.bpr = bpr;
                this.canWalk = canWalk;
                this.doors = doors;
            }

            // Walkable path from a to b for DRAWING, oriented a -> b.
            public List<Vector2> Path(Vector2 a, Vector2 b)
            {
                var path = this.Compute(a, b);
                // Compute() caches under an unordered key, so a hit may be oriented b -> a; flip it back.
                if (path.Count >= 2 &&
                    Vector2.DistanceSquared(path[0], a) > Vector2.DistanceSquared(path[path.Count - 1], a))
                {
                    var rev = new List<Vector2>(path);
                    rev.Reverse();
                    return rev;
                }
                return path;
            }

            // Walkable path LENGTH from a to b, for ordering. Direction-agnostic.
            public float Dist(Vector2 a, Vector2 b)
            {
                var key = Key(a, b);
                if (this.distCache.TryGetValue(key, out var d))
                    return d;
                var path = this.Compute(a, b);
                float len;
                if (path.Count < 2)
                {
                    len = Vector2.Distance(a, b);
                }
                else
                {
                    len = 0f;
                    for (int i = 1; i < path.Count; i++)
                        len += Vector2.Distance(path[i - 1], path[i]);
                }
                this.distCache[key] = len;
                return len;
            }

            // True when the leg between a and b is a real A* path (not a straight fallback). For debug.
            public bool Walkable(Vector2 a, Vector2 b)
                => this.okCache.TryGetValue(Key(a, b), out var ok) && ok;

            private List<Vector2> Compute(Vector2 a, Vector2 b)
            {
                var key = Key(a, b);
                if (this.pathCache.TryGetValue(key, out var cached))
                    return cached;
                List<Vector2> leg = null;
                // Only A* within a room's reach. A longer leg means the two points aren't in the same
                // room — pathing it would crawl across the floor through corridors (expensive + visually
                // wrong), so keep it straight.
                if (this.canWalk && Vector2.Distance(a, b) <= RouteMaxLegDist)
                    leg = WalkablePathfinder.FindPath(this.wd, this.bpr, a, b, this.doors, RouteMaxIterations);
                bool ok = leg != null && leg.Count >= 2;
                if (!ok)
                    leg = new List<Vector2> { a, b };
                this.pathCache[key] = leg;
                this.okCache[key] = ok;
                return leg;
            }

            private static (long, long) Key(Vector2 a, Vector2 b)
            {
                long ka = ((long)(int)a.X << 32) | (uint)(int)a.Y;
                long kb = ((long)(int)b.X << 32) | (uint)(int)b.Y;
                return ka <= kb ? (ka, kb) : (kb, ka); // unordered: A* path length is ~symmetric
            }
        }

        // RENDER THREAD: decide whether to launch a (background) route rebuild. Snapshots all inputs the
        // A* needs — the walkability grid, bytes-per-row and the door-override map (built here because it
        // reads game memory) plus the crystal grid positions — then hands them to a Task. Never blocks:
        // if a rebuild is already running, or the throttle hasn't elapsed, or nothing changed, it returns
        // immediately and the caller draws the last completed result.
        private static void MaybeRecomputeRoute(
            AreaInstance area, SekhemaHelperSettings settings, Vector2 player, List<Crystal> active)
        {
            if (routeTask != null && !routeTask.IsCompleted)
                return; // a rebuild is in flight — keep drawing the previous result
            long now = Environment.TickCount64;
            if ((now - routeTick) < RouteRecomputeIntervalMs)
                return; // throttle: at most ~4 rebuilds/sec

            // Coarse signature over the INPUT set (the visit order is an OUTPUT, so it must NOT be part of
            // the key): W/S flag + bucketed player + crystal positions in entity order. A stationary
            // player with an unchanged crystal set never recomputes.
            var sb = new StringBuilder();
            sb.Append(settings.HazardWalkableRoute ? 'W' : 'S');
            sb.Append((int)(player.X / 20f)).Append(',').Append((int)(player.Y / 20f)).Append(';');
            foreach (var c in active)
                sb.Append((int)c.Grid.X).Append(',').Append((int)c.Grid.Y).Append(';');
            var sig = sb.ToString();

            routeTick = now;
            if (sig == routeSig && routeResult.Count == active.Count)
                return; // unchanged
            routeSig = sig;

            // Snapshot the game-memory-backed inputs on THIS (render) thread; the task only touches copies.
            var wd = area.GridWalkableData;
            var bpr = area.TerrainMetadata.BytesPerRow;
            bool canWalk = settings.HazardWalkableRoute && wd != null && wd.Length > 0 && bpr > 0;
            var doors = canWalk ? LineWalker.BuildDoorOverrideMap(area) : null;
            bool orderByWalk = active.Count <= RouteMaxWalkableOrderCount;
            var crystals = new List<Vector2>(active.Count);
            foreach (var c in active)
                crystals.Add(c.Grid);
            int count = active.Count;
            var startPos = player;

            routeTask = Task.Run(() =>
            {
                var res = ComputeRoute(wd, bpr, doors, canWalk, orderByWalk, startPos, crystals);
                res.Count = count;
                Interlocked.Exchange(ref routeResult, res); // atomic swap: render thread sees old or new
            });
        }

        // BACKGROUND THREAD: pure compute over the snapshotted inputs (no game-memory access). Orders the
        // crystals by walkable distance, then builds each leg's walkable A* path. Both share one memoized
        // LegRouter, so each unique pair is pathed at most once.
        private static RouteResult ComputeRoute(
            byte[] wd, int bpr, HashSet<(int, int)> doors, bool canWalk, bool orderByWalk,
            Vector2 player, List<Vector2> crystals)
        {
            var router = new LegRouter(wd, bpr, doors, canWalk);

            // Order by walkable distance for a normal room (a handful of crystals). Guard the O(n^2)
            // distance matrix against a pathological room with many crystals: order by straight line
            // there, but still DRAW each leg along the terrain.
            Func<Vector2, Vector2, float> dist = orderByWalk
                ? router.Dist
                : (a, b) => Vector2.Distance(a, b);

            var order = RouteOrder(crystals, player, dist);

            var wp = new List<Vector2>(order.Count);
            foreach (var idx in order)
                wp.Add(crystals[idx]);

            var pts = new List<Vector2>(wp.Count + 1) { player };
            pts.AddRange(wp);
            var legs = new List<List<Vector2>>(wp.Count);
            for (int k = 0; k < wp.Count; k++)
                legs.Add(router.Path(pts[k], pts[k + 1]));

            // Per-leg diagnostics (drawn only when Debug is on): straight-line span + whether the leg is a
            // real A* path or a straight fallback. Tells us why a leg goes through walls (too far vs A*
            // found no walkable path).
            var info = new List<string>(wp.Count);
            for (int k = 0; k < wp.Count; k++)
                info.Add($"{Vector2.Distance(pts[k], pts[k + 1]):0} {(router.Walkable(pts[k], pts[k + 1]) ? "A*" : "straight")}");

            return new RouteResult { Wp = wp, Legs = legs, Info = info };
        }

        // Parse a comma/space separated list of crystal entity ids (Debug override). Returns false when
        // the string holds no parseable id, so callers fall through to the normal room route.
        private static bool TryParseIds(string raw, out HashSet<uint> ids)
        {
            ids = new HashSet<uint>();
            if (string.IsNullOrWhiteSpace(raw))
                return false;
            foreach (var tok in raw.Split(new[] { ',', ' ', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                if (uint.TryParse(tok, out var id))
                    ids.Add(id);
            return ids.Count > 0;
        }

        private static float HeightAt(float[][] h, int x, int y, float fallback)
        {
            if (h != null && y >= 0 && y < h.Length && h[y] != null && x >= 0 && x < h[y].Length)
                return h[y][x];
            return fallback;
        }

        // Debug recon: dump every Hourglass/Crystal entity with the fields that could distinguish
        // active from disabled/collected. Triggered by the "Dump Crystals" settings button.
        public static void Dump(string filePath)
        {
            var inGame = Core.States.InGameStateObject;
            var area = inGame.CurrentAreaInstance;
            if (area == null)
                return;

            Vector2 pPos = default;
            if (area.Player != null && area.Player.TryGetComponent<Render>(out var pr))
                pPos = new Vector2(pr.GridPosition.X, pr.GridPosition.Y);

            int total = 0;
            var list = new List<Entity>();
            foreach (var kv in area.AwakeEntities)
            {
                total++;
                var e = kv.Value;
                if (e == null || string.IsNullOrEmpty(e.Path))
                    continue;
                if (e.Path.IndexOf("Hourglass", StringComparison.OrdinalIgnoreCase) < 0 &&
                    e.Path.IndexOf("Crystal", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                list.Add(e);
            }

            var addrToId = new Dictionary<long, uint>();
            foreach (var e in list)
            {
                long a = e.Address.ToInt64();
                if (a != 0)
                    addrToId[a] = e.Id;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"awake total={total}, hourglass/crystal matched={list.Count}");
            sb.AppendLine($"player grid=({pPos.X:F0},{pPos.Y:F0})");

            foreach (var e in list)
            {
                string grid = "(no-render)";
                float dist = -1f;
                if (e.TryGetComponent<Render>(out var r))
                {
                    var g = new Vector2(r.GridPosition.X, r.GridPosition.Y);
                    grid = $"({g.X:F0},{g.Y:F0})";
                    dist = Vector2.Distance(pPos, g);
                }

                bool targetable = e.TryGetComponent<Targetable>(out var t) && t.IsTargetable;
                string states = "(none)";
                IntPtr smAddr = IntPtr.Zero;
                if (e.TryGetComponent<StateMachine>(out var sm) && sm.States != null)
                {
                    smAddr = sm.Address;
                    var parts = new List<string>();
                    foreach (var s in sm.States)
                        parts.Add($"{s.Name}={s.Value}");
                    states = string.Join(", ", parts);
                }

                sb.AppendLine($"id={e.Id} addr=0x{e.Address.ToInt64():X} valid={e.IsValid} " +
                              $"state={e.EntityState} targetable={targetable} dist={dist:F0} grid={grid}");
                sb.AppendLine($"    SM=0x{smAddr.ToInt64():X} [{states}]");

                if (smAddr != IntPtr.Zero)
                {
                    var hits = new List<string>();
                    ScanForCrystalRefs(smAddr, 0x300, addrToId, e.Id, "SM", hits);

                    var first = Mem.Read<IntPtr>(smAddr + 0x20);
                    var last = Mem.Read<IntPtr>(smAddr + 0x28);
                    long lc = (first != IntPtr.Zero && last.ToInt64() > first.ToInt64())
                        ? (last.ToInt64() - first.ToInt64()) / 8 : 0;
                    sb.AppendLine($"    listeners={lc}");
                    for (long i = 0; i < lc && i < 32; i++)
                    {
                        var node = Mem.Read<IntPtr>(first + (int)(i * 8));
                        if (node == IntPtr.Zero)
                            continue;
                        ScanForCrystalRefs(node, 0x200, addrToId, e.Id, $"node[{i}]", hits);
                        var sub = Mem.Read<IntPtr>(node);
                        if (sub != IntPtr.Zero)
                            ScanForCrystalRefs(sub - 0x100, 0x200, addrToId, e.Id, $"*node[{i}]", hits);
                    }

                    sb.AppendLine(hits.Count > 0
                        ? $"    REFS-> {string.Join("; ", hits)}"
                        : "    REFS-> (none found in SM/listeners)");
                }
            }

            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(filePath, sb.ToString());
        }

        private static void ScanForCrystalRefs(
            IntPtr baseAddr, int len, Dictionary<long, uint> addrToId, uint selfId, string tag, List<string> hits)
        {
            if (baseAddr == IntPtr.Zero)
                return;
            var bytes = Mem.ReadBytes(baseAddr, len);
            for (int off = 0; off + 8 <= bytes.Length; off += 8)
            {
                long val = BitConverter.ToInt64(bytes, off);
                if (val == 0)
                    continue;
                if (addrToId.TryGetValue(val, out var id) && id != selfId)
                    hits.Add($"{tag}+0x{off:X}->id{id}");
            }
        }

        // Active vs collected from the StateMachine. Confirmed across 21 live crystals + Ghidra
        // (2026-06-22): active reads deactivated=0 / targetable=1; collected reads deactivated=1 /
        // targetable=0. The named "deactivated" state is the game's authoritative, patch-resistant
        // signal. As a belt-and-suspenders cross-check we also read the StateMachine's terminal-state
        // byte at +0x10 (ctor FUN_1420a7aa0, init 0 -> 1 when the crystal is collected): if EITHER the
        // named state OR that byte says collected, the crystal is collected (handles a rare frame where
        // the states vector reads incompletely). See docs/re-findings-sekhema.md §4.5.
        private static bool IsActive(Entity e)
        {
            if (!e.TryGetComponent<StateMachine>(out var sm) || sm.States == null)
                return false;

            // Direct terminal-state byte (Ghidra-confirmed). Non-zero => machine finished => collected.
            if (sm.Address != IntPtr.Zero && Mem.Read<byte>(sm.Address + 0x10) != 0)
                return false;

            bool sawDeactivated = false, deactivated = false;
            bool sawTargetable = false, targetable = false;
            foreach (var s in sm.States)
            {
                if (s.Name == "deactivated")
                {
                    sawDeactivated = true;
                    deactivated = s.Value != 0;
                }
                else if (s.Name == "targetable")
                {
                    sawTargetable = true;
                    targetable = s.Value != 0;
                }
            }

            if (sawDeactivated)
                return !deactivated;
            if (sawTargetable)
                return targetable;
            return false;
        }

        // Keep only the crystals in the player's room, grouped by entity-id contiguity. A Sekhema floor
        // loads every room's crystals into one area instance, but each room's crystals are spawned as a
        // contiguous id block (dumps: 199-205 / 495-501 / 665-671; live: 1432-1438 vs 932-938), so a gap
        // larger than maxIdGap marks a room boundary. Returns the id-group containing the crystal nearest
        // the player. This is cheaper and more robust than spatial clustering (no chaining across
        // corridors, no distance tuning).
        // True if the player is within the room's crystal bounding box, expanded by margin. Defines
        // "the player is in this crystal room" geometrically — keeps the route hidden when the player
        // is in an adjacent, non-crystal room whose nearest crystal group is just across a wall.
        private static bool PlayerInsideRoom(List<Crystal> room, Vector2 player, float margin)
        {
            if (room.Count == 0)
                return false;

            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var c in room)
            {
                if (c.Grid.X < minX) minX = c.Grid.X;
                if (c.Grid.Y < minY) minY = c.Grid.Y;
                if (c.Grid.X > maxX) maxX = c.Grid.X;
                if (c.Grid.Y > maxY) maxY = c.Grid.Y;
            }

            return player.X >= minX - margin && player.X <= maxX + margin &&
                   player.Y >= minY - margin && player.Y <= maxY + margin;
        }

        private static List<Crystal> PlayerRoomByIdGroup(List<Crystal> all, Vector2 player, int maxIdGap)
        {
            if (all.Count <= 1 || maxIdGap <= 0)
                return all;

            var sorted = new List<Crystal>(all);
            sorted.Sort((a, b) => a.Id.CompareTo(b.Id));

            // Split into id-contiguous groups (gap > maxIdGap => new room).
            var groups = new List<List<Crystal>>();
            var cur = new List<Crystal> { sorted[0] };
            for (int i = 1; i < sorted.Count; i++)
            {
                if (sorted[i].Id - sorted[i - 1].Id > (uint)maxIdGap)
                {
                    groups.Add(cur);
                    cur = new List<Crystal>();
                }

                cur.Add(sorted[i]);
            }

            groups.Add(cur);

            // Pick the group (room) that owns the crystal nearest the player.
            List<Crystal> best = null;
            float bestSq = float.MaxValue;
            foreach (var g in groups)
            {
                foreach (var c in g)
                {
                    float d = Vector2.DistanceSquared(player, c.Grid);
                    if (d < bestSq)
                    {
                        bestSq = d;
                        best = g;
                    }
                }
            }

            return best ?? all;
        }

        // Greedy nearest-neighbour tour from the player, then a 2-opt pass to untangle crossings. The
        // distance metric is injected (walkable A* length, or straight line as a fallback).
        private static List<int> RouteOrder(List<Vector2> crystals, Vector2 start, Func<Vector2, Vector2, float> dist)
        {
            int n = crystals.Count;
            var order = new List<int>(n);
            var used = new bool[n];

            Vector2 cur = start;
            for (int step = 0; step < n; step++)
            {
                int best = -1;
                float bestD = float.MaxValue;
                for (int j = 0; j < n; j++)
                {
                    if (used[j])
                        continue;
                    float d = dist(cur, crystals[j]);
                    if (d < bestD)
                    {
                        bestD = d;
                        best = j;
                    }
                }

                used[best] = true;
                order.Add(best);
                cur = crystals[best];
            }

            TwoOpt(crystals, order, start, dist);
            return order;
        }

        // Standard 2-opt over the open path, anchored at the (fixed) player start.
        private static void TwoOpt(List<Vector2> crystals, List<int> order, Vector2 start, Func<Vector2, Vector2, float> dist)
        {
            int n = order.Count;
            if (n < 3)
                return;

            Vector2 P(int idx) => crystals[order[idx]];

            bool improved = true;
            int guard = 0;
            while (improved && guard++ < 64)
            {
                improved = false;
                for (int i = 0; i < n - 1; i++)
                {
                    Vector2 a = i == 0 ? start : P(i - 1);
                    for (int k = i + 1; k < n; k++)
                    {
                        Vector2 b = P(i);
                        Vector2 c = P(k);
                        Vector2 d = k + 1 < n ? P(k + 1) : c;
                        float before = dist(a, b) + (k + 1 < n ? dist(c, d) : 0f);
                        float after = dist(a, c) + (k + 1 < n ? dist(b, d) : 0f);
                        if (after + 0.01f < before)
                        {
                            order.Reverse(i, k - i + 1);
                            improved = true;
                        }
                    }
                }
            }
        }
    }
}
