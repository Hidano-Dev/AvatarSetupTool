using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Hidano.AvatarSetupTool.Editor
{
    /// <summary>
    /// 外部ライブラリを使わずに Apple ProRes 422 (SMPTE RDD 36、4cc "apcn") の
    /// QuickTime MOV を書き出す最小エンコーダ。
    /// RGB を BT.709 (ビデオレンジ) の 10bit YCbCr 4:2:2 へ変換し、8x8 DCT →
    /// 固定量子化 (フラット行列 + 固定 qScale の固定品質 VBR) → RDD 36 の
    /// 適応 Rice/Exp-Golomb 符号でスライスごとにエントロピー符号化する。
    /// フレームは AddFrame へ 1 枚ずつ渡して逐次エンコードする (全フレームをメモリに溜めない)。
    /// ピクセルは <see cref="Mp4Writer"/> と同じボトムアップの行順で渡すこと。
    /// </summary>
    internal sealed class ProResWriter : IVideoFrameWriter
    {
        /// <summary>
        /// 量子化ステップ (スライスヘッダの qScale インデックス)。行列はデフォルト (全要素 4) を使い、
        /// 実効除数は DCT 係数 (正規直交 × 4) に対して 4 × QScale。2 でほぼ視覚的ロスレス。
        /// </summary>
        private const int QScale = 2;

        private const int MaxSliceMbCount = 8; // log2 = 3。ピクチャヘッダの値と一致させること
        private const int Log2SliceMbWidth = 3;

        /// <summary>RDD 36 のプログレッシブ用スキャン順 (係数の符号化順 → ブロック内ラスタ位置)。</summary>
        private static readonly byte[] ProgressiveScan =
        {
            0, 1, 8, 9, 2, 3, 10, 11,
            16, 17, 24, 25, 18, 19, 26, 27,
            4, 5, 12, 20, 13, 6, 7, 14,
            21, 28, 29, 22, 15, 23, 30, 31,
            32, 33, 40, 48, 41, 34, 35, 42,
            49, 56, 57, 50, 43, 36, 37, 44,
            51, 58, 59, 52, 45, 38, 39, 46,
            53, 60, 61, 54, 47, 55, 62, 63,
        };

        // 符号表 (codebook) は 1 バイトに (riceOrder << 5) | (expOrder << 2) | switchBits を詰めた形式。
        // 直前の値に応じて表を切り替える適応符号 (RDD 36 の規定通り)
        private const byte FirstDcCodebook = 0xB8;
        private static readonly byte[] DcCodebooks = { 0x04, 0x28, 0x28, 0x4D, 0x4D, 0x70, 0x70 };
        private static readonly byte[] RunCodebooks =
        {
            0x06, 0x06, 0x05, 0x05, 0x04, 0x29, 0x29, 0x29,
            0x29, 0x28, 0x28, 0x28, 0x28, 0x28, 0x28, 0x4C,
        };
        private static readonly byte[] LevelCodebooks =
        {
            0x04, 0x0A, 0x05, 0x06, 0x04, 0x28, 0x28, 0x28, 0x28, 0x4C,
        };

        /// <summary>正規直交 8 点 DCT-II の基底。Basis[u, x] = c(u)/2 * cos((2x+1)uπ/16)。</summary>
        private static readonly double[,] DctBasis = BuildDctBasis();

        private readonly FileStream stream;
        private readonly int width;
        private readonly int height;
        private readonly int chromaWidth;
        private readonly int frameRate;
        private readonly List<int> sampleSizes = new List<int>();
        private readonly long mdatSizePosition;
        private readonly long firstSampleOffset;

        // フレームごとに再利用するバッファ (トップダウン行順の 10bit プレーン)
        private readonly ushort[] planeY;
        private readonly ushort[] planeCb;
        private readonly ushort[] planeCr;
        private readonly int[] rowCb;
        private readonly int[] rowCr;

        public ProResWriter(string filePath, int width, int height, int frameRate)
        {
            this.width = width;
            this.height = height;
            this.frameRate = frameRate;
            chromaWidth = (width + 1) / 2;
            planeY = new ushort[width * height];
            planeCb = new ushort[chromaWidth * height];
            planeCr = new ushort[chromaWidth * height];
            rowCb = new int[width];
            rowCr = new int[width];

            stream = new FileStream(filePath, FileMode.Create, FileAccess.ReadWrite);
            WriteFtyp();
            mdatSizePosition = stream.Position;
            WriteBe32(stream, 0); // mdat サイズ。Dispose で書き戻す
            WriteFourCc(stream, "mdat");
            firstSampleOffset = stream.Position;
        }

        public void AddFrame(Color32[] pixels)
        {
            ConvertToYCbCr422(pixels);
            var frame = EncodeFrame();
            stream.Write(frame, 0, frame.Length);
            sampleSizes.Add(frame.Length);
        }

        public void Dispose()
        {
            var mdatEnd = stream.Position;
            stream.Position = mdatSizePosition;
            WriteBe32(stream, (int)(mdatEnd - mdatSizePosition));
            stream.Position = mdatEnd;
            var moov = BuildMoov();
            stream.Write(moov, 0, moov.Length);
            stream.Dispose();
        }

        // ---- 色変換 (BT.709 ビデオレンジ 10bit、水平 1/2 のクロマは隣接 2 画素の平均) ----

        private void ConvertToYCbCr422(Color32[] pixels)
        {
            for (var y = 0; y < height; y++)
            {
                var src = (height - 1 - y) * width; // ボトムアップ → トップダウン
                var dstY = y * width;
                for (var x = 0; x < width; x++)
                {
                    var p = pixels[src + x];
                    planeY[dstY + x] = (ushort)(((187 * p.r + 629 * p.g + 63 * p.b + 128) >> 8) + 64);
                    rowCb[x] = ((-103 * p.r - 347 * p.g + 450 * p.b + 128) >> 8) + 512;
                    rowCr[x] = ((450 * p.r - 409 * p.g - 41 * p.b + 128) >> 8) + 512;
                }

                var dstC = y * chromaWidth;
                for (var cx = 0; cx < chromaWidth; cx++)
                {
                    var x0 = cx * 2;
                    var x1 = Math.Min(x0 + 1, width - 1);
                    planeCb[dstC + cx] = (ushort)((rowCb[x0] + rowCb[x1] + 1) >> 1);
                    planeCr[dstC + cx] = (ushort)((rowCr[x0] + rowCr[x1] + 1) >> 1);
                }
            }
        }

        // ---- フレーム / ピクチャ / スライスの符号化 ----

        private byte[] EncodeFrame()
        {
            var mbWidth = (width + 15) / 16;
            var mbHeight = (height + 15) / 16;

            var slices = new List<byte[]>();
            for (var mbY = 0; mbY < mbHeight; mbY++)
            {
                var mbX = 0;
                while (mbX < mbWidth)
                {
                    // 行末はデコーダと同じ規則でスライス幅を 8 → 4 → 2 → 1 と半減させて埋める
                    var mbCount = MaxSliceMbCount;
                    while (mbWidth - mbX < mbCount)
                    {
                        mbCount >>= 1;
                    }

                    slices.Add(EncodeSlice(mbX, mbY, mbCount));
                    mbX += mbCount;
                }
            }

            var sliceTableSize = slices.Count * 2;
            var slicesSize = 0;
            foreach (var slice in slices)
            {
                slicesSize += slice.Length;
            }

            const int frameHeaderSize = 20; // 量子化行列は省略 (デフォルトの全要素 4 を使用)
            const int pictureHeaderSize = 8;
            var pictureDataSize = sliceTableSize + slicesSize;
            var frameSize = 8 + frameHeaderSize + pictureHeaderSize + pictureDataSize;

            var buffer = new MemoryStream(frameSize);
            WriteBe32(buffer, frameSize);
            WriteFourCc(buffer, "icpf");

            // フレームヘッダ
            WriteBe16(buffer, frameHeaderSize);
            WriteBe16(buffer, 0); // バージョン
            WriteFourCc(buffer, "ast0"); // エンコーダ識別子 (Avatar Setup Tool)
            WriteBe16(buffer, width);
            WriteBe16(buffer, height);
            buffer.WriteByte(0x80); // 4:2:2、プログレッシブ
            buffer.WriteByte(0);
            buffer.WriteByte(1); // 色域: BT.709
            buffer.WriteByte(1); // 伝達特性: BT.709
            buffer.WriteByte(1); // 変換行列: BT.709
            buffer.WriteByte(0); // アルファなし
            buffer.WriteByte(0);
            buffer.WriteByte(0); // 量子化行列なし

            // ピクチャヘッダ + スライスサイズ表 + スライス本体
            buffer.WriteByte(pictureHeaderSize << 3);
            WriteBe32(buffer, pictureDataSize);
            WriteBe16(buffer, slices.Count);
            buffer.WriteByte(Log2SliceMbWidth << 4); // スライス高さは 1 MB (log2 = 0)
            foreach (var slice in slices)
            {
                WriteBe16(buffer, slice.Length);
            }

            foreach (var slice in slices)
            {
                buffer.Write(slice, 0, slice.Length);
            }

            return buffer.ToArray();
        }

        private byte[] EncodeSlice(int mbX, int mbY, int mbCount)
        {
            var lumaBlocks = mbCount * 4;
            var chromaBlocks = mbCount * 2;
            var lumaCoeffs = new int[lumaBlocks * 64];
            var cbCoeffs = new int[chromaBlocks * 64];
            var crCoeffs = new int[chromaBlocks * 64];

            var block = new double[64];
            for (var mb = 0; mb < mbCount; mb++)
            {
                var pixelX = (mbX + mb) * 16;
                var pixelY = mbY * 16;

                // 輝度は MB あたり 8x8 を左上 → 右上 → 左下 → 右下の順に 4 ブロック
                for (var sub = 0; sub < 4; sub++)
                {
                    var blockX = pixelX + (sub & 1) * 8;
                    var blockY = pixelY + (sub >> 1) * 8;
                    ExtractBlock(planeY, width, blockX, blockY, block);
                    QuantizeBlock(block, lumaCoeffs, (mb * 4 + sub) * 64);
                }

                // クロマ (幅 1/2) は MB あたり上 → 下の 2 ブロック
                for (var sub = 0; sub < 2; sub++)
                {
                    var blockX = pixelX / 2;
                    var blockY = pixelY + sub * 8;
                    ExtractBlock(planeCb, chromaWidth, blockX, blockY, block);
                    QuantizeBlock(block, cbCoeffs, (mb * 2 + sub) * 64);
                    ExtractBlock(planeCr, chromaWidth, blockX, blockY, block);
                    QuantizeBlock(block, crCoeffs, (mb * 2 + sub) * 64);
                }
            }

            var lumaData = EncodeComponent(lumaCoeffs, lumaBlocks);
            var cbData = EncodeComponent(cbCoeffs, chromaBlocks);
            var crData = EncodeComponent(crCoeffs, chromaBlocks);

            var slice = new MemoryStream(6 + lumaData.Length + cbData.Length + crData.Length);
            slice.WriteByte(6 << 3); // スライスヘッダ 6 バイト
            slice.WriteByte(QScale);
            WriteBe16(slice, lumaData.Length);
            WriteBe16(slice, cbData.Length);
            slice.Write(lumaData, 0, lumaData.Length);
            slice.Write(cbData, 0, cbData.Length);
            slice.Write(crData, 0, crData.Length); // Cr のサイズはスライス全体から逆算される
            return slice.ToArray();
        }

        /// <summary>右端・下端の MB は端のピクセルを複製してパディングする。</summary>
        private void ExtractBlock(ushort[] plane, int planeWidth, int blockX, int blockY, double[] dst)
        {
            var planeHeight = height;
            for (var y = 0; y < 8; y++)
            {
                var row = Math.Min(blockY + y, planeHeight - 1) * planeWidth;
                for (var x = 0; x < 8; x++)
                {
                    dst[y * 8 + x] = plane[row + Math.Min(blockX + x, planeWidth - 1)];
                }
            }
        }

        /// <summary>
        /// 8x8 DCT → 量子化。係数はビットストリーム上、正規直交 DCT の 4 倍スケールで
        /// 扱われるため、フラット行列 4 × QScale の除算は正規直交係数 / QScale に等しい。
        /// DC はミッドグレー (512 → DC 4096) を原点として符号化する。
        /// </summary>
        private static void QuantizeBlock(double[] block, int[] coeffs, int offset)
        {
            Span<double> temp = stackalloc double[64];

            // 行方向 → 列方向の分離 DCT
            for (var y = 0; y < 8; y++)
            {
                for (var u = 0; u < 8; u++)
                {
                    double sum = 0;
                    for (var x = 0; x < 8; x++)
                    {
                        sum += block[y * 8 + x] * DctBasis[u, x];
                    }

                    temp[y * 8 + u] = sum;
                }
            }

            for (var u = 0; u < 8; u++)
            {
                for (var v = 0; v < 8; v++)
                {
                    double sum = 0;
                    for (var y = 0; y < 8; y++)
                    {
                        sum += temp[y * 8 + u] * DctBasis[v, y];
                    }

                    var raster = v * 8 + u;
                    coeffs[offset + raster] = raster == 0
                        ? (int)Math.Round((sum - 4096.0) / QScale)
                        : (int)Math.Round(sum / QScale);
                }
            }
        }

        /// <summary>1 コンポーネント分 (スライス内全ブロック) の DC + AC をバイト列へ符号化する。</summary>
        private static byte[] EncodeComponent(int[] coeffs, int blocks)
        {
            var writer = new BitWriter();

            // DC: 先頭は絶対値、以降は直前との差分。符号表は直前の符号語で切り替える
            var prevDc = coeffs[0];
            EncodeCodeword(writer, FirstDcCodebook, ToGolomb(prevDc));
            var prevCode = 5;
            var signMask = 0;
            for (var i = 1; i < blocks; i++)
            {
                var dc = coeffs[i * 64];
                var delta = dc - prevDc;
                var diffSignMask = (delta >> 31) ^ signMask;
                var level = Math.Abs(delta);
                var code = level == 0 ? 0 : (level << 1) + diffSignMask;
                EncodeCodeword(writer, DcCodebooks[Math.Min(prevCode, 6)], code);
                prevCode = code;
                signMask = delta >> 31;
                prevDc = dc;
            }

            // AC: スキャン位置ごとに全ブロックを横断する順序で run/level を符号化する
            var prevRun = 4;
            var prevLevel = 2;
            var run = 0;
            for (var i = 1; i < 64; i++)
            {
                var raster = ProgressiveScan[i];
                for (var j = 0; j < blocks; j++)
                {
                    var value = coeffs[j * 64 + raster];
                    if (value == 0)
                    {
                        run++;
                        continue;
                    }

                    EncodeCodeword(writer, RunCodebooks[Math.Min(prevRun, 15)], run);
                    prevRun = run;
                    run = 0;
                    var level = Math.Abs(value);
                    EncodeCodeword(writer, LevelCodebooks[Math.Min(prevLevel, 9)], level - 1);
                    prevLevel = level;
                    writer.WriteBits(value < 0 ? 1u : 0u, 1);
                }
            }

            return writer.ToArray();
        }

        /// <summary>符号付き値を 0, -1, 1, -2, ... → 0, 1, 2, 3, ... の符号なしへ折り畳む。</summary>
        private static int ToGolomb(int value)
        {
            return (value << 1) ^ (value >> 31);
        }

        /// <summary>
        /// RDD 36 の符号語 (小さい値は Rice、閾値以上は Exp-Golomb へ切り替わるハイブリッド)。
        /// </summary>
        private static void EncodeCodeword(BitWriter writer, byte codebook, int value)
        {
            var switchBits = codebook & 3;
            var riceOrder = codebook >> 5;
            var expOrder = (codebook >> 2) & 7;
            var firstExp = (switchBits + 1) << riceOrder;

            if (value >= firstExp)
            {
                var shifted = value - firstExp + (1 << expOrder);
                var exponent = Log2(shifted);
                writer.WriteBits(0, exponent - expOrder + switchBits + 1);
                writer.WriteBits((uint)shifted, exponent + 1);
            }
            else if (riceOrder > 0)
            {
                writer.WriteBits(0, value >> riceOrder);
                writer.WriteBits(1, 1);
                writer.WriteBits((uint)(value & ((1 << riceOrder) - 1)), riceOrder);
            }
            else
            {
                writer.WriteBits(0, value);
                writer.WriteBits(1, 1);
            }
        }

        private static int Log2(int value)
        {
            var result = 0;
            while (value > 1)
            {
                value >>= 1;
                result++;
            }

            return result;
        }

        private static double[,] BuildDctBasis()
        {
            var basis = new double[8, 8];
            for (var u = 0; u < 8; u++)
            {
                var scale = u == 0 ? Math.Sqrt(0.125) : 0.5;
                for (var x = 0; x < 8; x++)
                {
                    basis[u, x] = scale * Math.Cos((2 * x + 1) * u * Math.PI / 16.0);
                }
            }

            return basis;
        }

        /// <summary>MSB ファーストのビット列。ToArray で 0 詰めのバイト境界へ揃える。</summary>
        private sealed class BitWriter
        {
            private readonly MemoryStream buffer = new MemoryStream();
            private ulong accumulator;
            private int bitCount;

            public void WriteBits(uint value, int count)
            {
                accumulator = (accumulator << count) | value;
                bitCount += count;
                while (bitCount >= 8)
                {
                    bitCount -= 8;
                    buffer.WriteByte((byte)(accumulator >> bitCount));
                }
            }

            public byte[] ToArray()
            {
                if (bitCount > 0)
                {
                    buffer.WriteByte((byte)(accumulator << (8 - bitCount)));
                    accumulator = 0;
                    bitCount = 0;
                }

                return buffer.ToArray();
            }
        }

        // ---- QuickTime MOV コンテナ ----

        private void WriteFtyp()
        {
            WriteBe32(stream, 20);
            WriteFourCc(stream, "ftyp");
            WriteFourCc(stream, "qt  ");
            WriteBe32(stream, 0x200);
            WriteFourCc(stream, "qt  ");
        }

        private byte[] BuildMoov()
        {
            var frames = sampleSizes.Count;
            return Atom("moov",
                BuildMvhd(frames),
                Atom("trak",
                    BuildTkhd(frames),
                    Atom("mdia",
                        BuildMdhd(frames),
                        BuildHdlr("mhlr", "vide", "Video"),
                        Atom("minf",
                            BuildVmhd(),
                            BuildHdlr("dhlr", "url ", "Data"),
                            Atom("dinf", BuildDref()),
                            Atom("stbl",
                                BuildStsd(),
                                BuildStts(frames),
                                BuildStsc(frames),
                                BuildStsz(),
                                BuildStco())))));
        }

        private static readonly uint[] IdentityMatrix =
        {
            0x00010000, 0, 0,
            0, 0x00010000, 0,
            0, 0, 0x40000000,
        };

        private byte[] BuildMvhd(int frames)
        {
            var body = new MemoryStream();
            WriteBe32(body, 0); // バージョン + フラグ
            WriteBe32(body, 0); // 作成日時
            WriteBe32(body, 0); // 更新日時
            WriteBe32(body, frameRate); // タイムスケール (1 フレーム = 1 tick)
            WriteBe32(body, frames);
            WriteBe32(body, 0x00010000); // 再生レート 1.0
            WriteBe16(body, 0x0100); // 音量 1.0
            for (var i = 0; i < 5; i++)
            {
                WriteBe16(body, 0);
            }

            foreach (var value in IdentityMatrix)
            {
                WriteBe32(body, (int)value);
            }

            for (var i = 0; i < 6; i++)
            {
                WriteBe32(body, 0); // プレビュー / ポスター / 選択範囲 / 現在時刻
            }

            WriteBe32(body, 2); // 次のトラック ID
            return Atom("mvhd", body.ToArray());
        }

        private byte[] BuildTkhd(int frames)
        {
            var body = new MemoryStream();
            WriteBe32(body, 0x00000003); // バージョン 0、フラグ: 有効 + ムービー内で使用
            WriteBe32(body, 0);
            WriteBe32(body, 0);
            WriteBe32(body, 1); // トラック ID
            WriteBe32(body, 0);
            WriteBe32(body, frames);
            WriteBe32(body, 0);
            WriteBe32(body, 0);
            WriteBe16(body, 0); // レイヤー
            WriteBe16(body, 0); // 代替グループ
            WriteBe16(body, 0); // 音量
            WriteBe16(body, 0);
            foreach (var value in IdentityMatrix)
            {
                WriteBe32(body, (int)value);
            }

            WriteBe32(body, width << 16);
            WriteBe32(body, height << 16);
            return Atom("tkhd", body.ToArray());
        }

        private byte[] BuildMdhd(int frames)
        {
            var body = new MemoryStream();
            WriteBe32(body, 0);
            WriteBe32(body, 0);
            WriteBe32(body, 0);
            WriteBe32(body, frameRate);
            WriteBe32(body, frames);
            WriteBe16(body, 0x55C4); // 言語: und
            WriteBe16(body, 0);
            return Atom("mdhd", body.ToArray());
        }

        private static byte[] BuildHdlr(string componentType, string subType, string name)
        {
            var body = new MemoryStream();
            WriteBe32(body, 0);
            WriteFourCc(body, componentType);
            WriteFourCc(body, subType);
            WriteBe32(body, 0);
            WriteBe32(body, 0);
            WriteBe32(body, 0);
            body.WriteByte((byte)name.Length); // QuickTime 形式の Pascal 文字列
            foreach (var c in name)
            {
                body.WriteByte((byte)c);
            }

            return Atom("hdlr", body.ToArray());
        }

        private static byte[] BuildVmhd()
        {
            var body = new MemoryStream();
            WriteBe32(body, 1); // フラグ 1 (QuickTime 互換)
            WriteBe16(body, 0); // グラフィックスモード: コピー
            WriteBe16(body, 0);
            WriteBe16(body, 0);
            WriteBe16(body, 0);
            return Atom("vmhd", body.ToArray());
        }

        private static byte[] BuildDref()
        {
            var body = new MemoryStream();
            WriteBe32(body, 0);
            WriteBe32(body, 1); // エントリ数
            WriteBe32(body, 12);
            WriteFourCc(body, "url ");
            WriteBe32(body, 1); // フラグ: 自己完結
            return Atom("dref", body.ToArray());
        }

        private byte[] BuildStsd()
        {
            var desc = new MemoryStream();
            WriteBe32(desc, 0); // 予約 6 バイト + 参照インデックスの前半
            WriteBe16(desc, 0);
            WriteBe16(desc, 1); // データ参照インデックス
            WriteBe16(desc, 0); // バージョン
            WriteBe16(desc, 0); // リビジョン
            WriteFourCc(desc, "ast0"); // ベンダー
            WriteBe32(desc, 0); // 時間方向の品質
            WriteBe32(desc, 512); // 空間方向の品質
            WriteBe16(desc, width);
            WriteBe16(desc, height);
            WriteBe32(desc, 0x00480000); // 72 dpi
            WriteBe32(desc, 0x00480000);
            WriteBe32(desc, 0);
            WriteBe16(desc, 1); // 1 サンプルあたりのフレーム数
            const string compressorName = "ProRes 422";
            desc.WriteByte((byte)compressorName.Length);
            foreach (var c in compressorName)
            {
                desc.WriteByte((byte)c);
            }

            for (var i = compressorName.Length + 1; i < 32; i++)
            {
                desc.WriteByte(0);
            }

            WriteBe16(desc, 24); // 色深度
            WriteBe16(desc, unchecked((short)-1) & 0xFFFF); // カラーテーブルなし

            // 拡張: 色情報 (BT.709)、プログレッシブ、正方形ピクセル
            var colr = new MemoryStream();
            WriteFourCc(colr, "nclc");
            WriteBe16(colr, 1);
            WriteBe16(colr, 1);
            WriteBe16(colr, 1);

            var fiel = new MemoryStream();
            fiel.WriteByte(1);
            fiel.WriteByte(0);

            var pasp = new MemoryStream();
            WriteBe32(pasp, 1);
            WriteBe32(pasp, 1);

            var entry = Atom("apcn",
                desc.ToArray(),
                Atom("colr", colr.ToArray()),
                Atom("fiel", fiel.ToArray()),
                Atom("pasp", pasp.ToArray()));

            var body = new MemoryStream();
            WriteBe32(body, 0);
            WriteBe32(body, 1); // エントリ数
            body.Write(entry, 0, entry.Length);
            return Atom("stsd", body.ToArray());
        }

        private static byte[] BuildStts(int frames)
        {
            var body = new MemoryStream();
            WriteBe32(body, 0);
            WriteBe32(body, 1);
            WriteBe32(body, frames);
            WriteBe32(body, 1); // 全フレーム同一の表示時間 (1 tick)
            return Atom("stts", body.ToArray());
        }

        private static byte[] BuildStsc(int frames)
        {
            var body = new MemoryStream();
            WriteBe32(body, 0);
            WriteBe32(body, 1);
            WriteBe32(body, 1); // 先頭チャンクから
            WriteBe32(body, frames); // 全サンプルを 1 チャンクに格納
            WriteBe32(body, 1); // サンプル記述インデックス
            return Atom("stsc", body.ToArray());
        }

        private byte[] BuildStsz()
        {
            var body = new MemoryStream();
            WriteBe32(body, 0);
            WriteBe32(body, 0); // サイズ可変
            WriteBe32(body, sampleSizes.Count);
            foreach (var size in sampleSizes)
            {
                WriteBe32(body, size);
            }

            return Atom("stsz", body.ToArray());
        }

        private byte[] BuildStco()
        {
            var body = new MemoryStream();
            WriteBe32(body, 0);
            WriteBe32(body, 1);
            WriteBe32(body, (int)firstSampleOffset);
            return Atom("stco", body.ToArray());
        }

        private static byte[] Atom(string type, params byte[][] parts)
        {
            var size = 8;
            foreach (var part in parts)
            {
                size += part.Length;
            }

            var result = new MemoryStream(size);
            WriteBe32(result, size);
            WriteFourCc(result, type);
            foreach (var part in parts)
            {
                result.Write(part, 0, part.Length);
            }

            return result.ToArray();
        }

        private static void WriteBe32(Stream stream, int value)
        {
            stream.WriteByte((byte)(value >> 24));
            stream.WriteByte((byte)(value >> 16));
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)value);
        }

        private static void WriteBe16(Stream stream, int value)
        {
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)value);
        }

        private static void WriteFourCc(Stream stream, string fourCc)
        {
            foreach (var c in fourCc)
            {
                stream.WriteByte((byte)c);
            }
        }
    }
}
