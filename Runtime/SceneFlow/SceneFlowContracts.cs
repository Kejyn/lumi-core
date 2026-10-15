using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.SceneManagement;

namespace Lumivale.Core.SceneFlow
{
    /// <summary>A build-settings scene path (recommended) or an unambiguous scene name.</summary>
    public sealed class SceneLoadRequest
    {
        public SceneLoadRequest(string scenePath, LoadSceneMode mode = LoadSceneMode.Single,
            ISceneTransition transition = null)
        {
            if (string.IsNullOrWhiteSpace(scenePath))
            {
                throw new ArgumentException("A scene path or name is required.", nameof(scenePath));
            }

            if (mode != LoadSceneMode.Single && mode != LoadSceneMode.Additive)
            {
                throw new ArgumentOutOfRangeException(nameof(mode));
            }

            ScenePath = scenePath;
            Mode = mode;
            Transition = transition;
        }

        public string ScenePath { get; }
        public LoadSceneMode Mode { get; }
        public ISceneTransition Transition { get; }
    }

    public sealed class SceneLoadResult
    {
        public SceneLoadResult(SceneLoadRequest request)
        {
            Request = request;
        }

        public SceneLoadRequest Request { get; }
        public string ScenePath => Request.ScenePath;
    }

    public sealed class SceneLoadProgress
    {
        public SceneLoadProgress(SceneLoadRequest request, float progress)
        {
            Request = request;
            Progress = progress;
        }

        public SceneLoadRequest Request { get; }
        public float Progress { get; }
    }

    public enum SceneLoadErrorCode
    {
        NotInitialized,
        ServiceStopping,
        Busy,
        SceneUnavailable,
        LoadingFailed,
        TransitionFailed,
        Cancelled
    }

    public sealed class SceneFlowException : InvalidOperationException
    {
        public SceneFlowException(SceneLoadErrorCode errorCode, string message, Exception innerException = null)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
        }

        public SceneLoadErrorCode ErrorCode { get; }
    }

    public sealed class SceneLoadFailure
    {
        public SceneLoadFailure(SceneLoadRequest request, SceneLoadErrorCode errorCode, Exception exception)
        {
            Request = request;
            ErrorCode = errorCode;
            Exception = exception;
        }

        public SceneLoadRequest Request { get; }
        public SceneLoadErrorCode ErrorCode { get; }
        public Exception Exception { get; }
    }

    /// <summary>Implementations must be used on Unity's main thread.</summary>
    public interface ISceneLoader
    {
        bool CanLoadScene(string scenePath);
        ISceneLoadOperation BeginLoad(SceneLoadRequest request);
    }

    /// <summary>
    /// Represents a started, non-cancellable engine operation. Frame waiting and status access must
    /// remain available until completion, including after a request has been cancelled.
    /// </summary>
    public interface ISceneLoadOperation
    {
        float Progress { get; }
        bool IsDone { get; }
        void AllowActivation();
        Task WaitForNextFrameAsync();
    }

    /// <summary>Presentation hooks only: Scene Flow does not depend on a UI implementation.</summary>
    public interface ISceneTransition
    {
        Task BeforeLoadAsync(SceneLoadRequest request, CancellationToken cancellationToken);
        Task AfterLoadAsync(SceneLoadResult result, CancellationToken cancellationToken);
        Task RecoverAsync(SceneLoadRequest request, Exception exception, CancellationToken cancellationToken);
    }
}
