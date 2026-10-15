using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lumivale.Core.Input;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace Lumivale.Core.Tests
{
    /// <summary>Core behavior against real Input System action maps and an owned virtual keyboard.</summary>
    public sealed class InputServiceTests : InputTestFixture
    {
        private InputActionAsset _source;
        private InputService _service;
        private Keyboard _keyboard;

        [SetUp]
        public void CreateActions()
        {
            _service = null;
            _keyboard = null;
            _source = ScriptableObject.CreateInstance<InputActionAsset>();
            _source.name = "Core Input test actions";
            _source.AddActionMap("Game").AddAction("CustomJump", InputActionType.Button, "<Keyboard>/space");
            _source.AddActionMap("Menu").AddAction("CustomAccept", InputActionType.Button, "<Keyboard>/enter");
            _source.AddActionMap("Paused").AddAction("CustomResume", InputActionType.Button, "<Keyboard>/escape");
            _source.AddActionMap("Unused").AddAction("Debug", InputActionType.Button, "<Keyboard>/f1");
        }

        [TearDown]
        public async Task CleanupCore()
        {
            if (_service != null)
            {
                await _service.ShutdownAsync(CancellationToken.None);
            }

            if (_keyboard != null && _keyboard.added)
            {
                InputSystem.RemoveDevice(_keyboard);
            }

            if (_source != null)
            {
                _source.Disable();
                UnityEngine.Object.DestroyImmediate(_source);
            }
        }

        [Test]
        public async Task InitializationClonesSourceAndEnablesOnlyConfiguredGameplayMaps()
        {
            await Initialize();

            Assert.That(_service.RuntimeAsset, Is.Not.SameAs(_source));
            Assert.That(_service.Id, Is.EqualTo("input"));
            Assert.That(_service.Dependencies, Is.Empty);
            Assert.That(_service.IsInitialized, Is.True);
            Assert.That(_service.CurrentContext, Is.EqualTo(InputContext.Gameplay));
            AssertEnabledMaps("Game");
            foreach (InputActionMap map in _source.actionMaps)
            {
                Assert.That(map.enabled, Is.False, "Source assets must remain untouched.");
            }
        }

        [Test]
        public async Task EnabledSourceMapIsNotModifiedByCloneOrShutdown()
        {
            _source.FindActionMap("Menu").Enable();
            await Initialize();
            AssertEnabledMaps("Game");
            await _service.ShutdownAsync(CancellationToken.None);
            Assert.That(_source.FindActionMap("Menu").enabled, Is.True);
            Assert.That(_source.FindActionMap("Game").enabled, Is.False);
        }

        [Test]
        public async Task ChangingContextsDisablesPreviousMapsAndCancelsHeldAction()
        {
            await InitializeWithKeyboard();
            int performed = 0;
            int cancelled = 0;
            _service.Subscribe("Game/CustomJump", _ => performed++);
            _service.Subscribe("Game/CustomJump", _ => cancelled++, InputActionPhase.Canceled);

            SetKeys(Key.Space);
            Assert.That(performed, Is.EqualTo(1));
            _service.SetContext(InputContext.UI);
            AssertEnabledMaps("Menu");
            Assert.That(cancelled, Is.EqualTo(1));
            _service.SetContext(InputContext.Pause);
            AssertEnabledMaps("Paused");
        }

        [Test]
        public async Task InvalidContextDoesNotDisableValidCurrentMaps()
        {
            await InitializeWithKeyboard();
            int cancelled = 0;
            _service.Subscribe("Game/CustomJump", _ => cancelled++, InputActionPhase.Canceled);
            SetKeys(Key.Space);

            Assert.Throws<ArgumentOutOfRangeException>(() => _service.SetContext((InputContext)123));
            Assert.That(_service.CurrentContext, Is.EqualTo(InputContext.Gameplay));
            Assert.That(cancelled, Is.Zero);
            AssertEnabledMaps("Game");
        }

        [Test]
        public void MissingMapRejectsAllContextsBeforeActivatingAnyRuntimeActions()
        {
            var maps = ContextMaps();
            maps[InputContext.Pause] = new[] { "Missing" };
            _service = new InputService(_source, maps);

            ArgumentException exception = Assert.ThrowsAsync<ArgumentException>(
                async () => await _service.InitializeAsync(null, CancellationToken.None));
            StringAssert.Contains("Missing", exception.Message);
            Assert.That(_service.RuntimeAsset, Is.Null);
            Assert.That(_service.IsInitialized, Is.False);
            Assert.That(_source.FindActionMap("Game").enabled, Is.False);
        }

        [TestCase("absent")]
        [TestCase("empty")]
        [TestCase("null")]
        [TestCase("duplicate")]
        public void EveryContextRequiresNonEmptyExistingUniqueMaps(string invalidConfiguration)
        {
            var maps = ContextMaps();
            switch (invalidConfiguration)
            {
                case "absent": maps.Remove(InputContext.UI); break;
                case "empty": maps[InputContext.UI] = Array.Empty<string>(); break;
                case "null": maps[InputContext.UI] = null; break;
                case "duplicate": maps[InputContext.UI] = new[] { "Menu", "Menu" }; break;
            }

            _service = new InputService(_source, maps);
            Assert.ThrowsAsync<ArgumentException>(async () =>
                await _service.InitializeAsync(null, CancellationToken.None));
            Assert.That(_service.RuntimeAsset, Is.Null);
        }

        [Test]
        public async Task ConfigurationIsCopiedAndMayShareMapsBetweenContexts()
        {
            var maps = ContextMaps();
            maps[InputContext.Pause] = new[] { "Menu" };
            _service = new InputService(_source, maps, InputContext.Pause);
            maps[InputContext.Pause][0] = "Missing";
            maps.Remove(InputContext.Gameplay);

            await _service.InitializeAsync(null, CancellationToken.None);
            AssertEnabledMaps("Menu");
            _service.SetContext(InputContext.Gameplay);
            AssertEnabledMaps("Game");
        }

        [Test]
        public async Task RepeatedInitializationKeepsOneCloneAndCancelledShutdownStillCleansIt()
        {
            await Initialize();
            InputActionAsset first = _service.RuntimeAsset;
            await _service.InitializeAsync(null, CancellationToken.None);
            Assert.That(_service.RuntimeAsset, Is.SameAs(first));

            await _service.ShutdownAsync(new CancellationToken(true));
            Assert.That(_service.IsInitialized, Is.False);
            Assert.That(_service.RuntimeAsset, Is.Null);
            Assert.That(first.FindActionMap("Game").enabled, Is.False,
                "Shutdown releases the clone immediately and schedules native destruction at frame end.");
            Assert.That(_source == null, Is.False);
            await _service.InitializeAsync(null, CancellationToken.None);
            Assert.That(_service.RuntimeAsset, Is.Not.SameAs(first));
            AssertEnabledMaps("Game");
        }

        [Test]
        public async Task ShutdownNotifiesConsumersBeforeDestroyingCloneAndClearsLifecycleObservers()
        {
            await Initialize();
            InputActionAsset clone = _service.RuntimeAsset;
            int shutdownNotifications = 0;
            bool cloneExistedDuringNotification = false;
            bool mapsWereDisabledDuringNotification = false;
            _service.StateChanged += () =>
            {
                if (!_service.IsInitialized)
                {
                    shutdownNotifications++;
                    cloneExistedDuringNotification = clone != null;
                    mapsWereDisabledDuringNotification = clone != null && !clone.FindActionMap("Game").enabled;
                }
            };

            await _service.ShutdownAsync(CancellationToken.None);
            Assert.That(shutdownNotifications, Is.EqualTo(1));
            Assert.That(cloneExistedDuringNotification, Is.True, "UI modules need the native asset to release action references.");
            Assert.That(mapsWereDisabledDuringNotification, Is.True);
            Assert.That(_service.RuntimeAsset, Is.Null);
            await _service.InitializeAsync(null, CancellationToken.None);
            await _service.ShutdownAsync(CancellationToken.None);
            Assert.That(shutdownNotifications, Is.EqualTo(1));
        }

        [Test]
        public async Task SharedContextMapRestoresAllActionsWhenConsumerDisabledOnlyOne()
        {
            _source.FindActionMap("Menu").AddAction("OtherMenuAction", InputActionType.Button, "<Keyboard>/tab");
            var maps = ContextMaps();
            maps[InputContext.Pause] = new[] { "Menu" };
            _service = new InputService(_source, maps, InputContext.UI);
            await _service.InitializeAsync(null, CancellationToken.None);
            _service.GetAction("Menu/CustomAccept").Disable();
            Assert.That(_service.RuntimeAsset.FindActionMap("Menu").enabled, Is.True);

            _service.SetContext(InputContext.Pause);
            Assert.That(_service.GetAction("Menu/CustomAccept").enabled, Is.True);
            Assert.That(_service.GetAction("Menu/OtherMenuAction").enabled, Is.True);
        }

        [Test]
        public async Task NestedSuspensionsApplyDesiredContextOnlyWhenLastLeaseIsReleased()
        {
            await Initialize();
            IDisposable first = _service.Suspend();
            IDisposable second = _service.Suspend();
            Assert.That(_service.IsSuspended, Is.True);
            AssertEnabledMaps();

            _service.SetContext(InputContext.UI);
            Assert.That(_service.CurrentContext, Is.EqualTo(InputContext.UI));
            AssertEnabledMaps();
            first.Dispose();
            first.Dispose();
            Assert.That(_service.IsSuspended, Is.True);
            AssertEnabledMaps();
            second.Dispose();
            Assert.That(_service.IsSuspended, Is.False);
            AssertEnabledMaps("Menu");
        }

        [Test]
        public async Task OldSuspensionCannotReleaseLeaseFromNewServiceLifetime()
        {
            await Initialize();
            IDisposable oldLease = _service.Suspend();
            await _service.ShutdownAsync(CancellationToken.None);
            oldLease.Dispose();
            await _service.InitializeAsync(null, CancellationToken.None);
            IDisposable stillOlderLease = _service.Suspend();
            await _service.ShutdownAsync(CancellationToken.None);
            await _service.InitializeAsync(null, CancellationToken.None);
            IDisposable currentLease = _service.Suspend();

            stillOlderLease.Dispose();
            Assert.That(_service.IsSuspended, Is.True);
            AssertEnabledMaps();
            currentLease.Dispose();
            AssertEnabledMaps("Game");
        }

        [Test]
        public async Task DisposedSubscriptionsAndPreviousLifetimeSubscribersReceiveNoFurtherInput()
        {
            await InitializeWithKeyboard();
            int disposedCalls = 0;
            int previousLifetimeCalls = 0;
            IDisposable disposed = _service.Subscribe("Game/CustomJump", _ => disposedCalls++);
            IDisposable previous = _service.Subscribe("Game/CustomJump", _ => previousLifetimeCalls++);
            SetKeys(Key.Space);
            Assert.That(disposedCalls, Is.EqualTo(1));
            Assert.That(previousLifetimeCalls, Is.EqualTo(1));
            SetKeys();
            disposed.Dispose();
            disposed.Dispose();
            SetKeys(Key.Space);
            Assert.That(disposedCalls, Is.EqualTo(1));
            Assert.That(previousLifetimeCalls, Is.EqualTo(2));

            await _service.ShutdownAsync(CancellationToken.None);
            SetKeys();
            await _service.InitializeAsync(null, CancellationToken.None);
            _service.RuntimeAsset.devices = new InputDevice[] { _keyboard };
            SetKeys(Key.Space);
            Assert.That(previousLifetimeCalls, Is.EqualTo(2));
            previous.Dispose();
        }

        [Test]
        public async Task FaultyActionAndStateObserversAreIsolatedAndReportedWithoutLogs()
        {
            _service = new InputService(_source, ContextMaps());
            int failures = 0;
            int healthyState = 0;
            int healthyAction = 0;
            _service.ObserverFailed += _ => throw new InvalidOperationException("Error observer failure");
            _service.ObserverFailed += _ => failures++;
            _service.StateChanged += () => throw new InvalidOperationException("State observer failure");
            _service.StateChanged += () => healthyState++;
            await _service.InitializeAsync(null, CancellationToken.None);
            _keyboard = InputSystem.AddDevice<Keyboard>("LumiCoreInputTestKeyboard");
            _service.RuntimeAsset.devices = new InputDevice[] { _keyboard };
            _service.Subscribe("Game/CustomJump", _ => throw new InvalidOperationException("Action observer failure"));
            _service.Subscribe("Game/CustomJump", _ => healthyAction++);

            SetKeys(Key.Space);
            Assert.That(healthyAction, Is.EqualTo(1));
            Assert.That(healthyState, Is.EqualTo(1));
            Assert.That(failures, Is.EqualTo(2));
            _service.SetContext(InputContext.UI);
            Assert.That(healthyState, Is.EqualTo(2));
            Assert.That(failures, Is.EqualTo(3));
            AssertEnabledMaps("Menu");
        }

        [Test]
        public async Task CancelledCallbackCanDisposeItsOwnSubscriptionDuringContextChange()
        {
            await InitializeWithKeyboard();
            int cancelled = 0;
            IDisposable token = null;
            token = _service.Subscribe("Game/CustomJump", _ =>
            {
                cancelled++;
                token.Dispose();
            }, InputActionPhase.Canceled);

            SetKeys(Key.Space);
            _service.SetContext(InputContext.UI);
            Assert.That(cancelled, Is.EqualTo(1));
            SetKeys();
            _service.SetContext(InputContext.Gameplay);
            SetKeys(Key.Space);
            _service.SetContext(InputContext.Pause);
            Assert.That(cancelled, Is.EqualTo(1));
            AssertEnabledMaps("Paused");
        }

        [Test]
        public async Task ReentrantCancelledCallbackReplacesPendingContextWithoutEnablingStaleMaps()
        {
            await InitializeWithKeyboard();
            _service.Subscribe("Game/CustomJump", _ => _service.SetContext(InputContext.Pause),
                InputActionPhase.Canceled);
            SetKeys(Key.Space);

            _service.SetContext(InputContext.UI);
            Assert.That(_service.CurrentContext, Is.EqualTo(InputContext.Pause));
            AssertEnabledMaps("Paused");
        }

        [Test]
        public async Task MissingActionsAndInvalidSubscriptionPhasesHaveExplicitErrors()
        {
            await Initialize();
            Assert.Throws<ArgumentException>(() => _service.GetAction("Game/Missing"));
            Assert.Throws<ArgumentException>(() => _service.GetAction(" "));
            Assert.Throws<ArgumentNullException>(() => _service.Subscribe("Game/CustomJump", null));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                _service.Subscribe("Game/CustomJump", _ => { }, InputActionPhase.Waiting));
        }

        [Test]
        public void CancelledInitializationAndDestroyedSourceNeverActivateRuntimeMaps()
        {
            _service = new InputService(_source, ContextMaps());
            Assert.ThrowsAsync<TaskCanceledException>(async () =>
                await _service.InitializeAsync(null, new CancellationToken(true)));
            Assert.That(_service.RuntimeAsset, Is.Null);
            UnityEngine.Object.DestroyImmediate(_source);
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await _service.InitializeAsync(null, CancellationToken.None));
            Assert.That(_service.RuntimeAsset, Is.Null);
        }

        [Test]
        public void RuntimeOperationsRequireAnInitializedService()
        {
            _service = new InputService(_source, ContextMaps());
            Assert.Throws<InvalidOperationException>(() => _service.GetAction("Game/CustomJump"));
            Assert.Throws<InvalidOperationException>(() => _service.SetContext(InputContext.UI));
            Assert.Throws<InvalidOperationException>(() => _service.Suspend());
        }

        private static Dictionary<InputContext, string[]> ContextMaps()
        {
            return new Dictionary<InputContext, string[]>
            {
                { InputContext.Gameplay, new[] { "Game" } },
                { InputContext.UI, new[] { "Menu" } },
                { InputContext.Pause, new[] { "Paused" } }
            };
        }

        private async Task Initialize()
        {
            _service = new InputService(_source, ContextMaps());
            await _service.InitializeAsync(null, CancellationToken.None);
        }

        private async Task InitializeWithKeyboard()
        {
            await Initialize();
            _keyboard = InputSystem.AddDevice<Keyboard>("LumiCoreInputTestKeyboard");
            _service.RuntimeAsset.devices = new InputDevice[] { _keyboard };
            SetKeys();
        }

        private static void SetKeysOn(Keyboard keyboard, params Key[] keys)
        {
            InputSystem.QueueStateEvent(keyboard, new KeyboardState(keys));
            InputSystem.Update();
        }

        private void SetKeys(params Key[] keys)
        {
            SetKeysOn(_keyboard, keys);
        }

        private void AssertEnabledMaps(params string[] expected)
        {
            var actual = new List<string>();
            foreach (InputActionMap map in _service.RuntimeAsset.actionMaps)
            {
                if (map.enabled)
                {
                    actual.Add(map.name);
                }
            }

            CollectionAssert.AreEquivalent(expected, actual);
        }
    }
}
