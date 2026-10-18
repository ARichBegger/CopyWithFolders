// MainForm.cs —— 主界面（布局 + 事件编排）
//
// 布局：复制规则区 → 待复制列表 → 操作区（进度）→ 日志
// 交互：拖入添加、行内下拉改级、右键菜单、快捷键、实时进度、可取消
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using CopyWithFolders.Core;

namespace CopyWithFolders
{
    public sealed class MainForm : Form
    {
        // ---- 规则区 ----
        private TextBox _txtDest = null!;
        private ComboBox _cmbPolicy = null!;

        private CheckBox _chkWrap = null!;
        private CheckBox _chkSkipEmpty = null!;
        private CheckBox _chkPreserveTime = null!;

        // ---- 列表 ----
        private ListView _lst = null!;
        private readonly PendingListModel _model = new();
        private readonly List<ListViewItem> _liveRows = new();

        // ---- 操作 ----
        private Button _btnCopy = null!;
        private Button _btnCancel = null!;
        private Button _btnPaste = null!;
        private ProgressBar _bar = null!;
        private Label _lblStatus = null!;

        // ---- 日志 ----
        private TextBox _txtLog = null!;
        private Button _btnLogToggle = null!;
        private Panel _logPanel = null!;

        // ---- 状态 ----
        private AppSettings _settings = new();
        private CopyScheduler? _scheduler;
        private NotifyIcon _tray = null!;
        private bool _exiting;
        private bool _busy;
        private bool _dragHover;

        /// <summary>
        /// 日志是否展开。不能直接用 _logPanel.Visible 判断：
        /// 构造阶段父窗体尚未显示，Visible 恒为 false，会误判成"已收起"。
        /// </summary>
        private bool _logExpanded = true;

        /// <summary>主窗体共用的提示气泡。</summary>
        private readonly ToolTip _tipMain = new ToolTip { AutoPopDelay = 12000, InitialDelay = 400 };

        /// <summary>接收"再次启动时传入路径"的轮询器（单实例场景）。</summary>
        private readonly System.Windows.Forms.Timer _handoffTimer = new System.Windows.Forms.Timer { Interval = 2000 };

        // ---- 字体变化时需要重新测量的对象 ----
        private TableLayoutPanel _rootLayout = null!;
        private TableLayoutPanel? _ruleGrid;
        private TableLayoutPanel? _destGrid;
        private Button? _btnOpenDest;

        private const int RuleLabelCol = 0;
        private const int RuleAdvCol = 2;
        private int _ruleRowHeight = 32;
        private readonly List<Button> _toolbarButtons = new();
        private FlowLayoutPanel? _toolbarFlow;
        private readonly List<Button> _logButtons = new();

        private const int ColPath = 0;
        private const int ColLevel = 1;
        private const int ColRelative = 2;
        private const int ColType = 3;
        private const int ColSize = 4;

        public MainForm() : this(null) { }

        public MainForm(IEnumerable<string>? initialPaths)
        {
            _settings = SettingsStore.Load(out string? loadErr);

            BuildUi();
            ApplySettingsToUi();
            SetupTray();

            // 已运行实例：定期接收"再次启动时传入的路径"（--append 场景）
            _handoffTimer.Tick += (_, _) => ConsumeHandoff();
            _handoffTimer.Start();
            ConsumeHandoff();

            Task.Run(() =>
            {
                try
                {
                    int n = StagingBuilder.CleanupStale();
                    if (n > 0) Log("已清理 " + n + " 个过期暂存目录。");
                }
                catch { }
            });

            if (loadErr != null) Log(loadErr);
            if (SettingsStore.UsingFallback)
                Log("程序目录不可写，设置已改存到：" + SettingsStore.ActivePath);

            Log("就绪。把文件/文件夹加入列表并设置父结构级别，然后点「复制到剪贴板」。");
            Log("随后在任意文件夹按 Ctrl+V 即可粘贴，父目录结构会自动保留。按 F1 查看快捷键。");

            if (initialPaths != null)
            {
                var list = initialPaths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
                if (list.Count > 0)
                {
                    AddPaths(list);
                    Log("已从命令行接收 " + list.Count + " 个路径。");
                }
            }
        }

        // ================= UI 构建 =================

        private void BuildUi()
        {
            Text = "CopyWithFolders - 带父目录结构复制";

            // 字体必须先于任何尺寸测量赋值，否则测量的是默认字体而非界面实际字体
            Font = new Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.Dpi;

            ApplyMinSize();

            // 恢复上次的窗口尺寸（小于最小值时以最小值为准）
            if (_settings.WindowW > 0 && _settings.WindowH > 0)
            {
                Size = new Size(
                    Math.Max(MinimumSize.Width, _settings.WindowW),
                    Math.Max(MinimumSize.Height, _settings.WindowH));
            }

            StartPosition = _settings.WindowX >= 0
                ? FormStartPosition.Manual
                : FormStartPosition.CenterScreen;
            if (_settings.WindowX >= 0) Location = new Point(_settings.WindowX, _settings.WindowY);
            AllowDrop = true;
            KeyPreview = true;
            DoubleBuffered = true;

            DragEnter += OnDragEnter;
            DragLeave += (_, _) => { _dragHover = false; Invalidate(); };
            DragDrop += OnDragDrop;
            KeyDown += OnGlobalKeyDown;

            // 字体被替换（字号变化、DPI 切换、系统主题字体变化）时，
            // 所有按字体实测的尺寸必须重算，否则会出现文字裁切与控件溢出。
            _curFont = Font;
            SystemEvents.UserPreferenceChanged += OnUserPreference;

            BuildLayout();
        }

        private Font _curFont = null!;

        /// <summary>
        /// 强制按当前字体重新排版。字体被外部替换后调用
        /// （构造后设置 Font、主题切换、容器缩放等场景）。
        /// </summary>
        public void ApplyCurrentFontMetrics()
        {
            _curFont = Font;
            RelayoutForFontChange();
        }

        /// <summary>按当前字体重新推导窗口最小尺寸。</summary>
        private void ApplyMinSize()
        {
            int toolbarW = MeasureToolbarWidth();
            int minW = Math.Max(700, toolbarW + 36);
            int rowH = MeasureRowHeight();
            int ruleH = rowH * 3 + 54;          // 规则区（3 行 + 边框）
            int actH = rowH * 2 + 10;           // 操作区（按钮 + 状态行）
            int toolbarH = rowH * 2 + 10;       // 列表工具栏（预留两行，容纳折行）
            // 最小高度 = 规则区 + 操作区 + 工具栏 + 列表 6 行 + 日志 3 行 + 边距
            int minH = ruleH + actH + toolbarH + rowH * 9 + 46;
            MinimumSize = new Size(minW, minH);
            if (Size.Width < minW || Size.Height < minH)
                Size = new Size(Math.Max(minW, Size.Width), Math.Max(minH, Size.Height));
        }

