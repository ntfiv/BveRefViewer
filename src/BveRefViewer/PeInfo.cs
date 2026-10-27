using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BveRefViewer
{
    /// <summary>
    /// DLLのPEヘッダーを読んで、CPU種別とエクスポート関数の一覧を調べる。
    /// ATSプラグインがAts32/Ats64に正しいビット数で指定されているか、
    /// BVEが呼ぶ関数(Elapse等)がネイティブエクスポートされているか
    /// (DllExportの処理に失敗していないか)の確認に使う。
    /// </summary>
    internal sealed class PeInfo
    {
        /// <summary>"x86" / "x64" / "ARM64" / "AnyCPU (.NET)" など。</summary>
        public string Arch;
        public bool IsManaged;
        public readonly List<string> Exports = new List<string>();
        public string Error;

        /// <summary>BVEのATSプラグインがエクスポートしているべき関数。</summary>
        public static readonly string[] AtsFunctions =
        {
            "Load", "Dispose", "GetPluginVersion", "SetVehicleSpec", "Initialize", "Elapse",
            "SetPower", "SetBrake", "SetReverser", "KeyDown", "KeyUp", "HornBlow",
            "DoorOpen", "DoorClose", "SetSignal", "SetBeaconData",
        };

        public static PeInfo Read(string path)
        {
            var info = new PeInfo();
            try
            {
                byte[] b = File.ReadAllBytes(path);
                if (b.Length < 0x40 || b[0] != 'M' || b[1] != 'Z') { info.Error = "PEファイルではありません"; return info; }
                int pe = BitConverter.ToInt32(b, 0x3C);
                if (pe < 0 || pe + 24 > b.Length || b[pe] != 'P' || b[pe + 1] != 'E' || b[pe + 2] != 0 || b[pe + 3] != 0)
                {
                    info.Error = "PEヘッダーが見つかりません"; return info;
                }
                ushort machine = BitConverter.ToUInt16(b, pe + 4);
                int sectionCount = BitConverter.ToUInt16(b, pe + 6);
                int optSize = BitConverter.ToUInt16(b, pe + 20);
                int opt = pe + 24;
                ushort magic = BitConverter.ToUInt16(b, opt);
                int dataDir = magic == 0x20b ? opt + 112 : opt + 96;

                switch (machine)
                {
                    case 0x014c: info.Arch = "x86"; break;
                    case 0x8664: info.Arch = "x64"; break;
                    case 0xAA64: info.Arch = "ARM64"; break;
                    default: info.Arch = "不明 (0x" + machine.ToString("X4") + ")"; break;
                }

                // セクション表(RVA → ファイル上の位置の変換に使う)
                int sec = opt + optSize;
                var sections = new List<(uint va, uint vsize, uint raw, uint rawSize)>();
                for (int i = 0; i < sectionCount; i++)
                {
                    int s = sec + i * 40;
                    if (s + 40 > b.Length) break;
                    sections.Add((BitConverter.ToUInt32(b, s + 12), BitConverter.ToUInt32(b, s + 8),
                                  BitConverter.ToUInt32(b, s + 20), BitConverter.ToUInt32(b, s + 16)));
                }
                int RvaToOffset(uint rva)
                {
                    foreach (var x in sections)
                    {
                        uint size = Math.Max(x.vsize, x.rawSize);
                        if (rva >= x.va && rva < x.va + size)
                            return (int)(rva - x.va + x.raw);
                    }
                    return -1;
                }

                // .NETアセンブリかどうか(データディレクトリ14番 = CLRヘッダー)
                uint clrRva = BitConverter.ToUInt32(b, dataDir + 14 * 8);
                if (clrRva != 0)
                {
                    info.IsManaged = true;
                    int clr = RvaToOffset(clrRva);
                    if (clr >= 0 && clr + 20 <= b.Length)
                    {
                        uint flags = BitConverter.ToUInt32(b, clr + 16);
                        bool ilOnly = (flags & 0x1) != 0;
                        bool req32 = (flags & 0x2) != 0;
                        if (machine == 0x014c && ilOnly && !req32) info.Arch = "AnyCPU (.NET)";
                    }
                }

                // エクスポート表(データディレクトリ0番)
                uint expRva = BitConverter.ToUInt32(b, dataDir);
                if (expRva != 0)
                {
                    int exp = RvaToOffset(expRva);
                    if (exp >= 0 && exp + 40 <= b.Length)
                    {
                        int nameCount = BitConverter.ToInt32(b, exp + 24);
                        int names = RvaToOffset(BitConverter.ToUInt32(b, exp + 32));
                        for (int i = 0; i < nameCount && names >= 0 && names + i * 4 + 4 <= b.Length; i++)
                        {
                            int n = RvaToOffset(BitConverter.ToUInt32(b, names + i * 4));
                            if (n < 0) continue;
                            int end = n;
                            while (end < b.Length && b[end] != 0) end++;
                            info.Exports.Add(Encoding.ASCII.GetString(b, n, end - n));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                info.Error = ex.Message;
            }
            return info;
        }
    }
}
