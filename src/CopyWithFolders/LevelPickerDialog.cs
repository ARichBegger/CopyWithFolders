// LevelPickerDialog.cs —— 父结构级别选择器
//
// 设计要点：
//   1) 支持到磁盘根目录的下一级（CopyLogic.BuildLevelOptions 递归到卷根为止）；
//   2) 路径按"每一级目录名"逐段截断：短名字保持完整，长名字各分一份宽度，
//      超出部分以 … 省略；预算随窗口宽度动态重算（拉伸窗口即刷新）；
//   3) 窗口可自由拉伸缩放，布局全部按实测尺寸，不写死像素；
//   4) 点击本窗口以外的任何位置即取消。
//
// 关于"点击窗口外取消"的实现——这里有两个容易踩的坑：
//   · ShowDialog 的宿主窗体被 Windows 禁用，点击它既不会产生鼠标消息、
//     也不会改变前台窗口，因此 Deactivate 与 GetForegroundWindow 都抓不到；
//   · 关闭对话框时本身会触发 Deactivate，若不设"正在关闭"标记，
//     会把用户点「确定」的结果覆写成 Cancel（导致选择不生效）。
// 因此采用"全局鼠标按键轮询"来判定窗口外点击，并用 _closing 标记守住关闭路径。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using CopyWithFolders.Core;

namespace CopyWithFolders
{
    public sealed class LevelPickerDialog : Form
    {
        private const int IndentCap = 6;
        private const int IndentStep = 12;

        private readonly List<(int Level, string Relative)> _options;
        private readonly string _source;
        private readonly List<RadioButton> _radios = new();
        private readonly Label _pathLabel;
        private readonly Label _hint;
        private readonly Panel _scrollHost;
        private readonly FlowLayoutPanel _list;
        private readonly ToolTip _tip = new ToolTip { AutoPopDelay = 15000, InitialDelay = 300 };

        private bool _ready;
        private bool _closing;

        // ---- 全局低级鼠标钩子：判定"点到窗口外" ----
        // 为什么不用 Deactivate / 前台窗口 / GetAsyncKeyState 轮询：
        //   · ShowDialog 的宿主窗体被禁用，点它不产生消息也不改变前台窗口；
        //   · 轮询有采样间隔，快速点击会漏掉；
        //   · 低级钩子在系统把鼠标事件分发给任何窗口之前就会回调，最可靠。
        private const int WH_MOUSE_LL = 14;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_MBUTTONDOWN = 0x0207;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        private IntPtr _hookId = IntPtr.Zero;
        private LowLevelMouseProc? _hookProc;   // 必须保持引用，否则会被 GC 回收导致崩溃

        /// <summary>用户选中的级别；取消时保持原值。</summary>
        public int SelectedLevel { get; private set; }

