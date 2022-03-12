using Serilog;
using Serilog.Core;

namespace RaterBot;

using System;
using System.Diagnostics;

public static class ShellHelper
{
    private static readonly Logger _logger = new LoggerConfiguration().WriteTo.Console().CreateLogger();

    public static int Bash(this string cmd)
    {
        var escapedArgs = cmd.Replace("\"", "\\\"");

        var process = new Process()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-c \"{escapedArgs}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };

        process.Start();
        string result = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        var exitCode = process.ExitCode;
        _logger.Information("Script output is:\n" + result);

        return exitCode;
    }
}