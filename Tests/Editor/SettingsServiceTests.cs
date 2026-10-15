using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lumivale.Core.Bootstrap;
using Lumivale.Core.Settings;
using NUnit.Framework;
using UnityEngine;

namespace Lumivale.Tests.EditMode
{
    public sealed class SettingsServiceTests
    {
        private const string Key = "com.lumivale.tests.settings.preferences";
        private MemoryStorage storage;
        private GraphicsBackend graphics;
        private SettingsValues projectDefaults;
        private SettingsService service;

        [SetUp]
        public void SetUp()
        {
            storage = new MemoryStorage();
            graphics = new GraphicsBackend();
            projectDefaults = new SettingsValues
            {
                Width = 1280,
                Height = 720,
                DisplayMode = FullScreenMode.Windowed,
                QualityName = "High",
                MasterVolume = 0.8f,
                MusicVolume = 0.6f,
                SfxVolume = 0.7f,
                LanguageCode = "en"
            };
            service = CreateService();
        }

        [TearDown]
        public async Task TearDown()
        {
            await service.ShutdownAsync(CancellationToken.None);
        }

        [Test]
        public async Task MissingStorageUsesProjectDefaultsWithoutApplyingOrSaving()
        {
            await Initialize();
            AssertValues(service.Current, projectDefaults);
            Assert.That(service.LastLoadStatus, Is.EqualTo(SettingsLoadStatus.Missing));
            Assert.That(storage.SaveCount, Is.Zero);
            Assert.That(graphics.ApplyCount, Is.Zero);
            Assert.That(service.Dependencies, Is.Empty);
        }

        [Test]
        public async Task ConfigurationAndReturnedSnapshotsDoNotExposeOwnedValues()
        {
            projectDefaults.MasterVolume = 0.1f;
            await Initialize();
            SettingsValues current = service.Current;
            SettingsValues defaults = service.Defaults;
            current.MasterVolume = 0.2f;
            defaults.MasterVolume = 0.3f;
            Assert.That(service.Current.MasterVolume, Is.EqualTo(0.8f));
            Assert.That(service.Defaults.MasterVolume, Is.EqualTo(0.8f));
        }

        [Test]
        public async Task SaveRestoresValuesAfterNewServiceInitialization()
        {
            await Initialize();
            SettingsValues changed = service.Current;
            changed.Width = 1920;
            changed.Height = 1080;
            changed.QualityName = "Low";
            changed.MasterVolume = 0f;
            changed.MusicVolume = 0.25f;
            changed.SfxVolume = 0.4f;
            changed.LanguageCode = "fr";
            service.Update(changed);
            service.Save();
            await service.ShutdownAsync(CancellationToken.None);
            service = CreateService();
            await Initialize();
            AssertValues(service.Current, changed);
            Assert.That(service.LastLoadStatus, Is.EqualTo(SettingsLoadStatus.Loaded));
            Assert.That(storage.SaveCount, Is.EqualTo(1));
            StringAssert.Contains("\"schemaVersion\":1", storage.Documents[Key]);
            StringAssert.Contains("\"QualityName\":\"Low\"", storage.Documents[Key]);
        }

        [Test]
        public async Task NativePlayerPrefsRoundTripsOnlyItsDedicatedKey()
        {
            string nativeKey = "com.lumivale.tests." + Guid.NewGuid().ToString("N") + ".preferences";
            string gameplayKey = nativeKey + ".gameplay";
            PlayerPrefs.SetString(gameplayKey, "unrelated gameplay data");
            try
            {
                var config = new SettingsConfiguration(projectDefaults, nativeKey, new[] { "en", "fr" });
                service = new SettingsService(config, new PlayerPrefsSettingsStorage(), graphics);
                await Initialize();
                SettingsValues changed = service.Current;
                changed.LanguageCode = "fr";
                changed.MusicVolume = 0.35f;
                service.Update(changed);
                service.Save();
                await service.ShutdownAsync(CancellationToken.None);
                service = new SettingsService(config, new PlayerPrefsSettingsStorage(), graphics);
                await Initialize();
                Assert.That(service.Current.LanguageCode, Is.EqualTo("fr"));
                Assert.That(service.Current.MusicVolume, Is.EqualTo(0.35f));
                Assert.That(PlayerPrefs.GetString(gameplayKey), Is.EqualTo("unrelated gameplay data"));
                Assert.That(PlayerPrefs.HasKey(nativeKey), Is.True);
            }
            finally
            {
                PlayerPrefs.DeleteKey(nativeKey);
                PlayerPrefs.DeleteKey(gameplayKey);
                PlayerPrefs.Save();
            }
        }

