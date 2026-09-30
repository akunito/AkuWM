namespace AkuWM.Core.Ipc;

public static class ConsoleOutput
{
    /// <summary>
    /// The console's default is the OEM code page: a title's bullet came out
    /// as byte 0x07 (CP437 draws it as a bullet), an accented name as '?'
    /// from the CLI, and from the shim an 'í' left as byte 0xA1 -- which the
    /// AutoHotkey reads as UTF-8 (lib-glaze.ahk) and tests/fullscreen could
    /// not decode at all (2026-09-30). UTF-8 on every binary that prints.
    /// </summary>
    public static void Utf8()
    {
        try
        {
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        }
        catch (IOException)
        {
            // No console at all: nothing to set.
        }
    }
}
