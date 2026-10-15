using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lumivale.Core.Bootstrap;
using Lumivale.Core.Persistence;
using NUnit.Framework;
using UnityEngine;

namespace Lumivale.Core.Tests.Persistence
{
    public sealed class PersistenceServiceTests
    {
        private string root;
        private PersistenceConfiguration configuration;
        private SaveSchema<TestData> schema;
        private readonly List<PersistenceService> services = new List<PersistenceService>();
        private readonly List<ControlledStorage> controlled = new List<ControlledStorage>();

        [SetUp]
        public void SetUp()
        {
            root = Path.Combine(Path.GetTempPath(), "LumiPersistenceServiceTests-" + Guid.NewGuid().ToString("N"));
            configuration = new PersistenceConfiguration("isolated-test", root);
            schema = new SaveSchema<TestData>("test-model", 1, data =>
            {
                if (data == null || data.Counter < 0) throw new ArgumentException("A nonnegative counter is required.");
            });
        }

        [TearDown]
        public async Task TearDown()
        {
            foreach (ControlledStorage storage in controlled) storage.Release.Set();
            foreach (PersistenceService service in services) await service.ShutdownAsync(CancellationToken.None);
            services.Clear();
            foreach (ControlledStorage storage in controlled) storage.Release.Dispose();
            controlled.Clear();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }

        [Test]
        public async Task NativeSaveLoadRoundTripIncludesExplicitUtcMetadata()
        {
            PersistenceService service = await Create();
            PersistenceResult<bool> saved = await service.SaveAsync("slot-1", Data(7, 2.5f), schema);
            Assert.That(saved.Status, Is.EqualTo(PersistenceStatus.Success));
            Assert.That(saved.Data, Is.True);
            Assert.That(saved.Metadata.SlotId, Is.EqualTo("slot-1"));
            Assert.That(saved.Metadata.ModelId, Is.EqualTo("test-model"));
            Assert.That(saved.Metadata.DataVersion, Is.EqualTo(1));
            Assert.That(saved.Metadata.SavedAtUtc.Kind, Is.EqualTo(DateTimeKind.Utc));
            Assert.That(File.Exists(Primary("slot-1")), Is.True);
            PersistenceResult<TestData> loaded = await service.LoadAsync("slot-1", schema);
            Assert.That(loaded.Status, Is.EqualTo(PersistenceStatus.Success));
            Assert.That(loaded.Data.Counter, Is.EqualTo(7));
            Assert.That(loaded.Data.PositionX, Is.EqualTo(2.5f));
            Assert.That(loaded.Data.Label, Is.EqualTo("demo"));
            Assert.That(loaded.RecoveredFromBackup, Is.False);
        }

        [Test]
        public async Task MultipleSlotsListSortedMetadataAndDeleteOnlyRequestedSlot()
        {
            PersistenceService service = await Create();
            foreach (string slot in new[] { "c", "a", "b" }) Assert.That((await service.SaveAsync(slot, Data(slot[0]), schema)).IsSuccess, Is.True);
            PersistenceResult<IReadOnlyList<SaveSlotInfo>> listed = await service.ListSlotsAsync();
            Assert.That(listed.IsSuccess, Is.True);
            Assert.That(listed.Data.Select(slot => slot.SlotId), Is.EqualTo(new[] { "a", "b", "c" }));
            Assert.That(listed.Data.All(slot => slot.Status == PersistenceStatus.Success && slot.Metadata.DataVersion == 1), Is.True);
            Assert.That((await service.DeleteAsync("b")).IsSuccess, Is.True);
            Assert.That((await service.ExistsAsync("b")).Status, Is.EqualTo(PersistenceStatus.NotFound));
            Assert.That((await service.LoadAsync("a", schema)).Data.Counter, Is.EqualTo((int)'a'));
            Assert.That((await service.LoadAsync("c", schema)).Data.Counter, Is.EqualTo((int)'c'));
        }

