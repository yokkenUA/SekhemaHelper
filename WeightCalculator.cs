using System.Text;

namespace SekhemaHelper
{
    // Ported from legacy WeightCalculator. weight = base + roomType + affliction + reward + connectivity.
    // Dynamic affliction weights depend on the player's Evasion / Energy Shield / Queen-of-the-Forest.
    public sealed class WeightCalculator
    {
        private readonly SekhemaHelperSettings settings;
        private readonly StringBuilder debug = new();

        // Effective player stats for the current frame (set by the core each refresh).
        public int Evasion;
        public int EnergyShield;
        public bool HasQueenOfTheForest;

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

        private double? DynamicAffliction(string name) => name switch
        {
            "Iron Manacles" => IronManacles(),
            "Shattered Shield" => ShatteredShield(),
            "Worn Sandals" => HasQueenOfTheForest ? 0 : (double?)null,
            "Corrosive Concoction" => (IronManacles() ?? 0) + (ShatteredShield() ?? 0),
            _ => null,
        };

        private double? IronManacles()
        {
            if (Evasion > 20000) return -5000;
            if (Evasion > 6000) return -750;
            return null;
        }

        private double? ShatteredShield()
        {
            if (EnergyShield > 6000) return -5000;
            if (EnergyShield > 1000) return -750;
            return null;
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
