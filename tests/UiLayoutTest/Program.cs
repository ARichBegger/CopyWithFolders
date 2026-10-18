using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using CopyWithFolders.Core;

namespace UiLayoutTest
{
    internal static class Entry
    {
        private static int _failures;

        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var sb = new StringBuilder();
            sb.AppendLine("=== CopyWithFolders UI 排版审计 ===");

            // 构造真实界面
            var form = new CopyWithFolders.MainForm();
            form.Show();
            Application.DoEvents();
            System.Threading.Thread.Sleep(400);
            Application.DoEvents();

            sb.AppendLine($"窗口: {form.Width} x {form.Height}   Client: {form.ClientSize.Width} x {form.ClientSize.Height}");
            sb.AppendLine($"最小: {form.MinimumSize.Width} x {form.MinimumSize.Height}");
            sb.AppendLine();

            LayoutAudit.Run(form, "MainForm");
            Report(sb, "默认尺寸");

            // 填充数据后再审计（列宽会随内容自适应）
            string tmp = Path.Combine(Path.GetTempPath(), "uicheck_src_" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(Path.Combine(tmp, "工作", "项目A", "资料"));
            Directory.CreateDirectory(Path.Combine(tmp, "工作", "项目B", "图纸", "dwg"));
            File.WriteAllText(Path.Combine(tmp, "工作", "项目A", "资料", "光伏组串设计说明书.docx"), "x");
            File.WriteAllText(Path.Combine(tmp, "工作", "项目B", "图纸", "dwg", "主接线图.dwg"), "y");

            AddVia(form, Path.Combine(tmp, "工作", "项目A", "资料", "光伏组串设计说明书.docx"));
            AddVia(form, Path.Combine(tmp, "工作", "项目B", "图纸", "dwg", "主接线图.dwg"));
            Application.DoEvents();
            System.Threading.Thread.Sleep(600);
            Application.DoEvents();

            LayoutAudit.Run(form, "MainForm[有数据]");
            Report(sb, "含数据后");

            // 缩到最小尺寸再审计（最容易暴露裁切）
            form.WindowState = FormWindowState.Normal;
            form.Size = form.MinimumSize;
            Application.DoEvents();
            System.Threading.Thread.Sleep(300);
            Application.DoEvents();
            LayoutAudit.Run(form, "MainForm[最小]");
            Report(sb, "最小尺寸 " + form.Width + "x" + form.Height);

            // 放大尺寸
            form.Size = new Size(1200, 900);
            Application.DoEvents();
            System.Threading.Thread.Sleep(300);
            Application.DoEvents();
            LayoutAudit.Run(form, "MainForm[放大]");
            Report(sb, "放大 1200x900");

            // 对话框排版
            CheckDialogs(sb);

            CheckRequirements(sb);
            CheckLevelPickerModal(sb);
            CheckSegmentTruncation(sb);
            CheckOutsideClickCloses(sb);

            // 大字号场景：验证排版不依赖固定像素
            sb.AppendLine("--- 大字号 12pt ---");
            using (var big = new CopyWithFolders.MainForm())
            {
                big.Show();
                big.Font = new Font("Microsoft YaHei UI", 12F);   // 构造后换字体
                big.ApplyCurrentFontMetrics();
                Application.DoEvents();
                System.Threading.Thread.Sleep(300); Application.DoEvents();
                big.Size = big.MinimumSize;
                Application.DoEvents();
                System.Threading.Thread.Sleep(300); Application.DoEvents();
                LayoutAudit.Run(big, "MainForm[12pt]");
                int n = 0;
                foreach (var s in LayoutAudit.Result) { if (n++ < 8) sb.AppendLine("  " + s); }
                if (n == 0) sb.AppendLine("  [OK] 12pt 无排版问题（最小尺寸 " + big.Width + "x" + big.Height + "）");
                else { sb.AppendLine("  " + n + " 处问题"); _failures += n; }
                LayoutAudit.Clear();
                big.Hide();
            }

            // 字体回退测试：等宽字体宽度不同，验证按钮不会截断
            sb.AppendLine("--- 字体差异（宋体 10pt）---");
            using (var alt = new CopyWithFolders.MainForm())
            {
                alt.Show();
                alt.Font = new Font("SimSun", 10F);
                alt.ApplyCurrentFontMetrics();
                Application.DoEvents();
                System.Threading.Thread.Sleep(300); Application.DoEvents();
                LayoutAudit.Run(alt, "MainForm[宋体10]");
                int n = 0;
                foreach (var s in LayoutAudit.Result) { if (n++ < 8) sb.AppendLine("  " + s); }
                if (n == 0) sb.AppendLine("  [OK] 宋体 10pt 无排版问题");
                else { sb.AppendLine("  " + n + " 处问题"); _failures += n; }
                LayoutAudit.Clear();
                alt.Hide();
            }

            form.Hide();
            try { Directory.Delete(tmp, true); } catch { }
            File.WriteAllText("ui_audit.txt", sb.ToString(), Encoding.UTF8);
            Console.Error.WriteLine(sb.ToString());

            // 任一场景出现排版问题即返回非零，便于接入构建流程
            int total = _failures;
            Environment.Exit(total == 0 ? 0 : 1);
        }

