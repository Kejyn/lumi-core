using System;
using System.IO;
using System.Text;
using System.Threading;
using Lumivale.Core.Persistence;
using NUnit.Framework;

namespace Lumivale.Core.Persistence.Tests
{
    public sealed class FileSaveStorageTests
    {
        private string testRoot;
        private string gameDirectory;
        private FileSaveStorage storage;

        [SetUp]
        public void SetUp()
        {
            testRoot = Path.Combine(Path.GetTempPath(), "lumivale-storage-tests-" + Guid.NewGuid().ToString("N"));
            gameDirectory = Path.Combine(testRoot, "game-test-a");
            storage = new FileSaveStorage();
            storage.Initialize(gameDirectory);
        }

        [TearDown]
        public void TearDown()
        {
            storage?.Dispose();
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, true);
            }
        }

        [Test]
        public void OwnsDirectoryExclusivelyUntilDisposed()
        {
            using (var other = new FileSaveStorage())
            {
                IOException failure = Assert.Throws<IOException>(() => other.Initialize(gameDirectory));
                StringAssert.Contains("exclusively acquire", failure.Message);
                storage.Dispose();
                Assert.DoesNotThrow(() => other.Initialize(gameDirectory));
            }
        }

        [Test]
        public void RepeatedInitializationIsIdempotentAndCannotChangeIdentity()
        {
            storage.Write("one", "first", CancellationToken.None);
            storage.Initialize(gameDirectory);
            Assert.That(storage.DirectoryPath, Is.EqualTo(Path.GetFullPath(gameDirectory)));
            Assert.That(storage.Read("one", SaveStorageFile.Primary, 1024), Is.EqualTo("first"));
            Assert.Throws<InvalidOperationException>(() => storage.Initialize(Path.Combine(testRoot, "another-game")));
            Assert.That(Directory.Exists(Path.Combine(testRoot, "another-game")), Is.False);
        }

        [Test]
        public void RequiresAbsoluteDirectoryAndAnActiveInitializedLifetime()
        {
            using (var notStarted = new FileSaveStorage())
            {
                Assert.Throws<ArgumentException>(() => notStarted.Initialize("relative-folder"));
                Assert.Throws<InvalidOperationException>(() => notStarted.ListSlots());
                Assert.Throws<InvalidOperationException>(() => notStarted.Read("one", SaveStorageFile.Primary, 1024));
                Assert.Throws<InvalidOperationException>(() => notStarted.Write("one", "first", CancellationToken.None));
            }

            storage.Dispose();
            Assert.Throws<ObjectDisposedException>(() => storage.Initialize(gameDirectory));
            Assert.Throws<ObjectDisposedException>(() => storage.Read("one", SaveStorageFile.Primary, 1024));
            Assert.DoesNotThrow(() => storage.Dispose());
        }

        [Test]
        public void MissingPrimaryAndBackupAreReportedAsMissing()
        {
            Assert.That(storage.Read("missing", SaveStorageFile.Primary, 1024), Is.Null);
            Assert.That(storage.Read("missing", SaveStorageFile.Backup, 1024), Is.Null);
            Assert.That(storage.ListSlots(), Is.Empty);
        }

        [Test]
        public void NativeUtf8WriteRoundTripsWithoutBomAndSurvivesAServiceLifetime()
        {
            const string document = "{\"title\":\"Lumivale é 雪 ☀\",\"counter\":42}";
            storage.Write("slot_1", document, CancellationToken.None);
            Assert.That(storage.Read("slot_1", SaveStorageFile.Primary, 1024), Is.EqualTo(document));
            byte[] bytes = File.ReadAllBytes(PathFor("slot_1", ".json"));
            Assert.That(bytes[0], Is.EqualTo((byte)'{'));
            Assert.That(File.Exists(PathFor("slot_1", ".tmp")), Is.False);
            storage.Dispose();
            storage = new FileSaveStorage();
            storage.Initialize(gameDirectory);
            Assert.That(storage.Read("slot_1", SaveStorageFile.Primary, 1024), Is.EqualTo(document));
        }

        [Test]
        public void NativeReplacementKeepsExactlyThePreviousCommittedVersion()
        {
            storage.Write("one", "version-one", CancellationToken.None);
            Assert.That(storage.Read("one", SaveStorageFile.Backup, 1024), Is.Null);
            storage.Write("one", "version-two", CancellationToken.None);
            Assert.That(storage.Read("one", SaveStorageFile.Primary, 1024), Is.EqualTo("version-two"));
            Assert.That(storage.Read("one", SaveStorageFile.Backup, 1024), Is.EqualTo("version-one"));
            storage.Write("one", "version-three", CancellationToken.None);
            Assert.That(storage.Read("one", SaveStorageFile.Primary, 1024), Is.EqualTo("version-three"));
            Assert.That(storage.Read("one", SaveStorageFile.Backup, 1024), Is.EqualTo("version-two"));
            Assert.That(File.Exists(PathFor("one", ".tmp")), Is.False);
        }

        [Test]
        public void InterruptedStagingIsIgnoredAndNeverPromotedAsASave()
        {
            File.WriteAllText(PathFor("interrupted", ".tmp"), "{ incomplete");
            Assert.That(storage.Read("interrupted", SaveStorageFile.Primary, 1024), Is.Null);
            Assert.That(storage.Read("interrupted", SaveStorageFile.Backup, 1024), Is.Null);
            Assert.That(storage.ListSlots(), Is.Empty);
            storage.Write("interrupted", "committed-document", CancellationToken.None);
            Assert.That(storage.Read("interrupted", SaveStorageFile.Primary, 1024), Is.EqualTo("committed-document"));
            Assert.That(File.Exists(PathFor("interrupted", ".tmp")), Is.False);
        }

        [Test]
        public void OrphanBackupRemainsReadableAndIsPreservedWhenCreatingNewPrimary()
        {
            File.WriteAllText(PathFor("one", ".bak"), "last-valid-version");
            File.WriteAllText(PathFor("one", ".tmp"), "partial-document");
            Assert.That(storage.Read("one", SaveStorageFile.Primary, 1024), Is.Null);
            Assert.That(storage.Read("one", SaveStorageFile.Backup, 1024), Is.EqualTo("last-valid-version"));
            CollectionAssert.AreEqual(new[] { "one" }, storage.ListSlots());
            storage.Write("one", "new-version", CancellationToken.None);
            Assert.That(storage.Read("one", SaveStorageFile.Backup, 1024), Is.EqualTo("last-valid-version"));
        }

        [Test]
        public void SlotListingIncludesOnlyOwnedValidPrimaryAndBackupNamesInStableOrder()
        {
            File.WriteAllText(PathFor("zeta", ".json"), "z");
            File.WriteAllText(PathFor("alpha", ".bak"), "a");
            File.WriteAllText(PathFor("alpha", ".json"), "a2");
            File.WriteAllText(PathFor("only-temp", ".tmp"), "incomplete");
            File.WriteAllText(Path.Combine(gameDirectory, "foreign.json"), "foreign");
            File.WriteAllText(Path.Combine(gameDirectory, "slot-UPPER.json"), "foreign");
            File.WriteAllText(Path.Combine(gameDirectory, "SLOT-other.json"), "foreign");
            File.WriteAllText(Path.Combine(gameDirectory, "slot-.json"), "foreign");
            CollectionAssert.AreEqual(new[] { "alpha", "zeta" }, storage.ListSlots());
        }

        [Test]
        public void SeparateGameDirectoriesHaveIndependentSlotsAndLocks()
        {
            using (var other = new FileSaveStorage())
            {
                other.Initialize(Path.Combine(testRoot, "game-test-b"));
                storage.Write("one", "game-a", CancellationToken.None);
                other.Write("one", "game-b", CancellationToken.None);
                Assert.That(storage.Read("one", SaveStorageFile.Primary, 1024), Is.EqualTo("game-a"));
                Assert.That(other.Read("one", SaveStorageFile.Primary, 1024), Is.EqualTo("game-b"));
                storage.Delete("one", CancellationToken.None);
                Assert.That(other.Read("one", SaveStorageFile.Primary, 1024), Is.EqualTo("game-b"));
            }
        }

        [Test]
        public void InvalidUtf8ProducesADiagnosticInsteadOfReplacingBytes()
        {
            File.WriteAllBytes(PathFor("invalid", ".json"), new byte[] { 0xc3, 0x28 });
            Assert.Throws<DecoderFallbackException>(() => storage.Read("invalid", SaveStorageFile.Primary, 1024));
            CollectionAssert.AreEqual(new byte[] { 0xc3, 0x28 }, File.ReadAllBytes(PathFor("invalid", ".json")));
        }

        [Test]
        public void ReadRejectsFilesOverTheConfiguredByteLimit()
        {
            File.WriteAllText(PathFor("large", ".json"), "123456789");
            Assert.Throws<InvalidDataException>(() => storage.Read("large", SaveStorageFile.Primary, 8));
            Assert.That(storage.Read("large", SaveStorageFile.Primary, 9), Is.EqualTo("123456789"));
            Assert.Throws<ArgumentOutOfRangeException>(() => storage.Read("large", SaveStorageFile.Primary, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => storage.Read("large", (SaveStorageFile)99, 9));
        }

        [Test]
        public void OversizedWriteLeavesTheExistingDocumentAndBackupUntouched()
        {
            storage.Dispose();
            storage = new FileSaveStorage(8);
            storage.Initialize(gameDirectory);
            storage.Write("one", "previous", CancellationToken.None);
            Assert.Throws<InvalidDataException>(() => storage.Write("one", "ééééé", CancellationToken.None));
            Assert.That(storage.Read("one", SaveStorageFile.Primary, 8), Is.EqualTo("previous"));
            Assert.That(storage.Read("one", SaveStorageFile.Backup, 8), Is.Null);
            Assert.That(File.Exists(PathFor("one", ".tmp")), Is.False);
        }

        [Test]
        public void InvalidUnicodeWriteDoesNotTouchThePreviousValidDocument()
        {
            storage.Write("one", "previous", CancellationToken.None);
            Assert.Throws<EncoderFallbackException>(() => storage.Write("one", "\ud800", CancellationToken.None));
            Assert.That(storage.Read("one", SaveStorageFile.Primary, 1024), Is.EqualTo("previous"));
        }

        [Test]
        public void FailedTemporaryWritePreservesBothCommittedFiles()
        {
            storage.Write("one", "backup-version", CancellationToken.None);
            storage.Write("one", "current-version", CancellationToken.None);
            Directory.CreateDirectory(PathFor("one", ".tmp"));
            Exception failure = Assert.Catch(() => storage.Write("one", "rejected-version", CancellationToken.None));
            Assert.That(failure, Is.InstanceOf<UnauthorizedAccessException>().Or.InstanceOf<IOException>());
            Assert.That(storage.Read("one", SaveStorageFile.Primary, 1024), Is.EqualTo("current-version"));
            Assert.That(storage.Read("one", SaveStorageFile.Backup, 1024), Is.EqualTo("backup-version"));
        }

        [Test]
        public void FailedNativeReplacementPreservesThePreviousDocumentAndRemovesStaging()
        {
            RequireWindowsSharingSemantics();
            storage.Write("one", "backup-version", CancellationToken.None);
            storage.Write("one", "current-version", CancellationToken.None);
            using (var lockedPrimary = new FileStream(PathFor("one", ".json"), FileMode.Open,
                FileAccess.Read, FileShare.None))
            {
                Assert.Throws<IOException>(() => storage.Write("one", "rejected-version", CancellationToken.None));
            }

            Assert.That(storage.Read("one", SaveStorageFile.Primary, 1024), Is.EqualTo("current-version"));
            Assert.That(storage.Read("one", SaveStorageFile.Backup, 1024), Is.EqualTo("backup-version"));
            Assert.That(File.Exists(PathFor("one", ".tmp")), Is.False);
        }

        [Test]
        public void PreCancelledWriteDoesNotModifyPrimaryBackupOrStaging()
        {
            storage.Write("one", "backup-version", CancellationToken.None);
            storage.Write("one", "current-version", CancellationToken.None);
            File.WriteAllText(PathFor("one", ".tmp"), "interrupted-version");
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                Assert.Throws<OperationCanceledException>(() => storage.Write("one", "cancelled-version", cancellation.Token));
            }

            Assert.That(storage.Read("one", SaveStorageFile.Primary, 1024), Is.EqualTo("current-version"));
            Assert.That(storage.Read("one", SaveStorageFile.Backup, 1024), Is.EqualTo("backup-version"));
            Assert.That(File.ReadAllText(PathFor("one", ".tmp")), Is.EqualTo("interrupted-version"));
        }

        [Test]
        public void DeleteRemovesAllOwnedSlotFilesButPreservesOtherSlots()
        {
            storage.Write("one", "old-one", CancellationToken.None);
            storage.Write("one", "one", CancellationToken.None);
            File.WriteAllText(PathFor("one", ".tmp"), "incomplete");
            storage.Write("two", "two", CancellationToken.None);
            storage.Delete("one", CancellationToken.None);
            Assert.That(storage.Read("one", SaveStorageFile.Primary, 1024), Is.Null);
            Assert.That(storage.Read("one", SaveStorageFile.Backup, 1024), Is.Null);
            Assert.That(File.Exists(PathFor("one", ".tmp")), Is.False);
            Assert.That(storage.Read("two", SaveStorageFile.Primary, 1024), Is.EqualTo("two"));
            Assert.DoesNotThrow(() => storage.Delete("missing", CancellationToken.None));
            CollectionAssert.AreEqual(new[] { "two" }, storage.ListSlots());
        }

        [Test]
        public void FailedBackupDeletionPreservesPrimaryRatherThanResurrectingOlderData()
        {
            RequireWindowsSharingSemantics();
            storage.Write("one", "old-version", CancellationToken.None);
            storage.Write("one", "current-version", CancellationToken.None);
            using (var lockedBackup = new FileStream(PathFor("one", ".bak"), FileMode.Open,
                FileAccess.Read, FileShare.Read))
            {
                Assert.Throws<IOException>(() => storage.Delete("one", CancellationToken.None));
            }

            Assert.That(storage.Read("one", SaveStorageFile.Primary, 1024), Is.EqualTo("current-version"));
            Assert.That(storage.Read("one", SaveStorageFile.Backup, 1024), Is.EqualTo("old-version"));
            storage.Delete("one", CancellationToken.None);
            Assert.That(storage.ListSlots(), Is.Empty);
        }

        [Test]
        public void PreCancelledDeleteLeavesAllRecoveryFilesAndPrimaryUntouched()
        {
            storage.Write("one", "old-version", CancellationToken.None);
            storage.Write("one", "current-version", CancellationToken.None);
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                Assert.Throws<OperationCanceledException>(() => storage.Delete("one", cancellation.Token));
            }

            Assert.That(storage.Read("one", SaveStorageFile.Primary, 1024), Is.EqualTo("current-version"));
            Assert.That(storage.Read("one", SaveStorageFile.Backup, 1024), Is.EqualTo("old-version"));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("../outside")]
        [TestCase("Upper")]
        [TestCase("é")]
        [TestCase("two slots")]
        [TestCase("con:")]
        [TestCase("a/b")]
        [TestCase("a\\b")]
        [TestCase("a.")]
        [TestCase("a\0b")]
        [TestCase("abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyzabcdefghijklm")]
        [TestCase("-slot")]
        [TestCase("_slot")]
        [TestCase("con")]
        [TestCase("prn")]
        [TestCase("aux")]
        [TestCase("nul")]
        [TestCase("com1")]
        [TestCase("com9")]
        [TestCase("lpt1")]
        [TestCase("lpt9")]
        public void InvalidSlotIdsAreRejectedBeforeAnyFilePathIsConstructed(string slotId)
        {
            Assert.Throws<ArgumentException>(() => storage.Read(slotId, SaveStorageFile.Primary, 1024));
            Assert.Throws<ArgumentException>(() => storage.Write(slotId, "document", CancellationToken.None));
            Assert.Throws<ArgumentException>(() => storage.Delete(slotId, CancellationToken.None));
            Assert.That(storage.ListSlots(), Is.Empty);
            Assert.That(Directory.GetFiles(gameDirectory), Has.Length.EqualTo(1));
        }

        private string PathFor(string slotId, string extension)
        {
            return Path.Combine(gameDirectory, "slot-" + slotId + extension);
        }

        private static void RequireWindowsSharingSemantics()
        {
            if (Path.DirectorySeparatorChar != '\\')
            {
                Assert.Ignore("This sharing-violation failure injection is specific to native Windows file sharing.");
            }
        }
    }
}