        [Test]
        public async Task ResetDefaultsChangesMemoryAndRequiresExplicitSave()
        {
            await Initialize();
            SettingsValues changed = service.Current;
            changed.MasterVolume = 0.2f;
            service.Update(changed);
            service.Save();
            string saved = storage.Documents[Key];
            int notifications = 0;
            service.Changed += _ => notifications++;
            service.ResetDefaults();
            AssertValues(service.Current, projectDefaults);
            Assert.That(notifications, Is.EqualTo(1));
            Assert.That(storage.Documents[Key], Is.EqualTo(saved));
            Assert.That(graphics.ApplyCount, Is.Zero);
            service.Save();
            Assert.That(storage.Documents[Key], Is.Not.EqualTo(saved));
        }

        [Test]
        public async Task InvalidVolumesClampFiniteValuesAndReplaceNonFiniteWithDefaults()
        {
            await Initialize();
            SettingsValues changed = service.Current;
            changed.MasterVolume = -4f;
            changed.MusicVolume = 12f;
            changed.SfxVolume = float.NaN;
            SettingsValues validated = service.Update(changed);
            Assert.That(validated.MasterVolume, Is.Zero);
            Assert.That(validated.MusicVolume, Is.EqualTo(1f));
            Assert.That(validated.SfxVolume, Is.EqualTo(0.7f));
            changed.MasterVolume = float.PositiveInfinity;
            changed.MusicVolume = float.NegativeInfinity;
            validated = service.Update(changed);
            Assert.That(validated.MasterVolume, Is.EqualTo(0.8f));
            Assert.That(validated.MusicVolume, Is.EqualTo(0.6f));
        }

        [Test]
        public async Task InvalidProjectDefaultVolumesUseSafeNormalizedDefaults()
        {
            projectDefaults.MasterVolume = float.NaN;
            projectDefaults.MusicVolume = -3f;
            projectDefaults.SfxVolume = 7f;
            service = CreateService();
            await Initialize();
            Assert.That(service.Current.MasterVolume, Is.EqualTo(1f));
            Assert.That(service.Current.MusicVolume, Is.Zero);
            Assert.That(service.Current.SfxVolume, Is.EqualTo(1f));
        }

        [Test]
        public async Task UnavailableStoredResolutionFallsBackWithoutChangingDisplay()
        {
            storage.Documents[Key] = "{\"schemaVersion\":1,\"values\":{\"Width\":8000,\"Height\":4000}}";
            await Initialize();
            Assert.That(service.Current.Width, Is.EqualTo(1280));
            Assert.That(service.Current.Height, Is.EqualTo(720));
            Assert.That(service.LastLoadStatus, Is.EqualTo(SettingsLoadStatus.Repaired));
            Assert.That(graphics.ApplyCount, Is.Zero);
        }

        [Test]
        public async Task UnavailableProjectDefaultsUseCurrentAvailableChoices()
        {
            projectDefaults.Width = 5;
            projectDefaults.Height = 5;
            projectDefaults.DisplayMode = FullScreenMode.ExclusiveFullScreen;
            projectDefaults.QualityName = "Removed";
            service = CreateService();
            await Initialize();
            Assert.That(service.Current.Width, Is.EqualTo(1920));
            Assert.That(service.Current.Height, Is.EqualTo(1080));
            Assert.That(service.Current.DisplayMode, Is.EqualTo(FullScreenMode.Windowed));
            Assert.That(service.Current.QualityName, Is.EqualTo("Low"));
        }

