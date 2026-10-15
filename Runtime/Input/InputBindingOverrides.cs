using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine.InputSystem;

namespace Lumivale.Core.Input
{
    /// <summary>
    /// A deliberately narrow reader for Input System's exported binding override document.
    /// No generic settings/save schema or storage policy is introduced into the Input module.
    /// </summary>
    internal static class InputBindingOverrides
    {
        private const int MaxDocumentCharacters = 256 * 1024;
        private const int MaxFieldCharacters = 2048;
        private const int MaxOverrides = 1024;

        internal static int FindBinding(InputAction action, Guid id)
        {
            for (int index = 0; index < action.bindings.Count; index++)
            {
                if (action.bindings[index].id == id)
                    return index;
            }
            return -1;
        }

        internal static void Resolve(InputActionAsset asset)
        {
            foreach (InputActionMap map in asset.actionMaps)
            {
                foreach (InputAction action in map.actions)
                {
                    // Force native resolution while all actions remain disabled. Invalid native
                    // processor/interaction configuration fails on the validation clone first.
                    _ = action.controls.Count;
                }
            }
        }

        internal static void Validate(InputActionAsset asset, string json)
        {
            if (json.Length == 0)
                return;
            if (json.Length > MaxDocumentCharacters)
                throw new ArgumentException("The binding override document exceeds 256 Ki characters.", nameof(json));

            var reader = new Reader(json);
            reader.Take('{');
            if (reader.ReadString() != "bindings")
                reader.Fail("The only root field must be 'bindings'.");
            reader.Take(':');
            reader.Take('[');
            var identities = new HashSet<Guid>();
            int count = 0;
            if (!reader.TryTake(']'))
            {
                do
                {
                    if (++count > MaxOverrides)
                        reader.Fail("Too many binding overrides.");
                    Dictionary<string, string> entry = reader.ReadEntry();
                    if (!Guid.TryParse(entry["id"], out Guid id) || id == Guid.Empty || !identities.Add(id))
                        reader.Fail("Each override requires a unique nonempty binding GUID.");
                    string actionPath = entry["action"];
                    InputAction action = asset.FindAction(actionPath);
                    if (action == null || actionPath != action.actionMap.name + "/" + action.name)
                        reader.Fail("An override refers to an unknown or ambiguous action path.");
                    int bindingIndex = FindBinding(action, id);
                    if (bindingIndex < 0)
                        reader.Fail("An override binding GUID does not belong to its action.");
                    if (action.bindings[bindingIndex].isComposite)
                        reader.Fail("Composite roots cannot be overridden by this API.");
                    if (entry["interactions"] != "null" || entry["processors"] != "null")
                        reader.Fail("This API persists control paths only; keep interactions and processors in the source asset.");
                }
                while (reader.TryTake(','));
                reader.Take(']');
            }
            reader.Take('}');
            reader.RequireEnd();
        }

        private sealed class Reader
        {
            private readonly string _json;
            private int _position;

            internal Reader(string json) => _json = json;

            internal Dictionary<string, string> ReadEntry()
            {
                Take('{');
                var result = new Dictionary<string, string>(StringComparer.Ordinal);
                do
                {
                    string field = ReadString();
                    if (field != "action" && field != "id" && field != "path" &&
                        field != "interactions" && field != "processors")
                        Fail("Unsupported binding override field.");
                    if (result.ContainsKey(field))
                        Fail("Duplicate binding override field.");
                    Take(':');
                    result.Add(field, ReadString());
                }
                while (TryTake(','));
                Take('}');
                if (result.Count != 5)
                    Fail("Every native override field must be present as a string.");
                return result;
            }

            internal string ReadString()
            {
                Take('"');
                var result = new StringBuilder();
                bool closed = false;
                while (_position < _json.Length)
                {
                    char value = _json[_position++];
                    if (value == '"')
                    {
                        closed = true;
                        break;
                    }
                    if (value < 0x20)
                        Fail("Unescaped control character in JSON string.");
                    if (value == '\\')
                    {
                        if (_position >= _json.Length)
                            Fail("Incomplete JSON escape.");
                        char escaped = _json[_position++];
                        switch (escaped)
                        {
                            case '"': value = '"'; break;
                            case '\\': value = '\\'; break;
                            case '/': value = '/'; break;
                            case 'b': value = '\b'; break;
                            case 'f': value = '\f'; break;
                            case 'n': value = '\n'; break;
                            case 'r': value = '\r'; break;
                            case 't': value = '\t'; break;
                            case 'u': value = ReadUnicode(); break;
                            default: Fail("Invalid JSON escape."); break;
                        }
                    }
                    result.Append(value);
                    if (result.Length > MaxFieldCharacters)
                        Fail("A binding override field exceeds 2048 characters.");
                }
                if (!closed)
                    Fail("Unterminated JSON string.");
                return result.ToString();
            }

            private char ReadUnicode()
            {
                if (_position + 4 > _json.Length)
                    Fail("Incomplete Unicode escape.");
                int value = 0;
                for (int index = 0; index < 4; index++)
                {
                    char digit = _json[_position++];
                    int number = digit >= '0' && digit <= '9' ? digit - '0' :
                        digit >= 'a' && digit <= 'f' ? digit - 'a' + 10 :
                        digit >= 'A' && digit <= 'F' ? digit - 'A' + 10 : -1;
                    if (number < 0)
                        Fail("Invalid Unicode escape.");
                    value = value * 16 + number;
                }
                return (char)value;
            }

            internal void Take(char expected)
            {
                if (!TryTake(expected))
                    Fail("Expected '" + expected + "'.");
            }

            internal bool TryTake(char expected)
            {
                SkipWhiteSpace();
                if (_position >= _json.Length || _json[_position] != expected)
                    return false;
                _position++;
                return true;
            }

            internal void RequireEnd()
            {
                SkipWhiteSpace();
                if (_position != _json.Length)
                    Fail("Trailing JSON content.");
            }

            private void SkipWhiteSpace()
            {
                while (_position < _json.Length)
                {
                    char value = _json[_position];
                    if (value != ' ' && value != '\t' && value != '\r' && value != '\n')
                        break;
                    _position++;
                }
            }

            internal void Fail(string message) =>
                throw new ArgumentException(message + " JSON position " + _position + ".", "json");
        }
    }
}
