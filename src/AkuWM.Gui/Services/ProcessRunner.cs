using System.Diagnostics;

namespace AkuWM.Gui.Services;

public static class ProcessRunner
{
    public static string? Capture(string file, string arguments, int timeoutMs)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
            {
                return null;
            }

            string output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(timeoutMs))
            {
                process.Kill();
            }

            return output;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <summary>A command line as a shortcut or a startup entry has it: a quoted program and its arguments, %VAR% expanded.</summary>
    public static string? ShellStart(string command)
    {
        (string file, string arguments) = Split(Environment.ExpandEnvironmentVariables(command));
        try
        {
            Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = true });
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or FileNotFoundException)
        {
            return ex.Message;
        }
    }

    public static (string File, string Arguments) Split(string command)
    {
        command = command.Trim();
        if (command.Length == 0)
        {
            return (string.Empty, string.Empty);
        }

        if (command[0] == '"')
        {
            int close = command.IndexOf('"', 1);
            return close < 0
                ? (command.Trim('"'), string.Empty)
                : (command[1..close], command[(close + 1)..].Trim());
        }

        int space = command.IndexOf(' ');
        return space < 0 ? (command, string.Empty) : (command[..space], command[(space + 1)..].Trim());
    }
}
