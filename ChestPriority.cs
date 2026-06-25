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
    using System.Text.RegularExpressions;

    // Final-room chest helper (SEKHEMA_WIP §11.3/§11.5). The reward room spawns MarakethSanctum chest
    // entities whose metadata id encodes everything we need: "…/MarakethSanctum/{Bronze|Silver|Gold}
    // Chest{Content}{1|2|3}" — TIER = which key opens it, Content = reward category, trailing 1/2/3 =
    // quality (1 base / 2 Superior / 3 Prime). Each chest's reward is its identity, so no opening needed.
    //
    // For each tier we rank that tier's chests by the user's per-content priority (quality + proximity as
    // tie-breakers) and highlight the top-N on the large map, where N = the live key count of that tier
    // (SEKHEMA_WIP §11.4). Drawing reuses HazardRoute's large-map (Radar) projection.
    internal static class ChestPriority
    {
        private const string ChestPathFragment = "/MarakethSanctum/";
        private static readonly Regex ChestRe =
            new(@"(Bronze|Silver|Gold)Chest([A-Za-z]+?)([123])?$", RegexOptions.Compiled);

        // Priority is an ORDERED list: position decides rank, top = best. A chest's content priority is
        // its place in this list (earlier = higher). Content types absent from the list (the low-value
        // gear/weapon chests we don't rank, e.g. Helmet/Boots, or a type added by a patch) score
        // UnlistedPriority and are NEVER selected — they only ever show as a dim dot. So "removing" a
        // type from the list means "don't mark it", not "mark it last".
        private const int UnlistedPriority = 0;

        // Default ranking (top = best). Only the content types worth steering toward are listed; everything
        // else (Gold/Ring/Amulet/Belt/Generic/armour/weapons/…) is intentionally unranked → lowest.
        public static List<string> DefaultOrder() => new()
        {
            "GrandSpectrum",
            "RadiusJewels",
            "LargeRelic",
            "Jewels",
            "Currency",
            "MediumRelic",
            "SmallRelic",
            "Maps",
            "Generic",
        };

        // Content types disabled (un-ticked) by default — listed for ordering but not marked until enabled.
        public static System.Collections.Generic.HashSet<string> DefaultDisabled() =>
            new(StringComparer.OrdinalIgnoreCase) { "Currency", "MediumRelic", "SmallRelic", "Maps", "Generic" };

        // Rank of a content type for sorting: higher number = higher priority. Listed-and-enabled types
        // score by their position (top of the list scores highest); unlisted OR user-disabled types score
        // UnlistedPriority (never marked).
        public static int PriorityOf(SekhemaHelperSettings settings, string content)
        {
            var order = settings?.ChestPriorityOrder;
            if (order == null || string.IsNullOrEmpty(content))
                return UnlistedPriority;
            if (settings.ChestDisabledContent != null && settings.ChestDisabledContent.Contains(content))
                return UnlistedPriority;   // switched off in the priority table
            for (int i = 0; i < order.Count; i++)
                if (string.Equals(order[i], content, StringComparison.OrdinalIgnoreCase))
                    return order.Count - i;   // index 0 -> order.Count (highest)
            return UnlistedPriority;
        }

        // Calibration copied from HazardRoute/Radar so placement matches the rest of the plugin.
        private const float LargeMapXBias = 0.6f;
        private const float LargeMapYBias = 0.3f;
        private const float LargeMapScaleBaseline = 0.187812f;
        private static readonly double CameraAngle = 38.7 * Math.PI / 180;

        private enum Tier { Bronze, Silver, Gold }

        private struct ChestInfo
        {
            public uint Id;
            public Tier Tier;
            public string Content;   // e.g. "Currency"
            public int Quality;      // 1 base / 2 Superior / 3 Prime
            public int Priority;     // resolved content priority
            public Vector2 Grid;
            public Vector2 Screen;
            public float Dist;       // grid distance from the player (proximity tie-break)
            public bool Selected;    // within this tier's key budget
        }

        // nBronze/nSilver/nGold: live key counts (-1 = unknown → that tier marks nothing).
        public static void Draw(SekhemaHelperSettings settings, int nBronze, int nSilver, int nGold)
        {
            if (!settings.DrawChestPriority)
                return;
            try { DrawInner(settings, nBronze, nSilver, nGold); }
            catch { /* never bubble a draw exception into the host */ }
        }

        private static void DrawInner(SekhemaHelperSettings settings, int nBronze, int nSilver, int nGold)
        {
            var gameUi = Core.States.InGameStateObject.GameUi;
            var largeMap = gameUi?.LargeMap;
            if (largeMap == null || !largeMap.IsVisible || gameUi.WorldMapPanel.IsVisible)
                return;

            var area = Core.States.InGameStateObject.CurrentAreaInstance;
            if (area?.Player == null || !area.Player.TryGetComponent<Render>(out var playerRender))
                return;
            var trackingPos = new Vector2(playerRender.GridPosition.X, playerRender.GridPosition.Y);
            float trackingHeight = playerRender.TerrainHeight;

            // ---- Radar large-map projection (1:1 with HazardRoute) ----
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

            var chests = CollectChests(settings, area, trackingPos, trackingHeight, center, cos, sin);
            if (chests.Count == 0)
                return;

            SelectByBudget(chests, Tier.Bronze, nBronze);
            SelectByBudget(chests, Tier.Silver, nSilver);
            SelectByBudget(chests, Tier.Gold, nGold);

            var dl = ImGui.GetForegroundDrawList();
            uint ring = ImGuiHelper.Color(settings.ChestMarkerColor);
            uint labelBg = ImGuiHelper.Color(new Vector4(0f, 0f, 0f, 0.65f));
            uint labelFg = ImGuiHelper.Color(new Vector4(1f, 1f, 1f, 1f));
            var font = ImGui.GetFont();
            float fontPx = ImGui.GetFontSize();

            foreach (var c in chests)
            {
                if (!c.Selected)
                    continue;

                uint fill = TierColor(c.Tier);
                dl.AddCircleFilled(c.Screen, settings.ChestMarkerRadius, fill, 18);
                dl.AddCircle(c.Screen, settings.ChestMarkerRadius, ring, 18, 2f);

                // Label: content + quality (P/S for Prime/Superior) above the marker.
                string q = c.Quality == 3 ? " P" : c.Quality == 2 ? " S" : string.Empty;
                var text = $"{c.Content}{q}";
                var ts = ImGui.CalcTextSize(text);
                var at = new Vector2(c.Screen.X - (ts.X * 0.5f), c.Screen.Y - settings.ChestMarkerRadius - ts.Y - 3f);
                var pad = new Vector2(3f, 1f);
                dl.AddRectFilled(at - pad, at + ts + pad, labelBg, 2f);
                dl.AddText(font, fontPx, at, labelFg, text);
            }
        }

        private static List<ChestInfo> CollectChests(
            SekhemaHelperSettings settings, AreaInstance area, Vector2 player, float playerHeight,
            Vector2 center, float cos, float sin)
        {
            var list = new List<ChestInfo>();
            foreach (var kv in area.AwakeEntities)
            {
                var e = kv.Value;
                if (e == null || string.IsNullOrEmpty(e.Path) || e.Path.IndexOf(ChestPathFragment, StringComparison.Ordinal) < 0)
                    continue;
                if (!TryParse(e.Path, out var tier, out var content, out var quality))
                    continue;
                if (!e.TryGetComponent<Render>(out var r))
                    continue;
                // Skip already-opened chests when the game exposes that via a Chest component flag.
                if (e.TryGetComponent<Chest>(out var chestComp) && chestComp.IsOpened)
                    continue;

                var grid = new Vector2(r.GridPosition.X, r.GridPosition.Y);
                var delta = grid - player;
                float deltaZ = (r.TerrainHeight - playerHeight) / 10.86957f;
                var screen = center + new Vector2((delta.X - delta.Y) * cos, (deltaZ - (delta.X + delta.Y)) * sin);

                int prio = PriorityOf(settings, content);

                list.Add(new ChestInfo
                {
                    Id = e.Id,
                    Tier = tier,
                    Content = content,
                    Quality = quality,
                    Priority = prio,
                    Grid = grid,
                    Screen = screen,
                    Dist = Vector2.Distance(player, grid),
                    Selected = false,
                });
            }
            return list;
        }

        // Mark the top-N chests of one tier (N = key count) by priority, then quality, then proximity.
        private static void SelectByBudget(List<ChestInfo> chests, Tier tier, int keys)
        {
            if (keys <= 0)
                return;
            var idx = new List<int>();
            for (int i = 0; i < chests.Count; i++)
                // Only rank listed content types; unlisted (gear/weapon) chests are never marked.
                if (chests[i].Tier == tier && chests[i].Priority > UnlistedPriority)
                    idx.Add(i);
            idx.Sort((a, b) =>
            {
                var x = chests[a];
                var y = chests[b];
                if (x.Priority != y.Priority) return y.Priority.CompareTo(x.Priority); // higher first
                if (x.Quality != y.Quality) return y.Quality.CompareTo(x.Quality);     // Prime first
                return x.Dist.CompareTo(y.Dist);                                       // nearer first
            });
            int take = Math.Min(keys, idx.Count);
            for (int k = 0; k < take; k++)
            {
                var c = chests[idx[k]];
                c.Selected = true;
                chests[idx[k]] = c;
            }
        }

        private static bool TryParse(string path, out Tier tier, out string content, out int quality)
        {
            tier = Tier.Bronze; content = string.Empty; quality = 1;
            int at = path.IndexOf(ChestPathFragment, StringComparison.Ordinal);
            if (at < 0)
                return false;
            string leaf = path.Substring(at + ChestPathFragment.Length);
            var m = ChestRe.Match(leaf);
            if (!m.Success)
                return false;
            tier = m.Groups[1].Value switch
            {
                "Silver" => Tier.Silver,
                "Gold" => Tier.Gold,
                _ => Tier.Bronze,
            };
            content = m.Groups[2].Value;
            quality = m.Groups[3].Success && int.TryParse(m.Groups[3].Value, out var q) ? q : 1;
            // "MarakethChestBase" and other non-tiered helpers won't match the tier alternation, so a
            // successful match is already a real reward chest.
            return content.Length > 0;
        }

        private static uint TierColor(Tier t) => t switch
        {
            Tier.Gold => ImGuiHelper.Color(new Vector4(1f, 0.84f, 0.1f, 0.95f)),
            Tier.Silver => ImGuiHelper.Color(new Vector4(0.82f, 0.85f, 0.95f, 0.95f)),
            _ => ImGuiHelper.Color(new Vector4(0.85f, 0.55f, 0.25f, 0.95f)),
        };

        // Debug: dump every MarakethSanctum chest entity found, parsed fields, and the per-tier selection
        // given the supplied live key counts. Triggered by a settings button.
        public static void Dump(string filePath, SekhemaHelperSettings settings, int nB, int nS, int nG)
        {
            var area = Core.States.InGameStateObject.CurrentAreaInstance;
            var sb = new StringBuilder();
            sb.AppendLine($"keys B/S/G = {nB}/{nS}/{nG}");
            if (area == null)
            {
                File.WriteAllText(filePath, "no area\n");
                return;
            }
            Vector2 pPos = default;
            if (area.Player != null && area.Player.TryGetComponent<Render>(out var pr))
                pPos = new Vector2(pr.GridPosition.X, pr.GridPosition.Y);

            var chests = new List<ChestInfo>();
            int totalChestEntities = 0;
            foreach (var kv in area.AwakeEntities)
            {
                var e = kv.Value;
                if (e == null || string.IsNullOrEmpty(e.Path) || e.Path.IndexOf(ChestPathFragment, StringComparison.Ordinal) < 0)
                    continue;
                totalChestEntities++;
                if (!TryParse(e.Path, out var tier, out var content, out var quality))
                {
                    sb.AppendLine($"id={e.Id} UNPARSED path={e.Path}");
                    continue;
                }
                bool opened = e.TryGetComponent<Chest>(out var cc) && cc.IsOpened;
                float dist = -1f;
                Vector2 grid = default;
                if (e.TryGetComponent<Render>(out var r))
                {
                    grid = new Vector2(r.GridPosition.X, r.GridPosition.Y);
                    dist = Vector2.Distance(pPos, grid);
                }
                int prio = PriorityOf(settings, content);
                chests.Add(new ChestInfo { Id = e.Id, Tier = tier, Content = content, Quality = quality, Priority = prio, Grid = grid, Dist = dist, Selected = opened });
            }
            SelectByBudget(chests, Tier.Bronze, nB);
            SelectByBudget(chests, Tier.Silver, nS);
            SelectByBudget(chests, Tier.Gold, nG);

            sb.AppendLine($"chest entities matched={totalChestEntities} parsed={chests.Count}");
            foreach (var c in chests)
                sb.AppendLine($"  [{(c.Selected ? "X" : " ")}] {c.Tier,-6} {c.Content,-14} q{c.Quality} prio={c.Priority,3} dist={c.Dist:F0} id={c.Id}");

            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(filePath, sb.ToString());
        }
    }
}
