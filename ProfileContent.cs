using System.Collections.Generic;

namespace SekhemaHelper
{
    // Weight profile ported verbatim from the legacy PathfindSanctum-PoE2 plugin.
    // Keyed by in-game display names of room type / affliction / reward.
    public class ProfileContent
    {
        public Dictionary<string, float> RoomTypeWeights { get; set; }
        public Dictionary<string, float> AfflictionWeights { get; set; }
        public Dictionary<string, float> RewardWeights { get; set; }

        private static ProfileContent CreateBaseProfile()
        {
            return new ProfileContent
            {
                RoomTypeWeights = new()
                {
                    // Keyed by the in-game DISPLAY name (same vocabulary the legacy PathfindSanctum
                    // profile used). ExtractRoomType maps SanctumRooms.Id tokens to these via the
                    // token->RoomType->display join (dump_dat.py, 2026-06-10):
                    //   Hourglass=TimerArena(Arena), Chalice=Lair(Lair), Escape=Explore(Explore),
                    //   Ritual=PortalArena(Ritual), Gauntlet, Boss.
                    ["Gauntlet"] = -1000,
                    ["Hourglass"] = -200,
                    ["Chalice"] = 0,
                    ["Ritual"] = 0,
                    ["Escape"] = 100,
                    ["Boss"] = 0,
                },
                AfflictionWeights = new()
                {
                    ["Orbala's Leathers"] = 0,
                    ["Glass Shard"] = -4000,
                    ["Ghastly Scythe"] = -4000,
                    ["Veiled Sight"] = -4000,
                    ["Myriad Aspersions"] = -4000,
                    ["Deceptive Mirror"] = -4000,
                    ["Purple Smoke"] = -4000,
                    ["Golden Smoke"] = -400,
                    ["Red Smoke"] = -4000,
                    ["Black Smoke"] = -4000,
                    ["Rapid Quicksand"] = -1000,
                    ["Deadly Snare"] = -1000,
                    ["Forgotten Traditions"] = -1000,
                    ["Season of Famine"] = -1000,
                    ["Orb of Negation"] = -1000,
                    ["Winter Drought"] = -1000,
                    ["Branded Balbalakh"] = -1000,
                    ["Chiselled Stone"] = -1000,
                    ["Weakened Flesh"] = -100,
                    ["Untouchable"] = -1000,
                    ["Costly Aid"] = -900,
                    ["Blunt Sword"] = -1000,
                    ["Spiked Shell"] = -1000,
                    ["Suspected Sympathiser"] = -200,
                    ["Haemorrhage"] = -100,
                    ["Corrosive Concoction"] = 0,
                    ["Iron Manacles"] = 0,
                    ["Shattered Shield"] = 0,
                    ["Unquenched Thirst"] = -200,
                    ["Unassuming Brick"] = -1000,
                    ["Tradition's Demand"] = -800,
                    ["Fiendish Wings"] = -400,
                    ["Hungry Fangs"] = -600,
                    ["Worn Sandals"] = -400,
                    ["Trade Tariff"] = -300,
                    ["Death Toll"] = -400,
                    ["Spiked Exit"] = -300,
                    ["Exhausted Wells"] = 0,
                    ["Gate Toll"] = -100,
                    ["Leaking Waterskin"] = -100,
                    ["Low Rivers"] = -100,
                    ["Sharpened Arrowhead"] = 0,
                    ["Rusted Mallet"] = 0,
                    ["Chains of Binding"] = 0,
                    ["Dishonoured Tattoo"] = 0,
                    ["Tattered Blindfold"] = 0,
                    ["Dark Pit"] = 0,
                    ["Honed Claws"] = 0,
                },
                RewardWeights = new()
                {
                    ["Gold Key"] = 0,
                    ["Silver Key"] = 0,
                    ["Bronze Key"] = 0,
                    ["Golden Cache"] = 0,
                    ["Silver Cache"] = 0,
                    ["Bronze Cache"] = 0,
                    ["Large Fountain"] = 100,
                    ["Fountain"] = 50,
                    ["Pledge to Kochai"] = 20,
                    ["Honour Halani"] = 8,   // legacy deity-specific keys — PoE1 only
                    ["Honour Ahkeli"] = -1,
                    ["Honour Orbala"] = 50,
                    ["Honour Galai"] = 300,
                    ["Honour Tabana"] = 0,
                    ["Merchant"] = 20,
                    // PoE2 0.5.x Legend* reward rooms (FOUND.md). Conservative defaults — tune to taste.
                    ["Honour"] = 50,   // generic honour shrine (no deity in PoE2 data)
                    ["Boon"] = 200,    // grants a powerful boon
                    ["Curse"] = 0,     // reward at the cost of an affliction
                    ["Random"] = 0,
                }
            };
        }

        public static ProfileContent CreateDefaultProfile() => CreateBaseProfile();

        public static ProfileContent CreateNoHitProfile()
        {
            var profile = CreateBaseProfile();
            profile.RoomTypeWeights["Gauntlet"] = -200;
            profile.RoomTypeWeights["Hourglass"] = -1000;   // timed arena (TimerArena) — risky for no-hit
            profile.AfflictionWeights["Death Toll"] = -500000;
            profile.AfflictionWeights["Spiked Exit"] = -600000;
            profile.AfflictionWeights["Deceptive Mirror"] = -400000;
            profile.AfflictionWeights["Glass Shard"] = -50000;
            profile.AfflictionWeights["Myriad Aspersions"] = -50000;
            profile.AfflictionWeights["Ghastly Scythe"] = 0;
            profile.AfflictionWeights["Deadly Snare"] = 0;
            profile.AfflictionWeights["Branded Balbalakh"] = 0;
            profile.AfflictionWeights["Chiselled Stone"] = 0;
            profile.AfflictionWeights["Weakened Flesh"] = 0;
            profile.AfflictionWeights["Costly Aid"] = 0;
            profile.AfflictionWeights["Suspected Sympathiser"] = 0;
            profile.AfflictionWeights["Haemorrhage"] = 0;
            profile.AfflictionWeights["Leaking Waterskin"] = 0;
            profile.AfflictionWeights["Rusted Mallet"] = 0;
            profile.AfflictionWeights["Chains of Binding"] = 0;
            profile.AfflictionWeights["Dishonoured Tattoo"] = 0;
            profile.AfflictionWeights["Dark Pit"] = 0;
            profile.AfflictionWeights["Honed Claws"] = 0;
            profile.AfflictionWeights["Hungry Fangs"] = 0;
            return profile;
        }

        public ProfileContent Clone() => new()
        {
            RoomTypeWeights = new Dictionary<string, float>(RoomTypeWeights),
            AfflictionWeights = new Dictionary<string, float>(AfflictionWeights),
            RewardWeights = new Dictionary<string, float>(RewardWeights),
        };
    }
}
