namespace AkuWM.Tests;

/// <summary>The files under Fixtures/, copied next to the test assembly.</summary>
public static class Fixture
{
    public static string Path(string name) =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    public static string Read(string name) => File.ReadAllText(Path(name));

    /// <summary>The GlazeWM configuration this desk runs today.</summary>
    public const string GlazeConfig = "glazewm-config.yaml";

    /// <summary>The raise-or-launch table out of hyper-desktops.ahk.</summary>
    public const string AhkToggles = "hyper-desktops-excerpt.ahk";
}

/// <summary>A directory that cleans itself up, for the tests that write files.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "akuwm-test-" + Guid.NewGuid().ToString("n")[..12]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A test that leaves a file open is not a reason to fail the run.
        }
    }
}
