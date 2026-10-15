using System;
using UnityEngine.InputSystem;

namespace Lumivale.Core.Input
{
    /// <summary>
    /// Owns one native Input System rebind and one Input suspension lease. All calls and disposal
    /// must occur on Unity's main thread. Cancellation finishes after the next input update,
    /// except shutdown which releases native resources before destroying the runtime asset.
    /// </summary>
    public sealed class InputRebindSession : IDisposable
    {
        private InputService _owner;
        private readonly InputAction _action;
        private readonly string _actionPath;
        private readonly int _bindingIndex;
        private readonly Guid _bindingId;
        private readonly string _controlsPath;
        private readonly float _timeoutSeconds;
        private readonly InputContext _context;
        private Action<InputRebindResult> _finished;
        private InputActionRebindingExtensions.RebindingOperation _operation;
        private IDisposable _suspension;
        private bool _finishQueued;
        private bool _afterUpdateHooked;
        private string _overridePath;

        internal InputRebindSession(InputService owner, InputAction action, string actionPath,
            int bindingIndex, Guid bindingId, string controlsPath, float timeoutSeconds,
            Action<InputRebindResult> finished)
        {
            _owner = owner;
            _action = action;
            _actionPath = actionPath;
            _bindingIndex = bindingIndex;
            _bindingId = bindingId;
            _controlsPath = controlsPath;
            _timeoutSeconds = timeoutSeconds;
            _context = owner.CurrentContext;
            _finished = finished;
            _overridePath = action.bindings[bindingIndex].overridePath;
        }

        public bool IsCompleted { get; private set; }
        public bool IsCanceled { get; private set; }

        internal void Start()
        {
            _owner.StateChanged += OnInputStateChanged;
            _suspension = _owner.Suspend();
            if (IsCompleted || _owner == null)
            {
                _suspension?.Dispose();
                _suspension = null;
                return;
            }
            if (_finishQueued || !_owner.IsInitialized)
            {
                IsCanceled = true;
                Finish();
                return;
            }

            // WithTargetBinding inside this native helper resolves a composite part's layout,
            // for example Button for 2DVector/Up, rather than the action's Vector2 layout.
            _operation = _action.PerformInteractiveRebinding(_bindingIndex)
                .WithControlsHavingToMatchPath(_controlsPath)
                .WithCancelingThrough("*/{Cancel}")
                .WithTimeout(_timeoutSeconds)
                .OnMatchWaitForAnother(0f)
                .OnPotentialMatch(FilterAndComplete)
                .OnComplete(_ => QueueFinish(false))
                .OnCancel(_ => QueueFinish(true));
            _operation.Start();
            // Registered after the native timeout hook: native completion first restores its
            // global event policy, then this hook restores maps and invokes project callbacks.
            InputSystem.onAfterUpdate += AfterInputUpdate;
            _afterUpdateHooked = true;
        }

        /// <summary>Cancels capture without changing its binding; safe to invoke repeatedly.</summary>
        public void Cancel()
        {
            if (IsCompleted || _finishQueued)
                return;
            if (_operation != null && _operation.started)
                _operation.Cancel();
            else
                QueueFinish(true);
        }

        public void Dispose() => Cancel();

        internal void StopForShutdown()
        {
            Cancel();
            Finish();
        }

        internal void AbortStart()
        {
            _finished = null;
            Cancel();
            IsCanceled = true;
            Finish();
        }

        private void OnInputStateChanged()
        {
            if (_owner != null && (!_owner.IsInitialized || _owner.CurrentContext != _context))
                Cancel();
        }

        private void FilterAndComplete(InputActionRebindingExtensions.RebindingOperation operation)
        {
            if (_owner == null || !_owner.IsInitialized)
            {
                operation.Cancel();
                return;
            }

            for (int index = operation.candidates.Count - 1; index >= 0; index--)
            {
                InputControl control = operation.candidates[index];
                if (!InputControlPath.Matches(_controlsPath, control) || !DeviceIsAllowed(control.device))
                    operation.RemoveCandidate(control);
            }
            if (operation.candidates.Count > 0)
                operation.Complete();
        }

        private bool DeviceIsAllowed(InputDevice device)
        {
            var devices = _owner.RuntimeAsset.devices;
            if (!devices.HasValue)
                return true;
            foreach (InputDevice allowed in devices.Value)
            {
                if (ReferenceEquals(allowed, device))
                    return true;
            }
            return false;
        }

        private void QueueFinish(bool canceled)
        {
            if (IsCompleted || _finishQueued)
                return;
            IsCanceled = canceled;
            if (!canceled)
                _overridePath = _action.bindings[_bindingIndex].overridePath;
            _finishQueued = true;
        }

        private void AfterInputUpdate()
        {
            if (_finishQueued)
                Finish();
        }

        private void Finish()
        {
            if (IsCompleted)
                return;
            IsCompleted = true;
            if (_afterUpdateHooked)
            {
                InputSystem.onAfterUpdate -= AfterInputUpdate;
                _afterUpdateHooked = false;
            }
            _operation?.Dispose();
            _operation = null;

            InputService owner = _owner;
            _owner = null;
            if (owner != null)
            {
                owner.StateChanged -= OnInputStateChanged;
                owner.ReleaseRebinding(this);
            }
            IDisposable suspension = _suspension;
            _suspension = null;
            suspension?.Dispose();
            Action<InputRebindResult> finished = _finished;
            _finished = null;
            owner?.DispatchRebindFinished(new InputRebindResult(_actionPath, _bindingId,
                IsCanceled, _overridePath), finished);
        }
    }
}
