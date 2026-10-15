using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Lumivale.Core.Bootstrap;
using Lumivale.Core.Input;
using Lumivale.Core.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.InputSystem.UI;

namespace Lumivale.Tests.PlayMode
{
    public sealed class UiServiceTests : InputTestFixture
    {
        private readonly List<GameObject> objects = new List<GameObject>();
        private InputActionAsset source;
        private InputService input;
        private UiService ui;
        private PauseService pause;
        private BootstrapCoordinator coordinator;
        private EventSystem eventSystem;
        private float originalTimeScale;

        [SetUp]
        public async Task SetUp()
        {
            originalTimeScale = Time.timeScale;
            source = ScriptableObject.CreateInstance<InputActionAsset>();
            InputActionMap gameplay = source.AddActionMap("Gameplay");
            gameplay.AddAction("Pause", InputActionType.Button, "<Keyboard>/escape");
            InputActionMap uiMap = source.AddActionMap("UI");
            uiMap.AddAction("Navigate", InputActionType.PassThrough, expectedControlLayout: "Vector2");
            uiMap.AddAction("Point", InputActionType.PassThrough, "<Mouse>/position", expectedControlLayout: "Vector2");
            uiMap.AddAction("Click", InputActionType.PassThrough, "<Mouse>/leftButton", expectedControlLayout: "Button");
            uiMap.AddAction("Submit", InputActionType.Button);
            uiMap.AddAction("Cancel", InputActionType.Button);
            uiMap.AddAction("ScrollWheel", InputActionType.PassThrough, expectedControlLayout: "Vector2");

            input = new InputService(source, new Dictionary<InputContext, string[]>
            {
                { InputContext.Gameplay, new[] { "Gameplay" } },
                { InputContext.UI, new[] { "UI" } },
                { InputContext.Pause, new[] { "UI" } }
            });
            ui = new UiService();
            pause = new PauseService("Pause", true);
            coordinator = new BootstrapCoordinator(new ILumiService[] { pause, ui, input });
            await coordinator.InitializeAsync();

            GameObject systemObject = NewObject("UiTestEventSystem", typeof(EventSystem));
            eventSystem = systemObject.GetComponent<EventSystem>();
            EventSystem.current = eventSystem;
        }

        [TearDown]
        public async Task CleanupUi()
        {
            if (coordinator != null)
            {
                await coordinator.ShutdownAsync();
            }

            for (int index = objects.Count - 1; index >= 0; index--)
            {
                if (objects[index] != null)
                {
                    UnityEngine.Object.DestroyImmediate(objects[index]);
                }
            }

            objects.Clear();
            if (source != null)
            {
                UnityEngine.Object.DestroyImmediate(source);
            }

            Time.timeScale = originalTimeScale;
        }

        [Test]
        public void RegistrationRejectsMissingAndDuplicateScreenIds()
        {
            UiScreen first = Screen("Menu");
            IDisposable token = ui.Register(first);
            Assert.That(first.CanvasGroup.alpha, Is.Zero);
            Assert.That(first.CanvasGroup.interactable, Is.False);
            Assert.Throws<InvalidOperationException>(() => ui.Register(Screen("Menu")));
            Assert.Throws<KeyNotFoundException>(() => ui.Open("Missing"));
            token.Dispose();
            token.Dispose();
            Assert.Throws<KeyNotFoundException>(() => ui.GetScreen("Menu"));
            Assert.DoesNotThrow(() => ui.Register(Screen("Menu")));
        }

        [Test]
        public void RegisteredScreenCannotBeReconfiguredOrSharedBetweenServices()
        {
            UiScreen first = Screen("Menu");
            ui.Register(first);
            Assert.Throws<InvalidOperationException>(() =>
                first.Configure("Changed", first.CanvasGroup));
            Assert.Throws<ArgumentException>(() =>
                Screen("Other").Configure("Other", first.CanvasGroup));
        }