        [Test]
        public async Task MissingSaveIsDistinguishedForLoadExistsAndDelete()
        {
            PersistenceService service = await Create();
            Assert.That((await service.LoadAsync("missing", schema)).Status, Is.EqualTo(PersistenceStatus.NotFound));
            PersistenceResult<bool> exists = await service.ExistsAsync("missing");
            Assert.That(exists.Status, Is.EqualTo(PersistenceStatus.NotFound));
            Assert.That(exists.Data, Is.False);
            Assert.That((await service.DeleteAsync("missing")).Status, Is.EqualTo(PersistenceStatus.NotFound));
            Assert.That((await service.ListSlotsAsync()).Data, Is.Empty);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("../outside")]
        [TestCase("slot/inside")]
        [TestCase("slot\\inside")]
        [TestCase("E:\\absolute")]
        [TestCase("MixedCase")]
        [TestCase("space name")]
        [TestCase("é")]
        [TestCase("slot.json")]
        public async Task InvalidSlotIdsAreRejectedBeforeStorageAccess(string slotId)
        {
            var storage = NewControlled();
            PersistenceService service = await Create(storage);
            int reads = storage.ReadCalls;
            Assert.That((await service.SaveAsync(slotId, Data(1), schema)).Status, Is.EqualTo(PersistenceStatus.InvalidData));
            Assert.That((await service.LoadAsync(slotId, schema)).Status, Is.EqualTo(PersistenceStatus.InvalidData));
            Assert.That((await service.ExistsAsync(slotId)).Status, Is.EqualTo(PersistenceStatus.InvalidData));
            Assert.That((await service.DeleteAsync(slotId)).Status, Is.EqualTo(PersistenceStatus.InvalidData));
            Assert.That(storage.ReadCalls, Is.EqualTo(reads));
            Assert.That(storage.WriteCalls, Is.Zero);
        }

        [Test]
        public async Task CorruptPrimaryIsReportedAndCannotBeOverwrittenImplicitly()
        {
            PersistenceService service = await Create();
            File.WriteAllText(Primary("corrupt"), "{broken", Encoding.UTF8);
            byte[] original = File.ReadAllBytes(Primary("corrupt"));
            Assert.That((await service.LoadAsync("corrupt", schema)).Status, Is.EqualTo(PersistenceStatus.Corrupt));
            Assert.That((await service.ExistsAsync("corrupt")).Status, Is.EqualTo(PersistenceStatus.Corrupt));
            Assert.That((await service.SaveAsync("corrupt", Data(2), schema)).Status, Is.EqualTo(PersistenceStatus.Corrupt));
            Assert.That(File.ReadAllBytes(Primary("corrupt")), Is.EqualTo(original));
            Assert.That((await service.ListSlotsAsync()).Data.Single().Status, Is.EqualTo(PersistenceStatus.Corrupt));
            Assert.That((await service.DeleteAsync("corrupt")).IsSuccess, Is.True);
            Assert.That((await service.SaveAsync("corrupt", Data(3), schema)).IsSuccess, Is.True);
        }

        [Test]
        public async Task NativeBackupRecoversCorruptPrimaryWithoutRewritingEitherFile()
        {
            PersistenceService service = await Create();
            await service.SaveAsync("recover", Data(1), schema);
            await service.SaveAsync("recover", Data(2), schema);
            File.WriteAllText(Primary("recover"), "incomplete", new UTF8Encoding(false));
            byte[] backup = File.ReadAllBytes(Backup("recover"));
            PersistenceResult<TestData> loaded = await service.LoadAsync("recover", schema);
            Assert.That(loaded.IsSuccess, Is.True);
            Assert.That(loaded.RecoveredFromBackup, Is.True);
            Assert.That(loaded.Data.Counter, Is.EqualTo(1));
            Assert.That(File.ReadAllText(Primary("recover")), Is.EqualTo("incomplete"));
            Assert.That(File.ReadAllBytes(Backup("recover")), Is.EqualTo(backup));
            Assert.That((await service.SaveAsync("recover", Data(3), schema)).Status, Is.EqualTo(PersistenceStatus.Corrupt));
            Assert.That((await service.ListSlotsAsync()).Data.Single().RecoveredFromBackup, Is.True);
        }

        [Test]
        public async Task MissingPrimaryRecoversBackupAndIgnoresInterruptedTemporaryDraft()
        {
            PersistenceService service = await Create();
            await service.SaveAsync("recover", Data(4), schema);
            await service.SaveAsync("recover", Data(5), schema);
            File.Delete(Primary("recover"));
            File.WriteAllText(Temporary("recover"), "{unfinished", Encoding.UTF8);
            PersistenceResult<TestData> loaded = await service.LoadAsync("recover", schema);
            Assert.That(loaded.IsSuccess, Is.True);
            Assert.That(loaded.RecoveredFromBackup, Is.True);
            Assert.That(loaded.Data.Counter, Is.EqualTo(4));
            Assert.That((await service.SaveAsync("recover", Data(6), schema)).IsSuccess, Is.True);
            Assert.That((await service.LoadAsync("recover", schema)).Data.Counter, Is.EqualTo(6));
            Assert.That(File.Exists(Temporary("recover")), Is.False);
            Assert.That(File.Exists(Backup("recover")), Is.True);
        }

