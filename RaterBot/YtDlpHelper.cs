using System.Diagnostics;

namespace RaterBot;

public static class YtDlpHelper
{
    public static bool Download(Uri url, string tempFilePath)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "yt-dlp",
                Arguments = $"{url} -o {tempFilePath}",
                CreateNoWindow = true,
            }
        };

        process.Start();
        process.WaitForExit();
        return process.ExitCode == 0;
    }
}