        [Test]
        public async Task InvalidDisplayModeAndQualityFallBackToProjectDefaults()
        {
            await Initialize();
            SettingsValues changed = service.Current;
            changed.DisplayMode = (FullScreenMode)123;
            changed.QualityName = "deleted profile";
            SettingsValues validated = service.Update(changed);
            Assert.That(validated.DisplayMode, Is.EqualTo(FullScreenMode.Windowed));
            Assert.That(validated.QualityName, Is.EqualTo("High"));
        }

        [Test]
        public async Task GraphicsOnlyApplyOnExplicitRequestAndUseQualityNameAfterReordering()
        {
            await Initialize();
            SettingsValues changed = service.Current;
            changed.DisplayMode = FullScreenMode.FullScreenWindow;
            changed.Width = 1920;
            changed.Height = 1080;
            service.Update(changed);
            Assert.That(graphics.ApplyCount, Is.Zero);
            Assert.That(storage.SaveCount, Is.Zero);
            graphics.Choices = Choices(new[] { "High", "Low" });
            service.ApplyGraphics();
            Assert.That(graphics.ApplyCount, Is.EqualTo(1));
            Assert.That(graphics.AppliedQuality, Is.EqualTo("High"));
            Assert.That(graphics.AppliedResolution, Is.EqualTo(new DisplayResolution(1920, 1080)));
            Assert.That(graphics.AppliedMode, Is.EqualTo(FullScreenMode.FullScreenWindow));
            Assert.That(storage.SaveCount, Is.Zero);
        }

        [Test]
        public async Task ApplyRevalidatesResolutionWhenDisplayCapabilitiesChange()
        {
            await Initialize();
            SettingsValues changed = service.Current;
            changed.Width = 1920;
            changed.Height = 1080;
            service.Update(changed);
            graphics.Choices = new GraphicsCapabilities(new[] { new DisplayResolution(1280, 720) },
                new[] { FullScreenMode.Windowed }, new[] { "Low" }, new DisplayResolution(1280, 720),
                FullScreenMode.Windowed, "Low");
            service.ApplyGraphics();
            Assert.That(service.Current.Width, Is.EqualTo(1280));
            Assert.That(service.Current.QualityName, Is.EqualTo("Low"));
            Assert.That(graphics.AppliedResolution, Is.EqualTo(new DisplayResolution(1280, 720)));
        }

        [Test]
        public async Task PartialDocumentRetainsConfiguredDefaultsAndHonorsExplicitZero()
        {
            storage.Documents[Key] = "{\"schemaVersion\":1,\"values\":{\"MasterVolume\":0,\"LanguageCode\":\"fr\"}}";
            await Initialize();
            Assert.That(service.Current.MasterVolume, Is.Zero);
            Assert.That(service.Current.MusicVolume, Is.EqualTo(0.6f));
            Assert.That(service.Current.SfxVolume, Is.EqualTo(0.7f));
            Assert.That(service.Current.Width, Is.EqualTo(1280));
            Assert.That(service.Current.QualityName, Is.EqualTo("High"));
            Assert.That(service.Current.LanguageCode, Is.EqualTo("fr"));
            Assert.That(service.LastLoadStatus, Is.EqualTo(SettingsLoadStatus.Loaded));
        }

