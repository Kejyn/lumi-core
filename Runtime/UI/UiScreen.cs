using System;
using UnityEngine;

namespace Lumivale.Core.UI
{
    /// <summary>A style-independent uGUI screen. Its registration ends when it is destroyed.</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CanvasGroup))]
    public sealed class UiScreen : MonoBehaviour
    {
        [SerializeField] private string id;
        [SerializeField] private CanvasGroup canvasGroup;
        [SerializeField] private UnityEngine.UI.Selectable firstSelected;

        private IDisposable registration;

        public string Id => id;
        public CanvasGroup CanvasGroup => canvasGroup;
        public UnityEngine.UI.Selectable FirstSelected => firstSelected;

        /// <summary>Configures a screen before registering it with a UI service.</summary>
        public void Configure(string screenId, CanvasGroup group,
            UnityEngine.UI.Selectable firstSelection = null)
        {
            if (registration != null)
            {
                throw new InvalidOperationException("A registered UI screen cannot be reconfigured.");
            }

            if (string.IsNullOrWhiteSpace(screenId))
            {
                throw new ArgumentException("A UI screen requires a non-empty id.", nameof(screenId));
            }

            if (group == null || group.gameObject != gameObject)
            {
                throw new ArgumentException("The CanvasGroup must belong to the screen GameObject.",
                    nameof(group));
            }

            if (firstSelection != null &&
                !firstSelection.transform.IsChildOf(transform))
            {
                throw new ArgumentException("The first selection must belong to the screen.",
                    nameof(firstSelection));
            }

            id = screenId;
            canvasGroup = group;
            firstSelected = firstSelection;
        }

        internal void Attach(IDisposable token)
        {
            if (registration != null)
            {
                throw new InvalidOperationException($"Screen '{id}' is already registered.");
            }

            registration = token;
        }

        internal void Detach(IDisposable token)
        {
            if (ReferenceEquals(registration, token))
            {
                registration = null;
            }
        }

        internal void Present(bool visible, bool interactive)
        {
            if (canvasGroup == null)
            {
                return;
            }

            canvasGroup.alpha = visible ? 1f : 0f;
            canvasGroup.interactable = visible && interactive;
            canvasGroup.blocksRaycasts = visible && interactive;
        }

        private void Awake()
        {
            if (canvasGroup == null)
            {
                canvasGroup = GetComponent<CanvasGroup>();
            }
        }

        private void OnDestroy()
        {
            registration?.Dispose();
            registration = null;
        }
    }
}
