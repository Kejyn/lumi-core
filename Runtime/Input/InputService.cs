using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lumivale.Core.Bootstrap;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;

namespace Lumivale.Core.Input
{
    /// <summary>
    /// Owns a private InputActionAsset instance and enables only the configured context maps.
    /// Invoke lifecycle, context and subscription methods on Unity's main thread.
    /// </summary>
    public sealed class InputService : ILumiService
    {
        private static readonly IReadOnlyCollection<string> NoDependencies = Array.Empty<string>();
        private readonly InputActionAsset _source;
        private readonly Dictionary<InputContext, string[]> _contextNames;
        private readonly HashSet<Subscription> _subscriptions = new HashSet<Subscription>();
        private readonly Dictionary<Gamepad, StickReference> _stickReferences = new Dictionary<Gamepad, StickReference>();
        private Dictionary<InputContext, HashSet<InputActionMap>> _contextMaps;
        private int _generation;
        private int _suspensionCount;
        private int _stateVersion;
        private bool _applyingState;
        private bool _statePending;
        private bool _stopping;
        private bool _bindingMutation;
        private InputRebindSession _rebindSession;
        private InputDeviceFamily _pendingDeviceFamily;
        private bool _hasPendingDeviceActivity;
        private Vector2 _mouseDeltaThisUpdate;

        public InputService(InputActionAsset source,
            IReadOnlyDictionary<InputContext, string[]> contextMaps,
            InputContext initialContext = InputContext.Gameplay)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (contextMaps == null)
            {
                throw new ArgumentNullException(nameof(contextMaps));
            }

            if (!Enum.IsDefined(typeof(InputContext), initialContext))
            {
                throw new ArgumentOutOfRangeException(nameof(initialContext));
            }

            _source = source;
            _contextNames = new Dictionary<InputContext, string[]>();
            foreach (KeyValuePair<InputContext, string[]> entry in contextMaps)
            {
                _contextNames.Add(entry.Key, entry.Value == null ? null : (string[])entry.Value.Clone());
            }

            CurrentContext = initialContext;
        }

        public string Id => "input";
        public IReadOnlyCollection<string> Dependencies => NoDependencies;

        /// <summary>
        /// The owned runtime clone, or null outside its initialized lifetime. Do not destroy it
        /// or enable maps directly; use SetContext and Suspend to preserve context exclusivity.
        /// </summary>
        public InputActionAsset RuntimeAsset { get; private set; }

        public InputContext CurrentContext { get; private set; }
        public bool IsInitialized { get; private set; }
        public bool IsSuspended => IsInitialized && _suspensionCount > 0;
        public bool IsRebinding => _rebindSession != null;

        /// <summary>The last meaningfully used device family; defaults to keyboard/mouse at initialization.</summary>
        public InputDeviceFamily ActiveDeviceFamily { get; private set; } = InputDeviceFamily.KeyboardMouse;

        /// <summary>Reports context, suspension and lifecycle changes; unsubscribe when leaving a scene.</summary>
        public event Action StateChanged;

        /// <summary>Reports subscriber exceptions without interrupting other subscribers or map changes.</summary>
        public event Action<Exception> ObserverFailed;

        /// <summary>Reports a completed rebind, import or reset; presentation and storage remain project-owned.</summary>
        public event Action BindingsChanged;

        /// <summary>Reports an actual device-family change after the native input update. Presentation remains project-owned.</summary>
        public event Action<InputDeviceFamily> ActiveDeviceChanged;

        public Task InitializeAsync(ServiceContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_stopping)
            {
                throw new InvalidOperationException("Input is stopping; wait for its shutdown before initializing it.");
            }

            if (IsInitialized)
            {
                return Task.CompletedTask;
            }

            if (_source == null)
            {
                throw new InvalidOperationException("The configured InputActionAsset is missing or was destroyed.");
            }