        [TestCase("")]
        [TestCase("this is not JSON")]
        [TestCase("{\"schemaVersion\":1}")]
        [TestCase("{\"schemaVersion\":1,\"values\":null}")]
        [TestCase("{\"schemaVersion\":1,\"values\":false}")]
        [TestCase("{\"schemaVersion\":1,\"values\":[]}")]
        [TestCase("{\"schemaVersion\":1,\"note\":\"quoted \\\"values\\\": {}\"}")]
        [TestCase("{\"schemaVersion\":1,\"other\":{\"values\":{}}}")]
        public async Task CorruptStorageUsesDefaultsAndPreservesOriginalDocument(string document)
        {
            storage.Documents[Key] = document;
            await Initialize();
            AssertValues(service.Current, projectDefaults);
            Assert.That(service.LastLoadStatus, Is.EqualTo(SettingsLoadStatus.InvalidData));
            Assert.That(storage.Documents[Key], Is.EqualTo(document));
            Assert.That(storage.SaveCount, Is.Zero);
        }

        [TestCase("{\"schemaVersion\":1,\"values\":{\"MasterVolume\":\"bad\"}}")]
        [TestCase("{\"schemaVersion\":1,\"values\":{\"MasterVolume\":true}}")]
        [TestCase("{\"schemaVersion\":1,\"values\":{\"MasterVolume\":{}}}")]
        [TestCase("{\"schemaVersion\":1,\"values\":{\"MasterVolume\":null}}")]
        [TestCase("{\"schemaVersion\":1,\"values\":{\"MusicVolume\":false}}")]
        [TestCase("{\"schemaVersion\":1,\"values\":{\"SfxVolume\":[]}}")]
        [TestCase("{\"schemaVersion\":1,\"values\":{\"Width\":\"1920\"}}")]
        [TestCase("{\"schemaVersion\":1,\"values\":{\"Height\":720.5}}")]
        [TestCase("{\"schemaVersion\":1,\"values\":{\"DisplayMode\":\"3\"}}")]
        [TestCase("{\"schemaVersion\":1,\"values\":{\"QualityName\":5}}")]
        [TestCase("{\"schemaVersion\":1,\"values\":{\"LanguageCode\":false}}")]
        [TestCase("{\"schemaVersion\":\"1\",\"values\":{}}")]
        [TestCase("{\"schemaVersion\":1.2,\"values\":{}}")]
        [TestCase("{\"schemaVersion\":true,\"values\":{}}")]
        [TestCase("{\"schemaVersion\":2147483648,\"values\":{}}")]
        public async Task IncompatibleKnownFieldTypesCannotSilentlyBecomeNumericZero(string document)
        {
            storage.Documents[Key] = document;
            await Initialize();
            AssertValues(service.Current, projectDefaults);
            Assert.That(service.LastLoadStatus, Is.EqualTo(SettingsLoadStatus.InvalidData));
            Assert.That(storage.Documents[Key], Is.EqualTo(document));
            Assert.That(storage.SaveCount, Is.Zero);
        }

        [TestCase("{\"schemaVersion\":1,\"schemaVersion\":1,\"values\":{}}")]
        [TestCase("{\"schemaVersion\":1,\"values\":{},\"values\":{}}")]
        [TestCase("{\"schemaVersion\":1,\"values\":{\"MasterVolume\":0.2,\"MasterVolume\":0.4}}")]
        [TestCase("{\"schemaVersion\":1,\"values\":{\"Width\":1280,\"Wi\\u0064th\":1920}}")]
        public async Task RepeatedKnownFieldsAreRejectedAsAmbiguousAndPreserved(string document)
        {
            storage.Documents[Key] = document;
            await Initialize();
            AssertValues(service.Current, projectDefaults);
            Assert.That(service.LastLoadStatus, Is.EqualTo(SettingsLoadStatus.InvalidData));
            Assert.That(storage.Documents[Key], Is.EqualTo(document));
            Assert.That(storage.SaveCount, Is.Zero);
        }

