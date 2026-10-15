using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lumivale.Core.Bootstrap;
using UnityEngine;

namespace Lumivale.Core.Settings
{
    public enum SettingsLoadStatus
    {
        Missing,
        Loaded,
        Repaired,
        InvalidData,
        UnsupportedVersion,
        StorageError
    }

    /// <summary>
    /// Owns validated local preferences. Graphics application and persistence are explicit operations.
    /// Call lifecycle and public methods on Unity's main thread.
    /// </summary>
    public sealed class SettingsService : ILumiService
    {
        public const int SchemaVersion = 1;
        private static readonly IReadOnlyCollection<string> NoDependencies = Array.Empty<string>();
        private readonly SettingsConfiguration configuration;
        private readonly ISettingsStorage storage;
        private readonly IGraphicsSettingsBackend graphics;
        private SettingsValues current;
        private SettingsValues defaults;

        public SettingsService(SettingsConfiguration configuration,
            ISettingsStorage storage = null, IGraphicsSettingsBackend graphics = null)
        {
            this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            this.storage = storage ?? new PlayerPrefsSettingsStorage();
            this.graphics = graphics ?? new UnityGraphicsSettingsBackend();
        }

        public string Id => "settings";
        public IReadOnlyCollection<string> Dependencies => NoDependencies;
        public bool IsInitialized { get; private set; }
        public string StorageKey => configuration.StorageKey;
        public SettingsLoadStatus LastLoadStatus { get; private set; }
        public string LastLoadMessage { get; private set; }
        public IReadOnlyList<string> SupportedLanguages => configuration.SupportedLanguages;

        public SettingsValues Current
        {
            get
            {
                EnsureInitialized();
                return current.Clone();
            }
        }

        public SettingsValues Defaults
        {
            get
            {
                EnsureInitialized();
                return defaults.Clone();
            }
        }

        /// <summary>Refreshes platform choices without changing preferences or the display.</summary>
        public GraphicsCapabilities Capabilities => graphics.GetCapabilities();

        /// <summary>Receives an independent snapshot after an effective in-memory preference change.</summary>
        public event Action<SettingsValues> Changed;
        public event Action<Exception> ObserverFailed;

        public Task InitializeAsync(ServiceContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsInitialized)
            {
                return Task.CompletedTask;
            }

            GraphicsCapabilities capabilities = graphics.GetCapabilities();
            SettingsValues validatedDefaults = Validate(configuration.Defaults, null, capabilities);
            SettingsValues loaded = Load(validatedDefaults, capabilities);
            cancellationToken.ThrowIfCancellationRequested();
            defaults = validatedDefaults;
            current = loaded;
            IsInitialized = true;
            return Task.CompletedTask;
        }

        public Task ShutdownAsync(CancellationToken cancellationToken)
        {
            // Explicit Save owns persistence. Shutdown releases observers even with a cancelled token.
            IsInitialized = false;
            current = null;
            defaults = null;
            Changed = null;
            ObserverFailed = null;
            return Task.CompletedTask;
        }

        /// <summary>Validates and copies a snapshot. Does not apply graphics or write storage.</summary>
        public SettingsValues Update(SettingsValues values)
        {
            EnsureInitialized();
            if (values == null)
            {
                throw new ArgumentNullException(nameof(values));
            }

            GraphicsCapabilities capabilities = graphics.GetCapabilities();
            defaults = Validate(configuration.Defaults, null, capabilities);
            SettingsValues validated = Validate(values, defaults, capabilities);
            SetCurrent(validated);
            return validated.Clone();
        }

        /// <summary>Restores validated project defaults in memory. Apply and Save remain explicit.</summary>
        public SettingsValues ResetDefaults()
        {
            EnsureInitialized();
            defaults = Validate(configuration.Defaults, null, graphics.GetCapabilities());
            SettingsValues reset = defaults.Clone();
            SetCurrent(reset);
            return reset.Clone();
        }

