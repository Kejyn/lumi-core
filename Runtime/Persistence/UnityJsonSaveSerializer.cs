using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Lumivale.Core.Persistence
{
    /// <summary>
    /// Native JsonUtility with strict grammar, field types and finite values.
    /// The root must be a concrete serializable data class. Capture Unity state before calling it.
    /// Properties, static and NonSerialized fields are ignored; dictionaries, polymorphism,
    /// SerializeReference, FormerlySerializedAs, callbacks, UnityEngine.Object references,
    /// recursive types and nested containers are unsupported.
    /// DTO schema nesting is conservatively bounded to seven levels before invoking Unity's serializer.
    /// Inline classes use value semantics: null and shared identity are not a portable save contract.
    /// Missing fields retain JsonUtility's defaults; a game's SaveSchema validator owns semantic rules.
    /// </summary>
    public sealed class UnityJsonSaveSerializer : ISaveSerializer
    {
        public const int MaximumDataDepth = 7;

        public string Serialize<T>(T data)
        {
            ValidateRootType(typeof(T));
            if (data == null) throw new ArgumentNullException(nameof(data));
            ValidateTypes(typeof(T), new HashSet<Type>(), 0);
            ValidateData(data, typeof(T), new HashSet<object>(ReferenceComparer.Instance), 0);
            string json = JsonUtility.ToJson(data);
            ValidateJson(StrictJson.Parse(json), typeof(T), 0);
            return json;
        }

        public T Deserialize<T>(string json)
        {
            ValidateRootType(typeof(T));
            ValidateTypes(typeof(T), new HashSet<Type>(), 0);
            JsonValue document = StrictJson.Parse(json);
            if (document.Kind != JsonValueKind.Object) throw new FormatException("The save data root must be an object.");
            ValidateJson(document, typeof(T), 0);
            T result = JsonUtility.FromJson<T>(json);
            if (result == null) throw new FormatException("JsonUtility did not produce a data object.");
            ValidateData(result, typeof(T), new HashSet<object>(ReferenceComparer.Instance), 0);
            return result;
        }

        private static void ValidateRootType(Type type)
        {
            if (!type.IsClass || type.IsAbstract || type == typeof(string) || IsCollection(type) ||
                typeof(UnityEngine.Object).IsAssignableFrom(type) || !type.IsSerializable)
                throw new ArgumentException("The save data root must be a concrete [Serializable] C# data class.");
        }

        private static void ValidateTypes(Type type, HashSet<Type> ancestors, int depth)
        {
            if (depth > MaximumDataDepth) throw new ArgumentException("The save DTO schema exceeds the supported nesting depth of seven.");
            if (type.IsEnum && (Enum.GetUnderlyingType(type) == typeof(long) || Enum.GetUnderlyingType(type) == typeof(ulong)))
                throw new ArgumentException("JsonUtility save enums must use 32 bits or fewer: " + type.FullName + ".");
            if (IsScalar(type)) return;
            if (TryElementType(type, out Type element))
            {
                if (IsCollection(element)) throw new ArgumentException("Nested containers are not supported by JsonUtility.");
                ValidateTypes(element, ancestors, depth + 1);
                return;
            }
            bool nativeUnityValue = type == typeof(Vector2) || type == typeof(Vector3) || type == typeof(Quaternion);
            if (typeof(UnityEngine.Object).IsAssignableFrom(type) || typeof(IDictionary).IsAssignableFrom(type) ||
                type.IsInterface || type.IsAbstract || type.IsGenericType || IsCollection(type) || !type.IsSerializable && !nativeUnityValue ||
                typeof(ISerializationCallbackReceiver).IsAssignableFrom(type) ||
                type == typeof(decimal) || type == typeof(DateTime) || type == typeof(Guid) || type == typeof(TimeSpan) ||
                type == typeof(IntPtr) || type == typeof(UIntPtr) || type == typeof(char) || type == typeof(object) ||
                type.Namespace != null && (type.Namespace == "System" || type.Namespace.StartsWith("System.", StringComparison.Ordinal)) ||
                type.Namespace != null && type.Namespace.StartsWith("UnityEngine", StringComparison.Ordinal) &&
                !nativeUnityValue)
                throw new ArgumentException("Unsupported save field type: " + type.FullName + ".");
            if (!ancestors.Add(type)) throw new ArgumentException("Recursive save DTO types are unsupported: " + type.FullName + ".");
            try
            {
                for (Type parent = type.BaseType; parent != null && parent != typeof(object) && parent != typeof(ValueType); parent = parent.BaseType)
                    if (!parent.IsSerializable) throw new ArgumentException("Every save DTO base class must have [Serializable]: " + parent.FullName + ".");
                foreach (FieldInfo field in Fields(type)) ValidateTypes(field.FieldType, ancestors, depth + 1);
            }
            finally { ancestors.Remove(type); }
        }

        private static void ValidateData(object value, Type type, HashSet<object> ancestors, int depth)
        {
            if (depth > StrictJson.MaximumDepth) throw new ArgumentException("The save data nesting limit was exceeded.");
            if (value == null) return;
            if (value.GetType() != type) throw new ArgumentException("Polymorphic save values are unsupported: " + type.FullName + ".");
            if (type == typeof(string)) StrictJson.ValidateString((string)value);
            if (type == typeof(float) && (float.IsNaN((float)value) || float.IsInfinity((float)value)) ||
                type == typeof(double) && (double.IsNaN((double)value) || double.IsInfinity((double)value)))
                throw new ArgumentException("Save floating-point values must be finite.");
            if (IsScalar(type)) return;
            bool track = !type.IsValueType;
            if (track && !ancestors.Add(value)) throw new ArgumentException("Cyclic save object graphs are unsupported.");
            try
            {
                if (TryElementType(type, out Type element))
                {
                    foreach (object item in (IEnumerable)value) ValidateData(item, element, ancestors, depth + 1);
                }
                else
                {
                    foreach (FieldInfo field in Fields(type)) ValidateData(field.GetValue(value), field.FieldType, ancestors, depth + 1);
                }
            }
            finally { if (track) ancestors.Remove(value); }
        }

        private static void ValidateJson(JsonValue value, Type type, int depth)
        {
            if (depth > StrictJson.MaximumDepth) throw new FormatException("The save data nesting limit was exceeded.");
            if (value.Kind == JsonValueKind.Null)
            {
                if (type.IsValueType) throw new FormatException("Null is not valid for " + type.FullName + ".");
                return;
            }
            if (type == typeof(string)) { RequireKind(value, JsonValueKind.String, type); return; }
            if (type == typeof(bool)) { RequireKind(value, JsonValueKind.Boolean, type); return; }
            if (IsNumeric(type))
            {
                RequireKind(value, JsonValueKind.Number, type);
                ValidateNumber(value.Text, type.IsEnum ? Enum.GetUnderlyingType(type) : type);
                return;
            }
            if (TryElementType(type, out Type element))
            {
                RequireKind(value, JsonValueKind.Array, type);
                foreach (JsonValue item in value.Items) ValidateJson(item, element, depth + 1);
                return;
            }
            RequireKind(value, JsonValueKind.Object, type);
            foreach (FieldInfo field in Fields(type))
            {
                if (value.Members.TryGetValue(field.Name, out JsonValue child)) ValidateJson(child, field.FieldType, depth + 1);
            }
        }

        private static void ValidateNumber(string text, Type type)
        {
            const NumberStyles integerStyle = NumberStyles.AllowLeadingSign;
            CultureInfo culture = CultureInfo.InvariantCulture;
            bool valid;
            if (type == typeof(float)) valid = float.TryParse(text, NumberStyles.Float, culture, out float floatNumber) &&
                !float.IsInfinity(floatNumber) && !float.IsNaN(floatNumber);
            else if (type == typeof(double)) valid = double.TryParse(text, NumberStyles.Float, culture, out double doubleNumber) &&
                !double.IsInfinity(doubleNumber) && !double.IsNaN(doubleNumber);
            else if (type == typeof(byte)) valid = byte.TryParse(text, integerStyle, culture, out _);
            else if (type == typeof(sbyte)) valid = sbyte.TryParse(text, integerStyle, culture, out _);
            else if (type == typeof(short)) valid = short.TryParse(text, integerStyle, culture, out _);
            else if (type == typeof(ushort)) valid = ushort.TryParse(text, integerStyle, culture, out _);
            else if (type == typeof(int)) valid = int.TryParse(text, integerStyle, culture, out _);
            else if (type == typeof(uint)) valid = uint.TryParse(text, integerStyle, culture, out _);
            else if (type == typeof(long)) valid = long.TryParse(text, integerStyle, culture, out _);
            else valid = ulong.TryParse(text, integerStyle, culture, out _);
            if (!valid) throw new FormatException("JSON number is not representable as " + type.Name + ".");
        }

        private static IEnumerable<FieldInfo> Fields(Type type)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (Type current = type; current != null && current != typeof(object) && current != typeof(ValueType); current = current.BaseType)
            {
                foreach (FieldInfo field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (field.IsStatic || field.IsNotSerialized) continue;
                    if (Attribute.IsDefined(field, typeof(SerializeReference)))
                        throw new ArgumentException("SerializeReference fields are unsupported: " + field.Name + ".");
                    if (!field.IsPublic && !Attribute.IsDefined(field, typeof(SerializeField))) continue;
                    if (Attribute.IsDefined(field, typeof(UnityEngine.Serialization.FormerlySerializedAsAttribute)))
                        throw new ArgumentException("FormerlySerializedAs aliases are unsupported; use an explicit game data version: " + field.Name + ".");
                    if (field.IsInitOnly) throw new ArgumentException("Readonly save fields cannot be persisted: " + field.Name + ".");
                    if (!names.Add(field.Name)) throw new ArgumentException("A save field name is hidden by inheritance: " + field.Name + ".");
                    yield return field;
                }
            }
        }

        private static bool TryElementType(Type type, out Type element)
        {
            if (type.IsArray && type.GetArrayRank() == 1) { element = type.GetElementType(); return true; }
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            { element = type.GetGenericArguments()[0]; return true; }
            element = null;
            return false;
        }

        private static bool IsCollection(Type type)
        {
            return type.IsArray || type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type);
        }

        private static bool IsScalar(Type type) { return type == typeof(string) || type == typeof(bool) || IsNumeric(type); }
        private static bool IsNumeric(Type type)
        {
            return type.IsEnum || type == typeof(float) || type == typeof(double) || type == typeof(byte) || type == typeof(sbyte) ||
                type == typeof(short) || type == typeof(ushort) || type == typeof(int) || type == typeof(uint) ||
                type == typeof(long) || type == typeof(ulong);
        }

        private static void RequireKind(JsonValue value, JsonValueKind expected, Type type)
        {
            if (value.Kind != expected) throw new FormatException("Expected " + expected + " for " + type.FullName + ".");
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object x, object y) { return ReferenceEquals(x, y); }
            public int GetHashCode(object value) { return RuntimeHelpers.GetHashCode(value); }
        }
    }
}
