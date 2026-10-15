using UnityEngine;

namespace Lumivale.Core.Audio
{
    /// <summary>Owned frame driver for an AudioService; never a global service locator.</summary>
    [AddComponentMenu("")]
    [DisallowMultipleComponent]
    public sealed class LumiAudioRunner : MonoBehaviour
    {
        private AudioService service;

        internal void Attach(AudioService owner) => service = owner;
        internal void Detach() => service = null;

        private void Update() => service?.Tick(Time.unscaledDeltaTime);

        private void OnDestroy()
        {
            AudioService owner = service;
            service = null;
            owner?.OnRunnerDestroyed();
        }
    }
}
