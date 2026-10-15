using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lumivale.Core.Bootstrap;
using UnityEngine;

namespace Lumivale.Core.Audio
{
    /// <summary>
    /// Owns persistent music and pooled SFX sources. Call its lifecycle and playback API on
    /// Unity's main thread. Mixer parameters are owned exclusively for this service lifetime.
    /// </summary>
    public sealed class AudioService : ILumiService
    {
        private readonly AudioConfiguration configuration;
        private readonly List<SfxVoice> sfxVoices = new List<SfxVoice>();
        private GameObject root;
        private LumiAudioRunner runner;
        private AudioSource music;
        private Task initializationTask;
        private Task shutdownTask;
        private bool initialized;
        private bool stopped;
        private bool masterWritten;
        private bool musicWritten;
        private bool sfxWritten;
        private bool fading;
        private bool stopAfterFade;
        private float fadeStart;
        private float fadeTarget;
        private float fadeDuration;
        private float fadeElapsed;

        public AudioService(AudioConfiguration configuration)
        {
            this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        }

        public string Id => "audio";
        public IReadOnlyCollection<string> Dependencies => Array.Empty<string>();
        public bool IsInitialized => initialized && root != null && music != null;
        public AudioClip CurrentMusic => IsInitialized ? music.clip : null;
        public bool IsMusicPlaying => IsInitialized && music.isPlaying;
        public float MasterVolume { get; private set; } = 1f;
        public float MusicVolume { get; private set; } = 1f;
        public float SfxVolume { get; private set; } = 1f;
        public int AllocatedSfxSourceCount => sfxVoices.Count;
        public int ActiveSfxCount
        {
            get
            {
                if (!IsInitialized) return 0;
                ReclaimFinishedSfx();
                int active = 0;
                foreach (SfxVoice voice in sfxVoices) if (voice.Busy) active++;
                return active;
            }
        }

        public Task InitializeAsync(ServiceContext context, CancellationToken cancellationToken)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (cancellationToken.IsCancellationRequested) return Task.FromCanceled(cancellationToken);
            if (stopped) throw new InvalidOperationException("Create a new AudioService after shutdown.");
            return initializationTask ?? (initializationTask = InitializeCoreAsync(cancellationToken));
        }

        private async Task InitializeCoreAsync(CancellationToken cancellationToken)
        {
            if (!Application.isPlaying)
                throw new InvalidOperationException("Initialize AudioService while Unity is in Play mode.");
            configuration.Validate();
            root = new GameObject("Lumi Audio");
            UnityEngine.Object.DontDestroyOnLoad(root);
            music = root.AddComponent<AudioSource>();
            ConfigureSource(music);
            music.outputAudioMixerGroup = configuration.MusicGroup;
            runner = root.AddComponent<LumiAudioRunner>();
            runner.Attach(this);
            try
            {
                // Mixer.SetFloat is unsupported from Awake/OnEnable. A Bootstrap starts in Awake.
                await Awaitable.NextFrameAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (stopped || root == null)
                    throw new OperationCanceledException("Audio initialization was stopped.");
                ApplyVolumes(1f, 1f, 1f);
                initialized = true;
            }
            catch
            {
                CleanupOwnedResources();
                throw;
            }
        }

        public Task ShutdownAsync(CancellationToken cancellationToken)
        {
            if (shutdownTask != null) return shutdownTask;
            if (cancellationToken.IsCancellationRequested) return Task.FromCanceled(cancellationToken);
            stopped = true;
            return shutdownTask = ShutdownCoreAsync();
        }

        private async Task ShutdownCoreAsync()
        {
            Task startup = initializationTask;
            CleanupOwnedResources();
            if (startup != null && !startup.IsCompleted)
            {
                try { await startup; }
                catch (Exception) { /* Startup owns its error; teardown still completes. */ }
            }
            if (Application.isPlaying) await Awaitable.NextFrameAsync();
        }

        /// <summary>Sets normalized mixer gains; zero additionally mutes owned sources exactly.</summary>
        public void SetVolumes(float master, float music, float sfx)
        {
            EnsureInitialized();
            ApplyVolumes(master, music, sfx);
        }

