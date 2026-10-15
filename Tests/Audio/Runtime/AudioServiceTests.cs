#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Lumivale.Core.Audio;
using Lumivale.Core.Bootstrap;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Lumivale.Core.Audio.Tests
{
    /// <summary>Uses a native temporary mixer and clips in memory; no Starter assets.</summary>
    public sealed class AudioServiceTests
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private string temporaryFolder;
        private AudioConfiguration configuration;
        private AudioService audio;
        private BootstrapCoordinator coordinator;
        private LumiAudioRunner ownedRunner;
        private AudioClip firstClip;
        private AudioClip secondClip;
        private GameObject listener;
        private float previousTimeScale;
        private bool previousListenerPause;
        private readonly List<BootstrapCoordinator> extraCoordinators = new List<BootstrapCoordinator>();

        [UnitySetUp]
        public IEnumerator Setup()
        {
            previousTimeScale = Time.timeScale;
            previousListenerPause = AudioListener.pause;
            Time.timeScale = 1f;
            AudioListener.pause = false;
            string folderName = "LumiCoreAudioTests_" + Guid.NewGuid().ToString("N");
            temporaryFolder = "Assets/" + folderName;
            AssetDatabase.CreateFolder("Assets", folderName);
            configuration = CreateMixerConfiguration(temporaryFolder + "/TestMixer.mixer", 2);
            firstClip = AudioClip.Create("CoreAudioFirst", 48000 * 3, 1, 48000, false);
            secondClip = AudioClip.Create("CoreAudioSecond", 48000 * 3, 1, 48000, false);
            var samples = new float[firstClip.samples];
            for (int index = 0; index < samples.Length; index++)
                samples[index] = 0.02f * Mathf.Sin(index * 2f * Mathf.PI * 440f / 48000f);
            Assert.That(firstClip.SetData(samples, 0), Is.True);
            Assert.That(secondClip.SetData(samples, 0), Is.True);
            bool activeListener = false;
            foreach (AudioListener candidate in UnityEngine.Object.FindObjectsByType<AudioListener>(FindObjectsSortMode.None))
                if (candidate.enabled && candidate.gameObject.activeInHierarchy) activeListener = true;
            if (!activeListener) listener = new GameObject("CoreAudioListener", typeof(AudioListener));
            var existingRunners = new HashSet<LumiAudioRunner>(
                UnityEngine.Object.FindObjectsByType<LumiAudioRunner>(FindObjectsInactive.Include, FindObjectsSortMode.None));
            audio = new AudioService(configuration);
            coordinator = new BootstrapCoordinator(new ILumiService[] { audio });
            yield return Wait(coordinator.InitializeAsync());
            foreach (LumiAudioRunner candidate in UnityEngine.Object.FindObjectsByType<LumiAudioRunner>(
                FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (!existingRunners.Contains(candidate)) ownedRunner = candidate;
            Assert.That(ownedRunner, Is.Not.Null);
        }

        [UnityTearDown]
        public IEnumerator Teardown()
        {
            for (int index = extraCoordinators.Count - 1; index >= 0; index--)
                yield return Wait(extraCoordinators[index].ShutdownAsync());
            extraCoordinators.Clear();
            if (coordinator != null) yield return Wait(coordinator.ShutdownAsync());
            if (firstClip != null) UnityEngine.Object.Destroy(firstClip);
            if (secondClip != null) UnityEngine.Object.Destroy(secondClip);
            if (listener != null) UnityEngine.Object.Destroy(listener);
            Time.timeScale = previousTimeScale;
            AudioListener.pause = previousListenerPause;
            if (!string.IsNullOrEmpty(temporaryFolder))
            {
                Assert.That(temporaryFolder, Does.StartWith("Assets/LumiCoreAudioTests_"));
                Assert.That(AssetDatabase.DeleteAsset(temporaryFolder), Is.True);
            }
            configuration = null;
            audio = null;
            coordinator = null;
            ownedRunner = null;
            listener = null;
            temporaryFolder = null;
            yield return null;
        }

        [Test]
        public void InitializationOwnsOnePersistentRootAndThreeMixerParameters()
        {
            Assert.That(audio.IsInitialized, Is.True);
            Assert.That(audio.Dependencies, Is.Empty);
            Assert.That(ownedRunner.gameObject.scene.name, Is.EqualTo("DontDestroyOnLoad"));
            Assert.That(Music().outputAudioMixerGroup, Is.EqualTo(configuration.MusicGroup));
            Assert.That(Music().playOnAwake, Is.False);
            AssertMixer("MasterVolume", 0f);
            AssertMixer("MusicVolume", 0f);
            AssertMixer("SfxVolume", 0f);
            Assert.That(audio.AllocatedSfxSourceCount, Is.Zero);
        }

        [Test]
        public void MixerVolumesPreserveIndividualSourceGainsAndZeroMutesExactly()
        {
            audio.PlayMusic(firstClip, volume: 0.65f);
            Assert.That(audio.PlaySfx2D(secondClip, 0.4f), Is.True);
            audio.SetVolumes(0.5f, 0.25f, 0.1f);
            AssertMixer("MasterVolume", -6.0206f);
            AssertMixer("MusicVolume", -12.0412f);
            AssertMixer("SfxVolume", -20f);
            Assert.That(Music().volume, Is.EqualTo(0.65f));
            Assert.That(Sfx()[0].volume, Is.EqualTo(0.4f));
            audio.SetVolumes(1f, 0f, 1f);
            Assert.That(Music().mute, Is.True);
            Assert.That(Sfx()[0].mute, Is.False);
            AssertMixer("MusicVolume", AudioVolume.SilenceDecibels);
            audio.SetVolumes(0f, 1f, 1f);
            Assert.That(Music().mute, Is.True);
            Assert.That(Sfx()[0].mute, Is.True);
            audio.SetVolumes(1f, 1f, 1f);
            Assert.That(Music().mute || Sfx()[0].mute, Is.False);
            Assert.That(Music().volume, Is.EqualTo(0.65f));
            Assert.That(Sfx()[0].volume, Is.EqualTo(0.4f));
        }

        [Test]
        public void InvalidPlaybackAndVolumeValuesLeaveCurrentPlaybackIntact()
        {
            audio.PlayMusic(firstClip, volume: 0.7f);
            audio.SetVolumes(0.8f, 0.6f, 0.4f);
            Assert.Throws<ArgumentNullException>(() => audio.PlayMusic(null));
            Assert.Throws<ArgumentOutOfRangeException>(() => audio.PlayMusic(secondClip, fadeInSeconds: float.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => audio.PlayMusic(secondClip, volume: -1f));
            Assert.Throws<ArgumentOutOfRangeException>(() => audio.StopMusic(float.PositiveInfinity));
            Assert.Throws<ArgumentOutOfRangeException>(() => audio.SetVolumes(1f, 1f, float.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => audio.PlaySfx2D(firstClip, 1.1f));
            Assert.Throws<ArgumentOutOfRangeException>(() => audio.PlaySfx3D(firstClip, new Vector3(float.NaN, 0f, 0f)));
            Assert.That(audio.CurrentMusic, Is.EqualTo(firstClip));
            Assert.That(Music().volume, Is.EqualTo(0.7f));
            Assert.That(audio.MasterVolume, Is.EqualTo(0.8f));
            AssertMixer("MasterVolume", AudioVolume.NormalizedToDecibels(0.8f));
            Assert.That(audio.AllocatedSfxSourceCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator UnloadedClipIsRejectedWithoutReplacingTheCurrentTrack()
        {
            string assetPath = temporaryFolder + "/PendingClip.wav";
            string absolutePath = Path.GetFullPath(Path.Combine(Application.dataPath, "../" + assetPath));
            const int sampleCount = 4800;
            using (var writer = new BinaryWriter(File.Create(absolutePath)))
            {
                writer.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
                writer.Write(36 + sampleCount * 2);
                writer.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E' });
                writer.Write(new[] { (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)1);
                writer.Write(48000);
                writer.Write(96000);
                writer.Write((short)2);
                writer.Write((short)16);
                writer.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
                writer.Write(sampleCount * 2);
                for (int index = 0; index < sampleCount; index++) writer.Write((short)0);
            }
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
            AudioClip unloaded = AssetDatabase.LoadAssetAtPath<AudioClip>(assetPath);
            Assert.That(unloaded, Is.Not.Null);
            Assert.That(unloaded.LoadAudioData(), Is.True);
            float deadline = Time.realtimeSinceStartup + 3f;
            while (unloaded.loadState == AudioDataLoadState.Loading)
            {
                Assert.That(Time.realtimeSinceStartup, Is.LessThan(deadline));
                yield return null;
            }
            Assert.That(unloaded.UnloadAudioData(), Is.True);
            Assert.That(unloaded.loadState, Is.EqualTo(AudioDataLoadState.Unloaded));
            audio.PlayMusic(firstClip);
            Assert.Throws<ArgumentException>(() => audio.PlayMusic(unloaded));
            Assert.Throws<ArgumentException>(() => audio.PlaySfx2D(unloaded));
            Assert.That(audio.CurrentMusic, Is.EqualTo(firstClip));
            Assert.That(audio.AllocatedSfxSourceCount, Is.Zero);
        }

        [Test]
        public void MusicCanPlayReplaceAndStopWithOneSource()
        {
            audio.PlayMusic(firstClip, loop: true);
            Assert.That(audio.CurrentMusic, Is.EqualTo(firstClip));
            Assert.That(audio.IsMusicPlaying, Is.True);
            Assert.That(Music().loop, Is.True);
            AudioSource source = Music();
            audio.PlayMusic(secondClip, loop: false, volume: 0.3f);
            Assert.That(Music(), Is.SameAs(source));
            Assert.That(audio.CurrentMusic, Is.EqualTo(secondClip));
            Assert.That(source.loop, Is.False);
            Assert.That(source.volume, Is.EqualTo(0.3f));
            audio.StopMusic();
            Assert.That(audio.IsMusicPlaying, Is.False);
            Assert.That(audio.CurrentMusic, Is.Null);
            Assert.That(source.clip, Is.Null);
        }

        [UnityTest]
        public IEnumerator MusicFadesInAndOutWhileTimeScaleIsZero()
        {
            Time.timeScale = 0f;
            audio.PlayMusic(firstClip, fadeInSeconds: 0.15f, volume: 0.6f);
            Assert.That(Music().volume, Is.Zero);
            yield return new WaitForSecondsRealtime(0.06f);
            Assert.That(Music().volume, Is.GreaterThan(0f).And.LessThan(0.6f));
            yield return new WaitForSecondsRealtime(0.2f);
            Assert.That(Music().volume, Is.EqualTo(0.6f).Within(0.001f));
            Assert.That(audio.IsMusicPlaying, Is.True);
            audio.StopMusic(0.15f);
            yield return new WaitForSecondsRealtime(0.06f);
            Assert.That(Music().volume, Is.GreaterThan(0f).And.LessThan(0.6f));
            yield return new WaitForSecondsRealtime(0.2f);
            Assert.That(audio.IsMusicPlaying, Is.False);
            Assert.That(audio.CurrentMusic, Is.Null);
            Assert.That(Time.timeScale, Is.Zero);
        }

        [UnityTest]
        public IEnumerator LatestMusicRequestSupersedesAnEarlierFadeOut()
        {
            audio.PlayMusic(firstClip);
            audio.StopMusic(0.3f);
            yield return null;
            audio.PlayMusic(secondClip, fadeInSeconds: 0.05f, volume: 0.4f);
            yield return new WaitForSecondsRealtime(0.4f);
            Assert.That(audio.CurrentMusic, Is.EqualTo(secondClip));
            Assert.That(audio.IsMusicPlaying, Is.True);
            Assert.That(Music().volume, Is.EqualTo(0.4f).Within(0.001f));
        }

        [Test]
        public void TwoAndThreeDimensionalSfxUseThePoolAndCorrectMixerGroup()
        {
            var position = new Vector3(3f, 2f, -5f);
            Assert.That(audio.PlaySfx2D(firstClip, 0.2f), Is.True);
            Assert.That(audio.PlaySfx3D(secondClip, position, 0.7f), Is.True);
            AudioSource[] sources = Sfx();
            Assert.That(sources, Has.Length.EqualTo(2));
            Assert.That(sources[0].spatialBlend, Is.Zero);
            Assert.That(sources[0].volume, Is.EqualTo(0.2f));
            Assert.That(sources[1].spatialBlend, Is.EqualTo(1f));
            Assert.That(sources[1].volume, Is.EqualTo(0.7f));
            Assert.That(sources[1].transform.position, Is.EqualTo(position));
            foreach (AudioSource source in sources)
            {
                Assert.That(source.outputAudioMixerGroup, Is.EqualTo(configuration.SfxGroup));
                Assert.That(source.isPlaying, Is.True);
            }
            Assert.That(audio.ActiveSfxCount, Is.EqualTo(2));
            Assert.That(audio.PlaySfx2D(firstClip), Is.False);
            Assert.That(audio.AllocatedSfxSourceCount, Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator FinishedSfxSourcesAreReusedAndResetTheirSpatialState()
        {
            var shortClip = AudioClip.Create("CoreShortSfx", 4800, 1, 48000, false);
            try
            {
                Assert.That(audio.PlaySfx3D(shortClip, Vector3.one, 0.3f), Is.True);
                AudioSource source = Sfx()[0];
                yield return new WaitForSecondsRealtime(0.25f);
                Assert.That(audio.ActiveSfxCount, Is.Zero);
                Assert.That(source.clip, Is.Null);
                Assert.That(audio.PlaySfx2D(firstClip, 0.8f), Is.True);
                Assert.That(Sfx()[0], Is.SameAs(source));
                Assert.That(source.spatialBlend, Is.Zero);
                Assert.That(source.transform.position, Is.EqualTo(Vector3.zero));
                Assert.That(source.volume, Is.EqualTo(0.8f));
                Assert.That(audio.AllocatedSfxSourceCount, Is.EqualTo(1));
            }
            finally { UnityEngine.Object.Destroy(shortClip); }
        }

        [UnityTest]
        public IEnumerator AudioRootSurvivesARealSceneUnload()
        {
            Scene scene = SceneManager.CreateScene("CoreAudioTemporaryScene_" + Guid.NewGuid().ToString("N"));
            audio.PlayMusic(firstClip);
            AsyncOperation unload = SceneManager.UnloadSceneAsync(scene);
            while (!unload.isDone) yield return null;
            Assert.That(ownedRunner, Is.Not.Null);
            Assert.That(audio.IsMusicPlaying, Is.True);
            Assert.That(audio.CurrentMusic, Is.EqualTo(firstClip));
        }

        [UnityTest]
        public IEnumerator ShutdownStopsPlaybackDestroysOwnedObjectsAndClearsMixerOverrides()
        {
            GameObject root = ownedRunner.gameObject;
            AudioSource music = Music();
            audio.PlayMusic(firstClip, fadeInSeconds: 0.5f);
            audio.PlaySfx3D(secondClip, Vector3.one);
            audio.SetVolumes(0.25f, 0f, 0.3f);
            Task shutdown = audio.ShutdownAsync(CancellationToken.None);
            Assert.That(audio.IsInitialized, Is.False);
            Assert.That(root.activeSelf, Is.False);
            Assert.That(music.clip, Is.Null);
            Assert.That(audio.CurrentMusic, Is.Null);
            Assert.That(audio.ActiveSfxCount, Is.Zero);
            Assert.That(audio.AllocatedSfxSourceCount, Is.Zero);
            yield return Wait(shutdown);
            Assert.That(root == null, Is.True);
            AssertMixer("MasterVolume", 0f);
            AssertMixer("MusicVolume", 0f);
            AssertMixer("SfxVolume", 0f);
            Assert.Throws<InvalidOperationException>(() => audio.PlayMusic(firstClip));
            Assert.Throws<InvalidOperationException>(() => audio.PlaySfx2D(firstClip));
            Assert.Throws<InvalidOperationException>(() => audio.SetVolumes(1f, 1f, 1f));
            Assert.That(audio.ShutdownAsync(CancellationToken.None), Is.SameAs(shutdown));
        }

        [UnityTest]
        public IEnumerator InitializationIsSharedAndShutdownDuringStartupPreventsLatePlayback()
        {
            int previousCount = UnityEngine.Object.FindObjectsByType<LumiAudioRunner>(FindObjectsSortMode.None).Length;
            var otherAudio = new AudioService(configuration);
            var otherCoordinator = new BootstrapCoordinator(new ILumiService[] { otherAudio });
            extraCoordinators.Add(otherCoordinator);
            Task first = otherAudio.InitializeAsync(otherCoordinator.Context, CancellationToken.None);
            Task duplicate = otherAudio.InitializeAsync(otherCoordinator.Context, CancellationToken.None);
            Assert.That(duplicate, Is.SameAs(first));
            Assert.That(UnityEngine.Object.FindObjectsByType<LumiAudioRunner>(FindObjectsSortMode.None),
                Has.Length.EqualTo(previousCount + 1));
            Task shutdown = otherAudio.ShutdownAsync(CancellationToken.None);
            yield return Wait(shutdown);
            Assert.That(first.IsCanceled, Is.True);
            Assert.That(otherAudio.IsInitialized, Is.False);
            Assert.That(UnityEngine.Object.FindObjectsByType<LumiAudioRunner>(FindObjectsSortMode.None),
                Has.Length.EqualTo(previousCount));
            Assert.Throws<InvalidOperationException>(() => otherAudio.InitializeAsync(otherCoordinator.Context,
                CancellationToken.None));
        }

        [UnityTest]
        public IEnumerator MissingExposedParameterRollsBackPartialStartup()
        {
            int previousCount = UnityEngine.Object.FindObjectsByType<LumiAudioRunner>(FindObjectsSortMode.None).Length;
            var invalid = new AudioConfiguration(configuration.Mixer, configuration.MasterGroup,
                configuration.MusicGroup, configuration.SfxGroup, musicVolumeParameter: "MissingMusicVolume");
            var otherAudio = new AudioService(invalid);
            var otherCoordinator = new BootstrapCoordinator(new ILumiService[] { otherAudio });
            extraCoordinators.Add(otherCoordinator);
            LogAssert.Expect(LogType.Warning, "Exposed name does not exist: MissingMusicVolume");
            Task startup = otherCoordinator.InitializeAsync();
            while (!startup.IsCompleted) yield return null;
            Assert.That(startup.IsFaulted, Is.True);
            Assert.That(startup.Exception.ToString(), Does.Contain("MissingMusicVolume"));
            yield return null;
            Assert.That(otherAudio.IsInitialized, Is.False);
            Assert.That(UnityEngine.Object.FindObjectsByType<LumiAudioRunner>(FindObjectsSortMode.None),
                Has.Length.EqualTo(previousCount));
        }

        [UnityTest]
        public IEnumerator CancellationDuringInitializationReleasesThePartialRoot()
        {
            int previousCount = UnityEngine.Object.FindObjectsByType<LumiAudioRunner>(FindObjectsSortMode.None).Length;
            var otherAudio = new AudioService(configuration);
            var otherCoordinator = new BootstrapCoordinator(new ILumiService[] { otherAudio });
            extraCoordinators.Add(otherCoordinator);
            using (var cancellation = new CancellationTokenSource())
            {
                Task startup = otherCoordinator.InitializeAsync(cancellation.Token);
                cancellation.Cancel();
                while (!startup.IsCompleted) yield return null;
                Assert.That(startup.IsCanceled, Is.True);
            }
            yield return null;
            Assert.That(otherAudio.IsInitialized, Is.False);
            Assert.That(UnityEngine.Object.FindObjectsByType<LumiAudioRunner>(FindObjectsSortMode.None),
                Has.Length.EqualTo(previousCount));
        }

        [Test]
        public void InvalidConfigurationIsRejectedBeforeAllocatingNativeSources()
        {
            Assert.Throws<ArgumentException>(() => new AudioConfiguration(configuration.Mixer,
                configuration.MasterGroup, configuration.MasterGroup, configuration.SfxGroup));
            Assert.Throws<ArgumentOutOfRangeException>(() => new AudioConfiguration(configuration.Mixer,
                configuration.MasterGroup, configuration.MusicGroup, configuration.SfxGroup, maxSfxSources: 0));
            Assert.Throws<ArgumentException>(() => new AudioConfiguration(configuration.Mixer,
                configuration.MasterGroup, configuration.MusicGroup, configuration.SfxGroup,
                masterVolumeParameter: "MusicVolume"));
        }

        private AudioSource Music() => ownedRunner.GetComponent<AudioSource>();

        private AudioSource[] Sfx()
        {
            var sources = new List<AudioSource>();
            foreach (AudioSource source in ownedRunner.GetComponentsInChildren<AudioSource>())
                if (source != Music()) sources.Add(source);
            return sources.ToArray();
        }

        private void AssertMixer(string parameter, float expected)
        {
            Assert.That(configuration.Mixer.GetFloat(parameter, out float actual), Is.True);
            Assert.That(actual, Is.EqualTo(expected).Within(0.001f));
        }

        private static IEnumerator Wait(Task task)
        {
            float deadline = Time.realtimeSinceStartup + 5f;
            while (!task.IsCompleted)
            {
                Assert.That(Time.realtimeSinceStartup, Is.LessThan(deadline), "Audio lifecycle task did not complete.");
                yield return null;
            }
            task.GetAwaiter().GetResult();
        }

        private static AudioConfiguration CreateMixerConfiguration(string path, int maximum)
        {
            Type controller = null;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                controller = assembly.GetType("UnityEditor.Audio.AudioMixerController", false);
                if (controller != null) break;
            }
            if (controller == null) throw new InvalidOperationException("Unity Editor native mixer creation API is unavailable.");
            object mixerObject = controller.GetMethod("CreateMixerControllerAtPath",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Invoke(null, new object[] { path });
            var mixer = (AudioMixer)mixerObject;
            object master = controller.GetProperty("masterGroup", Members).GetValue(mixerObject);
            object music = controller.GetMethod("CreateNewGroup", Members).Invoke(mixerObject, new object[] { "Music", false });
            object sfx = controller.GetMethod("CreateNewGroup", Members).Invoke(mixerObject, new object[] { "SFX", false });
            MethodInfo addChild = controller.GetMethod("AddChildToParent", Members);
            addChild.Invoke(mixerObject, new[] { music, master });
            addChild.Invoke(mixerObject, new[] { sfx, master });
            PropertyInfo exposedProperty = controller.GetProperty("exposedParameters", Members);
            Type exposedType = exposedProperty.PropertyType.GetElementType();
            Array exposed = Array.CreateInstance(exposedType, 3);
            object[] groups = { master, music, sfx };
            string[] names = { "MasterVolume", "MusicVolume", "SfxVolume" };
            for (int index = 0; index < groups.Length; index++)
            {
                object parameter = Activator.CreateInstance(exposedType);
                object guid = groups[index].GetType().GetMethod("GetGUIDForVolume", Members).Invoke(groups[index], null);
                exposedType.GetField("guid", Members).SetValue(parameter, guid);
                exposedType.GetField("name", Members).SetValue(parameter, names[index]);
                exposed.SetValue(parameter, index);
            }
            exposedProperty.SetValue(mixerObject, exposed);
            EditorUtility.SetDirty(mixer);
            AssetDatabase.SaveAssetIfDirty(mixer);
            return new AudioConfiguration(mixer, (AudioMixerGroup)master,
                (AudioMixerGroup)music, (AudioMixerGroup)sfx, maximum);
        }
    }
}
#endif