        [Test]
        public async Task TemporaryOnlyFileIsNotPromotedAndExplicitDeleteRemovesIt()
        {
            PersistenceService service = await Create();
            File.WriteAllText(Temporary("unfinished"), "{unfinished", Encoding.UTF8);
            Assert.That((await service.LoadAsync("unfinished", schema)).Status, Is.EqualTo(PersistenceStatus.NotFound));
            Assert.That((await service.ListSlotsAsync()).Data, Is.Empty);
            Assert.That((await service.DeleteAsync("unfinished")).Status, Is.EqualTo(PersistenceStatus.NotFound));
            Assert.That(File.Exists(Temporary("unfinished")), Is.False);
        }

        [Test]
        public async Task DeleteRemovesPrimaryBackupAndTemporaryWithoutResurrectingSlot()
        {
            PersistenceService service = await Create();
            await service.SaveAsync("delete", Data(1), schema);
            await service.SaveAsync("delete", Data(2), schema);
            File.WriteAllText(Temporary("delete"), "unfinished");
            string foreign = Path.Combine(configuration.StorageDirectory, "unrelated.txt");
            File.WriteAllText(foreign, "preserve");
            Assert.That((await service.DeleteAsync("delete")).IsSuccess, Is.True);
            Assert.That(File.Exists(Primary("delete")), Is.False);
            Assert.That(File.Exists(Backup("delete")), Is.False);
            Assert.That(File.Exists(Temporary("delete")), Is.False);
            Assert.That(File.ReadAllText(foreign), Is.EqualTo("preserve"));
            Assert.That((await service.LoadAsync("delete", schema)).Status, Is.EqualTo(PersistenceStatus.NotFound));
        }

        [Test]
        public async Task FutureFormatIsNotHiddenByValidBackupAndIsPreservedForLoadSaveList()
        {
            PersistenceService service = await Create();
            await service.SaveAsync("future", Data(1), schema);
            await service.SaveAsync("future", Data(2), schema);
            const string future = "{\"formatVersion\":2,\"changedStructure\":true}";
            File.WriteAllText(Primary("future"), future, new UTF8Encoding(false));
            Assert.That((await service.LoadAsync("future", schema)).Status, Is.EqualTo(PersistenceStatus.Incompatible));
            Assert.That((await service.SaveAsync("future", Data(3), schema)).Status, Is.EqualTo(PersistenceStatus.Incompatible));
            Assert.That((await service.ExistsAsync("future")).Status, Is.EqualTo(PersistenceStatus.Incompatible));
            Assert.That((await service.ListSlotsAsync()).Data.Single().Status, Is.EqualTo(PersistenceStatus.Incompatible));
            Assert.That(File.ReadAllText(Primary("future")), Is.EqualTo(future));
        }

        [Test]
        public async Task FutureDataVersionAndDifferentModelArePreserved()
        {
            PersistenceService service = await Create();
            var futureSchema = new SaveSchema<TestData>("test-model", 2);
            await service.SaveAsync("future", Data(8), futureSchema);
            byte[] before = File.ReadAllBytes(Primary("future"));
            Assert.That((await service.LoadAsync("future", schema)).Status, Is.EqualTo(PersistenceStatus.Incompatible));
            Assert.That((await service.SaveAsync("future", Data(9), schema)).Status, Is.EqualTo(PersistenceStatus.Incompatible));
            Assert.That((await service.LoadAsync("future", new SaveSchema<TestData>("other-model", 2))).Status, Is.EqualTo(PersistenceStatus.Incompatible));
            Assert.That((await service.SaveAsync("future", Data(9), new SaveSchema<TestData>("other-model", 2))).Status, Is.EqualTo(PersistenceStatus.Incompatible));
            Assert.That(File.ReadAllBytes(Primary("future")), Is.EqualTo(before));
        }

        [Test]
        public async Task FutureBackupWithMissingPrimaryCannotBeOverwritten()
        {
            PersistenceService service = await Create();
            File.WriteAllText(Backup("future"), "{\"formatVersion\":3}", new UTF8Encoding(false));
            Assert.That((await service.LoadAsync("future", schema)).Status, Is.EqualTo(PersistenceStatus.Incompatible));
            Assert.That((await service.SaveAsync("future", Data(1), schema)).Status, Is.EqualTo(PersistenceStatus.Incompatible));
            Assert.That(File.Exists(Primary("future")), Is.False);
        }

        [Test]
        public async Task GameCanExplicitlyReadOldSchemaConvertAndSaveNewVersion()
        {
            PersistenceService service = await Create();
            await service.SaveAsync("migrate", Data(3), schema);
            var versionTwo = new SaveSchema<TestData>("test-model", 2);
            Assert.That((await service.LoadAsync("migrate", versionTwo)).Status, Is.EqualTo(PersistenceStatus.Incompatible));
            TestData old = (await service.LoadAsync("migrate", schema)).Data;
            old.Counter += 10;
            Assert.That((await service.SaveAsync("migrate", old, versionTwo)).IsSuccess, Is.True);
            Assert.That((await service.LoadAsync("migrate", versionTwo)).Data.Counter, Is.EqualTo(13));
        }

