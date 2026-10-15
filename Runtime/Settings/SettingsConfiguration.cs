using System;
using System.Collections.Generic;

namespace Lumivale.Core.Settings
{
    /// <summary>Project-owned defaults, a dedicated preference key and optional supported language codes.</summary>
    public sealed class SettingsConfiguration
    {
        private readonly SettingsValues defaults;
        private readonly string[] languages;

        public SettingsConfiguration(SettingsValues defaults,
            string storageKey = "com.lumivale.preferences", IEnumerable<string> supportedLanguages = null)
        {
            if (defaults == null)
            {
                throw new ArgumentNullException(nameof(defaults));
            }

            if (string.IsNullOrWhiteSpace(storageKey))
            {
                throw new ArgumentException("A dedicated preference storage key is required.", nameof(storageKey));
            }

            this.defaults = defaults.Clone();
            if (!IsLanguageCode(this.defaults.LanguageCode))
            {
                throw new ArgumentException("The default language requires a valid language code.", nameof(defaults));
            }

            this.defaults.LanguageCode = this.defaults.LanguageCode.Trim();
            var unique = new List<string>();
            if (supportedLanguages != null)
            {
                foreach (string language in supportedLanguages)
                {
                    if (!IsLanguageCode(language))
                    {
                        throw new ArgumentException("A supported language code is invalid.", nameof(supportedLanguages));
                    }

                    string trimmed = language.Trim();
                    if (!unique.Exists(value => string.Equals(value, trimmed, StringComparison.OrdinalIgnoreCase)))
                    {
                        unique.Add(trimmed);
                    }
                }

                if (unique.Count == 0 || !unique.Exists(value => string.Equals(value,
                    this.defaults.LanguageCode, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new ArgumentException("Supported languages must include the default language.",
                        nameof(supportedLanguages));
                }
            }

            languages = unique.ToArray();
            this.defaults.LanguageCode = ValidateLanguage(this.defaults.LanguageCode);
            StorageKey = storageKey;
        }

        public string StorageKey { get; }
        public SettingsValues Defaults => defaults.Clone();
        public IReadOnlyList<string> SupportedLanguages => Array.AsReadOnly(languages);

        internal string ValidateLanguage(string value)
        {
            if (!IsLanguageCode(value))
            {
                return defaults.LanguageCode;
            }

            string code = value.Trim();
            if (languages.Length == 0)
            {
                return code;
            }

            foreach (string language in languages)
            {
                if (string.Equals(language, code, StringComparison.OrdinalIgnoreCase))
                {
                    return language;
                }
            }

            return defaults.LanguageCode;
        }

        private static bool IsLanguageCode(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string trimmed = value.Trim();
            if (trimmed.Length > 35 || !((trimmed[0] >= 'a' && trimmed[0] <= 'z') ||
                (trimmed[0] >= 'A' && trimmed[0] <= 'Z')) || trimmed[trimmed.Length - 1] == '-')
            {
                return false;
            }

            foreach (char character in trimmed)
            {
                if (!(character >= 'a' && character <= 'z') && !(character >= 'A' && character <= 'Z') &&
                    !(character >= '0' && character <= '9') && character != '-')
                {
                    return false;
                }
            }

            return true;
        }
    }
}
