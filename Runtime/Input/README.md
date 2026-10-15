# Input

`Lumivale.Core.Input` references Bootstrap and Unity's new Input System. It contains no gameplay
controller or prescribed gameplay actions. Projects define their own action maps and device bindings
in an `InputActionAsset`; keyboard, mouse and gamepad support follows those bindings.

Register `InputService` with the bootstrap coordinator. Its service id is `input`, with no service
dependencies. Configure all three contexts before initialization:

```csharp
var input = new InputService(sourceActions, new Dictionary<InputContext, string[]>
{
    [InputContext.Gameplay] = new[] { "MyGameplay", "Common" },
    [InputContext.UI] = new[] { "MyMenus", "Common" },
    [InputContext.Pause] = new[] { "MyMenus", "Common" }
}, initialContext: InputContext.Gameplay);
```

Initialization clones the source asset and validates every context before enabling actions. Each
context requires at least one existing map name, without duplicates within that context. Contexts
may share maps. Configuration arrays are copied; source assets remain unchanged, including their
enabled state. `RuntimeAsset` exposes the owned clone while initialized. Do not destroy the clone
or enable its maps directly; the uGUI bridge is an explicit integration that manages its UI actions.

`SetContext(context)` disables maps outside the selected context before enabling its maps.
`CurrentContext` reports the desired context. `Suspend()` disables all maps and returns a disposable
lease. Nested suspensions remain active until their last lease is released, making them suitable
for scene transitions:

```csharp
using (input.Suspend())
{
    await sceneFlow.LoadAsync(request);
}
```

A context requested while suspended takes effect when the final lease is released. Old leases
cannot reactivate maps after shutdown or release a suspension belonging to a later initialization.
Input context switching does not change `Time.timeScale`; pause behavior belongs to the UI module.

Use `GetAction("Map/Action")` for reading values and `Subscribe` for managed callbacks:

```csharp
IDisposable subscription = input.Subscribe("MyGameplay/MyAction", OnPerformed);
// Dispose from the owner's OnDisable or OnDestroy.
subscription.Dispose();
```

`Subscribe` accepts `Started`, `Performed` or `Canceled`, defaulting to `Performed`. Its token can
be disposed repeatedly or during its own callback. Shutdown removes every callback registered
through this API, disables and destroys the clone, and clears lifecycle observers. Direct event
subscriptions made through `GetAction` remain the caller's responsibility. `StateChanged` reports
context, suspension and lifecycle changes; observers should unsubscribe when leaving a scene.
Observer exceptions are isolated and reported through `ObserverFailed`, without console logging.

`ActiveDeviceFamily` defaults to `KeyboardMouse` at initialization. `ActiveDeviceChanged` reports
the last meaningfully used `KeyboardMouse` or `Gamepad` family after the native input update,
independently of enabled maps or rebinding. Subscribe to refresh the project's contextual prompts;
use runtime bindings/display strings so overrides remain visible. The runtime asset's `devices`
restriction applies. No `PlayerInput` is required, and this detection never consumes events.
New button presses count; releases, held-button repeats, noisy/synthetic controls and absolute
pointer-position updates do not. Mouse deltas accumulated within an update need 2 pixels, scroll
magnitude 1; processed sticks need magnitude 0.3 and a change of 0.1 from their reference captured
when switching to keyboard/mouse, accepting gradual movement while ignoring drift and held-stick
jitter. Thresholds are fixed
conservative defaults for this version. Notifications are coalesced to the last meaningful event
within an input update. Removed/disconnected pads release their reference; shutdown removes all
native hooks, references and family-change observers.

Missing assets, contexts, maps or actions produce explicit argument or lifecycle errors. Runtime
operations require initialization. An already initialized service retains its clone; initialization
after shutdown creates a new clone and retains the last requested context. Cleanup runs even with
a cancelled shutdown token. There is no mutable global service state; the Bootstrap creates fresh
service instances for new Play sessions, including sessions without Domain Reload.

Call lifecycle methods, context changes, reads, subscriptions and token disposal on Unity's main
thread. Core PlayMode tests in `Tests/Runtime` use the official `InputTestFixture`, an owned
virtual keyboard and real Input System updates; Starter
integration tests exercise the uGUI EventSystem, scene transitions and repeated Play sessions.

## Rebinding existing actions

Input owns capture and runtime overrides; the consuming project owns its controls screen,
reserved navigation bindings, preference key, document version and storage. This extension adds
no new service and no dependency on Settings or Persistence.

