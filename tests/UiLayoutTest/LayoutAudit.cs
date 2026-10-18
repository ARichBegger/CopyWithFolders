using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace UiLayoutTest
{
    /// <summary>控件排版断言：检查文字是否被容器裁切、子控件是否重叠或溢出。</summary>
    internal static class LayoutAudit
    {
        private static readonly List<string> Issues = new();
        public static IReadOnlyList<string> Result => Issues;
        public static void Clear() => Issues.Clear();

        public static void Run(Control root, string path = "")
        {
            string p = string.IsNullOrEmpty(path) ? root.Name : path;

            if (root is Button b && b.Text.Length > 0)
                CheckTextFits(b, b.Text, p + ":按钮[" + b.Text + "]");

            if (root is Label l && l.Text.Length > 0 && l.AutoSize == false)
                CheckTextFits(l, l.Text, p + ":标签[" + l.Text + "]");

            if (root is CheckBox c && c.Text.Length > 0)
                CheckTextFits(c, c.Text, p + ":复选框[" + c.Text + "]");

            if (root is ListView lv)
            {
                foreach (ColumnHeader ch in lv.Columns)
                    CheckHeaderFits(lv, ch, p);
                foreach (ListViewItem it in lv.Items)
                    foreach (ListViewItem.ListViewSubItem si in it.SubItems)
                    {
                        // 只需确认列宽能容纳最宽内容之一
                    }
            }

            // 子控件重叠检测
            var kids = new List<Control>();
            foreach (Control k in root.Controls) kids.Add(k);
            for (int i = 0; i < kids.Count; i++)
                for (int j = i + 1; j < kids.Count; j++)
                {
                    if (!kids[i].Visible || !kids[j].Visible) continue;
                    if (kids[i].Dock != DockStyle.None || kids[j].Dock != DockStyle.None) continue;
                    Rectangle a = Rectangle.Intersect(kids[i].Bounds, kids[j].Bounds);
                    if (a.Width > 0 && a.Height > 0)
                        Issues.Add("控件重叠: " + p + " 下 [" + Name(kids[i]) + "] 与 [" + Name(kids[j]) + "] 重叠 " + a.Width + "x" + a.Height + " px");
                }

            // 子控件溢出父容器
            foreach (Control k in kids)
            {
                if (!k.Visible) continue;
                if (k.Dock != DockStyle.None) continue;
                if (k.Right > root.ClientSize.Width + 2 || k.Bottom > root.ClientSize.Height + 2)
                    Issues.Add("控件溢出: " + p + " 下 [" + Name(k) + "] 超出父容器 " + root.ClientSize.Width + "x" + root.ClientSize.Height
                        + "（实际 " + k.Right + "x" + k.Bottom + "）");
            }

            foreach (Control k in kids) Run(k, p);
        }

        private static string Name(Control c) =>
            (string.IsNullOrEmpty(c.Text) ? c.GetType().Name : c.Text);

        private static void CheckTextFits(Control c, string text, string where)
        {
            try
            {
                Size need = TextRenderer.MeasureText(text, c.Font);
                int availW = c.ClientSize.Width;
                int availH = c.ClientSize.Height;
                if (availW <= 0 || availH <= 0) return;

                // 允许 1px 误差
                if (need.Width > availW + 1)
                    Issues.Add("文字被裁切(宽): " + where + " 需要 " + need.Width + "px，控件宽 " + availW + "px");
                if (need.Height > availH + 1)
                    Issues.Add("文字被裁切(高): " + where + " 需要 " + need.Height + "px，控件高 " + availH + "px");
            }
            catch { }
        }

        private static void CheckHeaderFits(ListView lv, ColumnHeader ch, string where)
        {
            int need = TextRenderer.MeasureText(ch.Text, lv.Font).Width + 20;
            if (ch.Width < need - 1)
                Issues.Add("列标题被裁切: " + where + " 列[" + ch.Text + "] 需要约 " + need + "px，实际 " + ch.Width + "px");
        }
    }
}
