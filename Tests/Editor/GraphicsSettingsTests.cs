using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lumivale.Core.Settings;
using NUnit.Framework;
using UnityEngine;

namespace Lumivale.Tests.EditMode
{
    public sealed class GraphicsSettingsTests
    {
        private const string Key = "com.lumivale.tests.graphics.preferences";
        private MemoryStorage storage;
        private RenderingBackend backend;
        private SettingsService service;
        private SettingsValues configured;

        [SetUp]
        public void SetUp()
        {
            storage = new MemoryStorage();
            backend = new RenderingBackend();
            configured = Values();
            service = CreateService(backend);
        }

        [TearDown]
        public async Task TearDown()
        {
            backend.ReleaseFailure = null;
            await service.ShutdownAsync(CancellationToken.None);
        }

        [Test]
        public async Task OldVersionOneDocumentKeepsExistingChoicesAndSeedsNewProjectDefaults()
        {
            storage.Document = "{\"schemaVersion\":1,\"values\":{\"Width\":1920,\"Height\":1080," +
                "\"QualityName\":\"Low\",\"MusicVolume\":0,\"LanguageCode\":\"fr\"}}";
            string original = storage.Document;
            await Initialize();
            Assert.That(service.LastLoadStatus, Is.EqualTo(SettingsLoadStatus.Loaded));
            Assert.That(service.Current.Width, Is.EqualTo(1920));
            Assert.That(service.Current.QualityName, Is.EqualTo("Low"));
            Assert.That(service.Current.MusicVolume, Is.Zero);
            Assert.That(service.Current.LanguageCode, Is.EqualTo("fr"));
            Assert.That(service.Current.FrameRateLimit, Is.EqualTo(configured.FrameRateLimit));
            Assert.That(service.Current.RenderScale, Is.EqualTo(configured.RenderScale));
            Assert.That(service.Current.AntiAliasing, Is.EqualTo(configured.AntiAliasing));
            Assert.That(storage.Document, Is.EqualTo(original));
            Assert.That(storage.SaveCount, Is.Zero);
            Assert.That(backend.ApplyCount, Is.Zero);
        }

        [Test]
        public async Task VersionOnePreservesExplicitFalseZeroAndUnlimited()
        {
            storage.Document = "{\"schemaVersion\":1,\"values\":{\"VSync\":false," +
                "\"PostProcessing\":false,\"ShadowDistance\":0,\"FrameRateLimit\":-1," +
                "\"AntiAliasing\":0,\"ShadowQuality\":0,\"TextureQuality\":0,\"LodQuality\":0}}";
            await Initialize();
            Assert.That(service.Current.VSync, Is.False);
            Assert.That(service.Current.PostProcessing, Is.False);
            Assert.That(service.Current.ShadowDistance, Is.Zero);
            Assert.That(service.Current.FrameRateLimit, Is.EqualTo(-1));
            Assert.That(service.Current.AntiAliasing, Is.EqualTo(GraphicsAntiAliasing.Off));
            Assert.That(service.Current.ShadowQuality, Is.EqualTo(GraphicsShadowQuality.Off));
            Assert.That(service.Current.TextureQuality, Is.EqualTo(GraphicsQuality.Low));
            Assert.That(service.Current.LodQuality, Is.EqualTo(GraphicsQuality.Low));
        }

        [Test]
        public async Task RenderingFieldsRoundTripInTheExistingPreferenceDocument()
        {
            await Initialize();
            var changed = service.Current;
            changed.FrameRateLimit = 120;
            changed.VSync = false;
            changed.RenderScale = 0.5f;
            changed.AntiAliasing = GraphicsAntiAliasing.MSAA4x;
            changed.PostProcessing = false;
            changed.ShadowQuality = GraphicsShadowQuality.Low;
            changed.ShadowDistance = 12f;
            changed.TextureQuality = GraphicsQuality.Low;
            changed.AnisotropicFiltering = AnisotropicFiltering.Disable;
            changed.LodQuality = GraphicsQuality.Low;
            service.Update(changed);
            service.Save();
            await service.ShutdownAsync(CancellationToken.None);
            service = CreateService(backend);
            await Initialize();
            Assert.That(service.Current.ContentEquals(changed), Is.True);
            Assert.That(storage.SaveCount, Is.EqualTo(1));
            StringAssert.Contains("\"schemaVersion\":1", storage.Document);
        }

