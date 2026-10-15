using System;
using System.Collections.Generic;
using Lumivale.Core.Persistence;
using NUnit.Framework;
using UnityEngine;

namespace Lumivale.Core.Persistence.Tests
{
    public sealed class PersistenceSerializationTests
    {
        private readonly UnityJsonSaveSerializer serializer = new UnityJsonSaveSerializer();
        private static readonly DateTime SavedAt = new DateTime(2026, 10, 13, 12, 30, 45, DateTimeKind.Utc);

        [Test]
        public void NativeSerializableDtoRoundTripsPlainFieldsCollectionsAndUnityValues()
        {
            var source = new Data
            {
                Count = 42, Value = 0.625f, Precise = 0.123456789012345, Flag = true,
                Name = "Lumivale \"save\"\nétat 🌙", Position = new Vector3(1, 2, 3),
                Planar = new Vector2(-1, 2), Rotation = Quaternion.identity,
                Numbers = new[] { 1, -2, 3 }, Children = new List<Child> { new Child { Name = "a", Number = 9 } },
                Child = new Child { Name = "nested", Number = 5 }, Choice = Choice.Second
            };
            string json = serializer.Serialize(source);
            Data loaded = serializer.Deserialize<Data>(json);
            Assert.That(loaded.Count, Is.EqualTo(source.Count));
            Assert.That(loaded.Value, Is.EqualTo(source.Value));
            Assert.That(loaded.Precise, Is.EqualTo(source.Precise));
            Assert.That(loaded.Flag, Is.True);
            Assert.That(loaded.Name, Is.EqualTo(source.Name));
            Assert.That(loaded.Position, Is.EqualTo(source.Position));
            Assert.That(loaded.Planar, Is.EqualTo(source.Planar));
            Assert.That(loaded.Rotation, Is.EqualTo(source.Rotation));
            Assert.That(loaded.Numbers, Is.EqualTo(source.Numbers));
            Assert.That(loaded.Children[0].Number, Is.EqualTo(9));
            Assert.That(loaded.Child.Name, Is.EqualTo("nested"));
            Assert.That(loaded.Choice, Is.EqualTo(Choice.Second));
            Assert.That(loaded, Is.Not.SameAs(source));
        }

        [Test]
        public void PrivateSerializeFieldPersistsWhilePropertiesAndNonSerializedFieldsDoNot()
        {
            var source = new PrivateData();
            source.SetValue(23);
            source.Ignored = 12;
            string json = serializer.Serialize(source);
            Assert.That(json, Does.Contain("value"));
            Assert.That(json, Does.Not.Contain("Ignored"));
            Assert.That(json, Does.Not.Contain("Computed"));
            Assert.That(serializer.Deserialize<PrivateData>(json).Computed, Is.EqualTo(23));
        }

        [Test]
        public void MissingFieldsUseNativeDefaultsAndUnknownFieldsAreIgnored()
        {
            Data loaded = serializer.Deserialize<Data>("{\"Count\":7,\"futureField\":{\"ignored\":true}}");
            Assert.That(loaded.Count, Is.EqualTo(7));
            Assert.That(loaded.Value, Is.Zero);
        }

        [Test]
        public void AllRepresentableIntegerWidthsRoundTrip()
        {
            var source = new Integers
            {
                Byte = byte.MaxValue, Sbyte = sbyte.MinValue, Short = short.MinValue, Ushort = ushort.MaxValue,
                Int = int.MinValue, Uint = uint.MaxValue, Long = long.MinValue, Ulong = ulong.MaxValue
            };
            Integers loaded = serializer.Deserialize<Integers>(serializer.Serialize(source));
            Assert.That(loaded.Byte, Is.EqualTo(source.Byte));
            Assert.That(loaded.Sbyte, Is.EqualTo(source.Sbyte));
            Assert.That(loaded.Short, Is.EqualTo(source.Short));
            Assert.That(loaded.Ushort, Is.EqualTo(source.Ushort));
            Assert.That(loaded.Int, Is.EqualTo(source.Int));
            Assert.That(loaded.Uint, Is.EqualTo(source.Uint));
            Assert.That(loaded.Long, Is.EqualTo(source.Long));
            Assert.That(loaded.Ulong, Is.EqualTo(source.Ulong));
        }