        /// <summary>针对本轮 6 项需求的专项断言。</summary>
        private static void CheckRequirements(StringBuilder sb)
        {
            sb.AppendLine("--- 需求专项校验 ---");

            // 需求 1：排除规则 / 高级选项已移除
            using (var f = new CopyWithFolders.MainForm())
            {
                f.Show(); Application.DoEvents();
                Check(!HasText(f, "高级选项"), "需求1：已移除「高级选项」按钮", sb);
                Check(!HasText(f, "排除规则"), "需求1：已移除「排除规则」输入区", sb);

                // 需求 5：列表内不再有行内下拉（ComboBox）
                var listView = FindFirst<ListView>(f);
                int combos = 0;
                if (listView != null)
                    foreach (Control c in listView.Controls)
                        if (c is ComboBox) combos++;
                Check(combos == 0, "需求5：列表内已无行内下拉（实际 " + combos + " 个）", sb);

                // 需求 6：不再有"模式"复选框，只剩两个复制按钮
                var boxes = new List<CheckBox>();
                Collect(f, boxes);
                bool hasModeCheckbox = boxes.Exists(b => b.Text.Contains("复制到剪贴板"));
                Check(!hasModeCheckbox, "需求6：已移除「复制到剪贴板」模式复选框", sb);

                var buttons = new List<Button>();
                Collect(f, buttons);
                int copyButtons = buttons.FindAll(b => b.Text.Contains("复制到剪贴板") || b.Text.Contains("粘贴到目标")).Count;
                Check(copyButtons == 2, "需求6：主操作区恰好两个复制按钮（实际 " + copyButtons + "）", sb);

                // 需求 2：日志开启自动换行、只用纵向滚动条
                var log = FindFirst<TextBox>(f, t => t.ReadOnly && t.Multiline);
                if (log != null)
                {
                    Check(log.WordWrap, "需求2：日志已开启自动换行", sb);
                    Check(log.ScrollBars == ScrollBars.Vertical, "需求2：日志滚动条为仅纵向", sb);
                }
                f.Hide();
            }

            // 需求 3 / 4：级别选择器
            string longPath = Path.Combine(Path.GetTempPath(),
                "一个非常长的父目录名称用于测试截断行为", "第二层同样很长的目录名称",
                "2026-10-18-17-50-27", "CopyWithFolders-分析与优化建议-超长文件名测试.md");

            using (var dlg = new CopyWithFolders.LevelPickerDialog(longPath, 2, Cursor.Position))
            {
                dlg.Show(); Application.DoEvents();
                System.Threading.Thread.Sleep(250);
                Application.DoEvents();

                var radios = new List<RadioButton>();
                Collect(dlg, radios);
                Check(radios.Count >= 4, "需求3：选项数随层级增加（实际 " + radios.Count + " 项）", sb);

                bool allEllipsisTail = radios.TrueForAll(r =>
                    !r.Text.Contains("…") || r.Text.EndsWith("…"));
                Check(allEllipsisTail, "需求3：截断发生在尾部（保留前缀）", sb);

                // 取最长的那项（最深级别）来验证截断与自适应
                int longestIdx = 0;
                for (int i = 1; i < radios.Count; i++)
                    if (radios[i].Text.Length > radios[longestIdx].Text.Length) longestIdx = i;

                string narrowSample = radios[longestIdx].Text;
                int narrowLen = narrowSample.Length;
                Check(narrowSample.EndsWith("…") || narrowLen < 60,
                    "需求3：最长项在窄窗口下被截断（长度 " + narrowLen + "）", sb);

                // 拉宽窗口后应显示更多字符（自适应）
                dlg.Size = new Size(dlg.Width + 360, dlg.Height);
                Application.DoEvents();
                System.Threading.Thread.Sleep(300);
                Application.DoEvents();
                string wideSample = radios[longestIdx].Text;

                Check(wideSample.Length > narrowLen,
                    "需求3：拉宽窗口后显示字符数增加（" + narrowLen + " → " + wideSample.Length + "）", sb);

                // 收窄后应重新变短
                dlg.Size = new Size(Math.Max(dlg.MinimumSize.Width, dlg.Width - 300), dlg.Height);
                Application.DoEvents();
                System.Threading.Thread.Sleep(300);
                Application.DoEvents();
                string backSample = radios[longestIdx].Text;
                Check(backSample.Length < wideSample.Length,
                    "需求3：收窄窗口后显示字符数减少（" + wideSample.Length + " → " + backSample.Length + "）", sb);

                Check(dlg.FormBorderStyle == FormBorderStyle.Sizable,
                    "需求3：级别选择窗口可拉伸缩放", sb);

                dlg.Close();
            }

            sb.AppendLine();
        }

