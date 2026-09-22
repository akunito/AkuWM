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
public readonly record struct Outline(WindowHandle Window, Rect? Frame, uint Colour, bool Topmost);