        [Test]
        public async Task InvalidSemanticDataAndNonFiniteValuesPreserveValidPrimaryExactly()
        {
            PersistenceService service = await Create();
            await service.SaveAsync("safe", Data(6), schema);
            byte[] original = File.ReadAllBytes(Primary("safe"));
            Assert.That((await service.SaveAsync("safe", Data(-1), schema)).Status, Is.EqualTo(PersistenceStatus.InvalidData));
            Assert.That((await service.SaveAsync("safe", Data(1, float.NaN), schema)).Status, Is.EqualTo(PersistenceStatus.InvalidData));
            Assert.That((await service.SaveAsync<TestData>("safe", null, schema)).Status, Is.EqualTo(PersistenceStatus.InvalidData));
            Assert.That(File.ReadAllBytes(Primary("safe")), Is.EqualTo(original));
            Assert.That((await service.LoadAsync("safe", schema)).Data.Counter, Is.EqualTo(6));
        }

        [Test]
        public async Task UnsupportedDictionaryDtoIsRejectedBeforeWriting()
        {
            PersistenceService service = await Create();
            var unsupported = new UnsupportedData { Values = new Dictionary<string, int> { { "key", 1 } } };
            PersistenceResult<bool> result = await service.SaveAsync("unsupported", unsupported,
                new SaveSchema<UnsupportedData>("dictionary-model", 1));
            Assert.That(result.Status, Is.EqualTo(PersistenceStatus.InvalidData));
            Assert.That(result.Exception, Is.Not.Null);
            Assert.That(File.Exists(Primary("unsupported")), Is.False);
        }

        [Test]
        public async Task WellFormedEnvelopeWithInvalidPayloadIsCorruptAndCanRecoverValidBackup()
        {
            PersistenceService service = await Create();
            await service.SaveAsync("payload", Data(1), schema);
            await service.SaveAsync("payload", Data(2), schema);
            string bad = SaveEnvelopeCodec.Encode(configuration.GameId,
                new SaveSlotMetadata("payload", DateTime.UtcNow, schema.ModelId, schema.Version),
                "{\"Counter\":\"bad\",\"PositionX\":0,\"Label\":\"demo\"}");
            File.WriteAllText(Primary("payload"), bad, new UTF8Encoding(false));
            PersistenceResult<TestData> loaded = await service.LoadAsync("payload", schema);
            Assert.That(loaded.IsSuccess, Is.True);
            Assert.That(loaded.RecoveredFromBackup, Is.True);
            Assert.That(loaded.Data.Counter, Is.EqualTo(1));
            byte[] beforePrimary = File.ReadAllBytes(Primary("payload"));
            byte[] beforeBackup = File.ReadAllBytes(Backup("payload"));
            Assert.That((await service.SaveAsync("payload", Data(3), schema)).Status, Is.EqualTo(PersistenceStatus.Corrupt));
            Assert.That(File.ReadAllBytes(Primary("payload")), Is.EqualTo(beforePrimary));
            Assert.That(File.ReadAllBytes(Backup("payload")), Is.EqualTo(beforeBackup));
        }

        [Test]
        public async Task InvalidUtf8AndOversizedNativeFilesReportCorruptionAndCanBeDeleted()
        {
            configuration = new PersistenceConfiguration("isolated-test", root, 1024);
            PersistenceService service = await Create();
            File.WriteAllBytes(Primary("invalid-utf8"), new byte[] { 0xC0, 0xAF });
            File.WriteAllBytes(Primary("oversize"), new byte[1025]);
            foreach (string slot in new[] { "invalid-utf8", "oversize" })
            {
                Assert.That((await service.LoadAsync(slot, schema)).Status, Is.EqualTo(PersistenceStatus.Corrupt));
                Assert.That((await service.DeleteAsync(slot)).IsSuccess, Is.True);
            }
        }

        [Test]
        public async Task FileSizeLimitRejectsNewDocumentAndKeepsExistingSave()
        {
            configuration = new PersistenceConfiguration("isolated-test", root, 1024);
            PersistenceService service = await Create();
            Assert.That((await service.SaveAsync("safe", Data(1), schema)).IsSuccess, Is.True);
            byte[] original = File.ReadAllBytes(Primary("safe"));
            var large = Data(2); large.Label = new string('x', 2000);
            Assert.That((await service.SaveAsync("safe", large, schema)).Status, Is.EqualTo(PersistenceStatus.InvalidData));
            Assert.That(File.ReadAllBytes(Primary("safe")), Is.EqualTo(original));
        }