        [Test]
        public async Task TypeValidationIgnoresUnknownNestedFieldsAndQuotedText()
        {
            storage.Documents[Key] = "{\"schemaVersion\":1,\"values\":{\"MasterVolume\":2.5e-1," +
                "\"note\":\"quoted \\\"Width\\\": true\",\"extra\":{\"Width\":\"bad\",\"MasterVolume\":true}}}";
            await Initialize();
            Assert.That(service.Current.MasterVolume, Is.EqualTo(0.25f));
            Assert.That(service.Current.Width, Is.EqualTo(1280));
            Assert.That(service.LastLoadStatus, Is.EqualTo(SettingsLoadStatus.Loaded));
        }

        [Test]
        public async Task NullTextFieldsUseValidatedDefaultsWithoutBecomingNumericValues()
        {
            storage.Documents[Key] = "{\"schemaVersion\":1,\"values\":{\"QualityName\":null,\"LanguageCode\":null}}";
            await Initialize();
            Assert.That(service.Current.QualityName, Is.EqualTo("High"));
            Assert.That(service.Current.LanguageCode, Is.EqualTo("en"));
            Assert.That(service.LastLoadStatus, Is.EqualTo(SettingsLoadStatus.Repaired));
        }

        [Test]
        public async Task FutureSchemaTypesAreNotValidatedUsingVersionOneRules()
        {
            string document = "{\"schemaVersion\":2,\"values\":{\"Width\":\"future-format\"," +
                "\"MasterVolume\":true,\"QualityName\":{\"future\":\"object\"}}}";
            storage.Documents[Key] = document;
            await Initialize();
            AssertValues(service.Current, projectDefaults);
            Assert.That(service.LastLoadStatus, Is.EqualTo(SettingsLoadStatus.UnsupportedVersion));
            Assert.That(storage.Documents[Key], Is.EqualTo(document));
            Assert.That(storage.SaveCount, Is.Zero);
        }

        [TestCase(0)]
        [TestCase(2)]
        public async Task UnknownSchemaPreservesStoredDataUntilExplicitSave(int version)
        {
            string document = "{\"schemaVersion\":" + version + ",\"values\":{\"MasterVolume\":0.1}}";
            storage.Documents[Key] = document;
            await Initialize();
            AssertValues(service.Current, projectDefaults);
            Assert.That(service.LastLoadStatus, Is.EqualTo(SettingsLoadStatus.UnsupportedVersion));
            Assert.That(storage.Documents[Key], Is.EqualTo(document));
            service.Save();
            Assert.That(storage.Documents[Key], Is.Not.EqualTo(document));
        }

        [Test]
        public async Task StorageReadFailureUsesDefaultsAndReportsTheFailure()
        {
            storage.ReadFailure = new InvalidOperationException("simulated unavailable storage");
            await Initialize();
            AssertValues(service.Current, projectDefaults);
            Assert.That(service.LastLoadStatus, Is.EqualTo(SettingsLoadStatus.StorageError));
            StringAssert.Contains("simulated unavailable storage", service.LastLoadMessage);
            Assert.That(storage.SaveCount, Is.Zero);
        }

        [Test]
        public async Task SaveAndGraphicsBackendFailuresAreVisibleToCaller()
        {
            await Initialize();
            storage.SaveFailure = new InvalidOperationException("save failed");
            Assert.Throws<InvalidOperationException>(() => service.Save());
            graphics.ApplyFailure = new InvalidOperationException("display failed");
            Assert.Throws<InvalidOperationException>(() => service.ApplyGraphics());
            Assert.That(service.IsInitialized, Is.True);
        }

        [Test]
        public async Task InvalidLanguageFallsBackAndSupportedCodesAreCanonicalized()
        {
            await Initialize();
            SettingsValues changed = service.Current;
            changed.LanguageCode = "de";
            Assert.That(service.Update(changed).LanguageCode, Is.EqualTo("en"));
            changed.LanguageCode = " FR ";
            Assert.That(service.Update(changed).LanguageCode, Is.EqualTo("fr"));
            changed.LanguageCode = "invalid code!";
            Assert.That(service.Update(changed).LanguageCode, Is.EqualTo("en"));
        }

