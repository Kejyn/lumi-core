using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lumivale.Core.SceneFlow;
using NUnit.Framework;

namespace Lumivale.Tests.EditMode
{
    public sealed class SceneFlowTests
    {
        private const string ScenePath = "Assets/Scenes/Destination.unity";

        [Test]
        public async Task SuccessfulLoadReportsFiniteMonotonicProgressAndCompletesOnce()
        {
            var loader = new FakeLoader();
            var transition = new RecordingTransition();
            var service = await CreateServiceAsync(loader, transition);
            var progress = new List<float>();
            int completions = 0;
            service.ProgressChanged += value => progress.Add(value.Progress);
            service.LoadCompleted += result => completions++;

            Task<SceneLoadResult> load = service.LoadAsync(new SceneLoadRequest(ScenePath));
            Assert.That(service.IsLoading, Is.True);
            loader.Operation.Advance(0.5f);
            await Task.Yield();
            loader.Operation.Advance(float.NaN);
            await Task.Yield();
            loader.Operation.Advance(0.2f);
            await Task.Yield();
            loader.Operation.Complete();
            SceneLoadResult result = await load;

            Assert.That(result.ScenePath, Is.EqualTo(ScenePath));
            Assert.That(completions, Is.EqualTo(1));
            Assert.That(service.IsLoading, Is.False);
            Assert.That(loader.Operation.ActivationAllowed, Is.True);
            Assert.That(transition.Calls, Is.EqualTo(new[] { "before", "after" }));
            Assert.That(progress[0], Is.EqualTo(0f));
            Assert.That(progress[progress.Count - 1], Is.EqualTo(1f));
            float previous = -1f;
            foreach (float value in progress)
            {
                Assert.That(float.IsNaN(value) || float.IsInfinity(value), Is.False);
                Assert.That(value, Is.InRange(0f, 1f));
                Assert.That(value, Is.GreaterThan(previous));
                previous = value;
            }

            await service.ShutdownAsync(CancellationToken.None);
        }

        [Test]
        public async Task MissingSceneFailsWithoutStartingTheLoaderOrTransition()
        {
            var loader = new FakeLoader { Available = false };
            var transition = new RecordingTransition();
            var service = await CreateServiceAsync(loader, transition);
            SceneLoadFailure failure = null;
            service.LoadFailed += value => failure = value;

            SceneFlowException exception = await ExpectAsync<SceneFlowException>(
                service.LoadAsync(new SceneLoadRequest(ScenePath)));

            Assert.That(exception.ErrorCode, Is.EqualTo(SceneLoadErrorCode.SceneUnavailable));
            Assert.That(exception.Message, Does.Contain("build profile"));
            Assert.That(failure.ErrorCode, Is.EqualTo(SceneLoadErrorCode.SceneUnavailable));
            Assert.That(loader.BeginCount, Is.Zero);
            Assert.That(transition.Calls, Is.Empty);
            Assert.That(service.IsLoading, Is.False);
            await service.ShutdownAsync(CancellationToken.None);
        }

        [Test]
        public async Task ConcurrentLoadIsRejectedWhileTheFirstOperationRetainsTheGate()
        {
            var loader = new FakeLoader();
            var service = await CreateServiceAsync(loader);
            Task<SceneLoadResult> first = service.LoadAsync(new SceneLoadRequest(ScenePath));
            Task<SceneLoadResult> second = service.LoadAsync(new SceneLoadRequest("AnotherScene"));

            Assert.That(second.IsCompleted, Is.True);
            SceneFlowException exception = await ExpectAsync<SceneFlowException>(second);
            Assert.That(exception.ErrorCode, Is.EqualTo(SceneLoadErrorCode.Busy));
            Assert.That(loader.BeginCount, Is.EqualTo(1));
            Assert.That(service.IsLoading, Is.True);

            loader.Operation.Complete();
            await first;
            Assert.That(service.IsLoading, Is.False);
            await service.ShutdownAsync(CancellationToken.None);
        }

