// IClipboardAccessor.cs —— 剪贴板读写抽象（跨平台 + 可单测）
using System;
using System.Collections.Generic;

namespace CopyWithFolders.Core
{
    /// <summary>
    /// 剪贴板读写接口。抽象出来是为了：
    ///   1) 逻辑层可在任意平台单测（用 MemoryClipboardAccessor）；
    ///   2) Windows 实现可走 OLE 与 Win32 两条通路。
    /// </summary>
    public interface IClipboardAccessor
    {
        /// <summary>把一批文件路径以系统文件拖放格式（CF_HDROP / FileDrop）写入剪贴板。</summary>
        bool SetFileDrop(IReadOnlyList<string> paths);

        /// <summary>读取剪贴板中的文件路径（若为文件拖放格式）。</summary>
        List<string> GetFileDrop();

        /// <summary>写入一段自定义格式数据（用于携带父结构元信息）。</summary>
        bool SetCustom(string format, byte[] data);

        /// <summary>读取自定义格式数据。</summary>
        byte[]? GetCustom(string format);
    }

    /// <summary>非 Windows 平台 / 测试用的纯内存实现。</summary>
    public sealed class MemoryClipboardAccessor : IClipboardAccessor
    {
        private List<string> _files = new List<string>();
        private Dictionary<string, byte[]> _custom = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        public bool SetFileDrop(IReadOnlyList<string> paths)
        {
            _files = new List<string>(paths ?? Array.Empty<string>());
            return _files.Count > 0;
        }

        public List<string> GetFileDrop() => new List<string>(_files);

        public bool SetCustom(string format, byte[] data)
        {
            if (string.IsNullOrEmpty(format) || data == null) return false;
            _custom[format] = data;
            return true;
        }

        public byte[]? GetCustom(string format)
        {
            if (_custom.TryGetValue(format ?? "", out var b)) return b;
            return null;
        }
    }
}
