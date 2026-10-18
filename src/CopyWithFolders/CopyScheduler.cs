// CopyScheduler.cs —— 复制任务调度（进度 / 取消 / 冲突决策 / 剪贴板模式）
//
// 两种模式：
//   ClipboardMode：把待复制项按各自父结构级别物化到 %TEMP% 暂存树，
//                  再把暂存树的顶层条目以 CF_HDROP 写入系统剪贴板。
//                  用户随后在任意文件夹 Ctrl+V，父结构自动保留。
//   DirectMode   ：直接落盘到指定目标目录（保留原有行为）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CopyWithFolders.Core;

namespace CopyWithFolders
{
    public sealed class CopyRequest
    {
        public List<PendingItem> Items { get; set; } = new();
        public string DestFolder { get; set; } = "";
        public CollisionPolicy Policy { get; set; } = CollisionPolicy.AutoRename;
        public CopyFilter Filter { get; set; } = new();
        public bool ClipboardMode { get; set; } = true;
        public bool WrapInFolder { get; set; }
        public HashSet<string> SkipConflicts { get; set; } = new();
    }

    /// <summary>调度器：所有后台工作在此收口，便于单测与复用。</summary>
    public sealed class CopyScheduler
    {
        private readonly IClipboardAccessor _clipboard;
        private readonly Func<string, ConflictChoice>? _askConflict;
        private readonly Action<string>? _log;
        private readonly IProgress<CopyProgress>? _progress;
        private readonly Action? _onStateChanged;

        public CancellationTokenSource? Cts { get; private set; }
        public bool IsBusy { get; private set; }
        public string StageRoot { get; private set; } = "";
        public string CurrentItem { get; private set; } = "";

        public CopyScheduler(
            IClipboardAccessor clipboard,
            Func<string, ConflictChoice>? askConflict,
            Action<string>? log,
            IProgress<CopyProgress>? progress,
            Action? onStateChanged)
        {
            _clipboard = clipboard;
            _askConflict = askConflict;
            _log = log;
            _progress = progress;
            _onStateChanged = onStateChanged;
        }

        /// <summary>执行一次复制任务（同步体，供 Task.Run 调用）。</summary>
        public CopyReport Execute(CopyRequest req)
        {
            var report = new CopyReport { DestRoot = req.DestFolder };
            Cts = new CancellationTokenSource();
            IsBusy = true;
            _onStateChanged?.Invoke();
            try
            {
                var ctx = BuildContext(req);
                var jobs = new List<CopyJob>();
                foreach (var it in req.Items)
                {
                    if (it.Excluded || !it.Exists) continue;
                    jobs.Add(new CopyJob(it.Path, it.Level));
                }

                var (files, bytes) = CopyLogic.Scan(jobs, req.Filter, ctx.Cancel);
                ctx.TotalFiles = files;
                ctx.TotalBytes = bytes;

                if (req.ClipboardMode)
                {
                    report = RunClipboard(req, ctx, files, bytes);
                }
                else
                {
                    report = CopyLogic.RunBatch(jobs, ctx);
                }
            }
            catch (Exception ex)
            {
                report.Results.Add(new CopyItemResult
                {
                    Source = "", Target = "", Status = CopyStatus.Failed, Message = "任务异常：" + ex.Message
                });
                report.JobFailed++;
                _log?.Invoke("任务异常：" + ex);
            }
            finally
            {
                IsBusy = false;
                Cts?.Dispose();
                Cts = null;
                _onStateChanged?.Invoke();
            }
            return report;
        }

        public void Cancel()
        {
            try { Cts?.Cancel(); } catch { }
        }

        private CopyContext BuildContext(CopyRequest req)
        {
            var ctx = new CopyContext
            {
                DestRoot = req.ClipboardMode ? "" : req.DestFolder,
                Policy = req.Policy,
                Filter = req.Filter,
                Cancel = Cts?.Token ?? CancellationToken.None,
                OnProgress = p =>
                {
                    CurrentItem = p.CurrentItem;
                    _progress?.Report(p);
                }
            };

            if (req.Policy == CollisionPolicy.Ask && _askConflict != null)
            {
                ctx.OnConflict = target =>
                {
                    // 用户在确认框中取消勾选的项直接跳过
                    return _askConflict(target);
                };
            }
            return ctx;
        }

