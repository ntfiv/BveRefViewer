using System.Collections.Generic;

namespace BveRefViewer
{
    /// <summary>参照されているファイルの種類。</summary>
    internal enum FileKind
    {
        Unknown,
        Scenario,
        Route,
        /// <summary>路線から Structure.Load で読まれるストラクチャーリスト。</summary>
        StructureList,
        /// <summary>路線から Signal.Load で読まれる信号現示リスト。</summary>
        SignalList,
        /// <summary>路線から Sound.Load で読まれるサウンドリスト。</summary>
        SoundList,
        /// <summary>路線から Sound3D.Load で読まれる3Dサウンドリスト。</summary>
        Sound3DList,
        /// <summary>路線から Station.Load で読まれる停車場リスト。</summary>
        StationList,
        /// <summary>路線から Train.Add / Train[].Load で読まれる他列車ファイル。</summary>
        OtherTrain,
        Vehicle,
        PerformanceCurve,
        /// <summary>性能曲線から参照される表(ヘッダー「BveTs Vehicle Performance Table」)。</summary>
        PerformanceTable,
        Parameters,
        Panel,
        Sound,
        MotorNoise,
        /// <summary>モーター音ファイルから参照される表(ヘッダー「BveTs Motor Noise Table」)。</summary>
        MotorNoiseTable,
        /// <summary>ATSプラグインのDLL(Ats/Ats32/Ats64、detailmodules.txtなどで指定されたもの)。</summary>
        AtsPlugin,
        /// <summary>DetailManagerのdetailmodules.txtなど、プラグインDLLを列挙したファイル。</summary>
        PluginList,
        /// <summary>プラグインDLLと同じフォルダにある設定ファイル(ini等)とそこから参照されるテキスト。</summary>
        PluginConfig,
        /// <summary>ATSプラグイン以外のDLL。</summary>
        Library,
        /// <summary>AtsEXのプラグイン一覧(VehiclePluginUsing.xml)。</summary>
        AtsExPluginList,
        /// <summary>AtsEXのプラグイン(VehiclePluginUsing.xmlに書かれた.NETのDLL)。</summary>
        AtsExPlugin,
        Image,
        Audio,
        Csv,
        Text,
        Model,
        /// <summary>プラグイン設定でフォルダが指定されている場合(中のファイルを子に付ける)。</summary>
        Folder,
        Other,
    }

    /// <summary>
    /// 参照ツリーの1ノード(1つの参照 = 参照元のファイルの1か所の記述)。
    /// </summary>
    internal sealed class RefNode
    {
        /// <summary>解決後の絶対パス。解決できなかった(見つからない)場合も、最有力候補のパスが入る。</summary>
        public string FullPath;
        /// <summary>参照元に書かれていた値そのまま(例: "Panel\Panel_3793F.txt")。</summary>
        public string RawValue;
        /// <summary>参照元でのキー名(例: "Panel")。キーの無い行(detailmodules.txt等)ではnull。</summary>
        public string Key;
        /// <summary>参照元でのセクション名(例: "Run")。</summary>
        public string Section;
        /// <summary>参照元での行番号(1始まり)。ルートは0。</summary>
        public int Line;

        public FileKind Kind;
        public bool Exists;
        /// <summary>明示的な参照ではなく、「プラグインDLLと同じフォルダにある設定ファイル」として推定したもの。</summary>
        public bool Inferred;
        /// <summary>同じファイルが既に別の場所で展開済みなので、ここでは子を展開していない。</summary>
        public bool Duplicate;
        /// <summary>意図的に中身を解析しないファイル(路線ファイルなど)。</summary>
        public bool NotExpanded;
        /// <summary>ATSプラグインのとき、期待されるCPU("x86"/"x64")。不明ならnull。</summary>
        public string ExpectedArch;
        /// <summary>車両フォルダの外(BveDcTrainPack-masterやGeneralAtsPluginなど)のファイル。</summary>
        public bool External;
        /// <summary>車両ノードのとき、その車両のフォルダとみなしたフォルダ。</summary>
        public string VehicleRoot;
        /// <summary>最上位の路線ノードのとき、その路線のフォルダとみなしたフォルダ。</summary>
        public string RouteRoot;
        /// <summary>車両ノードのとき、AtsEXを使っているか(AtsEXのプラグイン一覧かAtsEx.Callerが配下にある)。</summary>
        public bool UsesAtsEx;

        /// <summary>DLLの実際のCPU種別(PeInfo.Arch。"x86"/"x64"/"AnyCPU (.NET)"など)。DLL以外はnull。</summary>
        public string Arch;

        /// <summary>ツリーに添える補足("PluginOrganizerの設定"など)。</summary>
        public string Note;

        public string EncodingName;
        public string HeaderType;
        public string HeaderVersion;

        public readonly List<string> Warnings = new List<string>();
        public RefNode Parent;
        public readonly List<RefNode> Children = new List<RefNode>();

        public bool HasProblem => !Exists || Warnings.Count > 0;

        public RefNode AddChild(RefNode child)
        {
            child.Parent = this;
            Children.Add(child);
            return child;
        }

        public IEnumerable<RefNode> DescendantsAndSelf()
        {
            yield return this;
            foreach (var c in Children)
                foreach (var d in c.DescendantsAndSelf())
                    yield return d;
        }