        [Test]
        public async Task CancelledStartedLoadDrainsBeforeReportingFailureOrUnlocking()
        {
            var loader = new FakeLoader();
            var transition = new RecordingTransition();
            var service = await CreateServiceAsync(loader, transition);
            int failures = 0;
            int completions = 0;
            service.LoadFailed += failure => failures++;
            service.LoadCompleted += result => completions++;
            using (var cancellation = new CancellationTokenSource())
            {
                Task<SceneLoadResult> load = service.LoadAsync(new SceneLoadRequest(ScenePath), cancellation.Token);
                cancellation.Cancel();
                loader.Operation.Advance(0.5f);
                await loader.Operation.SecondWaitStarted;

                Assert.That(service.IsLoading, Is.True);
                Assert.That(load.IsCompleted, Is.False);
                Assert.That(failures, Is.Zero);
                Assert.That(loader.Operation.ActivationRequests, Is.EqualTo(2),
                    "Cancellation must enter a separate drain before the engine operation completes.");
                SceneFlowException busy = await ExpectAsync<SceneFlowException>(
                    service.LoadAsync(new SceneLoadRequest("AnotherScene")));
                Assert.That(busy.ErrorCode, Is.EqualTo(SceneLoadErrorCode.Busy));

                loader.Operation.Complete();
                await ExpectAsync<OperationCanceledException>(load);
                Assert.That(service.IsLoading, Is.False);
                Assert.That(completions, Is.Zero);
                // The rejected concurrent request and cancelled accepted request each fail once.
                Assert.That(failures, Is.EqualTo(2));
                Assert.That(transition.Calls, Is.EqualTo(new[] { "before", "recover" }));
                Assert.That(transition.RecoveryToken.CanBeCanceled, Is.False);
            }

            await service.ShutdownAsync(CancellationToken.None);
        }

        [Test]
        public async Task CancellationBeforeStartHasNoLoadingSideEffects()
        {
            var loader = new FakeLoader();
            var transition = new RecordingTransition();
            var service = await CreateServiceAsync(loader, transition);
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                await ExpectAsync<OperationCanceledException>(
                    service.LoadAsync(new SceneLoadRequest(ScenePath), cancellation.Token));
            }

            Assert.That(loader.BeginCount, Is.Zero);
            Assert.That(transition.Calls, Is.Empty);
            Assert.That(service.IsLoading, Is.False);
            await service.ShutdownAsync(CancellationToken.None);
        }

        [Test]
        public async Task LoaderFailureRunsRecoveryAndAllowsAnotherLoad()
        {
            var loader = new FakeLoader { BeginException = new InvalidOperationException("Engine refused load") };
            var transition = new RecordingTransition();
            var service = await CreateServiceAsync(loader, transition);
            SceneFlowException exception = await ExpectAsync<SceneFlowException>(
                service.LoadAsync(new SceneLoadRequest(ScenePath)));

            Assert.That(exception.ErrorCode, Is.EqualTo(SceneLoadErrorCode.LoadingFailed));
            Assert.That(exception.InnerException.Message, Is.EqualTo("Engine refused load"));
            Assert.That(transition.Calls, Is.EqualTo(new[] { "before", "recover" }));
            Assert.That(service.IsLoading, Is.False);

            loader.BeginException = null;
            Task<SceneLoadResult> retry = service.LoadAsync(new SceneLoadRequest(ScenePath));
            loader.Operation.Complete();
            await retry;
            await service.ShutdownAsync(CancellationToken.None);
        }

        [Test]
        public async Task BeforeTransitionFailureRecoversWithoutStartingAnEngineLoad()
        {
            var loader = new FakeLoader();
            var transition = new RecordingTransition { BeforeException = new InvalidOperationException("Prepare failed") };
            var service = await CreateServiceAsync(loader, transition);
            SceneFlowException exception = await ExpectAsync<SceneFlowException>(
                service.LoadAsync(new SceneLoadRequest(ScenePath)));

            Assert.That(exception.ErrorCode, Is.EqualTo(SceneLoadErrorCode.TransitionFailed));
            Assert.That(loader.BeginCount, Is.Zero);
            Assert.That(transition.Calls, Is.EqualTo(new[] { "before", "recover" }));
            Assert.That(service.IsLoading, Is.False);
            await service.ShutdownAsync(CancellationToken.None);
        }

