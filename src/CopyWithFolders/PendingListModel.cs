// PendingListModel.cs —— 待复制列表的数据模型（去重、状态、预览计算）
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using CopyWithFolders.Core;

namespace CopyWithFolders
{
    /// <summary>列表中的一行：路径 + 独立父结构级别。</summary>
    public sealed class PendingItem
    {
        public string Path { get; set; } = "";
        public int Level { get; set; } = 2;
        public bool Excluded { get; set; }

        /// <summary>加入时缓存的状态，刷新时会重新求值。</summary>
        public bool CachedExists { get; set; }
        public bool CachedIsDir { get; set; }
        public long CachedFileCount { get; set; } = -1;
        public long CachedBytes { get; set; } = -1;

        public bool Exists
        {
            get
            {
                string lp = CopyLogic.LongPath(Path);
                return File.Exists(lp) || Directory.Exists(lp);
            }
        }

        public bool IsDirectory => Directory.Exists(CopyLogic.LongPath(Path));

        /// <summary>复制后的相对路径预览。</summary>
        public string RelativePreview
        {
            get
            {
                try { return CopyLogic.BuildRelativePath(Path, Level); }
                catch { return "?"; }
            }
        }

        /// <summary>可选的最大级别。</summary>
        public int MaxLevel
        {
            get
            {
                try { return CopyLogic.MaxLevel(Path); }
                catch { return 0; }
            }
        }

        /// <summary>刷新缓存状态（存在性、类型、文件数、大小）。</summary>
        public void RefreshStats(bool withSize)
        {
            string lp = CopyLogic.LongPath(Path);
            if (File.Exists(lp))
            {
                CachedExists = true; CachedIsDir = false;
                CachedFileCount = 1; CachedBytes = -1;
                if (withSize) { try { CachedBytes = new FileInfo(Path).Length; } catch { CachedBytes = 0; } }
                return;
            }
            if (Directory.Exists(lp))
            {
                CachedExists = true; CachedIsDir = true;
                if (!withSize) { CachedFileCount = -1; CachedBytes = -1; return; }
                CachedFileCount = -1; CachedBytes = -1;
                var scan = ScanAsync(lp);
                CachedFileCount = scan.Files;
                CachedBytes = scan.Bytes;
                return;
            }
            CachedExists = false; CachedIsDir = false;
            CachedFileCount = -1; CachedBytes = -1;
        }

        private (long Files, long Bytes) ScanAsync(string dir)
        {
            try
            {
                var f = new CopyFilter();
                var r = CopyLogic.Scan(new[] { new CopyJob(dir, 0) }, f, CancellationToken.None);
                return r;
            }
            catch { return (0, 0); }
        }

        /// <summary>人类可读的大小。</summary>
        public static string FormatBytes(long b)
        {
            if (b < 0) return "—";
            if (b < 1024) return b + " B";
            double kb = b / 1024.0;
            if (kb < 1024) return kb.ToString("0.#") + " KB";
            double mb = kb / 1024.0;
            if (mb < 1024) return mb.ToString("0.#") + " MB";
            return (mb / 1024.0).ToString("0.##") + " GB";
        }
    }

    /// <summary>待复制列表的集合管理：去重、增删、查询。</summary>
    public sealed class PendingListModel
    {
        private readonly List<PendingItem> _items = new();

        public IReadOnlyList<PendingItem> Items => _items;
        public int Count => _items.Count;
        public int Total => _items.Count;

        /// <summary>返回是否新增；已存在的路径返回 false。</summary>
        public bool TryAdd(PendingItem item)
        {
            string key = Normalize(item.Path);
            foreach (var x in _items)
            {
                if (string.Equals(Normalize(x.Path), key, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            _items.Add(item);
            return true;
        }

        public int AddRange(IEnumerable<string> paths, int defaultLevel)
        {
            int added = 0, dup = 0;
            foreach (var p in paths)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                var it = new PendingItem { Path = p.Trim(), Level = defaultLevel };
                it.RefreshStats(false);
                if (TryAdd(it)) added++; else dup++;
            }
            LastDuplicateCount = dup;
            return added;
        }

        public int LastDuplicateCount { get; private set; }

        public void Remove(IEnumerable<PendingItem> items)
        {
            foreach (var it in items.ToList()) _items.Remove(it);
        }

        public void Clear() => _items.Clear();

        public void SetLevel(IEnumerable<PendingItem> items, int level)
        {
            foreach (var it in items) it.Level = level;
        }

        /// <summary>当前有效任务（存在且未被排除）。</summary>
        public List<CopyJob> BuildJobs()
        {
            var list = new List<CopyJob>();
            foreach (var it in _items)
            {
                if (it.Excluded) continue;
                if (!it.Exists) continue;
                list.Add(new CopyJob(it.Path, it.Level));
            }
            return list;
        }

        public List<StageRequest> BuildStageRequests(bool wrap)
        {
            var list = new List<StageRequest>();
            foreach (var it in _items)
            {
                if (it.Excluded) continue;
                if (!it.Exists) continue;
                list.Add(new StageRequest(it.Path, it.Level, wrap));
            }
            return list;
        }

        private static string Normalize(string p)
        {
            try { return CopyLogic.TrimEndSeparators(Path.GetFullPath(p)); }
            catch { return p.Trim(); }
        }
    }
}
