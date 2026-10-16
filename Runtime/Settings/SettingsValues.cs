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
        public int FrameRateLimit = 60;
        public bool VSync = true;
        public float RenderScale = 1f;
        public GraphicsAntiAliasing AntiAliasing = GraphicsAntiAliasing.Off;
        public bool PostProcessing = true;
        public GraphicsShadowQuality ShadowQuality = GraphicsShadowQuality.Medium;
        public float ShadowDistance = 35f;
        public GraphicsQuality TextureQuality = GraphicsQuality.High;
        public AnisotropicFiltering AnisotropicFiltering = AnisotropicFiltering.Enable;
        public GraphicsQuality LodQuality = GraphicsQuality.High;
        public float MasterVolume = 1f;
        public float MusicVolume = 1f;
        public float SfxVolume = 1f;
        public string LanguageCode = "en";

        public SettingsValues Clone()
        {
            return (SettingsValues)MemberwiseClone();
        }

        /// <summary>Compares the complete preference content without exposing service-owned state.</summary>
        public bool ContentEquals(SettingsValues other)
        {
            return other != null && Width == other.Width && Height == other.Height &&
                DisplayMode == other.DisplayMode && QualityName == other.QualityName &&
                FrameRateLimit == other.FrameRateLimit && VSync == other.VSync &&
                RenderScale.Equals(other.RenderScale) && AntiAliasing == other.AntiAliasing &&
                PostProcessing == other.PostProcessing && ShadowQuality == other.ShadowQuality &&
                ShadowDistance.Equals(other.ShadowDistance) && TextureQuality == other.TextureQuality &&
                AnisotropicFiltering == other.AnisotropicFiltering && LodQuality == other.LodQuality &&
                MasterVolume.Equals(other.MasterVolume) && MusicVolume.Equals(other.MusicVolume) &&
                SfxVolume.Equals(other.SfxVolume) && LanguageCode == other.LanguageCode;
        }

        internal bool SameAs(SettingsValues other) => ContentEquals(other);
    }
}
