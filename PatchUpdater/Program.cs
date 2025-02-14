﻿using System.Diagnostics;
using System.Reflection;

namespace Patch_Updater;

class Program
{
    private const string RepoUrl = "https://github.com/GTNH-UCN/ClientPatch/releases/download/";
    private static readonly string TempDownloadPath = Path.Combine(Path.GetTempPath(), $"patch_{Guid.NewGuid()}.7z");
    private static readonly string AssetsPath = Path.Combine(AppContext.BaseDirectory, "Assets");
    private static string? _gameDir;

    static async Task Main(string[] args)
    {
        Console.WriteLine("=== GTNH PatchUpdater ===");

        // 注册进程退出事件
        AppDomain.CurrentDomain.ProcessExit += (s, e) => CleanupTemporaryFiles();

        try
        {
            // 需要时释放exe
            await ExtractExecutablesAsync();

            // 1. 检测环境变量
            CheckAndSetGameDir();

            // 2. 获取最新可用补丁
            string patchUrl = await GetLatestPatchUrl();
            if (string.IsNullOrEmpty(patchUrl))
            {
                Console.WriteLine("未找到可用的补丁文件。");
                return;
            }

            // 3. 下载补丁（使用 aria2）
            bool downloadSuccess = DownloadPatch(patchUrl, TempDownloadPath);
            if (!downloadSuccess)
            {
                Console.WriteLine("补丁下载失败，请尝试挂上梯子后再次运行。");
                return;
            }

            // 4. 解压覆盖到 GTNHDir
            ExtractArchive(TempDownloadPath, _gameDir);

            Console.WriteLine("更新完成！按任意键退出...");
            Console.ReadKey();
        }
        finally
        {
            // 5. 进程退出前删除临时文件
            CleanupTemporaryFiles();
        }
    }

    /// <summary>
    /// 验证游戏目录路径是否有效（以.minecraft结尾）
    /// </summary>
    private static bool IsValidGameDir(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        path = path.Trim();
        return path.EndsWith(".minecraft", StringComparison.OrdinalIgnoreCase) &&
               Directory.Exists(path);
    }

    /// <summary>
    /// 获取用户输入的游戏目录并验证
    /// </summary>
    private static string GetValidGameDir(string prompt)
    {
        while (true)
        {
            Console.Write(prompt);
            string? input = Console.ReadLine()?.Trim();

            if (IsValidGameDir(input))
                return input!;

            Console.WriteLine("错误：游戏目录路径必须以.minecraft结尾且必须存在。请重新输入。");
        }
    }

    /// <summary>
    /// 检查 GTNHDir 环境变量，如果不存在或无效，则让用户输入并保存。
    /// </summary>
    private static void CheckAndSetGameDir()
    {
        _gameDir = Environment.GetEnvironmentVariable("GTNHDir");

        // 检查现有环境变量是否有效
        if (!IsValidGameDir(_gameDir))
        {
            string prompt = "未找到游戏目录，请输入游戏所在的版本目录（以.minecraft结尾）: ";
            _gameDir = GetValidGameDir(prompt);
            Environment.SetEnvironmentVariable("GTNHDir", _gameDir, EnvironmentVariableTarget.User);
            Console.WriteLine($"已保存 GTNHDir = {_gameDir}");
        }
        else
        {
            Console.WriteLine($"当前版本目录: {_gameDir}");
            Console.Write("是否修改版本目录？(y/N) (直接回车可跳过): ");
            var input = Console.ReadLine()?.Trim().ToUpper() ?? string.Empty;

            if (input == "Y")
            {
                string prompt = "请输入新的版本目录（以.minecraft结尾）: ";
                _gameDir = GetValidGameDir(prompt);
                Environment.SetEnvironmentVariable("GTNHDir", _gameDir, EnvironmentVariableTarget.User);
                Console.WriteLine($"已更新 GTNHDir = {_gameDir}");
            }
        }
    }

