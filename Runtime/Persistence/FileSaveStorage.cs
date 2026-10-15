using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace Lumivale.Core.Persistence
{
    /// <summary>
    /// Local UTF-8 save documents. The service owns validation and invokes this storage on a worker.
    /// One storage instance exclusively owns its configured game directory until disposed.
    /// </summary>
    public sealed class FileSaveStorage : ISaveStorage
    {
        public const int DefaultMaxFileBytes = 4 * 1024 * 1024;

        private const string SlotPrefix = "slot-";
        private const string PrimaryExtension = ".json";
        private const string BackupExtension = ".bak";
        private const string TemporaryExtension = ".tmp";
        private const string LockFileName = ".lumi-save.lock";

        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private readonly object sync = new object();
        private readonly int maxFileBytes;
        private string directoryPath;
        private FileStream directoryLock;
        private bool disposed;

        public FileSaveStorage(int maxFileBytes = DefaultMaxFileBytes)
        {
            if (maxFileBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxFileBytes));
            }

            this.maxFileBytes = maxFileBytes;
        }

        /// <summary>The normalized, exclusively owned directory after initialization.</summary>
        public string DirectoryPath
        {
            get
            {
                lock (sync)
                {
                    return directoryPath;
                }
            }
        }

        public void Initialize(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathRooted(directory))
            {
                throw new ArgumentException("An explicit absolute game save directory is required.", nameof(directory));
            }

            string normalized = Path.GetFullPath(directory);
            lock (sync)
            {
                ThrowIfDisposed();
                if (directoryLock != null)
                {
                    StringComparison comparison = Path.DirectorySeparatorChar == '\\'
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal;
                    if (!string.Equals(directoryPath, normalized, comparison))
                    {
                        throw new InvalidOperationException("A storage instance cannot change its game directory.");
                    }

                    return;
                }

                Directory.CreateDirectory(normalized);
                RejectReparsePoint(normalized);
                string lockPath = Path.Combine(normalized, LockFileName);
                RejectReparsePointIfPresent(lockPath);
                FileStream acquired;
                try
                {
                    acquired = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                        FileShare.None);
                }
                catch (IOException exception)
                {
                    throw new IOException("Could not exclusively acquire the game save directory. " +
                        "Another storage instance may own it: " + normalized, exception);
                }

                directoryLock = acquired;
                directoryPath = normalized;
            }
        }

        public string Read(string slotId, SaveStorageFile file, int maxBytes)
        {
            PersistenceIdentifier.Validate(slotId, nameof(slotId));
            if (maxBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxBytes));
            }

            if (file != SaveStorageFile.Primary && file != SaveStorageFile.Backup)
            {
                throw new ArgumentOutOfRangeException(nameof(file));
            }

            lock (sync)
            {
                RequireInitialized();
                string path = SlotPath(slotId, file == SaveStorageFile.Primary
                    ? PrimaryExtension : BackupExtension);
                FileStream stream;
                try
                {
                    RejectReparsePointIfPresent(path);
                    stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                }
                catch (FileNotFoundException)
                {
                    return null;
                }
                catch (DirectoryNotFoundException)
                {
                    return null;
                }

                using (stream)
                {
                    long length = stream.Length;
                    if (length > maxBytes)
                    {
                        throw new InvalidDataException("The save file exceeds the configured byte limit.");
                    }

                    byte[] bytes = new byte[(int)length];
                    int offset = 0;
                    while (offset < bytes.Length)
                    {
                        int read = stream.Read(bytes, offset, bytes.Length - offset);
                        if (read == 0)
                        {
                            throw new IOException("The save file changed while being read.");
                        }

                        offset += read;
                    }

                    return Utf8.GetString(bytes);
                }
            }
        }

        public void Write(string slotId, string document, CancellationToken cancellationToken)
        {
            PersistenceIdentifier.Validate(slotId, nameof(slotId));
            if (document == null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            cancellationToken.ThrowIfCancellationRequested();
            int byteCount = Utf8.GetByteCount(document);
            if (byteCount > maxFileBytes)
            {
                throw new InvalidDataException("The serialized save exceeds the configured byte limit.");
            }

            byte[] bytes = Utf8.GetBytes(document);
            lock (sync)
            {
                RequireInitialized();
                cancellationToken.ThrowIfCancellationRequested();
                string primary = SlotPath(slotId, PrimaryExtension);
                string backup = SlotPath(slotId, BackupExtension);
                string temporary = SlotPath(slotId, TemporaryExtension);
                bool primaryExists = RejectReparsePointIfPresent(primary);
                RejectReparsePointIfPresent(backup);
                RejectReparsePointIfPresent(temporary);
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write,
                        FileShare.None))
                    {
                        const int chunkSize = 64 * 1024;
                        for (int offset = 0; offset < bytes.Length; offset += chunkSize)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            stream.Write(bytes, offset, Math.Min(chunkSize, bytes.Length - offset));
                        }

                        stream.Flush(true);
                    }

                    // This is the final cancellation checkpoint. Once native commit starts, its result
                    // describes whether the document was committed, even if the token changes meanwhile.
                    cancellationToken.ThrowIfCancellationRequested();
                    if (primaryExists)
                    {
                        File.Replace(temporary, primary, backup);
                    }
                    else
                    {
                        File.Move(temporary, primary);
                    }
                }
                finally
                {
                    // A failed cleanup must not disguise the commit result or the primary write error.
                    TryDeleteTemporary(temporary);
                }
            }
        }

        public void Delete(string slotId, CancellationToken cancellationToken)
        {
            PersistenceIdentifier.Validate(slotId, nameof(slotId));
            lock (sync)
            {
                RequireInitialized();
                cancellationToken.ThrowIfCancellationRequested();
                string backup = SlotPath(slotId, BackupExtension);
                string temporary = SlotPath(slotId, TemporaryExtension);
                string primary = SlotPath(slotId, PrimaryExtension);
                RejectReparsePointIfPresent(backup);
                RejectReparsePointIfPresent(temporary);
                RejectReparsePointIfPresent(primary);

                // Keep the primary until all recovery/staging files are gone. If cleanup fails,
                // report that failure and preserve the primary rather than resurrecting a backup.
                File.Delete(backup);
                cancellationToken.ThrowIfCancellationRequested();
                File.Delete(temporary);
                cancellationToken.ThrowIfCancellationRequested();
                File.Delete(primary);
            }
        }

        public IReadOnlyList<string> ListSlots()
        {
            lock (sync)
            {
                RequireInitialized();
                var identifiers = new SortedSet<string>(StringComparer.Ordinal);
                foreach (string path in Directory.EnumerateFiles(directoryPath, SlotPrefix + "*"))
                {
                    string name = Path.GetFileName(path);
                    if (!name.StartsWith(SlotPrefix, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    string extension = Path.GetExtension(name);
                    if (extension != PrimaryExtension && extension != BackupExtension)
                    {
                        continue;
                    }

                    string slotId = name.Substring(SlotPrefix.Length,
                        name.Length - SlotPrefix.Length - extension.Length);
                    if (PersistenceIdentifier.IsValid(slotId))
                    {
                        RejectReparsePoint(path);
                        identifiers.Add(slotId);
                    }
                }

                return new List<string>(identifiers).AsReadOnly();
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                directoryLock?.Dispose();
                directoryLock = null;
            }
        }

        private string SlotPath(string slotId, string extension)
        {
            return Path.Combine(directoryPath, SlotPrefix + slotId + extension);
        }

        private void RequireInitialized()
        {
            ThrowIfDisposed();
            if (directoryLock == null)
            {
                throw new InvalidOperationException("The save storage has not been initialized.");
            }

            RejectReparsePoint(directoryPath);
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(FileSaveStorage));
            }
        }

        private static bool RejectReparsePointIfPresent(string path)
        {
            try
            {
                RejectReparsePoint(path);
                return true;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
        }

        private static void RejectReparsePoint(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Symbolic links and reparse points are not supported in the owned save directory: " + path);
            }
        }

        private static void TryDeleteTemporary(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
