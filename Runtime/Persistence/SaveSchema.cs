using System;

namespace Lumivale.Core.Persistence
{
    /// <summary>Explicit game-owned model identity, version and optional semantic validator.</summary>
    public sealed class SaveSchema<T>
    {
        private readonly Action<T> validator;

        public SaveSchema(string modelId, int version, Action<T> validate = null)
        {
            PersistenceIdentifier.Validate(modelId, nameof(modelId));
            if (version <= 0) throw new ArgumentOutOfRangeException(nameof(version));
            ModelId = modelId;
            Version = version;
            validator = validate;
        }

        public string ModelId { get; }
        public int Version { get; }

        /// <summary>Runs on the public caller's main thread; throw to reject game-specific invalid values.</summary>
        public void Validate(T data) => validator?.Invoke(data);
    }
}