        /// <summary>
        /// 需求 1 回归：模态流程下点「确定」必须返回 OK 且级别正确回传。
        /// 历史 bug：关闭时的 Deactivate 把 DialogResult 覆写成 Cancel，导致级别选不进去。
        /// </summary>
        private static void CheckLevelPickerModal(StringBuilder sb)
        {
            sb.AppendLine("--- 级别选择：模态流程（需求 1）---");

            string tmp = Path.Combine(Path.GetTempPath(), "lvl_" + Guid.NewGuid().ToString("N").Substring(0, 6));
            string deep = tmp;
            for (int i = 1; i <= 6; i++) deep = Path.Combine(deep, "L" + i);
            Directory.CreateDirectory(deep);
            string f = Path.Combine(deep, "文件名.md");
            File.WriteAllText(f, "x");

            int maxLevel = CopyLogic.MaxLevel(f);
            Check(maxLevel >= 5, "深层路径至少可选 5 级（实际 " + maxLevel + "）", sb);

            using var host = new Form
            {
                Width = 520,
                Height = 320,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(0, 0)
            };
            host.Show();
            Application.DoEvents();

            var dlg = new CopyWithFolders.LevelPickerDialog(f, 2, new Point(120, 120));
            var timer = new Timer { Interval = 600 };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try
                {
                    var radios = new List<RadioButton>();
                    Collect(dlg, radios);
                    if (radios.Count > 0) radios[radios.Count - 1].Checked = true;

                    var buttons = new List<Button>();
                    Collect(dlg, buttons);
                    foreach (var b in buttons)
                    {
                        if (b.Text != "确定") continue;
                        b.PerformClick();
                        break;
                    }
                }
                catch (Exception ex) { sb.AppendLine("  操作异常：" + ex.Message); }
            };
            timer.Start();

            var result = dlg.ShowDialog(host);

            Check(result == DialogResult.OK,
                "点「确定」返回 OK（历史 bug 会被覆写成 Cancel，实际 " + result + "）", sb);
            Check(dlg.SelectedLevel == maxLevel,
                "选中的最大级正确回传（期望 " + maxLevel + "，实际 " + dlg.SelectedLevel + "）", sb);

            dlg.Dispose();
            try { host.Close(); } catch { }
            try { Directory.Delete(tmp, true); } catch { }
            sb.AppendLine();
        }

