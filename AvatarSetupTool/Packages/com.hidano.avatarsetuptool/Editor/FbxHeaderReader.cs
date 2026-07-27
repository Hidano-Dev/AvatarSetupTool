using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Hidano.AvatarSetupTool.Editor
{
    /// <summary>
    /// FBX ファイルの先頭にあるヘッダメタデータ (いつ・どのツール・どのファイルから
    /// エクスポートされたか) を読み取る軽量パーサ。バイナリ FBX は FBXHeaderExtension
    /// (CreationTimeStamp / SceneInfo) とトップレベルの Creator ノードだけを辿り、
    /// Objects 以降の本体データは読まないため、巨大なファイルでも高速に動く。
    /// 返り値のキー:
    /// - "Creator" / "CreationTime"
    /// - "TimeStamp/Year" 〜 "TimeStamp/Second" (バイナリの CreationTimeStamp)
    /// - "Original|〜" / "LastSaved|〜" (SceneInfo の Properties70 に入っている文字列値)
    /// 解析に失敗した場合は取れた分だけ (または空の) 辞書を返す。
    /// </summary>
    internal static class FbxHeaderReader
    {
        private static readonly byte[] BinaryMagic = Encoding.ASCII.GetBytes("Kaydara FBX Binary  ");

        public static Dictionary<string, string> Read(string fullPath)
        {
            var result = new Dictionary<string, string>();
            try
            {
                using (var stream = new FileStream(
                    fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var magic = new byte[BinaryMagic.Length];
                    if (stream.Read(magic, 0, magic.Length) == magic.Length && IsBinaryMagic(magic))
                    {
                        ReadBinary(stream, result);
                    }
                    else
                    {
                        stream.Position = 0;
                        ReadAscii(stream, result);
                    }
                }
            }
            catch (Exception)
            {
                // 壊れた FBX や読み取り権限なしはデバッグ情報の欠落として扱う
            }

            return result;
        }

        private static bool IsBinaryMagic(byte[] magic)
        {
            for (var i = 0; i < BinaryMagic.Length; i++)
            {
                if (magic[i] != BinaryMagic[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static void ReadBinary(FileStream stream, Dictionary<string, string> result)
        {
            var reader = new BinaryReader(stream);
            stream.Position = 23; // マジック 21 bytes + 0x1A 0x00
            var version = reader.ReadUInt32();
            var wide = version >= 7500; // 7.5 からオフセット類が 64bit になった

            // トップレベルノードは FBXHeaderExtension, FileId, CreationTime, Creator,
            // GlobalSettings, Documents, ... Objects の順。メタデータはすべて Objects より
            // 前にあるため、Creator まで読めたら打ち切る
            while (stream.Position < stream.Length)
            {
                var name = ReadNode(reader, wide, result, string.Empty);
                if (name == null || name == "Objects" || result.ContainsKey("Creator"))
                {
                    break;
                }
            }
        }

        /// <summary>
        /// ノードを 1 つ読み、名前を返す (リスト終端の null レコードなら null)。
        /// 興味のある値は result へ集め、必要な枝 (ヘッダ配下) だけ再帰する。
        /// </summary>
        private static string ReadNode(
            BinaryReader reader, bool wide, Dictionary<string, string> result, string path)
        {
            long endOffset, numProps, propListLen;
            if (wide)
            {
                endOffset = (long)reader.ReadUInt64();
                numProps = (long)reader.ReadUInt64();
                propListLen = (long)reader.ReadUInt64();
            }
            else
            {
                endOffset = reader.ReadUInt32();
                numProps = reader.ReadUInt32();
                propListLen = reader.ReadUInt32();
            }

            int nameLen = reader.ReadByte();
            if (endOffset == 0)
            {
                return null;
            }

            var name = Encoding.ASCII.GetString(reader.ReadBytes(nameLen));
            var nodePath = path.Length == 0 ? name : path + "/" + name;

            var propsEnd = reader.BaseStream.Position + propListLen;
            var strings = new List<string>();
            var ints = new List<long>();
            for (long i = 0; i < numProps && reader.BaseStream.Position < propsEnd; i++)
            {
                if (!ReadProperty(reader, strings, ints))
                {
                    break;
                }
            }

            reader.BaseStream.Position = propsEnd;
            Collect(nodePath, strings, ints, result);

            if (ShouldDescend(nodePath))
            {
                while (reader.BaseStream.Position < endOffset)
                {
                    if (ReadNode(reader, wide, result, nodePath) == null)
                    {
                        break;
                    }
                }
            }

            reader.BaseStream.Position = endOffset;
            return name;
        }

        private static bool ShouldDescend(string nodePath)
        {
            return nodePath == "FBXHeaderExtension"
                || nodePath == "FBXHeaderExtension/CreationTimeStamp"
                || nodePath == "FBXHeaderExtension/SceneInfo"
                || nodePath == "FBXHeaderExtension/SceneInfo/Properties70";
        }

        private static void Collect(
            string nodePath, List<string> strings, List<long> ints, Dictionary<string, string> result)
        {
            if (nodePath == "Creator" || nodePath == "CreationTime")
            {
                if (strings.Count > 0 && !string.IsNullOrWhiteSpace(strings[0]))
                {
                    result[nodePath] = strings[0];
                }

                return;
            }

            if (nodePath.StartsWith("FBXHeaderExtension/CreationTimeStamp/", StringComparison.Ordinal)
                && ints.Count > 0)
            {
                result["TimeStamp/" + nodePath.Substring(nodePath.LastIndexOf('/') + 1)]
                    = ints[0].ToString();
                return;
            }

            // Properties70 の P ノード: [名前, 型, ラベル, フラグ, 値...] の順
            if (nodePath == "FBXHeaderExtension/SceneInfo/Properties70/P"
                && strings.Count >= 5
                && (strings[0].StartsWith("Original|", StringComparison.Ordinal)
                    || strings[0].StartsWith("LastSaved|", StringComparison.Ordinal))
                && !string.IsNullOrWhiteSpace(strings[4]))
            {
                result[strings[0]] = strings[4];
            }
        }

        private static bool ReadProperty(BinaryReader reader, List<string> strings, List<long> ints)
        {
            var type = (char)reader.ReadByte();
            switch (type)
            {
                case 'S':
                case 'R':
                {
                    var length = reader.ReadInt32();
                    var bytes = reader.ReadBytes(length);
                    if (type == 'S')
                    {
                        strings.Add(Encoding.UTF8.GetString(bytes));
                    }

                    return true;
                }

                case 'Y':
                    ints.Add(reader.ReadInt16());
                    return true;
                case 'C':
                    ints.Add(reader.ReadByte());
                    return true;
                case 'I':
                    ints.Add(reader.ReadInt32());
                    return true;
                case 'L':
                    ints.Add(reader.ReadInt64());
                    return true;
                case 'F':
                    reader.ReadSingle();
                    return true;
                case 'D':
                    reader.ReadDouble();
                    return true;

                case 'f':
                case 'd':
                case 'l':
                case 'i':
                case 'b':
                {
                    var arrayLength = reader.ReadInt32();
                    var encoding = reader.ReadInt32();
                    var compressedLength = reader.ReadInt32();
                    var elementSize = type == 'd' || type == 'l' ? 8 : type == 'b' ? 1 : 4;
                    reader.BaseStream.Position += encoding == 0
                        ? (long)arrayLength * elementSize
                        : compressedLength;
                    return true;
                }

                default:
                    return false; // 未知の型が来たらこのノードの残りは諦める
            }
        }

        /// <summary>
        /// ASCII FBX のヘッダを正規表現で読む。メタデータはファイル先頭付近にあるため
        /// 先頭 512KB だけ見れば十分。
        /// </summary>
        private static void ReadAscii(FileStream stream, Dictionary<string, string> result)
        {
            var buffer = new byte[512 * 1024];
            var read = stream.Read(buffer, 0, buffer.Length);
            var text = Encoding.UTF8.GetString(buffer, 0, read);

            var creator = Regex.Match(text, "^\\s*Creator:\\s*\"(.*)\"", RegexOptions.Multiline);
            if (creator.Success)
            {
                result["Creator"] = creator.Groups[1].Value;
            }

            var creationTime = Regex.Match(text, "^\\s*CreationTime:\\s*\"(.*)\"", RegexOptions.Multiline);
            if (creationTime.Success)
            {
                result["CreationTime"] = creationTime.Groups[1].Value;
            }

            foreach (Match match in Regex.Matches(
                text,
                "P:\\s*\"((?:Original|LastSaved)\\|[^\"]+)\",\\s*\"[^\"]*\",\\s*\"[^\"]*\",\\s*\"[^\"]*\",\\s*\"([^\"]*)\""))
            {
                if (!string.IsNullOrWhiteSpace(match.Groups[2].Value))
                {
                    result[match.Groups[1].Value] = match.Groups[2].Value;
                }
            }
        }
    }
}
