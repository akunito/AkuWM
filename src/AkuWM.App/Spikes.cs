using System.Diagnostics;
using System.Text;
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
/// These commands exist for M1 and go away with it. Everything they print is
/// also written to <c>spike-results.txt</c> next to the executable, because an
/// interactive measurement that scrolls off a console is not a measurement.
/// </para>
/// </remarks>
public static class Spikes
{
    private static readonly StringBuilder Transcript = new();

    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Say("usage: akuwm spike <s1|s2|s12|s3|s4|s5> [--seconds N] [--window 0x1234]");
            Say(string.Empty);
            Say("  s1   can a low-level keyboard hook see keys typed into an elevated window?");
            Say("  s2   can the focus be moved to an elevated window, cold?");
            Say("  s12  both at once, the way it will actually happen (interactive)");
            Say("  s3   can the shell cloak somebody else's window?");
            Say("  s4   how long does the hot path take?");
            Say("  s5   do the hooks keep firing while the main thread is busy?");
            Say("  s6   when a cloak refuses to come off, is it never cleared or put straight back?");
            Say("  s7   is there ANY call that brings back a window a dead manager left hidden?");
            return 2;
        }

        Dictionary<string, string?> options = Core.Ipc.CommandLine.Options(args, 2);

        int code = args[1].ToLowerInvariant() switch
        {
            "s1" => KeysAndFocus(Number(options, "seconds", 30), Window(options), focusTest: false),
            "s2" => ColdFocus(Window(options)),
            "s12" => KeysAndFocus(Number(options, "seconds", 30), Window(options), focusTest: true),
            "s3" => Cloak(Window(options)),
            "s4" => HotPath(),
            "s5" => Threads(Number(options, "seconds", 10)),
            "s6" => CloakFight(Window(options), Number(options, "seconds", 3)),
            "s7" => RescueCloaked(Window(options)),
            _ => Fail($"'{args[1]}' is not one of s1, s2, s12, s3, s4, s5"),
        };

        Save();
        return code;
    }

    /// <summary>
    /// S2, cold: no foreground right at all, which is the worst case -- a
    /// command arriving over the pipe, or a repair after a display change.
    /// </summary>
    private static int ColdFocus(WindowHandle target)
    {
        using var platform = new WindowsPlatform();
        WindowSnapshot? window = Pick(platform, target, preferElevated: true);
        if (window is null)
        {
            return Fail("no window to aim at; pass --window 0x1234");
        }

        Say($"S2 (cold): focusing {window.ProcessName} [{window.ClassName}] \"{window.Title}\"");
        Say($"    elevated: {window.IsElevated}");
        Say("    this process has NOT just received input, so it holds no foreground right");

        FocusResult result = Win32Focus.Focus(window.Handle);
        Say($"  route:  {result.Route}");
        Say($"  detail: {result.Detail}");
        Say(string.Empty);
        Say(result.Succeeded
            ? $"  ANSWER: yes, even cold — by {result.Route}."
            : "  ANSWER: not cold. Try s12, which asks the question the way it will really be asked.");
        return result.Succeeded ? 0 : 1;
    }

    /// <summary>
    /// S1 and S2, in two phases, each one asking a question that can be
    /// answered wrongly only by Windows and not by the person running it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first attempt at this could not tell "no key was typed into the
    /// elevated window" from "the hook never saw the keys" -- in both cases
    /// nothing arrives. So the foreground window is now sampled on a timer as
    /// well: if an elevated window was in front for seconds and not one
    /// keystroke came through, that is the answer, and it is a bad one.
    /// </para>
    /// <para>
    /// The two phases are kept apart because the focus test steals the
    /// foreground, which would ruin the keyboard test if they overlapped.
    /// </para>
    /// </remarks>
    private static int KeysAndFocus(int seconds, WindowHandle target, bool focusTest)
    {
        using var platform = new WindowsPlatform();
        WindowSnapshot? elevated = Pick(platform, target, preferElevated: true);

        if (elevated?.IsElevated != true)
        {
            Say("    NO elevated window is open — start Task Manager as administrator first.");
            return 1;
        }

        Say($"    elevated window: {elevated.ProcessName} \"{elevated.Title}\"");
        Say(string.Empty);

        int keyPhase = focusTest ? Math.Max(10, (seconds * 2) / 3) : seconds;
        KeyboardFinding keys = WatchKeyboard(platform, keyPhase);

        if (!focusTest)
        {
            return 0;
        }

        Say(string.Empty);
        Say($"== phase 2 of 2: click a NORMAL window and press one key ({seconds - keyPhase}s) ==");
        Say("   AkuWM will try to pull the elevated window to the front the instant it sees it,");
        Say("   which is exactly what a chord does.");
        FocusFinding focus = WatchFocus(platform, elevated, seconds - keyPhase);

        Say(string.Empty);
        Say("  S1 ANSWER: " + keys.Answer);
        Say("  S2 ANSWER: " + focus.Answer);
        return 0;
    }

    private readonly record struct KeyboardFinding(int Elevated, int Normal, double ElevatedSeconds, string Answer);

    private readonly record struct FocusFinding(string Answer);

    /// <summary>
    /// Phase one: count keystrokes, and independently measure how long an
    /// elevated window was in front. The second number is what makes the first
    /// one mean something.
    /// </summary>
    private static KeyboardFinding WatchKeyboard(WindowsPlatform platform, int seconds)
    {
        Say($"== phase 1 of 2: click the ELEVATED window and type ({seconds}s) ==");

        int elevatedKeys = 0;
        int normalKeys = 0;
        int elevatedSamples = 0;
        int samples = 0;
        var elevatedSeen = new HashSet<string>(StringComparer.Ordinal);
        var stopping = new CancellationTokenSource();

        // The sampler runs whether or not anything is typed, so "an elevated
        // window was in front for 8 seconds and no key arrived" is a statement
        // this spike can make.
        var sampler = new Thread(() =>
        {
            while (!stopping.IsCancellationRequested)
            {
                WindowSnapshot? front = platform.Window(platform.Foreground());
                samples++;
                if (front?.IsElevated == true)
                {
                    elevatedSamples++;
                    elevatedSeen.Add($"{front.ProcessName} [{front.ClassName}]");
                }

                Thread.Sleep(200);
            }
        })
        {
            IsBackground = true,
            Name = "foreground-sampler",
        };
        sampler.Start();

        var done = new ManualResetEventSlim(false);
        var hookThread = new Thread(() =>
        {
            using var hook = new Win32KeyboardHook();
            hook.Key = key =>
            {
                if (!key.Down)
                {
                    return false;
                }

                WindowSnapshot? front = platform.Window(platform.Foreground());
                if (front?.IsElevated == true)
                {
                    elevatedKeys++;
                }
                else
                {
                    normalKeys++;
                }

                return false; // a spike never swallows anything
            };

            if (!hook.Install())
            {
                Say("  the hook could not be installed at all");
                done.Set();
                return;
            }

            Say("  hook installed; type into the elevated window now");
            Win32MessageLoop.PumpFor(TimeSpan.FromSeconds(seconds));
            done.Set();
        })
        {
            Name = "platform",
            IsBackground = true,
        };

        hookThread.SetApartmentState(ApartmentState.STA);
        hookThread.Start();
        done.Wait(TimeSpan.FromSeconds(seconds + 5));
        stopping.Cancel();

        double elevatedSeconds = samples == 0 ? 0 : elevatedSamples * 0.2;

        Say(string.Empty);
        Say($"  an elevated window was in front for {elevatedSeconds:F1}s of {seconds}s");
        foreach (string window in elevatedSeen)
        {
            Say($"    it was: {window}");
        }

        Say($"  keys seen while it was in front:        {elevatedKeys}");
        Say($"  keys seen while a normal window was:    {normalKeys}");

        string answer = elevatedKeys > 0
            ? "yes — an unprivileged hook sees keys typed into an elevated window."
            : elevatedSeconds < 2
                ? "inconclusive — the elevated window was barely in front; run it again and type into it."
                : normalKeys == 0
                    ? "inconclusive — no key reached the hook at all, so nothing was measured."
                    : "NO — an elevated window was in front and typed into, and not one key reached the hook. " +
                      "AkuWM cannot own chords over a game without uiAccess.";

        return new KeyboardFinding(elevatedKeys, normalKeys, elevatedSeconds, answer);
    }

    /// <summary>
    /// Phase two: the chord path. A key arrives, and the process -- which now
    /// holds the foreground right because it just received input -- moves the
    /// focus to the elevated window.
    /// </summary>
    private static FocusFinding WatchFocus(WindowsPlatform platform, WindowSnapshot elevated, int seconds)
    {
        FocusResult? result = null;
        var wanted = new SemaphoreSlim(0);
        var stopping = new CancellationTokenSource();

        // Off the hook on purpose: the foreground right belongs to the process,
        // not the thread, and a low-level hook that blocks for a hundred
        // milliseconds is removed by Windows without asking.
        var worker = new Thread(() =>
        {
            while (!stopping.IsCancellationRequested)
            {
                if (wanted.Wait(200) && result is null)
                {
                    result = Win32Focus.Focus(elevated.Handle);
                }
            }
        })
        {
            IsBackground = true,
            Name = "focus-worker",
        };
        worker.Start();

        var done = new ManualResetEventSlim(false);
        var hookThread = new Thread(() =>
        {
            using var hook = new Win32KeyboardHook();
            hook.Key = key =>
            {
                if (key.Down && result is null)
                {
                    WindowSnapshot? front = platform.Window(platform.Foreground());
                    if (front?.IsElevated != true)
                    {
                        wanted.Release();
                    }
                }

                return false;
            };

            if (hook.Install())
            {
                Win32MessageLoop.PumpFor(TimeSpan.FromSeconds(seconds));
            }

            done.Set();
        })
        {
            Name = "platform",
            IsBackground = true,
        };

        hookThread.SetApartmentState(ApartmentState.STA);
        hookThread.Start();
        done.Wait(TimeSpan.FromSeconds(seconds + 5));
        stopping.Cancel();

        if (result is not { } focus)
        {
            return new FocusFinding("inconclusive — no key was typed into a normal window while this ran.");
        }

        Say($"  focus attempt: {focus.Route} ({focus.Detail})");
        return new FocusFinding(focus.Succeeded
            ? $"yes — right after a keystroke the focus moves to an elevated window, by {focus.Route}."
            : $"NO — {focus.Detail}");
    }

    /// <summary>S3: the shell's cloak, from .NET, on somebody else's window.</summary>
    private static int Cloak(WindowHandle target)
    {
        using var platform = new WindowsPlatform();
        WindowSnapshot? window = Pick(platform, target, preferElevated: false);
        if (window is null)
        {
            return Fail("no window to aim at; pass --window 0x1234");
        }

        using var shell = new ImmersiveShell();
        Say($"S3: cloaking {window.ProcessName} [{window.ClassName}] \"{window.Title}\"");
        Say($"    elevated: {window.IsElevated}");
        Say($"    shell available: {shell.Available} {shell.Unavailable}");

        if (!shell.Available)
        {
            Say("  ANSWER: NO — the shell's view collection could not be reached.");
            return 1;
        }

        string? error = shell.SetCloak(window.Handle, true);
        Thread.Sleep(300);
        CloakKind afterCloak = platform.Window(window.Handle)?.Cloak ?? CloakKind.None;

        string? back = shell.SetCloak(window.Handle, false);
        Thread.Sleep(300);
        CloakKind afterUncloak = platform.Window(window.Handle)?.Cloak ?? CloakKind.None;

        Say($"  cloak   -> error={error ?? "none"} readback={afterCloak}");
        Say($"  uncloak -> error={back ?? "none"} readback={afterUncloak}");
        Say(string.Empty);

        bool worked = afterCloak.HasFlag(CloakKind.Shell) && !afterUncloak.HasFlag(CloakKind.Shell);
        Say(worked
            ? "  ANSWER: yes — the cloak goes on and comes off, and DWM confirms both."
            : "  ANSWER: NO — the cloak did not take, or did not come off.");
        return worked ? 0 : 1;
    }

    /// <summary>
    /// S6: a cloak that will not come off — is it never cleared, or cleared
    /// and put straight back by something else?
    /// </summary>
    /// <remarks>
    /// The two look identical from one read and mean completely different
    /// things. Never cleared means the shell is refusing, and the window
    /// genuinely belongs somewhere else. Put back means a program on this
    /// machine is watching and re-hiding it, and AkuWM will be fighting that
    /// program for every window it shows.
    /// </remarks>
    private static int CloakFight(WindowHandle target, int seconds)
    {
        using var platform = new WindowsPlatform();

        WindowSnapshot? window = target.IsNone
            ? platform.Windows().FirstOrDefault(w => w.Cloak.HasFlag(CloakKind.Shell))
            : platform.Window(target);

        if (window is null)
        {
            return Fail("no cloaked window to look at; pass --window 0x1234");
        }

        Say($"S6: {window.ProcessName} [{window.ClassName}] \"{window.Title}\"");
        Say($"    cloak before: {window.Cloak}");
        Say($"    on the current virtual desktop, says Windows: {window.OnCurrentVirtualDesktop}");

        using var shell = new ImmersiveShell();
        string? error = shell.SetCloak(window.Handle, false);
        Say($"    uncloak call: {error ?? "no error"}");

        var samples = new List<(double Ms, CloakKind Cloak)>();
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            samples.Add((watch.Elapsed.TotalMilliseconds, platform.Window(window.Handle)?.Cloak ?? CloakKind.None));
            Thread.Sleep(40);
        }

        Say(string.Empty);
        CloakKind first = samples[0].Cloak;
        bool everCleared = samples.Any(sample => !sample.Cloak.HasFlag(CloakKind.Shell));
        (double Ms, CloakKind Cloak) back = samples
            .SkipWhile(sample => sample.Cloak.HasFlag(CloakKind.Shell))
            .FirstOrDefault(sample => sample.Cloak.HasFlag(CloakKind.Shell));

        Say($"  first read after the call ({samples[0].Ms:F0} ms): {first}");
        Say($"  it was uncloaked at some point:       {everCleared}");
        if (everCleared && back != default)
        {
            Say($"  and cloaked again after:              {back.Ms:F0} ms");
        }

        Say($"  final state:                          {samples[^1].Cloak}");
        Say(string.Empty);

        if (!everCleared)
        {
            Say("  ANSWER: never cleared. The shell is refusing to uncloak it, which means the window");
            Say("          belongs somewhere this desktop cannot show — another virtual desktop.");
        }
        else if (back != default)
        {
            Say($"  ANSWER: cleared, then re-cloaked after {back.Ms:F0} ms by something else on this machine.");
            Say("          AkuWM would be fighting that program for every window it shows.");
        }
        else
        {
            Say("  ANSWER: it came off and stayed off.");
        }

        return 0;
    }

    /// <summary>
    /// S7: can a window a dead window manager left hidden be brought back at
    /// all?
    /// </summary>
    /// <remarks>
    /// This is the one that matters for living with AkuWM rather than for
    /// building it. Windows have been lost on this desk every time the manager
    /// was restarted mid-test, and "open a new one" is not an answer. The type
    /// a cloak was set with cannot be read back, so every combination is tried
    /// in turn and the flag is watched after each.
    /// </remarks>
    private static int RescueCloaked(WindowHandle target)
    {
        using var platform = new WindowsPlatform();

        WindowSnapshot? window = target.IsNone
            ? platform.Windows().FirstOrDefault(w => w.Cloak.HasFlag(CloakKind.Shell))
            : platform.Window(target);

        if (window is null)
        {
            Say("S7: nothing on this desk is cloaked. Nothing to rescue, which is the good outcome.");
            return 0;
        }

        Say($"S7: {window.ProcessName} [{window.ClassName}] \"{window.Title}\"");
        Say($"    cloak before: {window.Cloak}");
        Say(string.Empty);

        using var shell = new ImmersiveShell();
        string? worked = null;

        foreach ((string what, string? error) in shell.TryEveryUncloak(window.Handle))
        {
            Thread.Sleep(120);
            CloakKind after = platform.Window(window.Handle)?.Cloak ?? CloakKind.None;
            Say($"  {what,-28} error={error ?? "none",-34} cloak={after}");

            if (!after.HasFlag(CloakKind.Shell))
            {
                worked = what;
                break;
            }
        }

        Say(string.Empty);
        if (worked is not null)
        {
            Say($"  ANSWER: yes — {worked} brings it back.");
            return 0;
        }

        // Last resort: ask the window itself to show, which goes through a
        // different path entirely.
        Say("  none of the cloak calls worked; trying ShowWindow instead");
        Win32Show.Show(window.Handle);
        Thread.Sleep(200);
        CloakKind final = platform.Window(window.Handle)?.Cloak ?? CloakKind.None;
        Say($"  after ShowWindow: cloak={final}");

        Say(string.Empty);
        Say(final.HasFlag(CloakKind.Shell)
            ? "  ANSWER: NO — nothing this process can call brings it back."
            : "  ANSWER: yes — ShowWindow brings it back where the cloak calls do not.");
        return 0;
    }

    /// <summary>S4: the latency budget.</summary>
    private static int HotPath()
    {
        using var platform = new WindowsPlatform();
        Say("S4: what the hot path costs. (`akuwm bench` is the permanent version of this.)");

        var watch = Stopwatch.StartNew();
        IReadOnlyList<WindowSnapshot> windows = platform.Windows();
        double enumerate = watch.Elapsed.TotalMilliseconds;

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

        var batch = windows
            .Where(w => !w.IsElevated && !w.IsMinimized)
            .Take(8)
            .Select(w => new Placement(w.Handle, w.FrameBounds))
            .ToList();

        watch.Restart();
        Win32Position.Place(batch);
        double place = watch.Elapsed.TotalMilliseconds;

        Say($"  enumerate every window ({windows.Count}): {enumerate:F2} ms (cold)");
        Say($"  read one window again:               {readOne:F3} ms");
        Say($"  place {batch.Count} windows in one batch:      {place:F2} ms");
        Say(string.Empty);
        Say("  for comparison, one GlazeWM CLI round trip is 47 ms (measured).");
        Say(readOne + place < 5
            ? "  ANSWER: the hot path fits in the 5 ms budget."
            : "  ANSWER: over budget — the hot path needs work.");
        return 0;
    }

    /// <summary>S5: hooks on their own thread while the main one is busy.</summary>
    private static int Threads(int seconds)
    {
        Say($"S5: window events on the platform thread while the main thread is busy ({seconds}s).");

        int events = 0;
        using var hooks = new Win32Hooks();
        hooks.Event += _ => Interlocked.Increment(ref events);
        hooks.Start();

        var watch = Stopwatch.StartNew();
        long spins = 0;
        while (watch.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            Thread.SpinWait(100_000);
            spins++;
        }

        hooks.Stop();

        Say($"  main thread spun {spins} times and never pumped a message");
        Say($"  window events delivered to the platform thread: {events}");
        Say(string.Empty);
        Say(events > 0
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

    private static void Say(string line)
    {
        Console.WriteLine(line);
        Transcript.AppendLine(line);
    }

    private static void Save()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "spike-results.txt");
            File.AppendAllText(
                path,
                $"--- {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---{Environment.NewLine}{Transcript}{Environment.NewLine}");
            Console.WriteLine($"(also written to {path})");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"could not write the transcript: {ex.Message}");
        }
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
        Say(message);
        return 1;
    }
}