        [Test]
        public async Task InjectedWriteFailureReportsDiagnosticAndPreservesPreviousNativeSave()
        {
            ControlledStorage storage = NewControlled();
            PersistenceService service = await Create(storage);
            await service.SaveAsync("safe", Data(1), schema);
            byte[] original = File.ReadAllBytes(Primary("safe"));
            storage.FailWrites = true;
            PersistenceResult<bool> result = await service.SaveAsync("safe", Data(2), schema);
            Assert.That(result.Status, Is.EqualTo(PersistenceStatus.StorageError));
            Assert.That(result.Exception, Is.TypeOf<IOException>());
            Assert.That(result.Message, Does.Contain("simulated"));
            Assert.That(File.ReadAllBytes(Primary("safe")), Is.EqualTo(original));
            storage.FailWrites = false;
            Assert.That((await service.LoadAsync("safe", schema)).Data.Counter, Is.EqualTo(1));
        }

        [Test]
        public async Task InjectedPermissionFailureIsStorageErrorAndIsNotMistakenForAbsence()
        {
            ControlledStorage storage = NewControlled();
            PersistenceService service = await Create(storage);
            storage.FailReads = true;
            PersistenceResult<TestData> loaded = await service.LoadAsync("missing", schema);
            Assert.That(loaded.Status, Is.EqualTo(PersistenceStatus.StorageError));
            Assert.That(loaded.Exception, Is.TypeOf<UnauthorizedAccessException>());
        }

        [Test]
        public async Task SimultaneousSameSlotSavesAndLoadsWaitForCommittedWrite()
        {
            ControlledStorage storage = NewControlled();
            PersistenceService service = await Create(storage);
            storage.BlockWrites = true;
            Task<PersistenceResult<bool>> first = service.SaveAsync("shared", Data(1), schema);
            await Deadline(storage.WriteEntered.Task);
            Task<PersistenceResult<bool>> second = service.SaveAsync("shared", Data(2), schema);
            Task<PersistenceResult<TestData>> load = service.LoadAsync("shared", schema);
            Assert.That(second.IsCompleted, Is.False);
            Assert.That(load.IsCompleted, Is.False);
            Assert.That(storage.WriteCalls, Is.EqualTo(1));
            storage.Release.Set();
            Assert.That((await first).IsSuccess, Is.True);
            Assert.That((await second).IsSuccess, Is.True);
            Assert.That((await load).IsSuccess, Is.True);
            Assert.That((await service.LoadAsync("shared", schema)).Data.Counter, Is.EqualTo(2));
            Assert.That(storage.MaximumConcurrentCalls, Is.EqualTo(1));
        }

        [Test]
        public async Task GameplaySnapshotIsCapturedBeforeWaitingForDiskGate()
        {
            ControlledStorage storage = NewControlled();
            PersistenceService service = await Create(storage);
            storage.BlockReads = true;
            Task<PersistenceResult<bool>> blocked = service.ExistsAsync("missing");
            await Deadline(storage.ReadEntered.Task);
            TestData data = Data(9);
            Task<PersistenceResult<bool>> save = service.SaveAsync("snapshot", data, schema);
            data.Counter = 42;
            storage.Release.Set();
            await blocked;
            Assert.That((await save).IsSuccess, Is.True);
            Assert.That((await service.LoadAsync("snapshot", schema)).Data.Counter, Is.EqualTo(9));
        }

        [Test]
        public async Task CancellationWhileQueuedDoesNotExecuteStorageOperation()
        {
            ControlledStorage storage = NewControlled();
            PersistenceService service = await Create(storage);
            storage.BlockWrites = true;
            Task<PersistenceResult<bool>> first = service.SaveAsync("first", Data(1), schema);
            await Deadline(storage.WriteEntered.Task);
            using (var cancellation = new CancellationTokenSource())
            {
                Task<PersistenceResult<bool>> queued = service.SaveAsync("canceled", Data(2), schema, cancellation.Token);
                cancellation.Cancel();
                await ExpectCanceled(queued);
                Assert.That(storage.WriteCalls, Is.EqualTo(1));
            }
            storage.Release.Set();
            await first;
            Assert.That(File.Exists(Primary("canceled")), Is.False);
        }

