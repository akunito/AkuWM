using AkuWM.Core.Git;
using AkuWM.Gui.Services;
using AkuWM.Gui.Shell;
using Avalonia;
using Avalonia.Controls;

namespace AkuWM.Gui.Sections;

/// <summary>The configuration under version control: status, commit the state files, push, pull, sync.</summary>
public sealed class GitSection : Section
{
    private readonly TextBlock _summary = Ui.Text(string.Empty, "h2");
    private readonly StackPanel _dirty = new() { Spacing = 2 };
    private readonly TextBox _message = new() { PlaceholderText = "commit message (state files only)", Width = 420 };
    private readonly TextBox _log = new() { IsReadOnly = true, AcceptsReturn = true, FontFamily = new Avalonia.Media.FontFamily("Cascadia Mono,Consolas,monospace"), FontSize = 12, MinHeight = 200 };

    public GitSection(AppServices services)
        : base(services)
    {
    }

    public override string Key => "git";

    public override string Title => "Git";

    public override string Blurb => "the config directory in its repository: commit, push, pull, sync";

    public override string Glyph => "⎇";

    public GitSync Git => new(Services.Config.Paths);

    protected override Control Build()
    {
        var actions = Ui.Row(8,
            Ui.Button("Commit state files", () => Commit(), "accent"),
            Ui.Button("Push", () => Do("push", () => Git.Push())),
            Ui.Button("Pull (ff-only)", () => Do("pull", () => Git.Pull())),
            Ui.Button("Sync (fetch, rebase, merge by id, push)", () => Sync()));
        return Ui.Scroll(Ui.Col(12,
            _summary,
            Ui.Field("state files with changes", Ui.Card(_dirty)),
            Ui.Row(8, _message, actions),
            Ui.Field("last output", _log),
            Ui.Dim("Only the layers and the snapshots are ever committed here. Sync rebases this machine's state commits onto upstream and merges a layer touched on both sides item by item (newest updated_at wins).")));
    }

    public override void Refresh()
    {
        _dirty.Children.Clear();
        GitStatus status;
        try
        {
            status = Git.Status();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            _summary.Text = "git: " + ex.Message;
            return;
        }

        if (!status.Repo)
        {
            _summary.Text = $"{Services.Config.Paths.ConfigDir} is not inside a git repository";
            _dirty.Children.Add(Ui.Dim("nothing to commit here"));
            return;
        }

        string ahead = status.Ahead is null ? "no upstream" : $"{status.Ahead} ahead · {status.Behind} behind {status.Upstream}";
        _summary.Text = $"{Git.Top} · {status.Branch} · {ahead} · {status.Dirty.Count} state file(s) changed";
        if (status.Dirty.Count == 0)
        {
            _dirty.Children.Add(Ui.Dim("clean"));
        }

        foreach (string line in status.Dirty)
        {
            _dirty.Children.Add(Ui.Mono(line));
        }
    }

    public string? Commit()
    {
        string message = _message.Text is { Length: > 0 } m ? m : "edited from the settings window";
        try
        {
            string? sha = Git.Commit(Git.StateFiles(), message);
            _log.Text = sha is null ? "nothing to commit" : $"committed {sha}: akuwm: {message}";
            Toast(_log.Text, sha is null);
            Refresh();
            return sha;
        }
        catch (InvalidOperationException ex)
        {
            _log.Text = ex.Message;
            Toast(ex.Message, error: true);
            return null;
        }
    }

    private void Do(string what, Func<string> action)
    {
        try
        {
            _log.Text = action();
            Toast($"{what}: done");
        }
        catch (InvalidOperationException ex)
        {
            _log.Text = ex.Message;
            Toast($"{what} failed: {ex.Message}", error: true);
        }

        Refresh();
    }

    public SyncResult Sync()
    {
        SyncResult result = Git.Sync();
        _log.Text = result.Ok
            ? $"synced: {result.Behind} behind, {result.Ahead} ahead, merged {result.Merged.Count} file(s), pushed {result.Pushed}"
            : result.Error ?? result.Skipped ?? "sync failed";
        Toast(_log.Text, !result.Ok);
        Refresh();
        return result;
    }
}
