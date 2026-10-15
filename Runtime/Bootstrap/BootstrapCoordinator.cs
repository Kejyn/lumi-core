using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Lumivale.Core.Bootstrap
{
    /// <summary>
    /// Owns one service lifecycle. Startup follows dependencies, and shutdown runs in reverse.
    /// All service calls retain the caller's synchronization context, including Unity's main thread.
    /// </summary>
    public sealed class BootstrapCoordinator
    {
        private readonly object gate = new object();
        private readonly IReadOnlyList<ILumiService> services;
        private readonly List<Registration> started = new List<Registration>();
        private readonly List<Exception> rollbackFailures = new List<Exception>();
        private BootstrapState state = BootstrapState.Created;
        private CancellationTokenSource initializationCancellation;
        private Task initializationTask;
        private Task shutdownTask;
        private bool stopRequested;

        public BootstrapCoordinator(IEnumerable<ILumiService> services)
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            var registrations = new List<ILumiService>();
            foreach (ILumiService service in services)
            {
                if (service == null)
                {
                    throw new ArgumentException("A Lumi service registration cannot be null.", nameof(services));
                }

                registrations.Add(service);
            }

            this.services = registrations.AsReadOnly();
            Context = new ServiceContext(this.services);
        }

        public ServiceContext Context { get; }

        public BootstrapState State
        {
            get
            {
                lock (gate)
                {
                    return state;
                }
            }
        }

        /// <summary>
        /// Starts services once. Concurrent startup requests share the same task and the first
        /// request's cancellation token. A stopped or faulted coordinator cannot be restarted.
        /// </summary>
        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            TaskCompletionSource<object> completion;
            CancellationTokenSource cancellation;
            lock (gate)
            {
                if (state == BootstrapState.Initializing || state == BootstrapState.Running)
                {
                    return initializationTask;
                }

                if (state != BootstrapState.Created)
                {
                    return Task.FromException(new InvalidOperationException(
                        $"A Lumi bootstrap in state '{state}' cannot be initialized again."));
                }

                completion = NewCompletion();
                cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                initializationCancellation = cancellation;
                initializationTask = completion.Task;
                state = BootstrapState.Initializing;
            }

            _ = InitializeAndCompleteAsync(completion, cancellation);
            return completion.Task;
        }

        /// <summary>
        /// Stops services once. If startup is running, it is canceled and its rollback is awaited
        /// before shutdown proceeds. Cancellation is honored before shutdown commits; after that,
        /// cleanup uses a non-canceled token and continues even when a service throws.
        /// </summary>
        public Task ShutdownAsync(CancellationToken cancellationToken = default)
        {
            TaskCompletionSource<object> completion;
            Task startup;
            CancellationTokenSource cancellation;
            lock (gate)
            {
                if (shutdownTask != null)
                {
                    return shutdownTask;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return Task.FromCanceled(cancellationToken);
                }

                completion = NewCompletion();
                shutdownTask = completion.Task;
                startup = initializationTask;
                cancellation = initializationCancellation;
                stopRequested = true;
                state = BootstrapState.Stopping;
            }

            Exception cancellationFailure = null;
            try
            {
                cancellation?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Startup completed and disposed its linked source after the lock was released.
            }
            catch (Exception exception)
            {
                cancellationFailure = exception;
            }

            _ = ShutdownAndCompleteAsync(completion, startup, cancellationFailure);
            return completion.Task;
        }

        private async Task InitializeAndCompleteAsync(
            TaskCompletionSource<object> completion, CancellationTokenSource cancellation)
        {
            CancellationToken token = cancellation.Token;
            try
            {
                List<Registration> order = BuildInitializationOrder();
                try
                {
                    foreach (Registration registration in order)
                    {
                        token.ThrowIfCancellationRequested();
                        // Include a partially initialized service in rollback.
                        started.Add(registration);
                        try
                        {
                            await registration.Service.InitializeAsync(Context, token);
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception exception)
                        {
                            throw new InvalidOperationException(
                                $"Lumi service '{registration.Id}' failed to initialize.", exception);
                        }
                    }

                    token.ThrowIfCancellationRequested();
                    lock (gate)
                    {
                        if (stopRequested)
                        {
                            throw new OperationCanceledException(token);
                        }

                        state = BootstrapState.Running;
                    }
                }
                catch (Exception startupFailure)
                {
                    // A canceled startup still needs a full attempt at cleanup.
                    List<Exception> failures = await StopStartedServicesAsync(CancellationToken.None);
                    if (failures.Count > 0)
                    {
                        rollbackFailures.AddRange(failures);
                        failures.Insert(0, startupFailure);
                        throw new AggregateException("Lumi startup and rollback failed.", failures);
                    }

                    throw;
                }

                completion.TrySetResult(null);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                MarkInitializationFaulted();
                completion.TrySetCanceled(token);
            }
            catch (Exception exception)
            {
                MarkInitializationFaulted();
                completion.TrySetException(exception);
            }
            finally
            {
                lock (gate)
                {
                    initializationCancellation = null;
                }

                cancellation.Dispose();
            }
        }

        private async Task ShutdownAndCompleteAsync(
            TaskCompletionSource<object> completion, Task startup, Exception cancellationFailure)
        {
            try
            {
                if (startup != null)
                {
                    try
                    {
                        await startup;
                    }
                    catch (Exception)
                    {
                        // The startup task reports its error to startup callers; cleanup continues.
                    }
                }

                var failures = new List<Exception>(rollbackFailures);
                if (cancellationFailure != null)
                {
                    failures.Add(cancellationFailure);
                }

                failures.AddRange(await StopStartedServicesAsync(CancellationToken.None));
                if (failures.Count > 0)
                {
                    throw new AggregateException("One or more Lumi services failed to shut down.", failures);
                }

                lock (gate)
                {
                    state = BootstrapState.Stopped;
                }

                completion.TrySetResult(null);
            }
            catch (Exception exception)
            {
                lock (gate)
                {
                    state = BootstrapState.Faulted;
                }

                completion.TrySetException(exception);
            }
        }

        private async Task<List<Exception>> StopStartedServicesAsync(CancellationToken cancellationToken)
        {
            var failures = new List<Exception>();
            for (int index = started.Count - 1; index >= 0; index--)
            {
                Registration registration = started[index];
                try
                {
                    await registration.Service.ShutdownAsync(cancellationToken);
                }
                catch (Exception exception)
                {
                    failures.Add(new InvalidOperationException(
                        $"Lumi service '{registration.Id}' failed to shut down.", exception));
                }
            }

            started.Clear();
            return failures;
        }

        private List<Registration> BuildInitializationOrder()
        {
            var registrations = new List<Registration>(services.Count);
            var byId = new Dictionary<string, Registration>(StringComparer.Ordinal);
            foreach (ILumiService service in services)
            {
                string id = service.Id;
                if (string.IsNullOrWhiteSpace(id))
                {
                    throw new InvalidOperationException("A Lumi service must have a non-empty Id.");
                }

                if (byId.ContainsKey(id))
                {
                    throw new InvalidOperationException($"Duplicate Lumi service Id '{id}'.");
                }

                if (service.Dependencies == null)
                {
                    throw new InvalidOperationException($"Lumi service '{id}' has a null dependency collection.");
                }

                var dependencies = new HashSet<string>(StringComparer.Ordinal);
                foreach (string dependency in service.Dependencies)
                {
                    if (string.IsNullOrWhiteSpace(dependency))
                    {
                        throw new InvalidOperationException($"Lumi service '{id}' has an empty dependency Id.");
                    }

                    if (dependency == id)
                    {
                        throw new InvalidOperationException($"Lumi service '{id}' depends on itself.");
                    }

                    dependencies.Add(dependency);
                }

                var registration = new Registration(service, id, dependencies);
                registrations.Add(registration);
                byId.Add(id, registration);
            }

            foreach (Registration registration in registrations)
            {
                foreach (string dependency in registration.Dependencies)
                {
                    if (!byId.ContainsKey(dependency))
                    {
                        throw new InvalidOperationException(
                            $"Lumi service '{registration.Id}' requires missing service '{dependency}'.");
                    }
                }
            }

            var order = new List<Registration>(registrations.Count);
            var initializedIds = new HashSet<string>(StringComparer.Ordinal);
            while (registrations.Count > 0)
            {
                int next = registrations.FindIndex(registration =>
                    registration.Dependencies.IsSubsetOf(initializedIds));
                if (next < 0)
                {
                    var remainingIds = new List<string>();
                    foreach (Registration registration in registrations)
                    {
                        remainingIds.Add(registration.Id);
                    }

                    throw new InvalidOperationException(
                        $"Lumi service dependency cycle detected among: {string.Join(", ", remainingIds)}.");
                }

                Registration selected = registrations[next];
                registrations.RemoveAt(next);
                order.Add(selected);
                initializedIds.Add(selected.Id);
            }

            return order;
        }

        private void MarkInitializationFaulted()
        {
            lock (gate)
            {
                if (!stopRequested)
                {
                    state = BootstrapState.Faulted;
                }
            }
        }

        private static TaskCompletionSource<object> NewCompletion()
        {
            return new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private sealed class Registration
        {
            public Registration(ILumiService service, string id, HashSet<string> dependencies)
            {
                Service = service;
                Id = id;
                Dependencies = dependencies;
            }

            public ILumiService Service { get; }
            public string Id { get; }
            public HashSet<string> Dependencies { get; }
        }
    }
}
