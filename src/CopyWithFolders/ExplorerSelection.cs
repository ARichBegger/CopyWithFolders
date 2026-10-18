using System;
using System.Collections.Generic;

namespace CopyWithFolders
{
    /// <summary>
    /// 读取文件资源管理器中当前选中的文件/文件夹路径。
    /// 通过 Shell.Application COM 枚举所有资源管理器窗口（与焦点无关）。
    /// </summary>
    internal static class ExplorerSelection
    {
        public static List<string> GetSelectedFiles()
        {
            var result = new List<string>();
            try
            {
                Type? shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null) return result;

                object? shellObj = Activator.CreateInstance(shellType);
                if (shellObj == null) return result;
                dynamic shell = shellObj;

                dynamic windows = shell.Windows();
                int winCount = windows.Count;
                for (int i = 0; i < winCount; i++)
                {
                    dynamic w = windows.Item(i);
                    if (w == null) continue;
                    try
                    {
                        dynamic doc = w.Document;
                        if (doc == null) continue;          // 非文件夹视图
                        dynamic items = doc.SelectedItems();
                        int itemCount = items.Count;
                        if (itemCount == 0) continue;
                        for (int j = 0; j < itemCount; j++)
                        {
                            dynamic item = items.Item(j);
                            string? path = item?.Path as string;
                            if (!string.IsNullOrEmpty(path)) result.Add(path);
                        }
                    }
                    catch
                    {
                        // 该窗口不是文件夹视图（如设置页），跳过
                    }
                }
            }
            catch
            {
                // COM 不可用时返回空列表，由调用方提示
            }
            return result;
        }
    }
}