        [Test]
        public async Task CancellationBeforeCommitPreservesPreviousValidSave()
        {
            ControlledStorage storage = NewControlled();
            PersistenceService service = await Create(storage);
            await service.SaveAsync("safe", Data(1), schema);
            byte[] original = File.ReadAllBytes(Primary("safe"));
            storage.BlockWrites = true;
            using (var cancellation = new CancellationTokenSource())
            {
                Task<PersistenceResult<bool>> save = service.SaveAsync("safe", Data(2), schema, cancellation.Token);
                await Deadline(storage.WriteEntered.Task);
                cancellation.Cancel();
                await ExpectCanceled(save);
            }
            Assert.That(File.ReadAllBytes(Primary("safe")), Is.EqualTo(original));
        }

        [Test]
        public async Task CommittedWriteReturnsSuccessWhenCancellationArrivesAfterCommitPoint()
        {
            ControlledStorage storage = NewControlled();
            PersistenceService service = await Create(storage);
            storage.BlockAfterWrite = true;
            using (var cancellation = new CancellationTokenSource())
            {
                Task<PersistenceResult<bool>> save = service.SaveAsync("committed", Data(3), schema, cancellation.Token);
                await Deadline(storage.CommitEntered.Task);
                cancellation.Cancel();
                storage.Release.Set();
                Assert.That((await save).IsSuccess, Is.True);
            }
            Assert.That((await service.LoadAsync("committed", schema)).Data.Counter, Is.EqualTo(3));
        }

        [Test]
        public async Task ShutdownCancelsQueuedAndPrecommitWorkThenRejectsFurtherCalls()
        {
            ControlledStorage storage = NewControlled();
            PersistenceService service = await Create(storage);
            storage.BlockWrites = true;
            Task<PersistenceResult<bool>> writing = service.SaveAsync("writing", Data(1), schema);
            await Deadline(storage.WriteEntered.Task);
            Task<PersistenceResult<TestData>> queued = service.LoadAsync("writing", schema);
            Task shutdown = service.ShutdownAsync(CancellationToken.None);
            Assert.That(service.IsInitialized, Is.False);
            Assert.That((await service.ExistsAsync("writing")).Status, Is.EqualTo(PersistenceStatus.ServiceStopping));
            await Deadline(shutdown);
            await ExpectCanceled(writing);
            await ExpectCanceled(queued);
            Assert.That(storage.Disposed, Is.True);
            Assert.That(File.Exists(Primary("writing")), Is.False);
            Assert.That(service.ShutdownAsync(CancellationToken.None), Is.SameAs(shutdown));
        }

        [Test]
        public async Task ShutdownWaitsForAlreadyCommittedWriteBeforeReleasingStorageOwnership()
        {
            ControlledStorage storage = NewControlled();
            PersistenceService service = await Create(storage);
            storage.BlockAfterWrite = true;
            Task<PersistenceResult<bool>> writing = service.SaveAsync("committed", Data(4), schema);
            await Deadline(storage.CommitEntered.Task);
            Task shutdown = service.ShutdownAsync(CancellationToken.None);
            Assert.That(shutdown.IsCompleted, Is.False);
            Assert.That(storage.Disposed, Is.False);
            storage.Release.Set();
            Assert.That((await writing).IsSuccess, Is.True);
            await Deadline(shutdown);
            Assert.That(storage.Disposed, Is.True);
            PersistenceService replacement = await Create();
            Assert.That((await replacement.LoadAsync("committed", schema)).Data.Counter, Is.EqualTo(4));
        }

        [Test]
        public async Task PreCanceledRequestsAndShutdownHaveNoMutation()
        {
            PersistenceService service = await Create();
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                await ExpectCanceled(service.SaveAsync("cancel", Data(1), schema, cancellation.Token));
                await ExpectCanceled(service.LoadAsync("cancel", schema, cancellation.Token));
                await ExpectCanceled(service.DeleteAsync("cancel", cancellation.Token));
                await ExpectCanceled(service.ExistsAsync("cancel", cancellation.Token));
                await ExpectCanceled(service.ListSlotsAsync(cancellation.Token));
                await ExpectCanceled(service.ShutdownAsync(cancellation.Token));
            }
            Assert.That(service.IsInitialized, Is.True);
            Assert.That((await service.ListSlotsAsync()).Data, Is.Empty);
        }

        [Test]
        public async Task NativeDirectoryOwnershipPreventsDuplicateServiceButRestartsAfterShutdown()
        {
            PersistenceService service = await Create();
            await service.SaveAsync("saved", Data(11), schema);
            var duplicate = new PersistenceService(configuration); services.Add(duplicate);
            await ExpectFailure<IOException>(duplicate.InitializeAsync(null, CancellationToken.None));
            await duplicate.ShutdownAsync(CancellationToken.None);
            Assert.That((await service.LoadAsync("saved", schema)).Data.Counter, Is.EqualTo(11));
            await service.ShutdownAsync(CancellationToken.None);
            PersistenceService replacement = await Create();
            Assert.That((await replacement.LoadAsync("saved", schema)).Data.Counter, Is.EqualTo(11));
            await ExpectFailure<InvalidOperationException>(service.InitializeAsync(null, CancellationToken.None));
        }

