namespace SekhemaHelper
{
    using GameHelper;
    using GameHelper.Plugin;
    using GameHelper.RemoteEnums;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.Utils;
    using ImGuiNET;
    using Newtonsoft.Json;
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Numerics;
    using System.Runtime.InteropServices;
    using System.Text;

    public sealed class SekhemaHelperCore : PCore<SekhemaHelperSettings>
    {
        private string SettingPathname => Path.Join(DllDirectory, "config", "settings.txt");
        private WeightCalculator weightCalculator;
        private bool dumpRequested;

        public override void OnEnable(bool isGameOpened)
        {
            if (File.Exists(SettingPathname))
            {
                var content = File.ReadAllText(SettingPathname);
                var opts = new JsonSerializerSettings { ObjectCreationHandling = ObjectCreationHandling.Replace };
                Settings = JsonConvert.DeserializeObject<SekhemaHelperSettings>(content, opts) ?? Settings;
            }

            this.weightCalculator = new WeightCalculator(Settings);
        }

        public override void OnDisable() => Mem.Close();

        public override void SaveSettings()
        {
            var dir = Path.GetDirectoryName(SettingPathname);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(SettingPathname, JsonConvert.SerializeObject(Settings, Formatting.Indented));
        }

        public override void DrawSettings()
        {
            ImGui.SeparatorText("Profile");
            if (ImGui.BeginCombo("Active Profile", Settings.CurrentProfile))
            {
                foreach (var name in Settings.Profiles.Keys)
                {
                    bool sel = name == Settings.CurrentProfile;
                    if (ImGui.Selectable(name, sel))
                        Settings.CurrentProfile = name;
                    if (sel)
                        ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }

            ImGui.SeparatorText("Display");
            ImGui.Checkbox("Draw Best Path", ref Settings.DrawBestPath);
            ImGui.SliderFloat("Frame Thickness", ref Settings.FrameThickness, 1f, 10f);
            ColorSwatch("Best Path Color", ref Settings.BestPathColor);
            ImGui.Checkbox("Debug (show weights)", ref Settings.DebugEnable);
            if (Settings.DebugEnable)
            {
                ColorSwatch("Debug Text Color", ref Settings.TextColor);
                ColorSwatch("Debug Background", ref Settings.BackgroundColor);
                // Dump every room's content FK pairs (+ the FloorData content vector) to
                // config\fk_dump.txt on the next frame the Trial map is open. Use to verify
                // classification / re-map FK offsets after a client patch.
                if (ImGui.Button("Dump FK -> config\\fk_dump.txt"))
                    this.dumpRequested = true;
            }

            DrawWeightSettings();
        }

        // Edit the active profile's weights in place. Everything lives in Settings.Profiles, which is
        // serialized to config\settings.txt, so edits persist and load with the profile; the
        // WeightCalculator reads the active profile every frame, so changes take effect live.
        private void DrawWeightSettings()
        {
            if (!Settings.Profiles.TryGetValue(Settings.CurrentProfile, out var profile) || profile == null)
                return;

            ImGui.SeparatorText($"Weights — {Settings.CurrentProfile}");
            ImGui.TextDisabled("Higher = more desirable. Drag to adjust (Ctrl+click to type). Saved to config.");

            if (ImGui.Button("Reset this profile to defaults"))
            {
                Settings.Profiles[Settings.CurrentProfile] = Settings.CurrentProfile == "No-Hit"
                    ? ProfileContent.CreateNoHitProfile()
                    : ProfileContent.CreateDefaultProfile();
                return;
            }

            DrawWeightGroup("Room types", profile.RoomTypeWeights);
            DrawWeightGroup("Afflictions", profile.AfflictionWeights);
            DrawWeightGroup("Rewards", profile.RewardWeights);
        }

        // One collapsible group of {name → weight} sliders. Edits the dictionary value in place.
        private static void DrawWeightGroup(string title, Dictionary<string, float> weights)
        {
            if (weights == null || weights.Count == 0)
                return;
            if (!ImGui.CollapsingHeader($"{title} ({weights.Count})"))
                return;

            ImGui.PushID(title);
            ImGui.Indent();
            var keys = new List<string>(weights.Keys);
            keys.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var key in keys)
            {
                float v = weights[key];
                if (ImGui.DragFloat(key, ref v, 10f, -1_000_000f, 1_000_000f, "%.0f"))
                    weights[key] = v;
            }
            ImGui.Unindent();
            ImGui.PopID();
        }