        [TestCase("{\"Count\":\"7\"}")]
        [TestCase("{\"Count\":true}")]
        [TestCase("{\"Count\":null}")]
        [TestCase("{\"Count\":1.25}")]
        [TestCase("{\"Count\":1e0}")]
        [TestCase("{\"Count\":2147483648}")]
        [TestCase("{\"Value\":\"bad\"}")]
        [TestCase("{\"Value\":{}}")]
        [TestCase("{\"Value\":1e999}")]
        [TestCase("{\"Precise\":1e999}")]
        [TestCase("{\"Name\":7}")]
        [TestCase("{\"Flag\":1}")]
        [TestCase("{\"Numbers\":{}}")]
        [TestCase("{\"Numbers\":[1,\"2\"]}")]
        [TestCase("{\"Child\":true}")]
        [TestCase("{\"Position\":{\"x\":\"1\"}}")]
        public void WrongFieldTypesAndNonRepresentableValuesAreRejectedBeforeNativeCoercion(string json)
        {
            Assert.Throws<FormatException>(() => serializer.Deserialize<Data>(json));
        }

        [TestCase("")]
        [TestCase("{Count:1}")]
        [TestCase("{\"Count\":01}")]
        [TestCase("{\"Count\":1,}")]
        [TestCase("{\"Count\":1} trailing")]
        [TestCase("{\"Name\":\"line\nline\"}")]
        [TestCase("{\"Name\":\"\\x41\"}")]
        [TestCase("{\"Count\":NaN}")]
        [TestCase("[]")]
        [TestCase("null")]
        [TestCase("{\"Count\":1,\"Count\":2}")]
        [TestCase("{\"Count\":1,\"\\u0043ount\":2}")]
        [TestCase("{\"unknown\":{\"a\":1,\"a\":2}}")]
        [TestCase("{\"Name\":\"\\ud800\"}")]
        [TestCase("{\"Name\":\"\\udc00\"}")]
        public void MalformedDocumentsAndDuplicateMembersAreRejected(string json)
        {
            Assert.Throws<FormatException>(() => serializer.Deserialize<Data>(json));
        }

        [Test]
        public void NonFiniteRuntimeValuesAreRejectedIncludingNestedValues()
        {
            Assert.Throws<ArgumentException>(() => serializer.Serialize(new Data { Value = float.NaN }));
            Assert.Throws<ArgumentException>(() => serializer.Serialize(new Data { Precise = double.PositiveInfinity }));
            Assert.Throws<ArgumentException>(() => serializer.Serialize(new Data { Position = new Vector3(float.NegativeInfinity, 0, 0) }));
        }

        [Test]
        public void UnsupportedRootShapesAreRejected()
        {
            Assert.Throws<ArgumentException>(() => serializer.Serialize(42));
            Assert.Throws<ArgumentException>(() => serializer.Serialize("text"));
            Assert.Throws<ArgumentException>(() => serializer.Serialize(new[] { 1, 2 }));
            Assert.Throws<ArgumentException>(() => serializer.Serialize(new List<int>()));
            Assert.Throws<ArgumentException>(() => serializer.Serialize(new NotSerializable()));
            Assert.Throws<ArgumentNullException>(() => serializer.Serialize<Data>(null));
        }

        [Test]
        public void UnityObjectReferencesAreRejectedEvenWhenNull()
        {
            Assert.Throws<ArgumentException>(() => serializer.Serialize(new UnityReference()));
        }

        [Test]
        public void DictionariesAndNestedContainersAreRejected()
        {
            Assert.Throws<ArgumentException>(() => serializer.Serialize(new DictionaryData()));
            Assert.Throws<ArgumentException>(() => serializer.Serialize(new NestedCollections()));
        }

        [Test]
        public void ManagedReferencePolymorphismAndUnsupportedSystemValuesAreRejected()
        {
            Assert.Throws<ArgumentException>(() => serializer.Serialize(new ReferenceData()));
            Assert.Throws<ArgumentException>(() => serializer.Serialize(new PolymorphicData { Child = new DerivedChild() }));
            Assert.Throws<ArgumentException>(() => serializer.Serialize(new DateData()));
            Assert.Throws<ArgumentException>(() => serializer.Serialize(new ReadonlyData()));
        }

        [Test]
        public void CyclicGraphsAreRejectedAndRepeatedNonCyclicReferencesAreAllowed()
        {
            var node = new Node();
            node.Next = node;
            Assert.Throws<ArgumentException>(() => serializer.Serialize(node));
            var shared = new Child { Number = 8 };
            var source = new Data { Child = shared, Children = new List<Child> { shared } };
            Data result = serializer.Deserialize<Data>(serializer.Serialize(source));
            Assert.That(result.Children[0].Number, Is.EqualTo(8));
            Assert.That(result.Child, Is.Not.SameAs(result.Children[0]));
        }

        [Test]
        public void ExcessiveJsonNestingIsBounded()
        {
            string json = "{}";
            for (int i = 0; i < 130; i++) json = "{\"unknown\":" + json + "}";
            Assert.Throws<FormatException>(() => serializer.Deserialize<Data>(json));
        }

