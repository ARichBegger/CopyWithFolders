// CopyOptions.cs —— 复制选项、进度报告与结果模型（纯逻辑层，零 UI 依赖）
using System;
using System.Collections.Generic;
using System.Threading;

namespace CopyWithFolders.Core
{
    /// <summary>同名冲突处理策略。</summary>
    public enum CollisionPolicy
    {
        AutoRename = 0,   // 自动重命名：追加 " (1)"、" (2)"…
        Overwrite = 1,    // 直接覆盖
        Ask = 2,          // 每次询问（由 UI 层批量决策，见 ConflictChoice）
        Skip = 3          // 直接跳过
    }

    /// <summary>用户在"批量冲突询问"中的选择，会作用于本次任务的全部剩余项。</summary>
    public enum ConflictChoice
    {
        Ask = 0,
        OverwriteThis = 1,   // 仅覆盖当前
        OverwriteAll = 2,    // 覆盖全部剩余
        SkipThis = 3,         // 仅跳过当前
        SkipAll = 4,          // 跳过全部剩余
        Cancel = 5            // 取消整个任务
    }

    public enum CopyStatus
    {
        Success = 0,
        Skipped = 1,
        Failed = 2,
        Cancelled = 3
    }

    /// <summary>过滤规则：按名称通配符排除（作用于文件与目录）。</summary>
    public sealed class CopyFilter
    {
        /// <summary>排除模式，支持 * 与 ?，不区分大小写。例：*.tmp;~$*;.git;node_modules</summary>
        public List<string> ExcludePatterns { get; } = new List<string>();

        /// <summary>为 true 时跳过空目录（源目录中不含任何文件也不含非空子目录）。</summary>
        public bool SkipEmptyDirectories { get; set; }

        /// <summary>为 true 时复制完成后同步文件的最后写入时间。</summary>
        public bool PreserveTimestamps { get; set; }

        /// <summary>为 true 时遇到符号链接 / 目录联接直接跳过，避免成环导致死循环。</summary>
        public bool SkipReparsePoints { get; set; } = true;

        /// <summary>判断某个文件名是否应被排除。</summary>
        public bool IsExcluded(string name)
        {
            if (ExcludePatterns.Count == 0 || string.IsNullOrEmpty(name)) return false;
            foreach (var raw in ExcludePatterns)
            {
                string pat = raw.Trim();
                if (pat.Length == 0) continue;
                try
                {
                    if (name.Equals(pat, StringComparison.OrdinalIgnoreCase)) return true;
                    if (WildcardMatch(name, pat, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (ArgumentException) { /* 非法通配符忽略该条 */ }
            }
            return false;
        }

        internal static bool WildcardMatch(string input, string pattern, StringComparison cmp)
        {
            int i = 0, p = 0, star = -1, mark = 0;
            while (i < input.Length)
            {
                if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(input[i])))
                {
                    i++; p++;
                }
                else if (p < pattern.Length && pattern[p] == '*')
                {
                    star = p++; mark = i;
                }
                else if (star >= 0)
                {
                    p = star + 1; i = ++mark;
                }
                else return false;
            }
            while (p < pattern.Length && pattern[p] == '*') p++;
            return p == pattern.Length;
        }
    }

    /// <summary>进度回调载荷。</summary>
    public readonly struct CopyProgress
    {
        public readonly long DoneFiles;
        public readonly long TotalFiles;
        public readonly long DoneBytes;
        public readonly long TotalBytes;
        public readonly string CurrentItem;

        public CopyProgress(long doneFiles, long totalFiles, long doneBytes, long totalBytes, string currentItem)
        {
            DoneFiles = doneFiles;
            TotalFiles = totalFiles;
            DoneBytes = doneBytes;
            TotalBytes = totalBytes;
            CurrentItem = currentItem ?? "";
        }

        /// <summary>按文件数计算的比例（0~100），无法确定总量时返回 -1。</summary>
        public int Percent => TotalFiles > 0
            ? (int)Math.Min(100, DoneFiles * 100 / TotalFiles)
            : -1;
    }

    /// <summary>单项复制结果。</summary>
    public sealed class CopyItemResult
    {
        public string Source = "";
        public string Target = "";
        public CopyStatus Status = CopyStatus.Success;
        public string Message = "";

        public bool Success => Status == CopyStatus.Success;
        public bool Skipped => Status == CopyStatus.Skipped;
        public bool Failed => Status == CopyStatus.Failed;

