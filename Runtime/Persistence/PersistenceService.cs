using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lumivale.Core.Bootstrap;

namespace Lumivale.Core.Persistence
{
    /// <summary>
    /// Instance-owned gameplay saves. Public calls, serialization and game validation retain the
    /// constructing main thread; immutable snapshots and native I/O run on workers. Accepted
    /// operations share one cancellable gate; shutdown cancels queued/precommit work and drains
    /// committed writes before releasing directory ownership. Create a new instance after shutdown.
    /// </summary>
    public sealed class PersistenceService : ILumiService
    {
        private readonly PersistenceConfiguration configuration;
        private readonly ISaveStorage storage;
        private readonly ISaveSerializer serializer;
        private readonly int callerThread;
        private readonly object gate = new object();
        private readonly SemaphoreSlim operationsGate = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly HashSet<Task> operations = new HashSet<Task>();
        private Task initializationTask;
        private Task shutdownTask;
        private bool initialized;
        private bool stopping;
        private bool stopped;
        private bool initializationFailed;

        public PersistenceService(PersistenceConfiguration configuration,
            ISaveStorage storage = null, ISaveSerializer serializer = null)
        {
            this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            this.storage = storage ?? new FileSaveStorage(configuration.MaxFileBytes);
            this.serializer = serializer ?? new UnityJsonSaveSerializer();
            callerThread = Thread.CurrentThread.ManagedThreadId;
        }

        public string Id => "persistence";
        public IReadOnlyCollection<string> Dependencies => Array.Empty<string>();
        public PersistenceConfiguration Configuration => configuration;
        public string StorageDirectory => configuration.StorageDirectory;
        public bool IsInitialized { get { lock (gate) return initialized && !stopping && !stopped; } }

        public Task InitializeAsync(ServiceContext context, CancellationToken cancellationToken)
        {
            RequireCallerThread();
            lock (gate)
            {
                if (stopping || stopped || initializationFailed)
                    return Task.FromException(new InvalidOperationException("Create a new PersistenceService after shutdown or failed initialization."));
                if (initializationTask != null) return initializationTask;
                if (cancellationToken.IsCancellationRequested) return Task.FromCanceled(cancellationToken);
                return initializationTask = InitializeCoreAsync(cancellationToken);
            }
        }

