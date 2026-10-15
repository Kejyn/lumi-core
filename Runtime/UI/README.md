# UI

`Lumivale.Core.UI` provides uGUI navigation and configurable pause behavior. It references
Bootstrap, Input, Unity Input System and `UnityEngine.UI`. It does not reference Scene Flow.
Projects own their Canvas, EventSystem, visual assets and navigation bindings. Invoke every API
on Unity's main thread.

`UiService` depends on the initialized `input` service. Configure a `UiScreen` with its id,
CanvasGroup and optional first Selectable, then register it after Bootstrap is ready. Keep the
returned `IDisposable` and dispose it when its owner is released. Destroying a registered screen
also unregisters it automatically. Screens stay active; their CanvasGroup controls visibility,
interaction and raycasts.

- `Open(id, rememberHistory: true)` opens a screen and records the previous screen.
- `OpenModal(id)` adds a modal; only the top screen accepts interaction.
- `Back()` closes the top modal or restores the previous screen.
- `Close(id)` also closes modals above that id. `CloseAll()` clears the navigation history.
- `GetScreen`, `CurrentScreenId`, `ModalCount` and `IsOpen` expose navigation state.
- `ScreenClosed` is raised after closing, unregistering or stopping an open screen. Observers
  must unsubscribe when their owner leaves a scene; service shutdown clears all observers.

Selection uses the active EventSystem. A remembered, active and interactable Selectable is
restored when possible, otherwise the screen's first selection is used. Modal screens should
have an appropriate visual raycast blocker where the design needs one. UiService does not
change Input contexts: composition decides when Gameplay or UI applies.

Defer navigation requested by `ISelectHandler.OnSelect` or `IDeselectHandler.OnDeselect`
until their callback returns: EventSystem prevents recursive selection. Likewise, when the UI
module uses an action, defer context changes that disable its maps until the native Input
System callback chain has finished. The Starter queues Pause/Cancel requests for `Update`.

`PauseService(pauseScreenId, controlTimeScale: false)` depends on Input and UI. `Pause()` opens
the pause modal and switches to the Pause context; `Resume()` restores the exact previous
context. Time scaling is opt-in and restores the previous value, including values other than 1.
Closing or destroying the pause screen and service shutdown also restore state. Repeated
Pause/Resume requests are idempotent; a Pause request during restoration is ignored. Interface
animation and delays that must run while paused must use unscaled time.

`InputSystemUiBridge.Bind(input, paths)` binds an exclusively owned
`InputSystemUIInputModule` to Input's runtime clone. `UiActionPaths` configures Navigate, Point,
Click, Submit, Cancel and ScrollWheel paths; no project gameplay actions are imposed. Configure
the module disabled and without source action references before binding. The bridge enables it
only when all six configured actions are enabled and Input is not suspended. `Unbind`, object
destruction and Input shutdown remove callbacks and generated action references.

The bridge sets `Point.wantsInitialStateCheck` on the runtime clone so a newly bound module
receives a stationary pointer's current position after a scene change. Input System 1.19 caches
this flag during binding resolution; the bridge reapplies Point's first binding unchanged via
the public `ChangeBinding(...).To(...)` API to rebuild that cache. The source asset is untouched.

The bridge exclusively controls these six runtime UI actions while bound. Unity Input System
1.19 retains externally enabled actions in the module's static reference table after disabling
it. The bridge therefore briefly disables the six actions before enabling the module, making
the module own its action activation and release its static references on disable. Source
assets are never changed. Other code must use Input contexts and suspension rather than
directly enabling or disabling those actions. Core regression tests check reference cleanup
against the installed Input System implementation; upgrading that package requires rerunning
those tests.

Core Input/UI tests live in `Tests/Runtime`, assembly `Lumivale.Core.Tests.PlayMode`. They use
the official `InputTestFixture` with virtual devices and cover navigation, pause restoration,
module ownership cleanup and stationary-pointer initialization. Real scene composition and
consecutive Play-session integration tests remain in `Lumi-start`.
