using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lumivale.Core.Bootstrap;
using UnityEngine;

namespace Lumivale.Core.SceneFlow
{
    /// <summary>
    /// Serializes scene navigation. Call lifecycle and navigation methods from Unity's main thread.
    /// Cancellation after BeginLoad drains Unity's non-cancellable operation before releasing the gate;
    /// the destination scene may therefore have become active even though the request is cancelled.
    /// </summary>
    public sealed class SceneFlowService : ILumiService
    {
        private static readonly IReadOnlyCollection<string> NoDependencies = Array.Empty<string>();
        private readonly object _sync = new object();
        private readonly ISceneLoader _loader;
        private readonly ISceneTransition _defaultTransition;
        private CancellationTokenSource _lifetimeCancellation;
        private TaskCompletionSource<bool> _activeCompletion;
        private TaskCompletionSource<bool> _shutdownCompletion;
        private bool _initialized;
        private bool _stopping;
        private bool _notifyingFailure;

        public SceneFlowService(ISceneLoader loader = null, ISceneTransition defaultTransition = null)
        {
            _loader = loader ?? new UnitySceneLoader();
            _defaultTransition = defaultTransition ?? ImmediateSceneTransition.Instance;
        }

        public string Id => "scene-flow";
        public IReadOnlyCollection<string> Dependencies => NoDependencies;

        public bool IsLoading
        {
            get
            {
                lock (_sync)
                {
                    return _activeCompletion != null;
                }
            }
        }

        public event Action<SceneLoadProgress> ProgressChanged;
        public event Action<SceneLoadResult> LoadCompleted;
        public event Action<SceneLoadFailure> LoadFailed;

        /// <summary>Reports observer exceptions without changing a navigation result.</summary>
        public event Action<Exception> ObserverFailed;

        public Task InitializeAsync(ServiceContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (_stopping)
                {
                    throw new SceneFlowException(SceneLoadErrorCode.ServiceStopping,
                        "Scene Flow is still stopping. Await ShutdownAsync before initializing again.");
                }

                if (!_initialized)
                {
                    _lifetimeCancellation = new CancellationTokenSource();
                    _initialized = true;
                    _shutdownCompletion = null;
                }
            }

            return Task.CompletedTask;
        }

        public Task ShutdownAsync(CancellationToken cancellationToken)
        {
            // Cleanup is mandatory, including when the caller's cancellation token is already cancelled.
            CancellationTokenSource lifetimeCancellation;
            Task activeLoad;
            TaskCompletionSource<bool> completion;
            lock (_sync)
            {
                if (_shutdownCompletion != null)
                {
                    return _shutdownCompletion.Task;
                }

                if (!_initialized)
                {
                    return Task.CompletedTask;
                }

                _initialized = false;
                _stopping = true;
                completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _shutdownCompletion = completion;
                lifetimeCancellation = _lifetimeCancellation;
                activeLoad = _activeCompletion?.Task ?? Task.CompletedTask;
            }

            Exception cancellationFailure = null;
            try
            {
                lifetimeCancellation.Cancel();
            }
            catch (Exception exception)
            {
                // A user transition may have registered a cancellation callback that throws.
                // Cancellation has still been requested; cleanup and draining remain mandatory.
                cancellationFailure = exception;
            }

            _ = CompleteShutdownAsync(activeLoad, lifetimeCancellation, completion, cancellationFailure);
            return completion.Task;
        }

        public Task<SceneLoadResult> LoadAsync(SceneLoadRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                return Task.FromException<SceneLoadResult>(new ArgumentNullException(nameof(request)));
            }

