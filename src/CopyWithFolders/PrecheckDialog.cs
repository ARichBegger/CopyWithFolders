// PrecheckDialog.cs —— 执行前预检确认（目标 / 项数 / 文件数 / 大小 / 冲突项 / 排除项）
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace CopyWithFolders
{
    /// <summary>预检信息。</summary>
    public sealed class PrecheckInfo
    {
        public string Mode { get; set; } = "";                 // 剪贴板 / 直接复制
        public string DestFolder { get; set; } = "";
        public int JobCount { get; set; }
        public long FileCount { get; set; }
        public long TotalBytes { get; set; }
        public List<string> ConflictItems { get; set; } = new();
        public List<PendingItem> ExcludedItems { get; set; } = new();
    }

    /// <summary>
    /// 执行前确认对话框：把不可逆的批量操作包进一次可回看的确认，
    /// 并允许在此勾选排除个别冲突项。
    /// </summary>
    public sealed class PrecheckDialog : Form
    {
        private readonly ListView _conflictList;
        public bool Confirmed { get; private set; }

        public PrecheckDialog(PrecheckInfo info)
        {
            Confirmed = false;

            Text = "复制前确认";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            MinimumSize = new Size(520, 0);
            BackColor = SystemColors.Window;
            Font = new Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.Dpi;
            int rowH = UiMetrics.RowHeight(Font);
            int overviewH = rowH * 5 + 8;
            int headH = rowH;
            int btnRowH = rowH + 6;
            int listH = Math.Max(160, rowH * 6);
            ClientSize = new Size(580, overviewH + headH + listH + btnRowH + 56);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 5,
                Padding = new Padding(14, 12, 14, 10)
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, overviewH));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, headH));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, listH));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, headH));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, btnRowH));

            // 概览
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiMetrics.LabelWidth("预计大小", Font)));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            AddRow(grid, 0, "操作方式", info.Mode);
            AddRow(grid, 1, "待复制项", info.JobCount + " 项");
            AddRow(grid, 2, "预计文件", info.FileCount.ToString("#,0") + " 个");
            AddRow(grid, 3, "预计大小", PendingItem.FormatBytes(info.TotalBytes));
            AddRow(grid, 4, info.Mode.StartsWith("剪贴板") ? "粘贴方式" : "目标目录", info.DestFolder);
            root.Controls.Add(grid, 0, 0);

            // 冲突区标题
            bool hasConflict = info.ConflictItems.Count > 0;
            root.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = hasConflict
                    ? "检测到 " + info.ConflictItems.Count + " 项在目标处可能重名（取消勾选可跳过）："
                    : "未检测到重名冲突。",
                ForeColor = hasConflict ? Color.FromArgb(176, 0, 32) : SystemColors.GrayText,
                TextAlign = ContentAlignment.MiddleLeft,
                AccessibleRole = AccessibleRole.StaticText
            }, 0, 1);

            _conflictList = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                CheckBoxes = true,
                HideSelection = false,
                BorderStyle = BorderStyle.FixedSingle,
                AccessibleName = "冲突项列表"
            };
            _conflictList.Columns.Add("文件 / 文件夹", 300);
            _conflictList.Columns.Add("将粘贴为", 220);
            foreach (var rel in info.ConflictItems)
            {
                var it = new ListViewItem(rel) { Checked = true, Tag = rel };
                _conflictList.Items.Add(it);
            }
            _conflictList.ItemChecked += (_, _) => UpdateConfirmState();
            root.Controls.Add(_conflictList, 0, 2);

            // 排除项提示
            root.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = SystemColors.GrayText,
                TextAlign = ContentAlignment.MiddleLeft,
                Text = info.ExcludedItems.Count > 0
                    ? "另有 " + info.ExcludedItems.Count + " 项被手动排除或已不存在，不会复制。"
                    : "",
                AccessibleRole = AccessibleRole.StaticText
            }, 0, 3);

            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false
            };
            var btnOk = UiMetrics.MakeButton("开始复制", Font, 96);
            btnOk.Click += (_, _) => { Confirmed = true; DialogResult = DialogResult.OK; };
            var btnCancel = UiMetrics.MakeButton("取消", Font, 80, DialogResult.Cancel);
            flow.Controls.Add(btnCancel);
            flow.Controls.Add(btnOk);
            root.Controls.Add(flow, 0, 4);
            AcceptButton = btnOk;
            CancelButton = btnCancel;

            Controls.Add(root);
        }

        private void AddRow(TableLayoutPanel g, int row, string k, string v)
        {
            g.RowStyles.Add(new RowStyle(SizeType.Absolute, UiMetrics.RowHeight(Font) - 6));
            var kl = new Label
            {
                Text = k,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = SystemColors.GrayText,
                AccessibleRole = AccessibleRole.StaticText
            };
            var vl = new Label
            {
                Text = v,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                AccessibleName = k,
                AccessibleRole = AccessibleRole.StaticText
            };
            g.Controls.Add(kl, 0, row);
            g.Controls.Add(vl, 1, row);
        }

        private void UpdateConfirmState()
        {
            // 仅在勾选为空时禁用"开始复制"由调用方处理，此处保留扩展位
        }

        /// <summary>用户在确认框中取消勾选的冲突项（相对路径集合）。</summary>
        public HashSet<string> GetUnchecked()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ListViewItem it in _conflictList.Items)
            {
                if (!it.Checked && it.Tag is string s) set.Add(s);
            }
            return set;
        }

        /// <summary>确认框中的全部冲突相对路径。</summary>
        public List<string> GetAllConflicts() =>
            _conflictList.Items.Cast<ListViewItem>()
                .Select(x => x.Tag as string ?? "")
                .Where(x => x.Length > 0)
                .ToList();
    }
}
