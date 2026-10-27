using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace BveRefViewer
{
    /// <summary>
    /// シナリオ/車両ファイルから、参照されているファイルを再帰的にたどってツリーを作る。
    ///
    /// BVEの車両関係ファイルはどれも「キー = 相対パス」か「1行1パス」の形なので、
    /// 種類ごとの専用パーサーは持たず、次のルールで汎用的に解析する。
    ///  - 「;」「#」「//」以降はコメント。「[...]」はセクション。
    ///  - 値を「|」「,」で区切り、ファイルのように見えるもの(拡張子付き)をパスとみなす。
    ///    車両ファイルやサウンドファイルなど、値が必ずパスになるキーは拡張子が無くてもパスとみなす。
    ///  - 相対パスは参照元ファイルのフォルダ基準(BVEの仕様)。プラグイン設定から参照される
    ///    ファイルは、見つからなければプラグインDLLのフォルダ基準でも探す。
    ///  - ATSプラグインのDLLには、同じフォルダにある設定ファイル(ini等)を「推定」として子に付ける。
    ///    DetailManagerのdetailmodules.txtもここで拾われ、その中のDLLがさらに展開される。
    /// </summary>
    internal sealed class RefScanner
    {
        /// <summary>中身を解析するテキストの拡張子。</summary>
        private static readonly HashSet<string> TextExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".cfg", ".ini", ".csv", ".xml", ".json", ".toml", ".yaml", ".yml", ".conf" };

        /// <summary>プラグインDLLの隣にあるとき設定ファイルとみなす拡張子。</summary>
        private static readonly HashSet<string> PluginConfigExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".cfg", ".ini", ".csv", ".xml", ".json", ".toml", ".yaml", ".yml", ".conf" };

        /// <summary>見つからなかったときに「見つかりません」と警告する拡張子(誤検出を減らすため既知のものだけ)。</summary>
        private static readonly HashSet<string> KnownFileExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".cfg", ".ini", ".csv", ".xml", ".json", ".toml", ".dll",
            ".wav", ".ogg", ".mp3", ".flac", ".wma", ".m4a",
            ".png", ".bmp", ".jpg", ".jpeg", ".gif", ".tga", ".dds", ".tif", ".tiff",
            ".x", ".csv", ".obj", ".fbx",
        };

        // 説明書のたぐいは例として架空のパスが書かれていることが多いので、推定の対象から外す
        private static readonly Regex DocFileRegex = new Regex(
            @"read[ _\-]?me|りーどみー|リードミー|お読み|読んで|必読|license|licence|ライセンス|notice|copying|copyright|著作権|" +
            @"credits?|authors?|third[ _\-]?party|changelog|changes|history|release[ _\-]?notes?|更新履歴|履歴|" +
            @"manual|マニュアル|説明|はじめに|使い方|使用方法|取扱|取り扱い|解説|規約|利用条件|注意",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // DLL名の末尾のビット数表記(KTY_HOSO64、keisei_x64、frontlamp_x86 など)
        private static readonly Regex ArchSuffixRegex = new Regex(
            @"[_\-\.]?(x64|x86|x32|win64|win32|amd64|64bit|32bit|64|32)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ArchOnlyRegex = new Regex(
            @"^(x64|x86|x32|win64|win32|amd64|64bit|32bit|64|32)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // 「Gauge0 = SPD」のように拡張子なしでファイル名だけ書かれた値
        private static readonly Regex BareNameRegex = new Regex(@"^[A-Za-z0-9_\-]{1,64}$", RegexOptions.Compiled);

        // "a.txt"、"Sound\Motor0.wav" のように拡張子(英字を含む1〜5文字)で終わるもの
        private static readonly Regex ExtRegex = new Regex(@"\.[A-Za-z][A-Za-z0-9]{0,4}$", RegexOptions.Compiled);

        private const int MaxDepth = 24;
        private const long MaxScanBytes = 4 * 1024 * 1024;
        /// <summary>マップファイルは大きいことがあるので、上限を別にする。</summary>
        private const long MaxMapBytes = 64 * 1024 * 1024;

        /// <summary>trueなら、ストラクチャー(.x)の中のテクスチャ画像まで確認する(既定はしない。遅くなるため)。</summary>
        public bool CheckTextures;

        /// <summary>
        /// ファイルのヘッダー種類(小文字)のキャッシュ。路線では同じ他列車ファイルを千回以上参照することがあり、
        /// そのたびにファイルを開くと遅いので、1ファイル1回だけ読む。
        /// </summary>
        private readonly Dictionary<string, string> headerCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private FileKind Kind(string fullPath, string parentKey, FileKind parentKind)
            => DetectKind(fullPath, parentKey, parentKind, headerCache);

        /// <summary>中身を展開済みのファイル(重複展開・循環参照を防ぐ)。</summary>
        private readonly Dictionary<string, RefNode> expanded = new Dictionary<string, RefNode>(StringComparer.OrdinalIgnoreCase);

        /// <summary>どこかのファイルに明示的に書かれていた(推定ではない)参照先。</summary>
        private readonly HashSet<string> explicitRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 展開済みならtrue(このノードは子を展開しない)。最初に展開したノードが参照を持っていた
        /// ときだけ「上で展開済み」と表示する(中身に参照の無いcsv等で表示がうるさくならないように)。
        /// </summary>
        private bool AlreadyExpanded(RefNode node)
        {
            if (expanded.TryGetValue(node.FullPath, out var first))
            {
                if (first.Children.Count > 0) node.Duplicate = true;
                return true;
            }
            expanded.Add(node.FullPath, node);
            return false;
        }

        public RefNode ScanRoot(string path)
        {
            string full = Path.GetFullPath(path);
            var root = new RefNode
            {
                FullPath = full,
                RawValue = Path.GetFileName(full),
                Exists = File.Exists(full),
            };
            root.Kind = Kind(full, null, FileKind.Unknown);
            if (root.Kind == FileKind.Unknown || root.Kind == FileKind.Text)
            {
                // ヘッダーで判別できないときは、とりあえず車両ファイル同様に中身をたどる
                root.Kind = FileKind.Text;
            }
            if (!root.Exists) root.Warnings.Add("ファイルが見つかりません");
            Expand(root, 0);
            MarkExternal(root, null);
            return root;
        }

        // ------------------------------------------------------------------
        // 車両フォルダの外のファイル
        // ------------------------------------------------------------------

        /// <summary>
        /// 車両ごとに「車両フォルダ」を決め、その外にある参照先に External を付ける
        /// (BveDcTrainPack-master や GeneralAtsPlugin のような共有データ)。
        /// </summary>
        private static void MarkExternal(RefNode node, string packageRoot)
        {
            bool topRoute = node.Kind == FileKind.Route && node.Parent?.Kind != FileKind.Route;
            if (node.Kind == FileKind.Vehicle && node.Exists)
            {
                node.VehicleRoot = GuessVehicleRoot(node);
                packageRoot = node.VehicleRoot;
            }
            else if (topRoute && node.Exists)
            {
                // 路線も同じように「路線フォルダ」を決め、その外(Nagoya_Common など)を外部とする
                node.RouteRoot = GuessRouteRoot(node);
                packageRoot = node.RouteRoot;
            }
            else if (packageRoot != null &&
                     node.FullPath.IndexOfAny(Path.GetInvalidPathChars()) < 0) // パスとして不正な参照は場所を判定しない
            {
                node.External = !IsUnder(node.FullPath, packageRoot);
            }
            foreach (var c in node.Children) MarkExternal(c, packageRoot);

            // 車両と、最上位の路線(マッププラグイン)について、AtsEXを使っているかを記録する
            if (node.Kind == FileKind.Vehicle || topRoute)
                node.UsesAtsEx = node.DescendantsAndSelf().Any(d =>
                    d.Kind == FileKind.AtsExPluginList || (d.Kind == FileKind.AtsPlugin && IsAtsExCaller(d.FullPath)));
        }

        /// <summary>
        /// 車両フォルダを推定する。基本は車両ファイルのあるフォルダだが、車両ファイルが
        /// Vehicle_txt のようなサブフォルダにあって参照の大半が「..\Notch」などを指している場合は、
        /// 1つ上のフォルダを車両フォルダとする(そこに直接の参照の半数以上が収まる場合だけ)。
        /// 2階層以上は上がらない(共有データを多く参照する車両で、隣の車両まで含む大きな
        /// フォルダを車両フォルダと誤認し、「未参照ファイル」が他の車両のファイルだらけになるのを防ぐ)。
        /// </summary>
        internal static string GuessVehicleRoot(RefNode vehicle)
        {
            string start = Path.GetDirectoryName(vehicle.FullPath);
            var refs = vehicle.Children.Where(c => c.Exists).Select(c => c.FullPath).ToList();
            if (refs.Count == 0) return start;
            if (refs.Count(r => IsUnder(r, start)) * 2 >= refs.Count) return start;
            string up = Path.GetDirectoryName(start);
            if (up != null && Path.GetDirectoryName(up) != null && // ドライブ直下までは上がらない
                refs.Count(r => IsUnder(r, up)) * 2 >= refs.Count)
                return up;
            return start;
        }

        /// <summary>
        /// 路線フォルダを推定する。シナリオから読まれた路線なら、シナリオのフォルダの直下にある
        /// 「路線のパッケージのフォルダ」(例: Scenarios\YamatojiLine、Uchibo\Uchibo20)。
        /// マップファイルを直接開いたときなどは、車両と同じ考え方(半数以上の参照が収まる所)で決める。
        /// </summary>
        internal static string GuessRouteRoot(RefNode route)
        {
            if (route.Parent?.Kind == FileKind.Scenario)
            {
                string scenarioDir = Path.GetDirectoryName(route.Parent.FullPath);
                if (IsUnder(route.FullPath, scenarioDir))
                {
                    string rel = route.FullPath.Substring(scenarioDir.TrimEnd('\\').Length + 1);
                    int sep = rel.IndexOf('\\');
                    return sep > 0 ? Path.Combine(scenarioDir, rel.Substring(0, sep)) : scenarioDir;
                }
            }
            return GuessVehicleRoot(route);
        }

        internal static bool IsUnder(string path, string dir)
        {
            if (path == null || dir == null) return false;
            string d = dir.TrimEnd('\\') + "\\";
            return path.StartsWith(d, StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------
        // 種類の判定
        // ------------------------------------------------------------------

        /// <summary>
        /// 種類を決める。参照元のキーで決まるものはキーを優先し、次にヘッダー、最後に拡張子。
        /// </summary>
        internal static FileKind DetectKind(string fullPath, string parentKey, FileKind parentKind,
                                            Dictionary<string, string> headerCache = null)
        {
            string ext = Path.GetExtension(fullPath);
            string key = parentKey?.Trim().ToLowerInvariant();

            // 路線(マップ)から読まれるファイルは、どの文で読まれたかで種類が決まる
            if (parentKind == FileKind.Route && key != null)
            {
                switch (key)
                {
                    case "include": return FileKind.Route;
                    case "structure.load": return FileKind.StructureList;
                    case "signal.load": return FileKind.SignalList;
                    case "sound.load": return FileKind.SoundList;
                    case "sound3d.load": return FileKind.Sound3DList;
                    case "station.load": return FileKind.StationList;
                }
                if (key.EndsWith("::mappluginusing>")) return FileKind.AtsExPluginList;
                if (key.StartsWith("train")) return FileKind.OtherTrain;
            }

            if (parentKind == FileKind.Vehicle && key != null)
            {
                switch (key)
                {
                    case "performancecurve": return FileKind.PerformanceCurve;
                    case "parameters": return FileKind.Parameters;
                    case "panel": return FileKind.Panel;
                    case "sound": return FileKind.Sound;
                    case "motornoise": return FileKind.MotorNoise;
                    case "ats":
                    case "ats32":
                    case "ats64": return FileKind.AtsPlugin;
                }
            }
            if (parentKind == FileKind.Scenario && key != null)
            {
                if (key == "route") return FileKind.Route;
                if (key == "vehicle") return FileKind.Vehicle;
            }
            if (ext.Equals(".dll", StringComparison.OrdinalIgnoreCase))
            {
                if (parentKind == FileKind.AtsExPluginList) return FileKind.AtsExPlugin;
                // プラグイン一覧・プラグイン設定から参照されるDLLは子プラグインとみなす
                if (parentKind == FileKind.PluginList || parentKind == FileKind.PluginConfig || parentKind == FileKind.AtsPlugin)
                    return FileKind.AtsPlugin;
                return FileKind.Library;
            }

            if (TextExts.Contains(ext) && File.Exists(fullPath))
            {
                if (Path.GetFileName(fullPath).Equals("detailmodules.txt", StringComparison.OrdinalIgnoreCase))
                    return FileKind.PluginList;
                // プラグインの設定から先は、BVEの形式に似たヘッダーがあってもプラグイン独自の形式とみなす
                if (parentKind == FileKind.AtsPlugin || parentKind == FileKind.PluginConfig || parentKind == FileKind.PluginList)
                    return FileKind.PluginConfig;

                string type;
                if (headerCache == null || !headerCache.TryGetValue(fullPath, out type))
                {
                    type = BveText.ReadHeaderOnly(fullPath)?.HeaderType?.ToLowerInvariant();
                    if (headerCache != null) headerCache[fullPath] = type;
                }
                if (type != null)
                {
                    if (type == "scenario") return FileKind.Scenario;
                    if (type == "vehicle") return FileKind.Vehicle;
                    if (type == "map") return FileKind.Route;
                    if (type.Contains("parameters")) return FileKind.Parameters;
                    // 「Vehicle Performance Table」「Motor Noise Table」は表。曲線・モーター音ファイル本体より先に判定する
                    if (type.Contains("performance table")) return FileKind.PerformanceTable;
                    if (type.Contains("motor noise table")) return FileKind.MotorNoiseTable;
                    if (type.Contains("performance")) return FileKind.PerformanceCurve;
                    if (type.Contains("panel")) return FileKind.Panel;
                    if (type.Contains("motor")) return FileKind.MotorNoise;
                    if (type.Contains("sound")) return FileKind.Sound;
                }
                // ヘッダーの無い表でも、性能曲線・モーター音ファイルから参照されていれば表とみなす
                if (parentKind == FileKind.PerformanceCurve) return FileKind.PerformanceTable;
                if (parentKind == FileKind.MotorNoise) return FileKind.MotorNoiseTable;
                return ext.Equals(".csv", StringComparison.OrdinalIgnoreCase) ? FileKind.Csv : FileKind.Text;
            }

            switch (ext.ToLowerInvariant())
            {
                case ".png": case ".bmp": case ".jpg": case ".jpeg": case ".gif":
                case ".tga": case ".dds": case ".tif": case ".tiff":
                    return FileKind.Image;
                case ".wav": case ".ogg": case ".mp3": case ".flac": case ".wma": case ".m4a":
                    return FileKind.Audio;
                case ".x": case ".obj": case ".fbx":
                    return FileKind.Model;
                case ".csv": return FileKind.Csv;
                case ".txt": case ".cfg": case ".ini": return FileKind.Text;
            }
            return FileKind.Other;
        }

        /// <summary>このキーの値は、拡張子が無くても必ずファイルパスとして扱う。</summary>
        private static bool IsPathKey(FileKind kind, string key)
        {
            string k = key?.Trim().ToLowerInvariant();
            switch (kind)
            {
                case FileKind.Scenario:
                    return k == "route" || k == "vehicle" || k == "image";
                case FileKind.Vehicle:
                    return k != null;
                case FileKind.Sound:
                    return k != null;
                case FileKind.PerformanceCurve:
                    return k != null;
                case FileKind.MotorNoise:
                    return k == "volume" || k == "frequency";
                case FileKind.Panel:
                    return k != null && k.EndsWith("image");
                case FileKind.PluginList:
                    return true;
                default:
                    return false;
            }
        }

        // ------------------------------------------------------------------
        // 展開
        // ------------------------------------------------------------------

        /// <summary>
        /// ノードの中身をたどる。1つのファイルで予期しないエラーが起きても、
        /// 全体の読み込みを止めずにそのノードの警告として残す。
        /// </summary>
        private void Expand(RefNode node, int depth)
        {
            Token.ThrowIfCancellationRequested();
            nodeCount++;
            ReportProgress(node.FullPath);
            try
            {
                ExpandCore(node, depth);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                // キャンセルだけは呼び出し元まで伝える(それ以外は警告として残して続ける)
                node.Warnings.Add("解析中にエラーが起きました: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------
        // 進み具合とキャンセル
        // ------------------------------------------------------------------

        /// <summary>読み込みを途中でやめるためのトークン(画面のキャンセルボタン)。</summary>
        public CancellationToken Token;

        /// <summary>進み具合の通知先(画面のステータスバー)。nullなら通知しない。</summary>
        public IProgress<ScanProgress> Progress;

        private int nodeCount;
        private readonly Stopwatch reportWatch = Stopwatch.StartNew();
        private long lastReportMs = -1000;

        /// <summary>進み具合を知らせる。画面の更新が追いつかなくならないよう、0.1秒に1回まで。</summary>
        private void ReportProgress(string current)
        {
            if (Progress == null) return;
            long now = reportWatch.ElapsedMilliseconds;
            if (now - lastReportMs < 100) return;
            lastReportMs = now;
            Progress.Report(new ScanProgress { Files = expanded.Count, References = nodeCount, Current = current });
        }

        private void ExpandCore(RefNode node, int depth)
        {
            if (!node.Exists || depth > MaxDepth) return;

            if (node.Kind == FileKind.AtsPlugin || node.Kind == FileKind.Library || node.Kind == FileKind.AtsExPlugin)
            {
                ExpandDll(node, depth);
                return;
            }
            string ext = Path.GetExtension(node.FullPath);
            if (node.Kind == FileKind.Model)
            {
                // ストラクチャーのテクスチャは、オプションがオンのときだけ確認する
                if (CheckTextures && ext.Equals(".x", StringComparison.OrdinalIgnoreCase)) ExpandXTextures(node, depth);
                return;
            }
            if (!TextExts.Contains(ext)) return;

            if (AlreadyExpanded(node))
            {
                ReadHeaderInfo(node);
                return;
            }
            long limit = node.Kind == FileKind.Route ? MaxMapBytes : MaxScanBytes;
            if (new FileInfo(node.FullPath).Length > limit)
            {
                node.NotExpanded = true;
                node.Warnings.Add("ファイルが大きすぎるため中身を解析していません");
                return;
            }

            BveTextFile text;
            try { text = BveText.Read(node.FullPath, (int)limit); }
            catch (Exception ex)
            {
                node.Warnings.Add("読み込めません: " + ex.Message);
                return;
            }
            node.EncodingName = text.EncodingName;
            node.HeaderType = text.HeaderType;
            node.HeaderVersion = text.HeaderVersion;

            switch (node.Kind)
            {
                case FileKind.AtsExPluginList:
                    ParseAtsExPluginUsing(node, text, depth);
                    return;
                case FileKind.Route:
                    ParseMap(node, text, depth);
                    return;
                case FileKind.StructureList:
                case FileKind.SignalList:
                case FileKind.SoundList:
                case FileKind.Sound3DList:
                case FileKind.StationList:
                    ParseList(node, text, depth);
                    return;
            }
            ParseReferences(node, text, depth);

            // AtsEXの車両プラグイン一覧は、車両ファイルと同じフォルダに置かれる
            if (node.Kind == FileKind.Vehicle) AttachVehiclePluginUsing(node, depth);
        }

        private static void ReadHeaderInfo(RefNode node)
        {
            // 文字コードの判定に使うので、先頭512バイトより多めに読む
            var h = BveText.ReadHeaderOnly(node.FullPath, 64 * 1024);
            if (h == null) return;
            node.EncodingName = h.EncodingName;
            node.HeaderType = h.HeaderType;
            node.HeaderVersion = h.HeaderVersion;
        }

        private void ParseReferences(RefNode node, BveTextFile text, int depth)
        {
            string baseDir = Path.GetDirectoryName(node.FullPath);
            // プラグイン設定から参照されるファイルは、プラグインDLLのフォルダ基準でも探す
            string pluginDir = null;
            var plugin = node.NearestPluginAncestor();
            if (plugin != null) pluginDir = Path.GetDirectoryName(plugin.FullPath);

            if (node.Parent != null && PluginOrganizerConfigName(node.Parent.FullPath) != null)
            {
                ParseOrganizerInstructions(node, text, baseDir, pluginDir, depth);
                return;
            }
            if (node.Parent != null && PluginConverterConfigName(node.Parent.FullPath) != null)
            {
                ParsePluginConverterXml(node, text, depth);
                return;
            }

            string ext = Path.GetExtension(node.FullPath);
            bool yaml =ext.Equals(".yml", StringComparison.OrdinalIgnoreCase) || ext.Equals(".yaml", StringComparison.OrdinalIgnoreCase);

            string section = null;
            for (int i = 0; i < text.Lines.Length; i++)
            {
                if ((i & 4095) == 0) Token.ThrowIfCancellationRequested();
                if (i == 0 && text.FirstLineIsHeader) continue;
                string line = (yaml ? StripYamlComment(text.Lines[i]) : StripComment(text.Lines[i])).Trim();
                if (line.Length == 0) continue;

                string key = null;
                string value = line;
                if (yaml)
                {
                    // YAML: 「- 」(リストの要素)を外し、「キー: 値」に分ける。
                    // 「C:\～」のドライブ指定と区別するため、区切りは後ろに空白か行末が続く「:」だけ。
                    if (line == "---" || line == "...") continue;
                    value = line.TrimStart('-', ' ', '\t');
                    int colon = FindYamlColon(value);
                    if (colon >= 0)
                    {
                        key = value.Substring(0, colon).Trim().Trim('"', '\'');
                        value = value.Substring(colon + 1).Trim();
                        if (value.Length == 0)
                        {
                            // 値の無いキー(「Plugins:」など)は、以降の行のまとまりとしてセクション扱い
                            section = key;
                            continue;
                        }
                    }
                }
                else
                {
                    if (line[0] == '[')
                    {
                        int close = line.IndexOf(']');
                        section = close > 0 ? line.Substring(1, close - 1).Trim() : line.Substring(1).Trim();
                        continue;
                    }
                    int eq = line.IndexOf('=');
                    if (eq >= 0)
                    {
                        key = line.Substring(0, eq).Trim();
                        value = line.Substring(eq + 1).Trim();
                    }
                }

                if (node.Kind == FileKind.Scenario && !IsPathKey(FileKind.Scenario, key))
                    continue; // タイトル・作者などは参照ではない

                bool pathKey = IsPathKey(node.Kind, key);
                foreach (string token in SplitTokens(value, node.Kind == FileKind.Scenario))
                {
                    if (!pathKey && !LooksLikePath(token))
                    {
                        // プラグイン設定の「..\IC」のようなフォルダ指定
                        if (node.Kind == FileKind.PluginConfig && LooksLikeFolder(token))
                            AddFolderReference(node, token, key, section, i + 1, baseDir, pluginDir, depth);
                        // RealAnalogGaugeの「Gauge0 = SPD」のように拡張子なしで書かれたファイル名
                        // (同じフォルダに「SPD.ini」「SPD.csv」などが実在するときだけ参照とみなす)
                        else if (node.Kind == FileKind.PluginConfig && BareNameRegex.IsMatch(token) && !token.All(char.IsDigit))
                            foreach (var ext2 in PluginConfigExts.OrderBy(x => x))
                                if (File.Exists(Path.Combine(baseDir, token + ext2)))
                                    AddReference(node, token + ext2, key, section, i + 1, baseDir, pluginDir, false, depth);
                        continue;
                    }
                    AddReference(node, token, key, section, i + 1, baseDir, pluginDir, pathKey, depth);
                }
            }
        }

        private void AddReference(RefNode parent, string raw, string key, string section, int line,
                                  string baseDir, string pluginDir, bool pathKey, int depth)
        {
            string resolved = Resolve(raw, baseDir);
            if (resolved == null)
            {
                // パス専用キーの値が不正なら、黙って捨てずにエラーとして載せる
                if (pathKey)
                {
                    var bad = parent.AddChild(new RefNode
                    {
                        FullPath = raw, RawValue = raw, Key = key, Section = section, Line = line,
                        Kind = FileKind.Unknown, Exists = false,
                    });
                    bad.Warnings.Add("パスとして使えない文字が含まれています");
                }
                return;
            }
            bool exists = File.Exists(resolved);
            if (!exists && pluginDir != null && !string.Equals(pluginDir, baseDir, StringComparison.OrdinalIgnoreCase))
            {
                string alt = Resolve(raw, pluginDir);
                if (alt != null && File.Exists(alt)) { resolved = alt; exists = true; }
            }
            if (!exists)
            {
                if (Directory.Exists(resolved)) return; // フォルダの指定は対象外
                // パス専用キー以外は、既知の拡張子のときだけ「見つからない」として載せる
                if (!pathKey && !KnownFileExts.Contains(Path.GetExtension(resolved))) return;
            }

            var child = new RefNode
            {
                FullPath = resolved,
                RawValue = raw,
                Key = key,
                Section = section,
                Line = line,
                Exists = exists,
            };
            child.Kind = Kind(resolved, key, parent.Kind);
            if (!exists) child.Warnings.Add("ファイルが見つかりません");
            if (exists) explicitRefs.Add(resolved);

            if (child.Kind == FileKind.AtsPlugin)
            {
                string k = key?.Trim().ToLowerInvariant();
                if (parent.Kind == FileKind.Vehicle)
                    child.ExpectedArch = k == "ats64" ? "x64" : "x86"; // Ats(BVE5)とAts32は32bit
                else
                    child.ExpectedArch = parent.NearestPluginAncestor()?.ExpectedArch ?? parent.ExpectedArch;
            }

            parent.AddChild(child);
            Expand(child, depth + 1);
        }

        /// <summary>
        /// PluginOrganizerのInstructions.yml / Instructions64.yml を読む。命令の書き方は2通りある。
        ///   v1.0:  - run Staf.dll
        ///   v1.1:  - cmd:
        ///            - run
        ///            - Staf.dll
        /// 「run」の引数は子プラグインのDLL、「copy」の1つ目の引数はコピー元(フォルダかファイル)として扱う。
        /// 「copy」のコピー先は実行時に作られることがあるので、存在確認はしない。
        /// </summary>
        private void ParseOrganizerInstructions(RefNode node, BveTextFile text, string baseDir, string pluginDir, int depth)
        {
            var commands = new List<List<(string arg, int line)>>();
            var sections = new List<string>();
            List<(string arg, int line)> current = null;
            int currentIndent = -1;
            string section = null;

            for (int i = 0; i < text.Lines.Length; i++)
            {

                if ((i & 4095) == 0) Token.ThrowIfCancellationRequested();
                string raw = StripYamlComment(text.Lines[i]);
                string t = raw.Trim();
                if (t.Length == 0) continue;
                int indent = raw.Length - raw.TrimStart().Length;

                if (t.StartsWith("- ") || t == "-")
                {
                    string item = t.Substring(1).Trim();
                    // 「- cmd:」の下に並ぶ引数
                    if (current != null && indent > currentIndent)
                    {
                        current.Add((Unquote(item), i + 1));
                        continue;
                    }
                    current = null;
                    if (item == "cmd:")
                    {
                        current = new List<(string, int)>();
                        currentIndent = indent;
                        commands.Add(current);
                        sections.Add(section);
                        continue;
                    }
                    if (FindYamlColon(item) >= 0) continue; // 「- prio: 0」などの設定値

                    // v1.0形式: 「- run パス」
                    int sp = item.IndexOfAny(new[] { ' ', '\t' });
                    var cmd = new List<(string, int)>();
                    if (sp > 0)
                    {
                        cmd.Add((item.Substring(0, sp), i + 1));
                        string rest = item.Substring(sp + 1).Trim();
                        // copyは「コピー元 コピー先」の2つ。パスに空白が無い前提で分ける
                        if (item.StartsWith("copy", StringComparison.OrdinalIgnoreCase))
                            foreach (var a in rest.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                                cmd.Add((Unquote(a), i + 1));
                        else
                            cmd.Add((Unquote(rest), i + 1));
                    }
                    else
                    {
                        cmd.Add((item, i + 1));
                    }
                    commands.Add(cmd);
                    sections.Add(section);
                    continue;
                }

                current = null;
                // 「init:」「exec:」など値の無いキーを、ツリーでまとめるセクション名にする
                int colon = FindYamlColon(t);
                if (colon >= 0 && colon == t.Length - 1) section = t.Substring(0, colon).Trim();
            }

            for (int c = 0; c < commands.Count; c++)
            {
                var cmd = commands[c];
                if (cmd.Count < 2) continue;
                string verb = cmd[0].arg.ToLowerInvariant();
                string sec = sections[c];
                if (verb == "run")
                {
                    AddReference(node, cmd[1].arg, "run", sec, cmd[1].line, baseDir, pluginDir, true, depth);
                }
                else if (verb == "copy")
                {
                    var (src, line) = cmd[1];
                    string dir = Resolve(src, baseDir);
                    if (dir != null && Directory.Exists(dir))
                        AddFolderReference(node, src, "copy", sec, line, baseDir, pluginDir, depth);
                    else
                        AddReference(node, src, "copy", sec, line, baseDir, pluginDir, true, depth);
                }
                else
                {
                    foreach (var (arg, line) in cmd.Skip(1))
                        if (LooksLikePath(arg))
                            AddReference(node, arg, cmd[0].arg, sec, line, baseDir, pluginDir, false, depth);
                }
            }
        }

        private static string Unquote(string s)
        {
            s = s.Trim();
            if (s.Length >= 2 && (s[0] == '"' || s[0] == '\'') && s[s.Length - 1] == s[0]) s = s.Substring(1, s.Length - 2);
            return s;
        }

        /// <summary>フォルダの指定: 実在するときだけ、中のファイルを子として付ける。</summary>
        private void AddFolderReference(RefNode parent, string raw, string key, string section, int line,
                                        string baseDir, string pluginDir, int depth)
        {
            string dir = Resolve(raw, baseDir);
            if (dir == null || !Directory.Exists(dir))
            {
                dir = pluginDir != null ? Resolve(raw, pluginDir) : null;
                if (dir == null || !Directory.Exists(dir)) return;
            }
            var folder = parent.AddChild(new RefNode
            {
                FullPath = dir, RawValue = raw, Key = key, Section = section, Line = line,
                Kind = FileKind.Folder, Exists = true,
            });
            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch (Exception) { return; }
            // フォルダの中身は一覧として並べるだけで、中身の解析(参照たどり)はしない。
            // KTY_libのNOTICE.txtのような説明書きは一覧にも出さない
            foreach (var f in files.Where(x => !DocFileRegex.IsMatch(Path.GetFileName(x)))
                                   .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(500))
            {
                var kind = Kind(f, null, FileKind.Unknown);
                folder.AddChild(new RefNode
                {
                    FullPath = f, RawValue = Path.GetFileName(f), Exists = true,
                    // フォルダ内のDLLは補助ライブラリのこともあるので、ATSプラグインとしての検査はしない
                    Kind = kind == FileKind.AtsPlugin ? FileKind.Library : kind,
                    Arch = kind == FileKind.AtsPlugin || kind == FileKind.Library ? PeInfo.Read(f).Arch : null,
                });
                explicitRefs.Add(f);
            }
        }

        /// <summary>DLL: PE情報を調べ、ATSプラグインなら同じフォルダの設定ファイルも子に付ける。</summary>
        private void ExpandDll(RefNode node, int depth)
        {
            var pe = PeInfo.Read(node.FullPath);
            if (pe.Error != null)
            {
                node.Warnings.Add("DLLを解析できません: " + pe.Error);
                return;
            }
            node.Arch = pe.Arch;
            if (node.Kind == FileKind.AtsPlugin)
            {
                if (node.ExpectedArch != null && pe.Arch != node.ExpectedArch)
                    node.Warnings.Add($"{node.ExpectedArch}用の指定ですが、DLLは {pe.Arch} です");
                var missing = PeInfo.AtsFunctions.Where(f => !pe.Exports.Contains(f)).ToList();
                if (missing.Count == PeInfo.AtsFunctions.Length)
                    node.Warnings.Add("ATSプラグインの関数がエクスポートされていません(DllExportの処理に失敗している可能性)");
                // Load・Disposeが無い配布プラグイン(ATS-N.dll等)も実際に動いているので、
                // 警告するのはBVEが必ず呼ぶGetPluginVersion・Elapseが無いときだけにする
                // (個々の関数の有無はプレビュー欄に✓/✗で出る)
                else if (missing.Contains("Elapse") || missing.Contains("GetPluginVersion"))
                    node.Warnings.Add("必須の関数がエクスポートされていません: " + string.Join(", ", missing));
            }
            else if (node.Kind != FileKind.AtsExPlugin)
            {
                return; // ただのライブラリは設定ファイルを探さない
            }
            // AtsEXプラグインは.NETのDLLなので、ATSプラグインの関数の検査はしない(設定ファイルは下で探す)

            if (AlreadyExpanded(node)) return;

            // AtsEXの入口(AtsEx.Caller.x86.dll等)は、読むファイルが決まっているのでそれだけを付ける
            if (node.Kind == FileKind.AtsPlugin && IsAtsExCaller(node.FullPath))
            {
                AttachAtsExCaller(node, depth);
                return;
            }

            // PluginOrganizer・PluginConverterは読み込む設定ファイルが決まっているので、それだけを付ける
            // (同じフォルダの他のファイルは推定しない)
            //   PluginOrganizer.dll → Instructions.yml、PluginOrganizer64.dll → Instructions64.yml
            //   PluginConverter.dll → PluginConverter.xml、PluginConverter_A0.dll → PluginConverter_A0.xml
            string fixedConfig = PluginOrganizerConfigName(node.FullPath) ?? PluginConverterConfigName(node.FullPath);
            if (fixedConfig != null)
            {
                string cfg = Path.Combine(Path.GetDirectoryName(node.FullPath), fixedConfig);
                var child = new RefNode
                {
                    FullPath = cfg,
                    RawValue = fixedConfig,
                    Exists = File.Exists(cfg),
                    Kind = FileKind.PluginConfig,
                    ExpectedArch = node.ExpectedArch,
                    Note = PluginOrganizerConfigName(node.FullPath) != null ? "PluginOrganizerの設定" : "PluginConverterの設定",
                };
                if (!child.Exists) child.Warnings.Add("ファイルが見つかりません");
                else explicitRefs.Add(cfg);
                node.AddChild(child);
                Expand(child, depth + 1);
                return;
            }

            string dir = Path.GetDirectoryName(node.FullPath);
            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch (Exception) { return; }

            // DetailManager(とMultiplexer)はdetailmodules.txtを読む
            string dllBase = Path.GetFileNameWithoutExtension(node.FullPath);
            bool readsDetailModules =
                dllBase.IndexOf("DetailManager", StringComparison.OrdinalIgnoreCase) >= 0 ||
                dllBase.IndexOf("Multiplexer", StringComparison.OrdinalIgnoreCase) >= 0 ||
                node.Parent?.Kind == FileKind.Vehicle;

            // 設定ファイルは名前がDLLと対応するものだけ(AAA*.dll → AAA.ini など)。
            // ini等を先に処理し、そこから参照されたcsv等は推定ノードとして二重に出さない
            var configs = SelectConfigFiles(dllBase, files.Where(f => PluginConfigExts.Contains(Path.GetExtension(f))));
            var siblings = files
                .Where(f => readsDetailModules && Path.GetFileName(f).Equals("detailmodules.txt", StringComparison.OrdinalIgnoreCase))
                .Concat(configs)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(f => !DocFileRegex.IsMatch(Path.GetFileName(f)))
                // 車両フォルダ直下にDLLを置いた場合、隣の車両ファイル等(BveTs形式)はプラグインの設定ではない
                .Where(f => BveText.ReadHeaderOnly(f)?.IsBveTs != true)
                .OrderBy(f => Path.GetFileName(f).Equals("detailmodules.txt", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(f => Path.GetExtension(f).Equals(".csv", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (string f in siblings)
            {
                // ini等から明示的に参照されているファイルは、そちらで表示されるので推定には出さない
                if (explicitRefs.Contains(f)) continue;

                var child = new RefNode
                {
                    FullPath = f,
                    RawValue = Path.GetFileName(f),
                    Exists = true,
                    Inferred = true,
                    ExpectedArch = node.ExpectedArch,
                };
                child.Kind = Kind(f, null, FileKind.AtsPlugin);
                node.AddChild(child);
                Expand(child, depth + 1);
            }
        }

        // ------------------------------------------------------------------
        // 文字列処理
        // ------------------------------------------------------------------

        private static string StripComment(string line)
        {
            int cut = line.Length;
            int a = line.IndexOf(';'); if (a >= 0 && a < cut) cut = a;
            int b = line.IndexOf('#'); if (b >= 0 && b < cut) cut = b;
            int c = line.IndexOf("//", StringComparison.Ordinal);
            // "http://" のようなURL中の「//」はコメントではない
            if (c >= 0 && c < cut && !(c > 0 && line[c - 1] == ':')) cut = c;
            return line.Substring(0, cut);
        }

        private static IEnumerable<string> SplitTokens(string value, bool scenario)
        {
            foreach (string part in value.Split('|', ','))
            {
                string t = part.Trim().Trim('"', '\'').Trim();
                if (scenario)
                {
                    // シナリオの「ファイル名 * 重み」の重みを外す
                    int star = t.LastIndexOf('*');
                    if (star > 0) t = t.Substring(0, star).Trim();
                }
                if (t.Length > 0) yield return t;
            }
        }

        private static bool LooksLikePath(string token)
        {
            if (token.Length < 3 || token.Length > 260) return false;
            if (token.Contains("://")) return false;
            if (!ExtRegex.IsMatch(token)) return false;
            // "1.5e" のような数値は除外
            return !double.TryParse(token, out _);
        }

        /// <summary>
        /// DLLと名前が対応する設定ファイルを選ぶ。
        ///  - 同じ名前:           KTY_HOSO.dll      → KTY_HOSO.ini
        ///  - ビット数表記を除いた名前: KTY_HOSO64.dll    → KTY_HOSO.ini
        ///  - DLL名の先頭部分:     ConstSpdCtrl.dll  → ConstSpd.ini(AAA*.dll → AAA.ini。3文字以上)
        ///  - 「DLL名_～」:        KTY_HOSO.dll      → KTY_HOSO_Effects.ini
        /// DLL名そのままの設定ファイルがあるときは、それより短い名前のもの(別のビット数用など)は付けない
        /// (keisei_x64.dll には keisei_x64.txt だけを付け、keisei.txt は付けない)。
        /// 「DLL名_x64」のような別のビット数用の設定ファイルも付けない。
        /// </summary>
        internal static List<string> SelectConfigFiles(string dllBase, IEnumerable<string> candidates)
        {
            string core = ArchSuffixRegex.Replace(dllBase, "");
            if (core.Length == 0) core = dllBase;

            var exact = new List<string>();
            var shorter = new List<string>();
            var companions = new List<string>();
            foreach (var f in candidates)
            {
                string cb = Path.GetFileNameWithoutExtension(f);
                if (cb.Equals(dllBase, StringComparison.OrdinalIgnoreCase))
                    exact.Add(f);
                else if (cb.Equals(core, StringComparison.OrdinalIgnoreCase) ||
                         (cb.Length >= 3 && dllBase.StartsWith(cb, StringComparison.OrdinalIgnoreCase)))
                    shorter.Add(f);
                else if (cb.Length > core.Length + 1 &&
                         cb.StartsWith(core, StringComparison.OrdinalIgnoreCase) &&
                         (cb[core.Length] == '_' || cb[core.Length] == '-' || cb[core.Length] == '.') &&
                         !ArchOnlyRegex.IsMatch(cb.Substring(core.Length + 1)))
                    companions.Add(f);
            }
            var result = new List<string>(exact);
            if (exact.Count == 0) result.AddRange(shorter);
            result.AddRange(companions);
            return result;
        }

        // ------------------------------------------------------------------
        // 路線(マップ)
        // ------------------------------------------------------------------

        // マップから他のファイルを読むのは次の文だけ(どれも引用符で囲んだパスを書く)。
        // マップ構文を全部解析するのは重いので、この文だけを拾う。
        // パスの書き方: 'ファイル名' か、文字列を入れた変数 $名前。
        // 後ろに「+」が続くもの('..\Train\local6_' + ceil(rand(3)) + '.txt' など)は実行時まで値が決まらない
        private const string MapPathPattern = @"(?:'(?<path>[^']*)'|\$(?<var>[A-Za-z_][A-Za-z0-9_]*))(?<concat>\s*\+)?";
        private static readonly Regex MapIncludeRegex = new Regex(
            @"\binclude\s*" + MapPathPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex MapLoadRegex = new Regex(
            @"\b(?<obj>structure|signal|sound3d|sound|station)\s*\.\s*load\s*\(\s*" + MapPathPattern,
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex MapTrainAddRegex = new Regex(
            @"\btrain\s*\.\s*add\s*\(\s*'(?<key>[^']*)'\s*,\s*" + MapPathPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex MapTrainLoadRegex = new Regex(
            @"\btrain\s*\[\s*'(?<key>[^']*)'\s*\]\s*\.\s*load\s*\(\s*" + MapPathPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // 変数への代入「$名前 = 式;」
        // 「;」まで同じ行に無いもの(次の行で「+ 乱数 + '.txt';」と続く等)は値が決まらないものとして扱う
        private static readonly Regex MapVarAssignRegex = new Regex(
            @"\$(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=(?!=)\s*(?<expr>[^;]*)(?<end>;)?", RegexOptions.Compiled);
        private static readonly Regex MapStringLiteralRegex = new Regex(@"^'(?<s>[^']*)'$", RegexOptions.Compiled);
        // AtsEX(BveEX)の特別なinclude「<AtsEx::MapPluginUsing>パス」
        private static readonly Regex MapSpecialIncludeRegex = new Regex(
            @"^\s*<(?<ns>[A-Za-z]+)::(?<cmd>[A-Za-z]+)>(?<rest>.*)$", RegexOptions.Compiled);

        /// <summary>
        /// マップの変数のうち、文字列そのものが代入されたもの(「$a = 'Train\x.txt';」)。
        /// include で読まれる順に代入されていくので、路線全体で1つの辞書を使う(BVEの変数も路線全体で共通)。
        /// 式(連結や乱数)が代入された変数は値が決まらないので消す。
        /// </summary>
        private readonly Dictionary<string, string> mapVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, string> MapLoadKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["structure"] = "Structure.Load", ["signal"] = "Signal.Load", ["sound"] = "Sound.Load",
            ["sound3d"] = "Sound3D.Load", ["station"] = "Station.Load",
        };

        /// <summary>
        /// マップファイルを読む。include・Structure.Load 等・Train.Add・Train[].Load の文だけを拾う。
        /// パスは、include されたファイルの中でも**最上位のマップファイル(シナリオの Route)のフォルダ基準**
        /// (実データで確認: 内房線の Maps\3793F\AddTrain_0.txt に書かれた 'AddTrain_Formation\～' は
        /// Maps\AddTrain_Formation にある。大和路線も同様)。
        /// </summary>
        private void ParseMap(RefNode node, BveTextFile text, int depth)
        {
            var top = node;
            while (top.Parent != null && top.Parent.Kind == FileKind.Route) top = top.Parent;
            string baseDir = Path.GetDirectoryName(top.FullPath);
            int unresolved = 0;

            void Add(Match m, string key, int lineNo)
            {
                string path;
                if (m.Groups["concat"].Success) { unresolved++; return; }
                if (m.Groups["path"].Success) path = m.Groups["path"].Value;
                else if (!mapVars.TryGetValue(m.Groups["var"].Value, out path)) { unresolved++; return; }

                if (key == "include")
                {
                    // AtsEXのマッププラグイン用の特別なinclude
                    //   '[[AtsEx::NOMPI]] メッセージ'            → AtsEXが無いときに出す表示。ファイルではない
                    //   '<AtsEx::MapPluginUsing>..\Plugins.xml'  → マッププラグインの一覧
                    if (path.StartsWith("[[")) return;
                    var tag = MapSpecialIncludeRegex.Match(path);
                    if (tag.Success)
                    {
                        if (tag.Groups["cmd"].Value.Equals("MapPluginUsing", StringComparison.OrdinalIgnoreCase))
                            AddReference(node, tag.Groups["rest"].Value.Trim(), "<" + tag.Groups["ns"].Value + "::MapPluginUsing>",
                                         null, lineNo, baseDir, null, true, depth);
                        return; // それ以外の特別なincludeは対象外
                    }
                }
                AddReference(node, path, key, null, lineNo, baseDir, null, true, depth);
            }

            for (int i = 0; i < text.Lines.Length; i++)
            {

                if ((i & 4095) == 0) Token.ThrowIfCancellationRequested();
                if (i == 0 && text.FirstLineIsHeader) continue;
                string raw = text.Lines[i];
                // ファイルを読む文は必ず引用符か変数を使うので、どちらも無い行は飛ばす(大半の行はここで終わる)
                if (raw.IndexOf('\'') < 0 && raw.IndexOf('$') < 0) continue;
                string line = StripMapComment(raw);

                if (line.IndexOf('$') >= 0)
                {
                    foreach (Match m in MapVarAssignRegex.Matches(line))
                    {
                        var lit = MapStringLiteralRegex.Match(m.Groups["expr"].Value.Trim());
                        if (lit.Success && m.Groups["end"].Success) mapVars[m.Groups["name"].Value] = lit.Groups["s"].Value;
                        else mapVars.Remove(m.Groups["name"].Value);
                    }
                }

                foreach (Match m in MapIncludeRegex.Matches(line))
                    Add(m, "include", i + 1);
                foreach (Match m in MapLoadRegex.Matches(line))
                    Add(m, MapLoadKeys[m.Groups["obj"].Value], i + 1);
                foreach (Match m in MapTrainAddRegex.Matches(line))
                    Add(m, "Train.Add('" + m.Groups["key"].Value + "')", i + 1);
                foreach (Match m in MapTrainLoadRegex.Matches(line))
                    Add(m, "Train['" + m.Groups["key"].Value + "'].Load", i + 1);
            }
            if (unresolved > 0) node.Note = $"値が決まらない変数での参照 {unresolved} 件は未確認";
        }

        /// <summary>マップのコメント(引用符の外の「#」「//」以降)を外す。</summary>
        private static string StripMapComment(string line)
        {
            bool inQuote = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '\'') { inQuote = !inQuote; continue; }
                if (inQuote) continue;
                if (c == '#' || (c == '/' && i + 1 < line.Length && line[i + 1] == '/')) return line.Substring(0, i);
            }
            return line;
        }

        /// <summary>
        /// 路線から読まれる表ファイル(ストラクチャー・信号・サウンド・3Dサウンド・停車場のリスト)を読む。
        /// 1列目がキー。ストラクチャー・サウンド・3Dサウンドのリストは2列目が必ずファイルのパス
        /// (無ければ「見つかりません」)。信号・停車場のリストはキーで他を参照するのが主なので、
        /// 拡張子の付いた値だけをパスとみなす。
        /// </summary>
        private void ParseList(RefNode node, BveTextFile text, int depth)
        {
            string baseDir = Path.GetDirectoryName(node.FullPath);
            bool secondIsPath = node.Kind == FileKind.StructureList || node.Kind == FileKind.SoundList || node.Kind == FileKind.Sound3DList;
            for (int i = 0; i < text.Lines.Length; i++)
            {
                if ((i & 4095) == 0) Token.ThrowIfCancellationRequested();
                if (i == 0 && text.FirstLineIsHeader) continue;
                string line = StripComment(text.Lines[i]).Trim();
                if (line.Length == 0) continue;
                string[] cols = line.Split(',');
                string key = cols[0].Trim();
                if (key.Length == 0) continue; // 「,,」だけの行や、説明だけの行
                for (int c = 1; c < cols.Length; c++)
                {
                    string token = cols[c].Trim().Trim('"').Trim();
                    if (token.Length == 0) continue;
                    bool pathCol = secondIsPath && c == 1;
                    if (!pathCol && !LooksLikePath(token)) continue;
                    AddReference(node, token, key, null, i + 1, baseDir, null, pathCol, depth);
                }
            }
        }

        // ------------------------------------------------------------------
        // ストラクチャー(.x)のテクスチャ
        // ------------------------------------------------------------------

        private static readonly Regex XTextureRegex = new Regex(
            @"TextureFilename\s*\{\s*""(?<path>[^""]*)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>.x ファイルのテクスチャ一覧を読んだ結果。</summary>
        internal sealed class XTextureInfo
        {
            /// <summary>バイナリ形式・圧縮形式など、中身を読めない形式。</summary>
            public bool NotText;
            public string Format;
            public string Error;
            public readonly List<(string path, int line)> Textures = new List<(string, int)>();
        }

        /// <summary>
        /// .x ファイルの TextureFilename { "～"; } を読む(テキスト形式のみ)。同じテクスチャは1回だけ。
        /// プレビュー欄でも使う。
        /// そのマテリアルがどの面で使われているか(MeshMaterialList)は見ていない。使っていないマテリアルの
        /// 画像も拾うが、仕様とする(調べるには .x の構造を詳しく読む必要があり、重くなるため)。
        /// </summary>
        internal static XTextureInfo ReadXTextures(string path)
        {
            var info = new XTextureInfo();
            try
            {
                byte[] head = new byte[16];
                using (var fs = File.OpenRead(path)) fs.Read(head, 0, head.Length);
                string magic = System.Text.Encoding.ASCII.GetString(head);
                if (!magic.StartsWith("xof ")) { info.NotText = true; info.Format = "Xファイルではない"; return info; }
                info.Format = magic.Substring(8, 4).Trim();
                if (!info.Format.Equals("txt", StringComparison.OrdinalIgnoreCase)) { info.NotText = true; return info; }

                var text = BveText.Read(path, 64 * 1024 * 1024);
                // 「TextureFilename {」「"a.png";」「}」と複数行に分けて書かれることが多いので、全体をまとめて探す
                string all = string.Join("\n", text.Lines);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int lineNo = 1, scanned = 0;
                foreach (Match m in XTextureRegex.Matches(all))
                {
                    // 見つかった位置までの改行を数えて行番号にする
                    for (; scanned < m.Index; scanned++) if (all[scanned] == '\n') lineNo++;
                    // Xファイルの文字列では「\\」が「\」を表す
                    string p = m.Groups["path"].Value.Replace("\\\\", "\\").Trim();
                    if (p.Length > 0 && seen.Add(p)) info.Textures.Add((p, lineNo));
                }
            }
            catch (Exception ex)
            {
                info.Error = ex.Message;
            }
            return info;
        }

        private void ExpandXTextures(RefNode node, int depth)
        {
            if (AlreadyExpanded(node)) return;
            var info = ReadXTextures(node.FullPath);
            if (info.Error != null) { node.Warnings.Add("Xファイルを読めません: " + info.Error); return; }
            if (info.NotText) { node.Note = $"{info.Format} 形式のためテクスチャは未確認"; return; }
            string dir = Path.GetDirectoryName(node.FullPath);
            foreach (var (p, line) in info.Textures)
                AddReference(node, p, "Texture", null, line, dir, null, true, depth);
        }

        // ------------------------------------------------------------------
        // AtsEX
        // ------------------------------------------------------------------

        /// <summary>AtsEXの入口のDLL(AtsEx.Caller.x86.dll など。改名後のBveEXも含む)かどうか。</summary>
        internal static bool IsAtsExCaller(string dllPath)
        {
            string name = Path.GetFileName(dllPath);
            return name.StartsWith("AtsEx.Caller", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("BveEx.Caller", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// AtsEx.Caller.x86.dll に、それが読むファイルを付ける。
        ///  - AtsEx.Caller.txt(または AtsEx.Caller.x86.txt): AtsEX本体のフォルダの場所(1行目、DLLのフォルダ基準)
        ///  - AtsEx.Caller.x86.VehiclePluginUsing.xml: AtsEXプラグインの一覧
        /// </summary>
        private void AttachAtsExCaller(RefNode caller, int depth)
        {
            string dir = Path.GetDirectoryName(caller.FullPath);
            string dllBase = Path.GetFileNameWithoutExtension(caller.FullPath);
            string core = ArchSuffixRegex.Replace(dllBase, "");

            // AtsEX本体の場所を書いたテキスト
            string txt = new[] { dllBase + ".txt", core + ".txt" }
                .Select(n => Path.Combine(dir, n)).FirstOrDefault(File.Exists);
            var txtNode = caller.AddChild(new RefNode
            {
                FullPath = txt ?? Path.Combine(dir, core + ".txt"),
                RawValue = Path.GetFileName(txt ?? core + ".txt"),
                Exists = txt != null,
                Kind = FileKind.PluginConfig,
                Note = "AtsEX本体の場所",
            });
            if (txt == null)
            {
                txtNode.Warnings.Add("ファイルが見つかりません");
            }
            else if (!AlreadyExpanded(txtNode))
            {
                explicitRefs.Add(txt);
                try
                {
                    var t = BveText.Read(txt);
                    txtNode.EncodingName = t.EncodingName;
                    for (int i = 0; i < t.Lines.Length; i++)
                    {
                        string line = t.Lines[i].Trim().Trim('"');
                        if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue;
                        string folder = Resolve(line, dir);
                        if (folder != null && Directory.Exists(folder))
                        {
                            AddFolderReference(txtNode, line, null, null, i + 1, dir, null, depth + 1);
                            txtNode.Children.Last().Note = "AtsEX本体";
                        }
                        else
                        {
                            var missing = txtNode.AddChild(new RefNode
                            {
                                FullPath = folder ?? line, RawValue = line, Line = i + 1,
                                Kind = FileKind.Folder, Exists = false, Note = "AtsEX本体",
                            });
                            missing.Warnings.Add("AtsEX本体のフォルダが見つかりません");
                        }
                        break; // 使うのは最初の1行だけ
                    }
                }
                catch (Exception ex)
                {
                    txtNode.Warnings.Add("読み込めません: " + ex.Message);
                }
            }

            // AtsEXプラグインの一覧
            string xml = Path.Combine(dir, dllBase + ".VehiclePluginUsing.xml");
            if (File.Exists(xml))
            {
                var xmlNode = caller.AddChild(new RefNode
                {
                    FullPath = xml, RawValue = Path.GetFileName(xml), Exists = true,
                    Kind = FileKind.AtsExPluginList,
                });
                explicitRefs.Add(xml);
                Expand(xmlNode, depth + 1);
            }
        }

        /// <summary>
        /// 車両ファイルと同じフォルダにあるAtsEXの車両プラグイン一覧
        /// (VehiclePluginUsing.xml、または「車両ファイル名.VehiclePluginUsing.xml」)を付ける。
        /// </summary>
        private void AttachVehiclePluginUsing(RefNode vehicle, int depth)
        {
            string dir = Path.GetDirectoryName(vehicle.FullPath);
            string vehicleBase = Path.GetFileNameWithoutExtension(vehicle.FullPath);
            foreach (string name in new[] { vehicleBase + ".VehiclePluginUsing.xml", "VehiclePluginUsing.xml" })
            {
                string xml = Path.Combine(dir, name);
                if (!File.Exists(xml)) continue;
                var xmlNode = vehicle.AddChild(new RefNode
                {
                    FullPath = xml, RawValue = name, Exists = true,
                    Kind = FileKind.AtsExPluginList,
                    Note = "車両ファイルと同じフォルダ",
                });
                explicitRefs.Add(xml);
                Expand(xmlNode, depth + 1);
            }
        }

        private static readonly Regex AtsExPathRegex = new Regex(
            @"<\s*(?<elem>[A-Za-z_][\w\.]*)[^>]*?\bPath\s*=\s*""(?<path>[^""]+)""", RegexOptions.Compiled);

        /// <summary>
        /// VehiclePluginUsing.xml を読む。例:
        ///   &lt;Assembly Path="AtsExPlugins\CityOneman.dll" /&gt;
        /// パスはxmlのフォルダ基準。&lt;!-- --&gt; のコメントは読み飛ばす。
        /// </summary>
        private void ParseAtsExPluginUsing(RefNode node, BveTextFile text, int depth)
        {
            string baseDir = Path.GetDirectoryName(node.FullPath);
            bool inComment = false;
            for (int i = 0; i < text.Lines.Length; i++)
            {
                if ((i & 4095) == 0) Token.ThrowIfCancellationRequested();
                string line = StripXmlComment(text.Lines[i], ref inComment);
                foreach (Match m in AtsExPathRegex.Matches(line))
                    AddReference(node, m.Groups["path"].Value, m.Groups["elem"].Value, null, i + 1,
                                 baseDir, null, true, depth);
            }
        }

        /// <summary>
        /// XMLの行から &lt;!-- --&gt; のコメント部分を取り除く。複数行にまたがるコメントは inComment で引き継ぐ。
        /// </summary>
        private static string StripXmlComment(string line, ref bool inComment)
        {
            var sb = new System.Text.StringBuilder();
            int pos = 0;
            while (pos < line.Length)
            {
                if (inComment)
                {
                    int end = line.IndexOf("-->", pos, StringComparison.Ordinal);
                    if (end < 0) break;
                    inComment = false; pos = end + 3;
                }
                else
                {
                    int start = line.IndexOf("<!--", pos, StringComparison.Ordinal);
                    if (start < 0) { sb.Append(line, pos, line.Length - pos); break; }
                    sb.Append(line, pos, start - pos);
                    inComment = true; pos = start + 4;
                }
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        // PluginConverter
        // ------------------------------------------------------------------

        /// <summary>PluginConverterのDLL(PluginConverter.dll、PluginConverter2.dll、PluginConverter_A0.dll など)なら、それが読むXMLの名前を返す。</summary>
        private static string PluginConverterConfigName(string dllPath)
        {
            string name = Path.GetFileNameWithoutExtension(dllPath);
            return Path.GetExtension(dllPath).Equals(".dll", StringComparison.OrdinalIgnoreCase)
                   && name.StartsWith("PluginConverter", StringComparison.OrdinalIgnoreCase)
                ? name + ".xml" : null;
        }

        private static readonly Regex PluginConverterPathRegex = new Regex(
            @"<\s*Path\s*>\s*(?<path>[^<]*?)\s*<\s*/\s*Path\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// PluginConverter.xml を読む。&lt;DLL&gt;&lt;Path&gt;Cast\CastPanelDx.dll&lt;/Path&gt;&lt;/DLL&gt; に書かれたDLLを
        /// ATSプラグインとしてたどる(パスはXMLのフォルダ基準)。&lt;!-- --&gt; のコメントは読み飛ばす。
        /// </summary>
        private void ParsePluginConverterXml(RefNode node, BveTextFile text, int depth)
        {
            string baseDir = Path.GetDirectoryName(node.FullPath);
            bool inComment = false;
            for (int i = 0; i < text.Lines.Length; i++)
            {
                if ((i & 4095) == 0) Token.ThrowIfCancellationRequested();
                string line = StripXmlComment(text.Lines[i], ref inComment);
                foreach (Match m in PluginConverterPathRegex.Matches(line))
                {
                    string p = m.Groups["path"].Value;
                    if (p.Length > 0) AddReference(node, p, "DLL", null, i + 1, baseDir, null, true, depth);
                }
            }
        }

        /// <summary>PluginOrganizerのDLLなら、それが読む設定ファイル名を返す(それ以外はnull)。</summary>
        private static string PluginOrganizerConfigName(string dllPath)
        {
            string name = Path.GetFileNameWithoutExtension(dllPath);
            if (name.Equals("PluginOrganizer", StringComparison.OrdinalIgnoreCase)) return "Instructions.yml";
            if (name.Equals("PluginOrganizer64", StringComparison.OrdinalIgnoreCase)) return "Instructions64.yml";
            return null;
        }

        /// <summary>YAMLのコメント(行頭か空白の後の「#」以降)を外す。引用符の中の「#」は残す。</summary>
        private static string StripYamlComment(string line)
        {
            char quote = '\0';
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
                if (c == '"' || c == '\'') { quote = c; continue; }
                if (c == '#' && (i == 0 || char.IsWhiteSpace(line[i - 1]))) return line.Substring(0, i);
            }
            return line;
        }

        /// <summary>「キー: 値」の区切りの「:」の位置(後ろに空白か行末が続くもの)。無ければ-1。</summary>
        private static int FindYamlColon(string s)
        {
            char quote = '\0';
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
                if ((c == '"' || c == '\'') && i == 0) { quote = c; continue; }
                if (c == ':' && (i + 1 == s.Length || s[i + 1] == ' ' || s[i + 1] == '\t')) return i;
            }
            return -1;
        }

        private static bool LooksLikeFolder(string token)
        {
            if (token.Length > 260 || token.Contains("://")) return false;
            return token == "." || token == ".." || token.Contains("\\") || token.Contains("/");
        }

        private static string Resolve(string raw, string baseDir)
        {
            try
            {
                if (raw.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return null;
                string p = raw.Replace('/', '\\');
                return Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(baseDir, p));
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}

namespace BveRefViewer
{
    /// <summary>読み込みの進み具合(画面のステータスバーに出す)。</summary>
    internal sealed class ScanProgress
    {
        /// <summary>中身を読んだファイルの数。</summary>
        public int Files;
        /// <summary>見つけた参照の数。</summary>
        public int References;
        /// <summary>いま読んでいるファイル。</summary>
        public string Current;
        /// <summary>「未参照ファイルを調べています」など、参照たどり以外の段階の説明。</summary>
        public string Phase;
    }
}
