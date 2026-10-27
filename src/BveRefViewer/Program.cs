using System;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace BveRefViewer
{
    internal static class Program
    {
        /// <summary>
        /// 起動時の引数にファイルパスがあれば、それを最初に読み込む
        /// (exeにシナリオ/車両ファイルをドラッグ&ドロップして起動できるように)。
        ///
        /// 「BveRefViewer.exe --dump 入力ファイル 出力ファイル」で、画面を出さずに
        /// 参照ツリーをテキストに書き出す(動作確認・差分比較用)。後ろに --textures を付けると .x のテクスチャも確認する。
        /// </summary>
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length >= 3 && args[0] == "--dump")
                return Dump(args[1], args[2], Array.IndexOf(args, "--textures") >= 0);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 「--shot 入力 出力.png [選択するファイル名の一部 または -] [動作] [--textures]」:
            // 画面を画像に保存して終了(表示確認用)。動作は次のどれか。
            //   parent: 選んだファイルの参照元の行を表示してから撮る
            //   during: 読み込み開始の約1秒後(読み込み中の表示)を撮る
            //   cancel: 約1秒後にキャンセルし、終わってから撮る
            //   summary: 「概要」タブを表示して撮る
            //   tab数字: その番号のタブ(0始まり。0=参照ツリー、2=一覧(車両)、3=一覧(路線))を表示して撮る
            //   reload-cancel: 読み込み完了後に再読み込みし、0.5秒後にキャンセルして撮る(前の表示が残るか)
            if (args.Length >= 3 && args[0] == "--shot")
            {
                MainForm.TestMode = true;
                MainForm.TexturesOverride = Array.IndexOf(args, "--textures") >= 0; // 保存された設定に左右されないように
                string select = args.Length >= 4 && args[3] != "-" ? args[3] : null;
                string action = args.Length >= 5 ? args[4] : null;
                var form = new MainForm(args[1]);
                form.Shown += (s, e) =>
                {
                    var started = System.Diagnostics.Stopwatch.StartNew();
                    bool cancelled = false;
                    var t = new Timer { Interval = 200 };
                    t.Tick += (s2, e2) =>
                    {
                        long ms = started.ElapsedMilliseconds;
                        if (action == "cancel" && !cancelled && ms >= 1000) { form.CancelLoadForTest(); cancelled = true; return; }
                        if (action == "reload-cancel" && !cancelled)
                        {
                            if (form.IsLoading || ms < 800) return;
                            form.ReloadForTest();
                            var c = new Timer { Interval = 500 };
                            c.Tick += (s3, e3) => { c.Stop(); form.CancelLoadForTest(); };
                            c.Start();
                            cancelled = true;
                            started.Restart();
                            return;
                        }
                        bool ready = action == "during" ? ms >= 1000 : (!form.IsLoading && ms >= 800);
                        if (!ready && ms < 120000) return;
                        t.Stop();
                        if (select != null) form.SelectByName(select);
                        if (action == "parent") form.GoToParentLine();
                        if (action == "summary") form.ShowSummaryTabForTest();
                        if (action != null && action.StartsWith("tab") && int.TryParse(action.Substring(3), out int tabNo)) form.ShowTabForTest(tabNo);
                        Application.DoEvents();
                        form.SaveScreenshot(args[2]);
                        form.Close();
                    };
                    t.Start();
                };
                Application.Run(form);
                return 0;
            }
            // 想定外のエラーでもアプリごと落とさず、内容を表示して操作を続けられるようにする
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) =>
                MessageBox.Show("エラーが起きました:\r\n" + e.Exception.Message, "BVE 参照ファイルビューア",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Application.Run(new MainForm(args.Length > 0 ? args[0] : null));
            return 0;
        }

        private static int Dump(string input, string output, bool textures)
        {
            var root = new RefScanner { CheckTextures = textures }.ScanRoot(input);
            var sb = new StringBuilder();
            void Write(RefNode n, int depth)
            {
                sb.Append(new string(' ', depth * 2)).Append(MainForm.NodeText(n));
                if (n.Line > 0) sb.Append("   <").Append(n.Line).Append("行目");
                if (n.Section != null) sb.Append(" [").Append(n.Section).Append(']');
                if (n.Line > 0) sb.Append('>');
                if (n.External) sb.Append("   {外部}");
                if (n.VehicleRoot != null) sb.Append("   {車両フォルダ: ").Append(n.VehicleRoot).Append('}');
                sb.AppendLine();
                foreach (var w in n.Warnings.Skip1()) sb.Append(new string(' ', depth * 2 + 4)).AppendLine("⚠ " + w);
                foreach (var c in n.Children) Write(c, depth + 1);
            }
            Write(root, 0);
            sb.AppendLine();
            sb.Append(DataSummary.Build(root, DataSummary.LoadLibraryDefs()).Report());
            File.WriteAllText(output, sb.ToString(), new UTF8Encoding(true));
            return 0;
        }

        /// <summary>先頭(ツリー表示に既に出ている警告)を除いた残りの警告。</summary>
        private static System.Collections.Generic.IEnumerable<string> Skip1(this System.Collections.Generic.List<string> list)
        {
            for (int i = 1; i < list.Count; i++) yield return list[i];
        }
    }
}