        [Test]
        public async Task MixedCaseDefaultLanguageUsesCanonicalFallbackWithoutSpuriousNotifications()
        {
            projectDefaults.LanguageCode = " EN ";
            service = CreateService();
            await Initialize();
            Assert.That(service.Current.LanguageCode, Is.EqualTo("en"));
            Assert.That(service.Defaults.LanguageCode, Is.EqualTo("en"));
            int notifications = 0;
            service.Changed += _ => notifications++;
            foreach (string invalidLanguage in new[] { null, "de", "invalid code!" })
            {
                SettingsValues changed = service.Current;
                changed.LanguageCode = invalidLanguage;
                Assert.That(service.Update(changed).LanguageCode, Is.EqualTo("en"));
            }
            Assert.That(notifications, Is.Zero);
        }

        [Test]
        public async Task NotificationSnapshotsAreIsolatedAndObserverErrorsDoNotBlockOthers()
        {
            await Initialize();
            int first = 0;
            int second = 0;
            Exception failure = null;
            service.Changed += values =>
            {
                first++;
                values.MasterVolume = 0f;
                throw new InvalidOperationException("observer failed");
            };
            service.Changed += values =>
            {
                second++;
                Assert.That(values.MasterVolume, Is.EqualTo(0.25f));
            };
            service.ObserverFailed += exception => failure = exception;
            SettingsValues changed = service.Current;
            changed.MasterVolume = 0.25f;
            service.Update(changed);
            service.Update(changed);
            Assert.That(first, Is.EqualTo(1));
            Assert.That(second, Is.EqualTo(1));
            Assert.That(failure, Is.TypeOf<InvalidOperationException>());
            Assert.That(service.Current.MasterVolume, Is.EqualTo(0.25f));
        }

        [Test]
        public async Task ShutdownClearsObserversAndRestartsFromExplicitlySavedData()
        {
            await Initialize();
            SettingsValues changed = service.Current;
            changed.MasterVolume = 0.1f;
            service.Update(changed);
            service.Save();
            int calls = 0;
            service.Changed += _ => calls++;
            await service.ShutdownAsync(new CancellationToken(true));
            Assert.That(service.IsInitialized, Is.False);
            Assert.Throws<InvalidOperationException>(() => service.Save());
            await Initialize();
            Assert.That(service.Current.MasterVolume, Is.EqualTo(0.1f));
            service.ResetDefaults();
            Assert.That(calls, Is.Zero);
        }

        [Test]
        public async Task RepeatedInitializationDoesNotReloadOrLoseUnsavedChanges()
        {
            await Initialize();
            SettingsValues changed = service.Current;
            changed.MasterVolume = 0.15f;
            service.Update(changed);
            await Initialize();
            Assert.That(service.Current.MasterVolume, Is.EqualTo(0.15f));
            Assert.That(storage.LoadCount, Is.EqualTo(1));
        }

        [Test]
        public void CancelledInitializationDoesNotReadStorage()
        {
            Assert.Throws<OperationCanceledException>(() => service.InitializeAsync(null, new CancellationToken(true)));
            Assert.That(service.IsInitialized, Is.False);
            Assert.That(storage.LoadCount, Is.Zero);
        }

        [Test]
        public async Task ServiceIntegratesWithCoordinatorAndAChangeObserverCanStopItSafely()
        {
            var coordinator = new BootstrapCoordinator(new ILumiService[] { service });
            await coordinator.InitializeAsync();
            Assert.That(coordinator.Context.Get<SettingsService>(), Is.SameAs(service));
            int staleNotifications = 0;
            service.Changed += _ => service.ShutdownAsync(CancellationToken.None);
            service.Changed += _ => staleNotifications++;
            SettingsValues changed = service.Current;
            changed.MasterVolume = 0.3f;
            SettingsValues returned = service.Update(changed);
            Assert.That(returned.MasterVolume, Is.EqualTo(0.3f));
            Assert.That(service.IsInitialized, Is.False);
            Assert.That(staleNotifications, Is.Zero);
            await coordinator.ShutdownAsync();
        }