        public LevelPickerDialog(string source, int currentLevel, Point anchor)
        {
            _source = source ?? "";
            _options = CopyLogic.BuildLevelOptions(_source);
            SelectedLevel = currentLevel;

            // 兜底初始化（无选项时直接取消，避免字段为空）
            _pathLabel = new Label();
            _hint = new Label();
            _scrollHost = new Panel();
            _list = new FlowLayoutPanel();

            if (_options.Count == 0)
            {
                DialogResult = DialogResult.Cancel;
                _closing = true;
                return;
            }

            int maxLv = _options[_options.Count - 1].Level;
            Text = "选择父目录结构（最多 " + maxLv + " 级，可到磁盘根目录）";
            FormBorderStyle = FormBorderStyle.Sizable;   // 可拉伸：窗口放大即可看全路径
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            MinimizeBox = false;
            MaximizeBox = true;
            KeyPreview = true;
            BackColor = SystemColors.Window;
            Font = new Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.Dpi;

            int rowH = UiMetrics.RowHeight(Font);
            int btnH = rowH + 6;

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(10, 8, 10, 8)
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, rowH));   // 源路径
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));     // 选项列表
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, rowH));   // 结果预览
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, btnH));   // 按钮
            Controls.Add(root);

            // ---- 源路径 ----
            _pathLabel = new Label
            {
                Dock = DockStyle.Fill,
                AutoEllipsis = true,
                Text = _source,
                TextAlign = ContentAlignment.MiddleLeft,
                AccessibleName = "待设置级别的完整路径",
                AccessibleRole = AccessibleRole.StaticText
            };
            _tip.SetToolTip(_pathLabel, _source);
            root.Controls.Add(_pathLabel, 0, 0);

            // ---- 选项列表 ----
            _scrollHost = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = SystemColors.Window
            };
            _list = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = false,
                Margin = new Padding(0),
                Padding = new Padding(4)
            };
            _scrollHost.Controls.Add(_list);
            _scrollHost.Resize += (_, _) => RelayoutOptions();
            root.Controls.Add(_scrollHost, 0, 1);

            // ---- 结果预览 ----
            _hint = new Label
            {
                Dock = DockStyle.Fill,
                AutoEllipsis = true,
                ForeColor = SystemColors.GrayText,
                TextAlign = ContentAlignment.MiddleLeft,
                AccessibleName = "粘贴结果预览",
                AccessibleRole = AccessibleRole.StaticText
            };
            root.Controls.Add(_hint, 0, 2);

            // ---- 按钮 ----
            var btnFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Margin = new Padding(0)
            };
            var btnOk = UiMetrics.MakeButton("确定", Font, 80);
            var btnCancel = UiMetrics.MakeButton("取消", Font, 80);
            btnCancel.Click += (_, _) => { _closing = true; DialogResult = DialogResult.Cancel; };
            // 关键：先置 _closing 再设 DialogResult，
            // 否则关闭过程中的 Deactivate 会把结果覆写成 Cancel。
            btnOk.Click += (_, _) => Accept();
            btnFlow.Controls.Add(btnCancel);
            btnFlow.Controls.Add(btnOk);
            root.Controls.Add(btnFlow, 0, 3);
            AcceptButton = btnOk;
            CancelButton = btnCancel;

            // ---- 生成单选项 ----
            for (int i = 0; i < _options.Count; i++)
            {
                int lvl = _options[i].Level;
                var rb = new RadioButton
                {
                    AutoSize = false,
                    Checked = lvl == SelectedLevel,
                    Tag = lvl,
                    Margin = new Padding(0),
                    AccessibleRole = AccessibleRole.RadioButton
                };
                int captured = lvl;
                rb.CheckedChanged += (_, _) =>
                {
                    if (!rb.Checked) return;
                    SelectedLevel = captured;
                    UpdateHint();
                };
                rb.KeyDown += OnOptionKeyDown;
                _radios.Add(rb);
                _list.Controls.Add(rb);
            }

            // ---- 初始尺寸与位置 ----
            int chosenW = Math.Min(820, Math.Max(420, MeasureWidestOption() + 96));
            int rowsVisible = Math.Min(_options.Count, 10);
            int chosenH = rowH * 3 + btnH + rowsVisible * (rowH + 2) + 24;
            ClientSize = new Size(chosenW, chosenH);
            MinimumSize = new Size(460, rowH * 3 + btnH + (rowH + 2) * 2 + 60);

            var wa = Screen.FromPoint(anchor).WorkingArea;
            Location = new Point(
                Math.Max(wa.Left + 6, Math.Min(anchor.X, wa.Right - Width - 6)),
                Math.Max(wa.Top + 6, Math.Min(anchor.Y, wa.Bottom - Height - 6)));

            UpdateHint();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            RelayoutOptions();
            _ready = true;

            InstallMouseHook();

            foreach (var rb in _radios)
            {
                if (!rb.Checked) continue;
                rb.Focus();
                break;
            }
        }

        /// <summary>确认：以当前勾选项为准回写结果。</summary>
        private void Accept()
        {
            if (_closing) return;
            SelectedLevel = CheckedLevel();
            _closing = true;
            try { DialogResult = DialogResult.OK; }
            catch { }
        }

        private int CheckedLevel()
        {
            foreach (var rb in _radios)
                if (rb.Checked && rb.Tag is int lv) return lv;
            return SelectedLevel;
        }

        private void InstallMouseHook()
        {
            if (_hookId != IntPtr.Zero) return;
            try
            {
                _hookProc = HookCallback;
                IntPtr hMod = GetModuleHandle(null);
                _hookId = SetWindowsHookEx(WH_MOUSE_LL, _hookProc, hMod, 0);
            }
            catch
            {
                _hookId = IntPtr.Zero;
            }
        }

        private void UninstallMouseHook()
        {
            if (_hookId == IntPtr.Zero) return;
            try { UnhookWindowsHookEx(_hookId); } catch { }
            _hookId = IntPtr.Zero;
            _hookProc = null;
        }

        /// <summary>钩子回调：鼠标按下且位置在窗口外 → 取消。</summary>
        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0 && !_closing && _ready && !IsDisposed)
                {
                    int msg = wParam.ToInt32();
                    if (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN || msg == WM_MBUTTONDOWN)
                    {
                        var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                        var screenPoint = new Point(info.pt.X, info.pt.Y);
                        if (!Bounds.Contains(screenPoint))
                        {
                            // 不要在钩子回调里直接关窗，交给消息循环处理
                            BeginInvoke(new Action(CancelByOutsideClick));
                        }
                    }
                }
            }
            catch { }
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        internal void CancelByOutsideClick()
        {
            if (_closing || IsDisposed) return;
            _closing = true;
            try
            {
                DialogResult = DialogResult.Cancel;
                if (!IsDisposed) Close();
            }
            catch { }
        }

        /// <summary>
        /// 切换到其他程序 / 桌面时自动取消。
        /// 注意：点被禁用的宿主窗体不会触发这里，那一路由鼠标轮询负责。
        /// </summary>
        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (!_ready || _closing || IsDisposed) return;
            if (!Modal) return;   // 非模态时不自动关闭
            CancelByOutsideClick();
        }

        /// <summary>任何关闭路径都先打标记，防止 Deactivate 覆写 DialogResult。</summary>
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _closing = true;
            base.OnFormClosing(e);
        }

        /// <summary>按当前可用宽度重排每个选项的文字。</summary>
        private void RelayoutOptions()
        {
            if (_radios.Count == 0 || _scrollHost.IsDisposed) return;

            int viewportW = _scrollHost.ClientSize.Width
                            - SystemInformation.VerticalScrollBarWidth - 8;
            if (viewportW < 80) viewportW = 80;

            int rowH = UiMetrics.RowHeight(Font);
            int totalH = 4;
            int maxRowW = 0;

            for (int i = 0; i < _options.Count; i++)
            {
                var (lvl, rel) = _options[i];
                var rb = _radios[i];

                string levelTag = lvl == 0 ? "仅文件" : "含 " + lvl + " 级";
                string prefix = levelTag.PadRight(7, ' ') + "  ";
                int prefixW = Measure(prefix);

                int indent = Math.Min(lvl, IndentCap) * IndentStep;
                // 单选圆点本身占位，必须计入预算，否则文字会被推出可视区而看不到省略号
                int glyphW = Font.Height + 6;
                // 行宽先按整行算；若目录名有可读下限导致超宽，则放宽该行并横向滚动
                int rowW = Math.Max(80, viewportW - 4);
                int roomForPath = rowW - glyphW - indent - prefixW - 6;
                if (roomForPath < 24) roomForPath = 24;

                var fit = FitSegments(rel, roomForPath);
                rb.Text = prefix + fit.Text;
                rb.AccessibleName = "保留到 " + levelTag + "：" + rel;

                // 文字实际占宽 + 级别标签 + 圆点 + 缩进 + 余量
                int needW = glyphW + prefixW + indent + fit.Width + 14;
                rb.Width = Math.Max(rowW, needW);
                maxRowW = Math.Max(maxRowW, rb.Width);
                rb.Height = rowH;
                rb.Padding = new Padding(indent, 0, 0, 0);
                rb.Left = 4;
                rb.Top = totalH;
                totalH = rb.Bottom + 2;

                _tip.SetToolTip(rb, rel);   // 省略后仍可悬停查看完整路径
            }

            // 内容比可视区宽时让容器横向滚动，而不是继续压缩目录名
            _list.Width = Math.Max(viewportW, maxRowW + 8);
            _list.Height = totalH + 4;
        }

        /// <summary>逐段适配结果：显示文本 + 实际占用像素宽度。</summary>
        private readonly struct SegmentFit
        {
            public readonly string Text;
            public readonly int Width;
            public SegmentFit(string text, int width) { Text = text; Width = width; }
        }

        /// <summary>
        /// 按"每一级目录名"逐段截断，而不是把整条路径一刀切。
        /// 分配策略（水填式）：
        ///   1) 天然宽度放得下的段保持完整；
        ///   2) 放不下的段均分剩余宽度，但每段不低于可读下限；
        ///   3) 若所有段都到了下限仍然超宽，就放宽整行，由容器横向滚动，
        ///      而不是继续压缩——避免"目录本身不长却被压成省略号"。
        /// </summary>
        private SegmentFit FitSegments(string rel, int budget)
        {
            if (string.IsNullOrEmpty(rel)) return new SegmentFit("", 0);

            int natural = Measure(rel);
            if (natural <= budget) return new SegmentFit(rel, natural);

            char sepChar = Path.DirectorySeparatorChar;
            string[] segs = rel.Split(sepChar);
            int n = segs.Length;

            const string ell = "…";
            int ellW = Measure(ell);
            int sepW = Measure(sepChar.ToString());

            // 每段可读下限：省略号 + 2 个汉字，保证"看得出来是什么"
            int minSeg = ellW + Measure("宽") * 2;
            if (minSeg < 36) minSeg = 36;

            int avail = budget - sepW * (n - 1);
            if (n <= 1 || avail <= 0)
            {
                string only = TruncateToWidth(rel, budget);
                return new SegmentFit(only, Measure(only));
            }

            var natW = new int[n];
            for (int i = 0; i < n; i++) natW[i] = Measure(segs[i]);

            // 按宽度升序做水填，短段优先拿满
            var order = new List<int>(n);
            for (int i = 0; i < n; i++) order.Add(i);
            order.Sort((a, b) => natW[a].CompareTo(natW[b]));

            var alloc = new int[n];
            int remaining = avail;
            int left = n;
            bool capped = false;

            foreach (int idx in order)
            {
                int fair = left > 0 ? remaining / left : 0;
                if (!capped && natW[idx] <= fair)
                {
                    alloc[idx] = natW[idx];
                    remaining -= natW[idx];
                    left--;
                }
                else
                {
                    // 从这一段起（含更长的段）都只能分到均份额；不低于可读下限
                    capped = true;
                    alloc[idx] = Math.Max(minSeg, fair);
                    remaining -= alloc[idx];
                    left--;
                }
            }

            // 逐段生成显示文本
            var parts = new string[n];
            int totalW = sepW * (n - 1);
            for (int i = 0; i < n; i++)
            {
                if (alloc[i] >= natW[i])
                {
                    parts[i] = segs[i];
                    totalW += natW[i];
                }
                else
                {
                    parts[i] = TruncateToWidth(segs[i], alloc[i]);
                    totalW += Measure(parts[i]);
                }
            }

            string joined = string.Join(sepChar.ToString(), parts);
            return new SegmentFit(joined, Measure(joined));
        }

        /// <summary>把单段文本截断到指定像素宽度：保留前几个字符 + …。</summary>
        private string TruncateToWidth(string text, int availPx)
        {
            const string ell = "…";
            int ellW = Measure(ell);
            if (availPx <= ellW) return ell;
            if (Measure(text) <= availPx) return text;

            int lo = 0, hi = text.Length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (Measure(text.Substring(0, mid)) + ellW <= availPx) lo = mid;
                else hi = mid - 1;
            }
            return lo <= 0 ? ell : text.Substring(0, lo) + ell;
        }

        /// <summary>整条路径的兜底截断（保留前缀）。</summary>
        private string FitPrefix(string text, int availPx)
        {
            if (string.IsNullOrEmpty(text) || availPx <= 0) return "";
            if (Measure(text) <= availPx) return text;
            return TruncateToWidth(text, availPx);
        }

        private int Measure(string s) =>
            TextRenderer.MeasureText(s, Font, new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPadding).Width;

        /// <summary>估算最宽选项所需宽度，用于初始窗口尺寸。</summary>
        private int MeasureWidestOption()
        {
            int max = 0;
            foreach (var (lvl, rel) in _options)
            {
                string levelTag = lvl == 0 ? "仅文件" : "含 " + lvl + " 级";
                string prefix = levelTag.PadRight(7, ' ') + "  ";
                max = Math.Max(max, Measure(prefix + rel) + Math.Min(lvl, IndentCap) * IndentStep + 10);
            }
            return max;
        }

        private void OnOptionKeyDown(object? sender, KeyEventArgs e)
        {
            if (sender is not RadioButton current) return;
            int idx = _radios.IndexOf(current);
            if (idx < 0) return;

            int target = -1;
            switch (e.KeyCode)
            {
                case Keys.Down: target = Math.Min(_radios.Count - 1, idx + 1); break;
                case Keys.Up: target = Math.Max(0, idx - 1); break;
                case Keys.Home: target = 0; break;
                case Keys.End: target = _radios.Count - 1; break;
            }
            if (target < 0) return;

            _radios[target].Checked = true;
            _radios[target].Focus();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        private void UpdateHint()
        {
            if (_hint == null || IsDisposed) return;
            string rel = "";
            try { rel = CopyLogic.BuildRelativePath(_source, SelectedLevel); } catch { }
            _hint.Text = "粘贴后将得到： " + rel;
            _tip.SetToolTip(_hint, rel);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                UninstallMouseHook();
                _tip.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
