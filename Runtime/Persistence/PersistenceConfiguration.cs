using System;
using System.IO;
using UnityEngine;

namespace Lumivale.Core.Persistence
{
    /// <summary>Owns the explicit game identity and its isolated native save directory.</summary>
    public sealed class PersistenceConfiguration
    {
        public const int DefaultMaxFileBytes = 4 * 1024 * 1024;

        /// <param name="rootPath">An absolute test/platform root, or Unity's persistentDataPath.</param>
        public PersistenceConfiguration(string gameId, string rootPath = null,
            int maxFileBytes = DefaultMaxFileBytes)
        {
            PersistenceIdentifier.Validate(gameId, nameof(gameId));
            if (maxFileBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxFileBytes));
            // Capture this Unity API on the calling main thread, never in disk workers.
            string root = rootPath ?? Application.persistentDataPath;
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathRooted(root))
                throw new ArgumentException("A non-empty absolute persistent-data root is required.", nameof(rootPath));
            GameId = gameId;
            RootPath = Path.GetFullPath(root);
            StorageDirectory = Path.GetFullPath(Path.Combine(RootPath, "LumiSaves", "game-" + GameId));
            MaxFileBytes = maxFileBytes;
        }

        public string GameId { get; }
        public string RootPath { get; }
        public string StorageDirectory { get; }
        public int MaxFileBytes { get; }
    }

    internal static class PersistenceIdentifier
    {
        internal static bool IsValid(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 64 || !IsAlphaNumeric(value[0])) return false;
            foreach (char character in value)
                if (!IsAlphaNumeric(character) && character != '_' && character != '-') return false;
            if (value == "con" || value == "prn" || value == "aux" || value == "nul") return false;
            if (value.Length == 4 && value[3] >= '1' && value[3] <= '9' &&
                (value.StartsWith("com", StringComparison.Ordinal) || value.StartsWith("lpt", StringComparison.Ordinal)))
                return false;
            return true;
        }

        internal static void Validate(string value, string parameterName)
        {
            if (!IsValid(value))
                throw new ArgumentException("Use 1-64 lowercase ASCII letters/digits, '-' or '_', starting with a letter/digit; reserved device names are forbidden.", parameterName);
        }

        private static bool IsAlphaNumeric(char value) => value >= 'a' && value <= 'z' || value >= '0' && value <= '9';
    }
}
