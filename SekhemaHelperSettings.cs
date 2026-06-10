using GameHelper.Plugin;
using System.Collections.Generic;
using System.Numerics;

namespace SekhemaHelper
{
    public sealed class SekhemaHelperSettings : IPSettings
    {
        public string CurrentProfile = "Default";
        public Dictionary<string, ProfileContent> Profiles = new()
        {
            ["Default"] = ProfileContent.CreateDefaultProfile(),
            ["No-Hit"] = ProfileContent.CreateNoHitProfile(),
        };

        public Vector4 BestPathColor = new(0.2f, 1f, 0.2f, 1f);
        public Vector4 TextColor = new(1f, 1f, 1f, 1f);
        public Vector4 BackgroundColor = new(0f, 0f, 0f, 0.75f);
        public float FrameThickness = 4f;

        public bool DrawBestPath = true;
        public bool DebugEnable = false;
    }
}
