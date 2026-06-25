using System.Text;

namespace SekhemaHelper
{
    // Base room weighting. weight = base + roomType + affliction + reward (+ connectivity for info only).
    // Connectivity is consumed by PathFinder as a lexicographic tie-breaker, not folded into the weight.
    //
    // Stat-removing afflictions are weighted DYNAMICALLY by the player's defensive profile (Armour /
    // Evasion / Energy Shield / Life / Queen-of-the-Forest): a "no Armour" curse barely matters to an
    // evasion build, while it's brutal for an armour tank. These come from the player's Stats component
    // (set by the core each refresh) — NOT the removed runState active-effect scan. When the stats are
    // unknown (all 0) the dynamic terms collapse to ~0 and the static profile weight is used instead.
    public sealed class WeightCalculator
    {
        private readonly SekhemaHelperSettings settings;
        private readonly StringBuilder debug = new();

        // Effective player stats for the current frame (set by the core each refresh via ReadPlayerStats).
        public int Evasion;
        public int EnergyShield;
        public int Armour;
        public int Life;
        public bool HasQueenOfTheForest;

        // Live resources for the current frame (set by the core each refresh). <0 = unknown → the
        // resource-aware reward suppression below is skipped (not in a Trial / not yet read).
        public int Water = -1;
        public double HonourPct = -1;

        // Relevance model tuning. K = max penalty when a curse removes the player's entire main defence
        // layer (matches the legacy "strongly avoid" magnitude). RefMitigation floors the Armour+Evasion
        // denominator so a build with little total mitigation rating scales its penalties down (nothing
        // meaningful to lose) instead of splitting 50/50 on noise.
        private const double K = 5000;
        private const double RefMitigation = 4000;

        // Deterrent applied to a SOFT-suppressed reward (Merchant with no water / honour restore at high
        // honour). Sized like a minor affliction: enough to lose to an equally-good alternative, far too
        // small to bury the room when every other path is worse. NOT a hard "never pick".
        private const double SuppressedRewardPenalty = -300;

        public WeightCalculator(SekhemaHelperSettings settings) => this.settings = settings;

        public (double weight, string debug) Calculate(SekhemaRoom room)
        {
            debug.Clear();
            if (room == null)
                return (0, string.Empty);

            var profile = settings.Profiles.TryGetValue(settings.CurrentProfile, out var p)
                ? p : ProfileContent.CreateDefaultProfile();

            double weight = 1_000_000;
            weight += RoomTypeWeight(room, profile);
            weight += AfflictionWeight(room, profile);
            weight += RewardWeight(room, profile);
            // Connectivity is NOT folded into the additive weight. As a flat +100 for >1 exit it
            // could override a better reward (two rooms feeding the SAME next room — the worse-reward one
            // winning on connectivity). It is now a strict TIE-BREAKER in PathFinder (reward first, then
            // connectivity), so it only steers among reward-equal routes. Shown here for info only.
            AppendConnectivityDebug(room);
            return (weight, debug.ToString());
        }

        private double RoomTypeWeight(SekhemaRoom room, ProfileContent profile)
        {
            if (string.IsNullOrEmpty(room.RoomType))
                return 0;
            if (profile.RoomTypeWeights.TryGetValue(room.RoomType, out var w))
            {
                debug.AppendLine($"{room.RoomType}:{w}");
                return w;
            }
            return 0;
        }

        // Penalty for the affliction a room imposes (room.Affliction, from FloorData). This is the ROOM's
        // affliction, distinct from the player's active effects (that detection was removed — no stable
        // read). Stat-removing curses are weighted dynamically by the player's defensive profile; the
        // rest fall through to the static profile table. Absent names contribute 0.
        private double AfflictionWeight(SekhemaRoom room, ProfileContent profile)
        {
            if (string.IsNullOrEmpty(room.Affliction))
                return 0;

            var dyn = DynamicAffliction(room.Affliction);
            if (dyn.HasValue)
            {
                debug.AppendLine($"{room.Affliction}:{dyn.Value:F0} (dyn)");
                return dyn.Value;
            }
            if (profile.AfflictionWeights != null && profile.AfflictionWeights.TryGetValue(room.Affliction, out var w))
            {
                debug.AppendLine($"{room.Affliction}:{w}");
                return w;
            }
            // Unknown affliction: still flag its presence so the advice isn't silently blind to it.
            debug.AppendLine($"{room.Affliction}:0 (unlisted)");
            return 0;
        }

