using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Lumivale.Core.Bootstrap
{
    /// <summary>
    /// Persistent Unity adapter for a coordinator. Place it on a dedicated root GameObject.
    /// Await ShutdownAsync before an intentional application exit when cleanup must finish;
    /// Unity does not await OnDestroy or OnApplicationQuit callbacks.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-10000)]
    public abstract class LumiBootstrap : MonoBehaviour
    {
        private static Task shutdownBarrier = Task.CompletedTask;
        private readonly TaskCompletionSource<object> ready =
            new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool ownsSingleton;
        private bool destroyed;
        private bool shutdownRequested;
        private Task predecessorShutdown = Task.CompletedTask;
        private Task shutdownTask;
        private Task lifecycleShutdown;

        public static LumiBootstrap Instance { get; private set; }

        public BootstrapCoordinator Coordinator { get; private set; }

        public bool IsReady => Coordinator != null && Coordinator.State == BootstrapState.Running;

        public Task Ready => ready.Task;

        protected abstract IEnumerable<ILumiService> CreateServices();

        protected virtual async void Awake()
        {
            if (shutdownRequested)
            {
                ready.TrySetCanceled();
                return;
            }

            if (Instance != null && Instance != this)
            {
                ready.TrySetException(new InvalidOperationException("A Lumi bootstrap already exists."));
                enabled = false;
                // Do not destroy other components or children on a duplicate's GameObject.
                Destroy(this);
                return;
            }

            try
            {
                if (transform.parent != null)
                {
                    throw new InvalidOperationException("LumiBootstrap must be on a root GameObject.");
                }

                Instance = this;
                ownsSingleton = true;
                predecessorShutdown = shutdownBarrier;
                DontDestroyOnLoad(gameObject);
                // A destroyed Unity object compares equal to null before its asynchronous
                // teardown necessarily finishes. Keep that teardown as an explicit barrier.
                await predecessorShutdown;
                if (destroyed || shutdownRequested)
                {
                    ready.TrySetCanceled();
                    return;
                }

                Coordinator = new BootstrapCoordinator(CreateServices());
                await Coordinator.InitializeAsync();
                ready.TrySetResult(null);
            }
            catch (OperationCanceledException)
            {
                ready.TrySetCanceled();
            }
            catch (Exception exception)
            {
                ready.TrySetException(exception);
                Debug.LogException(exception, this);
            }
        }

        public Task ShutdownAsync(CancellationToken cancellationToken = default)
        {
            if (shutdownTask != null)
            {
                return shutdownTask;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled(cancellationToken);
            }

            shutdownRequested = true;
            ready.TrySetCanceled();
            shutdownTask = ShutdownCoordinatorAsync();
            return shutdownTask;
        }

        protected virtual void OnApplicationQuit()
        {
            StartLifecycleShutdown();
        }

        protected virtual void OnDestroy()
        {
            destroyed = true;
            ready.TrySetCanceled();
            if (!ownsSingleton)
            {
                return;
            }

            // Publish the task before releasing ownership. A replacement that is itself
            // destroyed while waiting also preserves this predecessor in its shutdown task.
            shutdownBarrier = StartLifecycleShutdown();
            if (Instance == this)
            {
                Instance = null;
            }
        }

        private Task StartLifecycleShutdown()
        {
            if (!ownsSingleton)
            {
                return Task.CompletedTask;
            }

            Task stopping = ShutdownAsync();
            if (lifecycleShutdown == null)
            {
                lifecycleShutdown = ObserveShutdownForUnityLifecycleAsync(stopping);
            }

            return stopping;
        }

        private async Task ShutdownCoordinatorAsync()
        {
            await predecessorShutdown;
            if (Coordinator != null)
            {
                await Coordinator.ShutdownAsync();
            }
        }

        private async Task ObserveShutdownForUnityLifecycleAsync(Task stopping)
        {
            try
            {
                await stopping;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            // Editor sessions without domain reload must not inherit a previous play
            // session's Unity synchronization context or unfinished shutdown task.
            Instance = null;
            shutdownBarrier = Task.CompletedTask;
        }
    }
}
