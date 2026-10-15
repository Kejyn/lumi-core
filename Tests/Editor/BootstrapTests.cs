using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lumivale.Core.Bootstrap;
using NUnit.Framework;

namespace Lumivale.Tests.EditMode
{
    public sealed class BootstrapTests
    {
        [Test]
        public async Task InitializesDependenciesDeterministicallyAndStopsInReverseOrder()
        {
            var calls = new List<string>();
            var dependent = new TestService("dependent", calls, "dependency");
            var independent = new TestService("independent", calls);
            var dependency = new TestService("dependency", calls);
            var coordinator = new BootstrapCoordinator(new ILumiService[]
            {
                dependent, independent, dependency
            });

            await coordinator.InitializeAsync();
            Assert.That(coordinator.State, Is.EqualTo(BootstrapState.Running));
            CollectionAssert.AreEqual(new[]
            {
                "init:independent", "init:dependency", "init:dependent"
            }, calls);

            await coordinator.ShutdownAsync();
            Assert.That(coordinator.State, Is.EqualTo(BootstrapState.Stopped));
            CollectionAssert.AreEqual(new[]
            {
                "init:independent", "init:dependency", "init:dependent",
                "stop:dependent", "stop:dependency", "stop:independent"
            }, calls);
        }

        [Test]
        public async Task ConcurrentAndRepeatedInitializationSharesOneStartupTask()
        {
            var calls = new List<string>();
            var release = NewCompletion();
            var service = new TestService("service", calls)
            {
                Initialize = (context, token) => release.Task
            };
            var coordinator = new BootstrapCoordinator(new[] { service });

            Task first = coordinator.InitializeAsync();
            Task second = coordinator.InitializeAsync();
            Assert.That(second, Is.SameAs(first));
            Assert.That(coordinator.State, Is.EqualTo(BootstrapState.Initializing));
            CollectionAssert.AreEqual(new[] { "init:service" }, calls);

            release.SetResult(null);
            await WithDeadline(first);
            Assert.That(coordinator.InitializeAsync(), Is.SameAs(first));
            await coordinator.ShutdownAsync();
            CollectionAssert.AreEqual(new[] { "init:service", "stop:service" }, calls);
        }

        [Test]
        public void MissingDependenciesAreRejectedBeforeAnyServiceStarts()
        {
            var calls = new List<string>();
            var coordinator = new BootstrapCoordinator(new[]
            {
                new TestService("independent", calls),
                new TestService("dependent", calls, "missing")
            });

            InvalidOperationException exception = Assert.ThrowsAsync<InvalidOperationException>(
                async () => await coordinator.InitializeAsync());
            StringAssert.Contains("missing", exception.Message);
            Assert.That(calls, Is.Empty);
            Assert.That(coordinator.State, Is.EqualTo(BootstrapState.Faulted));
        }

        [Test]
        public void DuplicateIdsAreRejectedBeforeAnyServiceStarts()
        {
            var calls = new List<string>();
            var coordinator = new BootstrapCoordinator(new[]
            {
                new TestService("duplicate", calls),
                new TestService("duplicate", calls)
            });

            Assert.ThrowsAsync<InvalidOperationException>(async () => await coordinator.InitializeAsync());
            Assert.That(calls, Is.Empty);
        }

        [Test]
        public void DependencyCyclesAreRejectedBeforeAnyServiceStarts()
        {
            var calls = new List<string>();
            var coordinator = new BootstrapCoordinator(new[]
            {
                new TestService("independent", calls),
                new TestService("a", calls, "b"),
                new TestService("b", calls, "a")
            });

            InvalidOperationException exception = Assert.ThrowsAsync<InvalidOperationException>(
                async () => await coordinator.InitializeAsync());
            StringAssert.Contains("cycle", exception.Message);
            Assert.That(calls, Is.Empty);
        }

        [Test]
        public void SelfDependencyIsRejectedBeforeAnyServiceStarts()
        {
            var calls = new List<string>();
            var coordinator = new BootstrapCoordinator(new[] { new TestService("self", calls, "self") });

            Assert.ThrowsAsync<InvalidOperationException>(async () => await coordinator.InitializeAsync());
            Assert.That(calls, Is.Empty);
        }

        [Test]
        public void FailedInitializationRollsBackTheFailingServiceAndItsDependencies()
        {
            var calls = new List<string>();
            var failing = new TestService("failing", calls, "dependency")
            {
                Initialize = (context, token) => Task.FromException(new Exception("startup failed"))
            };
            var coordinator = new BootstrapCoordinator(new[]
            {
                new TestService("dependency", calls), failing,
                new TestService("unreached", calls, "failing")
            });

            InvalidOperationException exception = Assert.ThrowsAsync<InvalidOperationException>(
                async () => await coordinator.InitializeAsync());
            StringAssert.Contains("failing", exception.Message);
            CollectionAssert.AreEqual(new[]
            {
                "init:dependency", "init:failing", "stop:failing", "stop:dependency"
            }, calls);
            Assert.That(coordinator.State, Is.EqualTo(BootstrapState.Faulted));
        }

