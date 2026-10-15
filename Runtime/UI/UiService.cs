using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lumivale.Core.Bootstrap;
using Lumivale.Core.Input;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Lumivale.Core.UI
{
    /// <summary>Owns screen navigation, modal interaction and selection; projects own visual assets.</summary>
    public sealed class UiService : ILumiService
    {
        private static readonly string[] ServiceDependencies = { "input" };
        private readonly Dictionary<string, Registration> screens =
            new Dictionary<string, Registration>(StringComparer.Ordinal);
        private readonly List<string> history = new List<string>();
        private readonly List<string> modals = new List<string>();

        public string Id => "ui";
        public IReadOnlyCollection<string> Dependencies => Array.AsReadOnly(ServiceDependencies);
        public bool IsInitialized { get; private set; }
        public string CurrentScreenId { get; private set; }
        public int ModalCount => modals.Count;

        /// <summary>Raised after an open screen is closed, including destruction and shutdown.</summary>
        public event Action<string> ScreenClosed;

        public Task InitializeAsync(ServiceContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (!context.Get<InputService>().IsInitialized)
            {
                throw new InvalidOperationException("UI requires an initialized Input service.");
            }

            IsInitialized = true;
            return Task.CompletedTask;
        }

        public Task ShutdownAsync(CancellationToken cancellationToken)
        {
            // Committed cleanup is not canceled: visible screens and registrations must be released.
            IsInitialized = false;
            CloseAllInternal();
            foreach (Registration registration in screens.Values)
            {
                registration.Release();
            }

            screens.Clear();
            ScreenClosed = null;
            return Task.CompletedTask;
        }

        public IDisposable Register(UiScreen screen)
        {
            EnsureInitialized();
            if (screen == null)
            {
                throw new ArgumentNullException(nameof(screen));
            }

            if (string.IsNullOrWhiteSpace(screen.Id) || screen.CanvasGroup == null)
            {
                throw new ArgumentException("A screen requires an id and CanvasGroup.", nameof(screen));
            }

            if (screens.ContainsKey(screen.Id))
            {
                throw new InvalidOperationException($"Screen '{screen.Id}' is already registered.");
            }

            var registration = new Registration(this, screen);
            screen.Attach(registration);
            screens.Add(screen.Id, registration);
            screen.Present(false, false);
            return registration;
        }

        public UiScreen GetScreen(string screenId)
        {
            EnsureInitialized();
            if (screenId == null)
            {
                throw new ArgumentNullException(nameof(screenId));
            }

            if (!screens.TryGetValue(screenId, out Registration registration) ||
                registration.Screen == null)
            {
                throw new KeyNotFoundException($"Screen '{screenId}' is not registered.");
            }

            return registration.Screen;
        }

        public bool IsOpen(string screenId)
        {
            return screenId != null && (CurrentScreenId == screenId || modals.Contains(screenId));
        }

        public void Open(string screenId, bool rememberHistory = true)
        {
            GetScreen(screenId);
            if (modals.Contains(screenId))
            {
                throw new InvalidOperationException($"Screen '{screenId}' is already open as a modal.");
            }

            var closed = new List<string>();
            RememberFocus();
            RemoveModals(0, closed);
            if (CurrentScreenId != screenId)
            {
                if (CurrentScreenId != null)
                {
                    if (rememberHistory)
                    {
                        history.Add(CurrentScreenId);
                    }

                    Hide(CurrentScreenId);
                    closed.Add(CurrentScreenId);
                }

                CurrentScreenId = screenId;
            }

            RefreshPresentation();
            NotifyClosed(closed);
        }

        public void OpenModal(string screenId)
        {
            GetScreen(screenId);
            if (IsOpen(screenId))
            {
                throw new InvalidOperationException($"Screen '{screenId}' is already open.");
            }

            RememberFocus();
            modals.Add(screenId);
            RefreshPresentation();
        }

        public void CloseModal()
        {
            EnsureInitialized();
            if (modals.Count > 0)
            {
                Close(modals[modals.Count - 1]);
            }
        }

        /// <summary>Closes a screen and any modals above it, restoring the previous screen or focus.</summary>
        public void Close(string screenId)
        {
            GetScreen(screenId);
            var closed = new List<string>();
            RememberFocus();
            int modalIndex = modals.IndexOf(screenId);
            if (modalIndex >= 0)
            {
                RemoveModals(modalIndex, closed);
            }
            else if (CurrentScreenId == screenId)
            {
                RemoveModals(0, closed);
                Hide(screenId);
                closed.Add(screenId);
                CurrentScreenId = PopHistory();
            }
            else
            {
                return;
            }

            RefreshPresentation();
            NotifyClosed(closed);
        }

        public void Back()
        {
            EnsureInitialized();
            if (modals.Count > 0)
            {
                CloseModal();
            }
            else if (CurrentScreenId != null && history.Count > 0)
            {
                Close(CurrentScreenId);
            }
        }

        public void CloseAll()
        {
            EnsureInitialized();
            CloseAllInternal();
        }

        private void CloseAllInternal()
        {
            var closed = new List<string>();
            RememberFocus();
            RemoveModals(0, closed);
            if (CurrentScreenId != null)
            {
                Hide(CurrentScreenId);
                closed.Add(CurrentScreenId);
            }

            CurrentScreenId = null;
            history.Clear();
            ClearSelection();
            NotifyClosed(closed);
        }

        private void Unregister(Registration registration)
        {
            if (!screens.TryGetValue(registration.Id, out Registration current) ||
                !ReferenceEquals(registration, current))
            {
                return;
            }

            // Keep the entry available until navigation and close notifications have been settled.
            var closed = new List<string>();
            int modalIndex = modals.IndexOf(registration.Id);
            if (modalIndex >= 0)
            {
                RemoveModals(modalIndex, closed);
            }
            else if (CurrentScreenId == registration.Id)
            {
                RemoveModals(0, closed);
                Hide(registration.Id);
                closed.Add(registration.Id);
                CurrentScreenId = null;
            }

            history.RemoveAll(id => id == registration.Id);
            screens.Remove(registration.Id);
            registration.Release();
            if (CurrentScreenId == null)
            {
                CurrentScreenId = PopHistory();
            }

            RefreshPresentation();
            NotifyClosed(closed);
        }

        private string PopHistory()
        {
            while (history.Count > 0)
            {
                int last = history.Count - 1;
                string id = history[last];
                history.RemoveAt(last);
                if (screens.TryGetValue(id, out Registration entry) && entry.Screen != null)
                {
                    return id;
                }
            }

            return null;
        }

        private void RemoveModals(int startIndex, List<string> closed)
        {
            for (int index = modals.Count - 1; index >= startIndex; index--)
            {
                string id = modals[index];
                Hide(id);
                closed.Add(id);
                modals.RemoveAt(index);
            }
        }

        private void Hide(string id)
        {
            if (screens.TryGetValue(id, out Registration entry) && entry.Screen != null)
            {
                entry.Screen.Present(false, false);
            }
        }

        private void RememberFocus()
        {
            string activeId = modals.Count > 0 ? modals[modals.Count - 1] : CurrentScreenId;
            GameObject selected = EventSystem.current != null
                ? EventSystem.current.currentSelectedGameObject : null;
            if (selected != null && activeId != null && screens.TryGetValue(activeId, out Registration entry)
                && entry.Screen != null && selected.transform.IsChildOf(entry.Screen.transform))
            {
                entry.LastSelection = selected;
            }
        }

        private void RefreshPresentation()
        {
            string activeId = modals.Count > 0 ? modals[modals.Count - 1] : CurrentScreenId;
            if (CurrentScreenId != null && screens.TryGetValue(CurrentScreenId, out Registration root)
                && root.Screen != null)
            {
                root.Screen.Present(true, activeId == CurrentScreenId);
                root.Screen.transform.SetAsLastSibling();
            }

            foreach (string id in modals)
            {
                if (screens.TryGetValue(id, out Registration entry) && entry.Screen != null)
                {
                    entry.Screen.Present(true, id == activeId);
                    entry.Screen.transform.SetAsLastSibling();
                }
            }

            if (EventSystem.current == null)
            {
                return;
            }

            GameObject selection = null;
            if (activeId != null && screens.TryGetValue(activeId, out Registration active)
                && active.Screen != null)
            {
                UnityEngine.UI.Selectable remembered = active.LastSelection != null
                    ? active.LastSelection.GetComponent<UnityEngine.UI.Selectable>() : null;
                UnityEngine.UI.Selectable first = active.Screen.FirstSelected;
                if (CanSelect(remembered))
                {
                    selection = remembered.gameObject;
                }
                else if (CanSelect(first))
                {
                    selection = first.gameObject;
                }
            }

            EventSystem.current.SetSelectedGameObject(selection);
        }

        private static bool CanSelect(UnityEngine.UI.Selectable selectable)
        {
            return selectable != null && selectable.IsActive() && selectable.IsInteractable();
        }

        private static void ClearSelection()
        {
            if (EventSystem.current != null)
            {
                EventSystem.current.SetSelectedGameObject(null);
            }
        }

        private void NotifyClosed(List<string> ids)
        {
            foreach (string id in ids)
            {
                Action<string> handlers = ScreenClosed;
                if (handlers == null)
                {
                    continue;
                }

                foreach (Action<string> observer in handlers.GetInvocationList())
                {
                    try
                    {
                        observer(id);
                    }
                    catch (Exception exception)
                    {
                        Debug.LogException(exception);
                    }
                }
            }
        }

        private void EnsureInitialized()
        {
            if (!IsInitialized)
            {
                throw new InvalidOperationException("UI service is not initialized.");
            }
        }

        private sealed class Registration : IDisposable
        {
            private UiService owner;
            internal readonly string Id;
            internal UiScreen Screen;
            internal GameObject LastSelection;

            internal Registration(UiService service, UiScreen screen)
            {
                owner = service;
                Screen = screen;
                Id = screen.Id;
            }

            public void Dispose()
            {
                owner?.Unregister(this);
            }

            internal void Release()
            {
                owner = null;
                LastSelection = null;
                UiScreen screen = Screen;
                Screen = null;
                if (screen != null)
                {
                    screen.Detach(this);
                    screen.Present(false, false);
                }
            }
        }
    }
}
