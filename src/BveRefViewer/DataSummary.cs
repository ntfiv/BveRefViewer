using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace BveRefViewer
{
    /// <summary>
    /// 読み込んだデータの概要: どのBVEで動くか(推定)、AtsEXを使うか、どの外部ライブラリ・共有データが必要か。
    /// 参照ツリー(RefNode)から計算する。
    /// </summary>
    internal sealed class DataSummary
    {
        public readonly List<VehicleCompat> Vehicles = new List<VehicleCompat>();
        public readonly List<RefNode> AtsExVehiclePlugins = new List<RefNode>();
        public readonly List<RefNode> AtsExMapPlugins = new List<RefNode>();
        public readonly List<LibraryUse> Libraries = new List<LibraryUse>();

        public bool UsesAtsEx => AtsExVehiclePlugins.Count > 0 || AtsExMapPlugins.Count > 0
                                 || Vehicles.Any(v => v.Vehicle.UsesAtsEx);

        // ------------------------------------------------------------------
        // 動くBVEの推定
        // ------------------------------------------------------------------

        /// <summary>1つの車両について、BVE5・BVE6で動くかの推定。</summary>
        internal sealed class VehicleCompat
        {
            public RefNode Vehicle;
            /// <summary>車両ファイルのヘッダーのバージョン("2.00"など)。</summary>
            public string Version;
            /// <summary>2.00以降の新しい形式(Ats32/Ats64でプラグインを指定する形式)か。</summary>
            public bool NewFormat;
            /// <summary>BVE5(32bit)・BVE6(64bit)で動くか。</summary>
            public Verdict Bve5, Bve6;

            /// <summary>「両対応」「BVE5専用」「BVE6専用」「どちらも×」。</summary>
            public string Label => Bve5.Ok && Bve6.Ok ? "両対応" : Bve5.Ok ? "BVE5専用" : Bve6.Ok ? "BVE6専用" : "どちらも×";
        }

        internal sealed class Verdict
        {
            public bool Ok;
            public string Reason;
            public Verdict(bool ok, string reason) { Ok = ok; Reason = reason; }
            public string Mark => Ok ? "○" : "×";
        }

        /// <summary>
        /// 判定のルール:
        ///  - BVE5 は32bitのアプリなので x86 の ATSプラグイン(Ats か Ats32)、BVE6 は64bitのアプリなので
        ///    x64 の ATSプラグイン(Ats64)を使う。x86 だけなら BVE5専用、x64 だけなら BVE6専用、両方あれば両対応。
        ///  - 指定されたプラグインが見つからない・ビット数が違うときは、その側は ×。
        ///  - ATSプラグインの指定が無い車両は、車両ファイルの形式で決める。2.00 以降の新しい形式なら両対応、
        ///    古い形式(1.00 以前)は BVE5専用(BVE6 は「BVE5 以前のシナリオはプレイできない」と公式に書かれているため)。
        /// </summary>
        private static VehicleCompat Judge(RefNode v)
        {
            var c = new VehicleCompat { Vehicle = v, Version = v.HeaderVersion };
            c.NewFormat = double.TryParse(v.HeaderVersion, NumberStyles.Float, CultureInfo.InvariantCulture, out double ver) && ver >= 2.0;

            RefNode Plugin(string key) => v.Children.FirstOrDefault(
                ch => ch.Kind == FileKind.AtsPlugin && string.Equals(ch.Key?.Trim(), key, StringComparison.OrdinalIgnoreCase));
            // 32bit用: 新しい形式は Ats32、古い形式は Ats(どちらかしか無いはずだが、念のため両方見る)
            var ats32 = c.NewFormat ? Plugin("Ats32") ?? Plugin("Ats") : Plugin("Ats") ?? Plugin("Ats32");
            var ats64 = Plugin("Ats64");

            // 指定されたプラグインが、そのビット数のDLLとして実在するか
            Verdict Check(RefNode p, string arch)
            {
                string label = p.Key?.Trim() ?? "Ats";
                if (!p.Exists) return new Verdict(false, $"{label} のファイルが見つからない");
                if (p.Arch != arch) return new Verdict(false, $"{label} が {FileKindText.ShortArch(p.Arch) ?? "不明"} のDLL({arch} が必要)");
                return new Verdict(true, $"{label} に {arch} のプラグイン");
            }

            var oldFormat = new Verdict(false, $"車両ファイルが古い形式({c.Version ?? "?"})。BVE6 は BVE5 以前のシナリオを読めない");
            if (ats32 == null && ats64 == null)
            {
                c.Bve5 = new Verdict(true, "ATSプラグインなし");
                c.Bve6 = c.NewFormat ? new Verdict(true, "ATSプラグインなし") : oldFormat;
                return c;
            }
            c.Bve5 = ats32 != null ? Check(ats32, "x86") : new Verdict(false, "32bit(x86)のプラグインの指定なし");
            c.Bve6 = ats64 != null ? Check(ats64, "x64")
                   : !c.NewFormat ? oldFormat
                   : new Verdict(false, "64bit(x64)のプラグイン(Ats64)の指定なし");
            return c;
        }

        // ------------------------------------------------------------------
        // 外部ライブラリ・共有データ
        // ------------------------------------------------------------------

        /// <summary>名前の分かっている外部ライブラリ(パスの一部で見分ける)。</summary>
        internal sealed class LibraryDef
        {
            public string Name;
            /// <summary>「Rock_On\Train\JR」のようなパスの一部。末尾の * は前方一致(BveDcTrainPack* → BveDcTrainPack-master)。</summary>
            public string Pattern;
        }

        /// <summary>外部ライブラリ・共有データ1つ分の使われ方。</summary>
        internal sealed class LibraryUse
        {
            public string Name;
            /// <summary>実際に見つかったフォルダ(例: G:\BVE\_Common\BveDcTrainPack-master)。全部見つからないときは最初の参照から推定。</summary>
            public string Folder;
            /// <summary>名前の分かっている外部ライブラリか(false なら、フォルダ名で分けた「その他の外部フォルダ」)。</summary>
            public bool Known;
            public int References;
            public int Missing;
            /// <summary>ツリーで選ぶときの代表(見つからない参照があればそれを優先)。</summary>
            public RefNode First;

            public string Status => Missing == 0 ? "✓ あり" : Missing == References ? "✗ 見つからない" : "△ 一部見つからない";
        }

        /// <summary>組み込みの外部ライブラリ。libraries.txt で追加・上書きできる。</summary>
        internal static readonly LibraryDef[] BuiltInLibraries =
        {
            new LibraryDef { Name = "JR線車両パック", Pattern = @"Rock_On\Train\JR" },
            new LibraryDef { Name = "気動車車両パック", Pattern = @"BveDcTrainPack*" },
            new LibraryDef { Name = "GeneralAtsPlugin", Pattern = @"GeneralAtsPlugin" },
            new LibraryDef { Name = "Nagoya_Common", Pattern = @"Nagoya_Common" },
        };

        /// <summary>
        /// exe と同じフォルダの libraries.txt(「表示名 = パスの一部」を1行ずつ。# 以降はコメント)を読み、
        /// 組み込みの定義に足す。同じパスの一部があれば表示名を上書きする。
        /// </summary>
        internal static List<LibraryDef> LoadLibraryDefs()
        {
            var defs = BuiltInLibraries.Select(d => new LibraryDef { Name = d.Name, Pattern = d.Pattern }).ToList();
            try
            {
                string file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "libraries.txt");
                if (!File.Exists(file)) return defs;
                foreach (var raw in BveText.Read(file).Lines)
                {
                    string line = raw;
                    int hash = line.IndexOf('#');
                    if (hash >= 0) line = line.Substring(0, hash);
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string name = line.Substring(0, eq).Trim();
                    string pattern = line.Substring(eq + 1).Trim().Replace('/', '\\').Trim('\\');
                    if (name.Length == 0 || pattern.Length == 0) continue;
                    var same = defs.FirstOrDefault(d => d.Pattern.Equals(pattern, StringComparison.OrdinalIgnoreCase));
                    if (same != null) same.Name = name;
                    else defs.Insert(0, new LibraryDef { Name = name, Pattern = pattern }); // ユーザーの定義を優先
                }
            }
            catch (Exception) { }
            return defs;
        }

        /// <summary>パスの中に、フォルダの区切りに沿ってパターンが含まれるか。含まれればその終わりまでのフォルダを返す。</summary>
        private static string MatchLibrary(string path, LibraryDef def)
        {
            string[] segs = path.Split('\\');
            string[] pat = def.Pattern.Split('\\');
            for (int i = 0; i + pat.Length <= segs.Length - 1; i++) // 最後の要素はファイル名なので除く
            {
                bool ok = true;
                for (int j = 0; j < pat.Length && ok; j++)
                {
                    string p = pat[j];
                    ok = p.EndsWith("*")
                        ? segs[i + j].StartsWith(p.TrimEnd('*'), StringComparison.OrdinalIgnoreCase)
                        : segs[i + j].Equals(p, StringComparison.OrdinalIgnoreCase);
                }
                if (ok) return string.Join("\\", segs, 0, i + pat.Length);
            }
            return null;
        }

        // ------------------------------------------------------------------
        // 組み立て
        // ------------------------------------------------------------------

        public static DataSummary Build(RefNode root, List<LibraryDef> defs)
        {
            var s = new DataSummary();
            var uses = new Dictionary<string, LibraryUse>(StringComparer.OrdinalIgnoreCase);

            void Walk(RefNode n, string packageRoot)
            {
                if (n.Kind == FileKind.Vehicle && n.Exists && n.VehicleRoot != null && !n.Duplicate) // 同じ車両の2回目以降は除く
                {
                    s.Vehicles.Add(Judge(n));
                    packageRoot = n.VehicleRoot;
                }
                if (n.RouteRoot != null) packageRoot = n.RouteRoot;

                if (n.Kind == FileKind.AtsExPlugin)
                {
                    if (IsUnderMapPluginList(n)) s.AtsExMapPlugins.Add(n); else s.AtsExVehiclePlugins.Add(n);
                }

                // 外部の参照を、名前の分かっているライブラリか、フォルダ名ごとのまとまりに振り分ける
                if (n.External && n.Kind != FileKind.Folder)
                {
                    string name = null, folder = null;
                    bool known = false;
                    foreach (var d in defs)
                    {
                        folder = MatchLibrary(n.FullPath, d);
                        if (folder != null) { name = d.Name; known = true; break; }
                    }
                    if (name == null && packageRoot != null)
                    {
                        folder = FirstFolderOutside(n.FullPath, packageRoot);
                        name = folder != null ? Path.GetFileName(folder) : null;
                    }
                    if (name != null)
                    {
                        if (!uses.TryGetValue(name, out var u))
                            uses[name] = u = new LibraryUse { Name = name, Folder = folder, Known = known };
                        u.References++;
                        if (!n.Exists) u.Missing++;
                        if (u.First == null || (!n.Exists && u.First.Exists)) u.First = n;
                    }
                }
                foreach (var c in n.Children) Walk(c, packageRoot);
            }
            Walk(root, null);

            s.Libraries.AddRange(uses.Values
                .OrderByDescending(u => u.Known)
                .ThenByDescending(u => u.References));
            return s;
        }

        private static bool IsUnderMapPluginList(RefNode n)
        {
            for (var p = n.Parent; p != null; p = p.Parent)
                if (p.Kind == FileKind.Route) return true;
            return false;
        }

        /// <summary>
        /// パッケージのフォルダの外にあるファイルについて、両者の共通の親フォルダの1つ下のフォルダを返す
        /// (例: 車両 G:\…\train\JRS_DC54N から G:\…\Scenarios\GeneralAtsPlugin\Rock_On\EB.dll を参照
        /// → 共通の親は Scenarios、返すのは Scenarios\GeneralAtsPlugin)。
        /// </summary>
        private static string FirstFolderOutside(string path, string packageRoot)
        {
            string[] a = path.Split('\\');
            string[] b = packageRoot.TrimEnd('\\').Split('\\');
            int i = 0;
            while (i < a.Length - 1 && i < b.Length && a[i].Equals(b[i], StringComparison.OrdinalIgnoreCase)) i++;
            if (i == 0 || i >= a.Length - 1) return null; // ドライブが違う / ファイルが共通の親の直下にある
            return string.Join("\\", a, 0, i + 1);
        }

        // ------------------------------------------------------------------
        // 表示用の文字列
        // ------------------------------------------------------------------

        /// <summary>ツリーの上に出す1行の要約。</summary>
        public string OneLine()
        {
            var parts = new List<string>();
            if (Vehicles.Count > 0)
            {
                // 例: 「両対応」、車両ごとに違えば「両対応 2 / BVE5専用 1」
                var labels = Vehicles.GroupBy(v => v.Label).ToList();
                parts.Add(labels.Count == 1 ? labels[0].Key
                    : string.Join(" / ", labels.Select(g => $"{g.Key} {g.Count()}")));
            }
            parts.Add(UsesAtsEx ? "AtsEX 使用" : "AtsEX なし");
            var known = Libraries.Where(l => l.Known).ToList();
            parts.Add(known.Count == 0 ? "外部ライブラリなし"
                : "外部: " + string.Join("、", known.Select(l => l.Name + (l.Missing == 0 ? "" : l.Missing == l.References ? "(見つからない)" : "(一部不足)"))));
            return string.Join("   |   ", parts);
        }

        /// <summary>概要タブ・--dump に出す説明文。</summary>
        public string Report()
        {
            var sb = new StringBuilder();
            sb.AppendLine("■ 対応するBVE(推定)");
            if (Vehicles.Count == 0) sb.AppendLine("  車両がありません");
            foreach (var v in Vehicles)
            {
                sb.AppendLine($"  {Path.GetFileName(v.Vehicle.FullPath)}   → {v.Label}   (車両ファイル {v.Version ?? "?"}・{(v.NewFormat ? "新しい形式" : "古い形式")})");
                sb.AppendLine($"    BVE5 {v.Bve5.Mark}  {v.Bve5.Reason}" + (v.Bve5.Ok && v.NewFormat ? "(新しい形式のため BVE 5.8 が必要)" : ""));
                sb.AppendLine($"    BVE6 {v.Bve6.Mark}  {v.Bve6.Reason}");
            }
            sb.AppendLine("  ※ ATSプラグインのDLLのビット数から推定(BVE5 は x86、BVE6 は x64 のプラグインを使う)。");
            sb.AppendLine();

            sb.AppendLine("■ AtsEX");
            if (!UsesAtsEx) sb.AppendLine("  使っていません");
            else
            {
                foreach (var v in Vehicles.Where(v => v.Vehicle.UsesAtsEx))
                    sb.AppendLine($"  車両 {Path.GetFileName(v.Vehicle.FullPath)} が AtsEX を使用");
                foreach (var p in AtsExVehiclePlugins.Select(p => Path.GetFileName(p.FullPath)).Distinct(StringComparer.OrdinalIgnoreCase))
                    sb.AppendLine("    車両プラグイン: " + p);
                if (AtsExMapPlugins.Count > 0)
                {
                    sb.AppendLine("  路線が AtsEX のマッププラグインを使用");
                    foreach (var p in AtsExMapPlugins.Select(p => Path.GetFileName(p.FullPath)).Distinct(StringComparer.OrdinalIgnoreCase))
                        sb.AppendLine("    マッププラグイン: " + p);
                }
            }
            sb.AppendLine();

            sb.AppendLine("■ 外部ライブラリ・共有データ(車両・路線のフォルダの外から読んでいるもの)");
            if (Libraries.Count == 0) sb.AppendLine("  ありません");
            foreach (var l in Libraries)
                sb.AppendLine($"  {l.Status,-10} {(l.Known ? l.Name : "(その他) " + l.Name)}   参照 {l.References} 件"
                              + (l.Missing > 0 ? $" / 見つからない {l.Missing} 件" : "") + $"   {l.Folder}");
            return sb.ToString();
        }
    }
}