        /// <summary>
        /// Revalidates current choices and applies native display/quality settings. Unity display size
        /// changes complete at the end of the frame; the Editor Game View may constrain the result.
        /// </summary>
        public void ApplyGraphics()
        {
            EnsureInitialized();
            GraphicsCapabilities capabilities = graphics.GetCapabilities();
            SettingsValues newDefaults = Validate(configuration.Defaults, null, capabilities);
            SettingsValues validated = Validate(current, newDefaults, capabilities);
            graphics.Apply(new DisplayResolution(validated.Width, validated.Height),
                validated.DisplayMode, validated.QualityName);
            defaults = newDefaults;
            SetCurrent(validated);
        }

        /// <summary>Writes one versioned JSON preference document and flushes PlayerPrefs when native.</summary>
        public void Save()
        {
            EnsureInitialized();
            var document = new PreferenceDocument { schemaVersion = SchemaVersion, values = current.Clone() };
            storage.Save(configuration.StorageKey, JsonUtility.ToJson(document));
        }

        private SettingsValues Load(SettingsValues validatedDefaults, GraphicsCapabilities capabilities)
        {
            string json;
            try
            {
                if (!storage.TryLoad(configuration.StorageKey, out json))
                {
                    LastLoadStatus = SettingsLoadStatus.Missing;
                    LastLoadMessage = "No preference document exists; project defaults are used.";
                    return validatedDefaults.Clone();
                }
            }
            catch (Exception exception)
            {
                LastLoadStatus = SettingsLoadStatus.StorageError;
                LastLoadMessage = $"Preference storage could not be read: {exception.Message}";
                return validatedDefaults.Clone();
            }

            try
            {
                if (string.IsNullOrWhiteSpace(json))
                {
                    throw new ArgumentException("The preference document is empty.");
                }

                PreferenceHeader header = JsonUtility.FromJson<PreferenceHeader>(json);
                if (header == null)
                {
                    throw new ArgumentException("The preference document is missing.");
                }

                ValidateKnownFieldTypes(json, 0, true);
                if (header.schemaVersion != SchemaVersion)
                {
                    LastLoadStatus = SettingsLoadStatus.UnsupportedVersion;
                    LastLoadMessage = $"Preference schema {header.schemaVersion} is not supported; defaults are used. " +
                        "The stored document is preserved until an explicit Save.";
                    return validatedDefaults.Clone();
                }

                if (!HasRootValuesObject(json))
                {
                    throw new ArgumentException("The preference values object is missing.");
                }

                // Overwrite seeded values: fields absent from a V1 document retain project defaults.
                // Explicit zero volumes remain zero, instead of being mistaken for absent fields.
                var document = new PreferenceDocument
                {
                    schemaVersion = SchemaVersion,
                    values = validatedDefaults.Clone()
                };
                JsonUtility.FromJsonOverwrite(json, document);
                SettingsValues repaired = Validate(document.values, validatedDefaults, capabilities);
                bool changed = !repaired.SameAs(document.values);
                LastLoadStatus = changed ? SettingsLoadStatus.Repaired : SettingsLoadStatus.Loaded;
                LastLoadMessage = changed ? "Invalid or unavailable saved choices were replaced with valid values." :
                    "Version 1 preferences were loaded; missing fields use project defaults.";
                return repaired;
            }
            catch (Exception exception)
            {
                LastLoadStatus = SettingsLoadStatus.InvalidData;
                LastLoadMessage = $"The preference document is invalid: {exception.Message}. Defaults are used. " +
                    "The stored document is preserved until an explicit Save.";
                return validatedDefaults.Clone();
            }
        }

