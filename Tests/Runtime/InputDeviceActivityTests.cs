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
    /// <summary>Contextual device detection through native events, independent of Starter assets.</summary>
    public sealed class InputDeviceActivityTests : InputTestFixture
    {
        private InputActionAsset source;
        private InputService input;
        private Keyboard keyboard;
        private Mouse mouse;
        private Gamepad gamepad;

        [SetUp]
        public async Task CreateInput()
        {
            source = ScriptableObject.CreateInstance<InputActionAsset>();
            source.AddActionMap("Game").AddAction("Jump", InputActionType.Button, "<Keyboard>/space")
                .AddBinding("<Gamepad>/buttonSouth");
            source.AddActionMap("Menu").AddAction("Accept", InputActionType.Button, "<Keyboard>/enter");
            keyboard = InputSystem.AddDevice<Keyboard>();
            mouse = InputSystem.AddDevice<Mouse>();
            gamepad = InputSystem.AddDevice<Gamepad>();
            input = new InputService(source, new Dictionary<InputContext, string[]>
            {
                [InputContext.Gameplay] = new[] { "Game" },
                [InputContext.UI] = new[] { "Menu" },
                [InputContext.Pause] = new[] { "Menu" }
            });
            await input.InitializeAsync(null, CancellationToken.None);
            RestrictDevices();
            InputSystem.Update();
        }

        [TearDown]
        public async Task CleanupInput()
        {
            if (input != null)
                await input.ShutdownAsync(CancellationToken.None);
            if (source != null)
                Object.DestroyImmediate(source);
        }

        [Test]
        public void ActualButtonsSticksAndUnboundKeysSwitchOnlyWhenTheFamilyChanges()
        {
            var observed = new List<InputDeviceFamily>();
            input.ActiveDeviceChanged += observed.Add;
            Assert.That(input.ActiveDeviceFamily, Is.EqualTo(InputDeviceFamily.KeyboardMouse));

            Pad(new GamepadState().WithButton(GamepadButton.South));
            Pad(new GamepadState()); // A release is not a new interaction.
            Pointer(new MouseState().WithButton(MouseButton.Left));
            Pointer(new MouseState());
            Keys(Key.Space); // Same family: no duplicate notification.
            Pad(new GamepadState { leftStick = new Vector2(0.8f, 0f) });
            Keys(Key.Z); // Intentionally absent from all configured action maps.

            Assert.That(observed, Is.EqualTo(new[]
            {
                InputDeviceFamily.Gamepad, InputDeviceFamily.KeyboardMouse,
                InputDeviceFamily.Gamepad, InputDeviceFamily.KeyboardMouse
            }));
            Assert.That(source.enabled, Is.False);
        }

        [Test]
        public void DriftHeldControlsAndSyntheticPointerPositionCannotReclaimTheFamily()
        {
            var observed = new List<InputDeviceFamily>();
            input.ActiveDeviceChanged += observed.Add;
            Pad(new GamepadState { leftStick = new Vector2(0.8f, 0f), leftTrigger = 0.9f });
            Keys(Key.Z);
            Keys();
            Pad(new GamepadState { leftStick = new Vector2(0.82f, 0f), leftTrigger = 0.91f });
            Assert.That(input.ActiveDeviceFamily, Is.EqualTo(InputDeviceFamily.KeyboardMouse));

            Pad(new GamepadState { leftStick = new Vector2(0.82f, 0f), leftTrigger = 0.91f }
                .WithButton(GamepadButton.South));
            Pointer(new MouseState { position = new Vector2(1000f, 500f), delta = new Vector2(0.25f, 0f) });
            InputSystem.QueueDeltaStateEvent(mouse.position, new Vector2(20f, 80f));
            InputSystem.Update();
            Assert.That(input.ActiveDeviceFamily, Is.EqualTo(InputDeviceFamily.Gamepad));

            Pointer(new MouseState { position = new Vector2(24f, 80f), delta = new Vector2(4f, 0f) });
            Pad(new GamepadState { leftStick = new Vector2(0.1f, 0f) });
            Assert.That(input.ActiveDeviceFamily, Is.EqualTo(InputDeviceFamily.KeyboardMouse));
            Assert.That(observed.Count, Is.EqualTo(4));

            // Real gradual motion must accumulate relative to the last keyboard/mouse switch,
            // rather than requiring a large jump between consecutive hardware samples.
            for (int i = 11; i <= 60; i++)
                Pad(new GamepadState { leftStick = new Vector2(i * 0.01f, 0f) });
            Assert.That(input.ActiveDeviceFamily, Is.EqualTo(InputDeviceFamily.Gamepad));
            Assert.That(observed.Count, Is.EqualTo(5));
            InputSystem.QueueStateEvent(mouse, new MouseState { delta = new Vector2(1f, 0f) });
            InputSystem.QueueStateEvent(mouse, new MouseState { delta = new Vector2(1f, 0f) });
            InputSystem.Update();
            Assert.That(input.ActiveDeviceFamily, Is.EqualTo(InputDeviceFamily.KeyboardMouse));
            Assert.That(observed.Count, Is.EqualTo(6));
        }

        [Test]
        public void DeviceRestrictionAndNativeRebindingKeepTheirExistingBehavior()
        {
            var unownedGamepad = InputSystem.AddDevice<Gamepad>();
            InputSystem.QueueStateEvent(unownedGamepad, new GamepadState().WithButton(GamepadButton.South));
            InputSystem.Update();
            Assert.That(input.ActiveDeviceFamily, Is.EqualTo(InputDeviceFamily.KeyboardMouse));

            var action = input.GetAction("Game/Jump");
            var session = input.BeginRebind("Game/Jump", action.bindings[0].id, "<Keyboard>/*");
            Pad(new GamepadState().WithButton(GamepadButton.West));
            Assert.That(input.ActiveDeviceFamily, Is.EqualTo(InputDeviceFamily.Gamepad));
            Assert.That(session.IsCompleted, Is.False);
            Keys(Key.E);

            Assert.That(input.ActiveDeviceFamily, Is.EqualTo(InputDeviceFamily.KeyboardMouse));
            Assert.That(session.IsCompleted, Is.True);
            Assert.That(session.IsCanceled, Is.False);
            Assert.That(action.bindings[0].overridePath, Is.EqualTo("<Keyboard>/e"));
            Assert.That(input.IsSuspended, Is.False);
            Assert.That(action.enabled, Is.True);
        }

        [Test]
        public async Task ShutdownRemovesNativeHooksAndReinitializationStartsWithFreshObservers()
        {
            int oldNotifications = 0;
            input.ActiveDeviceChanged += _ => oldNotifications++;
            Pad(new GamepadState().WithButton(GamepadButton.South));
            await input.ShutdownAsync(CancellationToken.None);
            Keys(Key.Z);
            Assert.That(oldNotifications, Is.EqualTo(1));
            Assert.That(input.ActiveDeviceFamily, Is.EqualTo(InputDeviceFamily.Gamepad));

            await input.InitializeAsync(null, CancellationToken.None);
            RestrictDevices();
            Assert.That(input.ActiveDeviceFamily, Is.EqualTo(InputDeviceFamily.KeyboardMouse));
            int newNotifications = 0;
            input.ActiveDeviceChanged += _ => newNotifications++;
            Pad(new GamepadState());
            Pad(new GamepadState().WithButton(GamepadButton.South));
            Assert.That(newNotifications, Is.EqualTo(1));
            Assert.That(oldNotifications, Is.EqualTo(1));
        }

        private void RestrictDevices() => input.RuntimeAsset.devices = new InputDevice[] { keyboard, mouse, gamepad };

        private void Pad(GamepadState state)
        {
            InputSystem.QueueStateEvent(gamepad, state);
            InputSystem.Update();
        }

        private void Pointer(MouseState state)
        {
            InputSystem.QueueStateEvent(mouse, state);
            InputSystem.Update();
        }

        private void Keys(params Key[] keys)
        {
            InputSystem.QueueStateEvent(keyboard, new KeyboardState(keys));
            InputSystem.Update();
        }
    }
}
