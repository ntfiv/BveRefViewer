using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace BveRefViewer
{
    /// <summary>
    /// BVEのテキストファイルを読み込んだ結果。
    /// </summary>
    internal sealed class BveTextFile
    {
        public string[] Lines;
        /// <summary>実際に使った文字コード名(表示用)。</summary>
        public string EncodingName;
        /// <summary>ヘッダーの種類部分("Vehicle"、"Vehicle Sound"など)。ヘッダーが無ければnull。</summary>
        public string HeaderType;
        /// <summary>ヘッダーのバージョン("2.00"など)。</summary>
        public string HeaderVersion;
        /// <summary>1行目がヘッダー行かどうか(参照の解析では読み飛ばす)。</summary>
        public bool FirstLineIsHeader;
        /// <summary>BVE本体の形式("BveTs ～"のヘッダー)かどうか。</summary>
        public bool IsBveTs;
    }

    /// <summary>
    /// BVEのテキストファイルの読み込み(文字コードの判定とヘッダーの解析)。
    /// 文字コードは、BOM → ヘッダー末尾の「:UTF-8」などの指定 → UTF-8として正しく読めるか →
    /// それ以外はShift_JIS、の順で決める。
    /// </summary>
    internal static class BveText
    {
        // 例: "BveTs Vehicle Sound 3.00", "BveTs Scenario 2.00:UTF-8", "KTY Sound List 1.00 : shift_jis"
        // csvの表は "BveTs Vehicle Performance Table 1.00,,,,," のように後ろに「,」が続くことがある
        private static readonly Regex HeaderRegex = new Regex(
            @"^\s*(?<name>[A-Za-z][A-Za-z0-9 _\-]*?)\s+(?<ver>\d+\.\d+)\s*(:\s*(?<enc>[A-Za-z0-9_\-]+))?[\s,]*$",
            RegexOptions.Compiled);

        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public static BveTextFile Read(string path, int maxBytes = 4 * 1024 * 1024)
        {
            byte[] bytes;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                int len = (int)Math.Min(fs.Length, maxBytes);
                bytes = new byte[len];
                int read = 0;
                while (read < len)
                {
                    int n = fs.Read(bytes, read, len - read);
                    if (n <= 0) break;
                    read += n;
                }
                // 途中までしか読まなかったときは、末尾で切れたUTF-8の1文字を捨てる
                // (そのままだとUTF-8として読めずShift_JISと誤判定してしまう)
                if (len < fs.Length) bytes = TrimPartialUtf8(bytes);
            }
            return Decode(bytes);
        }

        private static byte[] TrimPartialUtf8(byte[] bytes)
        {
            int len = bytes.Length;
            int i = len - 1;
            // 末尾の継続バイト(10xxxxxx)をさかのぼって、文字の先頭バイトを探す
            while (i >= 0 && len - i <= 4 && (bytes[i] & 0xC0) == 0x80) i--;
            if (i < 0 || bytes[i] < 0xC0) return bytes; // ASCIIで終わっている/UTF-8ではない
            int need = bytes[i] >= 0xF0 ? 4 : bytes[i] >= 0xE0 ? 3 : 2;
            if (i + need <= len) return bytes; // 最後の文字は欠けていない
            Array.Resize(ref bytes, i);
            return bytes;
        }

        public static BveTextFile Decode(byte[] bytes)
        {
            Encoding enc = null;
            string encName = null;
            int offset = 0;

            // 1. BOM
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                enc = Encoding.UTF8; encName = "UTF-8 (BOM)"; offset = 3;
            }
            else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                enc = Encoding.Unicode; encName = "UTF-16LE (BOM)"; offset = 2;
            }
            else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                enc = Encoding.BigEndianUnicode; encName = "UTF-16BE (BOM)"; offset = 2;
            }

            // 2. ヘッダーの文字コード指定(1行目はASCIIのはずなのでLatin1で仮読みして調べる)
            string firstLineRaw = ReadFirstLineAscii(bytes, offset);
            var hm = HeaderRegex.Match(firstLineRaw);
            if (enc == null && hm.Success && hm.Groups["enc"].Success)
            {
                try
                {
                    enc = Encoding.GetEncoding(hm.Groups["enc"].Value);
                    encName = hm.Groups["enc"].Value + " (ヘッダー指定)";
                }
                catch (ArgumentException)
                {
                    // 不明な文字コード名は無視して自動判定に回す
                }
            }

            string text;
            if (enc != null)
            {
                text = enc.GetString(bytes, offset, bytes.Length - offset);
            }
            else
            {
                // 3. UTF-8として正しく読めればUTF-8、ダメならShift_JIS
                try
                {
                    text = StrictUtf8.GetString(bytes, offset, bytes.Length - offset);
                    encName = IsAscii(bytes) ? "ASCII" : "UTF-8 (自動判定)";
                }
                catch (DecoderFallbackException)
                {
                    text = Encoding.GetEncoding(932).GetString(bytes, offset, bytes.Length - offset);
                    encName = "Shift_JIS (自動判定)";
                }
            }

            var result = new BveTextFile
            {
                Lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'),
                EncodingName = encName,
            };
            if (result.Lines.Length > 0 && result.Lines[0].Length > 0 && result.Lines[0][0] == '﻿')
                result.Lines[0] = result.Lines[0].Substring(1);

            if (hm.Success)
            {
                result.FirstLineIsHeader = true;
                result.HeaderType = hm.Groups["name"].Value.Trim();
                result.HeaderVersion = hm.Groups["ver"].Value;
                // "BveTs Vehicle Sound" → "Vehicle Sound"。
                // BveTs以外のヘッダー(プラグイン独自形式など)はそのまま残す。
                if (result.HeaderType.StartsWith("BveTs ", StringComparison.OrdinalIgnoreCase))
                {
                    result.HeaderType = result.HeaderType.Substring(6).Trim();
                    result.IsBveTs = true;
                }
            }
            return result;
        }

        /// <summary>
        /// 先頭だけ読んでヘッダーを調べる(種類判定用、ファイル全体は読まない)。
        /// </summary>
        public static BveTextFile ReadHeaderOnly(string path, int maxBytes = 512)
        {
            try
            {
                return Read(path, maxBytes);
            }
            catch (Exception)
            {
                // 読めないファイル(使用中・パスが長すぎる等)は「ヘッダー無し」として扱う
                return null;
            }
        }

        private static string ReadFirstLineAscii(byte[] bytes, int offset)
        {
            var sb = new StringBuilder();
            for (int i = offset; i < bytes.Length && i < offset + 256; i++)
            {
                byte b = bytes[i];
                if (b == '\r' || b == '\n') break;
                sb.Append(b < 0x80 ? (char)b : '?');
            }
            return sb.ToString();
        }

        private static bool IsAscii(byte[] bytes)
        {
            foreach (var b in bytes) if (b >= 0x80) return false;
            return true;
        }
    }
}