        [Test]
        public void RecursiveDtoTypesAreRejectedEvenForNonCyclicOrEmptyInstances()
        {
            Assert.Throws<ArgumentException>(() => serializer.Serialize(new Node()));
            Assert.Throws<ArgumentException>(() => serializer.Serialize(new Node { Next = new Node() }));
            Assert.Throws<ArgumentException>(() => serializer.Serialize(new IndirectA()));
            Assert.Throws<ArgumentException>(() => serializer.Deserialize<Node>("{}"));
        }

        [Test]
        public void SupportedAcyclicNestingRoundTripsWithoutNativeDepthTruncation()
        {
            var source = new DepthA { Child = new DepthB { Child = new DepthC { Child = new DepthD
                { Child = new DepthE { Child = new DepthF { Child = new DepthG { Value = 71 } } } } } } };
            DepthA result = serializer.Deserialize<DepthA>(serializer.Serialize(source));
            Assert.That(result.Child.Child.Child.Child.Child.Child.Value, Is.EqualTo(71));
        }

        [Test]
        public void SchemasBeyondConservativeNativeDepthAreRejectedEvenWhenTheDeepFieldIsNull()
        {
            Assert.Throws<ArgumentException>(() => serializer.Serialize(new TooDeep()));
            Assert.Throws<ArgumentException>(() => serializer.Deserialize<TooDeep>("{}"));
        }

        [Test]
        public void SerializationCallbacksCannotMutateDataAfterValidation()
        {
            var data = new CallbackData();
            Assert.Throws<ArgumentException>(() => serializer.Serialize(data));
            Assert.That(data.CallbackCount, Is.Zero);
            Assert.Throws<ArgumentException>(() => serializer.Deserialize<CallbackData>("{}"));
        }

        [Test]
        public void WiderThan32BitEnumsAreRejectedBeforeNativeSerialization()
        {
            Assert.Throws<ArgumentException>(() => serializer.Serialize(new WideEnumData()));
            Assert.Throws<ArgumentException>(() => serializer.Deserialize<WideEnumData>("{}"));
        }

        [Test]
        public void LegacyUnityAliasesCannotBypassStrictJsonFieldTypeValidation()
        {
            Assert.Throws<ArgumentException>(() => serializer.Serialize(new AliasData()));
            Assert.Throws<ArgumentException>(() => serializer.Deserialize<AliasData>("{\"oldCount\":\"bad\"}"));
        }

        [Test]
        public void UnpairedSurrogatesAreRejectedBeforeNativeSerializationCanReplaceData()
        {
            Assert.Throws<FormatException>(() => serializer.Serialize(new Data { Name = "bad\ud800" }));
        }

        [Test]
        public void EnvelopeRoundTripsMetadataAndPayloadWithoutGameplayKnowledge()
        {
            string payload = serializer.Serialize(new Data { Count = 12, Name = "🌙" });
            string json = SaveEnvelopeCodec.Encode("test-game", new SaveSlotMetadata("slot-1", SavedAt, "test-model", 3), payload);
            EnvelopeReadResult result = SaveEnvelopeCodec.Decode(json, "test-game", "slot-1");
            Assert.That(result.Status, Is.EqualTo(PersistenceStatus.Success));
            Assert.That(result.FormatVersion, Is.EqualTo(1));
            Assert.That(result.Metadata.SlotId, Is.EqualTo("slot-1"));
            Assert.That(result.Metadata.SavedAtUtc, Is.EqualTo(SavedAt));
            Assert.That(result.Metadata.SavedAtUtc.Kind, Is.EqualTo(DateTimeKind.Utc));
            Assert.That(result.Metadata.DataVersion, Is.EqualTo(3));
            Assert.That(result.Metadata.ModelId, Is.EqualTo("test-model"));
            Assert.That(result.PayloadJson, Is.EqualTo(payload));
        }

        [TestCase("\"dataVersion\":3", "\"dataVersion\":4")]
        [TestCase("test-model", "other-model")]
        [TestCase("12:30:45", "12:30:46")]
        [TestCase("\\\"Count\\\":12", "\\\"Count\\\":13")]
        public void EnvelopeChecksumDetectsMetadataAndPayloadChanges(string original, string replacement)
        {
            string json = Envelope();
            Assert.That(json, Does.Contain(original));
            EnvelopeReadResult result = SaveEnvelopeCodec.Decode(json.Replace(original, replacement), "test-game", "slot-1");
            Assert.That(result.Status, Is.EqualTo(PersistenceStatus.Corrupt));
            Assert.That(result.Message, Does.Contain("checksum"));
        }

        [Test]
        public void WrongGameOrSlotCannotLoadAnEnvelope()
        {
            Assert.That(SaveEnvelopeCodec.Decode(Envelope(), "other-game", "slot-1").Status, Is.EqualTo(PersistenceStatus.Corrupt));
            Assert.That(SaveEnvelopeCodec.Decode(Envelope(), "test-game", "slot-2").Status, Is.EqualTo(PersistenceStatus.Corrupt));
        }