        [Test]
        public async Task RecoveryFailurePreservesBothErrorsAndReleasesTheGate()
        {
            var loader = new FakeLoader();
            var transition = new RecordingTransition
            {
                BeforeException = new InvalidOperationException("Prepare failed"),
                RecoveryException = new InvalidOperationException("Recovery failed")
            };
            var service = await CreateServiceAsync(loader, transition);
            SceneFlowException exception = await ExpectAsync<SceneFlowException>(
                service.LoadAsync(new SceneLoadRequest(ScenePath)));

            var errors = exception.InnerException as AggregateException;
            Assert.That(errors, Is.Not.Null);
            Assert.That(errors.InnerExceptions.Count, Is.EqualTo(2));
            Assert.That(service.IsLoading, Is.False);
            await service.ShutdownAsync(CancellationToken.None);
        }

        [Test]
        public async Task AfterTransitionFailureIsReportedAfterCompletedEngineLoadAndRecovery()
        {
            var loader = new FakeLoader();
            var transition = new RecordingTransition { AfterException = new InvalidOperationException("Reveal failed") };
            var service = await CreateServiceAsync(loader, transition);
            int completions = 0;
            SceneLoadFailure failure = null;
            service.LoadCompleted += result => completions++;
            service.LoadFailed += value => failure = value;
            Task<SceneLoadResult> load = service.LoadAsync(new SceneLoadRequest(ScenePath));
            loader.Operation.Complete();

            SceneFlowException exception = await ExpectAsync<SceneFlowException>(load);
            Assert.That(exception.ErrorCode, Is.EqualTo(SceneLoadErrorCode.TransitionFailed));
            Assert.That(failure.ErrorCode, Is.EqualTo(SceneLoadErrorCode.TransitionFailed));
            Assert.That(transition.Calls, Is.EqualTo(new[] { "before", "after", "recover" }));
            Assert.That(completions, Is.Zero);
            Assert.That(service.IsLoading, Is.False);
            await service.ShutdownAsync(CancellationToken.None);
        }

        [Test]
        public async Task RecoveryRetainsTheExclusiveGateUntilItsHookFinishes()
        {
            var loader = new FakeLoader();
            var recovery = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var transition = new RecordingTransition
            {
                BeforeException = new InvalidOperationException("Prepare failed"),
                RecoveryTask = recovery.Task
            };
            var service = await CreateServiceAsync(loader, transition);
            Task<SceneLoadResult> load = service.LoadAsync(new SceneLoadRequest(ScenePath));

            Assert.That(transition.Calls, Is.EqualTo(new[] { "before", "recover" }));
            Assert.That(service.IsLoading, Is.True);
            SceneFlowException busy = await ExpectAsync<SceneFlowException>(
                service.LoadAsync(new SceneLoadRequest("AnotherScene")));
            Assert.That(busy.ErrorCode, Is.EqualTo(SceneLoadErrorCode.Busy));
            recovery.SetResult(true);
            await ExpectAsync<SceneFlowException>(load);
            Assert.That(service.IsLoading, Is.False);
            await service.ShutdownAsync(CancellationToken.None);
        }

        [Test]
        public async Task RequestTransitionOverridesDefaultTransition()
        {
            var loader = new FakeLoader();
            var defaultTransition = new RecordingTransition();
            var overrideTransition = new RecordingTransition();
            var service = await CreateServiceAsync(loader, defaultTransition);
            Task<SceneLoadResult> load = service.LoadAsync(new SceneLoadRequest(ScenePath,
                transition: overrideTransition));
            loader.Operation.Complete();
            await load;

            Assert.That(defaultTransition.Calls, Is.Empty);
            Assert.That(overrideTransition.Calls, Is.EqualTo(new[] { "before", "after" }));
            await service.ShutdownAsync(CancellationToken.None);
        }