        private void ApplyVolumes(float master, float musicVolume, float sfx)
        {
            float masterDb = AudioVolume.NormalizedToDecibels(master);
            float musicDb = AudioVolume.NormalizedToDecibels(musicVolume);
            float sfxDb = AudioVolume.NormalizedToDecibels(sfx);
            float oldMaster = GetMixerValue(configuration.MasterVolumeParameter);
            float oldMusic = GetMixerValue(configuration.MusicVolumeParameter);
            float oldSfx = GetMixerValue(configuration.SfxVolumeParameter);
            bool previousMasterWritten = masterWritten;
            bool previousMusicWritten = musicWritten;
            bool previousSfxWritten = sfxWritten;
            bool changedMaster = false, changedMusic = false, changedSfx = false;
            try
            {
                SetMixerValue(configuration.MasterVolumeParameter, masterDb);
                masterWritten = changedMaster = true;
                SetMixerValue(configuration.MusicVolumeParameter, musicDb);
                musicWritten = changedMusic = true;
                SetMixerValue(configuration.SfxVolumeParameter, sfxDb);
                sfxWritten = changedSfx = true;
            }
            catch (Exception failure)
            {
                var errors = new List<Exception> { failure };
                RestoreMixerValue(configuration.MasterVolumeParameter, oldMaster,
                    changedMaster, previousMasterWritten, errors);
                RestoreMixerValue(configuration.MusicVolumeParameter, oldMusic,
                    changedMusic, previousMusicWritten, errors);
                RestoreMixerValue(configuration.SfxVolumeParameter, oldSfx,
                    changedSfx, previousSfxWritten, errors);
                masterWritten = previousMasterWritten;
                musicWritten = previousMusicWritten;
                sfxWritten = previousSfxWritten;
                if (errors.Count > 1) throw new AggregateException("Audio volume update and restoration failed.", errors);
                throw;
            }
            MasterVolume = master;
            MusicVolume = musicVolume;
            SfxVolume = sfx;
            RefreshMutes();
        }

        /// <summary>Replaces the current track immediately; only the latest request controls the fade.</summary>
        public void PlayMusic(AudioClip clip, bool loop = true, float fadeInSeconds = 0f, float volume = 1f)
        {
            EnsureInitialized();
            ValidateClip(clip);
            ValidateDuration(fadeInSeconds, nameof(fadeInSeconds));
            AudioVolume.ValidateNormalized(volume, nameof(volume));
            music.Stop();
            music.clip = clip;
            music.loop = loop;
            music.volume = fadeInSeconds > 0f ? 0f : volume;
            fading = false;
            music.Play();
            if (fadeInSeconds > 0f) BeginFade(volume, fadeInSeconds, false);
        }

        /// <summary>Stops immediately or fades out in unscaled time before releasing the clip.</summary>
        public void StopMusic(float fadeOutSeconds = 0f)
        {
            EnsureInitialized();
            ValidateDuration(fadeOutSeconds, nameof(fadeOutSeconds));
            if (music.clip == null || fadeOutSeconds == 0f) StopMusicImmediately();
            else BeginFade(0f, fadeOutSeconds, true);
        }

        public bool PlaySfx2D(AudioClip clip, float volume = 1f) => PlaySfx(clip, Vector3.zero, false, volume);

        public bool PlaySfx3D(AudioClip clip, Vector3 position, float volume = 1f)
        {
            if (!IsFinite(position.x) || !IsFinite(position.y) || !IsFinite(position.z))
                throw new ArgumentOutOfRangeException(nameof(position), "SFX position must be finite.");
            return PlaySfx(clip, position, true, volume);
        }

        private bool PlaySfx(AudioClip clip, Vector3 position, bool spatial, float volume)
        {
            EnsureInitialized();
            ValidateClip(clip);
            AudioVolume.ValidateNormalized(volume, nameof(volume));
            ReclaimFinishedSfx();
            SfxVoice available = sfxVoices.Find(voice => !voice.Busy);
            if (available == null)
            {
                if (sfxVoices.Count >= configuration.MaxSfxSources) return false;
                var sourceObject = new GameObject($"SFX {sfxVoices.Count}");
                sourceObject.transform.SetParent(root.transform, false);
                AudioSource source = sourceObject.AddComponent<AudioSource>();
                ConfigureSource(source);
                source.outputAudioMixerGroup = configuration.SfxGroup;
                available = new SfxVoice(source);
                sfxVoices.Add(available);
            }
            AudioSource selected = available.Source;
            selected.Stop();
            selected.clip = clip;
            selected.volume = volume;
            selected.spatialBlend = spatial ? 1f : 0f;
            selected.transform.position = position;
            selected.mute = MasterVolume == 0f || SfxVolume == 0f;
            available.Busy = true;
            available.StartFrame = Time.frameCount;
            selected.Play();
            return true;
        }

        internal void Tick(float unscaledDeltaTime)
        {
            if (!IsInitialized) return;
            if (fading)
            {
                fadeElapsed += unscaledDeltaTime;
                float progress = Mathf.Clamp01(fadeElapsed / fadeDuration);
                music.volume = Mathf.Lerp(fadeStart, fadeTarget, progress);
                if (progress >= 1f)
                {
                    fading = false;
                    if (stopAfterFade) StopMusicImmediately();
                }
            }
            ReclaimFinishedSfx();
            if (!fading && music.clip != null && !music.loop && !music.isPlaying && !AudioListener.pause)
                music.clip = null;
        }

