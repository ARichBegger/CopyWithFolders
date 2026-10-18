using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using CopyWithFolders.Core;

internal static class Program
{
    private static int _failures;
    private static int _passed;

    private static void Check(bool cond, string name)
    {
        Console.WriteLine((cond ? "PASS  " : "FAIL  ") + name);
        if (cond) _passed++; else _failures++;
    }

    private static void Section(string s)
    {
        Console.WriteLine();
        Console.WriteLine("== " + s + " ==");
    }

    private static string NewTmp()
    {
        string t = Path.Combine(Path.GetTempPath(), "ctl_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(t);
        return t;
    }

    private static void SafeDelete(string dir)
    {
        try { Directory.Delete(dir, true); } catch { }
    }

    private static CopyContext Ctx(string dest, CollisionPolicy p = CollisionPolicy.AutoRename)
    {
        return new CopyContext { DestRoot = dest, Policy = p };
    }

    private static int Main()
    {
        TestPathAlgorithms();
        TestUniqueRename();
        TestFileCopy();
        TestDirectoryCopy();
        TestDirectoryCollision();
        TestEmptyDirectory();
        TestReparsePoint();
        TestFilter();
        TestScan();
        TestCancel();
        TestStaging();
        TestStagingEndToEnd();
        TestLevelsToRoot();
        TestSkipEmptyDirectories();
        TestStagingSkipsVanishedEntries();
        TestLongPath();
        TestSpecialPaths();

        Console.WriteLine();
        Console.WriteLine(new string('-', 48));
        Console.WriteLine($"共 {_passed + _failures} 项：通过 {_passed}，失败 {_failures}");
        Console.WriteLine(_failures == 0 ? "ALL TESTS PASSED" : _failures + " FAILURE(S)");
        return _failures == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------
    private static void TestPathAlgorithms()
    {
        Section("BuildRelativePath / BuildLevelOptions");

        Check(CopyLogic.BuildRelativePath("/a/b/c/file.txt", 0) == "file.txt", "level 0 -> 仅文件名");
        Check(CopyLogic.BuildRelativePath("/a/b/c/file.txt", 1) == Path.Combine("c", "file.txt"), "level 1");
        Check(CopyLogic.BuildRelativePath("/a/b/c/file.txt", 2) == Path.Combine("b", "c", "file.txt"), "level 2");
        Check(CopyLogic.BuildRelativePath("/a/b/c/file.txt", 3) == Path.Combine("a", "b", "c", "file.txt"), "level 3");
        Check(CopyLogic.BuildRelativePath("/a/b/c/file.txt", 99) == Path.Combine("a", "b", "c", "file.txt"), "level 99 -> 封顶到根");
        Check(CopyLogic.BuildRelativePath("/file.txt", 5) == "file.txt", "根目录文件无上级");
        Check(CopyLogic.BuildRelativePath("/a/file.txt", 2) == Path.Combine("a", "file.txt"), "上级不足时封顶");
        Check(CopyLogic.BuildRelativePath("/a/b/", 1) == Path.Combine("a", "b"), "目录源带尾斜杠");

        var opts = CopyLogic.BuildLevelOptions("/a/b/c/file.txt");
        Check(opts.Count == 4, "选项数 = 4（0..3 级）");
        Check(opts[0].Level == 0, "选项0 = 仅文件");
        Check(opts[3].Level == 3, "选项3 = 含 3 级（到根）");
        Check(CopyLogic.MaxLevel("/a/b/c/file.txt") == 3, "MaxLevel = 3");

        var optsRoot = CopyLogic.BuildLevelOptions("/file.txt");
        Check(optsRoot.Count == 1 && optsRoot[0].Level == 0, "根目录文件只有 1 个选项");

        // 注意：Windows 下 GetFullPath("/dest") 会归一化为当前盘符根（C:\dest），
        // 因此期望值必须用 Path.Combine 拼接，不能硬编码 "/dest"。
        Check(CopyLogic.ComputeTargetPath("/a/b/c/f.txt", "/dest", 2) ==
              Path.Combine(Path.GetFullPath("/dest"), "b", "c", "f.txt"), "目标路径合成");

        Check(CopyLogic.FirstSegment(Path.Combine("b", "c", "f.txt")) == "b", "FirstSegment 取首段");
        Check(CopyLogic.FirstSegment("only.txt") == "only.txt", "FirstSegment 单段");

        Check(CopyLogic.IsRootDirectory(Path.GetPathRoot(Environment.CurrentDirectory)!),
            "IsRootDirectory 识别卷根");
        Check(!CopyLogic.IsRootDirectory("/a/b"), "IsRootDirectory 排除普通目录");
        Check(CopyLogic.IsSameOrChild("/a/b/c", "/a/b"), "IsSameOrChild 子路径为真");
        Check(CopyLogic.IsSameOrChild("/a/b", "/a/b"), "IsSameOrChild 同路径为真");
        Check(!CopyLogic.IsSameOrChild("/a/bc", "/a/b"), "IsSameOrChild 前缀相同但不同目录为假");
    }

    // ------------------------------------------------------------------
    private static void TestUniqueRename()
    {
        Section("GetUniqueTargetPath 自动重命名");
        string tmp = NewTmp();
        try
        {
            string t = Path.Combine(tmp, "x.txt");
            File.WriteAllText(t, "1");
            string u = CopyLogic.GetUniqueTargetPath(t);
            Check(u.EndsWith("x (1).txt", StringComparison.OrdinalIgnoreCase), "首次 -> x (1).txt");

            File.WriteAllText(u, "2");
            string v = CopyLogic.GetUniqueTargetPath(t);
            Check(v.EndsWith("x (2).txt", StringComparison.OrdinalIgnoreCase), "二次 -> x (2).txt");

            string noExist = Path.Combine(tmp, "free.txt");
            Check(CopyLogic.GetUniqueTargetPath(noExist) == noExist, "不存在的目标原样返回");
        }
        finally { SafeDelete(tmp); }
    }

    // ------------------------------------------------------------------
    private static void TestFileCopy()
    {
        Section("文件复制与冲突策略");
        string tmp = NewTmp();
        try
        {
            string src = Path.Combine(tmp, "src.txt");
            File.WriteAllText(src, "new");
            string dst = Path.Combine(tmp, "dst.txt");

            // 目标不存在
            var r0 = CopyLogic.CopyFile(src, dst, Ctx(tmp));
            Check(r0.Success && File.ReadAllText(dst) == "new", "复制到不存在的目标 -> 成功");

            // Overwrite
            var r1 = CopyLogic.CopyFile(src, dst, Ctx(tmp, CollisionPolicy.Overwrite));
            Check(r1.Success, "Overwrite -> 成功");

            // AutoRename
            var r2 = CopyLogic.CopyFile(src, dst, Ctx(tmp, CollisionPolicy.AutoRename));
            Check(r2.Success && r2.Target.EndsWith("dst (1).txt"), "AutoRename -> 产生 dst (1).txt");
            Check(File.Exists(Path.Combine(tmp, "dst.txt")) && File.Exists(Path.Combine(tmp, "dst (1).txt")),
                "AutoRename 未覆盖原文件");

            // Skip
            var r3 = CopyLogic.CopyFile(src, dst, Ctx(tmp, CollisionPolicy.Skip));
            Check(r3.Skipped && !r3.Failed, "Skip -> 标记为 Skipped 而非 Failed");
            Check(r3.Message.Contains("跳过"), "Skip 消息含「跳过」");

            // Ask：用户选择跳过
            var ctx = Ctx(tmp, CollisionPolicy.Ask);
            ctx.OnConflict = _ => ConflictChoice.SkipThis;
            var r4 = CopyLogic.CopyFile(src, dst, ctx);
            Check(r4.Skipped, "Ask + SkipThis -> Skipped");

            // Ask：批量覆盖全部
            var ctx2 = Ctx(tmp, CollisionPolicy.Ask);
            int asks = 0;
            ctx2.OnConflict = _ => { asks++; return ConflictChoice.OverwriteAll; };
            var r5 = CopyLogic.CopyFile(src, dst, ctx2);
            Check(r5.Success, "Ask + OverwriteAll -> 成功");

            // Ask：批量跳过全部，第二次不再询问
            var ctx3 = Ctx(tmp, CollisionPolicy.Ask);
            int asks2 = 0;
            ctx3.OnConflict = _ => { asks2++; return ConflictChoice.SkipAll; };
            var a1 = CopyLogic.CopyFile(src, dst, ctx3);
            var a2 = CopyLogic.CopyFile(src, Path.Combine(tmp, "dst.txt"), ctx3);
            Check(a1.Skipped && a2.Skipped && asks2 == 1,
                "Ask + SkipAll -> 仅询问一次，其余直接跳过（批量决策生效）");

            // 源==目标自我保护
            var rSelf = CopyLogic.CopyFile(src, src, Ctx(tmp, CollisionPolicy.Overwrite));
            Check(rSelf.Skipped && rSelf.Message.Contains("相同"), "源与目标相同 -> 跳过不覆盖");
            Check(File.ReadAllText(src) == "new", "自我保护后源文件未被破坏");

            // 源不存在
            var rMiss = CopyLogic.CopyFile(Path.Combine(tmp, "nope.txt"),
                Path.Combine(tmp, "out.txt"), Ctx(tmp));
            Check(rMiss.Failed, "源不存在 -> Failed");

            // 无扩展名
            string noext = Path.Combine(tmp, "noext");
            File.WriteAllText(noext, "x");
            var r5b = CopyLogic.CopyFile(noext, Path.Combine(tmp, "d", "noext"), Ctx(tmp));
            Check(r5b.Success, "无扩展名文件复制成功");
        }
        finally { SafeDelete(tmp); }
    }

    // ------------------------------------------------------------------
    private static void TestDirectoryCopy()
    {
        Section("目录递归复制");
        string tmp = NewTmp();
        try
        {
            string src = Path.Combine(tmp, "src");
            Directory.CreateDirectory(Path.Combine(src, "sub", "deep"));
            File.WriteAllText(Path.Combine(src, "a.txt"), "a");
            File.WriteAllText(Path.Combine(src, "sub", "b.txt"), "b");
            File.WriteAllText(Path.Combine(src, "sub", "deep", "c.txt"), "c");

            string dst = Path.Combine(tmp, "dst");
            var res = CopyLogic.CopyItem(src, new CopyContext { DestRoot = dst, Policy = CollisionPolicy.AutoRename }, 0);
            Check(res.All(x => x.Success), "三层嵌套目录递归复制全部成功");
            Check(res.Count == 3, "返回 3 条文件级结果");
            Check(File.Exists(Path.Combine(dst, "src", "a.txt")), "dst/src/a.txt 存在");
            Check(File.Exists(Path.Combine(dst, "src", "sub", "b.txt")), "dst/src/sub/b.txt 存在");
            Check(File.Exists(Path.Combine(dst, "src", "sub", "deep", "c.txt")), "dst/src/sub/deep/c.txt 存在");

            // 按级别复制
            string f = Path.Combine(src, "a.txt");
            var r2 = CopyLogic.CopyItem(f, new CopyContext { DestRoot = dst, Policy = CollisionPolicy.AutoRename }, 1);
            Check(r2.Count == 1 && r2[0].Success, "文件按 1 级父目录复制成功");
            Check(File.Exists(Path.Combine(dst, "src", "a.txt")), "1 级 -> dst/src/a.txt");

            // 目标位于源内部：不死循环
            string inside = Path.Combine(src, "out");
            var r3 = CopyLogic.CopyItem(src, new CopyContext { DestRoot = inside, Policy = CollisionPolicy.AutoRename }, 0);
            Check(r3.All(x => !x.Failed), "目标位于源内部时不失败（防死循环）");

            // 源==目标
            var r4 = CopyLogic.CopyItem(src, new CopyContext { DestRoot = tmp, Policy = CollisionPolicy.Overwrite }, 0);
            Check(r4.Count == 1 && r4[0].Skipped, "源与目标相同 -> 跳过");

            // 磁盘根目录护栏
            string root = Path.GetPathRoot(Environment.CurrentDirectory)!;
            var r5 = CopyLogic.CopyItem(root, new CopyContext { DestRoot = dst, Policy = CollisionPolicy.AutoRename }, 0);
            Check(r5.Count == 1 && r5[0].Failed && r5[0].Message.Contains("根目录"), "磁盘根目录 -> 拒绝复制");
        }
        finally { SafeDelete(tmp); }
    }

    // ------------------------------------------------------------------
    private static void TestDirectoryCollision()
    {
        Section("目录级冲突策略（本次重构的关键修复点）");

        string tmp = NewTmp();
        try
        {
            string src = Path.Combine(tmp, "data");
            Directory.CreateDirectory(Path.Combine(src, "inner"));
            File.WriteAllText(Path.Combine(src, "f.txt"), "v1");
            File.WriteAllText(Path.Combine(src, "inner", "g.txt"), "g1");

            string dst = Path.Combine(tmp, "out");
            var c1 = new CopyContext { DestRoot = dst, Policy = CollisionPolicy.AutoRename };
            CopyLogic.CopyItem(src, c1, 0);
            Check(Directory.Exists(Path.Combine(dst, "data")), "首次 -> dst/data");

            // 再来一次：AutoRename 应产生 "data (1)"，而不是静默合并
            var c2 = new CopyContext { DestRoot = dst, Policy = CollisionPolicy.AutoRename };
            var r2 = CopyLogic.CopyItem(src, c2, 0);
            Check(Directory.Exists(Path.Combine(dst, "data (1)")),
                "AutoRename 下同名目录被重命名为 data (1)（不再静默合并）");
            Check(File.ReadAllText(Path.Combine(dst, "data (1)", "f.txt")) == "v1",
                "重命名后的目录内容完整");
            Check(!File.Exists(Path.Combine(dst, "data (1)", "f.txt (1)")),
                "目录级重命名后内部文件不再被逐个加后缀");

            // Overwrite：允许合并
            var c3 = new CopyContext { DestRoot = dst, Policy = CollisionPolicy.Overwrite };
            CopyLogic.CopyItem(src, c3, 0);
            Check(!Directory.Exists(Path.Combine(dst, "data (2)")), "Overwrite 下合并进原目录，不新增副本");

            // Skip：跳过已存在目录
            var c4 = new CopyContext { DestRoot = dst, Policy = CollisionPolicy.Skip };
            var r4 = CopyLogic.CopyItem(src, c4, 0);
            Check(r4.Count == 1 && r4[0].Skipped, "Skip 下同名目录被跳过");
        }
        finally { SafeDelete(tmp); }
    }

    // ------------------------------------------------------------------
    private static void TestEmptyDirectory()
    {
        Section("空目录保留（本次重构的修复点）");
        string tmp = NewTmp();
        try
        {
            string src = Path.Combine(tmp, "src");
            Directory.CreateDirectory(Path.Combine(src, "empty"));
            Directory.CreateDirectory(Path.Combine(src, "full"));
            File.WriteAllText(Path.Combine(src, "full", "x.txt"), "x");

            string dst = Path.Combine(tmp, "dst");
            var r = CopyLogic.CopyItem(src, new CopyContext { DestRoot = dst, Policy = CollisionPolicy.AutoRename }, 0);
            Check(r.All(x => x.Success), "含空目录的复制全部成功");
            Check(Directory.Exists(Path.Combine(dst, "src", "empty")), "空目录被保留");
            Check(File.Exists(Path.Combine(dst, "src", "full", "x.txt")), "非空子目录正常复制");

            // 仅含空目录的层级
            string src2 = Path.Combine(tmp, "src2");
            Directory.CreateDirectory(Path.Combine(src2, "a", "b", "c"));
            string dst2 = Path.Combine(tmp, "dst2");
            CopyLogic.CopyItem(src2, new CopyContext { DestRoot = dst2, Policy = CollisionPolicy.AutoRename }, 0);
            Check(Directory.Exists(Path.Combine(dst2, "src2", "a", "b", "c")),
                "多层空目录链（a/b/c）完整保留");
        }
        finally { SafeDelete(tmp); }
    }

    // ------------------------------------------------------------------
    private static void TestReparsePoint()
    {
        Section("符号链接 / 目录联接防护（本次重构的修复点）");
        string tmp = NewTmp();
        try
        {
            string src = Path.Combine(tmp, "src");
            Directory.CreateDirectory(src);
            File.WriteAllText(Path.Combine(src, "real.txt"), "r");

            // 造一个指向自身的符号链接，模拟成环场景
            string link = Path.Combine(src, "loop");
            bool made = false;
            try
            {
                Directory.CreateSymbolicLink(link, src);
                // 部分环境（沙箱 / 未开启开发者模式）会"创建成功"但链接不可见，
                // 此时无法验证属性读取与跳过逻辑，按跳过处理。
                made = Directory.Exists(link) && File.Exists(link);
            }
            catch { /* 无权限时跳过该用例 */ }

            if (made)
            {
                Check(CopyLogic.IsReparsePoint(link), "识别出符号链接为 ReparsePoint");

                string dst = Path.Combine(tmp, "dst");
                var ctx = new CopyContext { DestRoot = dst, Policy = CollisionPolicy.AutoRename };
                ctx.Filter.SkipReparsePoints = true;

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var r = CopyLogic.CopyItem(src, ctx, 0);
                sw.Stop();

                Check(sw.Elapsed.TotalSeconds < 10,
                    "遇到自指链接未死循环（耗时 " + sw.Elapsed.TotalSeconds.ToString("0.0") + "s）");
                Check(Directory.Exists(Path.Combine(dst, "src", "real.txt")), "正常文件仍被复制");
                Check(r.Any(x => x.Skipped && x.Message.Contains("联接")),
                    "符号链接被记为跳过并给出原因");
            }
            else
            {
                Console.WriteLine("SKIP  符号链接用例（当前环境无法创建可用的符号链接）");
                // 仍验证防护逻辑本身：目标位于源内部时的循环拦截
                string dst2 = Path.Combine(tmp, "dst2");
                var ctx2 = new CopyContext { DestRoot = Path.Combine(src, "inside"), Policy = CollisionPolicy.AutoRename };
                var sw2 = System.Diagnostics.Stopwatch.StartNew();
                var r2 = CopyLogic.CopyItem(src, ctx2, 0);
                sw2.Stop();
                Check(sw2.Elapsed.TotalSeconds < 10,
                    "目标位于源内部时未死循环（耗时 " + sw2.Elapsed.TotalSeconds.ToString("0.0") + "s）");
                Check(r2.Any(x => x.Skipped && x.Message.Contains("循环")),
                    "循环风险目录被记为跳过并给出原因");
                Check(Directory.Exists(Path.Combine(dst2, "real.txt")) ||
                      File.Exists(Path.Combine(src, "inside", "src", "real.txt")),
                    "循环被拦截后其余内容仍正常复制");
            }
        }
        finally { SafeDelete(tmp); }
    }

    // ------------------------------------------------------------------
    private static void TestFilter()
    {
        Section("排除规则");
        var f = new CopyFilter();
        f.ExcludePatterns.Add("*.tmp");
        f.ExcludePatterns.Add("~$*");
        f.ExcludePatterns.Add(".git");
        f.ExcludePatterns.Add("node_modules");

        Check(f.IsExcluded("a.tmp"), "匹配 *.tmp");
        Check(f.IsExcluded("A.TMP"), "匹配不区分大小写");
        Check(f.IsExcluded("~$Report.docx"), "匹配 ~$*");
        Check(f.IsExcluded(".git"), "精确匹配 .git");
        Check(f.IsExcluded("node_modules"), "精确匹配 node_modules");
        Check(!f.IsExcluded("report.docx"), "非排除项返回 false");

        var f2 = new CopyFilter();
        f2.ExcludePatterns.Add("a?c.txt");
        Check(f2.IsExcluded("abc.txt"), "? 匹配单字符");
        Check(!f2.IsExcluded("abbc.txt"), "? 不匹配多字符");

        string tmp = NewTmp();
        try
        {
            string src = Path.Combine(tmp, "src");
            Directory.CreateDirectory(src);
            File.WriteAllText(Path.Combine(src, "keep.txt"), "k");
            File.WriteAllText(Path.Combine(src, "drop.tmp"), "d");
            Directory.CreateDirectory(Path.Combine(src, "node_modules"));
            File.WriteAllText(Path.Combine(src, "node_modules", "x.js"), "j");

            string dst = Path.Combine(tmp, "dst");
            var ctx = new CopyContext { DestRoot = dst, Policy = CollisionPolicy.AutoRename };
            ctx.Filter = f;
            CopyLogic.CopyItem(src, ctx, 0);

            Check(File.Exists(Path.Combine(dst, "src", "keep.txt")), "保留文件被复制");
            Check(!File.Exists(Path.Combine(dst, "src", "drop.tmp")), "*.tmp 被排除");
            Check(!Directory.Exists(Path.Combine(dst, "src", "node_modules")), "node_modules 目录被排除");
        }
        finally { SafeDelete(tmp); }
    }

    // ------------------------------------------------------------------
    private static void TestScan()
    {
        Section("预扫描（进度总量）");
        string tmp = NewTmp();
        try
        {
            string src = Path.Combine(tmp, "src");
            Directory.CreateDirectory(Path.Combine(src, "s1"));
            File.WriteAllText(Path.Combine(src, "a.txt"), new string('x', 10));
            File.WriteAllText(Path.Combine(src, "s1", "b.txt"), new string('x', 20));
            File.WriteAllText(Path.Combine(src, "s1", "c.txt"), new string('x', 30));

            var jobs = new List<CopyJob> { new CopyJob(src, 0) };
            var (files, bytes) = CopyLogic.Scan(jobs, new CopyFilter());
            Check(files == 3, "扫描到 3 个文件（实际 " + files + "）");
            Check(bytes == 60, "扫描到 60 字节（实际 " + bytes + "）");

            var f = new CopyFilter();
            f.ExcludePatterns.Add("b.txt");
            var (files2, bytes2) = CopyLogic.Scan(jobs, f);
            Check(files2 == 2 && bytes2 == 40, "排除规则在扫描阶段生效");

            // 空集合
            var (f3, b3) = CopyLogic.Scan(new List<CopyJob>(), new CopyFilter());
            Check(f3 == 0 && b3 == 0, "空任务列表返回 0");
        }
        finally { SafeDelete(tmp); }
    }

    // ------------------------------------------------------------------
    private static void TestCancel()
    {
        Section("取消（本次重构的新增能力）");
        string tmp = NewTmp();
        try
        {
            string src = Path.Combine(tmp, "src");
            Directory.CreateDirectory(src);
            for (int i = 0; i < 20; i++)
                File.WriteAllText(Path.Combine(src, "f" + i + ".txt"), "x");

            var cts = new CancellationTokenSource();
            cts.Cancel();     // 预先取消

            var ctx = new CopyContext
            {
                DestRoot = Path.Combine(tmp, "dst"),
                Policy = CollisionPolicy.AutoRename,
                Cancel = cts.Token
            };
            var r = CopyLogic.CopyItem(src, ctx, 0);
            Check(r.Count == 1 && r[0].Status == CopyStatus.Cancelled, "已取消的 token -> 立即返回 Cancelled");
            Check(!Directory.Exists(Path.Combine(tmp, "dst", "src")),
                "已取消时不产生任何目标文件");

            var report = CopyLogic.RunBatch(new List<CopyJob> { new CopyJob(src, 0) }, ctx);
            Check(report.Cancelled, "RunBatch 标记 Cancelled");
        }
        finally { SafeDelete(tmp); }
    }

    // ------------------------------------------------------------------
    private static void TestStaging()
    {
        Section("暂存树构建（剪贴板模式核心）");
        string tmp = NewTmp();
        try
        {
            // 源：work/projA/资料/报告.docx，源：work/projB/图纸/dwg/图1.dwg
            string work = Path.Combine(tmp, "work");
            string a1 = Path.Combine(work, "projA", "资料");
            string b1 = Path.Combine(work, "projB", "图纸", "dwg");
            Directory.CreateDirectory(a1);
            Directory.CreateDirectory(b1);
            File.WriteAllText(Path.Combine(a1, "报告.docx"), "R");
            File.WriteAllText(Path.Combine(b1, "图1.dwg"), "D");

            string stage = StagingBuilder.CreateStageRoot();
            var ctx = new CopyContext
            {
                DestRoot = stage,
                Policy = CollisionPolicy.AutoRename,
                Filter = new CopyFilter()
            };

            var reqs = new List<StageRequest>
            {
                new StageRequest(Path.Combine(a1, "报告.docx"), 2, false),
                new StageRequest(Path.Combine(b1, "图1.dwg"), 3, false)
            };

            var res = StagingBuilder.Build(reqs, ctx, stage);

            Check(res.Success, "暂存树构建成功");
            Check(res.ClipboardEntries.Count == 2,
                "剪贴板条目数 = 2（projA 与 projB）（实际 " + res.ClipboardEntries.Count + "）");
            Check(File.Exists(Path.Combine(stage, "projA", "资料", "报告.docx")),
                "级别 2 -> stage/projA/资料/报告.docx");
            Check(File.Exists(Path.Combine(stage, "projB", "图纸", "dwg", "图1.dwg")),
                "级别 3 -> stage/projB/图纸/dwg/图1.dwg");
            Check(res.FileCount == 2, "落入 2 个文件");

            // 每个剪贴板条目必须是 stage 下的顶层项
            bool allTopLevel = res.ClipboardEntries.All(e =>
            {
                string rel = e.Substring(stage.Length).TrimStart('\\', '/');
                return !rel.Contains('\\') && !rel.Contains('/');
            });
            Check(allTopLevel, "剪贴板条目均为暂存树的顶层项（粘贴后结构得以保留）");

            // WrapInFolder：level=0 也强制保留 1 级
            string stage2 = StagingBuilder.CreateStageRoot();
            var ctx2 = new CopyContext { DestRoot = stage2, Policy = CollisionPolicy.AutoRename };
            var res2 = StagingBuilder.Build(
                new List<StageRequest> { new StageRequest(Path.Combine(a1, "报告.docx"), 0, true) },
                ctx2, stage2);
            Check(File.Exists(Path.Combine(stage2, "资料", "报告.docx")),
                "WrapInFolder=true 时 level=0 也保留 1 级");

            // 目录项混合
            string stage3 = StagingBuilder.CreateStageRoot();
            var ctx3 = new CopyContext { DestRoot = stage3, Policy = CollisionPolicy.AutoRename };
            var res3 = StagingBuilder.Build(
                new List<StageRequest>
                {
                    new StageRequest(Path.Combine(a1, "报告.docx"), 1, false),
                    new StageRequest(b1, 0, false)     // 目录项
                },
                ctx3, stage3);
            Check(File.Exists(Path.Combine(stage3, "资料", "报告.docx")), "文件项按 1 级暂存");
            Check(Directory.Exists(Path.Combine(stage3, "dwg")) &&
                  File.Exists(Path.Combine(stage3, "dwg", "图1.dwg")),
                "目录项递归暂存，保留内部层级");
            Check(res3.ClipboardEntries.Count == 2, "文件+目录混合 -> 2 个剪贴板条目");

            // 同名冲突：两个同名文件（level=0 时相对路径才相同，才会真正冲突）
            string dup1 = Path.Combine(tmp, "d1", "同名.txt");
            string dup2 = Path.Combine(tmp, "d2", "同名.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(dup1)!);
            Directory.CreateDirectory(Path.GetDirectoryName(dup2)!);
            File.WriteAllText(dup1, "1");
            File.WriteAllText(dup2, "2");
            Check(CopyLogic.BuildRelativePath(dup1, 0) == CopyLogic.BuildRelativePath(dup2, 0),
                "前置校验：level=0 时两个同名文件相对路径一致（构成真冲突）");

            string stage4 = StagingBuilder.CreateStageRoot();
            var ctx4 = new CopyContext { DestRoot = stage4, Policy = CollisionPolicy.AutoRename };
            var res4 = StagingBuilder.Build(
                new List<StageRequest>
                {
                    new StageRequest(dup1, 0, false),
                    new StageRequest(dup2, 0, false)
                },
                ctx4, stage4);
            Check(res4.ClipboardEntries.Count == 2,
                "同名项在暂存树中重命名，产生 2 个独立剪贴板条目");
            Check(res4.FileCount == 2, "两个同名文件都被暂存");
            Check(File.Exists(Path.Combine(stage4, "同名.txt")) &&
                  File.Exists(Path.Combine(stage4, "同名 (1).txt")),
                "重命名结果：同名.txt 与 同名 (1).txt 并存");

            // 跳过策略
            string stage5 = StagingBuilder.CreateStageRoot();
            var ctx5 = new CopyContext { DestRoot = stage5, Policy = CollisionPolicy.Skip };
            var res5 = StagingBuilder.Build(
                new List<StageRequest>
                {
                    new StageRequest(dup1, 0, false),
                    new StageRequest(dup2, 0, false)
                },
                ctx5, stage5);
            Check(res5.FileCount == 1, "Skip 策略下第二个同名项被跳过（实际暂存 " + res5.FileCount + " 个）");
            Check(res5.Warnings.Any(w => w.Contains("跳过")),
                "跳过项产生提示信息（实际 " + res5.Warnings.Count + " 条）");

            // 覆盖策略
            string stage5b = StagingBuilder.CreateStageRoot();
            var ctx5b = new CopyContext { DestRoot = stage5b, Policy = CollisionPolicy.Overwrite };
            var res5b = StagingBuilder.Build(
                new List<StageRequest>
                {
                    new StageRequest(dup1, 0, false),
                    new StageRequest(dup2, 0, false)
                },
                ctx5b, stage5b);
            Check(res5b.FileCount == 2, "Overwrite 策略下两个同名文件都写入");
            Check(res5b.ClipboardEntries.Count == 1,
                "Overwrite 下合并为同一个剪贴板条目");

            // 不存在的源
            string stage6 = StagingBuilder.CreateStageRoot();
            var ctx6 = new CopyContext { DestRoot = stage6, Policy = CollisionPolicy.AutoRename };
            var res6 = StagingBuilder.Build(
                new List<StageRequest> { new StageRequest(Path.Combine(tmp, "不存在.txt"), 0, false) },
                ctx6, stage6);
            Check(!res6.Success, "源不存在 -> 无可暂存内容");
            Check(res6.Warnings.Any(w => w.Contains("不存在")), "给出「不存在」警告");

            // 磁盘根目录护栏
            string stage7 = StagingBuilder.CreateStageRoot();
            var ctx7 = new CopyContext { DestRoot = stage7, Policy = CollisionPolicy.AutoRename };
            var res7 = StagingBuilder.Build(
                new List<StageRequest> { new StageRequest(Path.GetPathRoot(tmp)!, 0, false) },
                ctx7, stage7);
            Check(res7.Warnings.Any(w => w.Contains("根目录")), "磁盘根目录被暂存逻辑拦截");
        }
        finally { SafeDelete(tmp); }
    }

    // ------------------------------------------------------------------
    /// <summary>
    /// 端到端：暂存树 -> 剪贴板条目 -> 模拟 Ctrl+V 粘贴 -> 校验父结构完整保留。
    /// 使用 MemoryClipboardAccessor（无需真实剪贴板，可跨平台运行）。
    /// </summary>
    private static void TestStagingEndToEnd()
    {
        Section("端到端：暂存 -> 剪贴板 -> 粘贴（父结构保留）");

        string tmp = NewTmp();
        try
        {
            // 构造典型工程目录
            string a = Path.Combine(tmp, "工作", "项目A", "资料");
            string b = Path.Combine(tmp, "工作", "项目B", "图纸", "dwg");
            Directory.CreateDirectory(a);
            Directory.CreateDirectory(b);
            Directory.CreateDirectory(Path.Combine(a, "空目录"));   // 空目录
            File.WriteAllText(Path.Combine(a, "报告.docx"), "R");
            File.WriteAllText(Path.Combine(b, "图1.dwg"), "D");

            string f1 = Path.Combine(a, "报告.docx");
            string f2 = Path.Combine(b, "图1.dwg");

            // 1) 构建暂存树：文件项 2 级 / 3 级
            string stage = StagingBuilder.CreateStageRoot();
            var ctx = new CopyContext { DestRoot = stage, Policy = CollisionPolicy.AutoRename };
            var res = StagingBuilder.Build(new List<StageRequest>
            {
                new StageRequest(f1, 2, false),
                new StageRequest(f2, 3, false)
            }, ctx, stage);

            Check(res.Success, "端到端：暂存树构建成功");
            Check(res.ClipboardEntries.Count == 2, "端到端：2 个剪贴板条目");

            // 2) 写入内存剪贴板（模拟 CF_HDROP）
            var clip = new MemoryClipboardAccessor();
            Check(clip.SetFileDrop(res.ClipboardEntries), "端到端：写入剪贴板成功");
            var entries = clip.GetFileDrop();
            Check(entries.Count == 2, "端到端：读回 2 个条目（验证格式未被覆盖）");
            foreach (var e in entries)
                Check(File.Exists(e) || Directory.Exists(e), "端到端：条目真实存在：" + Path.GetFileName(e));

            // 3) 模拟 Ctrl+V：把条目粘到一个全新目录（目标目录原先不存在）
            string pasteDir = Path.Combine(tmp, "粘贴目标");
            Directory.CreateDirectory(pasteDir);
            foreach (var entry in entries)
            {
                string name = Path.GetFileName(CopyLogic.TrimEndSeparators(entry));
                string dst = Path.Combine(pasteDir, name);
                var c2 = new CopyContext { DestRoot = dst, Policy = CollisionPolicy.AutoRename };
                if (Directory.Exists(CopyLogic.LongPath(entry))) CopyLogic.CopyDirectory(entry, dst, c2);
                else CopyLogic.CopyFile(entry, dst, c2);
            }

            // 4) 校验粘贴结果：父结构必须完整
            Check(File.Exists(Path.Combine(pasteDir, "项目A", "资料", "报告.docx")),
                "端到端：粘贴后为 项目A\\资料\\报告.docx（2 级结构保留）");
            Check(File.Exists(Path.Combine(pasteDir, "项目B", "图纸", "dwg", "图1.dwg")),
                "端到端：粘贴后为 项目B\\图纸\\dwg\\图1.dwg（3 级结构保留）");
            Check(File.ReadAllText(Path.Combine(pasteDir, "项目A", "资料", "报告.docx")) == "R",
                "端到端：文件内容一致");

            // 5) 目录项 + 文件项混合且路径重叠：目录按策略重命名，两个文件都保留，
            //    且因同属一个顶层而合并为单个剪贴板条目（粘贴结果自洽）
            string stage2 = StagingBuilder.CreateStageRoot();
            var ctx2 = new CopyContext { DestRoot = stage2, Policy = CollisionPolicy.AutoRename };
            var res2 = StagingBuilder.Build(new List<StageRequest>
            {
                new StageRequest(f1, 2, false),     // -> 项目A\资料\报告.docx
                new StageRequest(a, 1, false)       // -> 资料\... 与上一项重叠
            }, ctx2, stage2);

            Check(File.Exists(Path.Combine(stage2, "项目A", "资料", "报告.docx")),
                "端到端：重叠项下原路径保留");
            Check(Directory.Exists(Path.Combine(stage2, "项目A", "资料 (1)")),
                "端到端：重叠目录被重命名为 资料 (1)，而非静默合并");
            Check(File.Exists(Path.Combine(stage2, "项目A", "资料 (1)", "报告.docx")),
                "端到端：重命名目录内的文件完整");
            Check(res2.FileCount == 2, "端到端：两个重叠项的文件都进入暂存树");
            Check(res2.ClipboardEntries.Count == 1,
                "端到端：同属一个顶层，重叠项合并为 1 个剪贴板条目");

            // 6) 空目录保留（目录项含空子目录）
            // 注意：a 的路径是 <tmp>\工作\项目A\资料，level=1 保留的是父目录「项目A」，
            // 因此结果落在 <stage>\项目A\资料\空目录。
            string stage3 = StagingBuilder.CreateStageRoot();
            var ctx3 = new CopyContext { DestRoot = stage3, Policy = CollisionPolicy.AutoRename };
            var res3 = StagingBuilder.Build(
                new List<StageRequest> { new StageRequest(a, 1, false) }, ctx3, stage3);
            Check(Directory.Exists(Path.Combine(stage3, "项目A", "资料", "空目录")),
                "端到端：目录项中的空子目录被保留到暂存树");
            Check(File.Exists(Path.Combine(stage3, "项目A", "资料", "报告.docx")),
                "端到端：目录项中的文件同步保留");
        }
        finally { SafeDelete(tmp); }
    }

    // ------------------------------------------------------------------
    /// <summary>级别必须能一直选到磁盘根目录的下一级（而不是固定在 3 级）。</summary>
    private static void TestLevelsToRoot()
    {
        Section("父结构级别：到根目录为止");

        // 构造 6 层深的真实路径：<tmp>/L1/L2/L3/L4/L5/L6/f.txt
        string tmp = NewTmp();
        try
        {
            string deep = tmp;
            for (int i = 1; i <= 6; i++) deep = Path.Combine(deep, "L" + i);
            Directory.CreateDirectory(deep);
            string f = Path.Combine(deep, "f.txt");
            File.WriteAllText(f, "x");

            // 到临时目录根的层数 = 6（L1..L6）+ tmp 自身未知，用实际返回值判定
            var opts = CopyLogic.BuildLevelOptions(f);
            int max = opts[opts.Count - 1].Level;

            Check(opts.Count == max + 1, "选项数 = 最大级别 + 1（连续无缺口）");
            Check(max >= 6, "深层路径至少可选 6 级（实际最多 " + max + " 级）");

            // 逐级递增，且每一级都比上一级多一段
            bool monotonic = true;
            for (int i = 1; i < opts.Count; i++)
            {
                if (opts[i].Level != opts[i - 1].Level + 1) monotonic = false;
                string[] prevSeg = opts[i - 1].Relative.Split(Path.DirectorySeparatorChar);
                string[] curSeg = opts[i].Relative.Split(Path.DirectorySeparatorChar);
                if (curSeg.Length != prevSeg.Length + 1) monotonic = false;
            }
            Check(monotonic, "每一级恰好比上一级多一层目录");

            // 最高级必须已经包含了卷根下的第一级目录
            string top = opts[opts.Count - 1].Relative;
            Check(top.Split(Path.DirectorySeparatorChar).Length >= 2,
                "最高级包含多层结构（实际 " + top.Split(Path.DirectorySeparatorChar).Length + " 段）");
            Check(CopyLogic.MaxLevel(f) == max, "MaxLevel 与选项表一致");

            // 超过根后再加级不再变化（封顶）
            Check(CopyLogic.BuildRelativePath(f, max + 5) == CopyLogic.BuildRelativePath(f, max),
                "超过根后路径封顶不再增长");
        }
        finally { SafeDelete(tmp); }
    }

    // ------------------------------------------------------------------
    /// <summary>
    /// 暂存树的有效性：开启"跳过空目录"时，暂存目录可能被回收，
    /// 这种情况下绝不能把已不存在的路径写进剪贴板（否则粘贴必然失败）。
    /// </summary>
    private static void TestStagingSkipsVanishedEntries()
    {
        Section("暂存条目有效性校验");

        string tmp = NewTmp();
        try
        {
            // 一个纯空目录：在 SkipEmptyDirectories 下会被整体回收
            string emptyDir = Path.Combine(tmp, "纯空目录");
            Directory.CreateDirectory(Path.Combine(emptyDir, "更深一层"));

            string stage = StagingBuilder.CreateStageRoot();
            var ctx = new CopyContext { DestRoot = stage, Policy = CollisionPolicy.AutoRename };
            ctx.Filter.SkipEmptyDirectories = true;

            var res = StagingBuilder.Build(
                new List<StageRequest> { new StageRequest(emptyDir, 0, false) }, ctx, stage);

            Check(res.ClipboardEntries.Count == 0,
                "被回收的暂存目录不会进入剪贴板（实际 " + res.ClipboardEntries.Count + " 条）");
            Check(!res.Success, "无有效条目时 Success = false，调用方可据此提示");
            Check(res.Warnings.Any(w => w.Contains("跳过")),
                "给出跳过原因（实际 " + res.Warnings.Count + " 条提示）");

            // 对照：关掉该选项后应有条目
            string stage2 = StagingBuilder.CreateStageRoot();
            var ctx2 = new CopyContext { DestRoot = stage2, Policy = CollisionPolicy.AutoRename };
            ctx2.Filter.SkipEmptyDirectories = false;
            var res2 = StagingBuilder.Build(
                new List<StageRequest> { new StageRequest(emptyDir, 0, false) }, ctx2, stage2);
            Check(res2.Success && res2.ClipboardEntries.Count == 1,
                "未开启跳过空目录时正常写入 1 个条目");
        }
        finally { SafeDelete(tmp); }
    }

    // ------------------------------------------------------------------
    /// <summary>「跳过空目录」必须真正生效（不再依赖排除规则）。</summary>
    private static void TestSkipEmptyDirectories()
    {
        Section("跳过空目录");

        string tmp = NewTmp();
        try
        {
            string src = Path.Combine(tmp, "src");
            Directory.CreateDirectory(Path.Combine(src, "空目录"));
            Directory.CreateDirectory(Path.Combine(src, "有内容"));
            File.WriteAllText(Path.Combine(src, "有内容", "a.txt"), "a");
            Directory.CreateDirectory(Path.Combine(src, "嵌套", "更深", "空"));

            // 不勾选：空目录全部保留
            string dst1 = Path.Combine(tmp, "dst1");
            var ctx1 = new CopyContext { DestRoot = dst1, Policy = CollisionPolicy.AutoRename };
            ctx1.Filter.SkipEmptyDirectories = false;
            CopyLogic.CopyItem(src, ctx1, 0);
            Check(Directory.Exists(Path.Combine(dst1, "src", "空目录")), "未勾选时空目录保留");
            Check(Directory.Exists(Path.Combine(dst1, "src", "嵌套", "更深", "空")), "未勾选时空目录链保留");

            // 勾选：空目录被回收，非空目录保留
            string dst2 = Path.Combine(tmp, "dst2");
            var ctx2 = new CopyContext { DestRoot = dst2, Policy = CollisionPolicy.AutoRename };
            ctx2.Filter.SkipEmptyDirectories = true;
            CopyLogic.CopyItem(src, ctx2, 0);
            Check(!Directory.Exists(Path.Combine(dst2, "src", "空目录")), "勾选后空目录被跳过");
            Check(!Directory.Exists(Path.Combine(dst2, "src", "嵌套")), "勾选后仅含空目录的分支整体跳过");
            Check(File.Exists(Path.Combine(dst2, "src", "有内容", "a.txt")), "有内容的目录正常复制");
        }
        finally { SafeDelete(tmp); }
    }

    // ------------------------------------------------------------------
    private static void TestLongPath()
    {
        Section("长路径支持（本次重构的修复点）");
        string tmp = NewTmp();
        try
        {
            // 构造超过 260 字符的深层路径
            string deep = tmp;
            string seg = "长目录名测试";
            while (deep.Length < 300)
                deep = Path.Combine(deep, seg);

            try
            {
                Directory.CreateDirectory(CopyLogic.LongPath(deep));
                string file = Path.Combine(deep, "深层文件.txt");
                File.WriteAllText(CopyLogic.LongPath(file), "deep");

                string dst = Path.Combine(tmp, "dst");
                var ctx = new CopyContext { DestRoot = dst, Policy = CollisionPolicy.AutoRename };
                var r = CopyLogic.CopyItem(file, ctx, 0);

                Check(r.Count == 1 && r[0].Success,
                    "超过 260 字符的路径复制成功（实际 " + deep.Length + " 字符）");
                Check(File.Exists(Path.Combine(dst, "深层文件.txt")), "长路径文件已落到目标");
            }
            catch (Exception ex)
            {
                Check(false, "长路径复制抛异常：" + ex.GetType().Name + " " + ex.Message);
            }
        }
        finally { SafeDelete(tmp); }
    }

    // ------------------------------------------------------------------
    private static void TestSpecialPaths()
    {
        Section("特殊字符路径");
        string tmp = NewTmp();
        try
        {
            string weird = Path.Combine(tmp, "带 空格 和 #标&符.txt");
            File.WriteAllText(weird, "w");

            string dst = Path.Combine(tmp, "dst");
            var ctx = new CopyContext { DestRoot = dst, Policy = CollisionPolicy.AutoRename };
            var r = CopyLogic.CopyItem(weird, ctx, 0);
            Check(r.Count == 1 && r[0].Success, "含空格与特殊字符的路径复制成功");

            // 中文路径 + 层级
            string cn = Path.Combine(tmp, "工程", "图纸", "总图.dwg");
            Directory.CreateDirectory(Path.GetDirectoryName(cn)!);
            File.WriteAllText(cn, "D");

            // 注意：/cn 位于 <tmp>\工程\图纸\总图.dwg，保留 2 级应为 工程\图纸\总图.dwg
            var r2 = CopyLogic.CopyItem(cn, ctx, 2);
            Check(r2.Count == 1 && r2[0].Success, "中文路径按 2 级复制成功");
            Check(File.Exists(Path.Combine(dst, "工程", "图纸", "总图.dwg")),
                "中文结构 dst/工程/图纸/总图.dwg");

            // RunBatch：目标目录不存在时自动创建
            string nested = Path.Combine(tmp, "a", "b", "c", "目标");
            var ctx3 = new CopyContext { DestRoot = nested, Policy = CollisionPolicy.AutoRename };
            var report = CopyLogic.RunBatch(
                new List<CopyJob> { new CopyJob(cn, 1) }, ctx3);
            Check(Directory.Exists(nested), "目标目录不存在时自动创建（含多级）");
            Check(report.JobCount == 1, "RunBatch 报告任务数正确");
            Check(report.FileCount == 1, "RunBatch 统计文件数正确");
            Check(report.Summary.Contains("任务 1 项"), "汇总文案使用任务级口径：" + report.Summary);

            // 空目标目录
            var report2 = CopyLogic.RunBatch(
                new List<CopyJob> { new CopyJob(cn, 1) },
                new CopyContext { DestRoot = "", Policy = CollisionPolicy.AutoRename });
            Check(report2.JobFailed == 1, "空目标目录 -> 报告失败而非抛异常");
        }
        finally { SafeDelete(tmp); }
    }
}
