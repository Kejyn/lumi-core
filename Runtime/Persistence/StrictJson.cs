using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Lumivale.Core.Persistence
{
    internal enum JsonValueKind { Object, Array, String, Number, Boolean, Null }

    internal sealed class JsonValue
    {
        public JsonValueKind Kind;
        public string Text;
        public Dictionary<string, JsonValue> Members;
        public List<JsonValue> Items;
    }

    // JsonUtility tolerates malformed JSON and coerces several wrong numeric types to zero.
    // This small grammar reader rejects those inputs before native deserialization.
    internal static class StrictJson
    {
        public const int MaximumDepth = 128;

        public static JsonValue Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new FormatException("The JSON document is empty.");
            return new Reader(json).ReadDocument();
        }

        public static int PositiveInteger(JsonValue value, string name)
        {
            if (value == null || value.Kind != JsonValueKind.Number ||
                !int.TryParse(value.Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int result) || result < 1)
                throw new FormatException(name + " must be a positive integer.");
            return result;
        }

        public static void ValidateString(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                if (char.IsHighSurrogate(value[i]))
                {
                    if (++i == value.Length || !char.IsLowSurrogate(value[i]))
                        throw new FormatException("Unpaired high surrogate in a JSON string.");
                }
                else if (char.IsLowSurrogate(value[i])) throw new FormatException("Unpaired low surrogate in a JSON string.");
            }
        }

        private sealed class Reader
        {
            private readonly string text;
            private int index;

            public Reader(string text) { this.text = text; }

            public JsonValue ReadDocument()
            {
                JsonValue result = ReadValue(0);
                SkipWhiteSpace();
                if (index != text.Length) Fail("Unexpected trailing content.");
                return result;
            }

            private JsonValue ReadValue(int depth)
            {
                if (depth > MaximumDepth) Fail("The JSON nesting limit was exceeded.");
                SkipWhiteSpace();
                if (index == text.Length) Fail("Expected a JSON value.");
                char token = text[index];
                if (token == '{') return ReadObject(depth);
                if (token == '[') return ReadArray(depth);
                if (token == '"') return new JsonValue { Kind = JsonValueKind.String, Text = ReadString() };
                if (token == 't') return ReadLiteral("true", JsonValueKind.Boolean);
                if (token == 'f') return ReadLiteral("false", JsonValueKind.Boolean);
                if (token == 'n') return ReadLiteral("null", JsonValueKind.Null);
                if (token == '-' || IsDigit(token)) return ReadNumber();
                Fail("Invalid JSON value.");
                return null;
            }

            private JsonValue ReadObject(int depth)
            {
                index++;
                var members = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
                SkipWhiteSpace();
                if (Take('}')) return new JsonValue { Kind = JsonValueKind.Object, Members = members };
                while (true)
                {
                    SkipWhiteSpace();
                    if (index == text.Length || text[index] != '"') Fail("Expected an object member name.");
                    string name = ReadString();
                    SkipWhiteSpace();
                    if (!Take(':')) Fail("Expected ':' after the member name.");
                    JsonValue value = ReadValue(depth + 1);
                    if (members.ContainsKey(name)) Fail("Duplicate JSON member: " + name + ".");
                    members.Add(name, value);
                    SkipWhiteSpace();
                    if (Take('}')) break;
                    if (!Take(',')) Fail("Expected ',' or '}' after the member value.");
                }
                return new JsonValue { Kind = JsonValueKind.Object, Members = members };
            }

            private JsonValue ReadArray(int depth)
            {
                index++;
                var items = new List<JsonValue>();
                SkipWhiteSpace();
                if (Take(']')) return new JsonValue { Kind = JsonValueKind.Array, Items = items };
                while (true)
                {
                    items.Add(ReadValue(depth + 1));
                    SkipWhiteSpace();
                    if (Take(']')) break;
                    if (!Take(',')) Fail("Expected ',' or ']' after the array value.");
                }
                return new JsonValue { Kind = JsonValueKind.Array, Items = items };
            }

            private string ReadString()
            {
                index++;
                var result = new StringBuilder();
                while (index < text.Length)
                {
                    char character = text[index++];
                    if (character == '"')
                    {
                        string value = result.ToString();
                        ValidateString(value);
                        return value;
                    }
                    if (character < 0x20) Fail("Unescaped control character in a JSON string.");
                    if (character != '\\') { result.Append(character); continue; }
                    if (index == text.Length) Fail("Incomplete JSON string escape.");
                    char escape = text[index++];
                    switch (escape)
                    {
                        case '"': result.Append('"'); break;
                        case '\\': result.Append('\\'); break;
                        case '/': result.Append('/'); break;
                        case 'b': result.Append('\b'); break;
                        case 'f': result.Append('\f'); break;
                        case 'n': result.Append('\n'); break;
                        case 'r': result.Append('\r'); break;
                        case 't': result.Append('\t'); break;
                        case 'u':
                            if (index + 4 > text.Length) Fail("Incomplete Unicode escape.");
                            if (!ushort.TryParse(text.Substring(index, 4), NumberStyles.AllowHexSpecifier,
                                CultureInfo.InvariantCulture, out ushort code)) Fail("Invalid Unicode escape.");
                            result.Append((char)code);
                            index += 4;
                            break;
                        default: Fail("Invalid JSON string escape."); break;
                    }
                }
                Fail("Unterminated JSON string.");
                return null;
            }

            private JsonValue ReadNumber()
            {
                int start = index;
                Take('-');
                if (index == text.Length) Fail("Incomplete JSON number.");
                if (Take('0'))
                {
                    if (index < text.Length && IsDigit(text[index])) Fail("Leading zero in a JSON number.");
                }
                else
                {
                    if (text[index] < '1' || text[index] > '9') Fail("Invalid JSON integer.");
                    while (index < text.Length && IsDigit(text[index])) index++;
                }
                if (Take('.')) ReadDigits();
                if (Take('e') || Take('E'))
                {
                    if (!Take('+')) Take('-');
                    ReadDigits();
                }
                return new JsonValue { Kind = JsonValueKind.Number, Text = text.Substring(start, index - start) };
            }

            private void ReadDigits()
            {
                int start = index;
                while (index < text.Length && IsDigit(text[index])) index++;
                if (index == start) Fail("Expected digits in a JSON number.");
            }

            private JsonValue ReadLiteral(string literal, JsonValueKind kind)
            {
                if (index + literal.Length > text.Length ||
                    string.CompareOrdinal(text, index, literal, 0, literal.Length) != 0)
                    Fail("Invalid JSON literal.");
                index += literal.Length;
                return new JsonValue { Kind = kind, Text = literal };
            }

            private bool Take(char character)
            {
                if (index == text.Length || text[index] != character) return false;
                index++;
                return true;
            }

            private void SkipWhiteSpace()
            {
                while (index < text.Length && (text[index] == ' ' || text[index] == '\t' ||
                    text[index] == '\r' || text[index] == '\n')) index++;
            }

            private static bool IsDigit(char character) { return character >= '0' && character <= '9'; }
            private void Fail(string message) { throw new FormatException(message + " Offset: " + index + "."); }
        }
    }
}