        /// <summary>剪贴板模式：物化暂存树 -> 写入剪贴板。</summary>
        private CopyReport RunClipboard(CopyRequest req, CopyContext ctx, long files, long bytes)
        {
            var report = new CopyReport { DestRoot = "（系统剪贴板）" };

            _log?.Invoke("正在构建结构暂存树…");
            CopyLogic.ResetProgress();
            StageRoot = StagingBuilder.CreateStageRoot();
            _log?.Invoke("暂存目录：" + StageRoot);

            var requests = new List<StageRequest>();
            foreach (var it in req.Items)
            {
                if (it.Excluded || !it.Exists) continue;
                if (req.SkipConflicts.Contains(it.RelativePreview))
                {
                    report.JobSkipped++;
                    continue;
                }
                requests.Add(new StageRequest(it.Path, it.Level, req.WrapInFolder));
            }
            report.JobCount = requests.Count;

            var stage = StagingBuilder.Build(requests, ctx, StageRoot);

            foreach (var w in stage.Warnings) _log?.Invoke("[提示] " + w);

            long filesDone = CopyLogic.DoneFiles;
            report.FileCount = filesDone;
            report.TotalBytes = CopyLogic.DoneBytes;

            if (!stage.Success)
            {
                report.JobFailed = Math.Max(1, requests.Count);
                report.Results.Add(new CopyItemResult
                {
                    Source = "", Target = StageRoot, Status = CopyStatus.Failed,
                    Message = "没有可写入剪贴板的内容"
                });
                return report;
            }

            // 组装元信息并写入剪贴板（两种格式一次性写入，避免互相覆盖）
            var meta = WindowsClipboardAccessor.BuildMeta(requests);
            bool ok;
            if (_clipboard is WindowsClipboardAccessor wca)
            {
                ok = wca.PutAll(stage.ClipboardEntries, meta);
            }
            else
            {
                // 内存/其他实现：先写文件列表，再补元信息
                ok = _clipboard.SetFileDrop(stage.ClipboardEntries);
                if (ok)
                {
                    try
                    {
                        _clipboard.SetCustom(WindowsClipboardAccessor.CustomFormat,
                            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(meta));
                    }
                    catch { /* 元信息可选 */ }
                }
            }

            if (!ok)
            {
                report.JobFailed++;
                report.Results.Add(new CopyItemResult
                {
                    Source = "", Target = StageRoot, Status = CopyStatus.Failed,
                    Message = "写入系统剪贴板失败（可能被其他程序独占）"
                });
                _log?.Invoke("警告：系统剪贴板写入失败，请稍后重试或关闭占用剪贴板的程序。");
                return report;
            }

            report.JobOk = requests.Count;
            _log?.Invoke("已写入系统剪贴板，共 " + stage.ClipboardEntries.Count + " 个顶层项、" + filesDone + " 个文件。");
            _log?.Invoke("现在到任意文件夹按 Ctrl+V 即可粘贴，父目录结构会自动保留。");
            return report;
        }

        /// <summary>估算本次任务涉及的同名冲突（用于预检）。</summary>
        public static List<string> EstimateConflicts(CopyRequest req)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(req.DestFolder)) return result;

            string root = req.DestFolder;
            foreach (var it in req.Items)
            {
                if (it.Excluded || !it.Exists) continue;
                string rel = it.RelativePreview;
                string full = Path.Combine(root, rel);
                if (File.Exists(CopyLogic.LongPath(full)) || Directory.Exists(CopyLogic.LongPath(full)))
                {
                    if (!result.Contains(rel)) result.Add(rel);
                }
            }
            return result;
        }
    }
}
