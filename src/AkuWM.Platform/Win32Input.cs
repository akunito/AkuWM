using Windows.Win32;

namespace AkuWM.Platform;

/// <summary>The mouse buttons, read without a hook: one call per pass, and only while a raise waits.</summary>
public static class Win32Input
{
    private const int VkLButton = 0x01;
    private const int VkRButton = 0x02;
    private const int VkMButton = 0x04;

    public static bool AnyMouseButtonDown() =>
        (PInvoke.GetAsyncKeyState(VkLButton) & 0x8000) != 0
        || (PInvoke.GetAsyncKeyState(VkRButton) & 0x8000) != 0
        || (PInvoke.GetAsyncKeyState(VkMButton) & 0x8000) != 0;
}