        [Test]
        public async Task FiniteRangesClampAndInvalidEnumsUseProjectDefaults()
        {
            await Initialize();
            var changed = service.Current;
            changed.FrameRateLimit = 144;
            changed.RenderScale = 0.1f;
            changed.ShadowDistance = 999f;
            changed.AntiAliasing = (GraphicsAntiAliasing)999;
            changed.ShadowQuality = (GraphicsShadowQuality)999;
            changed.TextureQuality = (GraphicsQuality)999;
            changed.AnisotropicFiltering = (AnisotropicFiltering)999;
            changed.LodQuality = (GraphicsQuality)999;
            var actual = service.Update(changed);
            Assert.That(actual.FrameRateLimit, Is.EqualTo(configured.FrameRateLimit));
            Assert.That(actual.RenderScale, Is.EqualTo(0.5f));
            Assert.That(actual.ShadowDistance, Is.EqualTo(200f));
            Assert.That(actual.AntiAliasing, Is.EqualTo(configured.AntiAliasing));
            Assert.That(actual.ShadowQuality, Is.EqualTo(configured.ShadowQuality));
            Assert.That(actual.TextureQuality, Is.EqualTo(configured.TextureQuality));
            Assert.That(actual.AnisotropicFiltering, Is.EqualTo(configured.AnisotropicFiltering));
            Assert.That(actual.LodQuality, Is.EqualTo(configured.LodQuality));
            changed.RenderScale = 3f;
            changed.ShadowDistance = -1f;
            actual = service.Update(changed);
            Assert.That(actual.RenderScale, Is.EqualTo(1f));
            Assert.That(actual.ShadowDistance, Is.Zero);
        }

        [Test]
        public async Task NonFiniteRangesUseProjectDefaultsAndInvalidProjectDefaultsUseSafeValues()
        {
            await Initialize();
            var changed = service.Current;
            changed.RenderScale = float.NaN;
            changed.ShadowDistance = float.PositiveInfinity;
            var actual = service.Update(changed);
            Assert.That(actual.RenderScale, Is.EqualTo(configured.RenderScale));
            Assert.That(actual.ShadowDistance, Is.EqualTo(configured.ShadowDistance));
            await service.ShutdownAsync(CancellationToken.None);
            configured.RenderScale = float.NegativeInfinity;
            configured.ShadowDistance = float.NaN;
            configured.FrameRateLimit = 0;
            configured.TextureQuality = (GraphicsQuality)(-1);
            service = CreateService(backend);
            await Initialize();
            Assert.That(service.Defaults.RenderScale, Is.EqualTo(1f));
            Assert.That(service.Defaults.ShadowDistance, Is.EqualTo(35f));
            Assert.That(service.Defaults.FrameRateLimit, Is.EqualTo(60));
            Assert.That(service.Defaults.TextureQuality, Is.EqualTo(GraphicsQuality.High));
        }

        [TestCase(-1)]
        [TestCase(30)]
        [TestCase(60)]
        [TestCase(120)]
        public async Task SupportedFrameRateLimitsRemainDistinct(int value)
        {
            await Initialize();
            var changed = service.Current;
            changed.FrameRateLimit = value;
            Assert.That(service.Update(changed).FrameRateLimit, Is.EqualTo(value));
        }

