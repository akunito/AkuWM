namespace AkuWM.Core.Model;

/// <summary>What Windows should draw around a window.</summary>
public enum Corners
{
    /// <summary>Leave whatever Windows would do.</summary>
    Default,

    /// <summary>Square, which is what tiling wants: a rounded corner leaks the desktop through every gap.</summary>
    Square,

    Round,

    RoundSmall,
}

/// <summary>
/// The decoration AkuWM asks the shell to draw on one window.
/// </summary>
/// <remarks>
/// <para>
/// A value type with no nulls, so "what it should look like" can be compared
/// against "what it looks like" with one equality check and nothing is sent
/// twice. <see cref="Border"/> carries its own absence: see <see cref="NoBorder"/>.
/// </para>
/// <para>
/// Unlike a cloak or a move, none of this is dangerous to leave behind -- a
/// window with a coloured border after AkuWM dies is wrong-looking, not lost --
/// so it is reverted on unmanage and on the way out, and not journalled.
/// </para>
/// </remarks>
/// <param name="Border">COLORREF, or one of the two sentinels below.</param>
/// <param name="Corners">What Windows draws at the corners.</param>
/// <param name="TitleBar">False strips the caption, and the buttons with it.</param>
/// <param name="Opacity">1 is solid. Never 0: see the remarks.</param>
public readonly record struct Decoration(
    uint Border,
    Corners Corners,
    bool TitleBar = true,
    double Opacity = 1)
{
    /// <summary>The shell's own border, which is what a window has before AkuWM touches it.</summary>
    public const uint DefaultBorder = 0xFFFFFFFF;

    /// <summary>No border at all, which is not the same as the default one.</summary>
    public const uint NoBorder = 0xFFFFFFFE;

    /// <summary>What a window looks like when AkuWM has never touched it.</summary>
    public static readonly Decoration Untouched = new(DefaultBorder, Corners.Default, true, 1);

    /// <summary>
    /// The least solid a window may be made.
    /// </summary>
    /// <remarks>
    /// Measured, and in the plan: a window at zero is optimised away by the
    /// compositor and stops taking clicks, which reads as a window that
    /// vanished rather than one that went transparent.
    /// </remarks>
    public const double MinimumOpacity = 0.05;

    /// <summary>
    /// <c>#rrggbb</c> as the shell wants it.
    /// </summary>
    /// <remarks>
    /// COLORREF is 0x00BBGGRR -- blue first -- and every colour in the
    /// configuration is written the other way round. Getting this wrong is not
    /// an error, it is a border in the wrong colour, which is the kind of
    /// thing that survives a code review.
    /// </remarks>
    public static uint ColorRef(string? rrggbb)
    {
        if (rrggbb is not { Length: 7 } text || text[0] != '#'
            || !uint.TryParse(text.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out uint rgb))
        {
            return NoBorder;
        }

        return ((rgb & 0x0000FF) << 16) | (rgb & 0x00FF00) | ((rgb & 0xFF0000) >> 16);
    }
}