        [Test]
        public async Task RepeatedCoordinatorLifecyclesUseNewServicesAndKeepDiskData()
        {
            var first = new PersistenceService(configuration); services.Add(first);
            var coordinator = new BootstrapCoordinator(new ILumiService[] { first });
            await coordinator.InitializeAsync();
            Assert.That(coordinator.Context.Get<PersistenceService>(), Is.SameAs(first));
            await first.SaveAsync("restart", Data(12), schema);
            await coordinator.ShutdownAsync();
            var second = new PersistenceService(configuration); services.Add(second);
            var replacement = new BootstrapCoordinator(new ILumiService[] { second });
            await replacement.InitializeAsync();
            Assert.That(second, Is.Not.SameAs(first));
            Assert.That((await second.LoadAsync("restart", schema)).Data.Counter, Is.EqualTo(12));
            await replacement.ShutdownAsync();
        }

        [Test]
        public async Task DifferentGameIdentitiesOwnIndependentDirectoriesAndIdenticalSlotNames()
        {
            PersistenceService first = await Create();
            var otherConfiguration = new PersistenceConfiguration("other-game", root);
            var other = new PersistenceService(otherConfiguration); services.Add(other);
            await other.InitializeAsync(null, CancellationToken.None);
            await first.SaveAsync("slot", Data(1), schema);
            await other.SaveAsync("slot", Data(2), schema);
            Assert.That(first.StorageDirectory, Is.Not.EqualTo(other.StorageDirectory));
            Assert.That((await first.LoadAsync("slot", schema)).Data.Counter, Is.EqualTo(1));
            Assert.That((await other.LoadAsync("slot", schema)).Data.Counter, Is.EqualTo(2));
            await first.DeleteAsync("slot");
            Assert.That((await other.LoadAsync("slot", schema)).Data.Counter, Is.EqualTo(2));
        }

        [Test]
        public async Task ResettingOnlyAnIsolatedPreferenceKeyDoesNotAffectGameplaySave()
        {
            string preferenceKey = "lumi-persistence-test-" + Guid.NewGuid().ToString("N");
            PersistenceService service = await Create();
            try
            {
                PlayerPrefs.SetString(preferenceKey, "isolated preferences");
                await service.SaveAsync("gameplay", Data(13), schema);
                PlayerPrefs.DeleteKey(preferenceKey);
                PlayerPrefs.Save();
                Assert.That((await service.LoadAsync("gameplay", schema)).Data.Counter, Is.EqualTo(13));
            }
            finally { PlayerPrefs.DeleteKey(preferenceKey); PlayerPrefs.Save(); }
        }

        [Test]
        public async Task WorkerIoNeverRunsSerializerOrGameValidatorOffCallerThread()
        {
            int caller = Thread.CurrentThread.ManagedThreadId;
            ControlledStorage storage = NewControlled();
            var recordingSerializer = new RecordingSerializer();
            var service = new PersistenceService(configuration, storage, recordingSerializer); services.Add(service);
            await service.InitializeAsync(null, CancellationToken.None);
            int validations = 0;
            var checkedSchema = new SaveSchema<TestData>("test-model", 1, data =>
            {
                Assert.That(Thread.CurrentThread.ManagedThreadId, Is.EqualTo(caller)); validations++;
            });
            await service.SaveAsync("threads", Data(1), checkedSchema);
            await service.LoadAsync("threads", checkedSchema);
            Assert.That(validations, Is.EqualTo(2));
            Assert.That(recordingSerializer.Threads, Is.All.EqualTo(caller));
            Assert.That(storage.Threads, Has.None.EqualTo(caller));
            await Task.Run(() => Assert.Throws<InvalidOperationException>(() => service.ExistsAsync("threads")));
        }

        [Test]
        public async Task ApiBeforeInitializationReportsNotInitializedAndRepeatedStartupSharesTask()
        {
            var service = new PersistenceService(configuration); services.Add(service);
            Assert.That((await service.ExistsAsync("slot")).Status, Is.EqualTo(PersistenceStatus.NotInitialized));
            Task first = service.InitializeAsync(null, CancellationToken.None);
            Task second = service.InitializeAsync(null, CancellationToken.None);
            Assert.That(second, Is.SameAs(first));
            await first;
            Assert.That(service.IsInitialized, Is.True);
        }