            SceneFlowException rejection = null;
            CancellationTokenSource linkedCancellation = null;
            TaskCompletionSource<bool> completion = null;
            lock (_sync)
            {
                if (_stopping)
                {
                    rejection = new SceneFlowException(SceneLoadErrorCode.ServiceStopping,
                        "Scene Flow is stopping; wait for shutdown to finish before restarting it.");
                }
                else if (!_initialized)
                {
                    rejection = new SceneFlowException(SceneLoadErrorCode.NotInitialized,
                        "Initialize Scene Flow through the bootstrap coordinator before loading a scene.");
                }
                else if (_activeCompletion != null)
                {
                    rejection = new SceneFlowException(SceneLoadErrorCode.Busy,
                        "A scene load or transition is already running. Await its result before loading another scene.");
                }
                else
                {
                    linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken, _lifetimeCancellation.Token);
                    completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _activeCompletion = completion;
                }
            }

            if (rejection != null)
            {
                NotifyFailure(new SceneLoadFailure(request, rejection.ErrorCode, rejection));
                return Task.FromException<SceneLoadResult>(rejection);
            }

            return LoadCoreAsync(request, linkedCancellation, completion);
        }

        private async Task<SceneLoadResult> LoadCoreAsync(SceneLoadRequest request,
            CancellationTokenSource cancellation, TaskCompletionSource<bool> completion)
        {
            ISceneTransition transition = request.Transition ?? _defaultTransition;
            ISceneLoadOperation operation = null;
            bool transitionStarted = false;
            SceneLoadErrorCode stage = SceneLoadErrorCode.LoadingFailed;
            float lastProgress = -1f;
            SceneLoadResult completedResult = null;
            SceneLoadFailure completedFailure = null;

            void ReportProgress(float progress)
            {
                if (float.IsNaN(progress) || float.IsInfinity(progress))
                {
                    return;
                }

                progress = Mathf.Clamp01(progress);
                if (progress > lastProgress)
                {
                    lastProgress = progress;
                    Notify(ProgressChanged, new SceneLoadProgress(request, progress));
                }
            }

            try
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (!_loader.CanLoadScene(request.ScenePath))
                {
                    throw new SceneFlowException(SceneLoadErrorCode.SceneUnavailable,
                        $"Scene '{request.ScenePath}' cannot be loaded. Add it to the active build profile's scene list " +
                        "and use its full Assets/.../*.unity path or an unambiguous name.");
                }

                ReportProgress(0f);
                stage = SceneLoadErrorCode.TransitionFailed;
                transitionStarted = true;
                await transition.BeforeLoadAsync(request, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();

                stage = SceneLoadErrorCode.LoadingFailed;
                operation = _loader.BeginLoad(request);
                if (operation == null)
                {
                    throw new InvalidOperationException("The scene loader returned no operation.");
                }

                operation.AllowActivation();
                while (!operation.IsDone)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    // Unity reports 0..0.9 before activation; reserve the final 10% for activation/transition.
                    ReportProgress(Mathf.Min(operation.Progress, 0.9f));
                    await operation.WaitForNextFrameAsync();
                }

                cancellation.Token.ThrowIfCancellationRequested();
                ReportProgress(0.95f);
                var result = new SceneLoadResult(request);
                stage = SceneLoadErrorCode.TransitionFailed;
                await transition.AfterLoadAsync(result, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                ReportProgress(1f);
                completedResult = result;
                return result;
            }
            catch (Exception exception)
            {
                // Unity scene loading has no cancellation API. Never release the gate while it is in flight.
                if (operation != null)
                {
                    await DrainAsync(operation);
                }

                SceneLoadErrorCode errorCode = exception is OperationCanceledException
                    ? SceneLoadErrorCode.Cancelled
                    : exception is SceneFlowException flowException ? flowException.ErrorCode : stage;
                Exception reportedException = exception;
                if (!(exception is OperationCanceledException) && !(exception is SceneFlowException))
                {
                    reportedException = new SceneFlowException(errorCode,
                        $"{errorCode} while navigating to '{request.ScenePath}': {exception.Message}", exception);
                }

                if (transitionStarted)
                {
                    try
                    {
                        // Recovery must remain possible after request cancellation or service shutdown.
                        await transition.RecoverAsync(request, reportedException, CancellationToken.None);
                    }
                    catch (Exception recoveryException)
                    {
                        reportedException = new SceneFlowException(errorCode,
                            $"Navigation to '{request.ScenePath}' failed and transition recovery also failed.",
                            new AggregateException(reportedException, recoveryException));
                    }
                }

                completedFailure = new SceneLoadFailure(request, errorCode, reportedException);
                if (exception is OperationCanceledException)
                {
                    // Preserve cancellation semantics even if the recovery hook itself failed.
                    throw new OperationCanceledException(
                        $"Navigation to '{request.ScenePath}' was cancelled. A started Unity load was allowed to finish.",
                        reportedException, cancellation.Token);
                }

                throw reportedException;
            }
            finally
            {
                cancellation.Dispose();
                lock (_sync)
                {
                    if (ReferenceEquals(_activeCompletion, completion))
                    {
                        _activeCompletion = null;
                    }

                    completion.TrySetResult(true);
                }

                // Terminal callbacks may now start another load. No engine operation or recovery is pending.
                if (completedResult != null)
                {
                    Notify(LoadCompleted, completedResult);
                }
                else if (completedFailure != null)
                {
                    NotifyFailure(completedFailure);
                }
            }
        }

        private static async Task DrainAsync(ISceneLoadOperation operation)
        {
            operation.AllowActivation();
            while (!operation.IsDone)
            {
                await operation.WaitForNextFrameAsync();
            }
        }

        private async Task CompleteShutdownAsync(Task activeLoad, CancellationTokenSource lifetimeCancellation,
            TaskCompletionSource<bool> completion, Exception cancellationFailure)
        {
            await activeLoad;
            lifetimeCancellation.Dispose();
            lock (_sync)
            {
                _lifetimeCancellation = null;
                _stopping = false;
                if (cancellationFailure == null)
                {
                    completion.TrySetResult(true);
                }
                else
                {
                    completion.TrySetException(cancellationFailure);
                }
            }
        }

        private void Notify<T>(Action<T> observers, T value)
        {
            if (observers == null)
            {
                return;
            }

            foreach (Action<T> observer in observers.GetInvocationList())
            {
                try
                {
                    observer(value);
                }
                catch (Exception exception)
                {
                    ReportObserverFailure(exception);
                }
            }
        }

        private void NotifyFailure(SceneLoadFailure failure)
        {
            lock (_sync)
            {
                if (_notifyingFailure)
                {
                    // A synchronous fallback can fail too. Its task still faults, but recursively
                    // notifying the same failure handler would cause an unbounded stack overflow.
                    return;
                }

                _notifyingFailure = true;
            }

            try
            {
                Notify(LoadFailed, failure);
            }
            finally
            {
                lock (_sync)
                {
                    _notifyingFailure = false;
                }
            }
        }

        private void ReportObserverFailure(Exception exception)
        {
            Action<Exception> observers = ObserverFailed;
            if (observers == null)
            {
                Debug.LogWarning($"Scene Flow observer failed: {exception.Message}");
                return;
            }

            foreach (Action<Exception> observer in observers.GetInvocationList())
            {
                try
                {
                    observer(exception);
                }
                catch (Exception observerException)
                {
                    Debug.LogWarning($"Scene Flow error observer failed: {observerException.Message}");
                }
            }
        }
    }
}
