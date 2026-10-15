using System;

namespace Lumivale.Core.Persistence
{
    public enum PersistenceStatus
    {
        Success,
        NotFound,
        Corrupt,
        Incompatible,
        StorageError,
        InvalidData,
        NotInitialized,
        ServiceStopping
    }

    /// <summary>Immutable metadata; versions belong to the consuming game's DTO contract.</summary>
    public sealed class SaveSlotMetadata
    {
        public SaveSlotMetadata(string slotId, DateTime savedAtUtc, string modelId, int dataVersion)
        {
            PersistenceIdentifier.Validate(slotId, nameof(slotId));
            PersistenceIdentifier.Validate(modelId, nameof(modelId));
            if (savedAtUtc.Kind != DateTimeKind.Utc) throw new ArgumentException("The save date must be UTC.", nameof(savedAtUtc));
            if (dataVersion <= 0) throw new ArgumentOutOfRangeException(nameof(dataVersion));
            SlotId = slotId;
            SavedAtUtc = savedAtUtc;
            ModelId = modelId;
            DataVersion = dataVersion;
        }

        public string SlotId { get; }
        public DateTime SavedAtUtc { get; }
        public string ModelId { get; }
        public int DataVersion { get; }
    }

    /// <summary>Expected storage/data failures are diagnostic results; cancellation cancels the Task.</summary>
    public sealed class PersistenceResult<T>
    {
        public PersistenceResult(PersistenceStatus status, T data = default,
            SaveSlotMetadata metadata = null, bool recoveredFromBackup = false,
            string message = null, Exception exception = null)
        {
            Status = status;
            Data = data;
            Metadata = metadata;
            RecoveredFromBackup = recoveredFromBackup;
            Message = message;
            Exception = exception;
        }

        public PersistenceStatus Status { get; }
        public T Data { get; }
        public SaveSlotMetadata Metadata { get; }
        public bool RecoveredFromBackup { get; }
        public string Message { get; }
        public Exception Exception { get; }
        public bool IsSuccess => Status == PersistenceStatus.Success;
    }

    /// <summary>Listing preserves corrupt/incompatible slots instead of hiding them.</summary>
    public sealed class SaveSlotInfo
    {
        public SaveSlotInfo(string slotId, PersistenceStatus status, SaveSlotMetadata metadata = null,
            bool recoveredFromBackup = false, string message = null)
        {
            SlotId = slotId;
            Status = status;
            Metadata = metadata;
            RecoveredFromBackup = recoveredFromBackup;
            Message = message;
        }

        public string SlotId { get; }
        public PersistenceStatus Status { get; }
        public SaveSlotMetadata Metadata { get; }
        public bool RecoveredFromBackup { get; }
        public string Message { get; }
    }
}