    /// <summary>
    /// 获取最新可用的补丁 URL，最多尝试 3 天。
    /// </summary>
    private static async Task<string> GetLatestPatchUrl()
    {
        using HttpClient client = new HttpClient(new HttpClientHandler { UseProxy = true })
        {
            DefaultRequestHeaders = { { "User-Agent", "Mozilla/5.0" } }
        };

        DateTime today = DateTime.UtcNow;
        for (int i = 0; i < 3; i++)
        {
            string dateStr = today.AddDays(-i).ToString("yyyy-MM-dd");
            string patchUrl = $"{RepoUrl}patch-{dateStr}/patch-{dateStr}.7z";

            Console.WriteLine($"尝试获取补丁: {patchUrl}");
            HttpResponseMessage response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, patchUrl));
            if (response.IsSuccessStatusCode)
            {
                Console.WriteLine($"找到补丁文件: {patchUrl}");
                return patchUrl;
            }
        }

        return null;
    }

    /// <summary>
    /// 使用 aria2c 下载文件，并正确解析并显示进度条
    /// </summary>
    private static bool DownloadPatch(string patchUrl, string downloadPath)
    {
        string aria2Path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets/aria2c.exe");
        if (!File.Exists(aria2Path))
        {
            Console.WriteLine("错误: 未找到 aria2c.exe，请确保程序目录中包含 aria2c.exe");
            return false;
        }

        string tempFolder = Path.GetDirectoryName(downloadPath);
        string fileName = Path.GetFileName(downloadPath);

        // 自动获取 Windows 系统代理
        string proxy = GetSystemProxy();

        Console.WriteLine($"正在下载补丁: {patchUrl}");
        if (!string.IsNullOrEmpty(proxy))
        {
            Console.WriteLine($"使用代理: {proxy}");
        }

        // `--summary-interval=1` 每秒刷新进度
        string arguments = $"-x 16 -s 16 --check-certificate=false --enable-color=false --summary-interval=1 " +
                           $"--dir \"{tempFolder}\" --out \"{fileName}\" \"{patchUrl}\"";

        if (!string.IsNullOrEmpty(proxy))
        {
            arguments = $"--all-proxy=\"{proxy}\" " + arguments;
        }

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = aria2Path,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();

        // 监听 `aria2c` 的标准输出和错误输出
        Task stdoutTask = Task.Run(() =>
        {
            while (!process.StandardOutput.EndOfStream)
            {
                string line = process.StandardOutput.ReadLine();
                if (!string.IsNullOrEmpty(line))
                {
                    ParseAria2Progress(line);
                }
            }
        });

        Task stderrTask = Task.Run(() =>
        {
            while (!process.StandardError.EndOfStream)
            {
                string line = process.StandardError.ReadLine();
                if (!string.IsNullOrEmpty(line))
                {
                    ParseAria2Progress(line);
                }
            }
        });

        Task.WaitAll(stdoutTask, stderrTask);
        process.WaitForExit();
        Console.WriteLine(); // 换行，避免进度条干扰

        if (File.Exists(downloadPath))
        {
            Console.WriteLine("文件已成功下载。");
            return true;
        }
        else
        {
            Console.WriteLine("下载失败，请尝试挂上梯子后再次运行。");
            return false;
        }
    }

    /// <summary>
    /// 解析 aria2c 输出的下载进度信息
    /// </summary>
    private static void ParseAria2Progress(string line)
    {
        // 示例日志:
        // *** Download Progress Summary as of Sat, 27 Jan 2024 20:18:24 GMT ***
        // - [#70a74d 32MiB/120MiB(26%) CN:16 DL:1.2MiB ETA:1m]

        var match = System.Text.RegularExpressions.Regex.Match(line,
            @"\[#\w+\s+([\d.]+[KMGT]?iB)/([\d.]+[KMGT]?iB)\((\d+)%\).*?DL:([\d.]+[KMGT]?iB)");

        if (match.Success)
        {
            string downloaded = match.Groups[1].Value;
            string total = match.Groups[2].Value;
            string percent = match.Groups[3].Value;
            string speed = match.Groups[4].Value;

            Console.Write($"\r下载进度: [{downloaded}/{total} ({percent}%)] 速度: {speed}/s      ");
        }
    }


    /// <summary>
    /// 使用 7zr.exe 解压补丁文件，并优化输出
    /// </summary>
    private static void ExtractArchive(string archivePath, string? targetPath)
    {
        string sevenZipPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets/7zr.exe");
        if (!File.Exists(sevenZipPath))
        {
            Console.WriteLine("错误: 7zr.exe 不存在，请确保程序目录中包含 7zr.exe");
            return;
        }

        Console.WriteLine($"正在解压 {archivePath} 到 {targetPath}...");

        // `-bb0` 只输出错误信息
        // `-bsp1` 只显示进度（不会显示每个文件的详细信息）
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = sevenZipPath,
                Arguments = $"x \"{archivePath}\" -o\"{targetPath}\" -y -bsp1 -bb0",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();

        // 监听 7z 的标准输出并解析进度
        while (!process.StandardOutput.EndOfStream)
        {
            string line = process.StandardOutput.ReadLine();
            if (!string.IsNullOrEmpty(line))
            {
                Parse7zProgress(line);
            }
        }

        // 监听错误输出
        while (!process.StandardError.EndOfStream)
        {
            string errorLine = process.StandardError.ReadLine();
            if (!string.IsNullOrEmpty(errorLine))
            {
                Console.WriteLine($"[错误] {errorLine}");
            }
        }

        process.WaitForExit();
        Console.WriteLine(); // 确保换行
        Console.WriteLine("解压完成！");
    }

    /// <summary>
    /// 解析 7-Zip 进度信息并优化显示
    /// </summary>
    private static void Parse7zProgress(string line)
    {
        // `7zr.exe -bsp1` 输出格式类似：
        // " 21% 30% 50% 75% 100%"
        var match = System.Text.RegularExpressions.Regex.Match(line, @"(\d+)%");
        if (match.Success)
        {
            string percent = match.Groups[1].Value;
            Console.Write($"\r解压进度: {percent}%     "); // 覆盖行，不刷屏
        }
    }

    /// <summary>
    /// 获取 Windows 系统的代理地址
    /// </summary>
    private static string GetSystemProxy()
    {
        try
        {
            var proxy = System.Net.WebRequest.GetSystemWebProxy();
            var testUri = new Uri("https://github.com");
            var proxyUri = proxy.GetProxy(testUri);

            if (proxyUri != null && proxyUri.AbsoluteUri != testUri.AbsoluteUri)
            {
                return proxyUri.AbsoluteUri.TrimEnd('/');
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"获取系统代理失败: {ex.Message}");
        }

        return string.Empty;
    }

    /// <summary>
    /// 删除下载的临时文件，避免占用磁盘空间
    /// </summary>
    private static void CleanupTemporaryFiles()
    {
        try
        {
            if (File.Exists(TempDownloadPath))
            {
                File.Delete(TempDownloadPath);
            }
            if (Directory.Exists(AssetsPath))
            {
                File.Delete(Path.Combine(AssetsPath, "7zr.exe"));
                File.Delete(Path.Combine(AssetsPath, "aria2c.exe"));

                // 文件夹为空则删除
                if (!Directory.EnumerateFileSystemEntries(AssetsPath).Any())
                {
                    Directory.Delete(AssetsPath);
                    Console.WriteLine($"已删除临时文件夹: {AssetsPath}");
                }
            }

            Console.WriteLine("已清理临时文件:");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[警告] 无法删除临时文件 {TempDownloadPath}: {ex.Message}");
        }
    }

    private static async Task ExtractExecutablesAsync()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var exeNames = assembly.GetManifestResourceNames()
            .Where(x => x.EndsWith(".exe"));

        if (!Directory.Exists(AssetsPath))
        {
            var directoryInfo = Directory.CreateDirectory(AssetsPath);
            directoryInfo.Attributes |= FileAttributes.Hidden;
        }

        foreach (var resourceName in exeNames)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null) continue;

            // 正确提取文件名
            // 资源名称通常格式为: "PatchUpdater.Assets.7zr.exe"
            string fileName = resourceName.Split('.').TakeLast(2).First() + ".exe";
            // 或者更安全的方式：
            // string fileName = Path.GetFileName(resourceName.Replace('.', Path.DirectorySeparatorChar));

            string targetPath = Path.Combine(AssetsPath, fileName);

            // 如果文件已存在且正在使用，等待它释放
            if (File.Exists(targetPath))
            {
                try
                {
                    File.Delete(targetPath);
                }
                catch (IOException)
                {
                    continue;
                }
            }

            // 写入文件
            using var fileStream = File.Create(targetPath);
            await stream.CopyToAsync(fileStream);
        }
    }
}