        /// <summary>このノードより上にあるATSプラグインのノード(無ければnull)。</summary>
        public RefNode NearestPluginAncestor()
        {
            for (var p = Parent; p != null; p = p.Parent)
                if (p.Kind == FileKind.AtsPlugin) return p;
            return null;
        }
    }

    internal static class FileKindText
    {
        public static string Label(FileKind kind)
        {
            switch (kind)
            {
                case FileKind.Scenario: return "シナリオ";
                case FileKind.Route: return "路線(マップ)";
                case FileKind.StructureList: return "ストラクチャーリスト";
                case FileKind.SignalList: return "信号現示リスト";
                case FileKind.SoundList: return "サウンドリスト";
                case FileKind.Sound3DList: return "3Dサウンドリスト";
                case FileKind.StationList: return "停車場リスト";
                case FileKind.OtherTrain: return "他列車ファイル";
                case FileKind.Vehicle: return "車両";
                case FileKind.PerformanceCurve: return "性能曲線";
                case FileKind.PerformanceTable: return "性能曲線";
                case FileKind.Parameters: return "車両性能ファイル";
                case FileKind.Panel: return "運転台パネルファイル";
                case FileKind.Sound: return "車両サウンドファイル";
                case FileKind.MotorNoise: return "モーター音";
                case FileKind.MotorNoiseTable: return "モータ音テーブル";
                case FileKind.AtsPlugin: return "ATSプラグイン";
                case FileKind.PluginList: return "プラグイン一覧";
                case FileKind.PluginConfig: return "プラグイン設定";
                case FileKind.Library: return "DLL";
                case FileKind.AtsExPluginList: return "AtsEXプラグイン一覧";
                case FileKind.AtsExPlugin: return "AtsEXプラグイン";
                case FileKind.Image: return "画像";
                case FileKind.Audio: return "音声";
                case FileKind.Csv: return "CSV";
                case FileKind.Text: return "テキスト";
                case FileKind.Model: return "3Dモデル";
                case FileKind.Folder: return "フォルダ";
                case FileKind.Other: return "その他";
                default: return "不明";
            }
        }

        /// <summary>
        /// ノードごとの種類名。表はファイル名(わからなければ参照元のキー名)で中身を見分け、
        /// DLLはCPU種別を付ける。
        ///   p-force.csv  → 性能曲線(引張力テーブル)
        ///   PowerVol.csv → モータ音テーブル(ボリューム)
        ///   EB.dll       → ATSプラグイン(x86)
        /// </summary>
        public static string Label(RefNode n)
        {
            switch (n.Kind)
            {
                case FileKind.PerformanceTable:
                {
                    string sub = TableSubtype(n,
                        ("force", "引張力テーブル"), ("current", "電流テーブル"),
                        ("param", "進段パラメーター"), ("prmtr", "進段パラメーター"));
                    return sub == null ? "性能曲線" : "性能曲線（" + sub + "）";
                }
                case FileKind.MotorNoiseTable:
                {
                    string sub = TableSubtype(n, ("vol", "ボリューム"), ("freq", "周波数"));
                    return sub == null ? "モータ音テーブル" : "モータ音テーブル（" + sub + "）";
                }
                case FileKind.AtsPlugin:
                case FileKind.Library:
                case FileKind.AtsExPlugin:
                    return n.Arch == null ? Label(n.Kind) : Label(n.Kind) + "（" + ShortArch(n.Arch) + "）";
                default:
                    return Label(n.Kind);
            }
        }

        /// <summary>"AnyCPU (.NET)" → "AnyCPU" のように短くした表記。</summary>
        public static string ShortArch(string arch)
        {
            if (arch == null) return null;
            int p = arch.IndexOf(" (", System.StringComparison.Ordinal);
            return p > 0 ? arch.Substring(0, p) : arch;
        }

        /// <summary>まずファイル名、決まらなければ参照元のキー名(「Force =」など)で表の中身を判定する。</summary>
        private static string TableSubtype(RefNode n, params (string word, string label)[] rules)
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(n.FullPath ?? "").ToLowerInvariant();
            foreach (var r in rules)
                if (name.Contains(r.word)) return r.label;
            string key = n.Key?.ToLowerInvariant();
            if (key != null)
                foreach (var r in rules)
                    if (key.Contains(r.word)) return r.label;
            return null;
        }

        /// <summary>ツリーで種類ラベルを前に付けて目立たせる種類(中身を持つファイル)。</summary>
        public static bool IsContainer(FileKind kind)
        {
            switch (kind)
            {
                case FileKind.PerformanceTable:
                case FileKind.MotorNoiseTable:
                case FileKind.Library:
                case FileKind.StructureList:
                case FileKind.SignalList:
                case FileKind.SoundList:
                case FileKind.Sound3DList:
                case FileKind.StationList:
                case FileKind.OtherTrain:
                case FileKind.AtsExPluginList:
                case FileKind.AtsExPlugin:
                case FileKind.Scenario:
                case FileKind.Route:
                case FileKind.Vehicle:
                case FileKind.PerformanceCurve:
                case FileKind.Parameters:
                case FileKind.Panel:
                case FileKind.Sound:
                case FileKind.MotorNoise:
                case FileKind.AtsPlugin:
                case FileKind.PluginList:
                case FileKind.PluginConfig:
                case FileKind.Folder:
                    return true;
                default:
                    return false;
            }
        }
    }
}