        [Test]
        public void ScreenHistoryAndBackRestorePriorSelection()
        {
            UiScreen menu = RegisteredScreen("Menu");
            UiScreen settings = RegisteredScreen("Options");
            UnityEngine.UI.Button other = AddButton(menu, "SecondButton");
            ui.Open("Menu");
            Assert.That(eventSystem.currentSelectedGameObject,
                Is.EqualTo(menu.FirstSelected.gameObject));
            eventSystem.SetSelectedGameObject(other.gameObject);
            ui.Open("Options");
            Assert.That(menu.CanvasGroup.alpha, Is.Zero);
            Assert.That(settings.CanvasGroup.interactable, Is.True);
            ui.Back();
            Assert.That(ui.CurrentScreenId, Is.EqualTo("Menu"));
            Assert.That(eventSystem.currentSelectedGameObject, Is.EqualTo(other.gameObject));
            Assert.That(settings.CanvasGroup.alpha, Is.Zero);
            ui.Back();
            Assert.That(ui.CurrentScreenId, Is.EqualTo("Menu"));
        }

        [Test]
        public void ReopeningCurrentScreenDoesNotDuplicateHistory()
        {
            RegisteredScreen("Menu");
            RegisteredScreen("Options");
            ui.Open("Menu");
            ui.Open("Options");
            ui.Open("Options");
            ui.Back();
            Assert.That(ui.CurrentScreenId, Is.EqualTo("Menu"));
            ui.Back();
            Assert.That(ui.CurrentScreenId, Is.EqualTo("Menu"));
        }

        [Test]
        public void NavigationCanExplicitlySkipHistory()
        {
            RegisteredScreen("Menu");
            RegisteredScreen("Options");
            ui.Open("Menu");
            ui.Open("Options", rememberHistory: false);
            ui.Back();
            Assert.That(ui.CurrentScreenId, Is.EqualTo("Options"));
            ui.Close("Options");
            Assert.That(ui.CurrentScreenId, Is.Null);
            Assert.That(eventSystem.currentSelectedGameObject, Is.Null);
        }

        [Test]
        public void StackedModalsBlockUnderlyingScreensAndRestoreTheirFocus()
        {
            UiScreen menu = RegisteredScreen("Menu");
            UiScreen modal = RegisteredScreen("Confirm");
            UiScreen nested = RegisteredScreen("Nested");
            UnityEngine.UI.Button second = AddButton(menu, "SecondButton");
            ui.Open("Menu");
            eventSystem.SetSelectedGameObject(second.gameObject);
            ui.OpenModal("Confirm");
            Assert.That(menu.CanvasGroup.alpha, Is.EqualTo(1f));
            Assert.That(menu.CanvasGroup.interactable, Is.False);
            Assert.That(menu.CanvasGroup.blocksRaycasts, Is.False);
            Assert.That(eventSystem.currentSelectedGameObject, Is.EqualTo(modal.FirstSelected.gameObject));
            ui.OpenModal("Nested");
            Assert.That(modal.CanvasGroup.interactable, Is.False);
            Assert.That(nested.CanvasGroup.interactable, Is.True);
            Assert.Throws<InvalidOperationException>(() => ui.OpenModal("Confirm"));
            ui.Back();
            Assert.That(ui.ModalCount, Is.EqualTo(1));
            Assert.That(eventSystem.currentSelectedGameObject, Is.EqualTo(modal.FirstSelected.gameObject));
            ui.CloseModal();
            Assert.That(menu.CanvasGroup.interactable, Is.True);
            Assert.That(eventSystem.currentSelectedGameObject, Is.EqualTo(second.gameObject));
        }

        [Test]
        public void ClosingLowerModalClosesChildrenInReverseOrder()
        {
            RegisteredScreen("Menu");
            RegisteredScreen("Confirm");
            RegisteredScreen("Nested");
            var closed = new List<string>();
            ui.ScreenClosed += closed.Add;
            ui.Open("Menu");
            ui.OpenModal("Confirm");
            ui.OpenModal("Nested");
            ui.Close("Confirm");
            CollectionAssert.AreEqual(new[] { "Nested", "Confirm" }, closed);
            Assert.That(ui.ModalCount, Is.Zero);
            Assert.That(ui.CurrentScreenId, Is.EqualTo("Menu"));
        }

        [Test]
        public void ClosingAllClearsHistoryAndSelection()
        {
            RegisteredScreen("Menu");
            RegisteredScreen("Options");
            RegisteredScreen("Confirm");
            ui.Open("Menu");
            ui.Open("Options");
            ui.OpenModal("Confirm");
            ui.CloseAll();
            ui.Back();
            Assert.That(ui.CurrentScreenId, Is.Null);
            Assert.That(ui.ModalCount, Is.Zero);
            Assert.That(eventSystem.currentSelectedGameObject, Is.Null);
        }