        [TestCase("\"VSync\":0")]
        [TestCase("\"VSync\":\"false\"")]
        [TestCase("\"VSync\":null")]
        [TestCase("\"PostProcessing\":1")]
        [TestCase("\"PostProcessing\":[]")]
        [TestCase("\"FrameRateLimit\":\"60\"")]
        [TestCase("\"FrameRateLimit\":60.5")]
        [TestCase("\"RenderScale\":true")]
        [TestCase("\"RenderScale\":\"0.5\"")]
        [TestCase("\"ShadowDistance\":null")]
        [TestCase("\"AntiAliasing\":\"SMAA\"")]
        [TestCase("\"ShadowQuality\":false")]
        [TestCase("\"TextureQuality\":{}")]
        [TestCase("\"AnisotropicFiltering\":[]")]
        [TestCase("\"LodQuality\":1.5")]
        [TestCase("\"VSync\":true,\"VSync\":false")]
        [TestCase("\"RenderScale\":1,\"RenderScale\":0.5")]
        public async Task InvalidKnownRenderingFieldsPreserveTheStoredDocument(string fields)
        {
            string document = "{\"schemaVersion\":1,\"values\":{" + fields + "}}";
            storage.Document = document;
            await Initialize();
            Assert.That(service.LastLoadStatus, Is.EqualTo(SettingsLoadStatus.InvalidData));
            Assert.That(service.Current.ContentEquals(configured), Is.True);
            Assert.That(storage.Document, Is.EqualTo(document));
            Assert.That(storage.SaveCount, Is.Zero);
        }

        [Test]
        public async Task FutureSchemaRenderingFieldsArePreservedWithoutVersionOneTypeChecks()
        {
            storage.Document = "{\"schemaVersion\":2,\"values\":{\"VSync\":\"future\",\"RenderScale\":{}}}";
            string original = storage.Document;
            await Initialize();
            Assert.That(service.LastLoadStatus, Is.EqualTo(SettingsLoadStatus.UnsupportedVersion));
            Assert.That(storage.Document, Is.EqualTo(original));
            Assert.That(storage.SaveCount, Is.Zero);
        }

        [Test]
        public async Task RenderingBackendReceivesCopiesAndOnlyItsRenderingChangesAreAccepted()
        {
            await Initialize();
            backend.Normalize = snapshot =>
            {
                snapshot.RenderScale = 0.75f;
                snapshot.Width = 7;
                snapshot.MusicVolume = 0f;
                return snapshot;
            };
            var input = service.Current;
            var validated = service.Update(input);
            Assert.That(input.RenderScale, Is.EqualTo(configured.RenderScale));
            Assert.That(validated.RenderScale, Is.EqualTo(0.75f));
            Assert.That(validated.Width, Is.EqualTo(configured.Width));
            Assert.That(validated.MusicVolume, Is.EqualTo(configured.MusicVolume));
            backend.LastValidation.RenderScale = 0.5f;
            Assert.That(service.Current.RenderScale, Is.EqualTo(0.75f));
            service.ApplyGraphics();
            Assert.That(backend.ApplyCount, Is.EqualTo(1));
            Assert.That(backend.DisplayOnlyApplyCount, Is.Zero);
            Assert.That(backend.Applied.ContentEquals(service.Current), Is.True);
            backend.Applied.RenderScale = 0.5f;
            Assert.That(service.Current.RenderScale, Is.EqualTo(0.75f));
        }

        [Test]
        public async Task BackendRenderingResultsAreBoundedAgainAndMissingResultsAreRejected()
        {
            await Initialize();
            backend.Normalize = value => { value.RenderScale = float.NaN; value.ShadowDistance = 999f; return value; };
            var validated = service.Update(service.Current);
            Assert.That(validated.RenderScale, Is.EqualTo(1f));
            Assert.That(validated.ShadowDistance, Is.EqualTo(200f));
            backend.Normalize = _ => null;
            Assert.Throws<InvalidOperationException>(() => service.Update(service.Current));
            Assert.That(service.IsInitialized, Is.True);
        }

