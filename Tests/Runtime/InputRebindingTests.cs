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
    /// <summary>Real native capture and action delivery; no Starter assets or user preference storage.</summary>
    public sealed class InputRebindingTests : InputTestFixture
    {
        private InputActionAsset source;
        private InputService input;
        private Keyboard keyboard;
        private Mouse mouse;
        private Gamepad gamepad;
        private Keyboard otherKeyboard;

        [SetUp]
        public async Task CreateInput()
        {
            source = ScriptableObject.CreateInstance<InputActionAsset>();
            var game = source.AddActionMap("Game");
            var jump = game.AddAction("Jump", InputActionType.Button, "<Keyboard>/space");
            jump.AddBinding("<Gamepad>/buttonSouth");
            jump.AddBinding("<Mouse>/leftButton");
            game.AddAction("Move", InputActionType.Value, expectedControlLayout: "Vector2")
                .AddCompositeBinding("2DVector").With("Up", "<Keyboard>/w")
                .With("Down", "<Keyboard>/s").With("Left", "<Keyboard>/a")
                .With("Right", "<Keyboard>/d");
            source.AddActionMap("Menu").AddAction("Accept", InputActionType.Button, "<Keyboard>/enter");
            keyboard = InputSystem.AddDevice<Keyboard>("Rebinding Keyboard");
            mouse = InputSystem.AddDevice<Mouse>("Rebinding Mouse");
            gamepad = InputSystem.AddDevice<Gamepad>("Rebinding Gamepad");
            otherKeyboard = InputSystem.AddDevice<Keyboard>("Unowned Rebinding Keyboard");
            input = CreateService();
            await input.InitializeAsync(null, CancellationToken.None);
            input.RuntimeAsset.devices = new InputDevice[] { keyboard, mouse, gamepad };
            InputSystem.Update();
        }

        [TearDown]
        public async Task CleanupInput()
        {
            if (input != null)
                await input.ShutdownAsync(CancellationToken.None);
            foreach (InputDevice device in new InputDevice[] { keyboard, mouse, gamepad, otherKeyboard })
            {
                if (device != null && device.added)
                    InputSystem.RemoveDevice(device);
            }
            if (source != null)
            {
                source.Disable();
                UnityEngine.Object.DestroyImmediate(source);
            }
        }

        [Test]
        public void KeyboardCaptureSuspendsMapsRestoresContextAndDeliversOnlyNewBinding()
        {
            var runtime = input.RuntimeAsset;
            InputRebindResult result = null;
            int bindingEvents = 0;
            input.BindingsChanged += () => bindingEvents++;
            var session = input.BeginRebind("Game/Jump", Binding("Game/Jump", 0), "<Keyboard>/*",
                value => result = value);
            Assert.That(input.IsRebinding, Is.True);
            Assert.That(input.IsSuspended, Is.True);
            Assert.That(runtime.enabled, Is.False);

            Keys(keyboard, Key.E);
            Assert.That(session.IsCompleted, Is.True);
            Assert.That(session.IsCanceled, Is.False);
            Assert.That(input.IsRebinding, Is.False);
            Assert.That(input.IsSuspended, Is.False);
            Assert.That(input.CurrentContext, Is.EqualTo(InputContext.Gameplay));
            Assert.That(runtime.FindActionMap("Game").enabled, Is.True);
            Assert.That(runtime.FindActionMap("Menu").enabled, Is.False);
            Assert.That(input.RuntimeAsset, Is.SameAs(runtime));
            Assert.That(result.ActionPath, Is.EqualTo("Game/Jump"));
            Assert.That(result.BindingId, Is.EqualTo(Binding("Game/Jump", 0)));
            Assert.That(result.OverridePath, Is.EqualTo("<Keyboard>/e"));
            Assert.That(bindingEvents, Is.EqualTo(1));
            Assert.That(source.FindAction("Game/Jump").bindings[0].overridePath, Is.Null);
            Assert.That(source.enabled, Is.False);

            int callbacks = 0;
            input.Subscribe("Game/Jump", _ => callbacks++);
            Keys(keyboard);
            Keys(keyboard, Key.Space);
            Assert.That(callbacks, Is.Zero);
            Keys(keyboard);
            Keys(keyboard, Key.E);
            Assert.That(callbacks, Is.EqualTo(1));
        }

        [Test]
        public void MouseCaptureUsesRealButtonAndPreservesOtherDeviceBindings()
        {
            var session = input.BeginRebind("Game/Jump", Binding("Game/Jump", 2), "<Mouse>/*");
            InputSystem.QueueStateEvent(mouse, new MouseState { position = new Vector2(40f, 70f), delta = new Vector2(40f, 70f) });
            InputSystem.Update();
            Assert.That(session.IsCompleted, Is.False, "Pointer movement must not replace a Button binding.");
            InputSystem.QueueStateEvent(mouse, new MouseState().WithButton(MouseButton.Right));
            InputSystem.Update();
            Assert.That(session.IsCompleted, Is.True);
            Assert.That(input.GetAction("Game/Jump").bindings[2].overridePath, Is.EqualTo("<Mouse>/rightButton"));
            Assert.That(input.GetAction("Game/Jump").bindings[0].overridePath, Is.Null);
            int callbacks = 0;
            input.Subscribe("Game/Jump", _ => callbacks++);
            InputSystem.QueueStateEvent(mouse, new MouseState());
            InputSystem.Update();
            InputSystem.QueueStateEvent(mouse, new MouseState().WithButton(MouseButton.Right));
            InputSystem.Update();
            Assert.That(callbacks, Is.EqualTo(1));
        }

        [Test]
        public void GamepadCaptureProducesPortableGamepadBindingAndRealCallbacks()
        {
            var session = input.BeginRebind("Game/Jump", Binding("Game/Jump", 1), "<Gamepad>/*");
            Pad(GamepadButton.West);
            Assert.That(session.IsCompleted, Is.True);
            Assert.That(input.GetAction("Game/Jump").bindings[1].overridePath, Is.EqualTo("<Gamepad>/buttonWest"));
            int callbacks = 0;
            input.Subscribe("Game/Jump", _ => callbacks++);
            Pad();
            Pad(GamepadButton.West);
            Assert.That(callbacks, Is.EqualTo(1));
        }

        [Test]
        public void CompositePartCapturesAButtonAndPreservesVectorAction()
        {
            var session = input.BeginRebind("Game/Move", Binding("Game/Move", 1), "<Keyboard>/*");
            Keys(keyboard, Key.E);
            Assert.That(session.IsCompleted, Is.True);
            Assert.That(session.IsCanceled, Is.False);
            Assert.That(input.GetAction("Game/Move").bindings[1].overridePath, Is.EqualTo("<Keyboard>/e"));
            Keys(keyboard);
            Keys(keyboard, Key.E);
            Assert.That(input.GetAction("Game/Move").ReadValue<Vector2>(), Is.EqualTo(Vector2.up));
            Assert.That(source.FindAction("Game/Move").bindings[1].path, Is.EqualTo("<Keyboard>/w"));
        }

        [Test]
        public void CompositeRootAndUnknownBindingAreRejectedBeforeAnySuspension()
        {
            Assert.Throws<ArgumentException>(() => input.BeginRebind("Game/Move",
                Binding("Game/Move", 0), "<Keyboard>/*"));
            Assert.Throws<ArgumentException>(() => input.BeginRebind("Game/Jump", Guid.NewGuid(), "<Keyboard>/*"));
            Assert.Throws<ArgumentException>(() => input.BeginRebind("Game/Jump", Guid.Empty, "<Keyboard>/*"));
            Assert.That(input.IsRebinding, Is.False);
            Assert.That(input.IsSuspended, Is.False);
            Assert.That(input.GetAction("Game/Jump").enabled, Is.True);
        }

        [TestCase(0f)]
        [TestCase(121f)]
        [TestCase(float.NaN)]
        public void InvalidTimeoutDoesNotDisableActions(float timeout)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => input.BeginRebind("Game/Jump",
                Binding("Game/Jump", 0), "<Keyboard>/*", timeoutSeconds: timeout));
            Assert.That(input.IsSuspended, Is.False);
        }

        [Test]
        public void WrongDeviceFamilyDoesNotCompleteKeyboardCapture()
        {
            var session = input.BeginRebind("Game/Jump", Binding("Game/Jump", 0), "<Keyboard>/*");
            Pad(GamepadButton.South);
            Assert.That(session.IsCompleted, Is.False);
            Pad();
            Keys(keyboard, Key.E);
            Assert.That(session.IsCompleted, Is.True);
            Assert.That(input.GetAction("Game/Jump").bindings[0].overridePath, Is.EqualTo("<Keyboard>/e"));
        }

        [Test]
        public void RuntimeAssetDeviceRestrictionAlsoAppliesToCapture()
        {
            var session = input.BeginRebind("Game/Jump", Binding("Game/Jump", 0), "<Keyboard>/*");
            Keys(otherKeyboard, Key.F);
            Assert.That(session.IsCompleted, Is.False);
            Keys(keyboard, Key.E);
            Assert.That(session.IsCompleted, Is.True);
            Assert.That(input.GetAction("Game/Jump").bindings[0].overridePath, Is.EqualTo("<Keyboard>/e"));
        }

        [Test]
        public void EscapeCancelsWithoutChangingExistingOverrideOrReportingBindingsChanged()
        {
            input.GetAction("Game/Jump").ApplyBindingOverride(0, "<Keyboard>/e");
            string previous = input.ExportBindingOverrides();
            int bindingsChanged = 0;
            InputRebindResult result = null;
            input.BindingsChanged += () => bindingsChanged++;
            var session = input.BeginRebind("Game/Jump", Binding("Game/Jump", 0), "<Keyboard>/*", r => result = r);
            Keys(keyboard, Key.Escape);
            Assert.That(session.IsCompleted, Is.True);
            Assert.That(session.IsCanceled, Is.True);
            Assert.That(result.IsCanceled, Is.True);
            Assert.That(input.ExportBindingOverrides(), Is.EqualTo(previous));
            Assert.That(bindingsChanged, Is.Zero);
            Assert.That(input.IsSuspended, Is.False);
        }

        [Test]
        public void GamepadCancelUsageCancelsEvenKeyboardCapture()
        {
            var session = input.BeginRebind("Game/Jump", Binding("Game/Jump", 0), "<Keyboard>/*");
            Pad(GamepadButton.East);
            Assert.That(session.IsCompleted, Is.True);
            Assert.That(session.IsCanceled, Is.True);
            Assert.That(input.GetAction("Game/Jump").bindings[0].overridePath, Is.Null);
            Assert.That(input.IsRebinding, Is.False);
        }

        [Test]
        public void CaptureTimeoutUsesNativeUnscaledInputTimeAndCleansUp()
        {
            var session = input.BeginRebind("Game/Jump", Binding("Game/Jump", 0), "<Keyboard>/*", timeoutSeconds: 0.2f);
            currentTime += 0.5;
            InputSystem.Update();
            Assert.That(session.IsCompleted, Is.True);
            Assert.That(session.IsCanceled, Is.True);
            Assert.That(input.IsSuspended, Is.False);
            Assert.That(input.ExportBindingOverrides(), Is.Empty);
        }

        [Test]
        public void DisposalIsIdempotentAndContextChangesRestoreTheDesiredContext()
        {
            var session = input.BeginRebind("Game/Jump", Binding("Game/Jump", 0), "<Keyboard>/*");
            input.SetContext(InputContext.Pause);
            InputSystem.Update();
            session.Dispose();
            session.Cancel();
            Assert.That(session.IsCompleted, Is.True);
            Assert.That(session.IsCanceled, Is.True);
            Assert.That(input.CurrentContext, Is.EqualTo(InputContext.Pause));
            Assert.That(input.RuntimeAsset.FindActionMap("Menu").enabled, Is.True);
            Assert.That(input.RuntimeAsset.FindActionMap("Game").enabled, Is.False);
        }

        [Test]
        public void CapturesDoNotReleaseAnExternalSuspensionLease()
        {
            IDisposable outer = input.Suspend();
            var session = input.BeginRebind("Game/Jump", Binding("Game/Jump", 0), "<Keyboard>/*");
            Keys(keyboard, Key.E);
            Assert.That(session.IsCompleted, Is.True);
            Assert.That(input.IsSuspended, Is.True);
            Assert.That(input.RuntimeAsset.enabled, Is.False);
            outer.Dispose();
            Assert.That(input.IsSuspended, Is.False);
            Assert.That(input.GetAction("Game/Jump").enabled, Is.True);
        }

        [Test]
        public async Task ShutdownCancelsAndReleasesResourcesBeforeDestroyingTheClone()
        {
            int finished = 0;
            var session = input.BeginRebind("Game/Jump", Binding("Game/Jump", 0), "<Keyboard>/*",
                r => { Assert.That(r.IsCanceled, Is.True); finished++; });
            await input.ShutdownAsync(new CancellationToken(true));
            Assert.That(session.IsCompleted, Is.True);
            Assert.That(session.IsCanceled, Is.True);
            Assert.That(input.IsRebinding, Is.False);
            Assert.That(input.RuntimeAsset, Is.Null);
            Assert.That(finished, Is.EqualTo(1));
            Keys(keyboard, Key.E);
            Assert.That(finished, Is.EqualTo(1));
        }

        [Test]
        public async Task PreviousSessionCannotCancelANewServiceLifetime()
        {
            var old = input.BeginRebind("Game/Jump", Binding("Game/Jump", 0), "<Keyboard>/*");
            await input.ShutdownAsync(CancellationToken.None);
            await input.InitializeAsync(null, CancellationToken.None);
            input.RuntimeAsset.devices = new InputDevice[] { keyboard, mouse, gamepad };
            var current = input.BeginRebind("Game/Jump", Binding("Game/Jump", 0), "<Keyboard>/*");
            old.Dispose();
            Assert.That(current.IsCompleted, Is.False);
            Keys(keyboard, Key.E);
            Assert.That(current.IsCompleted, Is.True);
            Assert.That(current.IsCanceled, Is.False);
        }

        [Test]
        public void CaptureAndOverrideMutationsAreExclusiveWithinTheService()
        {
            var session = input.BeginRebind("Game/Jump", Binding("Game/Jump", 0), "<Keyboard>/*");
            Assert.Throws<InvalidOperationException>(() => input.BeginRebind("Game/Jump",
                Binding("Game/Jump", 1), "<Gamepad>/*"));
            Assert.Throws<InvalidOperationException>(() => input.ImportBindingOverrides(string.Empty));
            Assert.Throws<InvalidOperationException>(() => input.ResetBindingOverrides());
            Assert.Throws<InvalidOperationException>(() => input.ExportBindingOverrides());
            session.Dispose();
            InputSystem.Update();
            Assert.DoesNotThrow(() => input.ImportBindingOverrides(string.Empty));
        }

        [Test]
        public async Task ExportedOverridesImportIntoANewInstanceWithTheSameBindingIds()
        {
            input.BeginRebind("Game/Jump", Binding("Game/Jump", 0), "<Keyboard>/*");
            Keys(keyboard, Key.E);
            string document = input.ExportBindingOverrides();
            Assert.That(document, Does.Contain("<Keyboard>/e"));
            await input.ShutdownAsync(CancellationToken.None);
            input = CreateService();
            await input.InitializeAsync(null, CancellationToken.None);
            input.RuntimeAsset.devices = new InputDevice[] { keyboard, mouse, gamepad };
            int changed = 0;
            input.BindingsChanged += () => changed++;
            input.ImportBindingOverrides(document);
            Assert.That(input.GetAction("Game/Jump").bindings[0].overridePath, Is.EqualTo("<Keyboard>/e"));
            Assert.That(changed, Is.EqualTo(1));
            Assert.That(source.FindAction("Game/Jump").bindings[0].overridePath, Is.Null);
        }

        [Test]
        public void ExplicitResetRestoresDefaultsAndDoesNotChangeSourceConfiguration()
        {
            input.GetAction("Game/Jump").ApplyBindingOverride(0, "<Keyboard>/e");
            int changed = 0;
            input.BindingsChanged += () => changed++;
            input.ResetBindingOverrides();
            Assert.That(input.ExportBindingOverrides(), Is.Empty);
            Assert.That(input.GetAction("Game/Jump").bindings[0].effectivePath, Is.EqualTo("<Keyboard>/space"));
            Assert.That(changed, Is.EqualTo(1));
            Assert.That(input.GetAction("Game/Jump").enabled, Is.True);
            Assert.That(source.FindAction("Game/Jump").bindings[0].path, Is.EqualTo("<Keyboard>/space"));
        }

        [TestCase("{")]
        [TestCase("[]")]
        [TestCase("{\"bindings\":null}")]
        [TestCase("{\"bindings\":[],\"future\":2}")]
        [TestCase("{\"bindings\":[],\"bindings\":[]}")]
        [TestCase("{\"bindings\":[]} trailing")]
        [TestCase("{\"bindings\":[{}]}")]
        [TestCase("{\"bindings\":[{\"id\":123}]}")]
        public void MalformedDocumentsPreserveExistingOverridesAndActiveMaps(string document)
        {
            input.GetAction("Game/Jump").ApplyBindingOverride(0, "<Keyboard>/e");
            string before = input.ExportBindingOverrides();
            Assert.Throws<ArgumentException>(() => input.ImportBindingOverrides(document));
            Assert.That(input.ExportBindingOverrides(), Is.EqualTo(before));
            Assert.That(input.GetAction("Game/Jump").enabled, Is.True);
            Assert.That(input.IsSuspended, Is.False);
        }

        [Test]
        public void UnknownBindingIdsAndMismatchedActionPathsAreRejectedWithoutMutation()
        {
            input.GetAction("Game/Jump").ApplyBindingOverride(0, "<Keyboard>/e");
            string document = input.ExportBindingOverrides();
            string unknownId = document.Replace(Binding("Game/Jump", 0).ToString(), Guid.NewGuid().ToString());
            Assert.Throws<ArgumentException>(() => input.ImportBindingOverrides(unknownId));
            string wrongAction = document.Replace("Game/Jump", "Menu/Accept");
            Assert.Throws<ArgumentException>(() => input.ImportBindingOverrides(wrongAction));
            Assert.That(input.ExportBindingOverrides(), Is.EqualTo(document));
        }

        [Test]
        public void DuplicateBindingIdsAndOversizedDocumentsAreRejectedBeforeNativeLoading()
        {
            string entry = Entry(Binding("Game/Jump", 0), "Game/Jump", "<Keyboard>/e");
            Assert.Throws<ArgumentException>(() => input.ImportBindingOverrides("{\"bindings\":[" + entry + "," + entry + "]}"));
            Assert.Throws<ArgumentException>(() => input.ImportBindingOverrides(new string(' ', 256 * 1024 + 1)));
            Assert.Throws<ArgumentNullException>(() => input.ImportBindingOverrides(null));
            Assert.That(input.ExportBindingOverrides(), Is.Empty);
        }

        [Test]
        public void NonStringPathsUnknownFieldsAndCustomProcessorOverridesAreRejected()
        {
            string valid = "{\"bindings\":[" + Entry(Binding("Game/Jump", 0), "Game/Jump", "<Keyboard>/e") + "]}";
            Assert.Throws<ArgumentException>(() => input.ImportBindingOverrides(valid.Replace("\"<Keyboard>/e\"", "12")));
            Assert.Throws<ArgumentException>(() => input.ImportBindingOverrides(valid.Replace("\"processors\":\"null\"", "\"processors\":\"invert\"")));
            Assert.Throws<ArgumentException>(() => input.ImportBindingOverrides(valid.Replace("\"interactions\":\"null\"", "\"interactions\":\"hold\"")));
            Assert.Throws<ArgumentException>(() => input.ImportBindingOverrides(valid.Replace("\"path\":", "\"unexpected\":")));
            Assert.That(input.ExportBindingOverrides(), Is.Empty);
        }

        [Test]
        public void EscapedNativeJsonStringsAreDecodedBeforeIdentityValidation()
        {
            string valid = "{\"bindings\":[" + Entry(Binding("Game/Jump", 0), "Game/Jump", "<Keyboard>/e") + "]}";
            input.ImportBindingOverrides(valid.Replace("Game/Jump", "Game\\/J\\u0075mp"));
            Assert.That(input.GetAction("Game/Jump").bindings[0].overridePath, Is.EqualTo("<Keyboard>/e"));
        }

        [Test]
        public void FaultyBindingAndCompletionObserversAreIsolatedAfterNativeCleanup()
        {
            int healthy = 0;
            int failures = 0;
            input.ObserverFailed += _ => failures++;
            input.BindingsChanged += () => throw new InvalidOperationException("test binding observer");
            input.BindingsChanged += () => healthy++;
            var session = input.BeginRebind("Game/Jump", Binding("Game/Jump", 0), "<Keyboard>/*",
                _ => throw new InvalidOperationException("test completion observer"));
            Keys(keyboard, Key.E);
            Assert.That(session.IsCompleted, Is.True);
            Assert.That(input.IsRebinding, Is.False);
            Assert.That(input.IsSuspended, Is.False);
            Assert.That(healthy, Is.EqualTo(1));
            Assert.That(failures, Is.EqualTo(2));
        }

        [Test]
        public void CompletionCallbackCanBeginAnotherCaptureAfterNativePolicyWasRestored()
        {
            InputRebindSession next = null;
            input.BeginRebind("Game/Jump", Binding("Game/Jump", 0), "<Keyboard>/*",
                _ => next = input.BeginRebind("Game/Jump", Binding("Game/Jump", 1), "<Gamepad>/*"));
            Keys(keyboard, Key.E);
            Assert.That(next, Is.Not.Null);
            Assert.That(input.IsRebinding, Is.True);
            Pad(GamepadButton.West);
            Assert.That(next.IsCompleted, Is.True);
            Assert.That(next.IsCanceled, Is.False);
            Assert.That(input.IsSuspended, Is.False);
        }

        [Test]
        public void ReentrantContextChangeDuringInitialSuspensionCancelsBeforeStartingCapture()
        {
            input.StateChanged += () =>
            {
                if (input.IsSuspended && input.CurrentContext == InputContext.Gameplay)
                    input.SetContext(InputContext.UI);
            };
            var session = input.BeginRebind("Game/Jump", Binding("Game/Jump", 0), "<Keyboard>/*");
            Assert.That(session.IsCompleted, Is.True);
            Assert.That(session.IsCanceled, Is.True);
            Assert.That(input.IsRebinding, Is.False);
            Assert.That(input.CurrentContext, Is.EqualTo(InputContext.UI));
            Assert.That(input.IsSuspended, Is.False);
        }

        [Test]
        public void ReentrantShutdownDuringInitialSuspensionDoesNotStartOrLeakNativeCapture()
        {
            Task stopped = null;
            input.StateChanged += () =>
            {
                if (input.IsInitialized && input.IsSuspended)
                    stopped = input.ShutdownAsync(CancellationToken.None);
            };
            var session = input.BeginRebind("Game/Jump", Binding("Game/Jump", 0), "<Keyboard>/*");
            Assert.That(stopped, Is.Not.Null);
            Assert.That(stopped.IsCompleted, Is.True);
            Assert.That(session.IsCompleted, Is.True);
            Assert.That(session.IsCanceled, Is.True);
            Assert.That(input.IsRebinding, Is.False);
            Assert.That(input.IsInitialized, Is.False);
            Keys(keyboard, Key.E);
            Assert.That(source.FindAction("Game/Jump").bindings[0].overridePath, Is.Null);
        }

        [Test]
        public void NativeValidationObserversCannotStartCaptureOrAnotherImportDuringPreparation()
        {
            Guid bindingId = Binding("Game/Jump", 0);
            int attempts = 0;
            int rejected = 0;
            Action<object, InputActionChange> observer = (changed, change) =>
            {
                if (change != InputActionChange.BoundControlsChanged ||
                    !(changed is InputActionAsset asset) ||
                    !asset.name.EndsWith(" (binding validation)", StringComparison.Ordinal)) return;
                attempts += 2;
                try
                {
                    input.BeginRebind("Game/Jump", bindingId, "<Keyboard>/*");
                }
                catch (InvalidOperationException) { rejected++; }
                try
                {
                    input.ImportBindingOverrides(string.Empty);
                }
                catch (InvalidOperationException) { rejected++; }
            };
            InputSystem.onActionChange += observer;
            try
            {
                string document = "{\"bindings\":[" + Entry(bindingId, "Game/Jump", "<Keyboard>/e") + "]}";
                input.ImportBindingOverrides(document);
            }
            finally
            {
                InputSystem.onActionChange -= observer;
            }
            Assert.That(attempts, Is.GreaterThan(0), "Native resolution must actually invoke the observer.");
            Assert.That(rejected, Is.EqualTo(attempts));
            Assert.That(input.IsRebinding, Is.False);
            Assert.That(input.IsSuspended, Is.False);
            Assert.That(input.GetAction("Game/Jump").bindings[0].effectivePath, Is.EqualTo("<Keyboard>/e"));
            Assert.That(source.FindAction("Game/Jump").bindings[0].overridePath, Is.Null);
        }

        private InputService CreateService() => new InputService(source,
            new Dictionary<InputContext, string[]>
            {
                [InputContext.Gameplay] = new[] { "Game" },
                [InputContext.UI] = new[] { "Menu" },
                [InputContext.Pause] = new[] { "Menu" }
            });

        private Guid Binding(string actionPath, int index) => input.GetAction(actionPath).bindings[index].id;

        private static void Keys(Keyboard device, params Key[] keys)
        {
            InputSystem.QueueStateEvent(device, new KeyboardState(keys));
            InputSystem.Update();
        }

        private void Pad(params GamepadButton[] buttons)
        {
            var state = new GamepadState();
            foreach (GamepadButton button in buttons)
                state = state.WithButton(button);
            InputSystem.QueueStateEvent(gamepad, state);
            InputSystem.Update();
        }

        private static string Entry(Guid id, string action, string path) =>
            "{\"action\":\"" + action + "\",\"id\":\"" + id + "\",\"path\":\"" + path +
            "\",\"interactions\":\"null\",\"processors\":\"null\"}";
    }
}