        public override void DrawUI()
        {
            // Draw while the game OR GameHelper itself is foreground (so config/debug is visible too).
            bool ghForeground = Process.GetCurrentProcess().MainWindowHandle == GetForegroundWindow();
            if (!Core.Process.Foreground && !ghForeground)
                return;

            var drawList = ImGui.GetBackgroundDrawList();
            var gameUi = Core.States.InGameStateObject.GameUi;
            var panel = gameUi?.SekhemasTrialMapPanel;
            if (panel == null)
            {
                DebugHud(drawList, "panel = null (not in Trial?)");
                return;
            }
            if (!panel.IsVisible)
            {
                DebugHud(drawList, $"panel 0x{panel.Address.ToInt64():X} not visible (map closed)");
                return;
            }

            var floor = SekhemaReader.Read(panel);
            DebugHud(drawList, $"panel 0x{panel.Address.ToInt64():X}  {floor.Status}");
            if (!floor.IsValid)
                return;

            if (this.dumpRequested)
            {
                this.dumpRequested = false;
                DumpFk(floor);
            }

            ClassifyRooms(floor);

            // Player stats for dynamic affliction weights (best-effort; deltas summed).
            this.weightCalculator ??= new WeightCalculator(Settings);
            ReadPlayerStats(out var evasion, out var es, out var armour, out var life, out var qotf);
            this.weightCalculator.Evasion = evasion;
            this.weightCalculator.EnergyShield = es;
            this.weightCalculator.Armour = armour;
            this.weightCalculator.Life = life;
            this.weightCalculator.HasQueenOfTheForest = qotf;

            // Weights per room.
            var weights = new Dictionary<(int, int), double>();
            var debugTexts = new Dictionary<(int, int), string>();
            for (int l = 0; l < floor.Layers.Count; l++)
                for (int r = 0; r < floor.Layers[l].Count; r++)
                {
                    var (w, dbg) = this.weightCalculator.Calculate(floor.Layers[l][r]);
                    weights[(l, r)] = w;
                    debugTexts[(l, r)] = dbg;
                }

            if (Settings.DebugEnable)
            {
                foreach (var layer in floor.Layers)
                    foreach (var room in layer)
                    {
                        if (!room.HasWidget)
                            continue;
                        var txt = $"W:{weights[(room.Layer, room.Index)]:F0}\n{debugTexts[(room.Layer, room.Index)]}";
                        drawList.AddText(room.ScreenPos, ImGuiHelper.Color(Settings.TextColor), txt);
                    }
            }

            if (Settings.DrawBestPath)
            {
                var path = PathFinder.FindBestPath(floor, weights);
                uint color = ImGuiHelper.Color(Settings.BestPathColor);
                foreach (var (l, r) in path)
                {
                    if (l == floor.PlayerLayer && r == floor.PlayerRoom)
                        continue;
                    var room = floor.Get(l, r);
                    if (room == null || !room.HasWidget)
                        continue;
                    drawList.AddRect(room.ScreenPos, room.ScreenPos + room.ScreenSize,
                        color, 0f, ImDrawFlags.None, Settings.FrameThickness);
                }
            }
        }

