using System;
using UnityEngine;

namespace Lumivale.Core.Audio
{
    /// <summary>Conversion for normalized gain applied to a Unity mixer attenuation parameter.</summary>
    public static class AudioVolume
    {
        public const float SilenceDecibels = -80f;

        public static float NormalizedToDecibels(float volume)
        {
            ValidateNormalized(volume, nameof(volume));
            return volume == 0f ? SilenceDecibels : Mathf.Max(SilenceDecibels, 20f * Mathf.Log10(volume));
        }

        internal static void ValidateNormalized(float value, string parameter)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f || value > 1f)
                throw new ArgumentOutOfRangeException(parameter, "Volume must be finite and between 0 and 1.");
        }
    }
}