        [Test]
        public void RollbackFailuresAreAggregatedWithTheStartupFailure()
        {
            var calls = new List<string>();
            var failing = new TestService("failing", calls, "dependency")
            {
                Initialize = (context, token) => Task.FromException(new Exception("startup failed")),
                Shutdown = token => Task.FromException(new Exception("rollback failed"))
            };
            var coordinator = new BootstrapCoordinator(new[]
            {
                new TestService("dependency", calls), failing
            });

            AggregateException exception = Assert.ThrowsAsync<AggregateException>(
                async () => await coordinator.InitializeAsync());
            Assert.That(exception.InnerExceptions, Has.Count.EqualTo(2));
            CollectionAssert.AreEqual(new[]
            {
                "init:dependency", "init:failing", "stop:failing", "stop:dependency"
            }, calls);
        }

        [Test]
        public async Task ShutdownContinuesAfterFailuresAndIsNeverRepeated()
        {
            var calls = new List<string>();
            var failing = new TestService("failing", calls, "dependency")
            {
                Shutdown = token => Task.FromException(new Exception("shutdown failed"))
            };
            var coordinator = new BootstrapCoordinator(new[]
            {
                new TestService("dependency", calls)
                {
                    Shutdown = token => Task.FromException(new Exception("dependency shutdown failed"))
                },
                failing
            });
            await coordinator.InitializeAsync();

            Task shutdown = coordinator.ShutdownAsync();
            AggregateException exception = Assert.ThrowsAsync<AggregateException>(async () => await shutdown);
            Assert.That(exception.InnerExceptions, Has.Count.EqualTo(2));
            Assert.That(coordinator.ShutdownAsync(), Is.SameAs(shutdown));
            Assert.That(coordinator.State, Is.EqualTo(BootstrapState.Faulted));
            CollectionAssert.AreEqual(new[]
            {
                "init:dependency", "init:failing", "stop:failing", "stop:dependency"
            }, calls);
        }

        [Test]
        public async Task CancelingStartupRollsBackWithANonCanceledCleanupToken()
        {
            var calls = new List<string>();
            using (var cancellation = new CancellationTokenSource())
            {
                bool cleanupTokenWasCanceled = true;
                var service = new TestService("service", calls)
                {
                    Initialize = (context, token) => WaitForCancellation(token),
                    Shutdown = token =>
                    {
                        cleanupTokenWasCanceled = token.IsCancellationRequested;
                        return Task.CompletedTask;
                    }
                };
                var coordinator = new BootstrapCoordinator(new[] { service });
                Task startup = coordinator.InitializeAsync(cancellation.Token);
                cancellation.Cancel();

                await ExpectCancellation(startup);
                Assert.That(cleanupTokenWasCanceled, Is.False);
                Assert.That(coordinator.State, Is.EqualTo(BootstrapState.Faulted));
                CollectionAssert.AreEqual(new[] { "init:service", "stop:service" }, calls);
            }
        }

        [Test]
        public async Task ShutdownDuringStartupCancelsAndWaitsForRollback()
        {
            var calls = new List<string>();
            var cleanupRelease = NewCompletion();
            var cleanupEntered = NewCompletion();
            var service = new TestService("service", calls)
            {
                Initialize = (context, token) => WaitForCancellation(token),
                Shutdown = token =>
                {
                    cleanupEntered.TrySetResult(null);
                    return cleanupRelease.Task;
                }
            };
            var coordinator = new BootstrapCoordinator(new[] { service });
            Task startup = coordinator.InitializeAsync();
            Task shutdown = coordinator.ShutdownAsync();

            await WithDeadline(cleanupEntered.Task);
            Assert.That(coordinator.State, Is.EqualTo(BootstrapState.Stopping));
            Assert.That(shutdown.IsCompleted, Is.False);
            Assert.That(coordinator.ShutdownAsync(), Is.SameAs(shutdown));
            Assert.ThrowsAsync<InvalidOperationException>(async () => await coordinator.InitializeAsync());

            cleanupRelease.SetResult(null);
            await ExpectCancellation(startup);
            await WithDeadline(shutdown);
            Assert.That(coordinator.State, Is.EqualTo(BootstrapState.Stopped));
            CollectionAssert.AreEqual(new[] { "init:service", "stop:service" }, calls);
        }

        [Test]
        public async Task StoppedCoordinatorCannotRestart()
        {
            var calls = new List<string>();
            var coordinator = new BootstrapCoordinator(new[] { new TestService("service", calls) });
            await coordinator.InitializeAsync();
            await coordinator.ShutdownAsync();

            Assert.ThrowsAsync<InvalidOperationException>(async () => await coordinator.InitializeAsync());
            CollectionAssert.AreEqual(new[] { "init:service", "stop:service" }, calls);
        }