        // Classify each revealed room from the FloorData content vector (FloorData+0x18, stride 0x40),
        // each entry keyed by (layer,room) in its first two bytes and carrying up to 3 content FK
        // pairs {row, table} at +0x08 + k*0x10:
        //   SanctumRooms.dat  -> fight room "Caverns_<TYPE>_NN" (type) OR "Caverns_Treasure..." (reward)
        //   SanctumPersistentEffects.dat -> affliction (display name at row+0x28)
        //
        // This reads the game-data structure directly (verified live 2026-06-20: ids like
        // "Caverns_Arena_02" and affliction names like "Fiendish Wings" resolve here). It replaces the
        // old per-widget read at widget+0x4D8, which a client patch left empty — every room then fell
        // back to base weight, so editing the weight tables had no effect on the chosen path. Only
        // revealed rooms have entries in this vector; deeper rooms stay unclassified (weight = base).
        private static void ClassifyRooms(SekhemaFloor floor)
        {
            if (floor.FloorDataAddr == IntPtr.Zero)
                return;
            var first = Mem.Read<IntPtr>(floor.FloorDataAddr + 0x18);
            var last = Mem.Read<IntPtr>(floor.FloorDataAddr + 0x20);
            if (first == IntPtr.Zero || last.ToInt64() <= first.ToInt64())
                return;
            long count = (last.ToInt64() - first.ToInt64()) / 0x40;
            for (long i = 0; i < count && i < 512; i++)
            {
                var e = first + (int)(i * 0x40);
                int layer = Mem.Read<byte>(e + 0x00);
                int idx = Mem.Read<byte>(e + 0x01);
                var room = floor.Get(layer, idx);
                if (room == null)
                    continue;
                for (int k = 0; k < 3; k++)
                {
                    var rowPtr = Mem.Read<IntPtr>(e + 0x08 + k * 0x10);
                    var tablePtr = Mem.Read<IntPtr>(e + 0x10 + k * 0x10);
                    if (rowPtr == IntPtr.Zero || tablePtr == IntPtr.Zero)
                        continue;
                    var tpath = Mem.ReadWideString(Mem.Read<IntPtr>(tablePtr + 0x08), 96);
                    if (string.IsNullOrEmpty(tpath))
                        continue;

                    if (tpath.IndexOf("SanctumPersistentEffects", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var name = Mem.ReadWideString(Mem.Read<IntPtr>(rowPtr + 0x28), 48);
                        if (!string.IsNullOrEmpty(name))
                            room.Affliction = name;
                    }
                    else if (tpath.IndexOf("SanctumRooms", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var id = Mem.ReadWideString(Mem.Read<IntPtr>(rowPtr + 0x00), 64);
                        if (string.IsNullOrEmpty(id))
                            continue;
                        if (id.IndexOf("Treasure", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            var rw = MapReward(id);
                            if (rw != null)
                                room.Reward = rw;
                        }
                        else
                        {
                            var t = ExtractRoomType(id);
                            if (t != null)
                                room.RoomType = t;
                        }
                    }
                }
            }
        }

        // Map the SanctumRooms.Id token (parts[1]) to the in-game DISPLAY name the player sees, which
        // is also what the legacy PathfindSanctum profile keys on. Resolved offline (2026-06-10) by
        // joining SanctumRooms.RoomType -> SanctumRoomTypes display-name array (dump_dat.py):
        //   token   RoomType      UI display ("… Trial")
        //   Arena   TimerArena    Hourglass
        //   Ritual  PortalArena   Ritual
        //   Lair    Lair          Chalice
        //   Explore Explore       Escape
        //   Gauntlet Gauntlet     Gauntlet   (identity)
        //   Boss    Boss          (per-floor boss name)  (identity token kept)
        private static readonly Dictionary<string, string> TokenToDisplay = new(StringComparer.Ordinal)
        {
            ["Arena"] = "Hourglass",
            ["Lair"] = "Chalice",
            ["Explore"] = "Escape",
            // Ritual/Gauntlet/Boss tokens already match their display short name.
        };

        // "Caverns_Gauntlet_02" -> "Gauntlet"; "Caverns_Arena_03" -> "Hourglass" (UI display name).
        private static string ExtractRoomType(string id)
        {
            var parts = id.Split('_');
            if (parts.Length < 2)
                return null;
            var tok = parts[1];
            return TokenToDisplay.TryGetValue(tok, out var t) ? t : tok;
        }

        // Map a "<Region>_Treasure..." reward-room id to a ProfileContent reward name.
        // Region prefix is the floor tier (Caverns/Ruins/Depths/...); the token after "Treasure" is the kind.
        // Confirmed kinds (FOUND.md, all 4 floors): Key{Bronze,Silver,Gold}, Chest{Bronze,Silver,Gold},
        // Water{Minor,Major}, Merchant_01, LegendWater, LegendPledge, LegendHonor, LegendBoon,
        // LegendCurse, LegendRandom. Unknown -> null (base weight).
        private static string MapReward(string id)
        {
            string s = id.ToLowerInvariant();
            if (s.Contains("key"))
                return s.Contains("gold") ? "Gold Key" : s.Contains("silver") ? "Silver Key" : "Bronze Key";
            if (s.Contains("chest") || s.Contains("cache"))
                return s.Contains("gold") ? "Golden Cache" : s.Contains("silver") ? "Silver Cache" : "Bronze Cache";
            if (s.Contains("water") || s.Contains("fountain"))
                return (s.Contains("legend") || s.Contains("major") || s.Contains("large")) ? "Large Fountain" : "Fountain";
            if (s.Contains("merchant"))
                return "Merchant";
            if (s.Contains("pledge"))
                return "Pledge to Kochai";
            // The client carries no deity for the honour-shrine reward (PoE1 had Halani/Galai/...);
            // it is a single generic "Honour" reward in PoE2 0.5.x.
            if (s.Contains("honor") || s.Contains("honour"))
                return "Honour";
            if (s.Contains("boon"))
                return "Boon";
            if (s.Contains("curse"))
                return "Curse";
            if (s.Contains("random"))
                return "Random";
            return null;
        }

        // Debug recon: dump each room's content FK pairs (row, table, table-path) + any readable
        // strings in the FK row, to config\fk_dump.txt. Used to map FK -> names (room type /
        // affliction / reward) so weights can be wired. Content block lives at widget+0x4D8.
        private void DumpFk(SekhemaFloor floor)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"FloorData=0x{floor.FloorDataAddr.ToInt64():X} mapElement=0x{floor.MapElement.ToInt64():X} " +
                          $"player=({floor.PlayerLayer},{floor.PlayerRoom})");
            foreach (var layer in floor.Layers)
            {
                foreach (var room in layer)
                {
                    if (!room.HasWidget)
                        continue;
                    sb.AppendLine($"== ({room.Layer},{room.Index}) widget=0x{room.WidgetAddr.ToInt64():X} " +
                                  $"chosen={room.IsChosen} conn=[{string.Join(",", room.NextConnections)}] ==");
                    // Room-struct FK (FloorData room +0x28/+0x30) — available for ALL rooms incl. hidden?
                    {
                        var rrow = Mem.Read<IntPtr>(room.Address + 0x28);
                        var rtab = Mem.Read<IntPtr>(room.Address + 0x30);
                        if (rrow != IntPtr.Zero || rtab != IntPtr.Zero)
                        {
                            var tp = rtab != IntPtr.Zero ? Mem.ReadWideString(Mem.Read<IntPtr>(rtab + 0x08), 80) : "";
                            var id = rrow != IntPtr.Zero ? Mem.ReadWideString(Mem.Read<IntPtr>(rrow + 0x00), 48) : "";
                            sb.AppendLine($"  roomStruct+0x28: table=\"{tp}\" id=\"{id}\"");
                        }
                    }
                    for (int k = 0; k < 4; k++)
                    {
                        var slot = room.WidgetAddr + 0x4D8 + k * 0x10;
                        var rowPtr = Mem.Read<IntPtr>(slot);
                        var tablePtr = Mem.Read<IntPtr>(slot + 8);
                        if (rowPtr == IntPtr.Zero && tablePtr == IntPtr.Zero)
                            continue;
                        string tpath = tablePtr != IntPtr.Zero
                            ? Mem.ReadWideString(Mem.Read<IntPtr>(tablePtr + 0x08), 96) : "";
                        sb.AppendLine($"  FK[{k}] row=0x{rowPtr.ToInt64():X} table=0x{tablePtr.ToInt64():X} \"{tpath}\"");
                        if (rowPtr != IntPtr.Zero)
                            for (int off = 0; off <= 0xC0; off += 8)
                            {
                                var p = Mem.Read<IntPtr>(rowPtr + off);
                                if (p == IntPtr.Zero)
                                    continue;
                                var s = Mem.ReadWideString(p, 48);
                                if (Printable(s))
                                    sb.AppendLine($"    row+0x{off:X}: \"{s}\"");
                            }
                    }
                }
            }
            // Also dump the FloorData content vector (+0x18, stride 0x40, keyed by layer/room):
            // if hidden cells have entries here, we can classify ahead of reveal.
            sb.AppendLine();
            sb.AppendLine("== FloorData content vector (FloorData+0x18) ==");
            var cFirst = Mem.Read<IntPtr>(floor.FloorDataAddr + 0x18);
            var cLast = Mem.Read<IntPtr>(floor.FloorDataAddr + 0x20);
            long ccount = (cFirst != IntPtr.Zero && cLast.ToInt64() > cFirst.ToInt64())
                ? (cLast.ToInt64() - cFirst.ToInt64()) / 0x40 : 0;
            sb.AppendLine($"  entries={ccount}");
            for (long i = 0; i < ccount && i < 512; i++)
            {
                var e = cFirst + (int)(i * 0x40);
                byte el = Mem.Read<byte>(e + 0x00);
                byte er = Mem.Read<byte>(e + 0x01);
                sb.Append($"  [{i,3}] key=({el},{er})");
                for (int k = 0; k < 3; k++)
                {
                    var row = Mem.Read<IntPtr>(e + 0x08 + k * 0x10);
                    var table = Mem.Read<IntPtr>(e + 0x10 + k * 0x10);
                    if (row == IntPtr.Zero || table == IntPtr.Zero)
                        continue;
                    var tp = Mem.ReadWideString(Mem.Read<IntPtr>(table + 0x08), 80);
                    int slash = tp.LastIndexOf('/');
                    var tshort = slash >= 0 ? tp.Substring(slash + 1) : tp;
                    var id = Mem.ReadWideString(Mem.Read<IntPtr>(row + 0x00), 48);
                    var nm = Mem.ReadWideString(Mem.Read<IntPtr>(row + 0x28), 48);
                    sb.Append($"  FK{k}[{tshort}] id=\"{id}\" n28=\"{nm}\"");
                }
                sb.AppendLine();
            }

            var dir = Path.GetDirectoryName(SettingPathname);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Join(dir, "fk_dump.txt"), sb.ToString());
        }