        [Test]
        public void DestroyedScreensUnregisterAndHistorySkipsThem()
        {
            UiScreen menu = RegisteredScreen("Menu");
            UiScreen options = RegisteredScreen("Options");
            RegisteredScreen("Last");
            ui.Open("Menu");
            ui.Open("Options");
            ui.Open("Last");
            UnityEngine.Object.DestroyImmediate(options.gameObject);
            ui.Back();
            Assert.That(ui.CurrentScreenId, Is.EqualTo("Menu"));
            UnityEngine.Object.DestroyImmediate(menu.gameObject);
            Assert.That(ui.CurrentScreenId, Is.Null);
            Assert.Throws<KeyNotFoundException>(() => ui.GetScreen("Menu"));
        }

        [Test]
        public void UnregisteringScreenClosesItAndAllowsFreshRegistration()
        {
            RegisteredScreen("Menu");
            UiScreen modal = Screen("Confirm");
            IDisposable token = ui.Register(modal);
            ui.Open("Menu");
            ui.OpenModal("Confirm");
            token.Dispose();
            Assert.That(ui.IsOpen("Confirm"), Is.False);
            Assert.That(ui.ModalCount, Is.Zero);
            Assert.That(modal.CanvasGroup.alpha, Is.Zero);
            Assert.That(ui.GetScreen("Menu").CanvasGroup.interactable, Is.True);
            ui.Register(modal);
            token.Dispose();
            Assert.That(ui.GetScreen("Confirm"), Is.SameAs(modal));
        }

        [Test]
        public void PauseRestoresContextAndExactPreviousTimeScale()
        {
            RegisteredScreen("Game");
            RegisteredScreen("Pause");
            RegisteredScreen("Confirm");
            Time.timeScale = 0.35f;
            ui.Open("Game");
            pause.Pause();
            pause.Pause();
            Assert.That(pause.IsPaused, Is.True);
            Assert.That(input.CurrentContext, Is.EqualTo(InputContext.Pause));
            Assert.That(Time.timeScale, Is.Zero);
            Assert.That(ui.ModalCount, Is.EqualTo(1));
            ui.OpenModal("Confirm");
            pause.Resume();
            pause.Resume();
            Assert.That(pause.IsPaused, Is.False);
            Assert.That(input.CurrentContext, Is.EqualTo(InputContext.Gameplay));
            Assert.That(Time.timeScale, Is.EqualTo(0.35f));
            Assert.That(ui.ModalCount, Is.Zero);
        }

        [Test]
        public void ClosingOrDestroyingPauseScreenRestoresState()
        {
            UiScreen pauseScreen = RegisteredScreen("Pause");
            Time.timeScale = 0.6f;
            pause.Pause();
            ui.CloseModal();
            Assert.That(pause.IsPaused, Is.False);
            Assert.That(Time.timeScale, Is.EqualTo(0.6f));
            pause.Pause();
            UnityEngine.Object.DestroyImmediate(pauseScreen.gameObject);
            Assert.That(pause.IsPaused, Is.False);
            Assert.That(input.CurrentContext, Is.EqualTo(InputContext.Gameplay));
            Assert.That(Time.timeScale, Is.EqualTo(0.6f));
        }

        [Test]
        public void MissingPauseScreenDoesNotChangeInputOrTime()
        {
            Time.timeScale = 0.45f;
            Assert.Throws<KeyNotFoundException>(() => pause.Pause());
            Assert.That(input.CurrentContext, Is.EqualTo(InputContext.Gameplay));
            Assert.That(Time.timeScale, Is.EqualTo(0.45f));
            Assert.That(pause.IsPaused, Is.False);
        }

        [Test]
        public void ResumingFromInputStateObserverCannotLeaveTimeFrozen()
        {
            RegisteredScreen("Pause");
            Time.timeScale = 0.4f;
            input.StateChanged += () =>
            {
                if (input.CurrentContext == InputContext.Pause)
                {
                    pause.Resume();
                }
            };
            pause.Pause();
            Assert.That(pause.IsPaused, Is.False);
            Assert.That(Time.timeScale, Is.EqualTo(0.4f));
            Assert.That(input.CurrentContext, Is.EqualTo(InputContext.Gameplay));
            Assert.That(ui.IsOpen("Pause"), Is.False);
        }