        // Stat-removing curses, weighted by the player's defensive profile (see class header).
        //   Sharpened Arrowhead  "You have no Armour"                       -> -K * armourRel
        //   Iron Manacles        "You have no Evasion"                      -> -K * evasionRel
        //   Shattered Shield     "You have 50% less Energy Shield"          -> -K * esRel * 0.5
        //   Corrosive Concoction "You have no Armour, Evasion and ES"       -> full mitigation + full ES
        //   Worn Sandals         (Queen-of-the-Forest movement)            -> 0 weight when QotF is worn
        // Returning null falls through to the static profile weight.
        private double? DynamicAffliction(string name) => name switch
        {
            "Sharpened Arrowhead" => -K * ArmourRelevance(),
            "Iron Manacles" => -K * EvasionRelevance(),
            "Shattered Shield" => -K * EnergyShieldRelevance() * 0.5,
            "Corrosive Concoction" => -K * (ArmourRelevance() + EvasionRelevance()) - K * EnergyShieldRelevance(),
            "Worn Sandals" => HasQueenOfTheForest ? 0 : (double?)null,
            _ => null,
        };

        // Share of the player's hit-mitigation rating carried by Armour / Evasion. The denominator is
        // floored at RefMitigation so a build with negligible total rating yields negligible relevance
        // (rather than an arbitrary 50/50 split of near-zero values).
        private double MitigationDenominator() => System.Math.Max(Armour + Evasion, RefMitigation);
        private double ArmourRelevance() => Armour / MitigationDenominator();
        private double EvasionRelevance() => Evasion / MitigationDenominator();

        // Share of the player's HP pool carried by Energy Shield (vs Life). Guard against a zero pool.
        private double EnergyShieldRelevance()
        {
            double pool = EnergyShield + Life;
            return pool > 0 ? EnergyShield / pool : 0;
        }

        private double RewardWeight(SekhemaRoom room, ProfileContent profile)
        {
            if (string.IsNullOrEmpty(room.Reward) || !profile.RewardWeights.TryGetValue(room.Reward, out var w))
                return 0;

            // SOFT resource-aware suppression. When a reward is pointless given the live resource state
            // (Merchant with too little Sacred Water to spend; honour SHRINE while honour is already
            // high — fountains restore water not honour, so they are NOT touched here) it does NOT get
            // buried — it just loses its profile bonus and takes a small deterrent.
            // The path still goes through it when every alternative is meaningfully worse (a bad
            // affliction/room type, hundreds-to-thousands of weight, easily outweighs this); an
            // equally-good alternative simply wins. Only applied when the resource value is KNOWN.
            if (settings.SuppressMerchantLowWater && room.Reward == "Merchant" &&
                Water >= 0 && Water < settings.MerchantWaterThreshold)
            {
                debug.AppendLine($"{room.Reward}:{SuppressedRewardPenalty:F0} (low water {Water}<{settings.MerchantWaterThreshold})");
                return SuppressedRewardPenalty;
            }
            if (settings.SuppressHonourRestoreHighPct && IsHonourShrineReward(room.Reward) &&
                HonourPct >= 0 && HonourPct > settings.HonourRestoreThresholdPct)
            {
                debug.AppendLine($"{room.Reward}:{SuppressedRewardPenalty:F0} (honour {HonourPct:F0}%>{settings.HonourRestoreThresholdPct}%)");
                return SuppressedRewardPenalty;
            }

            debug.AppendLine($"{room.Reward}:{w}");
            return w;
        }

        // Honour-restoration reward rooms = honour SHRINES only ("Honour" / legacy "Honour <deity>").
        // Fountains are NOT here: in PoE2 Sanctum a Fountain restores Sacred Water, not Honour, so it
        // must not be suppressed by the honour rule.
        private static bool IsHonourShrineReward(string reward) =>
            reward.StartsWith("Honour", System.StringComparison.Ordinal);

        // Connectivity (number of forward exits) is consumed by PathFinder as a lexicographic
        // tie-breaker, not as a weight. Shown in the room debug text for visibility only.
        private void AppendConnectivityDebug(SekhemaRoom room)
            => debug.AppendLine($"Connectivity:{room.NextConnections.Count} (tiebreak)");
    }
}
