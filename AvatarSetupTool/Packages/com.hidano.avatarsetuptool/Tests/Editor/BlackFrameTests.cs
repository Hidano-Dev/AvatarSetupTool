using NUnit.Framework;
using UnityEngine;

namespace Hidano.AvatarSetupTool.Editor.Tests
{
    /// <summary>
    /// ModelCaptureService.IsAllBlack (黒フレーム判定述語) の単体テスト。
    /// 全画素 RGB=(0,0,0) のみ true、アルファは判定に関与しないことを検証する。
    /// </summary>
    public class BlackFrameTests
    {
        private static Color32[] MakeBlack(int length, byte alpha = 0)
        {
            var pixels = new Color32[length];
            for (var i = 0; i < length; i++)
            {
                pixels[i] = new Color32(0, 0, 0, alpha);
            }

            return pixels;
        }

        [Test]
        public void IsAllBlack_AllBlack_ReturnsTrue()
        {
            var pixels = MakeBlack(256 * 256);

            Assert.That(ModelCaptureService.IsAllBlack(pixels), Is.True);
        }

        [Test]
        public void IsAllBlack_SingleNonBlackAtStart_ReturnsFalse()
        {
            var pixels = MakeBlack(1024);
            pixels[0] = new Color32(1, 0, 0, 255);

            Assert.That(ModelCaptureService.IsAllBlack(pixels), Is.False);
        }

        [Test]
        public void IsAllBlack_SingleNonBlackAtEnd_ReturnsFalse()
        {
            var pixels = MakeBlack(1024);
            pixels[pixels.Length - 1] = new Color32(0, 0, 1, 255);

            Assert.That(ModelCaptureService.IsAllBlack(pixels), Is.False);
        }

        [Test]
        public void IsAllBlack_SingleNonBlackInMiddle_ReturnsFalse()
        {
            var pixels = MakeBlack(1024);
            pixels[pixels.Length / 2] = new Color32(0, 1, 0, 255);

            Assert.That(ModelCaptureService.IsAllBlack(pixels), Is.False);
        }

        [Test]
        public void IsAllBlack_AllBlackWithNonZeroAlpha_ReturnsTrue()
        {
            // アルファは判定に関与しない (RGB のみで判定する)
            var pixels = MakeBlack(1024, alpha: 255);

            Assert.That(ModelCaptureService.IsAllBlack(pixels), Is.True);
        }

        [Test]
        public void IsAllBlack_EmptyArray_ReturnsTrue()
        {
            Assert.That(ModelCaptureService.IsAllBlack(new Color32[0]), Is.True);
        }
    }
}
