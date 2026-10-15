using System;
using UnityEngine;

namespace Lumivale.Core.Settings
{
    /// <summary>A serializable preference snapshot. Services always copy supplied and returned values.</summary>
    [Serializable]
    public sealed class SettingsValues
    {
        public int Width;
        public int Height;
        public FullScreenMode DisplayMode = FullScreenMode.Windowed;
        public string QualityName;
        public float MasterVolume = 1f;
        public float MusicVolume = 1f;
        public float SfxVolume = 1f;
        public string LanguageCode = "en";

        public SettingsValues Clone()
        {
            return (SettingsValues)MemberwiseClone();
        }

        internal bool SameAs(SettingsValues other)
        {
            return other != null && Width == other.Width && Height == other.Height &&
                DisplayMode == other.DisplayMode && QualityName == other.QualityName &&
                MasterVolume.Equals(other.MasterVolume) && MusicVolume.Equals(other.MusicVolume) &&
                SfxVolume.Equals(other.SfxVolume) && LanguageCode == other.LanguageCode;
        }
    }
}
