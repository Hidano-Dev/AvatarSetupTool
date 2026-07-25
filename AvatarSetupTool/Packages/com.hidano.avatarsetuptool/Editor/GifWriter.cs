using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Hidano.AvatarSetupTool.Editor
{
    /// <summary>
    /// 外部ライブラリを使わずに GIF89a のループアニメーションを書き出す最小エンコーダ。
    /// 各フレームを RGB555 のヒストグラムに集計し、メディアンカットで 256 色以下の
    /// ローカルパレットを作成、Floyd–Steinberg 誤差拡散で割り当ててから LZW 圧縮する。
    /// フレームのピクセルは上端の行から順(トップダウン)で渡すこと。
    /// </summary>
    internal static class GifWriter
    {
        private const int MaxColors = 256;
        private const int HistogramSize = 1 << 15; // RGB 各 5bit
        private const int MaxLzwCode = 4096;
        private const int MaxLzwCodeSize = 12;

        private static readonly int[] ChannelShifts = { 10, 5, 0 };

        public static void Write(
            string filePath, IReadOnlyList<Color32[]> frames, int width, int height, int delayCentiseconds)
        {
            using (var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write))
            {
                WriteHeader(stream, width, height);
                WriteLoopExtension(stream);
                foreach (var frame in frames)
                {
                    WriteFrame(stream, frame, width, height, delayCentiseconds);
                }

                stream.WriteByte(0x3B); // Trailer
            }
        }

        private static void WriteHeader(Stream stream, int width, int height)
        {
            stream.Write(new[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a' }, 0, 6);
            WriteUInt16(stream, width);
            WriteUInt16(stream, height);
            stream.WriteByte(0x70); // グローバルパレットなし・色分解能 8bit
            stream.WriteByte(0);    // 背景色インデックス
            stream.WriteByte(0);    // ピクセルアスペクト比
        }

        /// <summary>NETSCAPE2.0 拡張で無限ループを指定する。</summary>
        private static void WriteLoopExtension(Stream stream)
        {
            var block = new byte[]
            {
                0x21, 0xFF, 0x0B,
                (byte)'N', (byte)'E', (byte)'T', (byte)'S', (byte)'C', (byte)'A', (byte)'P', (byte)'E',
                (byte)'2', (byte)'.', (byte)'0',
                0x03, 0x01, 0x00, 0x00, // ループ回数 0 = 無限
                0x00,
            };
            stream.Write(block, 0, block.Length);
        }

        private static void WriteFrame(
            Stream stream, Color32[] pixels, int width, int height, int delayCentiseconds)
        {
            var (palette, indices, paletteBits) = Quantize(pixels, width, height);

            // Graphic Control Extension
            stream.WriteByte(0x21);
            stream.WriteByte(0xF9);
            stream.WriteByte(0x04);
            stream.WriteByte(0x04); // 破棄方法: 前フレームをそのまま残す
            WriteUInt16(stream, delayCentiseconds);
            stream.WriteByte(0);    // 透過インデックス(未使用)
            stream.WriteByte(0);    // 終端

            // Image Descriptor
            stream.WriteByte(0x2C);
            WriteUInt16(stream, 0);
            WriteUInt16(stream, 0);
            WriteUInt16(stream, width);
            WriteUInt16(stream, height);
            stream.WriteByte((byte)(0x80 | (paletteBits - 1))); // ローカルパレットあり

            stream.Write(palette, 0, palette.Length);
            WriteImageData(stream, indices, paletteBits);
        }

        /// <summary>
        /// フレームを 256 色以下に量子化し、パレット(2 の冪サイズ、RGB 連続)と
        /// 各ピクセルのパレットインデックスを返す。
        /// </summary>
        private static (byte[] Palette, byte[] Indices, int PaletteBits) Quantize(
            Color32[] pixels, int width, int height)
        {
            var histogram = new int[HistogramSize];
            for (var i = 0; i < pixels.Length; i++)
            {
                var p = pixels[i];
                histogram[((p.r >> 3) << 10) | ((p.g >> 3) << 5) | (p.b >> 3)]++;
            }

            var usedBins = new List<int>();
            for (var bin = 0; bin < HistogramSize; bin++)
            {
                if (histogram[bin] > 0)
                {
                    usedBins.Add(bin);
                }
            }

            var boxes = BuildBoxes(usedBins, histogram);

            var paletteBits = 1;
            while ((1 << paletteBits) < boxes.Count)
            {
                paletteBits++;
            }

            var palette = new byte[(1 << paletteBits) * 3];
            for (var boxIndex = 0; boxIndex < boxes.Count; boxIndex++)
            {
                long sumR = 0, sumG = 0, sumB = 0, count = 0;
                foreach (var bin in boxes[boxIndex])
                {
                    long weight = histogram[bin];
                    sumR += Expand5((bin >> 10) & 0x1F) * weight;
                    sumG += Expand5((bin >> 5) & 0x1F) * weight;
                    sumB += Expand5(bin & 0x1F) * weight;
                    count += weight;
                }

                palette[boxIndex * 3] = (byte)((sumR + count / 2) / count);
                palette[boxIndex * 3 + 1] = (byte)((sumG + count / 2) / count);
                palette[boxIndex * 3 + 2] = (byte)((sumB + count / 2) / count);
            }

            var indices = Dither(pixels, width, height, palette, boxes.Count);
            return (palette, indices, paletteBits);
        }

        /// <summary>
        /// Floyd–Steinberg 誤差拡散で各ピクセルを最も近いパレット色へ割り当てる。
        /// パレットに無い中間色を点描で擬似表現し、グラデーションのバンディングを抑える。
        /// </summary>
        private static byte[] Dither(Color32[] pixels, int width, int height, byte[] palette, int paletteCount)
        {
            var indices = new byte[pixels.Length];

            // 最近傍パレット検索のキャッシュ(RGB555 単位で近似)
            var nearestCache = new short[HistogramSize];
            for (var i = 0; i < nearestCache.Length; i++)
            {
                nearestCache[i] = -1;
            }

            // 誤差バッファは 16 倍スケール。ピクセル x はバッファの x+1 に対応させ、
            // 両端 1 要素の余白で境界チェックを省く
            var currentR = new int[width + 2];
            var currentG = new int[width + 2];
            var currentB = new int[width + 2];
            var nextR = new int[width + 2];
            var nextG = new int[width + 2];
            var nextB = new int[width + 2];

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var i = y * width + x;
                    var p = pixels[i];
                    var r = ClampByte(p.r + ((currentR[x + 1] + 8) >> 4));
                    var g = ClampByte(p.g + ((currentG[x + 1] + 8) >> 4));
                    var b = ClampByte(p.b + ((currentB[x + 1] + 8) >> 4));

                    var key = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);
                    var index = nearestCache[key];
                    if (index < 0)
                    {
                        index = (short)FindNearest(palette, paletteCount, r, g, b);
                        nearestCache[key] = index;
                    }

                    indices[i] = (byte)index;

                    var errR = r - palette[index * 3];
                    var errG = g - palette[index * 3 + 1];
                    var errB = b - palette[index * 3 + 2];
                    currentR[x + 2] += errR * 7;
                    currentG[x + 2] += errG * 7;
                    currentB[x + 2] += errB * 7;
                    nextR[x] += errR * 3;
                    nextG[x] += errG * 3;
                    nextB[x] += errB * 3;
                    nextR[x + 1] += errR * 5;
                    nextG[x + 1] += errG * 5;
                    nextB[x + 1] += errB * 5;
                    nextR[x + 2] += errR;
                    nextG[x + 2] += errG;
                    nextB[x + 2] += errB;
                }

                (currentR, nextR) = (nextR, currentR);
                (currentG, nextG) = (nextG, currentG);
                (currentB, nextB) = (nextB, currentB);
                System.Array.Clear(nextR, 0, nextR.Length);
                System.Array.Clear(nextG, 0, nextG.Length);
                System.Array.Clear(nextB, 0, nextB.Length);
            }

            return indices;
        }

        private static int FindNearest(byte[] palette, int paletteCount, int r, int g, int b)
        {
            var best = 0;
            var bestDistance = int.MaxValue;
            for (var i = 0; i < paletteCount; i++)
            {
                var dr = r - palette[i * 3];
                var dg = g - palette[i * 3 + 1];
                var db = b - palette[i * 3 + 2];
                var distance = dr * dr + dg * dg + db * db;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                }
            }

            return best;
        }

        private static int ClampByte(int value)
        {
            return value < 0 ? 0 : value > 255 ? 255 : value;
        }

        /// <summary>
        /// メディアンカット。出現色(bin)の集合を、最も色範囲の広い箱を
        /// ピクセル数の中央で割る操作の繰り返しで 256 箱以下に分ける。
        /// </summary>
        private static List<List<int>> BuildBoxes(List<int> bins, int[] histogram)
        {
            if (bins.Count <= MaxColors)
            {
                var singles = new List<List<int>>(bins.Count);
                foreach (var bin in bins)
                {
                    singles.Add(new List<int> { bin });
                }

                return singles;
            }

            var boxes = new List<List<int>> { bins };
            while (boxes.Count < MaxColors)
            {
                var targetBox = -1;
                var targetShift = 0;
                var widestRange = 0;
                for (var i = 0; i < boxes.Count; i++)
                {
                    if (boxes[i].Count < 2)
                    {
                        continue;
                    }

                    foreach (var shift in ChannelShifts)
                    {
                        var min = 31;
                        var max = 0;
                        foreach (var bin in boxes[i])
                        {
                            var value = (bin >> shift) & 0x1F;
                            min = Mathf.Min(min, value);
                            max = Mathf.Max(max, value);
                        }

                        if (max - min > widestRange)
                        {
                            widestRange = max - min;
                            targetBox = i;
                            targetShift = shift;
                        }
                    }
                }

                if (targetBox < 0)
                {
                    break;
                }

                var box = boxes[targetBox];
                var shiftLocal = targetShift;
                box.Sort((a, b) => ((a >> shiftLocal) & 0x1F) - ((b >> shiftLocal) & 0x1F));

                long total = 0;
                foreach (var bin in box)
                {
                    total += histogram[bin];
                }

                long accumulated = 0;
                var splitIndex = 0;
                while (splitIndex < box.Count - 1 && accumulated + histogram[box[splitIndex]] <= total / 2)
                {
                    accumulated += histogram[box[splitIndex]];
                    splitIndex++;
                }

                if (splitIndex == 0)
                {
                    splitIndex = 1;
                }

                boxes[targetBox] = box.GetRange(0, splitIndex);
                boxes.Add(box.GetRange(splitIndex, box.Count - splitIndex));
            }

            return boxes;
        }

        private static void WriteImageData(Stream stream, byte[] indices, int paletteBits)
        {
            var minCodeSize = Mathf.Max(2, paletteBits);
            stream.WriteByte((byte)minCodeSize);

            var clearCode = 1 << minCodeSize;
            var endCode = clearCode + 1;
            var codeSize = minCodeSize + 1;
            var nextCode = endCode + 1;
            var codeTable = new Dictionary<int, int>();

            var block = new byte[255];
            var blockLength = 0;
            var bitBuffer = 0;
            var bitCount = 0;

            void FlushBlock()
            {
                if (blockLength == 0)
                {
                    return;
                }

                stream.WriteByte((byte)blockLength);
                stream.Write(block, 0, blockLength);
                blockLength = 0;
            }

            void Emit(int code)
            {
                bitBuffer |= code << bitCount;
                bitCount += codeSize;
                while (bitCount >= 8)
                {
                    block[blockLength++] = (byte)bitBuffer;
                    if (blockLength == block.Length)
                    {
                        FlushBlock();
                    }

                    bitBuffer >>= 8;
                    bitCount -= 8;
                }
            }

            Emit(clearCode);
            int prev = indices[0];
            for (var i = 1; i < indices.Length; i++)
            {
                var key = (prev << 8) | indices[i];
                if (codeTable.TryGetValue(key, out var existing))
                {
                    prev = existing;
                    continue;
                }

                Emit(prev);
                if (nextCode < MaxLzwCode)
                {
                    // 次に割り当てるコードが現在のビット幅に収まらなくなったら幅を上げる
                    if (nextCode == 1 << codeSize && codeSize < MaxLzwCodeSize)
                    {
                        codeSize++;
                    }

                    codeTable[key] = nextCode++;
                }
                else
                {
                    Emit(clearCode);
                    codeTable.Clear();
                    codeSize = minCodeSize + 1;
                    nextCode = endCode + 1;
                }

                prev = indices[i];
            }

            Emit(prev);
            Emit(endCode);
            if (bitCount > 0)
            {
                block[blockLength++] = (byte)bitBuffer;
            }

            FlushBlock();
            stream.WriteByte(0); // 画像データブロック終端
        }

        private static void WriteUInt16(Stream stream, int value)
        {
            stream.WriteByte((byte)(value & 0xFF));
            stream.WriteByte((byte)((value >> 8) & 0xFF));
        }

        private static int Expand5(int value)
        {
            return (value << 3) | (value >> 2);
        }
    }
}