```csharp
InputAction action = input.GetAction("MyGameplay/MyAction");
Guid bindingId = action.bindings[0].id; // Keep IDs stable in the source InputActionAsset.
InputRebindSession session = input.BeginRebind("MyGameplay/MyAction", bindingId,
    "<Keyboard>/*", result =>
    {
        if (!result.IsCanceled)
        {
            // Refresh display using action.GetBindingDisplayString(bindingIndex).
            // Persist input.ExportBindingOverrides() in the project's own preference document.
        }
    });
// When closing or destroying the controls view:
session.Dispose();
```

`BeginRebind(actionPath, bindingId, controlsPath, finished = null, timeoutSeconds = 10f)` returns an
`InputRebindSession`. `IsRebinding` is true until its resources and suspension lease are released.
Only one capture or override mutation is accepted by this service at a time. Timeout must be finite
and between 0.1 and 120 seconds; native Input System time is independent of `Time.timeScale`.
Useful capture paths include `<Keyboard>/*`, `<Mouse>/*` and `<Gamepad>/*`. The runtime asset's device
restriction is also respected when selecting candidates. Pointer position/delta and click counts
are excluded by Unity's native helper. Button actions accept buttons, including when their source
`expectedControlType` is empty. Composite roots are rejected; rebind parts separately. Unity resolves
the expected layout of each part, such as Button for `2DVector/Up`.

Capture suspends all configured action maps. Escape and controls with Unity's `Cancel` usage cancel,
including the standard gamepad East/B button, without changing the previous override. A context
change, session disposal or Input shutdown also cancels. A separate outer suspension remains owned
by its original lease. `Cancel()` and `Dispose()` are idempotent; `IsCompleted` and `IsCanceled` report
the outcome. Native cancellation completes on the next Input System update so the native operation
can remove hooks and restore its event policy before the service restores maps and calls `finished`.
Shutdown drains this cleanup synchronously before destroying the clone. Completion and
`BindingsChanged` observer exceptions are isolated and reported through `ObserverFailed`.

Do not start a second native `RebindingOperation` independently while using this API. Unity 1.19's
native capture temporarily changes Input System's global event handling policy. Exclusivity here is
per InputService; applications composing multiple Input services must serialize their captures too.
There is no additional mutable global registry in Core.

## Export, import and reset

`ExportBindingOverrides()` returns native Input System override JSON, or `""` when no overrides
exist. `ImportBindingOverrides(json)` explicitly replaces overrides; `ResetBindingOverrides()` is
equivalent to importing `""`. These APIs require initialization and reject calls during capture.
They do not touch the source asset, PlayerPrefs, Settings documents or gameplay save files.
`BindingsChanged` fires after a successful capture, import or reset; cancellation does not fire it.

The format is deliberately limited to **control path overrides**. Native JSON entries contain
`action`, `id`, `path`, `interactions` and `processors` as strings. The latter two must contain the
native `"null"` sentinel: configure processors/interactions on the source asset instead. This avoids
Unity silently logging and ignoring unknown processor/interaction configuration during import.
Directly created processor, interaction or composite-root overrides cannot be exported by this API.

Imports accept at most 256 Ki characters, 1024 entries and 2048 characters per field. The reader
rejects invalid JSON grammar/types, missing/duplicate/unknown fields, duplicate IDs, unknown binding
IDs, action mismatches and composite roots before native mutation. It then loads and resolves the
document on a disabled isolated clone. An unexpected runtime application failure restores the previous
override snapshot; failure of both application and rollback is reported as `AggregateException`.
Missing/changed binding IDs produce incompatibility instead of partially applying a document.

Projects should store this JSON in their own versioned preferences and preserve malformed or future
documents until an explicit user decision. Do not add it to the Core Settings V1 document or gameplay
DTO automatically. Keep navigation/escape/pointer bindings reserved in the project's controls screen,
validate that imported documents cannot override those reserved actions, and refresh contextual
gameplay hints from the current runtime binding display strings. Adding bindings should preserve all
existing IDs; replacing IDs intentionally requires a project-owned preference migration.

The Core regression suite in `InputRebindingTests` uses native Input System events against owned
virtual keyboard, mouse and gamepad devices. It covers actual capture/action delivery, composite parts,
reserved cancellation, device filtering, lifecycle, timeout, persistence round trips, rejection without
mutation and observer reentrance. Test execution results are recorded by the consuming project's report.
