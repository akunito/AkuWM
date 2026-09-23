using System.Text;
using AkuWM.Gui.Sections;
using Avalonia.Threading;

namespace AkuWM.Gui.Shell;

/// <summary>
/// <c>akuwm-gui --smoke &lt;file&gt;</c>: opens every section on the real desk,
/// then edits a rule through the same path the Save button takes and checks
/// the daemon sees it. PASS/FAIL lines in the file; the process exits after.
/// </summary>
public static class Smoke
{
    public static void Run(MainWindow window, string file, Action done)
    {
        var lines = new StringBuilder();
        int step = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) =>
        {
            try
            {
                if (step < window.Sections.Count)
                {
                    Section section = window.Sections[step];
                    window.ShowSection(section.Key);
                    bool ok = ReferenceEquals(window.Current, section) && section.View.IsVisible;
                    lines.Append(ok ? "PASS" : "FAIL").Append(" section ").Append(section.Key).Append(" opens\n");
                }
                else if (step == window.Sections.Count)
                {
                    window.ShowSection("rules");
                    foreach (string line in ((RulesSection)window.Current!).RoundTrip())
                    {
                        lines.Append(line).Append('\n');
                    }
                }
                else
                {
                    timer.Stop();
                    File.WriteAllText(file, lines.ToString());
                    window.Quitting = true;
                    done();
                    return;
                }
            }
            catch (Exception ex)
            {
                lines.Append("FAIL step ").Append(step).Append(": ").Append(ex.Message).Append('\n');
            }

            step++;
        };
        timer.Start();
    }
}
