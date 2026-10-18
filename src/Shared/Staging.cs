// ClipboardService.cs —— 系统剪贴板集成（Windows CF_HDROP / OLE IDataObject）
//
// 目标：把"待复制项 + 其父目录结构"写入系统剪贴板，使用户在任意文件夹按 Ctrl+V
//      即可像资源管理器复制一样粘贴，且自动保留父级目录结构。
//
// 原理：
//   1) 依据每项的父结构级别，在 %TEMP% 下构建一棵"结构暂存树"，
//      即 <Stage>\<相对路径>，例如 <Stage>\a\b\报告.docx；
//   2) 把暂存树的**顶层条目**（<Stage>\a）以 CF_HDROP 格式写入剪贴板；
//   3) 用户 Ctrl+V 时资源管理器把这批顶层条目粘贴进当前目录，
//      由于结构已固化在暂存树中，父目录结构得以完整保留；
//   4) 下次启动时清理过期暂存目录（避免污染磁盘）。
//
// 兼容性：
//   - CF_HDROP（DROPFILES 结构）为 Windows 原生文件拖放格式，
//     资源管理器、Office、VS Code、以及本程序自身的拖放控件均可识别；
//   - 同时提供 OLE IDataObject 通路，并在非 Windows 平台降级为纯逻辑实现，
//     保证逻辑层可独立测试。
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace CopyWithFolders.Core
{
    /// <summary>暂存树构建结果。</summary>
    public sealed class StageResult
    {
        /// <summary>暂存根目录（整体作为 CF_HDROP 的父目录存在）。</summary>
        public string StageRoot { get; set; } = "";

        /// <summary>应写入剪贴板的顶层条目绝对路径（每个都带完整父结构）。</summary>
        public List<string> ClipboardEntries { get; set; } = new List<string>();

        /// <summary>构建过程中的警告（被过滤、被跳过等）。</summary>
        public List<string> Warnings { get; set; } = new List<string>();

        /// <summary>实际落入暂存树的文件总数。</summary>
        public long FileCount { get; set; }

        public bool Success => ClipboardEntries.Count > 0;
    }

    /// <summary>构建暂存树所需的输入（一条 = 一个待复制项）。</summary>
    public readonly struct StageRequest
    {
        public readonly string Source;
        public readonly int Level;
        public readonly bool WrapInFolder;   // true 时强制保留至少 1 级

        public StageRequest(string source, int level, bool wrapInFolder)
        {
            Source = source ?? "";
            Level = level;
            WrapInFolder = wrapInFolder;
        }
    }
    public static class StagingBuilder
    {
        /// <summary>暂存根目录：%TEMP%\CopyWithFolders\Stage</summary>
        public static string StageBase
        {
            get
            {
                string t = Path.Combine(Path.GetTempPath(), "CopyWithFolders");
                return Path.Combine(t, "Stage");
            }
        }

        /// <summary>创建本次任务专用的暂存子目录（唯一命名，可安全并发）。</summary>
        public static string CreateStageRoot()
        {
            string root = Path.Combine(StageBase,
                DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(CopyLogic.LongPath(root));
            return root;
        }

        /// <summary>
        /// 把一批待复制项按各自级别物化到暂存树。
        /// </summary>
        public static StageResult Build(IEnumerable<StageRequest> requests, CopyContext ctx, string stageRoot)
        {
            var result = new StageResult { StageRoot = stageRoot };
            if (string.IsNullOrWhiteSpace(stageRoot))
                return result;

            try { Directory.CreateDirectory(CopyLogic.LongPath(stageRoot)); }
            catch (Exception ex)
            {
                result.Warnings.Add("无法创建暂存目录：" + ex.Message);
                return result;
            }

            // 记录已占用的顶层条目，避免同一顶层被重复登记
            var topEntries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var req in requests)
            {
                if (ctx.IsCancelled) break;

                string src = req.Source;
                if (string.IsNullOrWhiteSpace(src)) continue;

                bool isDir = Directory.Exists(CopyLogic.LongPath(src));
                bool isFile = File.Exists(CopyLogic.LongPath(src));
                if (!isDir && !isFile)
                {
                    result.Warnings.Add("跳过（源不存在）：" + src);
                    continue;
                }
                if (CopyLogic.IsRootDirectory(src))
                {
                    result.Warnings.Add("跳过（磁盘根目录）：" + src);
                    continue;
                }

                int level = req.Level;
                if (req.WrapInFolder && level < 1) level = 1;

                string rel = CopyLogic.BuildRelativePath(src, level);
                if (string.IsNullOrEmpty(rel))
                {
                    result.Warnings.Add("跳过（无法计算相对路径）：" + src);
                    continue;
                }

                // 冲突解析：与暂存树中已有项同名时按策略处理
                string stagedTarget = Path.Combine(stageRoot, rel);
                var (proceed, finalTarget, skipReason) = ResolveStaged(stagedTarget, isDir, ctx);
                if (!proceed)
                {
                    result.Warnings.Add("跳过：" + Path.GetFileName(TrimEnd(rel)) + "  (" + (skipReason ?? "已跳过") + ")");
                    continue;
                }

                var itemCtx = ctx.Clone();
                itemCtx.DestRoot = stageRoot;

                List<CopyItemResult> r;
                if (isDir)
                {
                    // 目录直接落到已解析的 finalTarget（避免二次解析改名）
                    r = CopyLogic.CopyDirectory(src, finalTarget, itemCtx);
                }
                else
                {
                    r = new List<CopyItemResult> { CopyLogic.CopyFile(src, finalTarget, itemCtx) };
                }

                long okCount = 0;
                foreach (var x in r)
                {
                    if (x.Status == CopyStatus.Success) okCount++;
                    else if (x.Failed) result.Warnings.Add("失败：" + x.Source + "  (" + x.Message + ")");
                }
                result.FileCount += okCount;

                // 重新读取 finalTarget 的相对路径（可能因重命名发生了变化）
                string finalRel = Relativize(finalTarget, stageRoot);
                string top = CopyLogic.FirstSegment(finalRel);
                if (!string.IsNullOrEmpty(top))
                {
                    string full = Path.Combine(stageRoot, top);
                    topEntries[top] = full;
                }
            }

            // 只保留确实存在的条目：启用"跳过空目录"时，暂存目录可能已被回收，
            // 若仍写进剪贴板会导致粘贴时源路径无效。
            foreach (var kv in topEntries)
            {
                if (File.Exists(CopyLogic.LongPath(kv.Value)) ||
                    Directory.Exists(CopyLogic.LongPath(kv.Value)))
                {
                    result.ClipboardEntries.Add(kv.Value);
                }
                else
                {
                    result.Warnings.Add("跳过（暂存内容为空，可能被\"跳过空目录\"回收）：" + kv.Key);
                }
            }
            return result;
        }

        private static string TrimEnd(string rel) => rel.TrimEnd('\\', '/');

        private static string Relativize(string full, string root)
        {
            string f = CopyLogic.TrimEndSeparators(Path.GetFullPath(full));
            string r = CopyLogic.TrimEndSeparators(Path.GetFullPath(root));
            if (f.Length > r.Length && f.StartsWith(r, StringComparison.OrdinalIgnoreCase))
                return f.Substring(r.Length).TrimStart('\\', '/');
            return Path.GetFileName(f);
        }

        /// <summary>暂存树内部的冲突解析（复用与落盘一致的策略语义）。</summary>
        private static (bool, string, string?) ResolveStaged(string target, bool isDir, CopyContext ctx)
        {
            string lp = CopyLogic.LongPath(target);
            bool exists = File.Exists(lp) || Directory.Exists(lp);
            if (!exists) return (true, target, null);

            switch (ctx.Policy)
            {
                case CollisionPolicy.AutoRename:
                    return (true, CopyLogic.GetUniqueTargetPath(target), null);
                case CollisionPolicy.Overwrite:
                    return (true, target, null);
                case CollisionPolicy.Skip:
                    return (false, target, "目标已存在");
                case CollisionPolicy.Ask:
                {
                    if (ctx.OnConflict == null) return (false, target, "无法询问");
                    var c = ctx.OnConflict(target);
                    switch (c)
                    {
                        case ConflictChoice.OverwriteThis:
                        case ConflictChoice.OverwriteAll: return (true, target, null);
                        case ConflictChoice.SkipAll: return (false, target, "批量跳过");
                        case ConflictChoice.Cancel:
                            ctx.RequestCancel();
                            return (false, target, "已取消");
                        default: return (false, target, "用户跳过");
                    }
                }
                default: return (true, target, null);
            }
        }

        /// <summary>
        /// 清理过期暂存目录。启动时调用，避免 %TEMP% 无限增长。
        /// </summary>
        public static int CleanupStale(int maxAgeHours = 24)
        {
            int removed = 0;
            try
            {
                string baseDir = StageBase;
                if (!Directory.Exists(CopyLogic.LongPath(baseDir))) return 0;
                var cutoff = DateTime.Now.AddHours(-maxAgeHours);
                foreach (string d in Directory.EnumerateDirectories(CopyLogic.LongPath(baseDir)))
                {
                    try
                    {
                        if (Directory.GetLastWriteTime(d) > cutoff) continue;
                        Directory.Delete(CopyLogic.LongPath(d), true);
                        removed++;
                    }
                    catch { /* 被占用则保留，下次再清 */ }
                }
            }
            catch { }
            return removed;
        }
    }
}
