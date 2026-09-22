namespace AkuWM.Core.Model;

/// <summary>
/// Whether AkuWM may set a window's layered alpha.
/// </summary>
/// <remarks>
/// A window that is WS_EX_LAYERED by its own doing is drawn with
/// UpdateLayeredWindow (WPF with AllowsTransparency, Chromium's menus, some
/// games' overlays). Calling SetLayeredWindowAttributes on such a window
/// switches it to the other layered mode and its own UpdateLayeredWindow
/// calls fail from then on: the last frame stays on screen, hit-testing
/// still works, nothing repaints. NordVPN's "Add apps" dialog sat on
/// "Loading..." for ever with the list loaded behind it (2026-09-22 16:38:
/// paused AkuWM, dialog loads and repaints; resumed, LWA_ALPHA 255 appears
/// on it and a checkbox click no longer draws). So the alpha is only ever
/// set on a window AkuWM itself made layered.
/// </remarks>
public static class Layering
{
    /// <param name="windowIsLayered">WS_EX_LAYERED is on the window now.</param>
    /// <param name="layeredByUs">AkuWM put that bit there.</param>
    /// <param name="opacity">What is wanted, 1 for solid.</param>
    /// <returns>Add the bit (when not layered) and set the alpha; or leave the window alone.</returns>
    public static bool ShouldSetAlpha(bool windowIsLayered, bool layeredByUs, double opacity)
    {
        if (opacity < 1)
        {
            // Translucency was asked for: on a window that is already layered
            // by its own doing this breaks its painting, so only a window
            // AkuWM layers itself (or has layered) gets it.
            return !windowIsLayered || layeredByUs;
        }

        // Solid: put 255 back only where AkuWM had lowered it.
        return layeredByUs;
    }
}