        private async Task InitializeCoreAsync(CancellationToken cancellationToken)
        {
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token))
            {
                try
                {
                    await Task.Run(() =>
                    {
                        linked.Token.ThrowIfCancellationRequested();
                        storage.Initialize(configuration.StorageDirectory);
                        linked.Token.ThrowIfCancellationRequested();
                    });
                    linked.Token.ThrowIfCancellationRequested();
                    lock (gate) initialized = true;
                }
                catch
                {
                    lock (gate) initializationFailed = true;
                    await Task.Run(() => storage.Dispose());
                    throw;
                }
            }
        }

        public Task ShutdownAsync(CancellationToken cancellationToken)
        {
            RequireCallerThread();
            TaskCompletionSource<object> completion;
            Task startup;
            Task[] active;
            lock (gate)
            {
                if (shutdownTask != null) return shutdownTask;
                if (cancellationToken.IsCancellationRequested) return Task.FromCanceled(cancellationToken);
                stopping = true;
                initialized = false;
                completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
                shutdownTask = completion.Task;
                startup = initializationTask;
                active = new Task[operations.Count];
                operations.CopyTo(active);
            }
            Exception cancellationFailure = null;
            try { lifetime.Cancel(); }
            catch (Exception exception) { cancellationFailure = exception; }
            _ = ShutdownCoreAsync(startup, active, completion, cancellationFailure);
            return completion.Task;
        }

        private async Task ShutdownCoreAsync(Task startup, Task[] active,
            TaskCompletionSource<object> completion, Exception cancellationFailure)
        {
            Exception cleanupFailure = null;
            try
            {
                if (startup != null)
                {
                    try { await startup; }
                    catch (Exception) { /* Initialization callers own their diagnostic. */ }
                }
                try { await Task.WhenAll(active); }
                catch (OperationCanceledException) { /* Queued/precommit work is canceled by shutdown. */ }
                catch (Exception exception) { cleanupFailure = exception; }
                try { await Task.Run(() => storage.Dispose()); }
                catch (Exception exception)
                {
                    cleanupFailure = cleanupFailure == null ? exception : new AggregateException(cleanupFailure, exception);
                }
            }
            finally
            {
                operationsGate.Dispose();
                lifetime.Dispose();
                lock (gate) { stopped = true; stopping = false; }
            }
            if (cancellationFailure != null)
                cleanupFailure = cleanupFailure == null ? cancellationFailure : new AggregateException(cancellationFailure, cleanupFailure);
            if (cleanupFailure == null) completion.TrySetResult(null);
            else completion.TrySetException(cleanupFailure);
        }

        /// <summary>Captures and validates data immediately; accepted disk writes are serialized.</summary>
        public Task<PersistenceResult<bool>> SaveAsync<T>(string slotId, T data, SaveSchema<T> schema,
            CancellationToken cancellationToken = default)
        {
            Task<PersistenceResult<bool>> rejection = CheckRequest<bool>(slotId, cancellationToken);
            if (rejection != null) return rejection;
            if (schema == null) return Invalid<bool>("A save schema is required.");
            string payload;
            try
            {
                schema.Validate(data);
                payload = serializer.Serialize(data);
            }
            catch (Exception exception) { return Invalid<bool>("The save data could not be validated/serialized: " + exception.Message, exception); }

            return EnqueueAsync<bool>(async token =>
            {
                SlotDocument existing = await Task.Run(() => ReadSlot(slotId, false, token));
                if (existing.Status != PersistenceStatus.NotFound && existing.Status != PersistenceStatus.Success)
                    return Failure<bool>(existing, "Existing data is preserved. Delete explicitly before replacing a corrupt or incompatible slot.");
                if (existing.Status == PersistenceStatus.Success &&
                    (existing.Metadata.ModelId != schema.ModelId || existing.Metadata.DataVersion > schema.Version))
                    return new PersistenceResult<bool>(PersistenceStatus.Incompatible, metadata: existing.Metadata,
                        message: "The existing slot belongs to another model or a future data version; it was preserved.");
                if (existing.Status == PersistenceStatus.Success && existing.Metadata.DataVersion == schema.Version)
                {
                    PersistenceResult<T> checkedPayload = Deserialize(existing, schema);
                    if (!checkedPayload.IsSuccess)
                        return new PersistenceResult<bool>(checkedPayload.Status, metadata: checkedPayload.Metadata,
                            recoveredFromBackup: checkedPayload.RecoveredFromBackup,
                            message: "The existing payload is invalid for this schema; it and its backup were preserved. Delete explicitly before replacing it.",
                            exception: checkedPayload.Exception);
                }
                token.ThrowIfCancellationRequested();
                var metadata = new SaveSlotMetadata(slotId, DateTime.UtcNow, schema.ModelId, schema.Version);
                string document;
                try { document = SaveEnvelopeCodec.Encode(configuration.GameId, metadata, payload); }
                catch (Exception exception)
                {
                    return new PersistenceResult<bool>(PersistenceStatus.InvalidData,
                        message: "The serialized save document is invalid: " + exception.Message, exception: exception);
                }
                if (new UTF8Encoding(false, true).GetByteCount(document) > configuration.MaxFileBytes)
                    return new PersistenceResult<bool>(PersistenceStatus.InvalidData,
                        message: "The serialized save exceeds the configured file-size limit.");
                await Task.Run(() => storage.Write(slotId, document, token));
                // No cancellation check here: the adapter's last check is before its commit point.
                return new PersistenceResult<bool>(PersistenceStatus.Success, true, metadata,
                    message: "The save was committed.");
            }, cancellationToken);
        }

        public Task<PersistenceResult<T>> LoadAsync<T>(string slotId, SaveSchema<T> schema,
            CancellationToken cancellationToken = default)
        {
            Task<PersistenceResult<T>> rejection = CheckRequest<T>(slotId, cancellationToken);
            if (rejection != null) return rejection;
            if (schema == null) return Invalid<T>("A save schema is required.");
            return EnqueueAsync<T>(async token =>
            {
                SlotDocument document = await Task.Run(() => ReadSlot(slotId, true, token));
                token.ThrowIfCancellationRequested();
                PersistenceResult<T> result = Deserialize(document, schema);
                token.ThrowIfCancellationRequested();
                if (result.Status == PersistenceStatus.Corrupt && document.Status == PersistenceStatus.Success && !document.Recovered)
                {
                    // A supported envelope can contain data invalid for its advertised DTO.
                    SlotDocument backup = await Task.Run(() => ReadBackup(slotId, token));
                    token.ThrowIfCancellationRequested();
                    PersistenceResult<T> recovered = Deserialize(backup, schema);
                    token.ThrowIfCancellationRequested();
                    if (recovered.IsSuccess) return recovered;
                }
                return result;
            }, cancellationToken);
        }

        /// <summary>Explicitly removes the primary, backup and owned temporary files.</summary>
        public Task<PersistenceResult<bool>> DeleteAsync(string slotId, CancellationToken cancellationToken = default)
        {
            Task<PersistenceResult<bool>> rejection = CheckRequest<bool>(slotId, cancellationToken);
            if (rejection != null) return rejection;
            return EnqueueAsync<bool>(async token =>
            {
                bool existed = await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    foreach (string existing in storage.ListSlots())
                        if (existing == slotId) return true;
                    return false;
                });
                await Task.Run(() => storage.Delete(slotId, token));
                return new PersistenceResult<bool>(existed ? PersistenceStatus.Success : PersistenceStatus.NotFound,
                    existed, message: existed ? "The slot was deleted." : "No save existed for this slot.");
            }, cancellationToken);
        }

        /// <summary>Inspects envelope readability and reports corruption/incompatibility explicitly.</summary>
        public Task<PersistenceResult<bool>> ExistsAsync(string slotId, CancellationToken cancellationToken = default)
        {
            Task<PersistenceResult<bool>> rejection = CheckRequest<bool>(slotId, cancellationToken);
            if (rejection != null) return rejection;
            return EnqueueAsync<bool>(async token =>
            {
                SlotDocument document = await Task.Run(() => ReadSlot(slotId, true, token));
                token.ThrowIfCancellationRequested();
                return new PersistenceResult<bool>(document.Status, document.Status == PersistenceStatus.Success,
                    document.Metadata, document.Recovered, document.Message);
            }, cancellationToken);
        }

        public Task<PersistenceResult<IReadOnlyList<SaveSlotInfo>>> ListSlotsAsync(CancellationToken cancellationToken = default)
        {
            Task<PersistenceResult<IReadOnlyList<SaveSlotInfo>>> rejection = CheckRequest<IReadOnlyList<SaveSlotInfo>>(null,
                cancellationToken, false);
            if (rejection != null) return rejection;
            return EnqueueAsync<IReadOnlyList<SaveSlotInfo>>(async token =>
            {
                IReadOnlyList<SaveSlotInfo> slots = await Task.Run(() =>
                {
                    var entries = new List<SaveSlotInfo>();
                    foreach (string slotId in storage.ListSlots())
                    {
                        token.ThrowIfCancellationRequested();
                        if (!PersistenceIdentifier.IsValid(slotId)) continue;
                        try
                        {
                            SlotDocument document = ReadSlot(slotId, true, token);
                            entries.Add(new SaveSlotInfo(slotId, document.Status, document.Metadata,
                                document.Recovered, document.Message));
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception exception)
                        {
                            entries.Add(new SaveSlotInfo(slotId, PersistenceStatus.StorageError, message: exception.Message));
                        }
                    }
                    return (IReadOnlyList<SaveSlotInfo>)entries.AsReadOnly();
                });
                token.ThrowIfCancellationRequested();
                return new PersistenceResult<IReadOnlyList<SaveSlotInfo>>(PersistenceStatus.Success, slots);
            }, cancellationToken);
        }

        private PersistenceResult<T> Deserialize<T>(SlotDocument document, SaveSchema<T> schema)
        {
            if (document.Status != PersistenceStatus.Success) return Failure<T>(document);
            if (document.Metadata.ModelId != schema.ModelId || document.Metadata.DataVersion != schema.Version)
                return new PersistenceResult<T>(PersistenceStatus.Incompatible, metadata: document.Metadata,
                    recoveredFromBackup: document.Recovered,
                    message: "The stored model/data version does not match the requested schema. Supply an explicit game migration before loading another version.");
            try
            {
                T data = serializer.Deserialize<T>(document.Payload);
                schema.Validate(data);
                return new PersistenceResult<T>(PersistenceStatus.Success, data, document.Metadata,
                    document.Recovered, document.Recovered ? "Loaded the last validated backup; the primary was absent or corrupt." : "The save was loaded.");
            }
            catch (Exception exception)
            {
                return new PersistenceResult<T>(PersistenceStatus.Corrupt, metadata: document.Metadata,
                    recoveredFromBackup: document.Recovered,
                    message: "The stored payload is invalid for this model: " + exception.Message, exception: exception);
            }
        }

        private SlotDocument ReadSlot(string slotId, bool recoverCorruptPrimary, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            SlotDocument result = ReadDocument(slotId, SaveStorageFile.Primary);
            if (result.Status == PersistenceStatus.NotFound) return ReadBackup(slotId, token);
            // Incompatible primary data always wins; it must never be hidden by an older backup.
            if (result.Status != PersistenceStatus.Corrupt || !recoverCorruptPrimary) return result;
            SlotDocument backup = ReadBackup(slotId, token);
            return backup.Status == PersistenceStatus.Success ? backup : result;
        }

        private SlotDocument ReadBackup(string slotId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return ReadDocument(slotId, SaveStorageFile.Backup);
        }

        private SlotDocument ReadDocument(string slotId, SaveStorageFile file)
        {
            try
            {
                string json = storage.Read(slotId, file, configuration.MaxFileBytes);
                return json == null ? new SlotDocument { Status = PersistenceStatus.NotFound, Message = "No save exists for this slot." }
                    : Decode(json, slotId, file == SaveStorageFile.Backup);
            }
            catch (Exception exception) when (exception is InvalidDataException || exception is DecoderFallbackException)
            {
                return new SlotDocument { Status = PersistenceStatus.Corrupt, Message = "The stored file is invalid: " + exception.Message };
            }
        }

        private SlotDocument Decode(string json, string slotId, bool backup)
        {
            EnvelopeReadResult result = SaveEnvelopeCodec.Decode(json, configuration.GameId, slotId);
            return new SlotDocument
            {
                Status = result.Status,
                Metadata = result.Metadata,
                Payload = result.PayloadJson,
                Recovered = backup && result.Status == PersistenceStatus.Success,
                Message = result.Message
            };
        }

        private Task<PersistenceResult<T>> CheckRequest<T>(string slotId, CancellationToken cancellationToken, bool validateSlot = true)
        {
            RequireCallerThread();
            if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<PersistenceResult<T>>(cancellationToken);
            lock (gate)
            {
                if (stopping || stopped)
                    return Task.FromResult(new PersistenceResult<T>(PersistenceStatus.ServiceStopping,
                        message: "Persistence is stopping/stopped; create a new service after shutdown."));
                if (!initialized)
                    return Task.FromResult(new PersistenceResult<T>(PersistenceStatus.NotInitialized,
                        message: "Await Bootstrap readiness before using Persistence."));
            }
            return validateSlot && !PersistenceIdentifier.IsValid(slotId) ? Invalid<T>("The slot identifier is invalid.") : null;
        }

        private Task<PersistenceResult<T>> EnqueueAsync<T>(Func<CancellationToken, Task<PersistenceResult<T>>> operation,
            CancellationToken cancellationToken)
        {
            CancellationTokenSource linked;
            var completion = new TaskCompletionSource<PersistenceResult<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate)
            {
                if (stopping || stopped)
                    return Task.FromResult(new PersistenceResult<T>(PersistenceStatus.ServiceStopping, message: "Persistence is stopping/stopped."));
                linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
                operations.Add(completion.Task);
            }
            _ = CompleteOperationAsync(operation, linked, completion);
            return completion.Task;
        }

        private async Task CompleteOperationAsync<T>(Func<CancellationToken, Task<PersistenceResult<T>>> operation,
            CancellationTokenSource linked, TaskCompletionSource<PersistenceResult<T>> completion)
        {
            bool entered = false;
            bool canceled = false;
            PersistenceResult<T> result = null;
            CancellationToken token = linked.Token;
            try
            {
                await operationsGate.WaitAsync(token);
                entered = true;
                token.ThrowIfCancellationRequested();
                result = await operation(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { canceled = true; }
            catch (Exception exception)
            {
                result = new PersistenceResult<T>(PersistenceStatus.StorageError,
                    message: "The storage operation failed: " + exception.Message, exception: exception);
            }
            finally
            {
                if (entered) operationsGate.Release();
                linked.Dispose();
                lock (gate) operations.Remove(completion.Task);
            }
            if (canceled) completion.TrySetCanceled(token);
            else completion.TrySetResult(result);
        }

        private void RequireCallerThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != callerThread)
                throw new InvalidOperationException("Call Persistence lifecycle/API methods on the constructing Unity main thread.");
        }

        private static Task<PersistenceResult<T>> Invalid<T>(string message, Exception exception = null) =>
            Task.FromResult(new PersistenceResult<T>(PersistenceStatus.InvalidData, message: message, exception: exception));

        private static PersistenceResult<T> Failure<T>(SlotDocument document, string message = null) =>
            new PersistenceResult<T>(document.Status, metadata: document.Metadata,
                recoveredFromBackup: document.Recovered, message: message ?? document.Message);

        private sealed class SlotDocument
        {
            internal PersistenceStatus Status;
            internal SaveSlotMetadata Metadata;
            internal string Payload;
            internal bool Recovered;
            internal string Message;
        }
    }
}