        [Test]
        public async Task ShutdownBeforeStartupHasNoServiceSideEffects()
        {
            var calls = new List<string>();
            var coordinator = new BootstrapCoordinator(new[] { new TestService("service", calls) });
            await coordinator.ShutdownAsync();

            Assert.That(coordinator.State, Is.EqualTo(BootstrapState.Stopped));
            Assert.ThrowsAsync<InvalidOperationException>(async () => await coordinator.InitializeAsync());
            Assert.That(calls, Is.Empty);
        }

        [Test]
        public async Task PreCanceledShutdownDoesNotCommitAndCommittedCleanupCannotBeCanceled()
        {
            var calls = new List<string>();
            var cleanupRelease = NewCompletion();
            bool cleanupTokenWasCanceled = true;
            var service = new TestService("service", calls)
            {
                Shutdown = token =>
                {
                    cleanupTokenWasCanceled = token.IsCancellationRequested;
                    return cleanupRelease.Task;
                }
            };
            var coordinator = new BootstrapCoordinator(new[] { service });
            await coordinator.InitializeAsync();

            using (var alreadyCanceled = new CancellationTokenSource())
            {
                alreadyCanceled.Cancel();
                await ExpectCancellation(coordinator.ShutdownAsync(alreadyCanceled.Token));
            }

            Assert.That(coordinator.State, Is.EqualTo(BootstrapState.Running));
            CollectionAssert.AreEqual(new[] { "init:service" }, calls);
            using (var cancellation = new CancellationTokenSource())
            {
                Task shutdown = coordinator.ShutdownAsync(cancellation.Token);
                cancellation.Cancel();
                Assert.That(cleanupTokenWasCanceled, Is.False);
                Assert.That(shutdown.IsCompleted, Is.False);
                cleanupRelease.SetResult(null);
                await WithDeadline(shutdown);
            }

            Assert.That(coordinator.State, Is.EqualTo(BootstrapState.Stopped));
            CollectionAssert.AreEqual(new[] { "init:service", "stop:service" }, calls);
        }

        [Test]
        public async Task ContextResolvesRegisteredTypedDependencies()
        {
            var calls = new List<string>();
            var dependency = new DependencyService(calls);
            var dependent = new TestService("dependent", calls, "dependency")
            {
                Initialize = (context, token) =>
                {
                    Assert.That(context.Get<DependencyService>(), Is.SameAs(dependency));
                    return Task.CompletedTask;
                }
            };
            var coordinator = new BootstrapCoordinator(new ILumiService[] { dependent, dependency });

            await coordinator.InitializeAsync();
            await coordinator.ShutdownAsync();
        }

        [Test]
        public void ContextRejectsMissingAndAmbiguousServiceTypes()
        {
            var calls = new List<string>();
            var coordinator = new BootstrapCoordinator(new[]
            {
                new TestService("a", calls), new TestService("b", calls)
            });

            Assert.Throws<InvalidOperationException>(() => coordinator.Context.Get<DependencyService>());
            Assert.Throws<InvalidOperationException>(() => coordinator.Context.Get<TestService>());
        }

        private static TaskCompletionSource<object> NewCompletion()
        {
            return new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private static async Task WaitForCancellation(CancellationToken cancellationToken)
        {
            var canceled = NewCompletion();
            using (cancellationToken.Register(() => canceled.TrySetCanceled()))
            {
                await canceled.Task;
            }
        }

        private static async Task ExpectCancellation(Task task)
        {
            try
            {
                await WithDeadline(task);
                Assert.Fail("The task should have been canceled.");
            }
            catch (OperationCanceledException)
            {
                Assert.That(task.IsCanceled, Is.True);
            }
        }

        private static async Task WithDeadline(Task task)
        {
            Task finished = await Task.WhenAny(task, Task.Delay(5000));
            Assert.That(finished, Is.SameAs(task), "The asynchronous lifecycle did not finish within five seconds.");
            await task;
        }

        private class TestService : ILumiService
        {
            private readonly IList<string> calls;

            public TestService(string id, IList<string> calls, params string[] dependencies)
            {
                Id = id;
                this.calls = calls;
                Dependencies = dependencies;
            }

            public string Id { get; }
            public IReadOnlyCollection<string> Dependencies { get; }
            public Func<ServiceContext, CancellationToken, Task> Initialize { get; set; }
            public Func<CancellationToken, Task> Shutdown { get; set; }

            public Task InitializeAsync(ServiceContext context, CancellationToken cancellationToken)
            {
                calls.Add($"init:{Id}");
                return Initialize == null ? Task.CompletedTask : Initialize(context, cancellationToken);
            }

            public Task ShutdownAsync(CancellationToken cancellationToken)
            {
                calls.Add($"stop:{Id}");
                return Shutdown == null ? Task.CompletedTask : Shutdown(cancellationToken);
            }
        }

        private sealed class DependencyService : TestService
        {
            public DependencyService(IList<string> calls) : base("dependency", calls)
            {
            }
        }
    }
}
