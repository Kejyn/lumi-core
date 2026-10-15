using System;
using UnityEngine.Audio;

namespace Lumivale.Core.Audio
{
    /// <summary>Project-owned mixer routing and exposed volume parameter names.</summary>
    public sealed class AudioConfiguration
    {
        public AudioConfiguration(AudioMixer mixer, AudioMixerGroup masterGroup,
            AudioMixerGroup musicGroup, AudioMixerGroup sfxGroup, int maxSfxSources = 12,
            string masterVolumeParameter = "MasterVolume",
            string musicVolumeParameter = "MusicVolume", string sfxVolumeParameter = "SfxVolume")
        {
            Mixer = mixer;
            MasterGroup = masterGroup;
            MusicGroup = musicGroup;
            SfxGroup = sfxGroup;
            MaxSfxSources = maxSfxSources;
            MasterVolumeParameter = masterVolumeParameter;
            MusicVolumeParameter = musicVolumeParameter;
            SfxVolumeParameter = sfxVolumeParameter;
            Validate();
        }

        public AudioMixer Mixer { get; }
        public AudioMixerGroup MasterGroup { get; }
        public AudioMixerGroup MusicGroup { get; }
        public AudioMixerGroup SfxGroup { get; }
        public int MaxSfxSources { get; }
        public string MasterVolumeParameter { get; }
        public string MusicVolumeParameter { get; }
        public string SfxVolumeParameter { get; }

        internal void Validate()
        {
            if (Mixer == null) throw new ArgumentNullException(nameof(Mixer));
            if (MasterGroup == null) throw new ArgumentNullException(nameof(MasterGroup));
            if (MusicGroup == null) throw new ArgumentNullException(nameof(MusicGroup));
            if (SfxGroup == null) throw new ArgumentNullException(nameof(SfxGroup));
            if (MasterGroup == MusicGroup || MasterGroup == SfxGroup || MusicGroup == SfxGroup)
                throw new ArgumentException("Master, Music and SFX must be distinct mixer groups.");
            if (MasterGroup.audioMixer != Mixer || MusicGroup.audioMixer != Mixer ||
                SfxGroup.audioMixer != Mixer)
                throw new ArgumentException("All audio groups must belong to the configured mixer.");
            if (MaxSfxSources < 1 || MaxSfxSources > 128)
                throw new ArgumentOutOfRangeException(nameof(MaxSfxSources), "Use between 1 and 128 SFX sources.");
            if (string.IsNullOrWhiteSpace(MasterVolumeParameter) ||
                string.IsNullOrWhiteSpace(MusicVolumeParameter) || string.IsNullOrWhiteSpace(SfxVolumeParameter))
                throw new ArgumentException("All three exposed volume parameter names are required.");
            if (MasterVolumeParameter == MusicVolumeParameter || MasterVolumeParameter == SfxVolumeParameter ||
                MusicVolumeParameter == SfxVolumeParameter)
                throw new ArgumentException("Volume parameter names must be distinct.");
        }
    }
}