        [Test]
        public async Task ObserverExceptionsCannotTurnSuccessfulNavigationIntoFailure()
        {
            var loader = new FakeLoader();
            var service = await CreateServiceAsync(loader);
            var observerErrors = new List<Exception>();
            int successfulObservers = 0;
            service.ObserverFailed += exception => observerErrors.Add(exception);
            service.ProgressChanged += progress => throw new InvalidOperationException("Progress observer failed");
            service.LoadCompleted += result => throw new InvalidOperationException("Completion observer failed");
            service.LoadCompleted += result => successfulObservers++;

            Task<SceneLoadResult> load = service.LoadAsync(new SceneLoadRequest(ScenePath));
            loader.Operation.Complete();
            SceneLoadResult result = await load;

            Assert.That(result.ScenePath, Is.EqualTo(ScenePath));
            Assert.That(successfulObservers, Is.EqualTo(1));
            Assert.That(observerErrors.Count, Is.GreaterThanOrEqualTo(2));
            Assert.That(service.IsLoading, Is.False);
            await service.ShutdownAsync(CancellationToken.None);
        }

        [Test]
        public async Task ShutdownWaitsForStartedLoadAndRejectsRestartUntilDrainFinishes()
        {
            var loader = new FakeLoader();
            var service = await CreateServiceAsync(loader);
            Task<SceneLoadResult> load = service.LoadAsync(new SceneLoadRequest(ScenePath));
            Task shutdown = service.ShutdownAsync(CancellationToken.None);
            Task repeatedShutdown = service.ShutdownAsync(CancellationToken.None);

            Assert.That(repeatedShutdown, Is.SameAs(shutdown));
            using (var cancelledCaller = new CancellationTokenSource())
            {
                cancelledCaller.Cancel();
                Assert.That(service.ShutdownAsync(cancelledCaller.Token), Is.SameAs(shutdown));
            }
            Assert.That(shutdown.IsCompleted, Is.False);
            Assert.That(service.IsLoading, Is.True);
            SceneFlowException restart = Assert.Throws<SceneFlowException>(
                () => service.InitializeAsync(null, CancellationToken.None));
            Assert.That(restart.ErrorCode, Is.EqualTo(SceneLoadErrorCode.ServiceStopping));
            SceneFlowException stoppedLoad = await ExpectAsync<SceneFlowException>(
                service.LoadAsync(new SceneLoadRequest("AnotherScene")));
            Assert.That(stoppedLoad.ErrorCode, Is.EqualTo(SceneLoadErrorCode.ServiceStopping));

            loader.Operation.Complete();
            await ExpectAsync<OperationCanceledException>(load);
            await shutdown;
            Assert.That(service.IsLoading, Is.False);
            SceneFlowException uninitialized = await ExpectAsync<SceneFlowException>(
                service.LoadAsync(new SceneLoadRequest(ScenePath)));
            Assert.That(uninitialized.ErrorCode, Is.EqualTo(SceneLoadErrorCode.NotInitialized));

            await service.InitializeAsync(null, CancellationToken.None);
            loader.Operation = new FakeOperation();
            Task<SceneLoadResult> next = service.LoadAsync(new SceneLoadRequest(ScenePath));
            loader.Operation.Complete();
            await next;
            await service.ShutdownAsync(CancellationToken.None);
        }

        [Test]
        public async Task ThrowingCancellationCallbackStillDrainsAndCompletesSharedShutdown()
        {
            var loader = new FakeLoader();
            var transition = new RecordingTransition { ThrowOnCancellation = true };
            var service = await CreateServiceAsync(loader, transition);
            Task<SceneLoadResult> load = service.LoadAsync(new SceneLoadRequest(ScenePath));
            Task shutdown = service.ShutdownAsync(CancellationToken.None);

            Assert.That(service.ShutdownAsync(CancellationToken.None), Is.SameAs(shutdown));
            Assert.That(shutdown.IsCompleted, Is.False);
            loader.Operation.Advance(0.4f);
            await loader.Operation.SecondWaitStarted;
            Assert.That(service.IsLoading, Is.True);
            Assert.That(shutdown.IsCompleted, Is.False);
            loader.Operation.Complete();
            await ExpectAsync<OperationCanceledException>(load);
            AggregateException exception = await ExpectAsync<AggregateException>(shutdown);
            IReadOnlyList<Exception> failures = exception.Flatten().InnerExceptions;
            Assert.That(failures.Count, Is.EqualTo(1));
            Assert.That(failures[0], Is.TypeOf<InvalidOperationException>());
            Assert.That(failures[0].Message, Is.EqualTo("Cancellation callback failed"));
            Assert.That(service.IsLoading, Is.False);

            await service.InitializeAsync(null, CancellationToken.None);
            transition.ThrowOnCancellation = false;
            await service.ShutdownAsync(CancellationToken.None);
        }

