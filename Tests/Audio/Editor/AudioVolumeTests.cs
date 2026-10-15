using System;
using Lumivale.Core.Audio;
using NUnit.Framework;

namespace Lumivale.Core.Audio.Tests
{
    public sealed class AudioVolumeTests
    {
        [TestCase(0f, -80f)]
        [TestCase(1f, 0f)]
        [TestCase(0.5f, -6.0206f)]
        [TestCase(0.1f, -20f)]
        [TestCase(0.00001f, -80f)]
        public void NormalizedVolumeConvertsToFiniteMixerAttenuation(float volume, float expected)
        {
            float actual = AudioVolume.NormalizedToDecibels(volume);
            Assert.That(float.IsNaN(actual) || float.IsInfinity(actual), Is.False);
            Assert.That(actual, Is.EqualTo(expected).Within(0.0001f));
        }

        [TestCase(-0.01f)]
        [TestCase(1.01f)]
        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(float.NegativeInfinity)]
        public void InvalidVolumesAreRejected(float invalid)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => AudioVolume.NormalizedToDecibels(invalid));
        }

        [Test]
        public void MissingConfigurationIsRejected()
        {
            Assert.Throws<ArgumentNullException>(() => new AudioService(null));
            Assert.Throws<ArgumentNullException>(() => new AudioConfiguration(null, null, null, null));
        }
    }
}