        private void OnUserPreference(object sender, UserPreferenceChangedEventArgs e)
        {
            try
            {
                if (IsDisposed) return;
                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed) return;
                    if (_curFont == null || _curFont.FontFamily != Font.FontFamily
                        || Math.Abs(_curFont.Size - Font.Size) > 0.01f)
                    {
                        _curFont = Font;
                        RelayoutForFontChange();
                    }
                }));
            }
            catch { }
        }

        /// <summary>
        /// 字体变化后重新测量所有按字体推导的尺寸。
        /// 覆盖：窗口最小尺寸、区块行高、标签列宽、按钮宽度、列表列宽、行内下拉高度。
        /// </summary>
        private void RelayoutForFontChange()
        {
            try
            {
                if (_rootLayout != null)
                {
                    int rowH = MeasureRowHeight() + 6;
                    int ruleH = MeasureRowHeight() * 3 + 54;
                    int actH = MeasureRowHeight() * 2 + 10;
                    if (_rootLayout.RowStyles.Count >= 4)
                    {
                        // 只改绝对行，列表 / 日志保持比例分配
                        _rootLayout.RowStyles[0].SizeType = SizeType.Absolute;
                        _rootLayout.RowStyles[0].Height = ruleH;
                        _rootLayout.RowStyles[2].SizeType = SizeType.Absolute;
                        _rootLayout.RowStyles[2].Height = actH;
                        ApplyLogRowLayout();
                    }
                    if (_ruleGrid != null && _ruleGrid.ColumnStyles.Count >= 2)
                    {
                        _ruleGrid.ColumnStyles[RuleLabelCol].SizeType = SizeType.Absolute;
                        _ruleGrid.ColumnStyles[RuleLabelCol].Width =
                            Math.Max(MeasureLabelWidth("重名处理："), MeasureLabelWidth("目标目录："));
                        // 三行行高同步
                        for (int i = 0; i < Math.Min(3, _ruleGrid.RowStyles.Count); i++)
                        {
                            _ruleGrid.RowStyles[i].SizeType = SizeType.Absolute;
                            _ruleGrid.RowStyles[i].Height = rowH;
                        }
                    }
                    _ruleRowHeight = rowH;
                    // 目标目录行的两个按钮宽度依赖字体，需同步重算列宽
                    if (_destGrid != null && _destGrid.ColumnStyles.Count >= 3)
                    {
                        _destGrid.ColumnStyles[1].Width = MeasureButtonWidth("浏览…", 72) + 4;
                        _destGrid.ColumnStyles[2].Width = MeasureButtonWidth("打开", 56) + 4;
                    }
                }

                if (_toolbarFlow != null) _toolbarFlow.PerformLayout();
                foreach (var b in _toolbarButtons) ResizeToolbarButton(b);
                if (_toolbarFlow != null) _toolbarFlow.PerformLayout();
                foreach (var b in _logButtons) ResizeLogButton(b);
                ResizeActionButtons();

                // 必须在按钮重算之后才套用最小尺寸，否则仍按旧按钮宽计算，
                // 工具栏放不下会出现溢出。
                ApplyMinSize();

                AutoResizeColumns();
                PerformLayout();
            }
            catch { }
        }

        private void ResizeToolbarButton(Button b)
        {
            if (string.IsNullOrEmpty(b.Text)) return;
            b.Height = MeasureRowHeight() - 4;
            b.Width = MeasureButtonWidth(b.Text, ToolbarMinWidth(b.Text));
        }

        private int ToolbarMinWidth(string text) => text.Length <= 2 ? 52 : (text.Length <= 4 ? 76 : 90);

        private void ResizeLogButton(Button b)
        {
            if (string.IsNullOrEmpty(b.Text)) return;
            b.Height = MeasureRowHeight() - 4;
            b.Width = MeasureButtonWidth(b.Text, 60);
        }

        private void ResizeActionButtons()
        {
            int h = MeasureRowHeight();
            if (_btnCopy != null) { _btnCopy.Height = h; _btnCopy.Width = MeasureButtonWidth(_btnCopy.Text, 118); }
            if (_btnPaste != null) { _btnPaste.Height = h; _btnPaste.Width = MeasureButtonWidth(_btnPaste.Text, 118); }
            if (_btnCancel != null) { _btnCancel.Height = h; _btnCancel.Width = MeasureButtonWidth(_btnCancel.Text, 66); }
        }

        /// <summary>构建主布局：把四个区块按当前字体的实测尺寸装配起来。</summary>
        private void BuildLayout()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(10),
                ColumnCount = 1,
                RowCount = 4
            };
            // 各区块高度按字体与控件实际尺寸推导，避免固定像素在
            // 大字号 / 高 DPI 下裁切控件文字。
            int ruleH = MeasureRowHeight() * 3 + 54;      // 复制规则区（3 行 + GroupBox 边框与内边距）
            int actH = MeasureRowHeight() * 2 + 10;       // 操作区（按钮行 + 状态行）

            // 规则区与操作区用绝对高度（内容固定），列表与日志按比例分配剩余空间。
            // 若列表也用绝对高度，窗口缩小时两者之和会超出窗口，把操作按钮和日志挤出可视区。
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, ruleH));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 62));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, actH));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
            _rootLayout = root;
            Controls.Add(root);

            root.Controls.Add(BuildRuleGroup(), 0, 0);
            root.Controls.Add(BuildListGroup(), 0, 1);
            root.Controls.Add(BuildActionRow(), 0, 2);
            _logPanel = BuildLogPanel();
            root.Controls.Add(_logPanel, 0, 3);
            ApplyLogRowLayout();
        }

        /// <summary>按日志是否展开重新分配列表 / 日志的高度比例。</summary>
        private void ApplyLogRowLayout()
        {
            if (_rootLayout == null || _rootLayout.RowStyles.Count < 4 || _logPanel == null) return;
            if (_logExpanded)
            {
                _rootLayout.RowStyles[1].SizeType = SizeType.Percent;
                _rootLayout.RowStyles[1].Height = 62f;
                _rootLayout.RowStyles[3].SizeType = SizeType.Percent;
                _rootLayout.RowStyles[3].Height = 38f;
            }
            else
            {
                // 日志收起时让列表占满，不浪费空间
                _rootLayout.RowStyles[1].SizeType = SizeType.Percent;
                _rootLayout.RowStyles[1].Height = 100f;
                _rootLayout.RowStyles[3].SizeType = SizeType.Absolute;
                _rootLayout.RowStyles[3].Height = 0f;
            }
            _rootLayout.PerformLayout();
        }

        private Control BuildRuleGroup()
        {
            var gb = new GroupBox { Text = "复制规则", Dock = DockStyle.Fill };
            var t = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(8, 6, 8, 6),
                ColumnCount = 2,
                RowCount = 3
            };
            // 标签列宽按最长标签实测，避免「目标目录：」被压缩换行
            int labelW = Math.Max(MeasureLabelWidth("重名处理："), MeasureLabelWidth("目标目录："));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, labelW));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _ruleGrid = t;

            // 行高按实际字体测量，不写死 —— 否则大字号 / 高 DPI 下
            // 复选框与输入框文字会被上下裁切、显得挤在一起。
            int rowH = MeasureRowHeight() + 6;
            _ruleRowHeight = rowH;
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, rowH));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, rowH));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, rowH));

            // 第 1 行：复制行为选项
            _chkWrap = MakeCheckBox("至少保留 1 级", "至少保留一级");
            _chkSkipEmpty = MakeCheckBox("跳过空目录", "跳过空目录");
            _chkPreserveTime = MakeCheckBox("保留时间戳", "保留时间戳");

            var optRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Margin = new Padding(0)
            };
            optRow.Controls.Add(_chkWrap);
            optRow.Controls.Add(_chkSkipEmpty);
            optRow.Controls.Add(_chkPreserveTime);

            t.Controls.Add(new Label
            {
                Text = "复制选项：",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                AccessibleRole = AccessibleRole.StaticText
            }, 0, 0);
            t.Controls.Add(optRow, 1, 0);

            // 第 2 行：目标目录（「粘贴到目标目录」按钮使用）
            _txtDest = new TextBox { Dock = DockStyle.Fill, AccessibleName = "目标目录" };
            var btnBrowse = new Button
            {
                Text = "浏览…",
                Dock = DockStyle.Fill,
                Margin = new Padding(4, 0, 0, 0),
                AutoSize = false
            };
            btnBrowse.Width = MeasureButtonWidth("浏览…", 72);
            btnBrowse.Click += (_, _) => BrowseDest();
            var btnOpen = new Button
            {
                Text = "打开",
                Dock = DockStyle.Fill,
                Margin = new Padding(4, 0, 0, 0),
                AutoSize = false
            };
            btnOpen.Width = MeasureButtonWidth("打开", 56);
            btnOpen.Click += (_, _) => OpenDestFolder();
            _btnOpenDest = btnOpen;

            var destTbl = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Margin = new Padding(0) };
            destTbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            destTbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, MeasureButtonWidth("浏览…", 72) + 4));
            destTbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, MeasureButtonWidth("打开", 56) + 4));
            destTbl.Controls.Add(_txtDest, 0, 0);
            destTbl.Controls.Add(btnBrowse, 1, 0);
            destTbl.Controls.Add(btnOpen, 2, 0);
            _destGrid = destTbl;

            t.Controls.Add(new Label
            {
                Text = "目标目录：",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                AccessibleRole = AccessibleRole.StaticText
            }, 0, 1);
            t.Controls.Add(destTbl, 1, 1);

            // 第 3 行：重名处理策略
            _cmbPolicy = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Dock = DockStyle.Fill,
                AccessibleName = "重名处理策略"
            };
            _cmbPolicy.Items.AddRange(new object[]
            {
                "自动重命名 (1)(2)…", "覆盖已有", "每次询问", "跳过已有"
            });
            _cmbPolicy.SelectedIndex = 0;

            t.Controls.Add(new Label
            {
                Text = "重名处理：",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                AccessibleRole = AccessibleRole.StaticText
            }, 0, 2);
            t.Controls.Add(_cmbPolicy, 1, 2);

            gb.Controls.Add(t);
            return gb;
        }

        private Control BuildListGroup()
        {
            var gb = new GroupBox { Text = "待复制列表（每项独立设置父结构级别）", Dock = DockStyle.Fill };
            var t = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(8, 6, 8, 6),
                ColumnCount = 1,
                RowCount = 2
            };
            int btnRowH = MeasureRowHeight() * 2 + 10;   // 预留两行，容纳大字号折行
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, btnRowH));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            // WrapContents = true：宽度不足时按钮自动折行，
            // 避免大字号下工具栏横向溢出（截图里"刷新"被挤出边界）。
            var btnFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                AutoScroll = true,
                Margin = new Padding(0)
            };
            _toolbarFlow = btnFlow;
            AddToolbar(btnFlow, "添加文件…", 90, () => AddViaDialog(false));
            AddToolbar(btnFlow, "添加文件夹…", 96, () => AddViaDialog(true));
            AddToolbar(btnFlow, "从资源管理器加入", 122, () => AddExplorerSelection());
            AddToolbar(btnFlow, "统一设级别…", 92, () => ApplyLevelToSelection());
            AddToolbar(btnFlow, "移除选中", 76, () => RemoveSelected());
            AddToolbar(btnFlow, "清空", 52, () => ClearAll());
            AddToolbar(btnFlow, "刷新", 52, () => { RefreshList(true); FillSizesAsync(); });
            t.Controls.Add(btnFlow, 0, 0);

            _lst = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = true,
                HideSelection = false,
                BorderStyle = BorderStyle.FixedSingle,
                AccessibleName = "待复制列表"
            };
            _lst.Columns.Add("完整路径", 300);
            _lst.Columns.Add("父结构级别", 96);
            _lst.Columns.Add("粘贴后结构（预览）", 230);
            _lst.Columns.Add("类型", 54);
            _lst.Columns.Add("大小 / 文件数", 110);

            _lst.MouseUp += OnListMouseUp;
            _lst.DoubleClick += OnListDoubleClick;
            _lst.KeyDown += (_, e) => OnListKeyDown(e);
            _lst.ContextMenuStrip = BuildContextMenu();

            t.Controls.Add(_lst, 0, 1);
            gb.Controls.Add(t);
            return gb;
        }

        private ContextMenuStrip BuildContextMenu()
        {
            var m = new ContextMenuStrip();
            m.Items.Add("设置父结构级别…", null, (_, _) => ApplyLevelToSelection());
            m.Items.Add("打开所在文件夹", null, (_, _) => OpenSourceFolder());
            m.Items.Add("复制完整路径", null, (_, _) => CopyPathToClipboard());
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("排除该项（本次不复制）", null, (_, _) => ExcludeSelected(true));
            m.Items.Add("恢复该项", null, (_, _) => ExcludeSelected(false));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("移除选中", null, (_, _) => RemoveSelected());
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("刷新列表", null, (_, _) => RefreshList(true));
            return m;
        }

        private Control BuildActionRow()
        {
            var p = new Panel { Dock = DockStyle.Fill };

            int rowH = MeasureRowHeight();
            var btnTop = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0),
                Height = rowH + 4
            };

            _btnCopy = MakeButton("复制到剪贴板", 118, () => StartCopy());
            _tipMain.SetToolTip(_btnCopy, "把列表内容连同父目录结构写入剪贴板，随后在任意文件夹按 Ctrl+V 粘贴（Ctrl+Enter）");
            _btnCopy.Height = rowH;
            _btnPaste = MakeButton("粘贴到目标目录", 118, () => StartCopy(true));
            _tipMain.SetToolTip(_btnPaste, "直接复制到上方「目标目录」指定的位置，适合批量归档（Ctrl+Shift+Enter）");
            _btnPaste.Height = rowH;
            _btnCancel = MakeButton("取消", 66, () => { _scheduler?.Cancel(); Log("正在取消…"); });
            _btnCancel.Height = rowH;
            _btnCancel.Enabled = false;

            btnTop.Controls.Add(_btnCopy);
            btnTop.Controls.Add(_btnPaste);
            btnTop.Controls.Add(_btnCancel);

            _bar = new ProgressBar
            {
                Dock = DockStyle.Bottom,
                Height = 6,
                Style = ProgressBarStyle.Continuous,
                Visible = false
            };
            _lblStatus = new Label
            {
                Dock = DockStyle.Fill,
                Text = "空闲",
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = SystemColors.GrayText,
                Padding = new Padding(2, 0, 0, 0),
                AutoEllipsis = true,
                AccessibleName = "状态"
            };

            p.Controls.Add(_bar);
            p.Controls.Add(_lblStatus);
            p.Controls.Add(btnTop);
            return p;
        }

        private Panel BuildLogPanel()
        {
            var p = new Panel { Dock = DockStyle.Fill };

            // 头部按钮条：用 FlowLayoutPanel 而非 Dock=Left，
            // 否则按钮相互压盖；宽度按文本测量，避免文字截断。
            int headH = MeasureRowHeight();
            var head = new Panel { Dock = DockStyle.Top, Height = headH + 4 };
            var headFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };

            _btnLogToggle = MakeLogButton("▼ 日志", ToggleLog);
            _tipMain.SetToolTip(_btnLogToggle, "折叠 / 展开日志区（Ctrl+L）");
            var btnClearLog = MakeLogButton("清空日志", () => _txtLog.Clear());
            var btnSaveLog = MakeLogButton("保存日志…", () => SaveLog());
            _logButtons.Add(_btnLogToggle);
            _logButtons.Add(btnClearLog);
            _logButtons.Add(btnSaveLog);

            headFlow.Controls.Add(_btnLogToggle);
            headFlow.Controls.Add(btnClearLog);
            headFlow.Controls.Add(btnSaveLog);
            head.Controls.Add(headFlow);

            _txtLog = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                // 开启自动换行并只保留纵向滚动条：窗口变窄时文字折行显示，
                // 不会因为横向滚动而"看不全"。
                ScrollBars = ScrollBars.Vertical,
                WordWrap = true,
                BackColor = SystemColors.Window,
                ForeColor = SystemColors.WindowText,
                Font = new Font("Consolas", 9F),
                AccessibleName = "日志"
            };

            p.Controls.Add(_txtLog);
            p.Controls.Add(head);
            return p;
        }

        /// <summary>
        /// 按文本内容自动计算按钮宽度，避免手写固定像素导致文字被截断
        /// （截断程度随系统字体 / DPI 变化，固定值不可靠）。
        /// </summary>
        /// <summary>加入工具栏并登记，便于字体变化时统一重算宽度。</summary>
        private void AddToolbar(Control host, string text, int minWidth, Action onClick)
        {
            var b = MakeButton(text, minWidth, onClick);
            _toolbarButtons.Add(b);
            host.Controls.Add(b);
        }

        private Button MakeButton(string text, int minWidth, Action onClick)
        {
            var b = new Button
            {
                Text = text,
                Height = MeasureRowHeight() - 4,
                Margin = new Padding(0, 0, 6, 0),
                AccessibleName = text,
                AutoSize = false
            };
            b.Click += (_, _) => onClick();
            b.Width = MeasureButtonWidth(text, minWidth);
            return b;
        }

        /// <summary>测量按钮文本所需宽度（含内边距），高 DPI 下自动放大。</summary>
        private int MeasureButtonWidth(string text, int minWidth)
            => UiMetrics.ButtonWidth(text, Font, minWidth);

        /// <summary>标准控件行高（随字体与 DPI 自适应）。</summary>
        private int MeasureRowHeight()
            => UiMetrics.RowHeight(Font);

        /// <summary>统一样式的复选框（显式边距，避免与相邻控件文字贴死）。</summary>
        private CheckBox MakeCheckBox(string text, string accessibleName)
            => UiMetrics.MakeCheckBox(text, Font, accessibleName);

        /// <summary>工具栏全部按钮的宽度总和（用于推导窗口最小宽度）。</summary>
        private int MeasureToolbarWidth()
        {
            string[] texts =
            {
                "添加文件…", "添加文件夹…", "从资源管理器加入",
                "统一设级别…", "移除选中", "清空", "刷新"
            };
            int total = 0;
            foreach (var s in texts) total += MeasureButtonWidth(s, 50) + 6;
            return total;
        }

        /// <summary>测量标签文本所需宽度（含右侧间距，足够容纳全角冒号）。</summary>
        private int MeasureLabelWidth(string text)
            => UiMetrics.LabelWidth(text, Font);

        /// <summary>日志头部的扁平按钮（System 风格，宽度自适应）。</summary>
        private Button MakeLogButton(string text, Action onClick)
        {
            int h = MeasureRowHeight() - 2;
            var b = new Button
            {
                Text = text,
                FlatStyle = FlatStyle.System,
                Height = h,
                Margin = new Padding(0, 0, 4, 0),
                AccessibleName = text,
                AutoSize = false
            };
            b.Click += (_, _) => onClick();
            b.Width = MeasureButtonWidth(text, 60);
            return b;
        }

        /// <summary>切换日志折叠时同步更新箭头文字与按钮宽度（▼/▶ 宽度不同）。</summary>
        private void UpdateLogToggleText()
        {
            string txt = _logExpanded ? "▼ 日志" : "▶ 日志";
            _btnLogToggle.Text = txt;
            _btnLogToggle.Width = MeasureButtonWidth(txt, 60);
        }
        // ================= 设置 =================

        private void ApplySettingsToUi()
        {
            _txtDest.Text = _settings.DestFolder;
            _cmbPolicy.SelectedIndex = Math.Max(0, Math.Min(3, _settings.CollisionPolicy));
            _chkWrap.Checked = _settings.WrapInFolder;
            _chkSkipEmpty.Checked = _settings.SkipEmptyDirectories;
            _chkPreserveTime.Checked = _settings.PreserveTimestamps;
            _logExpanded = !_settings.LogCollapsed;
            _logPanel.Visible = _logExpanded;
            UpdateLogToggleText();
            ApplyLogRowLayout();
            UpdateModeUi();
            UpdateStatus();
        }

        private void UpdateModeUi()
        {
            // 模式由点击哪个按钮决定，不再有"模式开关"控件；
            // 目标目录始终可编辑（「粘贴到目标目录」随时可能需要它）。
            _txtDest.Enabled = !_busy;
            if (_btnOpenDest != null) _btnOpenDest.Enabled = !_busy;
        }

        /// <summary>剪贴板模式下「粘贴到目标目录」仍可用，但目标目录框禁用；此处仅更新提示文本。</summary>
        private void UpdateStatus()
        {
            if (_busy) return;
            int total = _model.Count;
            if (total == 0)
            {
                _lblStatus.Text = "空闲 · 把文件或文件夹拖入窗口即可开始";
                return;
            }
            int valid = _model.Items.Count(x => x.Exists && !x.Excluded);
            _lblStatus.Text = "列表 " + total + " 项（有效 " + valid + " 项）· "
                              + "「复制到剪贴板」随处 Ctrl+V，或「粘贴到目标目录」直接落盘";
        }

        private void ReadSettingsFromUi()
        {
            _settings.DestFolder = _txtDest.Text.Trim();
            _settings.CollisionPolicy = Math.Max(0, _cmbPolicy.SelectedIndex);
            _settings.WrapInFolder = _chkWrap.Checked;
            _settings.SkipEmptyDirectories = _chkSkipEmpty.Checked;
            _settings.PreserveTimestamps = _chkPreserveTime.Checked;
        }

        private void SaveSettings()
        {
            ReadSettingsFromUi();
            if (!string.IsNullOrWhiteSpace(_txtDest.Text))
                _settings.PushRecent(_txtDest.Text.Trim());
            SettingsStore.Save(_settings, out string? err);
            if (err != null) Log(err);
        }

        private CopyFilter BuildFilter()
        {
            return new CopyFilter
            {
                SkipEmptyDirectories = _chkSkipEmpty.Checked,
                PreserveTimestamps = _chkPreserveTime.Checked
            };
        }

        // ================= 列表 =================

        private void AddViaDialog(bool folder)
        {
            if (folder)
            {
                using var dlg = new FolderBrowserDialog { Description = "选择要复制的文件夹", UseDescriptionForTitle = true };
                if (Directory.Exists(_txtDest.Text)) dlg.SelectedPath = _txtDest.Text;
                if (dlg.ShowDialog(this) == DialogResult.OK) AddPaths(new[] { dlg.SelectedPath });
            }
            else
            {
                using var dlg = new OpenFileDialog { Multiselect = true, Title = "选择要复制的文件" };
                if (dlg.ShowDialog(this) == DialogResult.OK) AddPaths(dlg.FileNames);
            }
        }

        private void AddExplorerSelection()
        {
            var files = ExplorerSelection.GetSelectedFiles();
            if (files.Count == 0)
            {
                Log("未检测到资源管理器中的选中项。请先在资源管理器中选中文件/文件夹。");
                _tray?.ShowBalloonTip(3000, "CopyWithFolders", "未检测到选中的文件/文件夹", ToolTipIcon.Warning);
                return;
            }
            Log("从资源管理器读到 " + files.Count + " 个选中项。");
            AddPaths(files);
        }

        private void AddPaths(IEnumerable<string> paths)
        {
            int defLevel = Math.Max(0, _settings.DefaultLevel);
            int added = _model.AddRange(paths.ToArray(), defLevel);
            int dup = _model.LastDuplicateCount;
            RefreshList(false);
            FillSizesAsync();

            string msg = "已加入 " + added + " 项（默认父结构级别 " + defLevel + " 级）";
            if (dup > 0) msg += "，跳过 " + dup + " 个重复项";
            Log(msg + "。");
            if (added > 0)
                _tray?.ShowBalloonTip(2500, "CopyWithFolders", msg, ToolTipIcon.Info);
        }

        private void RemoveSelected()
        {
            var sel = SelectedItems();
            if (sel.Count == 0) return;
            _model.Remove(sel);
            RefreshList(false);
        }

        private void ClearAll()
        {
            if (_model.Count == 0) return;
            if (MessageBox.Show(this, "确定清空列表中的 " + _model.Count + " 项吗？",
                    "清空列表", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            _model.Clear();
            RefreshList(false);
        }

        private void ExcludeSelected(bool exclude)
        {
            foreach (var it in SelectedItems()) it.Excluded = exclude;
            RefreshList(false);
        }

        private List<PendingItem> SelectedItems()
        {
            var list = new List<PendingItem>();
            foreach (ListViewItem li in _lst.SelectedItems)
                if (li.Tag is PendingItem p) list.Add(p);
            return list;
        }

        /// <summary>重建列表显示；withSize=true 时统计文件数与大小。</summary>
        private void RefreshList(bool withSize)
        {
            _lst.BeginUpdate();
            try
            {
                _lst.Items.Clear();
                _liveRows.Clear();

                foreach (var it in _model.Items)
                {
                    var li = new ListViewItem(it.Path) { Tag = it };
                    li.SubItems.Add(LevelText(it.Level));
                    li.SubItems.Add(it.Exists ? it.RelativePreview : "—");
                    li.SubItems.Add(it.Exists ? (it.IsDirectory ? "文件夹" : "文件") : "不存在");
                    li.SubItems.Add(withSize ? SizeTextFromCache(it) : "…");

                    if (!it.Exists)
                    {
                        li.ForeColor = Color.Gray;
                        li.SubItems[0].Text = it.Path + "（不存在）";
                    }
                    else if (it.Excluded)
                    {
                        li.ForeColor = Color.Gray;
                        li.SubItems[1].Text = LevelText(it.Level) + "（已排除）";
                        li.SubItems[1].ForeColor = Color.Firebrick;
                    }

                    _lst.Items.Add(li);
                    _liveRows.Add(li);

                }
            }
            finally
            {
                _lst.EndUpdate();
            }

            AutoResizeColumns();
            UpdateStatus();
        }

        /// <summary>
        /// 后台统计新增项的"大小 / 文件数"并回填列表。
        /// 同步统计会让界面在加入大目录时卡住，因此放到后台线程后再回填。
        /// </summary>
        private bool _filling;

        private async void FillSizesAsync()
        {
            if (_filling) return;
            var pending = _model.Items
                .Where(x => x.Exists && (x.CachedBytes < 0 || x.CachedFileCount < 0))
                .ToList();
            if (pending.Count == 0) return;

            _filling = true;
            try
            {
                await Task.Run(() =>
                {
                    foreach (var it in pending)
                    {
                        try { it.RefreshStats(true); } catch { }
                    }
                });
            }
            catch { return; }
            finally { _filling = false; }

            if (IsDisposed || _lst == null) return;

            _lst.BeginUpdate();
            try
            {
                foreach (ListViewItem li in _lst.Items)
                {
                    if (li.Tag is not PendingItem p) continue;
                    if (li.SubItems.Count <= ColSize) continue;
                    li.SubItems[ColSize].Text = SizeTextFromCache(p);
                }
            }
            finally { _lst.EndUpdate(); }

            AutoResizeColumns();
        }

        /// <summary>读取已缓存的统计结果（不触发重新扫描）。</summary>
        private static string SizeTextFromCache(PendingItem it)
        {
            if (!it.Exists) return "—";
            if (it.IsDirectory)
            {
                string n = it.CachedFileCount >= 0 ? it.CachedFileCount.ToString("#,0") + " 个" : "…";
                string b = it.CachedBytes >= 0 ? PendingItem.FormatBytes(it.CachedBytes) : "…";
                return n + " / " + b;
            }
            return PendingItem.FormatBytes(it.CachedBytes);
        }

        private static string LevelText(int level) => level == 0 ? "仅文件" : "含 " + level + " 级";

        private void AutoResizeColumns()
        {
            if (_lst.Columns.Count <= ColSize) return;
            try
            {
                // 固定列按表头实际文字宽度计算，避免「父结构级…」这类截断
                int levelW = MeasureColumnWidth(_lst.Columns[ColLevel].Text, 92);
                int typeW = MeasureColumnWidth(_lst.Columns[ColType].Text, 54);
                int sizeW = MeasureColumnWidth(_lst.Columns[ColSize].Text, 110);
                _lst.Columns[ColLevel].Width = levelW;
                _lst.Columns[ColType].Width = typeW;
                _lst.Columns[ColSize].Width = sizeW;

                int headerW = MeasureColumnWidth(_lst.Columns[ColRelative].Text, 150);
                int contentW = 0;
                foreach (ListViewItem li in _liveRows)
                {
                    if (li.SubItems.Count > ColRelative)
                        contentW = Math.Max(contentW,
                            TextRenderer.MeasureText(li.SubItems[ColRelative].Text, _lst.Font).Width);
                }

                // 关键：路径列与预览列争夺剩余宽度。
                // 若让预览列按内容无限伸展，会把「类型 / 大小」挤出可视区，
                // 因此给预览列设上限（不超过可伸缩空间的一半），其余留给完整路径。
                int fixedW = levelW + typeW + sizeW;
                int flexible = Math.Max(220, _lst.ClientSize.Width - fixedW - 24);
                int previewCap = Math.Max(headerW, (int)(flexible * 0.5));

                int previewW = Math.Max(headerW, Math.Min(contentW + 14, previewCap));
                int pathW = Math.Max(160, flexible - previewW);

                _lst.Columns[ColRelative].Width = previewW;
                _lst.Columns[ColPath].Width = pathW;
            }
            catch { }
        }

        /// <summary>测量列表列所需宽度：表头文字 + 内边距（随字体自适应）。</summary>
        private int MeasureColumnWidth(string text, int minWidth)
        {
            int w = TextRenderer.MeasureText(text, _lst.Font).Width + 22;
            return Math.Max(minWidth, w);
        }

        private int HitColumn(int x)
        {
            int acc = 0;
            for (int i = 0; i < _lst.Columns.Count; i++)
            {
                int w = _lst.Columns[i].Width;
                if (x >= acc && x < acc + w) return i;
                acc += w;
            }
            return -1;
        }

        private void OnListMouseUp(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            var hit = _lst.HitTest(e.X, e.Y);
            if (hit.Item == null) return;
            if (hit.Item.Tag is not PendingItem pi || !pi.Exists) return;
            if (HitColumn(e.X) != ColLevel) return;

            var pt = _lst.PointToScreen(new Point(e.X, e.Y));
            using var dlg = new LevelPickerDialog(pi.Path, pi.Level, pt);
            var result = dlg.ShowDialog(this);

            // 只在"确定"时接受结果；取消 / 点窗口外关闭都保持原级别
            if (result != DialogResult.OK) return;

            int picked = dlg.SelectedLevel;
            if (picked == pi.Level)
            {
                RefreshList(false);   // 级别未变也要刷新预览，保证显示与数据一致
                return;
            }

            pi.Level = picked;
            RefreshList(false);
            Log("「" + Path.GetFileName(CopyLogic.TrimEndSeparators(pi.Path)) + "」父结构级别："
                + LevelText(pi.Level) + " 已更新（粘贴后为 " + pi.RelativePreview + "）");
        }

        private void OnListDoubleClick(object? sender, EventArgs e)
        {
            // 双击「父结构级别」列不应触发打开文件夹，否则单击弹窗会被双击打断
            var pos = _lst.PointToClient(Cursor.Position);
            if (HitColumn(pos.X) == ColLevel) return;

            if (_lst.SelectedItems.Count == 1 && _lst.SelectedItems[0].Tag is PendingItem p && p.Exists)
                OpenSourceFolder();
        }

        private void OnListKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Delete) { RemoveSelected(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.C) { CopyPathToClipboard(); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter) { ApplyLevelToSelection(); e.Handled = true; }
        }

        private void ApplyLevelToSelection()
        {
            var sel = SelectedItems();
            if (sel.Count == 0) return;
            var first = sel[0];

            using var dlg = new LevelPickerDialog(first.Path, first.Level, Cursor.Position);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            int lvl = dlg.SelectedLevel;
            foreach (var it in sel) it.Level = lvl;
            RefreshList(false);
            Log("已将 " + sel.Count + " 项的父结构级别设为「" + LevelText(lvl) + "」。");
        }

        private void OpenSourceFolder()
        {
            var sel = SelectedItems();
            if (sel.Count == 0) return;
            var first = sel[0];
            try
            {
                if (first.IsDirectory)
                {
                    System.Diagnostics.Process.Start("explorer.exe", "\"" + first.Path + "\"");
                }
                else
                {
                    System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + first.Path + "\"");
                }
            }
            catch (Exception ex) { Log("打开所在文件夹失败：" + ex.Message); }
        }

        private void CopyPathToClipboard()
        {
            var sel = SelectedItems();
            if (sel.Count == 0) return;
            try
            {
                var paths = sel.Select(x => x.Path).ToArray();
                var data = new DataObject();
                // FileDrop：粘到资源管理器可得到文件副本
                data.SetData(DataFormats.FileDrop, false, paths);
                // 纯文本：粘到记事本 / 输入框可得到路径文本
                data.SetData(DataFormats.UnicodeText, false, string.Join(Environment.NewLine, paths));
                Clipboard.SetDataObject(data, true);
                Log("已复制 " + paths.Length + " 条路径（文件 + 文本两种格式）。");
            }
            catch (Exception ex) { Log("复制路径失败：" + ex.Message); }
        }

        // ================= 拖放 =================

        private void OnDragEnter(object? sender, DragEventArgs e)
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
            {
                e.Effect = DragDropEffects.Copy;
                _dragHover = true;
                Invalidate();
            }
            else e.Effect = DragDropEffects.None;
        }

        private void OnDragDrop(object? sender, DragEventArgs e)
        {
            _dragHover = false;
            Invalidate();
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                Log("拖入 " + files.Length + " 项。");
                AddPaths(files);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (!_dragHover) return;
            using var pen = new Pen(Color.FromArgb(0, 120, 215), 2);
            var r = new Rectangle(2, 2, ClientSize.Width - 4, ClientSize.Height - 4);
            e.Graphics.DrawRectangle(pen, r);
            const string txt = "松开以加入待复制列表";
            Size sz = TextRenderer.MeasureText(txt, Font);
            var tr = new Rectangle(r.X + 8, r.Bottom - sz.Height - 12, sz.Width + 16, sz.Height + 6);
            using var bg = new SolidBrush(Color.FromArgb(0, 120, 215));
            e.Graphics.FillRectangle(bg, tr);
            TextRenderer.DrawText(e.Graphics, txt, Font, tr, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        // ================= 快捷键与帮助 =================

        /// <summary>
        /// 清理旧暂存目录。
        /// 注意：必须保留最近 24 小时的目录——当前剪贴板引用的正是最新的那一个，
        /// 若按 0 小时清理会把正在使用的粘贴源一起删掉，导致 Ctrl+V 失败。
        /// </summary>
        /// <summary>
        /// 读取并消费交接文件：另一个实例启动时若发现已有实例在运行，
        /// 会把命令行路径写成 handoff_*.txt，这里取回来加入列表。
        /// </summary>
        private void ConsumeHandoff()
        {
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "CopyWithFolders");
                if (!Directory.Exists(CopyLogic.LongPath(dir))) return;

                foreach (string file in Directory.EnumerateFiles(CopyLogic.LongPath(dir), "handoff_*.txt"))
                {
                    string[] lines;
                    try { lines = File.ReadAllLines(file); }
                    catch { continue; }

                    try { File.Delete(file); } catch { }

                    var paths = lines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()).ToArray();
                    if (paths.Length == 0) continue;

                    ShowFromTray();
                    AddPaths(paths);
                    Log("已接收来自新实例的 " + paths.Length + " 个路径。");
                }
            }
            catch { /* 交接失败不影响主流程 */ }
        }

        private void CleanupStaging()
        {
            int n = StagingBuilder.CleanupStale(24);
            Log(n > 0 ? "已清理 " + n + " 个过期暂存目录（超过 24 小时）。"
                      : "没有需要清理的过期暂存目录。");
        }

        private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Control && e.Shift && e.KeyCode == Keys.Enter) { StartCopy(forceDirect: true); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.Enter) { StartCopy(); e.Handled = true; }
            else if (e.Control && e.Shift && e.KeyCode == Keys.O) { AddViaDialog(true); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.O) { AddViaDialog(false); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.E) { AddExplorerSelection(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.L) { ToggleLog(); e.Handled = true; }
            else if (e.KeyCode == Keys.F1) { ShowHelp(); e.Handled = true; }
        }

        private void ShowHelp()
        {
            MessageBox.Show(this,
@"快捷键一览

列表内：
  Delete      移除选中项
  Ctrl+A      全选
  Ctrl+C      复制选中项的完整路径
  Enter       批量设置父结构级别
  双击        打开所在文件夹

全局：
  Ctrl+O          添加文件
  Ctrl+Shift+O    添加文件夹
  Ctrl+E          从资源管理器加入
  Ctrl+Enter      复制到剪贴板
  Ctrl+Shift+Enter  直接粘贴到目标目录
  Ctrl+L          折叠 / 展开日志
  F1              显示本帮助

使用流程：
1. 把文件 / 文件夹拖入窗口，或用按钮加入列表
2. 点击每行「父结构级别」列（或选中多项按 Enter），
   在弹出的层级表里点选要保留到哪一级（可一直选到磁盘根目录）
   · 窗口可拉伸；点击窗口外的任何位置即取消
   · 超长的那一级目录名单独用 … 省略，短目录名保持完整
   · 把窗口拉宽可看到更多字符，悬停可查看完整路径
3. 二选一：
   · 「复制到剪贴板」 → 到任意文件夹按 Ctrl+V 粘贴
   · 「粘贴到目标目录」 → 直接落盘到上方指定的目录
4. 父目录结构自动保留

托盘菜单（右键右下角图标）：
  显示主界面 / 复制到剪贴板 / 粘贴到目标目录 / 最近目标目录 /
  快捷键与用法 / 清理过期暂存目录 / 退出

说明：
· 两种方式都要先设置父结构级别，这是本工具的核心
· 粘贴目标目录不存在时会自动创建
· 同名冲突可设为：自动重命名 / 覆盖 / 每次询问 / 跳过
  选「每次询问」时支持「覆盖全部 / 仅此项」批量决策",
"快捷键与用法", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ================= 执行 =================

        private async void StartCopy(bool forceDirect = false)
        {
            if (_busy) { Log("正在执行中，请稍候…"); return; }

            var items = _model.Items.Where(x => x.Exists && !x.Excluded).ToList();
            if (items.Count == 0)
            {
                Log("列表为空或全部无效，请先加入文件/文件夹。");
                MessageBox.Show(this, "列表为空或全部无效，请先加入文件/文件夹。",
                    "无可复制内容", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // 模式完全由按钮决定：点「复制到剪贴板」= 剪贴板模式，
            // 点「粘贴到目标目录」= 直接落盘模式。不再有独立的模式开关。
            bool clipboardMode = !forceDirect;
            var req = new CopyRequest
            {
                Items = items,
                DestFolder = _txtDest.Text.Trim(),
                Policy = (CollisionPolicy)Math.Max(0, _cmbPolicy.SelectedIndex),
                Filter = BuildFilter(),
                ClipboardMode = clipboardMode,
                WrapInFolder = _chkWrap.Checked
            };

            if (!clipboardMode && string.IsNullOrWhiteSpace(req.DestFolder))
            {
                MessageBox.Show(this, "直接粘贴模式需要先指定目标目录。",
                    "未设置目标目录", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // ---- 预检 ----
            var pre = new PrecheckInfo
            {
                Mode = clipboardMode ? "剪贴板（随后在任意文件夹 Ctrl+V）" : "直接复制到目标目录",
                DestFolder = clipboardMode ? "由资源管理器中的 Ctrl+V 决定" : req.DestFolder,
                JobCount = items.Count,
                ExcludedItems = _model.Items.Where(x => x.Excluded || !x.Exists).ToList()
            };
            var jobs = items.Select(x => new CopyJob(x.Path, x.Level)).ToList();
            var (pf, pb) = CopyLogic.Scan(jobs, req.Filter, CancellationToken.None);
            pre.FileCount = pf;
            pre.TotalBytes = pb;
            if (!clipboardMode)
                pre.ConflictItems = CopyScheduler.EstimateConflicts(req);

            using (var dlg = new PrecheckDialog(pre))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) { Log("已取消。"); return; }
                foreach (var s in dlg.GetUnchecked()) req.SkipConflicts.Add(s);
            }

            ReadSettingsFromUi();
            SetBusy(true);

            var scheduler = new CopyScheduler(
                new WindowsClipboardAccessor(),
                AskConflict,
                Log,
                new Progress<CopyProgress>(OnProgress),
                null);
            _scheduler = scheduler;
            string? finalStatus = null;

            try
            {
                var report = await Task.Run(() => scheduler.Execute(req));

                foreach (var r in report.Results)
                    if (r.Failed || r.Skipped) Log(r.ToString());
                Log(report.Summary);

                if (clipboardMode && report.JobFailed == 0 && report.JobOk > 0)
                {
                    _tray?.ShowBalloonTip(4000, "CopyWithFolders",
                        "已复制 " + report.FileCount + " 个文件到剪贴板，随处 Ctrl+V 粘贴", ToolTipIcon.Info);
                    finalStatus = "已写入剪贴板 " + report.FileCount + " 个文件 —— 现在到目标文件夹按 Ctrl+V";
                }
                else if (report.JobFailed > 0)
                {
                    _tray?.ShowBalloonTip(4000, "CopyWithFolders",
                        "完成：成功 " + report.JobOk + " · 跳过 " + report.JobSkipped + " · 失败 " + report.JobFailed,
                        ToolTipIcon.Warning);
                    finalStatus = "完成：成功 " + report.JobOk + " · 跳过 " + report.JobSkipped
                                  + " · 失败 " + report.JobFailed;
                }
                else
                {
                    finalStatus = "完成：成功 " + report.JobOk + " 项，共 " + report.FileCount + " 个文件"
                                  + (clipboardMode ? "（已在剪贴板）" : " → " + report.DestRoot);
                }
            }
            catch (Exception ex)
            {
                Log("任务异常：" + ex.Message);
                MessageBox.Show(this, "任务异常：" + ex.Message,
                    "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _scheduler = null;
                SetBusy(false);
                // SetBusy(false) 内部会调用 UpdateStatus()，此处再覆盖为本次结果，
                // 否则"已写入剪贴板…"等关键提示会被瞬间冲掉。
                if (finalStatus != null) _lblStatus.Text = finalStatus;
            }

            SaveSettings();
        }

        private ConflictChoice AskConflict(string target)
        {
            if (InvokeRequired)
                return (ConflictChoice)Invoke(new Func<ConflictChoice>(() => AskConflict(target)));
            bool isDir = Directory.Exists(CopyLogic.LongPath(target));
            using var dlg = new ConflictDialog(target, isDir, Path.GetDirectoryName(target) ?? "");
            if (dlg.ShowDialog(this) != DialogResult.OK) return ConflictChoice.SkipThis;
            return dlg.Choice;
        }

        private void OnProgress(CopyProgress p)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => OnProgress(p))); return; }
            if (p.Percent >= 0)
            {
                _bar.Value = Math.Min(100, Math.Max(0, p.Percent));
                _lblStatus.Text = "处理中 " + p.DoneFiles + " / " + p.TotalFiles + " 个文件（" + p.Percent + "%）";
            }
            else
            {
                _lblStatus.Text = "处理中… 已处理 " + p.DoneFiles + " 个文件";
            }
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            _btnCopy.Enabled = !busy;
            _btnPaste.Enabled = !busy;
            _btnCancel.Enabled = busy;
            _cmbPolicy.Enabled = !busy;
            _chkWrap.Enabled = !busy;
            _chkSkipEmpty.Enabled = !busy;
            _chkPreserveTime.Enabled = !busy;
            _txtDest.Enabled = !busy;
            if (_btnOpenDest != null) _btnOpenDest.Enabled = !busy;
            _bar.Visible = busy;
            if (busy)
            {
                _bar.Value = 0;
                _lblStatus.Text = "正在准备…";
            }
            else
            {
                _bar.Value = 0;
                UpdateStatus();
            }
        }

        // ================= 目标目录 =================

        private void BrowseDest()
        {
            using var dlg = new FolderBrowserDialog { Description = "选择目标目录", UseDescriptionForTitle = true };
            if (Directory.Exists(_txtDest.Text)) dlg.SelectedPath = _txtDest.Text;
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                _txtDest.Text = dlg.SelectedPath;
                SaveSettings();
            }
        }

        private void OpenDestFolder()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_txtDest.Text)) return;
                Directory.CreateDirectory(CopyLogic.LongPath(_txtDest.Text.Trim()));
                System.Diagnostics.Process.Start("explorer.exe", "\"" + _txtDest.Text.Trim() + "\"");
            }
            catch (Exception ex) { Log("打开目标目录失败：" + ex.Message); }
        }

        // ================= 日志 =================

        private void ToggleLog()
        {
            _logExpanded = !_logExpanded;
            _logPanel.Visible = _logExpanded;
            UpdateLogToggleText();
            _settings.LogCollapsed = !_logExpanded;
            ApplyLogRowLayout();
        }

        private void SaveLog()
        {
            using var dlg = new SaveFileDialog { Filter = "文本文件|*.txt", FileName = "CopyWithFolders_日志.txt" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try { File.WriteAllText(dlg.FileName, _txtLog.Text); Log("日志已保存到：" + dlg.FileName); }
            catch (Exception ex) { Log("保存日志失败：" + ex.Message); }
        }

        private void Log(string line)
        {
            if (_txtLog == null || IsDisposed) return;
            if (InvokeRequired) { try { BeginInvoke(new Action(() => Log(line))); } catch { } return; }
            string ts = DateTime.Now.ToString("HH:mm:ss");
            _txtLog.AppendText("[" + ts + "] " + line + Environment.NewLine);
            _txtLog.SelectionStart = _txtLog.TextLength;
            _txtLog.ScrollToCaret();
        }

        // ================= 托盘与生命周期 =================

        private void SetupTray()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("显示主界面", null, (_, _) => ShowFromTray());
            menu.Items.Add("复制到剪贴板", null, (_, _) => { ShowFromTray(); StartCopy(); });
            menu.Items.Add("粘贴到目标目录", null, (_, _) => { ShowFromTray(); StartCopy(true); });

            var recent = new ToolStripMenuItem("最近目标目录");
            recent.DropDownOpening += (_, _) =>
            {
                recent.DropDownItems.Clear();
                if (_settings.RecentFolders.Count == 0)
                {
                    recent.DropDownItems.Add("(暂无)").Enabled = false;
                    return;
                }
                foreach (var f in _settings.RecentFolders)
                {
                    var entry = new ToolStripMenuItem(f) { Tag = f };
                    entry.Click += (_, _) => { _txtDest.Text = f; SaveSettings(); };
                    recent.DropDownItems.Add(entry);
                }
            };
            menu.Items.Add(recent);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("快捷键与用法", null, (_, _) => { ShowFromTray(); ShowHelp(); });
            menu.Items.Add("清理过期暂存目录", null, (_, _) => CleanupStaging());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, (_, _) => { _exiting = true; Close(); });

            _tray = new NotifyIcon
            {
                Icon = GetAppIcon(),
                Text = "CopyWithFolders - 带父目录结构复制",
                Visible = true,
                ContextMenuStrip = menu
            };
            _tray.DoubleClick += (_, _) => ShowFromTray();
        }

        private static Icon GetAppIcon()
        {
            try
            {
                var icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (icon != null) return icon;
            }
            catch { }
            return SystemIcons.Application;
        }

        private void ShowFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_exiting)
            {
                e.Cancel = true;
                Hide();
                if (!_settings.TrayHintShown)
                {
                    _settings.TrayHintShown = true;
                    SettingsStore.Save(_settings, out _);
                    _tray.ShowBalloonTip(3500, "CopyWithFolders",
                        "已最小化到托盘，程序仍在后台。右键托盘图标可退出。", ToolTipIcon.Info);
                }
                return;
            }

            ReadSettingsFromUi();
            _settings.WindowX = Left;
            _settings.WindowY = Top;
            _settings.WindowW = Width;
            _settings.WindowH = Height;
            if (!string.IsNullOrWhiteSpace(_txtDest.Text)) _settings.PushRecent(_txtDest.Text.Trim());
            SettingsStore.Save(_settings, out _);
            try { SystemEvents.UserPreferenceChanged -= OnUserPreference; } catch { }
            try { _handoffTimer.Stop(); _handoffTimer.Dispose(); } catch { }
            try { _tipMain.Dispose(); } catch { }
            _tray.Visible = false;
            _tray.Dispose();
            base.OnFormClosing(e);
        }
    }
}
