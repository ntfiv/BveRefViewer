using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace BveRefViewer
{
    /// <summary>
    /// 音声の試聴(Windows標準のMCIを使う。wav/mp3は再生でき、oggはコーデックが入っていれば再生できる)。
    /// </summary>
    internal static class AudioPreview
    {
        private const string Alias = "bverefviewer_snd";
        private static bool opened;

        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        private static extern int mciSendString(string command, StringBuilder returnValue, int returnLength, IntPtr hwndCallback);

        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        private static extern bool mciGetErrorString(int errorCode, StringBuilder text, int length);

        /// <summary>再生する。失敗したらエラーメッセージ、成功したらnullを返す。</summary>
        public static string Play(string path)
        {
            Stop();
            int r = mciSendString($"open \"{path}\" type mpegvideo alias {Alias}", null, 0, IntPtr.Zero);
            if (r != 0) return ErrorText(r);
            opened = true;
            r = mciSendString($"play {Alias} from 0", null, 0, IntPtr.Zero);
            if (r != 0) { string e = ErrorText(r); Stop(); return e; }
            return null;
        }

        public static void Stop()
        {
            if (!opened) return;
            mciSendString($"stop {Alias}", null, 0, IntPtr.Zero);
            mciSendString($"close {Alias}", null, 0, IntPtr.Zero);
            opened = false;
        }

        private static string ErrorText(int code)
        {
            var sb = new StringBuilder(256);
            return mciGetErrorString(code, sb, sb.Capacity) ? sb.ToString() : "エラー " + code;
        }

        /// <summary>RIFFのチャンク名(4バイト)を読む。文字としてではなくバイト数で読むので、壊れたファイルでも位置がずれない。</summary>
        private static string ReadId(BinaryReader br) => Encoding.ASCII.GetString(br.ReadBytes(4));

        /// <summary>WAVファイルの形式(チャンネル数・サンプリング周波数・長さ等)を調べる。</summary>
        public static string DescribeWav(string path)
        {
            try
            {
                using (var br = new BinaryReader(File.OpenRead(path)))
                {
                    if (ReadId(br) != "RIFF") return "RIFF形式ではありません";
                    br.ReadInt32();
                    if (ReadId(br) != "WAVE") return "WAVE形式ではありません";
                    int format = 0, channels = 0, rate = 0, bits = 0;
                    long dataSize = -1;
                    while (br.BaseStream.Position + 8 <= br.BaseStream.Length)
                    {
                        string id = ReadId(br);
                        int size = br.ReadInt32();
                        long next = br.BaseStream.Position + size + (size & 1);
                        if (id == "fmt ")
                        {
                            format = br.ReadInt16();
                            channels = br.ReadInt16();
                            rate = br.ReadInt32();
                            br.ReadInt32();
                            br.ReadInt16();
                            bits = br.ReadInt16();
                        }
                        else if (id == "data")
                        {
                            dataSize = size;
                        }
                        if (next <= br.BaseStream.Position) break;
                        br.BaseStream.Position = next;
                    }
                    string fmt = format == 1 ? "PCM" : format == 3 ? "IEEE float" : format == -2 ? "拡張形式" : "形式コード " + format;
                    string s = $"{fmt} / {channels}ch / {rate} Hz / {bits} bit";
                    if (dataSize > 0 && rate > 0 && channels > 0 && bits > 0)
                        s += $" / 長さ {dataSize / (double)(rate * channels * bits / 8):0.00} 秒";
                    return s;
                }
            }
            catch (Exception ex)
            {
                return "WAVの解析に失敗: " + ex.Message;
            }
        }
    }
}
