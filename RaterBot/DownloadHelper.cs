using System.Diagnostics;

namespace RaterBot;

internal enum UrlType
{
    NotFound,
    TikTok,
    Vk,
    Reddit,
    Twitter,
    Youtube,
    EmbedableLink,
}

internal interface IMediaDownloader
{
    Task<string[]> DownloadGalleryDl(string url);
    string? DownloadYtDlp(string url, UrlType urlType);
}

internal sealed class ProcessMediaDownloader(Config config, ILogger<ProcessMediaDownloader> logger) : IMediaDownloader
{
    private static readonly string _tmp = Path.GetTempPath();
    private readonly string? _proxy = config.DownloaderProxy;

    public async Task<string[]> DownloadGalleryDl(string url)
    {
        using var process = new Process { StartInfo = CreateGalleryDlStartInfo(url, _proxy, _tmp) };

        process.Start();
        RedirectStandardErrorAsWarnings(process);
        KillIfNotExitedInAWhile(process);

        await process.WaitForExitAsync();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var results = stdout.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Where(x => x.StartsWith(_tmp)).ToArray();
        RemoveAfterDelay(results);
        return process.ExitCode == 0 ? results : [];
    }

    private void RedirectStandardErrorAsWarnings(Process process)
    {
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                logger.LogWarning("{ProcessName}: {StdErr}", process.StartInfo.FileName, e.Data);
        };
        process.BeginErrorReadLine();
    }

    private static void KillIfNotExitedInAWhile(Process process)
    {
        Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill();
            }
        });
    }

    private static void RemoveAfterDelay(IEnumerable<string> files) =>
        Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMinutes(5));
            foreach (var file in files)
                for (var retries = 3; retries >= 0; retries--)
                {
                    File.Delete(file);
                    if (File.Exists(file))
                        await Task.Delay(TimeSpan.FromSeconds(1));
                    else
                        break;
                }
        });

    public string? DownloadYtDlp(string url, UrlType urlType)
    {
        var ext = urlType == UrlType.Youtube ? "webm" : "mp4";
        var file = Path.Combine(_tmp, $"{Guid.NewGuid()}.{ext}");
        using var process = new Process { StartInfo = CreateYtDlpStartInfo(url, file, _proxy) };

        process.Start();
        RedirectStandardErrorAsWarnings(process);
        RemoveAfterDelay([file]);

        if (!process.WaitForExit(TimeSpan.FromMinutes(1)))
        {
            process.Kill();
            File.Delete(file);
            return null;
        }
        return process.ExitCode == 0 && File.Exists(file) ? file : null;
    }

    internal static ProcessStartInfo CreateGalleryDlStartInfo(string url, string? proxy, string outputDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "gallery-dl",
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        AddProxyArgument(startInfo, proxy);
        startInfo.ArgumentList.Add("--cookies");
        startInfo.ArgumentList.Add("db/cookies.txt");
        startInfo.ArgumentList.Add("-d");
        startInfo.ArgumentList.Add(outputDirectory);
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add("browser=firefox");
        startInfo.ArgumentList.Add(url);
        return startInfo;
    }

    internal static ProcessStartInfo CreateYtDlpStartInfo(string url, string outputFile, string? proxy)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "yt-dlp",
            CreateNoWindow = true,
            RedirectStandardError = true,
        };

        AddProxyArgument(startInfo, proxy);
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add(outputFile);
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("bestvideo[height<=1080][vcodec^=avc][ext=mp4]+bestaudio[ext=m4a]/best[ext=mp4]/best");
        startInfo.ArgumentList.Add(url);
        return startInfo;
    }

    private static void AddProxyArgument(ProcessStartInfo startInfo, string? proxy)
    {
        if (string.IsNullOrWhiteSpace(proxy))
            return;

        startInfo.ArgumentList.Add("--proxy");
        startInfo.ArgumentList.Add(proxy);
    }
}
