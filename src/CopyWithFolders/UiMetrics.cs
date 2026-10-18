// UiMetrics.cs —— 界面度量工具（按钮 / 行高 / 标签宽度）
//
// 关键点：控件的像素尺寸不能写死。写死的值只对"某个字体 + 某个 DPI"成立，
// 换字体大小或缩放比例就会出现文字被裁切、控件溢出、元素相互压盖。
// 统一在构建时按实际字体实测，从根本上避免排版问题。
using System;
using System.Drawing;
using System.Windows.Forms;

namespace CopyWithFolders
{
    internal static class UiMetrics
    {
        /// <summary>文本所需的完整宽度（高 DPI 下自动放大）。</summary>
        public static int TextWidth(string text, Font font)
        {
            return TextRenderer.MeasureText(text, font,
                new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPadding).Width;
        }

        /// <summary>文本所需高度。</summary>
        public static int TextHeight(Font font)
        {
            return TextRenderer.MeasureText("测试Ag", font,
                new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPadding).Height;
        }

        /// <summary>标准控件行高：容纳按钮 / 复选框 / 输入框而不裁切。</summary>
        public static int RowHeight(Font font)
        {
            return Math.Max(26, TextHeight(font) + 12);
        }

        /// <summary>按钮宽度：文字 + 左右内边距 + 边框余量。</summary>
        public static int ButtonWidth(string text, Font font, int minWidth)
        {
            return Math.Max(minWidth, TextWidth(text, font) + 28);
        }

        /// <summary>标签宽度：文字 + 右侧间隙，避免贴住相邻控件。</summary>
        public static int LabelWidth(string text, Font font)
        {
            return TextWidth(text, font) + 16;
        }

        /// <summary>创建一个宽度自适应的按钮。</summary>
        public static Button MakeButton(string text, Font font, int minWidth, DialogResult? dialogResult = null)
        {
            int h = RowHeight(font) - 2;
            var b = new Button
            {
                Text = text,
                Height = h,
                Width = ButtonWidth(text, font, 72),
                AutoSize = false,
                Margin = new Padding(0, 2, 8, 2),
                AccessibleName = text
            };
            if (dialogResult.HasValue) b.DialogResult = dialogResult.Value;
            return b;
        }

        /// <summary>创建一个统一样式的复选框（显式边距，避免与相邻控件文字贴死）。</summary>
        public static CheckBox MakeCheckBox(string text, Font font, string accessibleName)
        {
            return new CheckBox
            {
                Text = text,
                AutoSize = true,
                Margin = new Padding(2, 4, 18, 4),
                AccessibleName = accessibleName
            };
        }
    }
}
