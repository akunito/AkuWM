namespace AkuWM.Core.Model;

/// <param name="Window">Which window.</param>
/// <param name="Frame">Where its visible frame should end up.</param>
/// <remarks>
/// The rectangle is the <em>visible frame</em> -- what a person sees -- not the
/// outer rectangle Windows reports. The platform adds the invisible resize
/// border back at the edge, once, so nothing above it has to remember that a
/// window is nine pixels bigger than it looks.
/// </remarks>
/// <param name="Window">Which window.</param>
/// <param name="Frame">Where its VISIBLE frame should be.</param>
/// <param name="Border">
/// How far the window's outer rectangle sits outside its visible frame, as
/// (top, right, bottom, left).
/// </param>
/// <remarks>
/// The border travels with the placement because the model already knows it --
/// it is the difference between the two rectangles on the snapshot AkuWM read
/// when the window last changed. Working it out again at the far end meant a
/// <c>GetWindowRect</c> and a DWM round trip per window, inside the batch,
/// on every redraw.
/// </remarks>
public readonly record struct Placement(
    WindowHandle Window,
    Rect Frame,
    (int Top, int Right, int Bottom, int Left)? Border = null);
