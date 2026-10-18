// SettingsStore.cs —— 设置持久化（原子写 + 备份 + 回退路径）
//
// 优先写在 exe 同目录（保证"绿色可移动"：exe 与设置一起搬走）；
// 若该目录不可写（如放在 Program Files 或只读介质），自动回退到
// %AppData%\CopyWithFolders\settings.json，并在界面上明示实际路径。
using System;
using System.IO;
using System.Text.Json;

namespace CopyWithFolders
{
    public sealed class AppSettings
    {
        public string DestFolder { get; set; } = "";
        public int CollisionPolicy { get; set; } = 0;      // 0=自动重命名 1=覆盖 2=询问 3=跳过

        /// <summary>level=0 时是否也强制保留一层目录，避免多个文件散落到目标根目录。</summary>
        public bool WrapInFolder { get; set; }

        public bool SkipEmptyDirectories { get; set; }
        public bool PreserveTimestamps { get; set; }

        /// <summary>新增项的默认父结构级别。</summary>
        public int DefaultLevel { get; set; } = 2;

        /// <summary>托盘气泡是否已提示过（只提示一次）。</summary>
        public bool TrayHintShown { get; set; }

        /// <summary>最近使用的目标目录（最多 5 条），托盘菜单快捷访问。</summary>
        public System.Collections.Generic.List<string> RecentFolders { get; set; } = new();

        /// <summary>窗口位置与尺寸。</summary>
        public int WindowX { get; set; } = -1;
        public int WindowY { get; set; } = -1;
        public int WindowW { get; set; } = 720;
        public int WindowH { get; set; } = 760;
        public bool LogCollapsed { get; set; }

        public void PushRecent(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return;
            string f = folder.Trim();
            RecentFolders.RemoveAll(x => string.Equals(x, f, StringComparison.OrdinalIgnoreCase));
            RecentFolders.Insert(0, f);
            while (RecentFolders.Count > 5) RecentFolders.RemoveAt(RecentFolders.Count - 1);
        }
    }

    public static class SettingsStore
    {
        private static string? _activePath;

        /// <summary>exe 同目录下的首选路径。</summary>
        public static string PreferredPath =>
            Path.Combine(AppContext.BaseDirectory, "settings.json");

        /// <summary>回退路径。</summary>
        public static string FallbackPath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CopyWithFolders", "settings.json");

        /// <summary>实际生效的设置文件路径。</summary>
        public static string ActivePath => _activePath ?? PreferredPath;

        /// <summary>是否使用了回退路径。</summary>
        public static bool UsingFallback => !string.Equals(ActivePath, PreferredPath, StringComparison.OrdinalIgnoreCase);

        private static readonly JsonSerializerOptions Opts = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static AppSettings Load(out string? error)
        {
            error = null;
            foreach (var path in new[] { PreferredPath, FallbackPath })
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
                    if (s == null) continue;
                    _activePath = path;
                    return s;
                }
                catch (Exception ex)
                {
                    error ??= "读取设置失败（" + Path.GetFileName(path) + "）：" + ex.Message;
                }
            }

            var def = new AppSettings();
            if (string.IsNullOrWhiteSpace(def.DestFolder))
            {
                def.DestFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "CopyWithFolders");
            }
            _activePath = PreferredPath;
            return def;
        }

        /// <summary>原子写入：临时文件 + File.Move 覆盖，失败时保留 .bak。</summary>
        public static void Save(AppSettings s, out string? error)
        {
            error = null;
            string primary = PreferredPath;

            if (TryWrite(primary, s)) { _activePath = primary; return; }

            // 回退
            try
            {
                string fb = FallbackPath;
                Directory.CreateDirectory(Path.GetDirectoryName(fb)!);
                if (TryWrite(fb, s)) { _activePath = fb; return; }
            }
            catch (Exception ex)
            {
                error = "保存设置失败：" + ex.Message;
                return;
            }
            error = "保存设置失败：程序目录不可写，且回退目录也写入失败。";
        }

        private static bool TryWrite(string path, AppSettings s)
        {
            try
            {
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                string json = JsonSerializer.Serialize(s, Opts);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, json);

                if (File.Exists(path))
                {
                    try { File.Copy(path, path + ".bak", true); } catch { }
                }

                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
