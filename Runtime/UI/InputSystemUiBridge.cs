using System;
using System.Collections.Generic;
using Lumivale.Core.Input;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;

namespace Lumivale.Core.UI
{
    /// <summary>
    /// Binds an exclusively owned uGUI module to the Input service's runtime clone.
    /// The six configured UI actions are exclusively driven by this bridge while it is bound.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(EventSystem), typeof(InputSystemUIInputModule))]
    public sealed class InputSystemUiBridge : MonoBehaviour
    {
        private readonly List<InputActionReference> references = new List<InputActionReference>();
        private InputSystemUIInputModule module;
        private InputService input;
        private InputAction[] actions;
        private bool refreshing;
        private bool refreshPending;

        /// <summary>Binds only initialized services and validates every action before modifying the module.</summary>
        public void Bind(InputService inputService, UiActionPaths paths = null)
        {
            if (inputService == null)
            {
                throw new ArgumentNullException(nameof(inputService));
            }

            if (!inputService.IsInitialized)
            {
                throw new InvalidOperationException("The UI bridge requires initialized Input.");
            }

            paths = paths ?? new UiActionPaths();
            InputAction[] resolved =
            {
                inputService.GetAction(paths.Navigate), inputService.GetAction(paths.Point),
                inputService.GetAction(paths.Click), inputService.GetAction(paths.Submit),
                inputService.GetAction(paths.Cancel), inputService.GetAction(paths.ScrollWheel)
            };
            var unique = new HashSet<InputAction>(resolved);
            if (unique.Count != resolved.Length)
            {
                throw new ArgumentException("Each UI module action path must refer to a distinct action.",
                    nameof(paths));
            }

            Unbind();
            module = GetComponent<InputSystemUIInputModule>();
            module.enabled = false;
            module.UnassignActions();
            input = inputService;
            actions = resolved;
            try
            {
                // A newly created module must receive a stationary pointer's current position.
                // PassThrough actions otherwise wait for movement after every scene change.
                if (!resolved[1].wantsInitialStateCheck)
                {
                    resolved[1].wantsInitialStateCheck = true;
                    if (resolved[1].bindings.Count > 0)
                    {
                        // 1.19 caches this flag when resolving bindings. Reapply the identical
                        // binding through the public API to rebuild the owned clone's cache.
                        InputBinding binding = resolved[1].bindings[0];
                        resolved[1].ChangeBinding(0).To(binding);
                    }
                }
                module.actionsAsset = input.RuntimeAsset;
                foreach (InputAction action in actions)
                {
                    references.Add(InputActionReference.Create(action));
                }

                module.move = references[0];
                module.point = references[1];
                module.leftClick = references[2];
                module.submit = references[3];
                module.cancel = references[4];
                module.scrollWheel = references[5];
                input.StateChanged += Refresh;
                Refresh();
            }
            catch
            {
                Unbind();
                throw;
            }
        }

        /// <summary>Releases module callbacks and generated references; it leaves the module disabled.</summary>
        public void Unbind()
        {
            bool restoreActions = module != null && module.enabled && input != null &&
                input.IsInitialized && !input.IsSuspended;
            if (input != null)
            {
                input.StateChanged -= Refresh;
            }

            if (module != null)
            {
                module.enabled = false;
                module.UnassignActions();
            }

            if (restoreActions && actions != null)
            {
                foreach (InputAction action in actions)
                {
                    action.Enable();
                }
            }

            foreach (InputActionReference reference in references)
            {
                if (reference == null)
                {
                    continue;
                }

                if (Application.isPlaying)
                {
                    Destroy(reference);
                }
                else
                {
                    DestroyImmediate(reference);
                }
            }

            references.Clear();
            actions = null;
            input = null;
        }

        private void Refresh()
        {
            if (refreshing)
            {
                refreshPending = true;
                return;
            }

            refreshing = true;
            try
            {
                do
                {
                    refreshPending = false;
                    RefreshModule();
                }
                while (refreshPending);
            }
            finally
            {
                refreshing = false;
            }
        }

        private void RefreshModule()
        {
            if (module == null)
            {
                return;
            }

            if (input != null && !input.IsInitialized)
            {
                // Clear the module's asset/reference fields before Input destroys its native clone.
                Unbind();
                return;
            }

            bool enable = isActiveAndEnabled && input != null && input.IsInitialized &&
                !input.IsSuspended && actions != null;
            if (enable)
            {
                foreach (InputAction action in actions)
                {
                    if (!action.enabled)
                    {
                        enable = false;
                        break;
                    }
                }
            }

            if (enable && !module.enabled)
            {
                InputService expectedInput = input;
                InputContext expectedContext = input.CurrentContext;
                InputAction[] expectedActions = actions;
                // Input System 1.19 keeps externally-enabled actions in a static reference table
                // after module disable. Giving the module ownership guarantees removal at shutdown.
                foreach (InputAction action in expectedActions)
                {
                    action.Disable();
                    if (module == null || !isActiveAndEnabled ||
                        !ReferenceEquals(input, expectedInput) || !input.IsInitialized ||
                        input.IsSuspended || input.CurrentContext != expectedContext ||
                        !ReferenceEquals(actions, expectedActions))
                    {
                        return;
                    }
                }

                // Disabling an in-progress action can invoke a project callback synchronously.
                // It may change context, suspend input, or unbind/destroy this bridge.
                if (module != null && isActiveAndEnabled && ReferenceEquals(input, expectedInput)
                    && input.IsInitialized && !input.IsSuspended
                    && input.CurrentContext == expectedContext && actions != null)
                {
                    module.enabled = true;
                }
            }
            else if (!enable && module.enabled)
            {
                module.enabled = false;
            }
        }

        private void OnEnable()
        {
            Refresh();
        }

        private void OnDisable()
        {
            if (module == null || !module.enabled)
            {
                return;
            }

            module.enabled = false;
            // Restore the service's active context when only the bridge component is disabled.
            // The module no longer owns these actions; a subsequent OnEnable can acquire them again.
            if (input != null && input.IsInitialized && !input.IsSuspended && actions != null)
            {
                foreach (InputAction action in actions)
                {
                    action.Enable();
                }
            }
        }

        private void OnDestroy()
        {
            Unbind();
        }
    }
}
