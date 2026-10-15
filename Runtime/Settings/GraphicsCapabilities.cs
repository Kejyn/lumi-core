using System;
using System.Collections.Generic;
using UnityEngine;

namespace Lumivale.Core.Settings
{
    /// <summary>A display size; this V0.1 intentionally lets Unity select the refresh rate.</summary>
    public readonly struct DisplayResolution : IEquatable<DisplayResolution>
    {
        public DisplayResolution(int width, int height)
        {
            if (width <= 0 || height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(width), "Display dimensions must be positive.");
            }

            Width = width;
            Height = height;
        }

        public int Width { get; }
        public int Height { get; }
        public bool Equals(DisplayResolution other) => Width == other.Width && Height == other.Height;
        public override bool Equals(object obj) => obj is DisplayResolution other && Equals(other);
        public override int GetHashCode() => (Width * 397) ^ Height;
        public override string ToString() => $"{Width} x {Height}";
    }

    /// <summary>Available choices and the currently usable fallback, supplied by the display backend.</summary>
    public sealed class GraphicsCapabilities
    {
        public GraphicsCapabilities(IEnumerable<DisplayResolution> resolutions,
            IEnumerable<FullScreenMode> modes, IEnumerable<string> qualityNames,
            DisplayResolution currentResolution, FullScreenMode currentMode, string currentQualityName)
        {
            var resolutionList = new List<DisplayResolution>();
            if (resolutions != null)
            {
                foreach (DisplayResolution resolution in resolutions)
                {
                    if (resolution.Width > 0 && resolution.Height > 0 && !resolutionList.Contains(resolution))
                    {
                        resolutionList.Add(resolution);
                    }
                }
            }

            if (currentResolution.Width > 0 && currentResolution.Height > 0 &&
                !resolutionList.Contains(currentResolution))
            {
                resolutionList.Add(currentResolution);
            }

            var modeList = new List<FullScreenMode>();
            if (modes != null)
            {
                foreach (FullScreenMode mode in modes)
                {
                    if (Enum.IsDefined(typeof(FullScreenMode), mode) && !modeList.Contains(mode))
                    {
                        modeList.Add(mode);
                    }
                }
            }

            var qualityList = new List<string>();
            if (qualityNames != null)
            {
                foreach (string quality in qualityNames)
                {
                    if (!string.IsNullOrWhiteSpace(quality) && !qualityList.Contains(quality))
                    {
                        qualityList.Add(quality);
                    }
                }
            }

            if (resolutionList.Count == 0 || modeList.Count == 0 || qualityList.Count == 0)
            {
                throw new ArgumentException("Graphics capabilities require a resolution, display mode and quality profile.");
            }

            Resolutions = resolutionList.AsReadOnly();
            Modes = modeList.AsReadOnly();
            QualityNames = qualityList.AsReadOnly();
            CurrentResolution = currentResolution;
            CurrentMode = currentMode;
            CurrentQualityName = currentQualityName;
        }

        public IReadOnlyList<DisplayResolution> Resolutions { get; }
        public IReadOnlyList<FullScreenMode> Modes { get; }
        public IReadOnlyList<string> QualityNames { get; }
        public DisplayResolution CurrentResolution { get; }
        public FullScreenMode CurrentMode { get; }
        public string CurrentQualityName { get; }
    }
}