        [Test]
        public async Task ReentrantObserverDoesNotSendObsoleteValuesToLaterObservers()
        {
            await Initialize();
            bool replace = true;
            var observed = new List<float>();
            service.Changed += values =>
            {
                if (!replace)
                {
                    return;
                }

                replace = false;
                values.MasterVolume = 0.15f;
                service.Update(values);
            };
            service.Changed += values => observed.Add(values.MasterVolume);
            SettingsValues changed = service.Current;
            changed.MasterVolume = 0.3f;
            SettingsValues committed = service.Update(changed);
            Assert.That(committed.MasterVolume, Is.EqualTo(0.3f));
            Assert.That(service.Current.MasterVolume, Is.EqualTo(0.15f));
            CollectionAssert.AreEqual(new[] { 0.15f }, observed);
        }

        private SettingsService CreateService()
        {
            return new SettingsService(new SettingsConfiguration(projectDefaults, Key, new[] { "en", "fr" }),
                storage, graphics);
        }

        private Task Initialize() => service.InitializeAsync(null, CancellationToken.None);

        private static void AssertValues(SettingsValues actual, SettingsValues expected)
        {
            Assert.That(actual.Width, Is.EqualTo(expected.Width));
            Assert.That(actual.Height, Is.EqualTo(expected.Height));
            Assert.That(actual.DisplayMode, Is.EqualTo(expected.DisplayMode));
            Assert.That(actual.QualityName, Is.EqualTo(expected.QualityName));
            Assert.That(actual.MasterVolume, Is.EqualTo(expected.MasterVolume));
            Assert.That(actual.MusicVolume, Is.EqualTo(expected.MusicVolume));
            Assert.That(actual.SfxVolume, Is.EqualTo(expected.SfxVolume));
            Assert.That(actual.LanguageCode, Is.EqualTo(expected.LanguageCode));
        }

        private static GraphicsCapabilities Choices(string[] qualityNames = null)
        {
            return new GraphicsCapabilities(new[] { new DisplayResolution(1280, 720), new DisplayResolution(1920, 1080) },
                new[] { FullScreenMode.Windowed, FullScreenMode.FullScreenWindow }, qualityNames ?? new[] { "Low", "High" },
                new DisplayResolution(1920, 1080), FullScreenMode.Windowed, "Low");
        }

        private sealed class MemoryStorage : ISettingsStorage
        {
            public readonly Dictionary<string, string> Documents = new Dictionary<string, string>();
            public int LoadCount;
            public int SaveCount;
            public Exception ReadFailure;
            public Exception SaveFailure;

            public bool TryLoad(string key, out string json)
            {
                LoadCount++;
                if (ReadFailure != null)
                {
                    throw ReadFailure;
                }

                return Documents.TryGetValue(key, out json);
            }

            public void Save(string key, string json)
            {
                if (SaveFailure != null)
                {
                    throw SaveFailure;
                }

                SaveCount++;
                Documents[key] = json;
            }
        }

        private sealed class GraphicsBackend : IGraphicsSettingsBackend
        {
            public GraphicsCapabilities Choices = SettingsServiceTests.Choices();
            public int ApplyCount;
            public DisplayResolution AppliedResolution;
            public FullScreenMode AppliedMode;
            public string AppliedQuality;
            public Exception ApplyFailure;

            public GraphicsCapabilities GetCapabilities() => Choices;

            public void Apply(DisplayResolution resolution, FullScreenMode mode, string qualityName)
            {
                if (ApplyFailure != null)
                {
                    throw ApplyFailure;
                }

                ApplyCount++;
                AppliedResolution = resolution;
                AppliedMode = mode;
                AppliedQuality = qualityName;
            }
        }
    }
}