        [Test]
        public void PauseRequestedWhileRestoringCannotOverwriteTheRestoredSnapshot()
        {
            RegisteredScreen("Pause");
            Time.timeScale = 0.4f;
            ui.ScreenClosed += id =>
            {
                if (id == "Pause")
                {
                    pause.Pause();
                }
            };
            pause.Pause();
            pause.Resume();
            Assert.That(pause.IsPaused, Is.False);
            Assert.That(Time.timeScale, Is.EqualTo(0.4f));
            Assert.That(input.CurrentContext, Is.EqualTo(InputContext.Gameplay));
            Assert.That(ui.IsOpen("Pause"), Is.False);
        }

        [Test]
        public async Task PauseTimeScalingIsOptInAndPriorUiContextIsRestored()
        {
            await coordinator.ShutdownAsync();
            ui = new UiService();
            pause = new PauseService("Pause");
            coordinator = new BootstrapCoordinator(new ILumiService[] { input, ui, pause });
            await coordinator.InitializeAsync();
            RegisteredScreen("Pause");
            input.SetContext(InputContext.UI);
            Time.timeScale = 0.55f;
            pause.Pause();
            Assert.That(Time.timeScale, Is.EqualTo(0.55f));
            pause.Resume();
            Assert.That(input.CurrentContext, Is.EqualTo(InputContext.UI));
            Assert.That(Time.timeScale, Is.EqualTo(0.55f));
        }

        [Test]
        public async Task ShutdownWhilePausedRestoresTimeAndDetachesScreens()
        {
            UiScreen pauseScreen = RegisteredScreen("Pause");
            Time.timeScale = 0.7f;
            pause.Pause();
            await coordinator.ShutdownAsync();
            Assert.That(pause.IsPaused, Is.False);
            Assert.That(ui.IsInitialized, Is.False);
            Assert.That(Time.timeScale, Is.EqualTo(0.7f));
            Assert.That(pauseScreen.CanvasGroup.alpha, Is.Zero);
            Assert.Throws<InvalidOperationException>(() => ui.Open("Pause"));
            Assert.DoesNotThrow(() => pauseScreen.Configure("Fresh", pauseScreen.CanvasGroup));
        }

        [Test]
        public void BoundPointerActionReplaysStationaryPositionWithoutANewDeviceEvent()
        {
            Mouse mouse = InputSystem.AddDevice<Mouse>();
            input.RuntimeAsset.devices = new InputDevice[] { mouse };
            var position = new Vector2(120f, 80f);
            InputSystem.QueueStateEvent(mouse, new MouseState { position = position });
            InputSystem.Update();
            input.SetContext(InputContext.UI);
            InputSystemUiBridge bridge = Bridge(out InputSystemUIInputModule module);
            bridge.Bind(input);
            int pointsReceived = 0;
            using (input.Subscribe("UI/Point", _ => pointsReceived++))
            {
                // No new mouse event: this is the first input update after binding the module.
                InputSystem.Update();
                Assert.That(pointsReceived, Is.GreaterThan(0));
                Assert.That(module.point.action.ReadValue<Vector2>(), Is.EqualTo(position));
                Assert.That(source.FindAction("UI/Point").wantsInitialStateCheck, Is.False,
                    "The bridge configures only the owned runtime clone.");
            }
        }

        [Test]
        public void BridgeUsesOnlyRuntimeCloneAndFollowsContextAndSuspension()
        {
            InputSystemUiBridge bridge = Bridge(out InputSystemUIInputModule module);
            bridge.Bind(input);
            Assert.That(module.enabled, Is.False);
            Assert.That(module.actionsAsset, Is.SameAs(input.RuntimeAsset));
            Assert.That(module.move.action, Is.SameAs(input.GetAction("UI/Navigate")));
            input.SetContext(InputContext.UI);
            Assert.That(module.enabled, Is.True);
            Assert.That(source.FindActionMap("UI").enabled, Is.False);
            input.SetContext(InputContext.Pause);
            Assert.That(module.enabled, Is.True);
            using (input.Suspend())
            {
                Assert.That(module.enabled, Is.False);
                Assert.That(input.GetAction("UI/Navigate").enabled, Is.False);
            }

            Assert.That(module.enabled, Is.True);
            input.SetContext(InputContext.Gameplay);
            Assert.That(module.enabled, Is.False);
            Assert.That(input.GetAction("UI/Navigate").enabled, Is.False);
            bridge.Unbind();
            input.SetContext(InputContext.UI);
            Assert.That(module.enabled, Is.False);
            Assert.That(module.actionsAsset, Is.Null);
        }

