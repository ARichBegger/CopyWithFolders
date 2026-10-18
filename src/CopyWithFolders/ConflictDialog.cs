// ConflictDialog.cs —— 同名冲突批量询问
//
// 目标：把"每个文件弹一次 Yes/No"换成一次性决策，
// 大批量复制时用户只需点 1 次而不是上千次。
using System;
using System.Drawing;
using System.Windows.Forms;
using CopyWithFolders.Core;

namespace CopyWithFolders
{
    /// <summary>
    /// 冲突处理对话框。提供 5 种选择：
    /// 覆盖全部剩余 / 仅覆盖此项 / 跳过全部剩余 / 仅跳过此项 / 取消全部。
    /// </summary>
    public sealed class ConflictDialog : Form
    {
        public ConflictChoice Choice { get; private set; } = ConflictChoice.Ask;

        public ConflictDialog(string target, bool isDirectory, string parentDir)
        {
            Choice = ConflictChoice.Ask;

            Text = "目标已存在";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            BackColor = SystemColors.Window;
            Font = new Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormClosed += (_, _) => { };

            int rowH = UiMetrics.RowHeight(Font);
            int btnH = rowH + 4;
            int pathH = 104;
            int hintH = rowH;

            // 高度必须容纳：标题 + 路径框 + 3 行按钮 + 提示行 + 上下边距
            int clientW = 520;
            int clientH = rowH + pathH + btnH * 3 + hintH + 34;
            ClientSize = new Size(clientW, clientH);
            MinimumSize = new Size(400, clientH);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(14, 12, 14, 10)
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, rowH));      // 标题
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, pathH));     // 路径
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, btnH * 3));  // 按钮三行
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, hintH));     // 提示
            Controls.Add(root);

            // ---- 标题 ----
            root.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = isDirectory ? "同名文件夹已存在：" : "同名文件已存在：",
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                AccessibleRole = AccessibleRole.StaticText
            }, 0, 0);

            // ---- 冲突路径 ----
            var pathBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = SystemColors.Window,
                Text = target,
                AccessibleName = "冲突目标路径",
                Margin = new Padding(0, 2, 0, 10)
            };
            root.Controls.Add(pathBox, 0, 1);

            // ---- 按钮：2 列 × 3 行，显式行高，避免被压缩 ----
            var btnGrid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 3,
                Margin = new Padding(0)
            };
            btnGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            btnGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            for (int i = 0; i < 3; i++)
                btnGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, btnH));

            var bOverwriteAll = MakeChoiceButton("覆盖全部剩余", ConflictChoice.OverwriteAll);
            var bOverwriteThis = MakeChoiceButton("仅覆盖此项", ConflictChoice.OverwriteThis);
            var bSkipAll = MakeChoiceButton("跳过全部剩余", ConflictChoice.SkipAll);
            var bSkipThis = MakeChoiceButton("仅跳过此项", ConflictChoice.SkipThis);
            var bCancel = MakeChoiceButton("取消全部", ConflictChoice.Cancel);

            btnGrid.Controls.Add(bOverwriteAll, 0, 0);
            btnGrid.Controls.Add(bOverwriteThis, 1, 0);
            btnGrid.Controls.Add(bSkipAll, 0, 1);
            btnGrid.Controls.Add(bSkipThis, 1, 1);
            btnGrid.Controls.Add(bCancel, 0, 2);
            btnGrid.SetColumnSpan(bCancel, 2);
            root.Controls.Add(btnGrid, 0, 2);

            // ---- 提示 ----
            root.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = "「覆盖全部 / 跳过全部」对本次任务剩余项生效，不再逐个询问。",
                ForeColor = SystemColors.GrayText,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                AccessibleRole = AccessibleRole.StaticText
            }, 0, 3);

            CancelButton = bCancel;
        }

        private Button MakeChoiceButton(string text, ConflictChoice choice)
        {
            var b = new Button
            {
                Text = text,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 0, 6, 4),
                AutoSize = false,
                AccessibleName = text
            };
            b.Click += (_, _) => { Choice = choice; DialogResult = DialogResult.OK; };
            return b;
        }
    }
}
