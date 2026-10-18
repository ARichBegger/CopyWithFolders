// Program.cs —— 应用入口：高 DPI、单实例（激活已有窗口）、资源管理器右键接入
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace CopyWithFolders
{
    internal static class Program
    {
        private const string MutexName = @"Local\CopyWithFolders_SingleInstance";

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [STAThread]
        private static void Main(string[] args)
        {
            // 高 DPI：必须在创建任何窗体之前设置（csproj 中的 ApplicationHighDpiMode 是 WPF 属性，对 WinForms 无效）
            try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); }
            catch { }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 处理命令行参数：--append "path1" "path2"（供资源管理器"发送到"调用）
            var incoming = ParseIncomingPaths(args);

            bool created;
            using (var mutex = new Mutex(true, MutexName, out created))
            {
                if (!created)
                {
                    // 已有实例：尝试激活其窗口，而不是简单提示后退出
                    ActivateExistingInstance(incoming);
                    return;
                }

                var form = new MainForm(incoming);
                Application.Run(form);
            }
        }

        /// <summary>解析 "--append a b c" 形式的参数（用于"发送到"快捷方式）。</summary>
        private static List<string> ParseIncomingPaths(string[] args)
        {
            var result = new List<string>();
            if (args == null || args.Length == 0) return result;

            bool isAppend = args.Any(a =>
                string.Equals(a, "--append", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "--add", StringComparison.OrdinalIgnoreCase));

            foreach (var a in args)
            {
                if (string.IsNullOrWhiteSpace(a)) continue;
                if (a.StartsWith("--", StringComparison.Ordinal)) continue;
                // 去掉可能的引号
                string p = a.Trim().Trim('"');
                if (p.Length == 0) continue;
                try { if (File.Exists(p) || Directory.Exists(p)) result.Add(p); }
                catch { }
            }
            // 无 --append 但直接给了路径（拖到 exe 上）也接受
            if (!isAppend && result.Count > 0) { /* 同样有效 */ }
            return result;
        }

        private static void ActivateExistingInstance(List<string> incoming)
        {
            IntPtr hwnd = FindWindow(null, "CopyWithFolders - 带父目录结构复制");
            if (hwnd != IntPtr.Zero)
            {
                ShowWindow(hwnd, 9);      // SW_RESTORE
                SetForegroundWindow(hwnd);
            }
            else
            {
                MessageBox.Show("CopyWithFolders 已在运行（可能已最小化到托盘），请从托盘图标打开。",
                    "CopyWithFolders", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            // 传入的路径通过临时文件交接给已运行实例
            if (incoming.Count > 0)
            {
                try
                {
                    string handoff = Path.Combine(Path.GetTempPath(),
                        "CopyWithFolders", "handoff_" + Environment.ProcessId + ".txt");
                    Directory.CreateDirectory(Path.GetDirectoryName(handoff)!);
                    File.WriteAllLines(handoff, incoming);
                }
                catch { }
            }
        }
    }
}