        private SettingsValues Validate(SettingsValues values, SettingsValues fallback,
            GraphicsCapabilities capabilities)
        {
            SettingsValues result = values.Clone();
            DisplayResolution resolution = FindResolution(result.Width, result.Height, capabilities);
            if (resolution.Width == 0)
            {
                resolution = fallback == null ? default : FindResolution(fallback.Width, fallback.Height, capabilities);
                if (resolution.Width == 0)
                {
                    resolution = FindResolution(capabilities.CurrentResolution.Width,
                        capabilities.CurrentResolution.Height, capabilities);
                    if (resolution.Width == 0)
                    {
                        resolution = capabilities.Resolutions[0];
                    }
                }
            }

            result.Width = resolution.Width;
            result.Height = resolution.Height;
            if (!Contains(capabilities.Modes, result.DisplayMode))
            {
                result.DisplayMode = fallback != null && Contains(capabilities.Modes, fallback.DisplayMode) ?
                    fallback.DisplayMode : Contains(capabilities.Modes, capabilities.CurrentMode) ?
                        capabilities.CurrentMode : capabilities.Modes[0];
            }

            if (!Contains(capabilities.QualityNames, result.QualityName))
            {
                result.QualityName = fallback != null && Contains(capabilities.QualityNames, fallback.QualityName) ?
                    fallback.QualityName : Contains(capabilities.QualityNames, capabilities.CurrentQualityName) ?
                        capabilities.CurrentQualityName : capabilities.QualityNames[0];
            }

            result.MasterVolume = ValidateVolume(result.MasterVolume, fallback?.MasterVolume ?? 1f);
            result.MusicVolume = ValidateVolume(result.MusicVolume, fallback?.MusicVolume ?? 1f);
            result.SfxVolume = ValidateVolume(result.SfxVolume, fallback?.SfxVolume ?? 1f);
            result.LanguageCode = configuration.ValidateLanguage(result.LanguageCode);
            return result;
        }

        private static bool HasRootValuesObject(string json)
        {
            // JsonUtility can materialize a nested class even when its field is absent or null.
            // Check the actual top-level token after native parsing validated JSON syntax.
            int depth = 0;
            int valuesObject = -1;
            bool foundValues = false;
            for (int index = 0; index < json.Length; index++)
            {
                char character = json[index];
                if (character == '"')
                {
                    string name = ReadJsonString(json, ref index);
                    if (depth != 1)
                    {
                        continue;
                    }

                    int next = index + 1;
                    while (next < json.Length && char.IsWhiteSpace(json[next])) next++;
                    if (next >= json.Length || json[next] != ':') continue;
                    next++;
                    while (next < json.Length && char.IsWhiteSpace(json[next])) next++;
                    if (name == "values")
                    {
                        if (foundValues) throw new ArgumentException("The preference document repeats its values object.");
                        foundValues = true;
                        if (next < json.Length && json[next] == '{') valuesObject = next;
                    }
                }
                else if (character == '{' || character == '[')
                {
                    depth++;
                }
                else if (character == '}' || character == ']')
                {
                    depth--;
                }
            }

            if (valuesObject < 0) return false;
            ValidateKnownFieldTypes(json, valuesObject, false);
            return true;
        }

