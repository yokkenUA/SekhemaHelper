using System.Text;

namespace SekhemaHelper
{
    // Ported from legacy WeightCalculator. weight = base + roomType + affliction + reward + connectivity.
    // Dynamic affliction weights depend on the player's defensive profile (Armour / Evasion / Energy
    // Shield / Life / Queen-of-the-Forest). A stat-removing curse is weighted by how much that stat
    // actually contributes to the player's defence, so an evasion build ignores "no Armour" while an
    // armour tank ignores "no Evasion". See docs/re-findings.md and DynamicAffliction below.
    public sealed class WeightCalculator
    {
        private readonly SekhemaHelperSettings settings;
        private readonly StringBuilder debug = new();

        // Effective player stats for the current frame (set by the core each refresh).
        public int Evasion;
        public int EnergyShield;
        public int Armour;
        public int Life;
        public bool HasQueenOfTheForest;

        // Relevance model tuning. K = max penalty when a curse removes the player's entire main
        // defence layer (matches the legacy "strongly avoid" magnitude). REF_MIT floors the
        // Armour+Evasion denominator so that a build with little total mitigation rating scales its
        // penalties down (nothing meaningful to lose) instead of splitting 50/50 on noise.
        private const double K = 5000;
        private const double RefMitigation = 4000;

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
            weight += ConnectivityBonus(room);
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

        private double AfflictionWeight(SekhemaRoom room, ProfileContent profile)
        {
            if (string.IsNullOrEmpty(room.Affliction))
                return 0;

            var dyn = DynamicAffliction(room.Affliction);
            if (dyn.HasValue)
            {
                debug.AppendLine($"{room.Affliction}:{dyn.Value}");
                return dyn.Value;
            }
            if (profile.AfflictionWeights.TryGetValue(room.Affliction, out var w))
            {
                debug.AppendLine($"{room.Affliction}:{w}");
                return w;
            }
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
            if (!string.IsNullOrEmpty(room.Reward) && profile.RewardWeights.TryGetValue(room.Reward, out var w))
            {
                debug.AppendLine($"{room.Reward}:{w}");
                return w;
            }
            return 0;
        }

        private double ConnectivityBonus(SekhemaRoom room)
        {
            int bonus = room.NextConnections.Count > 1 ? 100 : 0;
            debug.AppendLine($"Connectivity:{bonus}");
            return bonus;
        }
    }
}
