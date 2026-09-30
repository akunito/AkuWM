using System.Diagnostics;
using AkuWM.Core.Config;

namespace AkuWM.Core.Nodes;

public sealed class NodeException(string message) : Exception(message);

public readonly record struct ShellRun(int Code, string Out, string Err);

/// <summary>
/// A command line on a node, over ssh or locally. The command itself is a
/// POSIX shell line (the nodes are NixOS); how it gets there is the
/// launcher's business: from WSL on this desk, because the keys and the
/// agent live there (Windows' OpenSSH has neither, measured 2026-09-30).
/// </summary>
public static class NodeShell
{
    public static readonly string[] SshOptions = ["-o", "BatchMode=yes", "-o", "ConnectTimeout=6", "-o", "ServerAliveInterval=15"];

    /// <summary>What actually runs things. Replaced in tests.</summary>
    public static Func<NodeConfig, string, int, ShellRun> Launcher { get; set; } = Launch;

    public static bool IsLocal(NodeConfig node) => string.IsNullOrWhiteSpace(node.Ssh);

    /// <summary><c>["-p", port, "user@host"]</c>.</summary>
    public static string[] SshTarget(NodeConfig node)
    {
        string spec = (node.Ssh ?? string.Empty).Trim();
        int at = spec.IndexOf('@');
        int colon = at >= 0 ? spec.IndexOf(':', at) : -1;
        return colon >= 0 ? ["-p", spec[(colon + 1)..], spec[..colon]] : [spec];
    }

    /// <summary>The argv for the command: ssh for a remote node, a local shell otherwise.</summary>
    public static string[] Argv(NodeConfig node, string remote) =>
        IsLocal(node) ? ["sh", "-c", remote] : ["ssh", "-A", .. SshOptions, .. SshTarget(node), remote];

    /// <summary>Runs, and turns a failure into a <see cref="NodeException"/> when <paramref name="check"/>.</summary>
    public static ShellRun Run(NodeConfig node, string remote, int timeoutSeconds = 60, bool check = true)
    {
        ShellRun run = Launcher(node, remote, timeoutSeconds);
        if (check && run.Code != 0)
        {
            string err = (run.Err.Length > 0 ? run.Err : run.Out).Trim();
            if (run.Code == 124)
            {
                throw new NodeException($"{node.Id}: timeout after {timeoutSeconds}s running: {Cut(remote, 80)}");
            }

            if (err.Contains("Connection refused") || err.Contains("No route to host") || err.Contains("Could not resolve") || err.Contains("timed out"))
            {
                string last = err.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "ssh failed";
                throw new NodeException($"{node.Id}: unreachable ({last})");
            }

            throw new NodeException($"{node.Id}: {(err.Length > 0 ? Tail(err, 400) : $"exit {run.Code}")}");
        }

        return run;
    }

    public static (bool Ok, string Detail) Reachable(NodeConfig node)
    {
        try
        {
            string[] lines = Run(node, "echo ok && uname -n && uptime -p 2>/dev/null || true", 15).Out.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return (true, lines.Length > 1 ? string.Join(" · ", lines.Skip(1).Select(l => l.Trim())) : "ok");
        }
        catch (NodeException ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>On Windows through <c>wsl.exe</c> (the keys are there); elsewhere the argv as it is.</summary>
    private static ShellRun Launch(NodeConfig node, string remote, int timeoutSeconds)
    {
        string[] argv = Argv(node, remote);
        var info = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        if (OperatingSystem.IsWindows())
        {
            info.FileName = "wsl.exe";
            info.ArgumentList.Add("--");
            foreach (string a in argv)
            {
                info.ArgumentList.Add(a);
            }
        }
        else
        {
            info.FileName = argv[0];
            foreach (string a in argv.Skip(1))
            {
                info.ArgumentList.Add(a);
            }
        }

        try
        {
            using Process process = Process.Start(info)!;
            var output = new System.Text.StringBuilder();
            var error = new System.Text.StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) { output.Append(e.Data).Append('\n'); } };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { error.Append(e.Data).Append('\n'); } };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (!process.WaitForExit(timeoutSeconds * 1000))
            {
                process.Kill(entireProcessTree: true);
                return new ShellRun(124, output.ToString(), error.ToString());
            }

            process.WaitForExit();
            return new ShellRun(process.ExitCode, output.ToString(), error.ToString());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new ShellRun(127, string.Empty, ex.Message);
        }
    }

    /// <summary>POSIX single-quote quoting, as Python's shlex.quote.</summary>
    public static string Quote(string s)
    {
        if (s.Length > 0 && s.All(c => char.IsAsciiLetterOrDigit(c) || "@%+=:,./-_".Contains(c)))
        {
            return s;
        }

        return "'" + s.Replace("'", "'\"'\"'") + "'";
    }

    private static string Cut(string s, int n) => s.Length <= n ? s : s[..n];

    private static string Tail(string s, int n) => s.Length <= n ? s : s[^n..];
}