        private static void ValidateKnownFieldTypes(string json, int objectStart, bool header)
        {
            // JsonUtility silently converts incompatible JSON primitive types to numeric zero.
            // Inspect only known immediate fields of the selected object, before native overwrite.
            int depth = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int index = objectStart; index < json.Length; index++)
            {
                char character = json[index];
                if (character == '"')
                {
                    string name = ReadJsonString(json, ref index);
                    if (depth != 1) continue;
                    int value = index + 1;
                    while (value < json.Length && char.IsWhiteSpace(json[value])) value++;
                    if (value >= json.Length || json[value] != ':') continue;
                    value++;
                    while (value < json.Length && char.IsWhiteSpace(json[value])) value++;
                    bool integer = header ? name == "schemaVersion" :
                        name == "Width" || name == "Height" || name == "DisplayMode";
                    bool number = !header && (name == "MasterVolume" || name == "MusicVolume" || name == "SfxVolume");
                    bool text = !header && (name == "QualityName" || name == "LanguageCode");
                    if (!integer && !number && !text) continue;
                    if (!seen.Add(name))
                        throw new ArgumentException($"Preference field '{name}' appears more than once.");
                    bool valid = value < json.Length && (text ? json[value] == '"' || IsNullToken(json, value) :
                        IsNumberToken(json, value, integer));
                    if (!valid) throw new ArgumentException($"Preference field '{name}' has an incompatible JSON type.");
                }
                else if (character == '{' || character == '[') depth++;
                else if (character == '}' || character == ']')
                {
                    depth--;
                    if (depth == 0) return;
                }
            }
        }

        private static bool IsNullToken(string json, int start)
        {
            return start + 4 <= json.Length && string.CompareOrdinal(json, start, "null", 0, 4) == 0 &&
                IsValueBoundary(json, start + 4);
        }

        private static bool IsNumberToken(string json, int start, bool integerOnly)
        {
            int index = start;
            if (index < json.Length && json[index] == '-') index++;
            if (index >= json.Length) return false;
            if (json[index] == '0') index++;
            else
            {
                if (json[index] < '1' || json[index] > '9') return false;
                do { index++; } while (index < json.Length && json[index] >= '0' && json[index] <= '9');
            }
            if (index < json.Length && json[index] == '.')
            {
                if (integerOnly) return false;
                index++;
                int fractionStart = index;
                while (index < json.Length && json[index] >= '0' && json[index] <= '9') index++;
                if (index == fractionStart) return false;
            }
            if (index < json.Length && (json[index] == 'e' || json[index] == 'E'))
            {
                if (integerOnly) return false;
                index++;
                if (index < json.Length && (json[index] == '+' || json[index] == '-')) index++;
                int exponentStart = index;
                while (index < json.Length && json[index] >= '0' && json[index] <= '9') index++;
                if (index == exponentStart) return false;
            }
            return IsValueBoundary(json, index) && (!integerOnly || int.TryParse(json.Substring(start, index - start),
                NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _));
        }

        private static bool IsValueBoundary(string json, int index)
        {
            while (index < json.Length && char.IsWhiteSpace(json[index])) index++;
            return index < json.Length && (json[index] == ',' || json[index] == '}');
        }

        private static string ReadJsonString(string json, ref int index)
        {
            var value = new StringBuilder();
            while (++index < json.Length)
            {
                char character = json[index];
                if (character == '"') return value.ToString();
                if (character == '\\')
                {
                    character = json[++index];
                    if (character == 'u')
                    {
                        value.Append((char)Convert.ToInt32(json.Substring(index + 1, 4), 16));
                        index += 4;
                        continue;
                    }
                }

                value.Append(character);
            }

            throw new ArgumentException("A JSON string is unterminated.");
        }

        private static float ValidateVolume(float value, float fallback)
        {
            return float.IsNaN(value) || float.IsInfinity(value) ? fallback : Mathf.Clamp01(value);
        }

        private static DisplayResolution FindResolution(int width, int height, GraphicsCapabilities capabilities)
        {
            foreach (DisplayResolution resolution in capabilities.Resolutions)
            {
                if (resolution.Width == width && resolution.Height == height)
                {
                    return resolution;
                }
            }

            return default;
        }

        private static bool Contains<T>(IReadOnlyList<T> values, T requested)
        {
            foreach (T value in values)
            {
                if (EqualityComparer<T>.Default.Equals(value, requested))
                {
                    return true;
                }
            }

            return false;
        }

        private void SetCurrent(SettingsValues values)
        {
            if (current.SameAs(values))
            {
                return;
            }

            current = values;
            Delegate[] subscribers = Changed?.GetInvocationList();
            if (subscribers == null)
            {
                return;
            }

            foreach (Delegate subscriber in subscribers)
            {
                if (!IsInitialized || !ReferenceEquals(current, values))
                {
                    // A preceding observer stopped the service or committed a newer snapshot.
                    break;
                }

                try
                {
                    ((Action<SettingsValues>)subscriber)(values.Clone());
                }
                catch (Exception exception)
                {
                    Delegate[] failures = ObserverFailed?.GetInvocationList();
                    if (failures == null)
                    {
                        continue;
                    }

                    foreach (Delegate failure in failures)
                    {
                        try
                        {
                            ((Action<Exception>)failure)(exception);
                        }
                        catch (Exception)
                        {
                            // Diagnostics must not interrupt preference updates or other observers.
                        }
                    }
                }
            }
        }

        private void EnsureInitialized()
        {
            if (!IsInitialized)
            {
                throw new InvalidOperationException("Settings must be initialized before this operation.");
            }
        }

        [Serializable]
        private sealed class PreferenceHeader
        {
            public int schemaVersion;
        }

        [Serializable]
        private sealed class PreferenceDocument
        {
            public int schemaVersion;
            public SettingsValues values;
        }
    }
}