            InputActionAsset clone = UnityEngine.Object.Instantiate(_source);
            clone.name = _source.name + " (Lumi runtime)";
            try
            {
                clone.Disable();
                Dictionary<InputContext, HashSet<InputActionMap>> maps = ValidateContexts(clone);
                cancellationToken.ThrowIfCancellationRequested();
                RuntimeAsset = clone;
                _contextMaps = maps;
                _suspensionCount = 0;
                _generation++;
                IsInitialized = true;
                ActiveDeviceFamily = InputDeviceFamily.KeyboardMouse;
                _hasPendingDeviceActivity = false;
                _mouseDeltaThisUpdate = Vector2.zero;
                CaptureStickReferences();
                InputSystem.onEvent += ObserveDeviceActivity;
                InputSystem.onAfterUpdate += DispatchDeviceActivity;
                InputSystem.onDeviceChange += ForgetRemovedDevice;
                RequestStateApplication();
            }
            catch
            {
                InputSystem.onEvent -= ObserveDeviceActivity;
                InputSystem.onAfterUpdate -= DispatchDeviceActivity;
                InputSystem.onDeviceChange -= ForgetRemovedDevice;
                _stickReferences.Clear();
                IsInitialized = false;
                RuntimeAsset = null;
                _contextMaps = null;
                clone.Disable();
                DestroyClone(clone);
                throw;
            }

