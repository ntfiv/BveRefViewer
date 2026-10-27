using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BveRefViewer
{
    /// <summary>
    /// メイン画面。左に参照ツリー/一覧/未参照ファイル、右に選択したファイルの中身を表示する。
    /// </summary>
    internal sealed class MainForm : Form
    {
        // ---- 上部ツールバー ----
        private readonly ToolStrip toolStrip = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
        private readonly ToolStripTextBox txtPath = new ToolStripTextBox { AutoSize = false, Width = 520 };
        private readonly ToolStripDropDownButton btnRecent = new ToolStripDropDownButton("最近のファイル");
        private readonly ToolStripButton btnProblems = new ToolStripButton("問題のあるものだけ") { CheckOnClick = true };
        private readonly ToolStripButton btnTextures = new ToolStripButton("テクスチャも確認")
        {
            CheckOnClick = true,
            ToolTipText = "路線のストラクチャー(.x)が使うテクスチャ画像まで確認します。大きな路線では初回の読み込みが遅くなります。使っていないマテリアルの画像も対象になります。",
        };

        // ---- 左側 ----
        private readonly SplitContainer split = new SplitContainer { Dock = DockStyle.Fill };
        private readonly TabControl tabs = new TabControl { Dock = DockStyle.Fill };
        private readonly TreeView tree = new TreeView { Dock = DockStyle.Fill, HideSelection = false, ShowNodeToolTips = true };
        // 一覧は車両側(シナリオ・車両とその参照先)と路線側(路線とその参照先)に分ける
        private readonly ListView vehicleList = NewListView();
        private readonly ListView routeList = NewListView();
        private readonly TabPage tabVehicleList = new TabPage("一覧(車両)");
        private readonly TabPage tabRouteList = new TabPage("一覧(路線)");
        private readonly ListView unrefList = NewListView();
        // ---- 概要(動くBVE・AtsEX・外部ライブラリ) ----
        private readonly Label lblSummary = new Label
        {
            Dock = DockStyle.Top, AutoSize = false, Height = 26, Padding = new Padding(6, 5, 4, 0),
            AutoEllipsis = true, Cursor = Cursors.Hand, BackColor = Color.FromArgb(236, 242, 250),
            Text = "シナリオか車両ファイルを開くと、対応するBVE・AtsEX・外部ライブラリの要約がここに出ます。",
        };
        private readonly TextBox txtSummary = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
            BackColor = SystemColors.Window, BorderStyle = BorderStyle.None,
        };
        private readonly ListView libList = NewListView();
        private readonly List<DataSummary.LibraryDef> libraryDefs = DataSummary.LoadLibraryDefs();
        private TabPage tabSummary;

        private readonly Label unrefNote = new Label
        {
            Dock = DockStyle.Top, AutoSize = false, Height = 36, Padding = new Padding(4),
            ForeColor = SystemColors.GrayText,
            Text = "車両ファイルのフォルダ内で、どこからも参照されていないファイル。\r\nプラグインが独自に読み込むファイルもここに出ることがあります。",
        };

        // ---- 右側(プレビュー) ----
        private readonly TextBox txtInfo = new TextBox
        {
            Dock = DockStyle.Top, Multiline = true, ReadOnly = true, Height = 128,
            ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, BackColor = SystemColors.Window,
        };
        private readonly FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 2, 0, 2) };
        private readonly Button btnGoParent = new Button { Text = "参照元の行を表示", AutoSize = true };
        private readonly Button btnExplorer = new Button { Text = "フォルダを開く", AutoSize = true };
        private readonly Button btnOpen = new Button { Text = "既定のアプリで開く", AutoSize = true };
        private readonly Button btnCopy = new Button { Text = "パスをコピー", AutoSize = true };
        private readonly Panel contentHost = new Panel { Dock = DockStyle.Fill };

        private readonly RichTextBox rtb = new RichTextBox
        {
            Dock = DockStyle.Fill, ReadOnly = true, WordWrap = false, DetectUrls = false,
            BackColor = SystemColors.Window, BorderStyle = BorderStyle.None, HideSelection = false,
        };
        private readonly Panel imagePanel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.FromArgb(64, 64, 64) };
        private readonly PictureBox picture = new PictureBox { SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Fill, BackColor = Color.Transparent };
        private readonly CheckBox chkActualSize = new CheckBox { Text = "原寸で表示", AutoSize = true, Dock = DockStyle.Top, ForeColor = Color.White, Padding = new Padding(4) };
        private readonly Panel audioPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
        private readonly Label audioInfo = new Label { AutoSize = true, Location = new Point(8, 8) };
        private readonly Button btnPlay = new Button { Text = "▶ 再生", AutoSize = true, Location = new Point(8, 40) };
        private readonly Button btnStop = new Button { Text = "■ 停止", AutoSize = true, Location = new Point(100, 40) };

        private readonly StatusStrip status = new StatusStrip();
        private readonly ToolStripStatusLabel lblStatus = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        // 読み込み中だけ出す: 総数は事前に分からないので、動き続ける形のバー
        private readonly ToolStripProgressBar progressBar = new ToolStripProgressBar
        {
            Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30, Size = new Size(160, 16), Visible = false,
        };
        private readonly ToolStripStatusLabel lnkCancel = new ToolStripStatusLabel
        {
            Text = "キャンセル (Esc)", IsLink = true, Visible = false,
        };

        // ---- 状態 ----
        private RefNode root;
        private readonly Dictionary<RefNode, TreeNode> treeNodeOf = new Dictionary<RefNode, TreeNode>();
        private RefNode selected;
        private string currentFile;

        /// <summary>車両フォルダの外のファイル(BveDcTrainPack-master等)の色。目立たない灰緑色。</summary>
        private static readonly Color ExternalColor = Color.FromArgb(60, 140, 95);

        private static readonly string RecentFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BveRefViewer", "recent.txt");

        public MainForm(string initialPath)
        {
            Text = "BVE 参照ファイルビューア";
            Size = new Size(1400, 860);
            StartPosition = FormStartPosition.CenterScreen;
            AllowDrop = true;
            Font = new Font("Yu Gothic UI", 9F);

            BuildToolStrip();
            BuildLeft();
            BuildRight();

            status.Items.Add(lblStatus);
            status.Items.Add(progressBar);
            status.Items.Add(lnkCancel);
            lnkCancel.Click += (s, e) => CancelLoad();
            KeyPreview = true;
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape && IsLoading) { CancelLoad(); e.Handled = true; }
            };
            Controls.Add(split);
            Controls.Add(toolStrip);
            Controls.Add(status);
            split.SplitterDistance = 620;

            // ドラッグ&ドロップ: フォームだけでなく、ツリー・一覧・プレビューなど全部の部品の上で受け付ける
            // (子の部品はAllowDropを個別に有効にしないと、その上ではドロップできないことがある)
            EnableFileDrop(this);
            // プレビュー欄で入れ替えて表示する部品は、この時点ではまだ画面に載っていないので個別に
            EnableFileDrop(rtb);
            EnableFileDrop(imagePanel);
            EnableFileDrop(audioPanel);
            FormClosed += (s, e) => { AudioPreview.Stop(); picture.Image?.Dispose(); };

            lblStatus.Text = "シナリオファイルか車両ファイル(.txt)を開くか、このウインドウにドラッグ&ドロップしてください。";
            LoadRecent();
            if (!string.IsNullOrEmpty(initialPath)) Shown += (s, e) => LoadFile(initialPath);
        }

        // ==================================================================
        // 画面の組み立て
        // ==================================================================

        private void EnableFileDrop(Control c)
        {
            c.AllowDrop = true;
            c.DragEnter += (s, e) =>
            {
                e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            };
            c.DragDrop += (s, e) =>
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                    BeginInvoke(new Action(() => LoadFile(files[0]))); // ドラッグ元(エクスプローラー)を待たせない
            };
            foreach (Control child in c.Controls) EnableFileDrop(child);
        }

        private static ListView NewListView()
        {
            return new ListView
            {
                Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false,
                MultiSelect = false, GridLines = false,
            };
        }

        private void BuildToolStrip()
        {
            var btnOpenFile = new ToolStripButton("開く...");
            btnOpenFile.Click += (s, e) => ShowOpenDialog();
            var btnReload = new ToolStripButton("再読み込み");
            btnReload.Click += (s, e) => { if (currentFile != null) LoadFile(currentFile); };
            var btnExpandAll = new ToolStripButton("すべて展開");
            btnExpandAll.Click += (s, e) => { tree.BeginUpdate(); tree.ExpandAll(); tree.EndUpdate(); tree.SelectedNode?.EnsureVisible(); };
            var btnCollapse = new ToolStripButton("折りたたむ");
            btnCollapse.Click += (s, e) => { tree.BeginUpdate(); tree.CollapseAll(); ExpandDefault(); tree.EndUpdate(); };
            btnProblems.CheckedChanged += (s, e) => RebuildViews();
            btnTextures.Checked = TexturesOverride ?? ReadSetting("CheckTextures") == "1";
            btnTextures.CheckedChanged += (s, e) =>
            {
                if (TexturesOverride == null) WriteSetting("CheckTextures", btnTextures.Checked ? "1" : "0");
                if (currentFile != null) LoadFile(currentFile); // 確認する範囲が変わるので読み直す
            };
            txtPath.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; LoadFile(txtPath.Text.Trim().Trim('"')); }
            };

            toolStrip.Items.AddRange(new ToolStripItem[]
            {
                btnOpenFile, btnRecent, btnReload, new ToolStripSeparator(),
                new ToolStripLabel("ファイル:"), txtPath, new ToolStripSeparator(),
                btnExpandAll, btnCollapse, btnProblems, new ToolStripSeparator(), btnTextures,
            });
        }

        private void BuildLeft()
        {
            var tabTree = new TabPage("参照ツリー");
            tabTree.Controls.Add(tree);
            tabVehicleList.Controls.Add(vehicleList);
            tabRouteList.Controls.Add(routeList);
            var tabUnref = new TabPage("未参照ファイル");
            tabUnref.Controls.Add(unrefList);
            tabUnref.Controls.Add(unrefNote);

            // 概要タブ: 上に説明文、下に外部ライブラリの一覧(ダブルクリックでツリーの該当箇所へ)
            tabSummary = new TabPage("概要");
            libList.Dock = DockStyle.Bottom;
            libList.Height = 170;
            libList.Columns.Add("状態", 110);
            libList.Columns.Add("外部ライブラリ・共有データ", 200);
            libList.Columns.Add("参照", 55, HorizontalAlignment.Right);
            libList.Columns.Add("見つからない", 85, HorizontalAlignment.Right);
            libList.Columns.Add("フォルダ", 400);
            libList.DoubleClick += (s, e) =>
            {
                if (libList.SelectedItems.Count > 0 && libList.SelectedItems[0].Tag is RefNode n) SelectInTree(n);
            };
            var libNote = new Label
            {
                Dock = DockStyle.Bottom, Height = 20, ForeColor = SystemColors.GrayText,
                Text = "外部ライブラリ・共有データ(ダブルクリックで、そこを参照している箇所をツリーで表示)",
            };
            txtSummary.Font = new Font("MS Gothic", 10F);
            tabSummary.Controls.Add(txtSummary);
            tabSummary.Controls.Add(libNote);
            tabSummary.Controls.Add(libList);

            tabs.TabPages.AddRange(new[] { tabTree, tabSummary, tabVehicleList, tabRouteList, tabUnref });
            split.Panel1.Controls.Add(tabs);
            split.Panel1.Controls.Add(lblSummary);
            lblSummary.Click += (s, e) => tabs.SelectedTab = tabSummary;
            new ToolTip().SetToolTip(lblSummary, "クリックで「概要」タブを表示");

            tree.AfterSelect += (s, e) =>
            {
                if (e.Node?.Tag is RefNode n) ShowPreview(n, 0);
                else if (e.Node?.Tag is SectionGroup g) ShowPreview(g.Owner, g.FirstLine);
            };
            tree.NodeMouseClick += (s, e) => { if (e.Button == MouseButtons.Right) tree.SelectedNode = e.Node; };
            tree.ContextMenuStrip = BuildContextMenu();

            SetupRefList(vehicleList);
            SetupRefList(routeList);

            unrefList.Columns.Add("ファイル", 380);
            unrefList.Columns.Add("サイズ", 90, HorizontalAlignment.Right);
            unrefList.Columns.Add("更新日時", 140);
            unrefList.ColumnClick += (s, e) => SortList(unrefList, e.Column);
            unrefList.SelectedIndexChanged += (s, e) =>
            {
                if (unrefList.SelectedItems.Count > 0 && unrefList.SelectedItems[0].Tag is RefNode n) ShowPreview(n, 0);
            };
            unrefList.ContextMenuStrip = BuildContextMenu();
        }

        private ContextMenuStrip BuildContextMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("フォルダを開く", null, (s, e) => OpenExplorer());
            menu.Items.Add("既定のアプリで開く", null, (s, e) => OpenDefault());
            menu.Items.Add("パスをコピー", null, (s, e) => CopyPath());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("このファイルを起点に開き直す", null, (s, e) =>
            {
                if (selected != null && selected.Exists) LoadFile(selected.FullPath);
            });
            return menu;
        }

        private void BuildRight()
        {
            buttons.Controls.AddRange(new Control[] { btnGoParent, btnExplorer, btnOpen, btnCopy });
            btnGoParent.Click += (s, e) => GoToParentLine();
            btnExplorer.Click += (s, e) => OpenExplorer();
            btnOpen.Click += (s, e) => OpenDefault();
            btnCopy.Click += (s, e) => CopyPath();

            rtb.Font = new Font("MS Gothic", 10F);

            imagePanel.Controls.Add(picture);
            imagePanel.Controls.Add(chkActualSize);
            chkActualSize.CheckedChanged += (s, e) => ApplyImageMode();

            audioPanel.Controls.AddRange(new Control[] { audioInfo, btnPlay, btnStop });
            btnPlay.Click += (s, e) =>
            {
                if (selected == null) return;
                string err = AudioPreview.Play(selected.FullPath);
                if (err != null)
                    MessageBox.Show(this, "このPCでは再生できませんでした。\r\n「既定のアプリで開く」で確認してください。\r\n\r\n" + err,
                        Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            btnStop.Click += (s, e) => AudioPreview.Stop();

            var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4, 0, 0, 0) };
            right.Controls.Add(contentHost);
            right.Controls.Add(buttons);
            right.Controls.Add(txtInfo);
            split.Panel2.Controls.Add(right);
            SetPreviewButtons(false);
        }

        // ==================================================================
        // 読み込み
        // ==================================================================

        private void ShowOpenDialog()
        {
            using (var dlg = new OpenFileDialog
            {
                Title = "シナリオファイル または 車両ファイルを開く",
                Filter = "BVEのファイル (*.txt)|*.txt|すべてのファイル (*.*)|*.*",
            })
            {
                if (currentFile != null) dlg.InitialDirectory = Path.GetDirectoryName(currentFile);
                if (dlg.ShowDialog(this) == DialogResult.OK) LoadFile(dlg.FileName);
            }
        }

        // ---- 読み込み中の状態 ----
        private CancellationTokenSource loadCts;
        private int loadId;
        private Stopwatch loadWatch;
        private string loadingFile;

        /// <summary>読み込み中かどうか(表示確認用の --shot が完了を待つのにも使う)。</summary>
        internal bool IsLoading { get; private set; }

        /// <summary>
        /// ファイルを読み込む。重い路線でも画面が固まらないよう、解析は別スレッドで行い、
        /// ステータスバーに進み具合を出す。キャンセルしたときは前の表示をそのまま残す。
        /// 読み込み中に別のファイルを開いたら、前の読み込みはやめて新しい方を読む。
        /// </summary>
        private async void LoadFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            if (!File.Exists(path))
            {
                MessageBox.Show(this, "ファイルが見つかりません:\r\n" + path, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // 前の読み込みが続いていれば止める
            try { loadCts?.Cancel(); } catch (ObjectDisposedException) { }
            var cts = new CancellationTokenSource();
            loadCts = cts;
            int id = ++loadId;
            bool textures = btnTextures.Checked;
            SetLoading(true, path);
            var progress = new Progress<ScanProgress>(p => { if (id == loadId) ShowProgress(p); });

            try
            {
                // かかった時間を段階ごとに測る(ステータスバーに出して、遅いときの原因が分かるように)
                long scanMs = 0, unrefMs = 0;
                var result = await Task.Run(() =>
                {
                    var t = Stopwatch.StartNew();
                    var scanner = new RefScanner { CheckTextures = textures, Token = cts.Token, Progress = progress };
                    var r = scanner.ScanRoot(path);
                    scanMs = t.ElapsedMilliseconds;
                    t.Restart();
                    var unref = FindUnreferenced(r, cts.Token, progress);
                    unrefMs = t.ElapsedMilliseconds;
                    return (root: r, unref);
                }, cts.Token);
                if (id != loadId) return; // 途中で別のファイルが開かれた

                lblStatus.Text = "画面を作成しています…";
                status.Refresh();
                var ui = Stopwatch.StartNew();
                AudioPreview.Stop();
                root = result.root;
                currentFile = root.FullPath;
                txtPath.Text = currentFile;
                Text = "BVE 参照ファイルビューア - " + Path.GetFileName(currentFile);
                RebuildViews();
                FillUnreferenced(result.unref);
                ShowDataSummary();
                AddRecent(currentFile);
                if (tree.Nodes.Count > 0) tree.SelectedNode = tree.Nodes[0];
                ShowSummary(scanMs, unrefMs, ui.ElapsedMilliseconds, textures);
            }
            catch (OperationCanceledException)
            {
                if (id == loadId)
                {
                    lblStatus.ForeColor = SystemColors.ControlText;
                    lblStatus.Text = "読み込みをキャンセルしました" + (root != null ? "(前の表示のままです)" : "");
                }
            }
            catch (Exception ex)
            {
                if (id == loadId)
                    MessageBox.Show(this, "読み込みに失敗しました:\r\n" + ex, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                if (id == loadId) SetLoading(false, null);
                // 終わった読み込みのトークンは、次の読み込みで止めようとしないよう外してから破棄する
                if (loadCts == cts) loadCts = null;
                cts.Dispose();
            }
        }

        /// <summary>表示確認用(--shot): 保存された設定を使わず、テクスチャ確認のオン/オフを決める。</summary>
        internal static bool? TexturesOverride;

        /// <summary>表示確認用(--shot)で起動しているときはtrue。「最近のファイル」を書き換えない。</summary>
        internal static bool TestMode;

        /// <summary>表示確認用(--shot): キャンセルを押したのと同じ動作。</summary>
        internal void CancelLoadForTest() => CancelLoad();

        /// <summary>表示確認用(--shot): 「概要」タブを表示する。</summary>
        internal void ShowSummaryTabForTest() { tabs.SelectedTab = tabSummary; Refresh(); }

        /// <summary>表示確認用(--shot): 指定した番号のタブ(0始まり)を表示する。</summary>
        internal void ShowTabForTest(int index) { tabs.SelectedIndex = index; Refresh(); }

        /// <summary>表示確認用(--shot): 「再読み込み」を押したのと同じ動作。</summary>
        internal void ReloadForTest() { if (currentFile != null) LoadFile(currentFile); }

        private void CancelLoad()
        {
            if (!IsLoading) return;
            try { loadCts?.Cancel(); } catch (ObjectDisposedException) { }
            lblStatus.Text = "キャンセルしています…";
        }

        private void SetLoading(bool loading, string path)
        {
            IsLoading = loading;
            loadingFile = path;
            progressBar.Visible = loading;
            lnkCancel.Visible = loading;
            UseWaitCursor = false;
            Cursor = loading ? Cursors.AppStarting : Cursors.Default; // 操作はできるので砂時計にはしない
            if (loading)
            {
                loadWatch = Stopwatch.StartNew();
                lblStatus.ForeColor = SystemColors.ControlText;
                lblStatus.Text = "読み込み中… " + Path.GetFileName(path);
            }
        }

        private void ShowProgress(ScanProgress p)
        {
            if (!IsLoading) return;
            double sec = loadWatch?.Elapsed.TotalSeconds ?? 0;
            string what = p.Phase ?? $"ファイル {p.Files:#,0} 個 / 参照 {p.References:#,0} 件";
            lblStatus.Text = $"読み込み中… {sec:0.0} 秒   {what}   {ShortenPath(p.Current)}";
        }

        /// <summary>読み込み中のファイルのフォルダ基準で短く表示する(外ならパスの末尾だけ)。</summary>
        private string ShortenPath(string full)
        {
            if (string.IsNullOrEmpty(full)) return "";
            string baseDir = loadingFile != null ? Path.GetDirectoryName(loadingFile) + "\\" : null;
            if (baseDir != null && full.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase)) return full.Substring(baseDir.Length);
            return full.Length > 70 ? "…" + full.Substring(full.Length - 70) : full;
        }

        /// <summary>動くBVE・AtsEX・外部ライブラリの要約を、ツリーの上の行と「概要」タブに出す。</summary>
        private void ShowDataSummary()
        {
            var sum = DataSummary.Build(root, libraryDefs);
            lblSummary.Text = sum.OneLine();
            bool libMissing = sum.Libraries.Any(l => l.Known && l.Missing > 0);
            lblSummary.ForeColor = libMissing ? Color.Firebrick : SystemColors.ControlText;
            txtSummary.Text = sum.Report(); // AppendLine で作るので、改行はそのままテキストボックスで使える
            txtSummary.Select(0, 0); // 全体が選択された状態で表示されないように

            libList.BeginUpdate();
            libList.Items.Clear();
            foreach (var l in sum.Libraries)
            {
                var item = new ListViewItem(new[]
                {
                    l.Status, l.Known ? l.Name : "(その他) " + l.Name, l.References.ToString(),
                    l.Missing > 0 ? l.Missing.ToString() : "", l.Folder ?? "",
                })
                { Tag = l.First };
                if (l.Missing == l.References) item.ForeColor = Color.Red;
                else if (l.Missing > 0) item.ForeColor = Color.DarkOrange;
                else if (!l.Known) item.ForeColor = SystemColors.GrayText;
                libList.Items.Add(item);
            }
            libList.EndUpdate();
        }

        /// <summary>読み込み後のステータスバー: 車両と路線に分けた件数と、かかった時間。</summary>
        private void ShowSummary(long scanMs, long unrefMs, long uiMs, bool textures)
        {
            var all = root.DescendantsAndSelf().Skip(1).ToList();
            int missing = all.Count(n => !n.Exists);
            int warn = all.Count(n => n.Exists && n.Warnings.Count > 0);
            // 車両側と路線側で分けて数える
            var routeNodes = all.Where(IsInRoute).ToList();
            var vehicleNodes = all.Where(n => !IsInRoute(n)).ToList();
            string Summary(List<RefNode> list) =>
                $"参照 {list.Count:#,0} 件 / 見つからない {list.Count(n => !n.Exists)} 件 / 警告 {list.Count(n => n.Exists && n.Warnings.Count > 0)} 件";
            lblStatus.Text = "車両: " + Summary(vehicleNodes)
                             + $" / 車両フォルダ外 {vehicleNodes.Count(n => n.External)} 件 / 未参照 {unrefList.Items.Count} 個"
                             + (routeNodes.Count > 0 ? "   路線: " + Summary(routeNodes) : "")
                             + (root.DescendantsAndSelf().Any(n => n.UsesAtsEx) ? "   AtsEX使用" : "")
                             + $"   (解析 {scanMs / 1000.0:0.0}秒 + 未参照 {unrefMs / 1000.0:0.0}秒 + 表示 {uiMs / 1000.0:0.0}秒"
                             + (textures ? "・テクスチャ確認あり)" : ")");
            lblStatus.ForeColor = missing > 0 ? Color.Firebrick : warn > 0 ? Color.DarkOrange : SystemColors.ControlText;
        }

        private void RebuildViews()
        {
            if (root == null) return;
            BuildTree();
            BuildList();
        }

        // ==================================================================
        // 参照ツリー
        // ==================================================================

        /// <summary>サウンドファイルの[Run]などをまとめる、ツリー上だけの仮ノード。</summary>
        private sealed class SectionGroup
        {
            public RefNode Owner;
            public int FirstLine;
        }

        private void BuildTree()
        {
            tree.BeginUpdate();
            tree.Nodes.Clear();
            treeNodeOf.Clear();
            var tn = MakeTreeNode(root, btnProblems.Checked);
            if (tn != null) tree.Nodes.Add(tn);
            ExpandDefault();
            tree.EndUpdate();
        }

        private TreeNode MakeTreeNode(RefNode n, bool problemsOnly)
        {
            if (problemsOnly && n.Parent != null && !n.DescendantsAndSelf().Any(d => d.HasProblem)) return null;

            var tn = new TreeNode(NodeText(n)) { Tag = n, ToolTipText = n.FullPath };
            if (!n.Exists) tn.ForeColor = Color.Red;
            else if (n.Warnings.Count > 0) tn.ForeColor = Color.DarkOrange;
            else if (n.External) tn.ForeColor = ExternalColor;
            else if (n.Duplicate || n.Inferred) tn.ForeColor = Color.DimGray;
            if (n.External) tn.ToolTipText += IsInRoute(n) ? "\r\n(路線フォルダの外)" : "\r\n(車両フォルダの外)";
            treeNodeOf[n] = tn;

            // セクションがあるファイルは、セクションごとにまとめる
            bool group = n.Kind != FileKind.Vehicle && n.Kind != FileKind.Scenario && n.Children.Any(c => c.Section != null);
            if (!group)
            {
                foreach (var c in n.Children)
                {
                    var ct = MakeTreeNode(c, problemsOnly);
                    if (ct != null) tn.Nodes.Add(ct);
                }
                return tn;
            }

            foreach (var c in n.Children.Where(c => c.Section == null))
            {
                var ct = MakeTreeNode(c, problemsOnly);
                if (ct != null) tn.Nodes.Add(ct);
            }
            foreach (var g in n.Children.Where(c => c.Section != null)
                                        .GroupBy(c => c.Section, StringComparer.OrdinalIgnoreCase))
            {
                var items = g.Select(c => MakeTreeNode(c, problemsOnly)).Where(x => x != null).ToList();
                if (items.Count == 0) continue;
                int bad = g.Count(c => c.HasProblem);
                var gt = new TreeNode($"[{g.Key}]  {g.Count()} 件" + (bad > 0 ? $"  (問題 {bad} 件)" : ""))
                {
                    Tag = new SectionGroup { Owner = n, FirstLine = g.Min(c => c.Line) },
                    ForeColor = bad > 0 ? Color.Firebrick : Color.SteelBlue,
                };
                gt.Nodes.AddRange(items.ToArray());
                tn.Nodes.Add(gt);
            }
            return tn;
        }

        /// <summary>ツリー・ファイル出力で共通の、ノードの表示文字列。</summary>
        internal static string NodeText(RefNode n)
        {
            var sb = new StringBuilder();
            if (FileKindText.IsContainer(n.Kind)) sb.Append('[').Append(FileKindText.Label(n)).Append("] ");
            if (n.Parent == null) sb.Append(Path.GetFileName(n.FullPath));
            else if (n.Key != null) sb.Append(n.Key).Append(" = ").Append(n.RawValue);
            else sb.Append(n.RawValue);

            if (!n.Exists) sb.Append("   ✗ 見つかりません");
            else if (n.Warnings.Count > 0) sb.Append("   ⚠ ").Append(n.Warnings[0]);
            if (n.Inferred) sb.Append("   (同じフォルダの設定ファイル)");
            if (n.Note != null) sb.Append("   (").Append(n.Note).Append(')');
            if (n.UsesAtsEx) sb.Append("   (AtsEX使用)");
            if (n.Kind == FileKind.AtsPlugin && RefScanner.IsAtsExCaller(n.FullPath)) sb.Append("   (AtsEXの入口)");
            if (n.Duplicate) sb.Append("   (上で展開済み)");

            int bad = n.Children.Count == 0 ? 0 : n.DescendantsAndSelf().Skip(1).Count(d => d.HasProblem);
            if (bad > 0) sb.Append($"   (配下に問題 {bad} 件)");
            return sb.ToString();
        }

        /// <summary>最初は「シナリオ・車両」の階層と、問題のあるノードまでを開いておく。</summary>
        private void ExpandDefault()
        {
            foreach (var kv in treeNodeOf)
            {
                var n = kv.Key;
                if (n.Parent == null || n.Kind == FileKind.Scenario || n.Kind == FileKind.Vehicle)
                    kv.Value.Expand();
                // 路線の中の問題では自動で開かない(数が多くなりがちなので。問題数は路線ノードの表示で分かる)
                if (n.HasProblem && !IsInRoute(n))
                    for (var p = kv.Value.Parent; p != null; p = p.Parent) p.Expand();
            }
            if (tree.Nodes.Count > 0) { tree.Nodes[0].EnsureVisible(); ScrollTreeLeft(); }
        }

        /// <summary>路線(マップ)とその中から参照されているものか。</summary>
        private static bool IsInRoute(RefNode n)
        {
            for (var p = n; p != null; p = p.Parent)
                if (p.Kind == FileKind.Route) return true;
            return false;
        }

        /// <summary>表示確認用: ファイル名の一部が一致する最初のノードを選択する。</summary>
        internal void SelectByName(string part)
        {
            var n = root?.DescendantsAndSelf().FirstOrDefault(
                x => x.RawValue != null && x.RawValue.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0);
            if (n != null) SelectInTree(n);
        }

        /// <summary>表示確認用: ウインドウの見た目をPNGに保存する(画面上の表示をそのまま写す)。</summary>
        internal void SaveScreenshot(string path)
        {
            var r = RectangleToScreen(ClientRectangle);
            using (var bmp = new Bitmap(r.Width, r.Height))
            {
                using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        private void SelectInTree(RefNode n)
        {
            if (!treeNodeOf.TryGetValue(n, out var tn)) return;
            tabs.SelectedIndex = 0;
            tree.SelectedNode = tn;
            tn.EnsureVisible();
            ScrollTreeLeft();
            tree.Focus();
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        /// <summary>長いノードを選ぶとツリーが横にスクロールして左端が切れるので、左端に戻す。</summary>
        private void ScrollTreeLeft()
        {
            const int WM_HSCROLL = 0x0114, SB_LEFT = 6;
            SendMessage(tree.Handle, WM_HSCROLL, (IntPtr)SB_LEFT, IntPtr.Zero);
        }

        // ==================================================================
        // 一覧・未参照ファイル
        // ==================================================================

        private string DisplayPath(string full)
        {
            string baseDir = Path.GetDirectoryName(root.FullPath) + "\\";
            return full.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase) ? full.Substring(baseDir.Length) : full;
        }

        /// <summary>一覧(車両)・一覧(路線)の共通の設定: 列、並べ替え、選択でプレビュー、ダブルクリックでツリーへ。</summary>
        private void SetupRefList(ListView lv)
        {
            lv.Columns.Add("状態", 90);
            lv.Columns.Add("種類", 100);
            lv.Columns.Add("ファイル", 300);
            lv.Columns.Add("参照元", 160);
            lv.Columns.Add("行", 45, HorizontalAlignment.Right);
            lv.Columns.Add("記述", 260);
            lv.ColumnClick += (s, e) => SortList(lv, e.Column);
            lv.SelectedIndexChanged += (s, e) =>
            {
                if (lv.SelectedItems.Count > 0 && lv.SelectedItems[0].Tag is RefNode n) ShowPreview(n, 0);
            };
            lv.DoubleClick += (s, e) =>
            {
                if (lv.SelectedItems.Count > 0 && lv.SelectedItems[0].Tag is RefNode n) SelectInTree(n);
            };
            lv.ContextMenuStrip = BuildContextMenu();
        }

        /// <summary>
        /// 一覧を作る。路線とその中から参照されているものは「一覧(路線)」、それ以外(シナリオ・車両側)は
        /// 「一覧(車両)」に分ける。タブの名前に件数を出す。
        /// </summary>
        private void BuildList()
        {
            var vehicleItems = new List<ListViewItem>();
            var routeItems = new List<ListViewItem>();
            foreach (var n in root.DescendantsAndSelf().Skip(1))
            {
                if (btnProblems.Checked && !n.HasProblem) continue;
                (IsInRoute(n) ? routeItems : vehicleItems).Add(MakeListItem(n));
            }
            FillRefList(vehicleList, vehicleItems);
            FillRefList(routeList, routeItems);
            tabVehicleList.Text = $"一覧(車両 {vehicleItems.Count:#,0})";
            tabRouteList.Text = $"一覧(路線 {routeItems.Count:#,0})";
        }

        private static void FillRefList(ListView lv, List<ListViewItem> items)
        {
            lv.BeginUpdate();
            lv.Items.Clear();
            lv.ListViewItemSorter = null;
            lv.Items.AddRange(items.ToArray()); // 路線は数千件になるので1件ずつではなくまとめて追加する
            lv.EndUpdate();
        }

        private ListViewItem MakeListItem(RefNode n)
        {
            string state = !n.Exists ? "✗ 見つからない" : n.Warnings.Count > 0 ? "⚠ 警告" : n.External ? "外部" : n.Inferred ? "推定" : "OK";
            var item = new ListViewItem(new[]
            {
                state,
                FileKindText.Label(n),
                DisplayPath(n.FullPath),
                n.Parent != null ? Path.GetFileName(n.Parent.FullPath) : "",
                n.Line > 0 ? n.Line.ToString() : "",
                (n.Section != null ? "[" + n.Section + "] " : "") + (n.Key != null ? n.Key + " = " : "") + n.RawValue
                    + (n.Warnings.Count > 0 && n.Exists ? "   ⚠ " + string.Join(" / ", n.Warnings) : ""),
            })
            { Tag = n };
            if (!n.Exists) item.ForeColor = Color.Red;
            else if (n.Warnings.Count > 0) item.ForeColor = Color.DarkOrange;
            else if (n.External) item.ForeColor = ExternalColor;
            return item;
        }

        /// <summary>「未参照ファイル」一覧の1行分(別スレッドで集めて、画面には後でまとめて載せる)。</summary>
        private sealed class UnrefItem
        {
            public RefNode Node;
            public long Size;
            public DateTime Time;
        }

        /// <summary>
        /// 車両フォルダにあって、どこからも参照されていないファイルを集める(別スレッドで実行)。
        /// </summary>
        private static List<UnrefItem> FindUnreferenced(RefNode root, CancellationToken token, IProgress<ScanProgress> progress)
        {
            progress?.Report(new ScanProgress { Phase = "未参照ファイルを調べています" });
            var result = new List<UnrefItem>();
            var referenced = new HashSet<string>(
                root.DescendantsAndSelf().Where(n => n.Exists).Select(n => n.FullPath), StringComparer.OrdinalIgnoreCase);

            // 調べるフォルダ: 車両フォルダ(入れ子になっているものはまとめる)
            var dirs = root.DescendantsAndSelf()
                .Where(n => n.Exists && n.Kind == FileKind.Vehicle)
                .Select(n => (n.VehicleRoot ?? Path.GetDirectoryName(n.FullPath)).TrimEnd('\\') + "\\")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(d => d.Length)
                .ToList();
            var roots = new List<string>();
            foreach (var d in dirs)
                if (!roots.Any(r => d.StartsWith(r, StringComparison.OrdinalIgnoreCase))) roots.Add(d);

            foreach (var dir in roots)
            {
                foreach (var f in EnumerateFilesSafe(dir))
                {
                    if (result.Count % 256 == 0) token.ThrowIfCancellationRequested();
                    if (referenced.Contains(f)) continue;
                    if (result.Count >= 20000) return result;
                    try
                    {
                        var fi = new FileInfo(f);
                        result.Add(new UnrefItem
                        {
                            Node = new RefNode
                            {
                                FullPath = f, RawValue = Path.GetFileName(f), Exists = true,
                                Kind = RefScanner.DetectKind(f, null, FileKind.Unknown),
                            },
                            Size = fi.Length,
                            Time = fi.LastWriteTime,
                        });
                    }
                    catch (Exception) { } // 調べている間に消えたファイルなど
                }
            }
            return result;
        }

        /// <summary>
        /// フォルダ以下のファイルを列挙する。アクセスできないサブフォルダがあっても止まらずに飛ばす
        /// (Directory.EnumerateFiles の AllDirectories は、途中のフォルダで例外が出ると全体が止まるため)。
        /// </summary>
        private static IEnumerable<string> EnumerateFilesSafe(string dir)
        {
            var stack = new Stack<string>();
            stack.Push(dir);
            while (stack.Count > 0)
            {
                string d = stack.Pop();
                string[] files, subs;
                try { files = Directory.GetFiles(d); subs = Directory.GetDirectories(d); }
                catch (Exception) { continue; }
                foreach (var f in files) yield return f;
                foreach (var s in subs) stack.Push(s);
            }
        }

        /// <summary>集めた未参照ファイルを一覧に載せる(画面のスレッドで実行)。</summary>
        private void FillUnreferenced(List<UnrefItem> items)
        {
            unrefList.BeginUpdate();
            unrefList.Items.Clear();
            unrefList.ListViewItemSorter = null;
            foreach (var it in items)
            {
                unrefList.Items.Add(new ListViewItem(new[]
                {
                    DisplayPath(it.Node.FullPath), FormatSize(it.Size), it.Time.ToString("yyyy/MM/dd HH:mm"),
                })
                { Tag = it.Node });
            }
            unrefList.EndUpdate();
        }

        private static void SortList(ListView lv, int column)
        {
            var current = lv.ListViewItemSorter as ColumnSorter;
            bool desc = current != null && current.Column == column && !current.Descending;
            lv.ListViewItemSorter = new ColumnSorter { Column = column, Descending = desc };
        }

        private sealed class ColumnSorter : System.Collections.IComparer
        {
            public int Column;
            public bool Descending;

            public int Compare(object x, object y)
            {
                string a = ((ListViewItem)x).SubItems[Column].Text;
                string b = ((ListViewItem)y).SubItems[Column].Text;
                int r = int.TryParse(a, out int ia) && int.TryParse(b, out int ib)
                    ? ia.CompareTo(ib)
                    : string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase);
                return Descending ? -r : r;
            }
        }

        // ==================================================================
        // プレビュー
        // ==================================================================

        /// <summary>
        /// 選択したファイルのプレビュー。パスに使えない文字を含む参照や、表示中に消されたファイルなどで
        /// 例外が起きても、アプリを落とさずにエラー内容を表示する。
        /// </summary>
        private void ShowPreview(RefNode n, int highlightLine)
        {
            try
            {
                ShowPreviewCore(n, highlightLine);
            }
            catch (Exception ex)
            {
                txtInfo.Text = "表示できません: " + ex.Message + "\r\nパス: " + n?.FullPath;
                ShowText("このファイルはプレビューできません。\n\n" + ex.Message, 0, null);
            }
        }

        private void ShowPreviewCore(RefNode n, int highlightLine)
        {
            AudioPreview.Stop();
            selected = n;
            SetPreviewButtons(true);
            btnGoParent.Enabled = n.Parent != null && n.Line > 0;

            // 未参照ファイル一覧から選んだDLLは、ここでCPU種別を調べる
            if ((n.Kind == FileKind.AtsPlugin || n.Kind == FileKind.Library) && n.Arch == null && n.Exists)
                n.Arch = PeInfo.Read(n.FullPath).Arch;

            var info = new StringBuilder();
            info.AppendLine($"種類: {FileKindText.Label(n)}" + (n.Inferred ? "(プラグインと同じフォルダにあるため推定)" : ""));
            info.AppendLine("パス: " + n.FullPath);
            if (n.VehicleRoot != null) info.AppendLine("車両フォルダ: " + n.VehicleRoot);
            if (n.Kind == FileKind.Vehicle && n.Exists)
                info.AppendLine("AtsEX: " + (n.UsesAtsEx ? "使用している" : "使用していない"));
            if (n.External) info.AppendLine(IsInRoute(n) ? "場所: 路線フォルダの外" : "場所: 車両フォルダの外");
            if (n.RouteRoot != null) info.AppendLine("路線フォルダ: " + n.RouteRoot);
            if (n.Parent != null)
            {
                info.Append($"参照元: {Path.GetFileName(n.Parent.FullPath)}");
                if (n.Line > 0) info.Append($"  {n.Line} 行目");
                if (n.Section != null) info.Append($"  [{n.Section}]");
                info.AppendLine("  " + (n.Key != null ? n.Key + " = " : "") + n.RawValue);
            }
            if (n.Kind == FileKind.Folder)
            {
                if (!n.Exists) info.AppendLine("状態: ✗ フォルダが見つかりません");
                foreach (var w in n.Warnings) info.AppendLine("⚠ " + w);
                txtInfo.Text = info.ToString().TrimEnd();
                if (!n.Exists)
                {
                    ShowText(MissingHelp(n), 0, null);
                    return;
                }
                var sbDir = new StringBuilder("フォルダの中身:\n");
                try
                {
                    foreach (var d in Directory.GetDirectories(n.FullPath)) sbDir.Append("  [") .Append(Path.GetFileName(d)).Append("]\n");
                    foreach (var f in Directory.GetFiles(n.FullPath))
                        sbDir.Append("  ").Append(Path.GetFileName(f)).Append("   ").Append(FormatSize(new FileInfo(f).Length)).Append('\n');
                }
                catch (Exception ex) { sbDir.Append(ex.Message); }
                ShowText(sbDir.ToString(), 0, null);
                return;
            }
            if (n.Exists)
            {
                var fi = new FileInfo(n.FullPath);
                info.AppendLine($"サイズ: {FormatSize(fi.Length)}   更新日時: {fi.LastWriteTime:yyyy/MM/dd HH:mm:ss}");
            }
            else
            {
                info.AppendLine("状態: ✗ ファイルが見つかりません");
            }
            if (n.HeaderType != null || n.EncodingName != null)
                info.AppendLine("ヘッダー: " + (n.HeaderType != null ? $"{n.HeaderType} {n.HeaderVersion}" : "なし")
                                + (n.EncodingName != null ? "   文字コード: " + n.EncodingName : ""));
            foreach (var w in n.Warnings.Where(w => w != "ファイルが見つかりません"))
                info.AppendLine("⚠ " + w);
            txtInfo.Text = info.ToString().TrimEnd();

            if (!n.Exists)
            {
                ShowText(MissingHelp(n), 0, null);
                return;
            }

            string ext = Path.GetExtension(n.FullPath).ToLowerInvariant();
            switch (n.Kind)
            {
                case FileKind.Image:
                    ShowImage(n);
                    return;
                case FileKind.Audio:
                    ShowAudio(n);
                    return;
                case FileKind.AtsPlugin:
                case FileKind.Library:
                case FileKind.AtsExPlugin:
                    ShowText(DescribeDll(n), 0, null);
                    return;
            }
            if (ext == ".x")
            {
                // ストラクチャー: テクスチャの一覧と有無を情報欄に出す(このファイル1つだけなので速い)
                var xi = RefScanner.ReadXTextures(n.FullPath);
                var sbx = new StringBuilder();
                if (xi.Error != null) sbx.Append("\r\nXファイルを読めません: " + xi.Error);
                else if (xi.NotText) sbx.Append($"\r\nテクスチャ: {xi.Format} 形式のため確認できません");
                else
                {
                    string xdir = Path.GetDirectoryName(n.FullPath);
                    sbx.Append($"\r\nテクスチャ ({xi.Textures.Count}):");
                    foreach (var (tex, _) in xi.Textures)
                    {
                        bool ok;
                        try { ok = File.Exists(Path.Combine(xdir, tex)); } catch (Exception) { ok = false; }
                        sbx.Append("\r\n  " + (ok ? "✓ " : "✗ ") + tex);
                    }
                }
                txtInfo.AppendText(sbx.ToString());
                if (xi.NotText || xi.Error != null)
                {
                    ShowText("テキスト形式ではないXファイルのため、中身は表示できません。", 0, null);
                    return;
                }
            }
            if (IsTextLike(ext))
            {
                ShowFileText(n, highlightLine);
                return;
            }
            ShowText("この形式はプレビューできません。「既定のアプリで開く」で確認してください。", 0, null);
        }

        private static bool IsTextLike(string ext)
        {
            switch (ext)
            {
                case ".txt": case ".cfg": case ".ini": case ".csv": case ".xml": case ".json":
                case ".toml": case ".yaml": case ".yml": case ".conf": case ".log": case ".md": case ".x":
                    return true;
                default:
                    return false;
            }
        }

        private void SetPreviewButtons(bool enabled)
        {
            btnGoParent.Enabled = enabled;
            btnExplorer.Enabled = enabled;
            btnOpen.Enabled = enabled;
            btnCopy.Enabled = enabled;
        }

        private void ShowContent(Control c)
        {
            if (contentHost.Controls.Count == 1 && contentHost.Controls[0] == c) return;
            contentHost.Controls.Clear();
            contentHost.Controls.Add(c);
        }

        private void ShowFileText(RefNode n, int highlightLine)
        {
            const int previewBytes = 2 * 1024 * 1024; // これより大きいファイルは先頭だけ表示する(表示が重くなるため)
            BveTextFile text;
            bool truncated;
            try
            {
                text = BveText.Read(n.FullPath, previewBytes);
                truncated = new FileInfo(n.FullPath).Length > previewBytes;
            }
            catch (Exception ex) { ShowText("読み込めません: " + ex.Message, 0, null); return; }

            // 見つからない参照のある行は赤く塗る
            var badLines = new HashSet<int>(n.Children.Where(c => !c.Exists && c.Line > 0).Select(c => c.Line));
            var sb = new StringBuilder();
            for (int i = 0; i < text.Lines.Length; i++)
                sb.Append((i + 1).ToString().PadLeft(5)).Append("  ").Append(text.Lines[i]).Append('\n');
            // 行番号がずれないよう、注意書きは末尾に付ける
            if (truncated)
                sb.Append("\n(ファイルが大きいため、先頭の 2MB だけを表示しています。続きは「既定のアプリで開く」で確認してください)\n");
            ShowText(sb.ToString(), highlightLine, badLines);
        }

        private void ShowText(string text, int highlightLine, HashSet<int> badLines)
        {
            ShowContent(rtb);
            rtb.SuspendLayout();
            rtb.Clear();
            rtb.Text = text;
            if (badLines != null)
                foreach (int line in badLines) ColorLine(line, Color.MistyRose);
            if (highlightLine > 0)
            {
                ColorLine(highlightLine, Color.Khaki);
                int start = rtb.GetFirstCharIndexFromLine(Math.Max(0, highlightLine - 1 - 5));
                // 該当行が画面中央付近に来るよう、少し上の行から表示する
                rtb.Select(rtb.TextLength, 0);
                rtb.ScrollToCaret();
                rtb.Select(Math.Max(0, start), 0);
                rtb.ScrollToCaret();
                rtb.Select(rtb.GetFirstCharIndexFromLine(highlightLine - 1), 0);
            }
            else
            {
                rtb.Select(0, 0);
                rtb.ScrollToCaret();
            }
            rtb.ResumeLayout();
        }

        private void ColorLine(int line, Color color)
        {
            if (line <= 0) return;
            int start = rtb.GetFirstCharIndexFromLine(line - 1);
            if (start < 0) return;
            int next = rtb.GetFirstCharIndexFromLine(line);
            int end = next < 0 ? rtb.TextLength : next - 1;
            rtb.Select(start, Math.Max(0, end - start));
            rtb.SelectionBackColor = color;
        }

        private void ShowImage(RefNode n)
        {
            picture.Image?.Dispose();
            picture.Image = null;
            try
            {
                // ファイルをロックしないよう、メモリに読み込んでから表示する
                using (var ms = new MemoryStream(File.ReadAllBytes(n.FullPath)))
                using (var img = Image.FromStream(ms))
                {
                    picture.Image = new Bitmap(img);
                    txtInfo.AppendText($"\r\n画像: {img.Width} × {img.Height} px   {img.PixelFormat}");
                }
                ApplyImageMode();
                ShowContent(imagePanel);
            }
            catch (Exception)
            {
                ShowText("この画像形式はプレビューできません(" + Path.GetExtension(n.FullPath) + ")。\r\n「既定のアプリで開く」で確認してください。", 0, null);
            }
        }

        private void ApplyImageMode()
        {
            if (chkActualSize.Checked && picture.Image != null)
            {
                picture.Dock = DockStyle.None;
                picture.SizeMode = PictureBoxSizeMode.AutoSize;
                picture.Location = new Point(0, chkActualSize.Height);
            }
            else
            {
                picture.SizeMode = PictureBoxSizeMode.Zoom;
                picture.Dock = DockStyle.Fill;
            }
        }

        private void ShowAudio(RefNode n)
        {
            string ext = Path.GetExtension(n.FullPath).ToLowerInvariant();
            audioInfo.Text = ext == ".wav" ? AudioPreview.DescribeWav(n.FullPath) : ext.TrimStart('.').ToUpperInvariant() + " ファイル";
            ShowContent(audioPanel);
        }

        private static string DescribeDll(RefNode n)
        {
            var pe = PeInfo.Read(n.FullPath);
            var sb = new StringBuilder();
            if (pe.Error != null) return "DLLを解析できません: " + pe.Error;

            sb.AppendLine("CPU: " + pe.Arch + (n.ExpectedArch != null ? $"   (この指定で必要なのは {n.ExpectedArch})" : ""));
            sb.AppendLine(".NETアセンブリ: " + (pe.IsManaged ? "はい" : "いいえ"));
            try
            {
                var v = FileVersionInfo.GetVersionInfo(n.FullPath);
                if (!string.IsNullOrEmpty(v.FileVersion)) sb.AppendLine("ファイルバージョン: " + v.FileVersion);
                if (!string.IsNullOrEmpty(v.ProductName)) sb.AppendLine("製品名: " + v.ProductName);
                if (!string.IsNullOrEmpty(v.CompanyName)) sb.AppendLine("会社名: " + v.CompanyName);
                if (!string.IsNullOrEmpty(v.FileDescription)) sb.AppendLine("説明: " + v.FileDescription);
                if (!string.IsNullOrEmpty(v.LegalCopyright)) sb.AppendLine("著作権: " + v.LegalCopyright);
            }
            catch (Exception) { }

            if (n.Kind == FileKind.AtsPlugin)
            {
                sb.AppendLine();
                sb.AppendLine("ATSプラグインの関数:");
                foreach (var f in PeInfo.AtsFunctions)
                    sb.AppendLine("  " + (pe.Exports.Contains(f) ? "✓ " : "✗ ") + f);
            }
            var others = pe.Exports.Where(e => !PeInfo.AtsFunctions.Contains(e)).ToList();
            if (others.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"その他のエクスポート関数 ({others.Count}):");
                foreach (var e in others.Take(200)) sb.AppendLine("  " + e);
            }
            if (n.Inferred == false && n.Kind == FileKind.AtsPlugin && n.Children.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("同じフォルダの設定ファイルはツリーの子として表示しています。");
            }
            return sb.ToString();
        }

        private static string MissingHelp(RefNode n)
        {
            var sb = new StringBuilder();
            sb.AppendLine(n.Kind == FileKind.Folder ? "フォルダが見つかりません。" : "ファイルが見つかりません。");
            sb.AppendLine();
            sb.AppendLine("探した場所: " + n.FullPath);
            if (n.FullPath.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                sb.AppendLine();
                sb.AppendLine("パスに使えない文字(制御文字など)が含まれています。参照元の記述を確認してください。");
                return sb.ToString();
            }
            string dir = Path.GetDirectoryName(n.FullPath);
            string name = Path.GetFileNameWithoutExtension(n.FullPath);
            if (name.Length == 0)
            {
                // 「Sound\」のようにファイル名の無い指定では、似た名前の候補は探さない
            }
            else if (Directory.Exists(dir))
            {
                // 拡張子違い・似た名前のファイルがあれば候補として出す(打ち間違いの発見用)
                var similar = Directory.GetFiles(dir)
                    .Where(f => Path.GetFileNameWithoutExtension(f).Equals(name, StringComparison.OrdinalIgnoreCase)
                             || Path.GetFileName(f).IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                    .Take(20).ToList();
                if (similar.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("同じフォルダにある似た名前のファイル:");
                    foreach (var f in similar) sb.AppendLine("  " + Path.GetFileName(f));
                }
            }
            else
            {
                sb.AppendLine();
                sb.AppendLine("フォルダ自体がありません: " + dir);
            }
            return sb.ToString();
        }

        internal void GoToParentLine()
        {
            if (selected?.Parent == null || selected.Line <= 0) return;
            var target = selected;
            if (treeNodeOf.TryGetValue(target.Parent, out var tn))
            {
                tabs.SelectedIndex = 0;
                tree.SelectedNode = tn; // AfterSelectで親のプレビューが出るので、その後で行を強調し直す
            }
            ShowPreview(target.Parent, target.Line);
        }

        // ==================================================================
        // ボタン操作
        // ==================================================================

        private void OpenExplorer()
        {
            if (selected == null) return;
            try
            {
                if (File.Exists(selected.FullPath) || Directory.Exists(selected.FullPath))
                {
                    Process.Start("explorer.exe", "/select,\"" + selected.FullPath + "\"");
                    return;
                }
                // 見つからないファイルは、存在する一番近いフォルダを開く
                string dir = Path.GetDirectoryName(selected.FullPath);
                while (dir != null && !Directory.Exists(dir)) dir = Path.GetDirectoryName(dir);
                if (dir != null) Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "フォルダを開けませんでした:\r\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OpenDefault()
        {
            if (selected == null || !File.Exists(selected.FullPath)) return;
            try
            {
                Process.Start(new ProcessStartInfo(selected.FullPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "開けませんでした:\r\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void CopyPath()
        {
            if (selected != null) Clipboard.SetText(selected.FullPath);
        }

        // ==================================================================
        // 最近のファイル
        // ==================================================================

        private List<string> ReadRecent()
        {
            try
            {
                return File.Exists(RecentFile)
                    ? File.ReadAllLines(RecentFile, Encoding.UTF8).Where(l => l.Trim().Length > 0).ToList()
                    : new List<string>();
            }
            catch (Exception) { return new List<string>(); }
        }

        private void LoadRecent()
        {
            btnRecent.DropDownItems.Clear();
            var items = ReadRecent();
            foreach (var p in items)
            {
                string path = p;
                btnRecent.DropDownItems.Add(path, null, (s, e) => LoadFile(path));
            }
            btnRecent.Enabled = items.Count > 0;
            if (txtPath.Text.Length == 0 && items.Count > 0) txtPath.Text = items[0];
        }

        // ==================================================================
        // 設定(「名前=値」を1行ずつ。最近のファイルと同じフォルダに保存)
        // ==================================================================

        private static readonly string SettingsFile = Path.Combine(Path.GetDirectoryName(RecentFile), "settings.txt");

        private static string ReadSetting(string name)
        {
            try
            {
                if (!File.Exists(SettingsFile)) return null;
                foreach (var line in File.ReadAllLines(SettingsFile, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0 && line.Substring(0, eq).Trim() == name) return line.Substring(eq + 1).Trim();
                }
            }
            catch (Exception) { }
            return null;
        }

        private static void WriteSetting(string name, string value)
        {
            try
            {
                var lines = File.Exists(SettingsFile)
                    ? File.ReadAllLines(SettingsFile, Encoding.UTF8).Where(l => !l.StartsWith(name + "=")).ToList()
                    : new List<string>();
                lines.Add(name + "=" + value);
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile));
                File.WriteAllLines(SettingsFile, lines, Encoding.UTF8);
            }
            catch (Exception) { }
        }

        private void AddRecent(string path)
        {
            if (TestMode) return;
            try
            {
                var items = ReadRecent();
                items.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                items.Insert(0, path);
                Directory.CreateDirectory(Path.GetDirectoryName(RecentFile));
                File.WriteAllLines(RecentFile, items.Take(15), Encoding.UTF8);
            }
            catch (Exception) { }
            LoadRecent();
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
            return (bytes / 1024.0 / 1024.0).ToString("0.00") + " MB";
        }
    }
}