        private static bool Printable(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length < 2)
                return false;
            foreach (var c in s)
                if (c < 0x20 || c > 0x7E)
                    return false;
            return true;
        }

        private static void ReadPlayerStats(out int evasion, out int energyShield, out int armour, out int life, out bool qotf)
        {
            evasion = 0;
            energyShield = 0;
            armour = 0;
            life = 0;
            qotf = false;
            var player = Core.States.InGameStateObject.CurrentAreaInstance.Player;
            if (player == null || !player.TryGetComponent<Stats>(out var stats))
                return;
            // Confirmed live 2026-06-10 (Research --stats vs character sheet):
            //  - effective Evasion / Max ES match StatsChangedByItems (NOT the sum of both dicts).
            //    The items layer is the player's OWN gear/passive total; the buff layer can carry
            //    transient externals (e.g. another player's ES aura), which must NOT count for a
            //    solo Sekhema run, so we read the items layer here.
            //  - the Queen-of-the-Forest stat is granted via the buff/action layer
            //    (key 9490 movement_speed_is_only_base_…_per_x_evasion_rating: items=0, buffs>0 when worn).
            // Armour (235) and Max Life (239) added for the relevance-based affliction model
            // (see WeightCalculator.DynamicAffliction). Stat ids verified vs game_dat/Stats.tsv.
            evasion = ItemStat(stats, GameStats.evasion_rating);
            energyShield = ItemStat(stats, GameStats.maximum_energy_shield);
            armour = ItemStat(stats, GameStats.armour);
            life = ItemStat(stats, GameStats.maximum_life);
            qotf = StatValue(stats, GameStats.movement_speed_is_only_base_positive_1percentage_per_x_evasion_rating) > 0;
        }