            NotifyStateChanged();
            return Task.CompletedTask;
        }

        public Task ShutdownAsync(CancellationToken cancellationToken)
        {
            // Releasing owned Input System state is mandatory, even with a cancelled token.
            if (_stopping)
            {
                return Task.CompletedTask;
            }

            _stopping = true;
            InputActionAsset clone = RuntimeAsset;
            IsInitialized = false;
            RuntimeAsset = null;
            _contextMaps = null;
            _suspensionCount = 0;
            _generation++;
            _stateVersion++;
            InputSystem.onEvent -= ObserveDeviceActivity;
            InputSystem.onAfterUpdate -= DispatchDeviceActivity;
            InputSystem.onDeviceChange -= ForgetRemovedDevice;
            _hasPendingDeviceActivity = false;
            _mouseDeltaThisUpdate = Vector2.zero;
            _stickReferences.Clear();
            try
            {
                _rebindSession?.StopForShutdown();
                foreach (Subscription subscription in new List<Subscription>(_subscriptions))
                {
                    subscription.Dispose();
                }

                if (clone != null)
                {
                    clone.Disable();
                }
            }
            finally
            {
                // Consumers such as InputSystemUIInputModule must release their action references
                // while the clone still exists, particularly before DestroyImmediate in EditMode.
                NotifyStateChanged();
                StateChanged = null;
                ObserverFailed = null;
                BindingsChanged = null;
                ActiveDeviceChanged = null;
                try
                {
                    if (clone != null)
                    {
                        DestroyClone(clone);
                    }
                }
                finally
                {
                    _stopping = false;
                }
            }

            return Task.CompletedTask;
        }

        /// <summary>Changes the desired context. During suspension it is applied when the last lease is released.</summary>
        public void SetContext(InputContext context)
        {
            RequireInitialized();
            if (!_contextMaps.ContainsKey(context))
            {
                throw new ArgumentOutOfRangeException(nameof(context), context, "No action maps exist for this context.");
            }

            if (CurrentContext == context)
            {
                return;
            }

            CurrentContext = context;
            RequestStateApplication();
            NotifyStateChanged();
        }

        /// <summary>
        /// Disables all maps until every suspension lease is disposed. Use around scene transitions;
        /// leases from a previous service lifetime cannot enable a new runtime asset.
        /// </summary>
        public IDisposable Suspend()
        {
            RequireInitialized();
            var lease = new Suspension(this, _generation);
            _suspensionCount++;
            if (_suspensionCount == 1)
            {
                RequestStateApplication();
                NotifyStateChanged();
            }

            return lease;
        }

        /// <summary>Finds an action in the runtime clone. Prefer its unambiguous Map/Action path.</summary>
        public InputAction GetAction(string path)
        {
            RequireInitialized();
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("An action path is required.", nameof(path));
            }

            InputAction action = RuntimeAsset.FindAction(path);
            return action ?? throw new ArgumentException($"Input action '{path}' was not found in the runtime asset.", nameof(path));
        }

        /// <summary>
        /// Captures one existing non-composite binding. Escape or a device's Cancel control cancels;
        /// dispose the session when its view closes. Completion is dispatched after the native input update.
        /// </summary>
        public InputRebindSession BeginRebind(string actionPath, Guid bindingId, string controlsPath,
            Action<InputRebindResult> finished = null, float timeoutSeconds = 10f)
        {
            RequireBindingMutationAvailable();
            if (bindingId == Guid.Empty)
                throw new ArgumentException("An existing binding ID is required.", nameof(bindingId));
            if (string.IsNullOrWhiteSpace(controlsPath) || controlsPath.Length > 512)
                throw new ArgumentException("A bounded Input System control path is required.", nameof(controlsPath));
            if (float.IsNaN(timeoutSeconds) || float.IsInfinity(timeoutSeconds) ||
                timeoutSeconds < 0.1f || timeoutSeconds > 120f)
                throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), "Use a timeout between 0.1 and 120 seconds.");

            InputAction action = GetAction(actionPath);
            int index = InputBindingOverrides.FindBinding(action, bindingId);
            if (index < 0)
                throw new ArgumentException("The binding ID does not belong to this action.", nameof(bindingId));
            if (action.bindings[index].isComposite)
                throw new ArgumentException("Rebind individual composite parts, not the composite root.", nameof(bindingId));

            var session = new InputRebindSession(this, action, actionPath, index, bindingId,
                controlsPath, timeoutSeconds, finished);
            _rebindSession = session;
            try
            {
                session.Start();
                return session;
            }
            catch
            {
                session.AbortStart();
                throw;
            }
        }

        /// <summary>Returns native Input System override JSON, or an empty string for defaults. Does not write storage.</summary>
        public string ExportBindingOverrides()
        {
            RequireBindingMutationAvailable();
            string json = RuntimeAsset.SaveBindingOverridesAsJson();
            InputBindingOverrides.Validate(RuntimeAsset, json);
            return json;
        }

        /// <summary>
        /// Replaces overrides after bounded grammar, binding identity and native resolution validation on
        /// an isolated clone. Malformed or incompatible documents leave the active overrides unchanged.
        /// An empty string explicitly resets overrides. Projects version and persist this document separately.
        /// </summary>
        public void ImportBindingOverrides(string json)
        {
            RequireBindingMutationAvailable();
            if (json == null)
                throw new ArgumentNullException(nameof(json));

            // Resolving the validation clone can synchronously notify global Input System
            // observers. Protect the preparation phase too, before any native callback runs.
            _bindingMutation = true;
            try
            {
                InputActionAsset asset = RuntimeAsset;
                InputBindingOverrides.Validate(asset, json);
                InputActionAsset validationClone = UnityEngine.Object.Instantiate(asset);
                validationClone.name = asset.name + " (binding validation)";
                try
                {
                    validationClone.Disable();
                    validationClone.LoadBindingOverridesFromJson(json);
                    InputBindingOverrides.Resolve(validationClone);
                }
                finally
                {
                    validationClone.Disable();
                    DestroyClone(validationClone);
                }

                if (!IsInitialized || RuntimeAsset != asset)
                    throw new InvalidOperationException("Input stopped while validating the binding change.");
                string previous = asset.SaveBindingOverridesAsJson();
                using (Suspend())
                {
                    if (!IsInitialized || RuntimeAsset != asset)
                        throw new InvalidOperationException("Input stopped while preparing the binding change.");
                    try
                    {
                        asset.LoadBindingOverridesFromJson(json);
                        InputBindingOverrides.Resolve(asset);
                    }
                    catch (Exception importFailure)
                    {
                        try
                        {
                            asset.LoadBindingOverridesFromJson(previous);
                            InputBindingOverrides.Resolve(asset);
                        }
                        catch (Exception rollbackFailure)
                        {
                            throw new AggregateException("Binding import and rollback both failed.",
                                importFailure, rollbackFailure);
                        }
                        throw;
                    }
                }
            }
            finally
            {
                _bindingMutation = false;
            }
            NotifyBindingsChanged();
        }

        /// <summary>Explicitly removes runtime overrides. Source assets and saved documents are never modified.</summary>
        public void ResetBindingOverrides() => ImportBindingOverrides(string.Empty);

        internal void ReleaseRebinding(InputRebindSession session)
        {
            if (ReferenceEquals(_rebindSession, session))
                _rebindSession = null;
        }

        internal void DispatchRebindFinished(InputRebindResult result, Action<InputRebindResult> finished)
        {
            if (!result.IsCanceled && IsInitialized)
                NotifyBindingsChanged();
            try
            {
                finished?.Invoke(result);
            }
            catch (Exception exception)
            {
                NotifyObserverFailed(exception);
            }
        }

        /// <summary>
        /// Subscribes to a runtime action phase. Dispose the returned token when its owner is disabled
        /// or destroyed. Shutdown also removes every registration owned by this service.
        /// </summary>
        public IDisposable Subscribe(string path, Action<InputAction.CallbackContext> callback,
            InputActionPhase phase = InputActionPhase.Performed)
        {
            if (callback == null)
            {
                throw new ArgumentNullException(nameof(callback));
            }

            if (phase != InputActionPhase.Started && phase != InputActionPhase.Performed &&
                phase != InputActionPhase.Canceled)
            {
                throw new ArgumentOutOfRangeException(nameof(phase), phase, "Subscribe to Started, Performed or Canceled.");
            }

            var subscription = new Subscription(this, GetAction(path), callback, phase, _generation);
            _subscriptions.Add(subscription);
            subscription.Attach();
            return subscription;
        }

        private Dictionary<InputContext, HashSet<InputActionMap>> ValidateContexts(InputActionAsset clone)
        {
            var result = new Dictionary<InputContext, HashSet<InputActionMap>>();
            foreach (InputContext context in Enum.GetValues(typeof(InputContext)))
            {
                if (!_contextNames.TryGetValue(context, out string[] names) || names == null || names.Length == 0)
                {
                    throw new ArgumentException($"Configure at least one action map for the '{context}' context.");
                }

                var maps = new HashSet<InputActionMap>();
                foreach (string name in names)
                {
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        throw new ArgumentException($"The '{context}' context contains an empty action map name.");
                    }

                    InputActionMap map = clone.FindActionMap(name);
                    if (map == null)
                    {
                        throw new ArgumentException($"Action map '{name}' configured for '{context}' was not found.");
                    }

                    if (!maps.Add(map))
                    {
                        throw new ArgumentException($"Action map '{name}' is listed more than once for '{context}'.");
                    }
                }

                result.Add(context, maps);
            }

            return result;
        }

        private void RequestStateApplication()
        {
            _stateVersion++;
            _statePending = true;
            if (_applyingState)
            {
                return;
            }

            _applyingState = true;
            try
            {
                while (_statePending && IsInitialized)
                {
                    _statePending = false;
                    int version = _stateVersion;
                    InputActionAsset asset = RuntimeAsset;
                    HashSet<InputActionMap> desired = _contextMaps[CurrentContext];
                    bool suspended = IsSuspended;

                    // Disable old maps before enabling new ones. Disabling may invoke canceled
                    // callbacks which can unsubscribe, suspend or change the desired context.
                    foreach (InputActionMap map in asset.actionMaps)
                    {
                        if (!IsCurrentApplication(asset, version))
                        {
                            break;
                        }

                        if (map.enabled && (suspended || !desired.Contains(map)))
                        {
                            map.Disable();
                        }
                    }

                    if (!suspended)
                    {
                        foreach (InputActionMap map in asset.actionMaps)
                        {
                            if (!IsCurrentApplication(asset, version))
                            {
                                break;
                            }

                            if (desired.Contains(map))
                            {
                                // A UI module can temporarily disable only some actions. The map's
                                // enabled flag means "at least one"; Enable restores all desired actions.
                                map.Enable();
                            }
                        }
                    }
                }
            }
            finally
            {
                _applyingState = false;
            }
        }

        private bool IsCurrentApplication(InputActionAsset asset, int version)
        {
            return IsInitialized && RuntimeAsset == asset && _stateVersion == version;
        }

        private void ReleaseSuspension(int generation)
        {
            if (!IsInitialized || _generation != generation || _suspensionCount == 0)
            {
                return;
            }

            _suspensionCount--;
            if (_suspensionCount == 0)
            {
                RequestStateApplication();
                NotifyStateChanged();
            }
        }

        private void RequireInitialized()
        {
            if (!IsInitialized)
            {
                throw new InvalidOperationException("Initialize Input through Bootstrap before using its runtime actions.");
            }
        }

        private void ObserveDeviceActivity(InputEventPtr eventPtr, InputDevice device)
        {
            if (!IsInitialized || device == null ||
                (eventPtr.type != StateEvent.Type && eventPtr.type != DeltaStateEvent.Type))
                return;

            var allowedDevices = RuntimeAsset.devices;
            if (allowedDevices.HasValue)
            {
                bool allowed = false;
                foreach (InputDevice candidate in allowedDevices.Value)
                    if (ReferenceEquals(candidate, device))
                    {
                        allowed = true;
                        break;
                    }
                if (!allowed)
                    return;
            }

            InputDeviceFamily family;
            bool meaningful;
            if (device is Gamepad gamepad)
            {
                family = InputDeviceFamily.Gamepad;
                if (!_stickReferences.TryGetValue(gamepad, out StickReference reference))
                {
                    reference = new StickReference(gamepad);
                    _stickReferences.Add(gamepad, reference);
                }
                meaningful = HasButtonPress(eventPtr, device) ||
                    HasStickMovement(eventPtr, gamepad.leftStick, reference.Left) ||
                    HasStickMovement(eventPtr, gamepad.rightStick, reference.Right);
            }
            else if (device is Mouse mouse)
            {
                family = InputDeviceFamily.KeyboardMouse;
                // Absolute position alone can be warped or refreshed synthetically. Only a meaningful
                // relative movement, scroll or new button press counts as pointer activity.
                if (mouse.delta.ReadValueFromEvent(eventPtr, out Vector2 delta))
                    _mouseDeltaThisUpdate += delta;
                meaningful = HasButtonPress(eventPtr, device) ||
                    _mouseDeltaThisUpdate.sqrMagnitude >= 4f ||
                    (mouse.scroll.ReadValueFromEvent(eventPtr, out Vector2 scroll) && scroll.sqrMagnitude >= 1f);
            }
            else if (device is Keyboard)
            {
                family = InputDeviceFamily.KeyboardMouse;
                meaningful = HasButtonPress(eventPtr, device);
            }
            else
                return;

            if (meaningful)
            {
                InputDeviceFamily previous = _hasPendingDeviceActivity ? _pendingDeviceFamily : ActiveDeviceFamily;
                if (family == InputDeviceFamily.KeyboardMouse && previous != family)
                    CaptureStickReferences();
                if (family == InputDeviceFamily.Gamepad)
                    _mouseDeltaThisUpdate = Vector2.zero;
                _pendingDeviceFamily = family;
                _hasPendingDeviceActivity = true;
            }
        }

        private static bool HasButtonPress(InputEventPtr eventPtr, InputDevice device)
        {
            // Native enumeration excludes noisy/synthetic controls and unchanged state. Releases,
            // key repeats and a held trigger's small fluctuations must not reclaim the active family.
            foreach (InputControl control in eventPtr.EnumerateChangedControls(device))
                if (control is ButtonControl button && button.ReadValue() < button.pressPointOrDefault &&
                    button.ReadValueFromEvent(eventPtr) >= button.pressPointOrDefault)
                    return true;
            return false;
        }

        private static bool HasStickMovement(InputEventPtr eventPtr, StickControl stick, Vector2 reference)
        {
            return !stick.noisy && !stick.synthetic &&
                stick.ReadValueFromEvent(eventPtr, out Vector2 value) && value.sqrMagnitude >= 0.09f &&
                (value - reference).sqrMagnitude >= 0.01f;
        }

        private void CaptureStickReferences()
        {
            _stickReferences.Clear();
            foreach (Gamepad gamepad in Gamepad.all)
                _stickReferences.Add(gamepad, new StickReference(gamepad));
        }

        private void ForgetRemovedDevice(InputDevice device, InputDeviceChange change)
        {
            if (device is Gamepad gamepad &&
                (change == InputDeviceChange.Removed || change == InputDeviceChange.Disconnected))
                _stickReferences.Remove(gamepad);
        }

        private void DispatchDeviceActivity()
        {
            _mouseDeltaThisUpdate = Vector2.zero;
            if (!IsInitialized || !_hasPendingDeviceActivity)
                return;
            _hasPendingDeviceActivity = false;
            InputDeviceFamily family = _pendingDeviceFamily;
            if (ActiveDeviceFamily == family)
                return;
            ActiveDeviceFamily = family;
            if (ActiveDeviceChanged == null)
                return;
            foreach (Action<InputDeviceFamily> observer in ActiveDeviceChanged.GetInvocationList())
            {
                try
                {
                    observer(family);
                }
                catch (Exception exception)
                {
                    NotifyObserverFailed(exception);
                }
            }
        }

        private void RequireBindingMutationAvailable()
        {
            RequireInitialized();
            if (_stopping || _bindingMutation || IsRebinding)
                throw new InvalidOperationException("Finish or cancel the active binding operation before changing bindings.");
        }

        private void NotifyBindingsChanged()
        {
            if (BindingsChanged == null)
                return;
            foreach (Action observer in BindingsChanged.GetInvocationList())
            {
                try
                {
                    observer();
                }
                catch (Exception exception)
                {
                    NotifyObserverFailed(exception);
                }
            }
        }

        private void NotifyStateChanged()
        {
            if (StateChanged == null)
            {
                return;
            }

            foreach (Action observer in StateChanged.GetInvocationList())
            {
                try
                {
                    observer();
                }
                catch (Exception exception)
                {
                    NotifyObserverFailed(exception);
                }
            }
        }

        private void NotifyObserverFailed(Exception exception)
        {
            if (ObserverFailed == null)
            {
                return;
            }

            foreach (Action<Exception> observer in ObserverFailed.GetInvocationList())
            {
                try
                {
                    observer(exception);
                }
                catch
                {
                    // Error observers cannot interrupt Input System callbacks or lifecycle cleanup.
                }
            }
        }

        private static void DestroyClone(InputActionAsset clone)
        {
            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(clone);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(clone);
            }
        }

        private sealed class Suspension : IDisposable
        {
            private InputService _owner;
            private readonly int _generation;

            public Suspension(InputService owner, int generation)
            {
                _owner = owner;
                _generation = generation;
            }

            public void Dispose()
            {
                InputService owner = _owner;
                _owner = null;
                owner?.ReleaseSuspension(_generation);
            }
        }

        private readonly struct StickReference
        {
            public readonly Vector2 Left;
            public readonly Vector2 Right;

            public StickReference(Gamepad gamepad)
            {
                Left = gamepad.leftStick.ReadValue();
                Right = gamepad.rightStick.ReadValue();
            }
        }

        private sealed class Subscription : IDisposable
        {
            private InputService _owner;
            private InputAction _action;
            private Action<InputAction.CallbackContext> _callback;
            private readonly InputActionPhase _phase;
            private readonly int _generation;

            public Subscription(InputService owner, InputAction action,
                Action<InputAction.CallbackContext> callback, InputActionPhase phase, int generation)
            {
                _owner = owner;
                _action = action;
                _callback = callback;
                _phase = phase;
                _generation = generation;
            }

            public void Attach()
            {
                switch (_phase)
                {
                    case InputActionPhase.Started: _action.started += Invoke; break;
                    case InputActionPhase.Performed: _action.performed += Invoke; break;
                    case InputActionPhase.Canceled: _action.canceled += Invoke; break;
                }
            }

            public void Dispose()
            {
                InputService owner = _owner;
                if (owner == null)
                {
                    return;
                }

                _owner = null;
                switch (_phase)
                {
                    case InputActionPhase.Started: _action.started -= Invoke; break;
                    case InputActionPhase.Performed: _action.performed -= Invoke; break;
                    case InputActionPhase.Canceled: _action.canceled -= Invoke; break;
                }

                _action = null;
                _callback = null;
                owner._subscriptions.Remove(this);
            }

            private void Invoke(InputAction.CallbackContext context)
            {
                InputService owner = _owner;
                if (owner == null || !owner.IsInitialized || owner._generation != _generation)
                {
                    return;
                }

                try
                {
                    _callback(context);
                }
                catch (Exception exception)
                {
                    owner.NotifyObserverFailed(exception);
                }
            }
        }
    }
}
