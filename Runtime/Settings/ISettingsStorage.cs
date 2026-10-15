using UnityEngine;

namespace Lumivale.Core.Settings
{
    /// <summary>Stores one versioned preference document, separate from gameplay save data.</summary>
    public interface ISettingsStorage
    {
        bool TryLoad(string key, out string json);
        void Save(string key, string json);
    }

    /// <summary>Native local, unencrypted PlayerPrefs storage. Do not store secrets here.</summary>
    public sealed class PlayerPrefsSettingsStorage : ISettingsStorage
    {
        public bool TryLoad(string key, out string json)
        {
            if (!PlayerPrefs.HasKey(key))
            {
                json = null;
                return false;
            }

            json = PlayerPrefs.GetString(key);
            return true;
        }

        public void Save(string key, string json)
        {
            PlayerPrefs.SetString(key, json);
            PlayerPrefs.Save();
        }
    }
}