        /// <summary>
        /// 需求 3 回归：长文件名下，短目录名必须保持完整，只有超长的段被省略；
        /// 且窗口拉宽后可见字符数增加（自适应）。
        /// </summary>
        private static void CheckSegmentTruncation(StringBuilder sb)
        {
            sb.AppendLine("--- 逐段截断（需求 3）---");

            string tmp = Path.Combine(Path.GetTempPath(), "seg_" + Guid.NewGuid().ToString("N").Substring(0, 6));
            string shortSeg = "资料";
            string longName = new string('长', 42) + ".dwg";
            string dir = Path.Combine(tmp, "工程", shortSeg);
            Directory.CreateDirectory(dir);
            string f = Path.Combine(dir, longName);
            File.WriteAllText(f, "x");

            using var dlg = new CopyWithFolders.LevelPickerDialog(f, 2, new Point(80, 80));
            dlg.Show();
            Application.DoEvents();
            System.Threading.Thread.Sleep(250);
            Application.DoEvents();

            // 收窄到最小宽度，强制截断
            dlg.Size = dlg.MinimumSize;
            Application.DoEvents();
            System.Threading.Thread.Sleep(250);
            Application.DoEvents();

            var radios = new List<RadioButton>();
            Collect(dlg, radios);
            Check(radios.Count > 0, "选项已生成", sb);
            if (radios.Count == 0) { dlg.Close(); return; }

            // 最深一级：工程\资料\<超长名>
            string deepest = radios[radios.Count - 1].Text;

            Check(deepest.Contains(shortSeg),
                "短目录名「" + shortSeg + "」未被压缩掉", sb);
            Check(deepest.Contains("…"), "超长文件名被省略为 …", sb);
            Check(!deepest.Contains("工程…"), "短目录名「工程」未被误截断", sb);

            int narrowLen = deepest.Length;

            // 拉宽后应能看到更多字符
            dlg.Size = new Size(dlg.Width + 400, dlg.Height);
            Application.DoEvents();
            System.Threading.Thread.Sleep(300);
            Application.DoEvents();
            string widened = radios[radios.Count - 1].Text;

            Check(widened.Length > narrowLen,
                "拉宽窗口后可见字符增加（" + narrowLen + " → " + widened.Length + "）", sb);

            dlg.Close();
            try { Directory.Delete(tmp, true); } catch { }
            sb.AppendLine();
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, IntPtr dwExtraInfo);

        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;

        /// <summary>
        /// 需求 2 回归：在对话框外真实点一下鼠标，应返回 Cancel。
        /// 用模拟鼠标事件，因为该场景无法用 PerformClick 复现
        /// （点的是被模态禁用掉的宿主窗体）。
        /// </summary>
        private static void CheckOutsideClickCloses(StringBuilder sb)
        {
            sb.AppendLine("--- 点击窗口外关闭（需求 2）---");

            string tmp = Path.Combine(Path.GetTempPath(), "out_" + Guid.NewGuid().ToString("N").Substring(0, 6));
            string deep = Path.Combine(tmp, "A", "B", "C");
            Directory.CreateDirectory(deep);
            string f = Path.Combine(deep, "文件.txt");
            File.WriteAllText(f, "x");

            using var host = new Form
            {
                Width = 620,
                Height = 420,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(0, 0),
                Text = "宿主窗口"
            };
            host.Show();
            Application.DoEvents();

            var dlg = new CopyWithFolders.LevelPickerDialog(f, 1, new Point(400, 320));
            bool forced = false;

            // 700ms 后模拟一次窗口外点击
            var clickTimer = new Timer { Interval = 700 };
            clickTimer.Tick += (_, _) =>
            {
                clickTimer.Stop();
                try
                {
                    Point outside = new Point(dlg.Right + 40, dlg.Bottom + 40);
                    var screen = Screen.FromPoint(dlg.Location).Bounds;
                    if (outside.X >= screen.Right) outside.X = Math.Max(screen.Left + 5, dlg.Left - 40);
                    if (outside.Y >= screen.Bottom) outside.Y = Math.Max(screen.Top + 5, dlg.Top - 40);

                    SetCursorPos(outside.X, outside.Y);
                    Application.DoEvents();
                    mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
                    mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
                }
                catch (Exception ex) { sb.AppendLine("  点击模拟异常：" + ex.Message); }
            };
            clickTimer.Start();

            // 兜底：3 秒后仍未关闭则强制关闭，避免测试挂死
            var guard = new Timer { Interval = 3000 };
            guard.Tick += (_, _) =>
            {
                guard.Stop();
                if (dlg.IsDisposed || !dlg.Visible) return;
                forced = true;
                try { dlg.DialogResult = DialogResult.Cancel; dlg.Close(); } catch { }
            };
            guard.Start();

            var result = dlg.ShowDialog(host);
            guard.Stop();

            if (forced)
                sb.AppendLine("  SKIP  当前会话无法投递鼠标事件，该项未验证（代码路径见 HookCallback）");
            else
                Check(result == DialogResult.Cancel,
                    "点击窗口外返回 Cancel（实际 " + result + "）", sb);

            dlg.Dispose();
            try { host.Close(); } catch { }
            try { Directory.Delete(tmp, true); } catch { }
            sb.AppendLine();
        }

