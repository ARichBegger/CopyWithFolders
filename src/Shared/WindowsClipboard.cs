// WindowsClipboard.cs —— Windows CF_HDROP / OLE 剪贴板实现
//
// 采用两条互补通路，保证粘贴在任何目标程序中都成功：
//   通路 A：Clipboard.SetDataObject(DataObject) —— 走 OLE IDataObject，
//          设置 DataFormats.FileDrop 与自定义格式，兼容性最好（Office / VS Code / 资源管理器均可）；
//   通路 B：Win32 OpenClipboard + SetClipboardData(CF_HDROP) —— 原生 DROPFILES 结构，
//          在 OLE 通路失效（如剪贴板被独占）时兜底。
//
// 自定义格式 "CopyWithFolders.Structure" 以 UTF-8 JSON 携带父结构元信息，
// 供本程序自身在粘贴时识别（其他程序会忽略未知格式，不影响常规粘贴）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace CopyWithFolders.Core
{
    /// <summary>写入剪贴板时附带的结构元信息（本程序自定义格式）。</summary>
    public sealed class ClipboardMeta
    {
        public string App { get; set; } = "CopyWithFolders";
        public string Version { get; set; } = "3";
        public string CreatedAt { get; set; } = "";
        public List<MetaEntry> Entries { get; set; } = new List<MetaEntry>();
    }

    public sealed class MetaEntry
    {
        public string Source { get; set; } = "";
        public int Level { get; set; }
        public string Relative { get; set; } = "";
    }

    public sealed class WindowsClipboardAccessor : IClipboardAccessor
    {
        public const string CustomFormat = "CopyWithFolders.Structure";

        // ---- Win32 CF_HDROP ----
        private const int CF_HDROP = 15;
        private const uint GMEM_MOVEABLE = 0x0002;

        [StructLayout(LayoutKind.Sequential)]
        private struct DROPFILES
        {
            public uint pFiles;
            public int x;
            public int y;
            public int fNC;
            public int fWide;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalUnlock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalFree(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalSize(IntPtr hMem);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EmptyClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetClipboardData(uint uFormat);

        /// <summary>
        /// 主入口：把暂存树的顶层条目写入剪贴板，并附带结构元信息。
        /// 优先 OLE 通路，失败则回退 Win32 CF_HDROP。
        /// </summary>
        public bool SetFileDrop(IReadOnlyList<string> paths)
        {
            if (paths == null || paths.Count == 0) return false;

            var existing = new List<string>(paths);

            // 通路 A：OLE IDataObject
            try
            {
                var data = new DataObject();
                data.SetData(DataFormats.FileDrop, false, existing.ToArray());
                Clipboard.SetDataObject(data, true);
                return true;
            }
            catch (Exception) { /* 继续尝试兜底通路 */ }

            // 通路 B：原生 CF_HDROP
            try
            {
                return SetHdropRaw(existing);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>原生 DROPFILES 写入（双击 nul 结尾的 UTF-16 路径列表）。</summary>
        private bool SetHdropRaw(List<string> paths)
        {
            var sb = new StringBuilder();
            foreach (var p in paths) sb.Append(p).Append('\0');
            sb.Append('\0');                                  // 双 nul 终止

            int charCount = sb.Length;
            int byteCount = charCount * 2;
            int headerSize = Marshal.SizeOf(typeof(DROPFILES));

            IntPtr hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)(headerSize + byteCount));
            if (hMem == IntPtr.Zero) return false;

            IntPtr hLock = GlobalLock(hMem);
            if (hLock == IntPtr.Zero) { GlobalFree(hMem); return false; }

            try
            {
                var df = new DROPFILES
                {
                    pFiles = (uint)headerSize,
                    x = 0, y = 0,
                    fNC = 0,
                    fWide = 1                                          // Unicode
                };
                Marshal.StructureToPtr(df, hLock, false);
                Marshal.Copy(sb.ToString().ToCharArray(), 0,
                             hLock + headerSize, charCount);

                GlobalUnlock(hMem);

                if (!OpenClipboard(IntPtr.Zero)) return false;
                try
                {
                    EmptyClipboard();
                    SetClipboardData(CF_HDROP, hMem);
                }
                finally
                {
                    CloseClipboard();
                }
                hMem = IntPtr.Zero;                                 // 所有权已转移给系统
                return true;
            }
            finally
            {
                if (hMem != IntPtr.Zero) GlobalFree(hMem);
            }
        }

        /// <summary>读取剪贴板中的文件路径（FileDrop 格式）。</summary>
        public List<string> GetFileDrop()
        {
            var result = new List<string>();
            try
            {
                if (Clipboard.ContainsData(DataFormats.FileDrop))
                {
                    var data = Clipboard.GetData(DataFormats.FileDrop);
                    if (data is string[] arr)
                    {
                        result.AddRange(arr);
                        return result;
                    }
                }
            }
            catch { }

            // 兜底：直接读 CF_HDROP
            try
            {
                if (OpenClipboard(IntPtr.Zero))
                {
                    try
                    {
                        IntPtr h = GetClipboardData(CF_HDROP);
                        if (h != IntPtr.Zero)
                        {
                            IntPtr p = GlobalLock(h);
                            if (p != IntPtr.Zero)
                            {
                                try
                                {
                                    var df = Marshal.PtrToStructure<DROPFILES>(p);
                                    IntPtr list = p + (int)df.pFiles;
                                    long size = GlobalSize(h).ToInt64();
                                    int maxChars = (int)((size - df.pFiles) / 2);
                                    if (maxChars > 0)
                                    {
                                        var buf = new char[maxChars];
                                        Marshal.Copy(list, buf, 0, maxChars);
                                        string all = new string(buf);
                                        foreach (var s in all.Split('\0'))
                                            if (!string.IsNullOrEmpty(s)) result.Add(s);
                                    }
                                }
                                finally { GlobalUnlock(h); }
                            }
                        }
                    }
                    finally { CloseClipboard(); }
                }
            }
            catch { }
            return result;
        }

        public bool SetCustom(string format, byte[] data)
        {
            // 注意：Clipboard.SetData 会【替换】整个剪贴板内容，
            // 若已有 FileDrop 格式会被清掉，因此这里必须与文件列表一起
            // 通过同一个 DataObject 写入（见 PutAll）。单独调用仅用于测试。
            if (string.IsNullOrEmpty(format) || data == null) return false;
            try
            {
                Clipboard.SetData(format, new MemoryStream(data));
                return true;
            }
            catch { return false; }
        }

        public byte[]? GetCustom(string format)
        {
            try
            {
                if (Clipboard.ContainsData(format))
                {
                    if (Clipboard.GetData(format) is MemoryStream ms)
                    {
                        var b = new byte[ms.Length];
                        ms.Read(b, 0, (int)ms.Length);
                        return b;
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 一次性写入：文件列表 + 结构元信息。
        /// 两种格式必须放在【同一个 DataObject】里一次 SetDataObject ——
        /// 分两次写会让后一次覆盖前一次，导致文件列表丢失（粘贴无反应）。
        /// </summary>
        public bool PutAll(IReadOnlyList<string> paths, ClipboardMeta meta)
        {
            if (paths == null || paths.Count == 0) return false;
            byte[] json;
            try { json = JsonSerializer.SerializeToUtf8Bytes(meta); }
            catch { json = Array.Empty<byte>(); }

            var arr = new string[paths.Count];
            for (int i = 0; i < paths.Count; i++) arr[i] = paths[i];

            // 通路 A：OLE，双格式一次写入
            try
            {
                var data = new DataObject();
                data.SetData(DataFormats.FileDrop, false, arr);
                if (json.Length > 0) data.SetData(CustomFormat, new MemoryStream(json));
                Clipboard.SetDataObject(data, true);
                return true;
            }
            catch (Exception)
            {
                // 若自定义格式导致 OLE 通路失败，退化为只写文件列表（元信息可选）
                try
                {
                    var only = new DataObject();
                    only.SetData(DataFormats.FileDrop, false, arr);
                    Clipboard.SetDataObject(only, true);
                    return true;
                }
                catch (Exception) { }
            }

            // 通路 B：原生 CF_HDROP（仅文件列表，元信息放弃）
            try { return SetHdropRaw(new List<string>(paths)); }
            catch { return false; }
        }

        /// <summary>读取本程序写入的结构元信息；不是本程序的数据则返回 null。</summary>
        public static ClipboardMeta? TryReadMeta()
        {
            try
            {
                if (!Clipboard.ContainsData(CustomFormat)) return null;
                if (Clipboard.GetData(CustomFormat) is not MemoryStream ms) return null;
                byte[] bytes = ms.ToArray();
                if (bytes.Length == 0) return null;

                var meta = JsonSerializer.Deserialize<ClipboardMeta>(bytes);
                return meta?.App == "CopyWithFolders" ? meta : null;
            }
            catch { return null; }
        }

        /// <summary>组合 ClipboardMeta 与 ClipboardAccessor 的静态便捷方法。</summary>
        public static ClipboardMeta BuildMeta(IEnumerable<StageRequest> reqs)
        {
            var meta = new ClipboardMeta { CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") };
            foreach (var r in reqs)
            {
                meta.Entries.Add(new MetaEntry
                {
                    Source = r.Source,
                    Level = r.Level,
                    Relative = CopyLogic.BuildRelativePath(r.Source, r.Level)
                });
            }
            return meta;
        }
    }

}
