using System.Diagnostics;

namespace RaterBot;

internal enum UrlType
{
    NotFound,
    Instagram,
    Twitter,
    TikTok,
    Vk,
    Reddit,
    Youtube,
}

internal static class DownloadHelper
{
    private static readonly string _tmp = Path.GetTempPath();

    public static async Task<string[]> DownloadGalleryDl(Uri url)
    {
        var args = $"\"{url}\" --cookies db/cookies.txt -d {_tmp} -o browser=firefox";

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "gallery-dl",
                Arguments = args,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            },
        };

        process.Start();
        KillIfNotExitedInAWhile(process);

        await process.WaitForExitAsync();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var results = stdout.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Where(x => x.StartsWith(_tmp)).ToArray();
        RemoveAfterDelay(results);
        return process.ExitCode == 0 ? results : [];
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

    public static void RemoveAfterDelay(IEnumerable<string> files) =>
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

    public static string? DownloadYtDlp(Uri url, UrlType urlType)
    {
        var ext = urlType == UrlType.Youtube ? "webm" : "mp4";
        var file = Path.Combine(_tmp, $"{Guid.NewGuid()}.{ext}");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "yt-dlp",
                Arguments = $"{url} -o {file} -f \"bestvideo[height<=1080][vcodec^=avc][ext=mp4]+bestaudio[ext=m4a]/best[ext=mp4]/best\"",
                CreateNoWindow = true,
            },
        };

        process.Start();
        RemoveAfterDelay([file]);

        if (!process.WaitForExit(TimeSpan.FromMinutes(1)))
        {
            process.Kill();
            File.Delete(file);
            return null;
        }
        return process.ExitCode == 0 ? file : null;
    }
}
