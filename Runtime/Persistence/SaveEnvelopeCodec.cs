using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Lumivale.Core.Persistence
{
    internal sealed class EnvelopeReadResult
    {
        public PersistenceStatus Status;
        public string Message;
        public SaveSlotMetadata Metadata;
        public string PayloadJson;
        public int FormatVersion;
    }

    internal static class SaveEnvelopeCodec
    {
        public const int FormatVersion = 1;
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        public static string Encode(string gameId, SaveSlotMetadata metadata, string payloadJson)
        {
            PersistenceIdentifier.Validate(gameId, nameof(gameId));
            if (metadata == null) throw new ArgumentNullException(nameof(metadata));
            PersistenceIdentifier.Validate(metadata.SlotId, nameof(metadata));
            PersistenceIdentifier.Validate(metadata.ModelId, nameof(metadata));
            if (metadata.SavedAtUtc.Kind != DateTimeKind.Utc || metadata.DataVersion < 1)
                throw new ArgumentException("Save metadata requires a UTC timestamp and a positive data version.", nameof(metadata));
            if (StrictJson.Parse(payloadJson).Kind != JsonValueKind.Object)
                throw new FormatException("The save payload must be a JSON object.");
            var document = new Document
            {
                formatVersion = FormatVersion,
                gameId = gameId,
                slotId = metadata.SlotId,
                savedAtUtc = metadata.SavedAtUtc.ToString("o", CultureInfo.InvariantCulture),
                modelId = metadata.ModelId,
                dataVersion = metadata.DataVersion,
                payloadJson = payloadJson
            };
            document.sha256 = Checksum(document);
            return JsonUtility.ToJson(document);
        }

        public static EnvelopeReadResult Decode(string json, string expectedGameId, string expectedSlotId)
        {
            PersistenceIdentifier.Validate(expectedGameId, nameof(expectedGameId));
            PersistenceIdentifier.Validate(expectedSlotId, nameof(expectedSlotId));
            try
            {
                JsonValue root = StrictJson.Parse(json);
                if (root.Kind != JsonValueKind.Object) throw new FormatException("The save envelope root must be an object.");
                int format = StrictJson.PositiveInteger(Member(root, "formatVersion"), "formatVersion");
                // Future formats can change all remaining fields; do not apply V1 field rules to them.
                if (format != FormatVersion)
                    return new EnvelopeReadResult { Status = PersistenceStatus.Incompatible, FormatVersion = format,
                        Message = "Unsupported save file format version: " + format + "." };
                string gameId = Text(root, "gameId");
                string slotId = Text(root, "slotId");
                string modelId = Text(root, "modelId");
                if (!PersistenceIdentifier.IsValid(gameId) || !PersistenceIdentifier.IsValid(slotId) ||
                    !PersistenceIdentifier.IsValid(modelId)) throw new FormatException("Save identifiers are invalid.");
                if (!string.Equals(gameId, expectedGameId, StringComparison.Ordinal) ||
                    !string.Equals(slotId, expectedSlotId, StringComparison.Ordinal))
                    throw new FormatException("The save envelope belongs to a different game or slot.");
                string utcText = Text(root, "savedAtUtc");
                if (!DateTime.TryParseExact(utcText, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
                    out DateTime utc) || utc.Kind != DateTimeKind.Utc)
                    throw new FormatException("The save timestamp must be an ISO 8601 UTC timestamp.");
                int dataVersion = StrictJson.PositiveInteger(Member(root, "dataVersion"), "dataVersion");
                string payload = Text(root, "payloadJson");
                string checksum = Text(root, "sha256");
                var document = new Document { formatVersion = format, gameId = gameId, slotId = slotId,
                    modelId = modelId, savedAtUtc = utcText, dataVersion = dataVersion, payloadJson = payload };
                if (!string.Equals(checksum, Checksum(document), StringComparison.Ordinal))
                    throw new FormatException("The save integrity checksum does not match.");
                if (StrictJson.Parse(payload).Kind != JsonValueKind.Object)
                    throw new FormatException("The save payload must be a JSON object.");
                return new EnvelopeReadResult { Status = PersistenceStatus.Success, FormatVersion = format,
                    Metadata = new SaveSlotMetadata(slotId, utc, modelId, dataVersion), PayloadJson = payload };
            }
            catch (FormatException exception)
            {
                return new EnvelopeReadResult { Status = PersistenceStatus.Corrupt, Message = exception.Message };
            }
            catch (EncoderFallbackException exception)
            {
                return new EnvelopeReadResult { Status = PersistenceStatus.Corrupt, Message = exception.Message };
            }
        }

        private static JsonValue Member(JsonValue root, string name)
        {
            if (!root.Members.TryGetValue(name, out JsonValue value)) throw new FormatException("Missing save member: " + name + ".");
            return value;
        }

        private static string Text(JsonValue root, string name)
        {
            JsonValue value = Member(root, name);
            if (value.Kind != JsonValueKind.String) throw new FormatException("Save member '" + name + "' must be a string.");
            return value.Text;
        }

        private static string Checksum(Document document)
        {
            // Each UTF-8 field has an explicit 32-bit little-endian byte length. No separators can collide.
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, Utf8, true))
                {
                    Write(writer, document.formatVersion.ToString(CultureInfo.InvariantCulture));
                    Write(writer, document.gameId);
                    Write(writer, document.slotId);
                    Write(writer, document.savedAtUtc);
                    Write(writer, document.modelId);
                    Write(writer, document.dataVersion.ToString(CultureInfo.InvariantCulture));
                    Write(writer, document.payloadJson);
                }
                using (SHA256 sha = SHA256.Create())
                {
                    byte[] hash = sha.ComputeHash(stream.ToArray());
                    var hexadecimal = new StringBuilder(hash.Length * 2);
                    foreach (byte item in hash) hexadecimal.Append(item.ToString("x2", CultureInfo.InvariantCulture));
                    return hexadecimal.ToString();
                }
            }
        }

        private static void Write(BinaryWriter writer, string value)
        {
            byte[] bytes = Utf8.GetBytes(value);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        [Serializable]
        private sealed class Document
        {
            public int formatVersion;
            public string gameId;
            public string slotId;
            public string savedAtUtc;
            public string modelId;
            public int dataVersion;
            public string payloadJson;
            public string sha256;
        }
    }
}