        [TestCase("{\"formatVersion\":2}", PersistenceStatus.Incompatible)]
        [TestCase("{\"formatVersion\":2147483647,\"future\":[1,2]}", PersistenceStatus.Incompatible)]
        [TestCase("{\"formatVersion\":\"1\"}", PersistenceStatus.Corrupt)]
        [TestCase("{\"formatVersion\":1.0}", PersistenceStatus.Corrupt)]
        [TestCase("{\"formatVersion\":0}", PersistenceStatus.Corrupt)]
        [TestCase("{\"formatVersion\":2147483648}", PersistenceStatus.Corrupt)]
        [TestCase("{\"formatVersion\":1,\"formatVersion\":2}", PersistenceStatus.Corrupt)]
        [TestCase("{\"formatVersion\":1}", PersistenceStatus.Corrupt)]
        public void EnvelopeVersionIsLexicallyValidatedAndFutureFormatsDoNotApplyV1Rules(string json, PersistenceStatus status)
        {
            Assert.That(SaveEnvelopeCodec.Decode(json, "test-game", "slot-1").Status, Is.EqualTo(status));
        }

        [Test]
        public void EnvelopeEncodeRejectsMalformedOrNonObjectPayloads()
        {
            var metadata = new SaveSlotMetadata("slot-1", SavedAt, "test-model", 3);
            Assert.Throws<FormatException>(() => SaveEnvelopeCodec.Encode("test-game", metadata, "[]"));
            Assert.Throws<FormatException>(() => SaveEnvelopeCodec.Encode("test-game", metadata, "{\"a\":1,}"));
        }

        private static string Envelope()
        {
            return SaveEnvelopeCodec.Encode("test-game", new SaveSlotMetadata("slot-1", SavedAt, "test-model", 3), "{\"Count\":12}");
        }

        private enum Choice { First, Second }
        [Serializable] private sealed class Data
        {
            public int Count;
            public float Value;
            public double Precise;
            public bool Flag;
            public string Name;
            public Vector2 Planar;
            public Vector3 Position;
            public Quaternion Rotation;
            public int[] Numbers;
            public Child Child;
            public List<Child> Children;
            public Choice Choice;
        }
        [Serializable] private class Child { public string Name; public int Number; }
        [Serializable] private sealed class DerivedChild : Child { }
        [Serializable] private sealed class Node { public Node Next; }
        [Serializable] private sealed class IndirectA { public IndirectB Next = null; }
        [Serializable] private sealed class IndirectB { public IndirectA Next = null; }
        [Serializable] private sealed class DepthA { public DepthB Child; }
        [Serializable] private sealed class DepthB { public DepthC Child; }
        [Serializable] private sealed class DepthC { public DepthD Child; }
        [Serializable] private sealed class DepthD { public DepthE Child; }
        [Serializable] private sealed class DepthE { public DepthF Child; }
        [Serializable] private sealed class DepthF { public DepthG Child; }
        [Serializable] private sealed class DepthG { public int Value; }
        [Serializable] private sealed class TooDeep { public DepthA Child = null; }
        [Serializable] private sealed class CallbackData : ISerializationCallbackReceiver
        {
            [NonSerialized] public int CallbackCount;
            public int Value = 0;
            public void OnBeforeSerialize() { CallbackCount++; Value = 7; }
            public void OnAfterDeserialize() { CallbackCount++; }
        }
        private enum WideChoice : long { Value = 0x100000000L }
        [Serializable] private sealed class WideEnumData { public WideChoice Choice = WideChoice.Value; }
        [Serializable] private sealed class AliasData
        {
            [UnityEngine.Serialization.FormerlySerializedAs("oldCount")] public int Count = 0;
        }
        [Serializable] private sealed class PrivateData
        {
            [SerializeField] private int value;
            [NonSerialized] public int Ignored;
            public int Computed => value;
            public void SetValue(int next) { value = next; }
        }
        [Serializable] private sealed class Integers
        {
            public byte Byte; public sbyte Sbyte; public short Short; public ushort Ushort;
            public int Int; public uint Uint; public long Long; public ulong Ulong;
        }
        private sealed class NotSerializable { }
        [Serializable] private sealed class UnityReference { public GameObject Object = null; }
        [Serializable] private sealed class DictionaryData { public Dictionary<string, int> Values = new Dictionary<string, int>(); }
        [Serializable] private sealed class NestedCollections { public List<List<int>> Values = new List<List<int>>(); }
        [Serializable] private sealed class ReferenceData { [SerializeReference] public Child Child = null; }
        [Serializable] private sealed class PolymorphicData { public Child Child; }
        [Serializable] private sealed class DateData { public DateTime Date = DateTime.UtcNow; }
        [Serializable] private sealed class ReadonlyData { public readonly int Value = 1; }
    }
}