        private void BeginFade(float target, float duration, bool stop)
        {
            fadeStart = music.volume;
            fadeTarget = target;
            fadeDuration = duration;
            fadeElapsed = 0f;
            stopAfterFade = stop;
            fading = true;
        }

        private void StopMusicImmediately()
        {
            fading = false;
            stopAfterFade = false;
            music.Stop();
            music.clip = null;
            music.volume = 1f;
        }

        private void ReclaimFinishedSfx()
        {
            foreach (SfxVoice voice in sfxVoices)
            {
                if (voice.Busy && Time.frameCount > voice.StartFrame &&
                    !voice.Source.isPlaying && (!AudioListener.pause || voice.Source.ignoreListenerPause))
                {
                    voice.Busy = false;
                    voice.Source.clip = null;
                }
            }
        }

        private void RefreshMutes()
        {
            if (music != null) music.mute = MasterVolume == 0f || MusicVolume == 0f;
            foreach (SfxVoice voice in sfxVoices)
                if (voice.Source != null) voice.Source.mute = MasterVolume == 0f || SfxVolume == 0f;
        }

        private float GetMixerValue(string parameter)
        {
            if (!configuration.Mixer.GetFloat(parameter, out float value))
                throw new InvalidOperationException($"AudioMixer has no exposed parameter '{parameter}'.");
            return value;
        }

        private void SetMixerValue(string parameter, float value)
        {
            if (!configuration.Mixer.SetFloat(parameter, value))
                throw new InvalidOperationException($"AudioMixer rejected exposed parameter '{parameter}'.");
        }

        private void RestoreMixerValue(string parameter, float value, bool changed, bool previouslyWritten,
            List<Exception> errors)
        {
            if (!changed) return;
            bool restored = previouslyWritten ? configuration.Mixer.SetFloat(parameter, value) :
                configuration.Mixer.ClearFloat(parameter);
            if (!restored) errors.Add(new InvalidOperationException($"Could not restore mixer parameter '{parameter}'."));
        }

        internal void OnRunnerDestroyed()
        {
            stopped = true;
            CleanupOwnedResources();
        }

        private void CleanupOwnedResources()
        {
            initialized = false;
            fading = false;
            if (runner != null) runner.Detach();
            if (music != null)
            {
                music.Stop();
                music.clip = null;
                music.mute = true;
            }
            foreach (SfxVoice voice in sfxVoices)
            {
                if (voice.Source == null) continue;
                voice.Source.Stop();
                voice.Source.clip = null;
                voice.Source.mute = true;
            }
            sfxVoices.Clear();
            if (configuration.Mixer != null)
            {
                if (masterWritten) configuration.Mixer.ClearFloat(configuration.MasterVolumeParameter);
                if (musicWritten) configuration.Mixer.ClearFloat(configuration.MusicVolumeParameter);
                if (sfxWritten) configuration.Mixer.ClearFloat(configuration.SfxVolumeParameter);
            }
            masterWritten = musicWritten = sfxWritten = false;
            GameObject ownedRoot = root;
            root = null;
            music = null;
            runner = null;
            if (ownedRoot != null)
            {
                ownedRoot.SetActive(false);
                if (Application.isPlaying) UnityEngine.Object.Destroy(ownedRoot);
                else UnityEngine.Object.DestroyImmediate(ownedRoot);
            }
        }

        private void EnsureInitialized()
        {
            if (!IsInitialized) throw new InvalidOperationException("Audio service is not initialized.");
        }

        private static void ConfigureSource(AudioSource source)
        {
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = 0f;
            source.dopplerLevel = 0f;
            source.minDistance = 1f;
            source.maxDistance = 30f;
            source.volume = 1f;
        }

        private static void ValidateClip(AudioClip clip)
        {
            if (clip == null) throw new ArgumentNullException(nameof(clip));
            if (clip.samples <= 0 || clip.frequency <= 0)
                throw new ArgumentException("Audio clip has no usable sample data.", nameof(clip));
            if (clip.loadState != AudioDataLoadState.Loaded)
                throw new ArgumentException("Load the audio clip data before requesting playback.", nameof(clip));
        }

        private static void ValidateDuration(float duration, string parameter)
        {
            if (!IsFinite(duration) || duration < 0f)
                throw new ArgumentOutOfRangeException(parameter, "Fade duration must be finite and non-negative.");
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private sealed class SfxVoice
        {
            public SfxVoice(AudioSource source) => Source = source;
            public AudioSource Source { get; }
            public bool Busy { get; set; }
            public int StartFrame { get; set; }
        }
    }
}