        [Test]
        public void BridgeRepeatedBindingsReleaseInputSystemStaticReferences()
        {
            InputSystemUiBridge bridge = Bridge(out InputSystemUIInputModule module);
            input.SetContext(InputContext.UI);
            InputAction[] actions = UiActions();
            for (int index = 0; index < 3; index++)
            {
                bridge.Bind(input);
                Assert.That(module.enabled, Is.True);
                Assert.That(ModuleReferencesContainAny(actions), Is.True);
                bridge.Unbind();
                Assert.That(module.enabled, Is.False);
                Assert.That(ModuleReferencesContainAny(actions), Is.False);
                Assert.That(input.GetAction("UI/Navigate").enabled, Is.True);
            }
        }

        [Test]
        public async Task InputShutdownWhileBridgeBoundReleasesCallbacksBeforeDestroyingClone()
        {
            InputSystemUiBridge bridge = Bridge(out InputSystemUIInputModule module);
            input.SetContext(InputContext.UI);
            bridge.Bind(input);
            InputAction[] actions = UiActions();
            await input.ShutdownAsync(CancellationToken.None);
            Assert.That(module.enabled, Is.False);
            Assert.That(input.RuntimeAsset, Is.Null);
            Assert.That(ModuleReferencesContainAny(actions), Is.False);
            Assert.DoesNotThrow(bridge.Unbind);
            Assert.That(module.actionsAsset, Is.Null);
        }

        [Test]
        public void InvalidBridgePathsPreserveExistingValidBinding()
        {
            InputSystemUiBridge bridge = Bridge(out InputSystemUIInputModule module);
            input.SetContext(InputContext.UI);
            bridge.Bind(input);
            InputActionReference original = module.move;
            Assert.Throws<ArgumentException>(() =>
                bridge.Bind(input, new UiActionPaths { Navigate = "Missing/Action" }));
            Assert.That(module.move, Is.SameAs(original));
            Assert.That(module.enabled, Is.True);
        }

        private UiScreen RegisteredScreen(string id)
        {
            UiScreen screen = Screen(id);
            ui.Register(screen);
            return screen;
        }

        private UiScreen Screen(string id)
        {
            GameObject gameObject = NewObject(id, typeof(RectTransform), typeof(CanvasGroup),
                typeof(UiScreen));
            UiScreen screen = gameObject.GetComponent<UiScreen>();
            UnityEngine.UI.Button first = AddButton(screen, "FirstButton");
            screen.Configure(id, gameObject.GetComponent<CanvasGroup>(), first);
            return screen;
        }

        private UnityEngine.UI.Button AddButton(UiScreen screen, string name)
        {
            GameObject gameObject = NewObject(name, typeof(RectTransform), typeof(CanvasRenderer),
                typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.Button));
            gameObject.transform.SetParent(screen.transform, false);
            return gameObject.GetComponent<UnityEngine.UI.Button>();
        }

        private InputSystemUiBridge Bridge(out InputSystemUIInputModule module)
        {
            eventSystem.gameObject.SetActive(false);
            module = eventSystem.gameObject.AddComponent<InputSystemUIInputModule>();
            module.enabled = false;
            InputSystemUiBridge bridge = eventSystem.gameObject.AddComponent<InputSystemUiBridge>();
            eventSystem.gameObject.SetActive(true);
            return bridge;
        }

        private InputAction[] UiActions()
        {
            return new[]
            {
                input.GetAction("UI/Navigate"), input.GetAction("UI/Point"),
                input.GetAction("UI/Click"), input.GetAction("UI/Submit"),
                input.GetAction("UI/Cancel"), input.GetAction("UI/ScrollWheel")
            };
        }

        private static bool ModuleReferencesContainAny(InputAction[] actions)
        {
            // Regression evidence for the installed Input System 1.19 implementation.
            // Reflection is confined to this test; runtime uses only public APIs.
            FieldInfo field = typeof(InputSystemUIInputModule).GetField("s_InputActionReferenceCounts",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(field, Is.Not.Null, "Input System changed: revisit the ownership regression test.");
            IDictionary table = (IDictionary)field.GetValue(null);
            foreach (InputAction action in actions)
            {
                if (table.Contains(action))
                {
                    return true;
                }
            }

            return false;
        }

        private GameObject NewObject(string name, params Type[] components)
        {
            var gameObject = new GameObject(name, components);
            objects.Add(gameObject);
            return gameObject;
        }
    }
}
