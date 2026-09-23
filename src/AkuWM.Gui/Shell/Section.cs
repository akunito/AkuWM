using AkuWM.Gui.Services;
using Avalonia.Controls;

namespace AkuWM.Gui.Shell;

/// <summary>One entry of the sidebar. The view is built once, on first show, and refreshed on every show.</summary>
public abstract class Section
{
    private Control? _view;

    protected Section(AppServices services) => Services = services;

    protected AppServices Services { get; }

    public abstract string Key { get; }

    public abstract string Title { get; }

    public abstract string Blurb { get; }

    public abstract string Glyph { get; }

    public Control View => _view ??= Build();

    protected abstract Control Build();

    /// <summary>Reads everything again. Called on show and by the header's button.</summary>
    public virtual void Refresh()
    {
    }

    public virtual void Shown() => Refresh();

    public virtual void Hidden()
    {
    }

    public virtual void Select(string id)
    {
    }

    protected void Toast(string text, bool error = false) => Services.Toast(text, error);
}
