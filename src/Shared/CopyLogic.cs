// CopyLogic.cs —— 纯逻辑层（零 UI 依赖，可独立测试）
// 负责：路径层级计算、重名处理、递归复制、安全护栏、预扫描、进度与取消。
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace CopyWithFolders.Core
{
    public static class CopyLogic
    {
        // ---------- 路径工具 ----------

        /// <summary>
        /// 补齐 Windows 长路径前缀（\\?\），使超过 260 字符的嵌套目录可正常读写。
        /// 非 Windows 平台直接返回原值。
        /// </summary>
        public static string LongPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
            if (path.StartsWith(@"\\", StringComparison.Ordinal))          // UNC
                return @"\\?\UNC\" + path.Substring(2);
            try
            {
                string full = Path.GetFullPath(path);
                return full.StartsWith(@"\\?\", StringComparison.Ordinal) ? full : @"\\?\" + full;
            }
            catch { return path; }
        }

        /// <summary>去掉末尾分隔符（卷根除外）。</summary>
        public static string TrimEndSeparators(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            string root = Path.GetPathRoot(path) ?? "";
            if (!string.IsNullOrEmpty(root) &&
                string.Equals(path.TrimEnd('\\', '/'), root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                return root;
            return path.TrimEnd('\\', '/');
        }

        /// <summary>是否为磁盘 / 卷根目录。</summary>
        public static bool IsRootDirectory(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                string full = Path.GetFullPath(path);
                string root = Path.GetPathRoot(full) ?? "";
                if (string.IsNullOrEmpty(root)) return false;
                return string.Equals(TrimEndSeparators(full), TrimEndSeparators(root), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>取相对路径的第一段（顶层目录名 / 文件名）。</summary>
        public static string FirstSegment(string relative)
        {
            if (string.IsNullOrEmpty(relative)) return "";
            int i = relative.IndexOfAny(new[] { '\\', '/' });
            return i < 0 ? relative : relative.Substring(0, i);
        }

        // ---------- 父结构层级计算 ----------

        /// <summary>
        /// 根据源路径与父目录级别数计算相对路径。
        /// level=0：仅文件名；level=1：文件名 + 1 级父目录名……直到根目录为止。
        /// 例：C:\a\b\c\file.txt，level=2 -&gt; b\c\file.txt
        /// </summary>
        public static string BuildRelativePath(string source, int parentLevels)
        {
            string full = Path.GetFullPath(source);
            if (!IsRootDirectory(full)) full = TrimEndSeparators(full);

            string fileName = Path.GetFileName(full);
            if (parentLevels <= 0) return fileName;
            if (string.IsNullOrEmpty(fileName)) return "";

            var segments = new List<string>();
            string current = Path.GetDirectoryName(full) ?? "";
            for (int i = 0; i < parentLevels && !string.IsNullOrEmpty(current); i++)
            {
                string seg = Path.GetFileName(current);
                if (string.IsNullOrEmpty(seg)) break;                       // 卷根没有名称
                segments.Insert(0, seg);
                string parent = Path.GetDirectoryName(current) ?? "";
                if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
                current = parent;
            }
            segments.Add(fileName);
            return Path.Combine(segments.ToArray());
        }

        public static string ComputeTargetPath(string source, string destRoot, int parentLevels)
        {
            return Path.Combine(Path.GetFullPath(destRoot), BuildRelativePath(source, parentLevels));
        }

        /// <summary>生成该源路径的全部可选父结构级别（0 级到根为止），供可视化选择器使用。</summary>
        public static List<(int Level, string Relative)> BuildLevelOptions(string source)
        {
            var options = new List<(int, string)>();
            string prev = "";
            for (int level = 0; level <= 64; level++)
            {
                string rel = BuildRelativePath(source, level);
                if (level > 0 && string.Equals(rel, prev, StringComparison.OrdinalIgnoreCase)) break;
                options.Add((level, rel));
                prev = rel;
            }
            return options;
        }

        /// <summary>该路径可选的最大父结构级别。</summary>
        public static int MaxLevel(string source)
        {
            var o = BuildLevelOptions(source);
            return o.Count == 0 ? 0 : o[o.Count - 1].Level;
        }

        // ---------- 重名处理 ----------

        /// <summary>自动重命名：目标已存在时追加 " (n)"，返回唯一路径。</summary>
        public static string GetUniqueTargetPath(string target)
        {
            if (!File.Exists(LongPath(target)) && !Directory.Exists(LongPath(target))) return target;
            string dir = Path.GetDirectoryName(target) ?? ".";
            string name = Path.GetFileNameWithoutExtension(target);
            string ext = Path.GetExtension(target);
            for (int i = 1; i < 10000; i++)
            {
                string candidate = Path.Combine(dir, name + " (" + i + ")" + ext);
                if (!File.Exists(LongPath(candidate)) && !Directory.Exists(LongPath(candidate))) return candidate;
            }
            throw new IOException("无法为目标生成唯一文件名: " + target);
        }

        /// <summary>
        /// 归一化路径用于比较：剥离 \\?\ 前缀与末尾分隔符。
        /// 关键点：Directory.EnumerateFileSystemEntries 以长路径前缀调用时，返回的条目路径
        /// 会带着 \\?\ 前缀；若与不带前缀的目标路径直接比较，前缀不一致会导致
        /// IsSameOrChild 恒为 false，递归防护形同虚设（曾导致无限递归）。
        /// </summary>
        internal static string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            string p = path;
            if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                p = @"\\" + p.Substring(8);
            else if (p.StartsWith(@"\\?\", StringComparison.Ordinal))
                p = p.Substring(4);

            try { p = TrimEndSeparators(Path.GetFullPath(p)); }
            catch { p = TrimEndSeparators(p); }
            return p;
        }

        /// <summary>判断 child 是否等于 ancestor 或是其子路径。</summary>
        public static bool IsSameOrChild(string child, string ancestor)
        {
            string c = Normalize(child);
            string a = Normalize(ancestor);
            if (c.Length == 0 || a.Length == 0) return false;
            if (string.Equals(c, a, StringComparison.OrdinalIgnoreCase)) return true;
            return c.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        // ---------- 安全护栏 ----------

        /// <summary>是否为符号链接 / 目录联接等重解析点。</summary>
        public static bool IsReparsePoint(string path)
        {
            try
            {
                FileAttributes a = File.GetAttributes(LongPath(path));
                return (a & FileAttributes.ReparsePoint) != 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// 枚举目录下的所有条目，返回**不带** \\?\ 前缀的普通路径。
        /// 这样调用方拼路径、比较路径时行为与短路径一致。
        /// </summary>
        private static List<string> EnumerateEntries(string dir)
        {
            var list = new List<string>();
            foreach (string e in Directory.EnumerateFileSystemEntries(LongPath(dir)))
                list.Add(StripLongPrefix(e));
            return list;
        }

        /// <summary>剥离 \\?\ 前缀（保留 UNC 的 \\ 形式）。</summary>
        public static string StripLongPrefix(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                return @"\\" + path.Substring(8);
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
                return path.Substring(4);
            return path;
        }

        private static bool ExistsAny(string path)
        {
            return File.Exists(LongPath(path)) || Directory.Exists(LongPath(path));
        }

        // ---------- 预扫描 ----------

        /// <summary>预扫描任务清单，统计总文件数与总字节数（用于确定进度）。</summary>
        public static (long Files, long Bytes) Scan(IEnumerable<CopyJob> jobs, CopyFilter filter,
                                                   CancellationToken cancel = default)
        {
            long files = 0, bytes = 0;
            foreach (var job in jobs)
            {
                if (cancel.IsCancellationRequested) break;
                string lp = LongPath(job.Source);
                if (File.Exists(lp))
                {
                    if (filter.IsExcluded(Path.GetFileName(TrimEndSeparators(job.Source)))) continue;
                    files++;
                    try { bytes += new FileInfo(job.Source).Length; } catch { }
                }
                else if (Directory.Exists(lp))
                {
                    if (filter.IsExcluded(Path.GetFileName(TrimEndSeparators(job.Source)))) continue;
                    ScanDir(job.Source, filter, cancel, ref files, ref bytes, 0);
                }
            }
            return (files, bytes);
        }

        private static void ScanDir(string dir, CopyFilter filter, CancellationToken cancel,
                                   ref long files, ref long bytes, int depth)
        {
            if (depth > 256 || cancel.IsCancellationRequested) return;
            try
            {
                foreach (string entry in EnumerateEntries(dir))
                {
                    if (cancel.IsCancellationRequested) return;
                    string name = Path.GetFileName(TrimEndSeparators(entry));
                    if (filter.IsExcluded(name)) continue;

                    FileAttributes attr;
                    try { attr = File.GetAttributes(LongPath(entry)); }
                    catch { continue; }

                    if ((attr & FileAttributes.ReparsePoint) != 0 && filter.SkipReparsePoints) continue;

                    if ((attr & FileAttributes.Directory) != 0)
                        ScanDir(entry, filter, cancel, ref files, ref bytes, depth + 1);
                    else
                    {
                        files++;
                        try { bytes += new FileInfo(entry).Length; } catch { }
                    }
                }
            }
            catch { /* 无权限等，扫描阶段忽略 */ }
        }

        // ---------- 冲突决策 ----------

        /// <summary>依据策略与用户决策，决定最终目标路径及是否应执行。</summary>
        private static (bool proceed, string target, string? skipReason) ResolveTarget(
            string target, bool isDir, CopyContext ctx, out ConflictChoice effective)
        {
            effective = ctx.StickyChoice;
            bool exists = ExistsAny(target);
            if (!exists) return (true, target, null);

            // 1) 本次任务中用户已做出批量决策
            if (ctx.StickyChoice != ConflictChoice.Ask)
            {
                if (ctx.StickyChoice == ConflictChoice.OverwriteAll) return (true, target, null);
                if (ctx.StickyChoice == ConflictChoice.SkipAll)
                    return (false, target, isDir ? "已跳过（批量决策：跳过全部）" : "已跳过（批量决策：跳过全部）");
            }

            // 2) 按策略自动处理
            switch (ctx.Policy)
            {
                case CollisionPolicy.AutoRename:
                    return (true, GetUniqueTargetPath(target), null);

                case CollisionPolicy.Overwrite:
                    return (true, target, null);

                case CollisionPolicy.Skip:
                    return (false, target, "已跳过（目标已存在）");

                case CollisionPolicy.Ask:
                {
                    if (ctx.OnConflict == null)
                        return (false, target, "已跳过（无法询问）");

                    ConflictChoice c = ctx.OnConflict(target);
                    effective = c;
                    switch (c)
                    {
                        case ConflictChoice.OverwriteThis: return (true, target, null);
                        case ConflictChoice.OverwriteAll:
                            ctx.StickyChoice = ConflictChoice.OverwriteAll;
                            return (true, target, null);
                        case ConflictChoice.SkipThis: return (false, target, "已跳过（用户选择跳过）");
                        case ConflictChoice.SkipAll:
                            ctx.StickyChoice = ConflictChoice.SkipAll;
                            return (false, target, "已跳过（批量决策：跳过全部）");
                        case ConflictChoice.Cancel:
                            ctx.StickyChoice = ConflictChoice.Cancel;
                            ctx.RequestCancel();
                            return (false, target, "任务已取消");
                        default:
                            return (false, target, "已跳过");
                    }
                }
                default:
                    return (true, target, null);
            }
        }

        // ---------- 进度 ----------

        private static long _doneFiles;
        private static long _doneBytes;
        private static string _current = "";

        /// <summary>后台线程入口调用，重置进度累加器。</summary>
        public static void ResetProgress() { _doneFiles = 0; _doneBytes = 0; _current = ""; }

        public static long DoneFiles => _doneFiles;
        public static long DoneBytes => _doneBytes;

        private static void Report(CopyContext ctx)
        {
            if (ctx.OnProgress == null) return;
            try
            {
                ctx.OnProgress(new CopyProgress(_doneFiles, ctx.TotalFiles, _doneBytes, ctx.TotalBytes, _current));
            }
            catch { /* UI 回调异常不打断复制 */ }
        }

        private static void Touch(string target, string source, CopyContext ctx)
        {
            if (!ctx.Filter.PreserveTimestamps) return;
            try
            {
                bool isDir = Directory.Exists(LongPath(target));
                var t = isDir ? Directory.GetLastWriteTime(source) : File.GetLastWriteTime(source);
                if (isDir) Directory.SetLastWriteTime(target, t);
                else File.SetLastWriteTime(target, t);
            }
            catch { }
        }

        // ---------- 复制原语 ----------

        /// <summary>复制单个文件。</summary>
        public static CopyItemResult CopyFile(string source, string target, CopyContext ctx)
        {
            var r = new CopyItemResult { Source = source, Target = target };

            if (ctx.IsCancelled)
            {
                r.Status = CopyStatus.Cancelled; r.Message = "已取消"; return r;
            }

            var (proceed, final, skipReason) = ResolveTarget(target, false, ctx, out _);
            r.Target = final;
            if (!proceed)
            {
                r.Status = skipReason != null && skipReason.Contains("取消")
                    ? CopyStatus.Cancelled : CopyStatus.Skipped;
                r.Message = skipReason ?? "";
                return r;
            }

            try
            {
                if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(final), StringComparison.OrdinalIgnoreCase))
                {
                    r.Status = CopyStatus.Skipped; r.Message = "源与目标相同，已跳过"; return r;
                }

                string fl = LongPath(final);
                if (File.Exists(fl)) File.SetAttributes(fl, FileAttributes.Normal);

                string? dir = Path.GetDirectoryName(final);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(LongPath(dir));

                _current = final;
                File.Copy(LongPath(source), fl, true);
                Touch(final, source, ctx);

                _doneFiles++;
                try { _doneBytes += new FileInfo(source).Length; } catch { }
                r.Status = CopyStatus.Success;
                Report(ctx);
                return r;
            }
            catch (Exception ex)
            {
                r.Status = CopyStatus.Failed; r.Message = ex.Message; return r;
            }
        }

        /// <summary>
        /// 取"目标锚点"：用于判断目标是否落在某个源目录内部。
        /// 优先用上下文里记录的目标根；否则退化为当前正在写入的目标目录。
        /// </summary>
        private static string DestAnchor(CopyContext ctx, string sourceDir, string finalDir)
        {
            return string.IsNullOrWhiteSpace(ctx.DestRoot)
                ? finalDir
                : ctx.DestRoot;
        }

        /// <summary>递归复制整个目录。目录级同样遵守 CollisionPolicy（关键修复点）。</summary>
        public static List<CopyItemResult> CopyDirectory(string sourceDir, string targetDir, CopyContext ctx)
        {
            var results = new List<CopyItemResult>();
            CopyDirectoryInternal(sourceDir, targetDir, ctx, results, 0);
            return results;
        }

        private static void CopyDirectoryInternal(string sourceDir, string targetDir, CopyContext ctx,
                                                  List<CopyItemResult> results, int depth)
        {
            if (depth > 256)
            {
                results.Add(new CopyItemResult
                {
                    Source = sourceDir, Target = targetDir, Status = CopyStatus.Failed,
                    Message = "目录层级过深，已中止（可能存在循环引用）"
                });
                return;
            }

            if (ctx.IsCancelled)
            {
                results.Add(new CopyItemResult
                {
                    Source = sourceDir, Target = targetDir, Status = CopyStatus.Cancelled, Message = "已取消"
                });
                return;
            }

            // 目录级冲突决策：AutoRename 重命名整个目录，Overwrite 才合并
            var (proceed, finalDir, skipReason) = ResolveTarget(targetDir, true, ctx, out _);
            if (!proceed)
            {
                results.Add(new CopyItemResult
                {
                    Source = sourceDir, Target = finalDir,
                    Status = skipReason != null && skipReason.Contains("取消")
                        ? CopyStatus.Cancelled : CopyStatus.Skipped,
                    Message = skipReason ?? ""
                });
                return;
            }

            try
            {
                Directory.CreateDirectory(LongPath(finalDir));
            }
            catch (Exception ex)
            {
                results.Add(new CopyItemResult
                {
                    Source = sourceDir, Target = finalDir, Status = CopyStatus.Failed, Message = ex.Message
                });
                return;
            }

            List<string> entries;
            try
            {
                entries = EnumerateEntries(sourceDir);
            }
            catch (Exception ex)
            {
                results.Add(new CopyItemResult
                {
                    Source = sourceDir, Target = finalDir, Status = CopyStatus.Failed, Message = ex.Message
                });
                return;
            }

            foreach (string entry in entries)
            {
                if (ctx.IsCancelled) break;

                string name = Path.GetFileName(TrimEndSeparators(entry));
                if (ctx.Filter.IsExcluded(name)) continue;

                FileAttributes attr;
                try { attr = File.GetAttributes(LongPath(entry)); }
                catch { continue; }

                bool isDir = (attr & FileAttributes.Directory) != 0;
                if ((attr & FileAttributes.ReparsePoint) != 0 && ctx.Filter.SkipReparsePoints)
                {
                    results.Add(new CopyItemResult
                    {
                        Source = entry, Target = Path.Combine(finalDir, name),
                        Status = CopyStatus.Skipped, Message = "已跳过（符号链接 / 目录联接）"
                    });
                    continue;
                }

                string child = Path.Combine(finalDir, name);

                if (isDir)
                {
                    // 关键护栏：若该源子目录内部包含我们的目标根（目标位于源内部），
                    // 继续递归就会把刚生成的输出目录再复制进自己，形成无限递归。
                    // 同时也拦截指向祖先的符号链接。
                    string destAnchor = DestAnchor(ctx, sourceDir, finalDir);
                    if (IsSameOrChild(destAnchor, entry))
                    {
                        results.Add(new CopyItemResult
                        {
                            Source = entry, Target = child, Status = CopyStatus.Skipped,
                            Message = "已跳过（目标位于该目录内部，避免循环）"
                        });
                        continue;
                    }
                    CopyDirectoryInternal(entry, child, ctx, results, depth + 1);
                }
                else
                {
                    results.Add(CopyFile(entry, child, ctx));
                }
            }

            // "跳过空目录"：复制完成后若目标目录仍然为空，则回收它。
            // 用实际的空判断（而不是"本次是否处理过条目"），
            // 因为子项也可能全部被策略跳过，最终仍是空目录。
            if (ctx.Filter.SkipEmptyDirectories && IsEmptyDirectory(finalDir))
            {
                try { Directory.Delete(LongPath(finalDir), true); } catch { }
            }
            else
            {
                Touch(finalDir, sourceDir, ctx);
            }
        }

        /// <summary>目录是否为空（既无文件也无子目录）。</summary>
        private static bool IsEmptyDirectory(string dir)
        {
            try
            {
                using var e = Directory.EnumerateFileSystemEntries(LongPath(dir)).GetEnumerator();
                return !e.MoveNext();
            }
            catch { return false; }
        }

        /// <summary>统一入口（不含父目录结构，等价 level=0）。</summary>
        public static List<CopyItemResult> CopyItem(string source, CopyContext ctx)
        {
            if (ctx.Filter.IsExcluded(Path.GetFileName(TrimEndSeparators(source))))
                return new List<CopyItemResult>();

            if (IsRootDirectory(source))
            {
                return new List<CopyItemResult>
                {
                    new CopyItemResult
                    {
                        Source = source, Target = source, Status = CopyStatus.Failed,
                        Message = "不支持复制磁盘根目录"
                    }
                };
            }

            string target = Path.Combine(ctx.DestRoot, BuildRelativePath(source, 0));

            if (Directory.Exists(LongPath(source)))
            {
                if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                    return new List<CopyItemResult>
                    {
                        new CopyItemResult
                        {
                            Source = source, Target = target, Status = CopyStatus.Skipped, Message = "源与目标相同，已跳过"
                        }
                    };
                return CopyDirectory(source, target, ctx);
            }

            if (File.Exists(LongPath(source)))
                return new List<CopyItemResult> { CopyFile(source, target, ctx) };

            return new List<CopyItemResult>
            {
                new CopyItemResult
                {
                    Source = source, Target = target, Status = CopyStatus.Failed, Message = "源文件/文件夹不存在"
                }
            };
        }

        /// <summary>按指定级别复制（保留 N 级父目录结构）。</summary>
        public static List<CopyItemResult> CopyItem(string source, CopyContext ctx, int parentLevels)
        {
            if (ctx.Filter.IsExcluded(Path.GetFileName(TrimEndSeparators(source))))
                return new List<CopyItemResult>();

            if (IsRootDirectory(source))
            {
                return new List<CopyItemResult>
                {
                    new CopyItemResult
                    {
                        Source = source, Target = source, Status = CopyStatus.Failed,
                        Message = "不支持复制磁盘根目录"
                    }
                };
            }

            string target = ComputeTargetPath(source, ctx.DestRoot, parentLevels);

            if (Directory.Exists(LongPath(source)))
            {
                if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                    return new List<CopyItemResult>
                    {
                        new CopyItemResult
                        {
                            Source = source, Target = target, Status = CopyStatus.Skipped, Message = "源与目标相同，已跳过"
                        }
                    };
                return CopyDirectory(source, target, ctx);
            }

            if (File.Exists(LongPath(source)))
                return new List<CopyItemResult> { CopyFile(source, target, ctx) };

            return new List<CopyItemResult>
            {
                new CopyItemResult
                {
                    Source = source, Target = target, Status = CopyStatus.Failed, Message = "源文件/文件夹不存在"
                }
            };
        }

        /// <summary>
        /// 执行整批任务。调用前应先调 Scan 填充 ctx.TotalFiles / TotalBytes。
        /// 目标目录不存在时自动创建。
        /// </summary>
        public static CopyReport RunBatch(IEnumerable<CopyJob> jobs, CopyContext ctx)
        {
            var report = new CopyReport { DestRoot = ctx.DestRoot };

            if (string.IsNullOrWhiteSpace(ctx.DestRoot))
            {
                report.JobCount = 1; report.JobFailed = 1;
                report.Results.Add(new CopyItemResult
                {
                    Source = "", Target = "", Status = CopyStatus.Failed, Message = "未指定目标目录"
                });
                return report;
            }

            try
            {
                Directory.CreateDirectory(LongPath(Path.GetFullPath(ctx.DestRoot)));
            }
            catch (Exception ex)
            {
                report.JobCount = 1; report.JobFailed = 1;
                report.Results.Add(new CopyItemResult
                {
                    Source = ctx.DestRoot, Target = ctx.DestRoot, Status = CopyStatus.Failed,
                    Message = "无法创建目标目录：" + ex.Message
                });
                return report;
            }

            ResetProgress();
            var jobList = new List<CopyJob>(jobs);
            report.JobCount = jobList.Count;

            foreach (var job in jobList)
            {
                if (ctx.IsCancelled) { report.Cancelled = true; break; }

                var results = CopyItem(job.Source, ctx, job.Level);
                bool anyFail = false, anyOk = false, anySkip = false;
                foreach (var r in results)
                {
                    report.Results.Add(r);
                    if (r.Status == CopyStatus.Failed) anyFail = true;
                    else if (r.Status == CopyStatus.Skipped || r.Status == CopyStatus.Cancelled) anySkip = true;
                    else if (r.Status == CopyStatus.Success) { anyOk = true; report.FileCount++; }
                }
                if (anyFail) report.JobFailed++;
                else if (anySkip && !anyOk) report.JobSkipped++;
                else report.JobOk++;

                Report(ctx);

                if (ctx.IsCancelled) { report.Cancelled = true; break; }
            }

            report.TotalBytes = _doneBytes;
            return report;
        }
    }
}
