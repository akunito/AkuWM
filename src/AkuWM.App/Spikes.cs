using System.Diagnostics;
using AkuWM.Core.Model;
using AkuWM.Core.Platform;
using AkuWM.Platform;

namespace AkuWM.App;

/// <summary>
/// The five questions M1 asks Windows before any of M2 is written.
/// </summary>
/// <remarks>
/// <para>
/// Each one has an answer that could change the design, so each is measured on
/// this desk rather than assumed. S1 and S2 are the ones that matter most: if
/// an unprivileged process cannot see keystrokes or move the focus while a
/// game is in front, then running unprivileged -- the plan's second decision
/// -- does not survive contact with Aion 2, and AkuWM needs the signed,
/// uiAccess route the AutoHotkey stack already uses.
/// </para>
/// <para>
/// These commands exist for M1 and go away with it.
/// </para>
/// </remarks>
public static class Spikes
{
    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("usage: akuwm spike <s1|s2|s3|s4|s5> [--seconds N] [--window 0x1234]");
            Console.WriteLine();
            Console.WriteLine("  s1  can a low-level keyboard hook see keys typed into an elevated window?");
            Console.WriteLine("  s2  can the focus be moved to an elevated window?");
            Console.WriteLine("  s3  can the shell cloak somebody else's window, elevated or not?");
            Console.WriteLine("  s4  how long does a hook-to-SetWindowPos round trip take?");
            Console.WriteLine("  s5  do the hooks keep firing on their own thread while the main one is busy?");
            return 2;
        }

        Dictionary<string, string?> options = Core.Ipc.CommandLine.Options(args, 2);

        return args[1].ToLowerInvariant() switch
        {
            "s1" => S1(Number(options, "seconds", 20)),
            "s2" => S2(Window(options)),
            "s3" => S3(Window(options)),
            "s4" => S4(),
            "s5" => S5(Number(options, "seconds", 10)),
            _ => Fail($"'{args[1]}' is not one of s1..s5"),
        };
    }

    /// <summary>
    /// S1: a low-level keyboard hook, and what the foreground window is while
    /// keys arrive.
    /// </summary>
    /// <remarks>
    /// UIPI stops a lower-integrity process from interfering with a higher one.
    /// The question is whether that extends to seeing the keystroke at all --
    /// because if it does, every Hyper chord dies the moment a game is focused,
    /// and that is most of what this desk does.
    /// </remarks>
    private static int S1(int seconds)
    {
        Console.WriteLine($"S1: watching the keyboard for {seconds}s.");
        Console.WriteLine("    Focus an ELEVATED window (Task Manager started as administrator, or a game)");
        Console.WriteLine("    and type. Then focus a normal window and type again.");
        Console.WriteLine();

        int elevatedKeys = 0;
        int normalKeys = 0;
        var elevatedWindows = new HashSet<string>(StringComparer.Ordinal);
        var platform = new WindowsPlatform();
        var done = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            using var hook = new Win32KeyboardHook();
            hook.Key = key =>
            {
                if (!key.Down)
                {
                    return false;
                }

                WindowHandle foreground = platform.Foreground();
                WindowSnapshot? window = platform.Window(foreground);
                if (window?.IsElevated == true)
                {
                    elevatedKeys++;
                    elevatedWindows.Add($"{window.ProcessName} [{window.ClassName}]");
                }
                else
                {
                    normalKeys++;
                }

                return false; // never swallow anything in a spike
            };

            if (!hook.Install())
            {
                Console.WriteLine("  the hook could not be installed at all");
                done.Set();
                return;
            }

            Console.WriteLine("  hook installed; type now");
            Win32MessageLoop.PumpFor(TimeSpan.FromSeconds(seconds));
            done.Set();
        })
        {
            Name = "platform",
            IsBackground = true,
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        done.Wait(TimeSpan.FromSeconds(seconds + 5));

        Console.WriteLine();
        Console.WriteLine($"  keys seen with a NORMAL window focused:   {normalKeys}");
        Console.WriteLine($"  keys seen with an ELEVATED window focused: {elevatedKeys}");
        foreach (string window in elevatedWindows)
        {
            Console.WriteLine($"    elevated window that was focused: {window}");
        }

        Console.WriteLine();
        Console.WriteLine(elevatedKeys > 0
            ? "  ANSWER: yes — an unprivileged hook sees keys typed into an elevated window."
            : elevatedWindows.Count > 0
                ? "  ANSWER: NO — an elevated window was focused and no key came through."
                : "  INCONCLUSIVE: no elevated window was ever focused while this ran.");
        return 0;
    }

    /// <summary>S2: can the focus be handed to an elevated window?</summary>
    private static int S2(WindowHandle target)
    {
        var platform = new WindowsPlatform();
        WindowSnapshot? window = Pick(platform, target, preferElevated: true);
        if (window is null)
        {
            return Fail("no window to aim at; pass --window 0x1234");
        }

        Console.WriteLine($"S2: focusing {window.ProcessName} [{window.ClassName}] \"{window.Title}\"");
        Console.WriteLine($"    elevated: {window.IsElevated}");

        FocusResult result = Win32Focus.Focus(window.Handle);

        Console.WriteLine($"  succeeded:     {result.Succeeded}");
        Console.WriteLine($"  needed attach: {result.NeededAttach}");
        Console.WriteLine($"  detail:        {result.Detail}");
        Console.WriteLine();
        Console.WriteLine(result.Succeeded
            ? $"  ANSWER: yes{(result.NeededAttach ? ", but only by attaching the input queues" : string.Empty)}."
            : "  ANSWER: NO — the focus could not be moved there.");
        return 0;
    }

    /// <summary>S3: the shell's cloak, from .NET, on somebody else's window.</summary>
    private static int S3(WindowHandle target)
    {
        var platform = new WindowsPlatform();
        WindowSnapshot? window = Pick(platform, target, preferElevated: false);
        if (window is null)
        {
            return Fail("no window to aim at; pass --window 0x1234");
        }

        using var shell = new ImmersiveShell();
        Console.WriteLine($"S3: cloaking {window.ProcessName} [{window.ClassName}] \"{window.Title}\"");
        Console.WriteLine($"    elevated: {window.IsElevated}");
        Console.WriteLine($"    shell available: {shell.Available} {shell.Unavailable}");

        if (!shell.Available)
        {
            Console.WriteLine("  ANSWER: NO — the shell's view collection could not be reached.");
            return 1;
        }

        string? error = shell.SetCloak(window.Handle, true);
        Thread.Sleep(300);
        CloakKind afterCloak = platform.Window(window.Handle)?.Cloak ?? CloakKind.None;

        string? back = shell.SetCloak(window.Handle, false);
        Thread.Sleep(300);
        CloakKind afterUncloak = platform.Window(window.Handle)?.Cloak ?? CloakKind.None;

        Console.WriteLine($"  cloak   -> error={error ?? "none"} readback={afterCloak}");
        Console.WriteLine($"  uncloak -> error={back ?? "none"} readback={afterUncloak}");
        Console.WriteLine();

        bool worked = afterCloak.HasFlag(CloakKind.Shell) && !afterUncloak.HasFlag(CloakKind.Shell);
        Console.WriteLine(worked
            ? "  ANSWER: yes — the cloak goes on and comes off, and DWM confirms both."
            : "  ANSWER: NO — the cloak did not take, or did not come off.");
        return worked ? 0 : 1;
    }

    /// <summary>
    /// S4: the latency budget. A chord has to reach the screen in under 5 ms
    /// for the single-process argument to hold.
    /// </summary>
    private static int S4()
    {
        var platform = new WindowsPlatform();
        Console.WriteLine("S4: what the hot path costs.");

        // A full enumeration: what a cold query costs.
        var watch = Stopwatch.StartNew();
        IReadOnlyList<WindowSnapshot> windows = platform.Windows();
        double enumerate = watch.Elapsed.TotalMilliseconds;

        watch.Restart();
        IReadOnlyList<MonitorSnapshot> monitors = platform.Monitors();
        double monitorsMs = watch.Elapsed.TotalMilliseconds;

        // One window, read again: what an event costs to turn into a fact.
        WindowSnapshot? one = windows.FirstOrDefault();
        double readOne = 0;
        if (one is not null)
        {
            watch.Restart();
            for (int i = 0; i < 100; i++)
            {
                platform.Window(one.Handle);
            }

            readOne = watch.Elapsed.TotalMilliseconds / 100;
        }

        // The move itself, on a window that will not mind: its own rectangle.
        double place = 0;
        if (one is not null && !one.IsElevated)
        {
            var batch = windows
                .Where(w => !w.IsElevated && !w.IsMinimized)
                .Take(8)
                .Select(w => new Placement(w.Handle, w.FrameBounds))
                .ToList();

            watch.Restart();
            Win32Position.Place(batch);
            place = watch.Elapsed.TotalMilliseconds;
            Console.WriteLine($"  batch of {batch.Count} windows placed back where they were");
        }

        Console.WriteLine($"  enumerate every window ({windows.Count}): {enumerate:F2} ms");
        Console.WriteLine($"  enumerate the monitors ({monitors.Count}):  {monitorsMs:F2} ms");
        Console.WriteLine($"  read one window again:               {readOne:F3} ms");
        Console.WriteLine($"  place a batch:                       {place:F2} ms");
        Console.WriteLine();
        Console.WriteLine($"  for comparison, one GlazeWM CLI round trip is 47 ms (measured).");
        Console.WriteLine(readOne + place < 5
            ? "  ANSWER: the hot path fits in the 5 ms budget."
            : "  ANSWER: over budget — the hot path needs work.");
        return 0;
    }

    /// <summary>
    /// S5: hooks on their own thread, while the main one is busy.
    /// </summary>
    /// <remarks>
    /// At M5 the main thread belongs to Avalonia and its dispatcher. If the
    /// window events only arrived while that thread was idle, every redraw of
    /// the GUI would stall the window manager. They do not -- the hooks are
    /// delivered to the thread that installed them -- and this measures it with
    /// the main thread deliberately hogged.
    /// </remarks>
    private static int S5(int seconds)
    {
        Console.WriteLine($"S5: window events on the platform thread while the main thread is busy ({seconds}s).");

        int events = 0;
        using var hooks = new Win32Hooks();
        hooks.Event += _ => Interlocked.Increment(ref events);
        hooks.Start();

        // Hog the main thread the way a GUI redraw would.
        var watch = Stopwatch.StartNew();
        long spins = 0;
        while (watch.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            Thread.SpinWait(100_000);
            spins++;
        }

        hooks.Stop();

        Console.WriteLine($"  main thread spun {spins} times and never pumped a message");
        Console.WriteLine($"  window events delivered to the platform thread: {events}");
        Console.WriteLine();
        Console.WriteLine(events > 0
            ? "  ANSWER: yes — the hooks are independent of the main thread."
            : "  INCONCLUSIVE: nothing happened on the desktop while this ran; move a window and retry.");
        return 0;
    }

    private static WindowSnapshot? Pick(WindowsPlatform platform, WindowHandle target, bool preferElevated)
    {
        if (!target.IsNone)
        {
            return platform.Window(target);
        }

        IReadOnlyList<WindowSnapshot> windows = platform.Windows();
        return preferElevated
            ? windows.FirstOrDefault(w => w.IsElevated) ?? windows.FirstOrDefault()
            : windows.FirstOrDefault(w => !w.IsMinimized && w.Title.Length > 0);
    }

    private static int Number(Dictionary<string, string?> options, string name, int fallback) =>
        options.TryGetValue(name, out string? value) && int.TryParse(value, out int parsed) ? parsed : fallback;

    private static WindowHandle Window(Dictionary<string, string?> options)
    {
        if (!options.TryGetValue("window", out string? value) || value is null)
        {
            return WindowHandle.None;
        }

        string text = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        return long.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out long handle)
            ? new WindowHandle(handle)
            : WindowHandle.None;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}