        private async Task<PersistenceService> Create(ISaveStorage storage = null)
        {
            var service = new PersistenceService(configuration, storage); services.Add(service);
            await service.InitializeAsync(null, CancellationToken.None);
            return service;
        }

        private ControlledStorage NewControlled()
        {
            var storage = new ControlledStorage(configuration.MaxFileBytes); controlled.Add(storage); return storage;
        }

        private string Primary(string slot) => Path.Combine(configuration.StorageDirectory, "slot-" + slot + ".json");
        private string Backup(string slot) => Path.Combine(configuration.StorageDirectory, "slot-" + slot + ".bak");
        private string Temporary(string slot) => Path.Combine(configuration.StorageDirectory, "slot-" + slot + ".tmp");
        private static TestData Data(int counter, float x = 0f) => new TestData { Counter = counter, PositionX = x, Label = "demo" };

        private static async Task Deadline(Task task)
        {
            Task completed = await Task.WhenAny(task, Task.Delay(5000));
            Assert.That(completed, Is.SameAs(task), "An isolated asynchronous test operation did not finish.");
            await task;
        }

        private static async Task ExpectCanceled(Task task)
        {
            try { await Deadline(task); Assert.Fail("Expected a canceled task."); }
            catch (OperationCanceledException) { Assert.That(task.IsCanceled, Is.True); }
        }

        private static async Task ExpectFailure<T>(Task task) where T : Exception
        {
            try { await Deadline(task); Assert.Fail("Expected " + typeof(T).Name); }
            catch (T) { }
        }

        [Serializable]
        public sealed class TestData
        {
            public int Counter;
            public float PositionX;
            public string Label;
        }

        [Serializable]
        public sealed class UnsupportedData { public Dictionary<string, int> Values; }

        private sealed class RecordingSerializer : ISaveSerializer
        {
            private readonly UnityJsonSaveSerializer inner = new UnityJsonSaveSerializer();
            public readonly List<int> Threads = new List<int>();
            public string Serialize<T>(T data) { Threads.Add(Thread.CurrentThread.ManagedThreadId); return inner.Serialize(data); }
            public T Deserialize<T>(string json) { Threads.Add(Thread.CurrentThread.ManagedThreadId); return inner.Deserialize<T>(json); }
        }

        private sealed class ControlledStorage : ISaveStorage
        {
            private readonly FileSaveStorage inner;
            private int active;
            public readonly ManualResetEventSlim Release = new ManualResetEventSlim(false);
            public readonly TaskCompletionSource<bool> WriteEntered = Completion();
            public readonly TaskCompletionSource<bool> ReadEntered = Completion();
            public readonly TaskCompletionSource<bool> CommitEntered = Completion();
            public readonly List<int> Threads = new List<int>();
            public bool BlockWrites, BlockReads, BlockAfterWrite, FailWrites, FailReads, Disposed;
            public int ReadCalls, WriteCalls, MaximumConcurrentCalls;
            public ControlledStorage(int maxBytes) => inner = new FileSaveStorage(maxBytes);
            public void Initialize(string directory) { Record(); inner.Initialize(directory); }
            public string Read(string slot, SaveStorageFile file, int maxBytes)
            {
                Enter();
                try
                {
                    ReadCalls++;
                    if (FailReads) throw new UnauthorizedAccessException("simulated permission failure");
                    if (BlockReads) { ReadEntered.TrySetResult(true); Wait(CancellationToken.None); }
                    return inner.Read(slot, file, maxBytes);
                }
                finally { Interlocked.Decrement(ref active); }
            }
            public void Write(string slot, string document, CancellationToken token)
            {
                Enter();
                try
                {
                    WriteCalls++;
                    if (FailWrites) throw new IOException("simulated write failure");
                    if (BlockWrites) { WriteEntered.TrySetResult(true); Wait(token); }
                    inner.Write(slot, document, token);
                    if (BlockAfterWrite) { CommitEntered.TrySetResult(true); Wait(CancellationToken.None); }
                }
                finally { Interlocked.Decrement(ref active); }
            }
            public void Delete(string slot, CancellationToken token) { Record(); inner.Delete(slot, token); }
            public IReadOnlyList<string> ListSlots() { Record(); return inner.ListSlots(); }
            public void Dispose() { Record(); inner.Dispose(); Disposed = true; }
            private void Enter() { Record(); MaximumConcurrentCalls = Math.Max(MaximumConcurrentCalls, Interlocked.Increment(ref active)); }
            private void Record() { lock (Threads) Threads.Add(Thread.CurrentThread.ManagedThreadId); }
            private void Wait(CancellationToken token)
            {
                if (!Release.Wait(10000, token)) throw new TimeoutException("A controlled test gate was not released.");
            }
            private static TaskCompletionSource<bool> Completion() => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