        public override string ToString()
        {
            string tag = Status switch
            {
                CopyStatus.Success => "[OK]  ",
                CopyStatus.Skipped => "[SKIP]",
                CopyStatus.Cancelled => "[STOP]",
                _ => "[FAIL]"
            };
            return tag + " " + Source + "  ->  " + Target
                + (string.IsNullOrEmpty(Message) ? "" : "  ( " + Message + " )");
        }
    }

    /// <summary>单个待复制任务的描述（路径 + 父结构级别）。</summary>
    public readonly struct CopyJob
    {
        public readonly string Source;
        public readonly int Level;

        public CopyJob(string source, int level)
        {
            Source = source ?? "";
            Level = level;
        }

        public override string ToString() => Level == 0 ? Source : Source + " (+" + Level + ")";
    }

    /// <summary>一次复制任务的全部上下文。</summary>
    public sealed class CopyContext
    {
        public string DestRoot { get; set; } = "";
        public CollisionPolicy Policy { get; set; } = CollisionPolicy.AutoRename;
        public CopyFilter Filter { get; set; } = new CopyFilter();
        public CancellationToken Cancel { get; set; } = CancellationToken.None;

        /// <summary>
        /// 可选的取消令牌源。Ask 策略下用户选择「取消全部」时，
        /// 需要通过它把取消信号传给外层调度器（CancellationToken 本身只读）。
        /// </summary>
        public CancellationTokenSource? CancelSource { get; set; }

        /// <summary>请求取消：优先用 CancelSource，退化为直接置位标记。</summary>
        public void RequestCancel()
        {
            if (CancelSource != null)
            {
                try { CancelSource.Cancel(); }
                catch (ObjectDisposedException) { }
            }
            else
            {
                CancelRequested = true;
            }
        }

        /// <summary>无 CancelSource 时的取消标记（由 IsCancelled 统一判断）。</summary>
        public bool CancelRequested { get; private set; }

        /// <summary>是否已请求取消。</summary>
        public bool IsCancelled => Cancel.IsCancellationRequested || CancelRequested;

        /// <summary>总文件数 / 总字节数（由预扫描填充，用于确定进度）。</summary>
        public long TotalFiles { get; set; }
        public long TotalBytes { get; set; }

        /// <summary>进度回调，可从后台线程调用，实现方需自行 Invoke 到 UI。</summary>
        public Action<CopyProgress>? OnProgress { get; set; }

        /// <summary>批量冲突询问回调。仅在 Policy==Ask 且目标已存在时触发。
        /// 返回值语义见 ConflictChoice；非 UI 线程调用时实现方需自行切换线程。</summary>
        public Func<string, ConflictChoice>? OnConflict { get; set; }

        /// <summary>本次任务中累积的冲突决策（供后续项快速复用）。</summary>
        internal ConflictChoice StickyChoice { get; set; } = ConflictChoice.Ask;

        public CopyContext Clone()
        {
            return new CopyContext
            {
                DestRoot = DestRoot,
                Policy = Policy,
                Filter = Filter,
                Cancel = Cancel,
                CancelSource = CancelSource,
                CancelRequested = CancelRequested,
                TotalFiles = TotalFiles,
                TotalBytes = TotalBytes,
                OnProgress = OnProgress,
                OnConflict = OnConflict
            };
        }
    }

    /// <summary>任务汇总报告。</summary>
    public sealed class CopyReport
    {
        public List<CopyItemResult> Results { get; } = new List<CopyItemResult>();
        public int JobCount { get; set; }
        public int JobOk { get; set; }
        public int JobSkipped { get; set; }
        public int JobFailed { get; set; }
        public long FileCount { get; set; }
        public long TotalBytes { get; set; }
        public bool Cancelled { get; set; }
        public string DestRoot { get; set; } = "";

        public int FileFailed => 0; // 占位，实际由 Results 统计

        public string Summary
        {
            get
            {
                string head = Cancelled ? "已取消" : "完成";
                string s = head + "：任务 " + JobCount + " 项（成功 " + JobOk
                         + " · 跳过 " + JobSkipped + " · 失败 " + JobFailed + "），"
                         + "共处理文件 " + FileCount + " 个";
                if (!string.IsNullOrEmpty(DestRoot)) s += "  ->  " + DestRoot;
                return s;
            }
        }
    }
}