        [TestCase(nameof(SettingsValues.FrameRateLimit))]
        [TestCase(nameof(SettingsValues.VSync))]
        [TestCase(nameof(SettingsValues.RenderScale))]
        [TestCase(nameof(SettingsValues.AntiAliasing))]
        [TestCase(nameof(SettingsValues.PostProcessing))]
        [TestCase(nameof(SettingsValues.ShadowQuality))]
        [TestCase(nameof(SettingsValues.ShadowDistance))]
        [TestCase(nameof(SettingsValues.TextureQuality))]
        [TestCase(nameof(SettingsValues.AnisotropicFiltering))]
        [TestCase(nameof(SettingsValues.LodQuality))]
        public async Task RenderingChangeParticipatesInEqualityAndNotifications(string fieldName)
        {
            await Initialize();
            int notifications = 0;
            service.Changed += _ => notifications++;
            var same = service.Current;
            Assert.That(same.ContentEquals(service.Current), Is.True);
            service.Update(same);
            Assert.That(notifications, Is.Zero);
            var field = typeof(SettingsValues).GetField(fieldName);
            object before = field.GetValue(same);
            if (field.FieldType == typeof(bool)) field.SetValue(same, !(bool)before);
            else if (field.FieldType == typeof(int)) field.SetValue(same, 120);
            else if (field.FieldType == typeof(float)) field.SetValue(same,
                fieldName == nameof(SettingsValues.RenderScale) ? 0.75f : 12f);
            else
            {
                Array choices = Enum.GetValues(field.FieldType);
                int index = Array.IndexOf(choices, before);
                field.SetValue(same, choices.GetValue((index + 1) % choices.Length));
            }
            Assert.That(same.ContentEquals(service.Current), Is.False);
            service.Update(same);
            Assert.That(notifications, Is.EqualTo(1));
            Assert.That(service.Current.ContentEquals(null), Is.False);
        }

        [Test]
        public async Task ResetRestoresAllRenderingDefaultsWithoutApplyingOrSaving()
        {
            await Initialize();
            var changed = service.Current;
            changed.FrameRateLimit = -1;
            changed.RenderScale = 0.5f;
            changed.PostProcessing = false;
            changed.ShadowDistance = 0f;
            service.Update(changed);
            var actual = service.ResetDefaults();
            Assert.That(actual.ContentEquals(configured), Is.True);
            Assert.That(backend.ApplyCount, Is.Zero);
            Assert.That(storage.SaveCount, Is.Zero);
        }

        [Test]
        public async Task ShutdownReleasesNativeResourcesEvenWithCancellationAndBackendCanBeReused()
        {
            await Initialize();
            await service.ShutdownAsync(new CancellationToken(true));
            Assert.That(backend.ReleaseCount, Is.EqualTo(1));
            Assert.That(service.IsInitialized, Is.False);
            await Initialize();
            service.ApplyGraphics();
            Assert.That(service.IsInitialized, Is.True);
            Assert.That(backend.ApplyCount, Is.EqualTo(1));
            Assert.That(service.RenderingCapabilities, Is.SameAs(backend.RenderingCapabilities));
        }

        [Test]
        public async Task FailedNativeReleaseStillClearsServiceStateAndObservers()
        {
            await Initialize();
            int notifications = 0;
            service.Changed += _ => notifications++;
            backend.ReleaseFailure = new InvalidOperationException("Native restoration failed.");
            Assert.Throws<InvalidOperationException>(() => service.ShutdownAsync(CancellationToken.None));
            Assert.That(service.IsInitialized, Is.False);
            Assert.Throws<InvalidOperationException>(() => { var ignored = service.Current; });
            backend.ReleaseFailure = null;
            await Initialize();
            var changed = service.Current;
            changed.VSync = false;
            service.Update(changed);
            Assert.That(notifications, Is.Zero);
        }

        [Test]
        public async Task NativeRestorationCallbacksCannotReenterPreferenceUpdatesDuringShutdown()
        {
            await Initialize();
            var previous = service.Current;
            bool invoked = false;
            backend.OnRelease = () =>
            {
                invoked = true;
                Assert.That(service.IsInitialized, Is.False);
                Assert.Throws<InvalidOperationException>(() => service.Update(previous));
            };
            await service.ShutdownAsync(CancellationToken.None);
            Assert.That(invoked, Is.True);
            backend.OnRelease = null;
        }

        [Test]
        public async Task ExistingDisplayOnlyBackendsRemainSupportedAndExposeNoRenderingCapabilities()
        {
            await service.ShutdownAsync(CancellationToken.None);
            var display = new DisplayBackend();
            service = CreateService(display);
            await Initialize();
            Assert.That(service.RenderingCapabilities, Is.SameAs(RenderingCapabilities.None));
            Assert.That(service.RenderingCapabilities.SupportsRenderScale, Is.False);
            Assert.That(service.RenderingCapabilities.AntiAliasingModes, Is.Empty);
            service.ApplyGraphics();
            Assert.That(display.ApplyCount, Is.EqualTo(1));
        }