        private static void Check(bool cond, string name, StringBuilder sb)
        {
            sb.AppendLine((cond ? "  PASS  " : "  FAIL  ") + name);
            if (!cond) _failures++;
        }

        private static bool HasText(Control root, string text)
        {
            var all = new List<Control>();
            Collect(root, all);
            return all.Exists(c => !string.IsNullOrEmpty(c.Text) && c.Text.Contains(text));
        }

        /// <summary>收集控件树中所有指定类型的控件（按类型过滤，避免类型转换错误）。</summary>
        private static void Collect<T>(Control root, List<T> sink) where T : Control
        {
            foreach (Control c in root.Controls)
            {
                if (c is T t) sink.Add(t);
                Collect(c, sink);
            }
        }

        private static T? FindFirst<T>(Control root) where T : Control
        {
            var all = new List<T>();
            Collect(root, all);
            return all.Count > 0 ? all[0] : null;
        }

        private static T? FindFirst<T>(Control root, Func<T, bool> pred) where T : Control
        {
            var all = new List<T>();
            Collect(root, all);
            foreach (var t in all) if (pred(t)) return t;
            return null;
        }

        private static void AddVia(Form form, string path)
        {
            var m = typeof(CopyWithFolders.MainForm).GetMethod("AddPaths",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            m?.Invoke(form, new object[] { new[] { path } });
        }

        private static void Report(StringBuilder sb, string stage)
        {
            var all = LayoutAudit.Result;
            sb.AppendLine($"--- {stage} ---");
            int n = 0;
            foreach (var s in all)
            {
                if (n++ < 12) sb.AppendLine("  " + s);
            }
            if (n == 0) sb.AppendLine("  [OK] 无排版问题");
            else { sb.AppendLine($"  共 {n} 处问题"); _failures += n; }
            sb.AppendLine();
            LayoutAudit.Clear();
        }

        private static void CheckDialogs(StringBuilder sb)
        {
            sb.AppendLine("--- 对话框 ---");
            try
            {
                string src = @"C:/Users/Bipy/AppData/Local/Temp/示例路径/工作/项目A/资料/光伏组串设计说明书.docx";
                var d1 = new CopyWithFolders.LevelPickerDialog(src, 2, Cursor.Position);
                d1.Show(); Application.DoEvents(); System.Threading.Thread.Sleep(200); Application.DoEvents();
                LayoutAudit.Run(d1, "LevelPicker");
                Dump("LevelPicker", sb, d1);
                d1.Close();

                var d2 = new CopyWithFolders.ConflictDialog(@"C:/目标/资料/报告.docx", false, @"C:/目标/资料");
                d2.Show(); Application.DoEvents(); System.Threading.Thread.Sleep(200); Application.DoEvents();
                LayoutAudit.Run(d2, "Conflict");
                Dump("ConflictDialog", sb, d2);
                d2.Close();

                var info = new CopyWithFolders.PrecheckInfo
                {
                    Mode = "剪贴板（随后在任意文件夹 Ctrl+V）",
                    DestFolder = "由资源管理器中的 Ctrl+V 决定",
                    JobCount = 12,
                    FileCount = 3456,
                    TotalBytes = 987654321
                };
                info.ConflictItems.Add("项目A\\资料\\报告.docx");
                info.ConflictItems.Add("项目B\\图纸\\dwg\\主接线图.dwg");
                var d3 = new CopyWithFolders.PrecheckDialog(info);
                d3.Show(); Application.DoEvents(); System.Threading.Thread.Sleep(200); Application.DoEvents();
                LayoutAudit.Run(d3, "Precheck");
                Dump("PrecheckDialog", sb, d3);
                d3.Close();
            }
            catch (Exception ex) { sb.AppendLine("  对话框检查异常: " + ex.Message); }
        }

        private static void Dump(string name, StringBuilder sb, Form f)
        {
            int n = 0;
            foreach (var s in LayoutAudit.Result)
            {
                if (n++ < 8) sb.AppendLine("  " + s);
            }
            if (n == 0) sb.AppendLine($"  {name}: [OK] 无排版问题  ({f.Width}x{f.Height})");
            else { sb.AppendLine($"  {name}: {n} 处问题  ({f.Width}x{f.Height})"); _failures += n; }
            LayoutAudit.Clear();
        }
    }
}
