using UnityModManagerNet;

namespace PEV.Core
{
    /// <summary>Mod-wide preferences; defaults preserve the complete reference effect.</summary>
    public sealed class NoiseFieldSettings : UnityModManager.ModSettings
    {
        public bool RealtimeSceneSampling = true;

        public override void Save(UnityModManager.ModEntry entry) => Save(this, entry);
    }
}