        [Test]
        public void CapabilityModesAreCopiedDeduplicatedReadOnlyAndValidated()
        {
            var modes = new List<GraphicsAntiAliasing> { GraphicsAntiAliasing.Off, GraphicsAntiAliasing.SMAA,
                GraphicsAntiAliasing.SMAA };
            var capabilities = new RenderingCapabilities(supportsRenderScale: true, antiAliasingModes: modes);
            modes.Clear();
            Assert.That(capabilities.SupportsRenderScale, Is.True);
            Assert.That(capabilities.AntiAliasingModes, Is.EqualTo(new[] { GraphicsAntiAliasing.Off, GraphicsAntiAliasing.SMAA }));
            Assert.Throws<NotSupportedException>(() => ((IList<GraphicsAntiAliasing>)capabilities.AntiAliasingModes).Clear());
            Assert.Throws<ArgumentException>(() => new RenderingCapabilities(antiAliasingModes:
                new[] { (GraphicsAntiAliasing)999 }));
        }

        private Task Initialize() => service.InitializeAsync(null, CancellationToken.None);

        private SettingsService CreateService(IGraphicsSettingsBackend graphics) =>
            new SettingsService(new SettingsConfiguration(configured, Key, new[] { "en", "fr" }), storage, graphics);

        private static SettingsValues Values() => new SettingsValues
        {
            Width = 1280, Height = 720, DisplayMode = FullScreenMode.Windowed, QualityName = "High",
            MusicVolume = 0.5f, FrameRateLimit = 60, VSync = true, RenderScale = 1f,
            AntiAliasing = GraphicsAntiAliasing.SMAA, PostProcessing = true,
            ShadowQuality = GraphicsShadowQuality.Medium, ShadowDistance = 35f,
            TextureQuality = GraphicsQuality.High, AnisotropicFiltering = AnisotropicFiltering.Enable,
            LodQuality = GraphicsQuality.High
        };

        private sealed class MemoryStorage : ISettingsStorage
        {
            public string Document;
            public int SaveCount;
            public bool TryLoad(string key, out string json) { json = Document; return json != null; }
            public void Save(string key, string json) { Document = json; SaveCount++; }
        }

        private class DisplayBackend : IGraphicsSettingsBackend
        {
            public int ApplyCount;
            public GraphicsCapabilities GetCapabilities() => new GraphicsCapabilities(
                new[] { new DisplayResolution(1280, 720), new DisplayResolution(1920, 1080) },
                new[] { FullScreenMode.Windowed, FullScreenMode.FullScreenWindow },
                new[] { "Low", "High" }, new DisplayResolution(1280, 720), FullScreenMode.Windowed, "High");
            public virtual void Apply(DisplayResolution resolution, FullScreenMode mode, string qualityName) => ApplyCount++;
        }

        private sealed class RenderingBackend : DisplayBackend, IRenderingSettingsBackend
        {
            public int DisplayOnlyApplyCount;
            public int ReleaseCount;
            public Exception ReleaseFailure;
            public Action OnRelease;
            public Func<SettingsValues, SettingsValues> Normalize;
            public SettingsValues LastValidation;
            public SettingsValues Applied;
            public RenderingCapabilities RenderingCapabilities { get; } = new RenderingCapabilities(
                true, true, true, true, true, true, true, true,
                new[] { GraphicsAntiAliasing.Off, GraphicsAntiAliasing.SMAA, GraphicsAntiAliasing.MSAA4x });

            public SettingsValues ValidateRendering(SettingsValues values, SettingsValues fallback)
            {
                LastValidation = values;
                return Normalize == null ? values : Normalize(values);
            }

            public override void Apply(DisplayResolution resolution, FullScreenMode mode, string qualityName) =>
                DisplayOnlyApplyCount++;
            public void Apply(SettingsValues values) { ApplyCount++; Applied = values; }
            public void Release()
            {
                ReleaseCount++;
                OnRelease?.Invoke();
                if (ReleaseFailure != null) throw ReleaseFailure;
            }
        }
    }
}
