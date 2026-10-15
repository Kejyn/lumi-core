using System;
using System.Collections.Generic;
using System.Threading;

namespace Lumivale.Core.Persistence
{
    public enum SaveStorageFile { Primary, Backup }

    /// <summary>
    /// Synchronous native-I/O adapter, invoked only on service disk workers. Read returns null
    /// for absence; access failures throw. Write honors cancellation before an indivisible commit,
    /// then returns success after commit even if cancellation was requested concurrently.
    /// One adapter owns its directory exclusively until Dispose after all work has drained.
    /// </summary>
    public interface ISaveStorage : IDisposable
    {
        void Initialize(string directory);
        string Read(string slotId, SaveStorageFile file, int maxBytes);
        void Write(string slotId, string document, CancellationToken cancellationToken);
        void Delete(string slotId, CancellationToken cancellationToken);
        IReadOnlyList<string> ListSlots();
    }
}