        // Effective value from the items layer (matches the in-game character sheet).
        private static int ItemStat(Stats stats, GameStats key)
            => stats.StatsChangedByItems != null && stats.StatsChangedByItems.TryGetValue(key, out var v) ? v : 0;

        // Sum across both stat dicts (used for buff/action-granted stats like QotF).
        private static int StatValue(Stats stats, GameStats key)
        {
            int v = 0;
            if (stats.StatsChangedByItems != null && stats.StatsChangedByItems.TryGetValue(key, out var a))
                v += a;
            if (stats.StatsChangedByBuffAndActions != null && stats.StatsChangedByBuffAndActions.TryGetValue(key, out var b))
                v += b;
            return v;
        }

        // Always-on status line (when Debug is on) so failures are visible instead of a blank overlay.
        private void DebugHud(ImDrawListPtr drawList, string text)
        {
            if (!Settings.DebugEnable)
                return;
            var pos = new Vector2(20f, 120f);
            var size = ImGui.CalcTextSize(text);
            drawList.AddRectFilled(pos - new Vector2(4, 2), pos + size + new Vector2(4, 2),
                ImGuiHelper.Color(new Vector4(0f, 0f, 0f, 0.8f)));
            drawList.AddText(pos, ImGuiHelper.Color(new Vector4(1f, 0.9f, 0.2f, 1f)), "SekhemaHelper: " + text);
        }

        [DllImport("user32.dll")]
        private static extern nint GetForegroundWindow();

        private static void ColorSwatch(string label, ref Vector4 color)
        {
            if (ImGui.ColorButton(label, color))
                ImGui.OpenPopup(label);
            ImGui.SameLine();
            ImGui.Text(label);
            if (ImGui.BeginPopup(label))
            {
                ImGui.ColorPicker4(label, ref color);
                ImGui.EndPopup();
            }
        }
    }
}
