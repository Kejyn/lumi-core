using System;
using System.Threading;
using System.Threading.Tasks;

namespace Lumivale.Core.SceneFlow
{
    public sealed class ImmediateSceneTransition : ISceneTransition
    {
        public static ImmediateSceneTransition Instance { get; } = new ImmediateSceneTransition();

        public Task BeforeLoadAsync(SceneLoadRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task AfterLoadAsync(SceneLoadResult result, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task RecoverAsync(SceneLoadRequest request, Exception exception, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    /// <summary>A configurable delay using real elapsed time, independent of Time.timeScale.</summary>
    public sealed class DelaySceneTransition : ISceneTransition
    {
        private readonly TimeSpan _beforeDelay;
        private readonly TimeSpan _afterDelay;

        public DelaySceneTransition(TimeSpan beforeDelay, TimeSpan afterDelay)
        {
            if (beforeDelay < TimeSpan.Zero || beforeDelay.TotalMilliseconds > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(beforeDelay));
            }

            if (afterDelay < TimeSpan.Zero || afterDelay.TotalMilliseconds > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(afterDelay));
            }

            _beforeDelay = beforeDelay;
            _afterDelay = afterDelay;
        }

        public Task BeforeLoadAsync(SceneLoadRequest request, CancellationToken cancellationToken)
        {
            return Task.Delay(_beforeDelay, cancellationToken);
        }

        public Task AfterLoadAsync(SceneLoadResult result, CancellationToken cancellationToken)
        {
            return Task.Delay(_afterDelay, cancellationToken);
        }

        public Task RecoverAsync(SceneLoadRequest request, Exception exception, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
