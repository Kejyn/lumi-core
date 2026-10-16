using System;
using System.Collections.Generic;

namespace Lumivale.Core.Settings
{
    public enum GraphicsAntiAliasing
    {
        Off,
        FXAA,
        SMAA,
        MSAA2x,
        MSAA4x,
        MSAA8x
    }

    public enum GraphicsShadowQuality
    {
        Off,
        Low,
        Medium,
        High
    }

    public enum GraphicsQuality
    {
        Low,
        Medium,
        High
    }

    /// <summary>Rendering choices supported by an optional project or pipeline backend.</summary>
    public sealed class RenderingCapabilities
    {
        public static RenderingCapabilities None { get; } = new RenderingCapabilities();

        public RenderingCapabilities(bool supportsFrameRateLimit = false, bool supportsVSync = false,
            bool supportsRenderScale = false, bool supportsPostProcessing = false,
            bool supportsShadows = false, bool supportsTextureQuality = false,
            bool supportsAnisotropicFiltering = false, bool supportsLod = false,
            IEnumerable<GraphicsAntiAliasing> antiAliasingModes = null)
        {
            SupportsFrameRateLimit = supportsFrameRateLimit;
            SupportsVSync = supportsVSync;
            SupportsRenderScale = supportsRenderScale;
            SupportsPostProcessing = supportsPostProcessing;
            SupportsShadows = supportsShadows;
            SupportsTextureQuality = supportsTextureQuality;
            SupportsAnisotropicFiltering = supportsAnisotropicFiltering;
            SupportsLod = supportsLod;
            var choices = new List<GraphicsAntiAliasing>();
            if (antiAliasingModes != null)
            {
                foreach (GraphicsAntiAliasing mode in antiAliasingModes)
                {
                    if (!Enum.IsDefined(typeof(GraphicsAntiAliasing), mode))
                        throw new ArgumentException("An anti-aliasing capability is invalid.", nameof(antiAliasingModes));
                    if (!choices.Contains(mode)) choices.Add(mode);
                }
            }
            AntiAliasingModes = choices.AsReadOnly();
        }

        public bool SupportsFrameRateLimit { get; }
        public bool SupportsVSync { get; }
        public bool SupportsRenderScale { get; }
        public bool SupportsPostProcessing { get; }
        public bool SupportsShadows { get; }
        public bool SupportsTextureQuality { get; }
        public bool SupportsAnisotropicFiltering { get; }
        public bool SupportsLod { get; }
        public IReadOnlyList<GraphicsAntiAliasing> AntiAliasingModes { get; }
    }
}
