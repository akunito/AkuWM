namespace AkuWM.Core.Import;

/// <summary>What an import produced, for the CLI to print and a test to assert on.</summary>
public sealed class ImportSummary
{
    public int Monitors { get; set; }
    public int Workspaces { get; set; }
    public int Rules { get; set; }
    public int Shortcuts { get; set; }
    public int Startup { get; set; }

    /// <summary>Things the import decided or could not decide, one line each.</summary>
    public List<string> Notes { get; } = [];

    public override string ToString() =>
        $"{Rules} rules, {Workspaces} workspaces, {Shortcuts} shortcuts, " +
        $"{Monitors} monitor roles, {Startup} startup entries";
}