        [Test]
        public async Task AnAlreadyCancelledShutdownTokenDoesNotSkipCleanup()
        {
            var service = await CreateServiceAsync(new FakeLoader());
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                await service.ShutdownAsync(cancellation.Token);
            }

            SceneFlowException exception = await ExpectAsync<SceneFlowException>(
                service.LoadAsync(new SceneLoadRequest(ScenePath)));
            Assert.That(exception.ErrorCode, Is.EqualTo(SceneLoadErrorCode.NotInitialized));
        }

        [Test]
        public async Task FailureHandlerCanRetryWithoutRecursiveNotifications()
        {
            var loader = new FakeLoader { Available = false };
            var service = await CreateServiceAsync(loader);
            int notifications = 0;
            Task<SceneLoadResult> fallback = null;
            service.LoadFailed += failure =>
            {
                notifications++;
                Assert.That(service.IsLoading, Is.False);
                fallback = service.LoadAsync(new SceneLoadRequest("AlsoUnavailable"));
            };

            await ExpectAsync<SceneFlowException>(service.LoadAsync(new SceneLoadRequest(ScenePath)));
            Assert.That(notifications, Is.EqualTo(1));
            Assert.That(fallback, Is.Not.Null);
            await ExpectAsync<SceneFlowException>(fallback);
            Assert.That(service.IsLoading, Is.False);
            await service.ShutdownAsync(CancellationToken.None);
        }

        [Test]
        public async Task BusyFailureHandlerCannotRecursivelyOverflowOnAnotherRejectedLoad()
        {
            var loader = new FakeLoader();
            var service = await CreateServiceAsync(loader);
            int notifications = 0;
            Task<SceneLoadResult> fallback = null;
            service.LoadFailed += failure =>
            {
                notifications++;
                fallback = service.LoadAsync(new SceneLoadRequest("Fallback"));
            };

            Task<SceneLoadResult> accepted = service.LoadAsync(new SceneLoadRequest(ScenePath));
            SceneFlowException busy = await ExpectAsync<SceneFlowException>(
                service.LoadAsync(new SceneLoadRequest("Rejected")));
            Assert.That(busy.ErrorCode, Is.EqualTo(SceneLoadErrorCode.Busy));
            Assert.That(notifications, Is.EqualTo(1));
            await ExpectAsync<SceneFlowException>(fallback);
            loader.Operation.Complete();
            await accepted;
            await service.ShutdownAsync(CancellationToken.None);
        }

        [Test]
        public async Task CompletionHandlerCanStartTheNextLoadWithoutThePreviousFinallyClearingItsGate()
        {
            var loader = new FakeLoader();
            var service = await CreateServiceAsync(loader);
            Task<SceneLoadResult> next = null;
            int completions = 0;
            service.LoadCompleted += result =>
            {
                completions++;
                if (completions == 1)
                {
                    Assert.That(service.IsLoading, Is.False);
                    loader.Operation = new FakeOperation();
                    next = service.LoadAsync(new SceneLoadRequest("NextScene"));
                }
            };

            Task<SceneLoadResult> first = service.LoadAsync(new SceneLoadRequest(ScenePath));
            loader.Operation.Complete();
            await first;
            Assert.That(next, Is.Not.Null);
            Assert.That(service.IsLoading, Is.True);
            loader.Operation.Complete();
            await next;
            Assert.That(completions, Is.EqualTo(2));
            Assert.That(service.IsLoading, Is.False);
            await service.ShutdownAsync(CancellationToken.None);
        }

        [Test]
        public async Task LoadingRequiresInitializationAndRepeatedInitializationIsHarmless()
        {
            var loader = new FakeLoader();
            var service = new SceneFlowService(loader);
            SceneFlowException exception = await ExpectAsync<SceneFlowException>(
                service.LoadAsync(new SceneLoadRequest(ScenePath)));
            Assert.That(exception.ErrorCode, Is.EqualTo(SceneLoadErrorCode.NotInitialized));
            await service.InitializeAsync(null, CancellationToken.None);
            await service.InitializeAsync(null, CancellationToken.None);
            Assert.That(service.Id, Is.EqualTo("scene-flow"));
            Assert.That(service.Dependencies, Is.Empty);

            Task<SceneLoadResult> load = service.LoadAsync(new SceneLoadRequest(ScenePath));
            loader.Operation.Complete();
            await load;
            Assert.That(loader.BeginCount, Is.EqualTo(1));
            await service.ShutdownAsync(CancellationToken.None);
        }

        private static async Task<SceneFlowService> CreateServiceAsync(FakeLoader loader,
            ISceneTransition transition = null)
        {
            var service = new SceneFlowService(loader, transition);
            await service.InitializeAsync(null, CancellationToken.None);
            return service;
        }

        private static async Task<T> ExpectAsync<T>(Task task) where T : Exception
        {
            try
            {
                await task;
            }
            catch (T exception)
            {
                return exception;
            }

            Assert.Fail($"Expected {typeof(T).Name}.");
            return null;
        }

        private sealed class FakeLoader : ISceneLoader
        {
            public bool Available = true;
            public Exception BeginException;
            public int BeginCount;
            public FakeOperation Operation = new FakeOperation();

            public bool CanLoadScene(string scenePath)
            {
                return Available;
            }

            public ISceneLoadOperation BeginLoad(SceneLoadRequest request)
            {
                BeginCount++;
                if (BeginException != null)
                {
                    throw BeginException;
                }

                return Operation;
            }
        }

        private sealed class FakeOperation : ISceneLoadOperation
        {
            private TaskCompletionSource<bool> _nextFrame = NewFrame();
            private readonly TaskCompletionSource<bool> _secondWaitStarted = NewFrame();
            private int _waitCount;
            public float Progress { get; private set; }
            public bool IsDone { get; private set; }
            public bool ActivationAllowed { get; private set; }
            public int ActivationRequests { get; private set; }
            public Task SecondWaitStarted => _secondWaitStarted.Task;

            public void AllowActivation()
            {
                ActivationAllowed = true;
                ActivationRequests++;
            }

            public Task WaitForNextFrameAsync()
            {
                _waitCount++;
                if (_waitCount == 2)
                {
                    _secondWaitStarted.TrySetResult(true);
                }

                return _nextFrame.Task;
            }

            public void Advance(float progress)
            {
                Progress = progress;
                TaskCompletionSource<bool> previous = _nextFrame;
                _nextFrame = NewFrame();
                previous.TrySetResult(true);
            }

            public void Complete()
            {
                IsDone = true;
                Advance(1f);
            }

            private static TaskCompletionSource<bool> NewFrame()
            {
                return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        private sealed class RecordingTransition : ISceneTransition
        {
            public readonly List<string> Calls = new List<string>();
            public Exception BeforeException;
            public Exception AfterException;
            public Exception RecoveryException;
            public Task RecoveryTask;
            public CancellationToken RecoveryToken;
            public bool ThrowOnCancellation;

            public Task BeforeLoadAsync(SceneLoadRequest request, CancellationToken cancellationToken)
            {
                Calls.Add("before");
                cancellationToken.ThrowIfCancellationRequested();
                if (ThrowOnCancellation)
                {
                    cancellationToken.Register(() =>
                        throw new InvalidOperationException("Cancellation callback failed"));
                }

                return BeforeException == null ? Task.CompletedTask : Task.FromException(BeforeException);
            }

            public Task AfterLoadAsync(SceneLoadResult result, CancellationToken cancellationToken)
            {
                Calls.Add("after");
                cancellationToken.ThrowIfCancellationRequested();
                return AfterException == null ? Task.CompletedTask : Task.FromException(AfterException);
            }

            public Task RecoverAsync(SceneLoadRequest request, Exception exception, CancellationToken cancellationToken)
            {
                Calls.Add("recover");
                RecoveryToken = cancellationToken;
                return RecoveryException == null ? RecoveryTask ?? Task.CompletedTask : Task.FromException(RecoveryException);
            }
        }
    }
}
