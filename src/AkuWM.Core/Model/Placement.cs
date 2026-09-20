namespace AkuWM.Core.Model;

/// <param name="Window">Which window.</param>
/// <param name="Frame">Where its visible frame should end up.</param>
/// <remarks>
/// The rectangle is the <em>visible frame</em> -- what a person sees -- not the
/// outer rectangle Windows reports. The platform adds the invisible resize
/// border back at the edge, once, so nothing above it has to remember that a
/// window is nine pixels bigger than it looks.
/// </remarks>
public readonly record struct Placement(WindowHandle Window, Rect Frame);
