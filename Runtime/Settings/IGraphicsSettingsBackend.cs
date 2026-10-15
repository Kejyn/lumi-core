using System;
using System.Collections.Generic;
using UnityEngine;

namespace Lumivale.Core.Settings
{
    /// <summary>Separates capability validation from Unity display mutation and makes it testable.</summary>
    public interface IGraphicsSettingsBackend
    {
        GraphicsCapabilities GetCapabilities();
        void Apply(DisplayResolution resolution, FullScreenMode mode, string qualityName);
    }

    /// <summary>Native Unity display and quality adapter, independent of a render pipeline.</summary>
    public sealed class UnityGraphicsSettingsBackend : IGraphicsSettingsBackend
    {
        public GraphicsCapabilities GetCapabilities()
        {
            var resolutions = new List<DisplayResolution>();
            foreach (Resolution resolution in Screen.resolutions)
            {
                if (resolution.width > 0 && resolution.height > 0)
                {
                    resolutions.Add(new DisplayResolution(resolution.width, resolution.height));
                }
            }

            Resolution display = Screen.currentResolution;
            int width = Screen.width > 0 ? Screen.width : display.width;
            int height = Screen.height > 0 ? Screen.height : display.height;
            var current = new DisplayResolution(Math.Max(1, width), Math.Max(1, height));
            var modes = new List<FullScreenMode>();
            RuntimePlatform platform = Application.platform;
            bool desktop = platform == RuntimePlatform.WindowsPlayer || platform == RuntimePlatform.WindowsEditor ||
                platform == RuntimePlatform.OSXPlayer || platform == RuntimePlatform.OSXEditor ||
                platform == RuntimePlatform.LinuxPlayer || platform == RuntimePlatform.LinuxEditor;
            if (desktop)
            {
                modes.Add(FullScreenMode.Windowed);
                modes.Add(FullScreenMode.FullScreenWindow);
                if (platform == RuntimePlatform.WindowsPlayer || platform == RuntimePlatform.WindowsEditor)
                {
                    modes.Add(FullScreenMode.ExclusiveFullScreen);
                }
                else if (platform == RuntimePlatform.OSXPlayer || platform == RuntimePlatform.OSXEditor)
                {
                    modes.Add(FullScreenMode.MaximizedWindow);
                }
            }
            else if (platform == RuntimePlatform.WebGLPlayer)
            {
                modes.Add(FullScreenMode.Windowed);
                modes.Add(FullScreenMode.FullScreenWindow);
            }
            else
            {
                modes.Add(FullScreenMode.FullScreenWindow);
            }

            string[] names = QualitySettings.names;
            int qualityIndex = QualitySettings.GetQualityLevel();
            string currentQuality = qualityIndex >= 0 && qualityIndex < names.Length ? names[qualityIndex] : null;
            return new GraphicsCapabilities(resolutions, modes, names, current, Screen.fullScreenMode, currentQuality);
        }

        public void Apply(DisplayResolution resolution, FullScreenMode mode, string qualityName)
        {
            int qualityIndex = Array.IndexOf(QualitySettings.names, qualityName);
            if (qualityIndex < 0)
            {
                throw new InvalidOperationException($"Quality profile '{qualityName}' is no longer available.");
            }

            QualitySettings.SetQualityLevel(qualityIndex, true);
            Screen.SetResolution(resolution.Width, resolution.Height, mode);
        }
    }
}
