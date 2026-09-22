namespace AkuWM.Core.Model;

/// <summary>
/// A border AkuWM draws ITSELF around a window it may not decorate.
/// </summary>
/// <remarks>
/// An elevated window refuses every DWM attribute from a process without
/// uiAccess (UIPI, 0x80070006: Purple, the Razer installer), and a game with
/// an anti-cheat is not a window to touch at all. The outline is a separate
/// thin window of AkuWM's own -- layered, click-through, never activated --
/// placed just above the target and following its rectangle. Nothing is
/// injected and nothing on the target changes. Never over a fullscreen
/// window: a window above a game costs it the direct path to the screen.
/// </remarks>
/// <param name="Frame">Where the target's visible frame is; null takes the outline away.</param>
/// <param name="Colour">COLORREF, as the DWM border colour is.</param>
/// <param name="Topmost">The target is in the always-on-top band, so the outline must be too.</param>
/// <param name="Corner">Corner radius at 100 %, as Windows 11 rounds the window itself: 8 round, 4 small, 0 square.</param>
/// <param name="Width">Border width at 100 %.</param>
public readonly record struct Outline(WindowHandle Window, Rect? Frame, uint Colour, bool Topmost, int Corner = 0, int Width = 2)
{
    /// <summary>The radius Windows 11 gives a window for each corner preference.</summary>
    public static int RadiusOf(Corners corners) => corners switch
    {
        Corners.Square => 0,
        Corners.RoundSmall => 4,
        _ => 8, // Round, and Default -- Windows 11 rounds an ordinary window by 8
    };
}
