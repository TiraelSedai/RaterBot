using Shouldly;

namespace RaterBot.Tests.Unit;

public class ProcessMediaDownloaderTests
{
    [Fact]
    public void CreateGalleryDlStartInfo_IncludesProxyWhenConfigured()
    {
        var startInfo = ProcessMediaDownloader.CreateGalleryDlStartInfo(
            "https://example.com/post",
            "socks5://user:pass@127.0.0.1:1080",
            "/tmp/media"
        );

        startInfo.FileName.ShouldBe("gallery-dl");
        startInfo
            .ArgumentList.ToArray()
            .ShouldBe([
                "--proxy",
                "socks5://user:pass@127.0.0.1:1080",
                "--cookies",
                "db/cookies.txt",
                "-d",
                "/tmp/media",
                "-o",
                "browser=firefox",
                "https://example.com/post",
            ]);
    }

    [Fact]
    public void CreateGalleryDlStartInfo_OmitsProxyWhenProxyIsBlank()
    {
        var startInfo = ProcessMediaDownloader.CreateGalleryDlStartInfo("https://example.com/post", "   ", "/tmp/media");

        startInfo.ArgumentList.ShouldNotContain("--proxy");
    }

    [Fact]
    public void CreateYtDlpStartInfo_IncludesProxyWhenConfigured()
    {
        var startInfo = ProcessMediaDownloader.CreateYtDlpStartInfo("https://example.com/video", "/tmp/video.mp4", "http://proxy:8080");

        startInfo.FileName.ShouldBe("yt-dlp");
        var args = startInfo.ArgumentList.ToArray();
        args[Array.IndexOf(args, "--proxy") + 1].ShouldBe("http://proxy:8080");
        args[^1].ShouldBe("https://example.com/video");
    }

    [Fact]
    public void CreateYtDlpStartInfo_OmitsProxyWhenProxyIsBlank()
    {
        var startInfo = ProcessMediaDownloader.CreateYtDlpStartInfo("https://example.com/video", "/tmp/video.mp4", string.Empty);

        startInfo.ArgumentList.ShouldNotContain("--proxy");
    }
}
