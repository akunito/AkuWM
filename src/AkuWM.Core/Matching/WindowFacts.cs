namespace AkuWM.Core.Matching;

/// <summary>
/// The three things a rule can be matched on. A record, not the live window,
/// so the matcher is pure and the GUI's "test this rule" answers with exactly
/// the engine that will run it.
/// </summary>
public readonly record struct WindowFacts(string Process, string Class, string Title)
{
    public static readonly WindowFacts Empty = new(string.Empty, string.Empty, string.Empty);
}
