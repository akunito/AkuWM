using AkuWM.Core.Model;

namespace AkuWM.Core.Layout;

/// <summary>How a rectangle is carried from one screen to another.</summary>
public enum AcrossMode
{
    /// <summary>The pixels are the pixels: same size, same offset into the work area.</summary>
    Absolute,

    /// <summary>
    /// The screen is the unit: a window filling 99% of one screen fills 99% of
    /// the next, and sits at the same fraction across it.
    /// </summary>
    Proportional,

    /// <summary>Absolute, unless the window would not fit; then proportional.</summary>
    Hybrid,
}

/// <summary>
/// Moving a window between screens of different sizes.
/// </summary>
/// <remarks>
/// Diego's example, 2026-09-21: a window of 990x990 on a 1000x1000 screen,
/// moved to a 2000x2000 one, arrives as 1980x1980 proportionally and as
/// 990x990 absolutely. Neither is right for everybody -- absolute keeps a
/// carefully sized editor the size it was, proportional keeps a window that
/// filled the screen filling the screen -- so it is a setting, and the third
/// value is the one that only intervenes when the pixels do not fit.
///
/// All of it is arithmetic over two rectangles, which is why every mode can be
/// tested against every monitor arrangement without a screen being plugged in.
/// </remarks>
public static class AcrossMonitors
{
    public static AcrossMode Parse(string? name) => name?.ToLowerInvariant() switch
    {
        "absolute" => AcrossMode.Absolute,
        "proportional" => AcrossMode.Proportional,
        _ => AcrossMode.Hybrid,
    };

    /// <summary>The names the configuration accepts, for the validator and the GUI.</summary>
    public static readonly string[] Names = ["absolute", "proportional", "hybrid"];

    /// <summary>
    /// The whole rectangle: where it sits on the new screen as well as how big
    /// it is. For a window moved by a command, which chooses no position.
    /// </summary>
    public static Rect Map(Rect rect, Rect from, Rect to, AcrossMode mode)
    {
        if (from.Width <= 0 || from.Height <= 0)
        {
            return rect;
        }

        if (!Scales(rect, from, to, mode))
        {
            return rect with { X = to.X + (rect.X - from.X), Y = to.Y + (rect.Y - from.Y) };
        }

        double wide = (double)to.Width / from.Width;
        double tall = (double)to.Height / from.Height;

        return new Rect(
            to.X + (int)Math.Round((rect.X - from.X) * wide),
            to.Y + (int)Math.Round((rect.Y - from.Y) * tall),
            Math.Max(1, (int)Math.Round(rect.Width * wide)),
            Math.Max(1, (int)Math.Round(rect.Height * tall)));
    }

    /// <summary>
    /// The size only, the rectangle staying where it is.
    /// </summary>
    /// <remarks>
    /// For a window the person DRAGGED: they chose the position themselves, on
    /// the screen it landed on, and a window manager that then moves it
    /// somewhere else of its own accord is the thing this whole evening was
    /// spent removing. It grows or shrinks from the corner they dropped.
    /// </remarks>
    public static Rect Resize(Rect rect, Rect from, Rect to, AcrossMode mode)
    {
        if (from.Width <= 0 || from.Height <= 0 || !Scales(rect, from, to, mode))
        {
            return rect;
        }

        return rect with
        {
            Width = Math.Max(1, (int)Math.Round(rect.Width * (double)to.Width / from.Width)),
            Height = Math.Max(1, (int)Math.Round(rect.Height * (double)to.Height / from.Height)),
        };
    }

    /// <summary>Hybrid asks the only question that separates it: does it fit?</summary>
    private static bool Scales(Rect rect, Rect from, Rect to, AcrossMode mode) => mode switch
    {
        AcrossMode.Proportional => true,
        AcrossMode.Hybrid => rect.Width > to.Width || rect.Height > to.Height,
        _ => false,
    };
}
