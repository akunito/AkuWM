using AkuWM.Core.Model;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// The lock screen is a visible, titled, uncloaked top-level window for as
/// long as the desk is locked. At the 10:15 resume on 2026-10-01 AkuWM tiled
/// it (LockApp, "Windows Default Lock Screen", 3840x2160) on the displayed
/// workspace; the shell cloaked it at unlock, the window was refused as
/// cloaked-elsewhere, and its tiling slot stayed behind: two test windows on
/// that workspace were laid out in the right two thirds. The shell's surfaces
/// are never the desk's to arrange.
/// </summary>
public class ShellSurfaceTests
{
    [Fact]
    public void The_lock_screen_is_never_managed_and_never_enters_the_layout()
    {
        var f = new DeskFixture();
        f.Open(1);
        f.Turn();
        f.Platform.WindowList.Add(FakePlatform.Window(2, "LockApp", className: "Windows.UI.Core.CoreWindow", title: "Windows Default Lock Screen"));
        f.Sync();
        DeskWindowOrNull(f, 2, out bool managed, out UnmanagedReason reason);
        Assert.False(managed);
        Assert.Equal(UnmanagedReason.Shell, reason);
        Assert.DoesNotContain(f.Turn().Place, p => p.Window == new WindowHandle(2));
        // Unlock: the shell cloaks it. Still unmanaged, still no slot.
        f.Platform.WindowList[^1] = FakePlatform.Window(2, "LockApp", className: "Windows.UI.Core.CoreWindow", title: "Windows Default Lock Screen", cloak: CloakKind.Shell);
        f.Desk.Observe(f.Platform.WindowList[^1]);
        f.Platform.WindowList.Add(FakePlatform.Window(3, "notepad", className: "Notepad", title: "Untitled"));
        f.Sync();
        var turn = f.Turn();
        Assert.Contains(turn.Place, p => p.Window == new WindowHandle(3));
        Assert.DoesNotContain(turn.Place, p => p.Window == new WindowHandle(2));
    }

    [Theory]
    [InlineData("ShellExperienceHost", "Windows.UI.Core.CoreWindow", "New notification")]
    [InlineData("StartMenuExperienceHost", "Windows.UI.Core.CoreWindow", "Start")]
    [InlineData("SearchHost", "Windows.UI.Core.CoreWindow", "Search")]
    [InlineData("TextInputHost", "Windows.UI.Core.CoreWindow", "Windows Input Experience")]
    public void The_other_shell_surfaces_are_shell_too(string process, string className, string title)
    {
        Assert.True(ShadowModel.IsShellSurface(FakePlatform.Window(9, process, className: className, title: title)));
    }

    [Fact]
    public void An_application_hosted_by_ApplicationFrameHost_is_not_shell()
    {
        // A UWP app's CoreWindow sits inside ApplicationFrameHost; the desk
        // sees the host window, with its own class.
        Assert.False(ShadowModel.IsShellSurface(FakePlatform.Window(9, "ApplicationFrameHost", className: "ApplicationFrame Window", title: "Settings")));
        Assert.False(ShadowModel.IsShellSurface(FakePlatform.Window(9, "notepad", className: "Notepad", title: "Untitled")));
    }

    private static void DeskWindowOrNull(DeskFixture f, long handle, out bool managed, out UnmanagedReason reason)
    {
        var w = f.Managed(handle);
        Assert.NotNull(w);
        managed = w!.Managed;
        reason = w.Reason;
    }
}
