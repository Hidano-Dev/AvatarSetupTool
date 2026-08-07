using System;
using NUnit.Framework;

namespace Hidano.AvatarSetupTool.Editor.Tests
{
    public class TextureMaxSizeCalculatorTests
    {
        [TestCase(31, 31, 32)]
        [TestCase(32, 32, 32)]
        [TestCase(33, 33, 64)]
        [TestCase(2048, 2048, 2048)]
        [TestCase(2049, 2049, 4096)]
        [TestCase(4096, 4096, 4096)]
        [TestCase(16384, 16384, 16384)]
        [TestCase(16385, 16385, 16384)]
        public void Calculate_Boundaries_ReturnExpectedOption(int width, int height, int expected)
        {
            Assert.That(TextureMaxSizeCalculator.Calculate(width, height), Is.EqualTo(expected));
        }

        [TestCase(512, 2049, 4096)]
        [TestCase(4097, 1024, 8192)]
        public void Calculate_UsesLongestSide(int width, int height, int expected)
        {
            Assert.That(TextureMaxSizeCalculator.Calculate(width, height), Is.EqualTo(expected));
        }

        [TestCase(1, 1)]
        [TestCase(31, 33)]
        [TestCase(16385, 20000)]
        public void Calculate_ReturnValueIsAlwaysAnOption(int width, int height)
        {
            var result = TextureMaxSizeCalculator.Calculate(width, height);

            Assert.That(Array.IndexOf(TextureMaxSizeCalculator.SizeOptions, result), Is.GreaterThanOrEqualTo(0));
        }
    }
}
