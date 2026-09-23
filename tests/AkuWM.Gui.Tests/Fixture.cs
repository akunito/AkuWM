using System.Text.Json.Nodes;
using AkuWM.Core.Config;
using AkuWM.Gui.Services;

namespace AkuWM.Gui.Tests;

/// <summary>A configuration directory of its own, a fake daemon, and the services the window takes.</summary>
public sealed class Fixture : IDisposable
{
    public Fixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "akuwm-gui-" + Guid.NewGuid().ToString("N"));
        string configDir = Path.Combine(Root, "akuwm");
        string runtimeDir = Path.Combine(Root, "state");
        Directory.CreateDirectory(configDir);
        Directory.CreateDirectory(runtimeDir);
        Paths = new ConfigPaths(configDir, "TESTBOX", runtimeDir);
        File.WriteAllText(Paths.CommonFile, Common);
        File.WriteAllText(Paths.ProfileFile, Profile);
        Daemon = new FakeDaemon();
        Services = new AppServices
        {
            Daemon = Daemon,
            Config = new ConfigService(Paths),
            Run = (_, _, _) => null,
            Start = command => { Started.Add(command); return null; },
        };
        Services.Toast = (text, error) => Toasts.Add((error ? "ERR " : string.Empty) + text);
    }

    public string Root { get; }

    public ConfigPaths Paths { get; }

    public FakeDaemon Daemon { get; }

    public AppServices Services { get; }

    public List<string> Started { get; } = [];

    public List<string> Toasts { get; } = [];

    public JsonObject CommonJson() => (JsonObject)JsonNode.Parse(File.ReadAllText(Paths.CommonFile))!;

    public JsonObject ProfileJson() => (JsonObject)JsonNode.Parse(File.ReadAllText(Paths.ProfileFile))!;

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    public const string Common = """
        {
          "version": 1,
          "monitors": [
            { "id": "main", "match": { "edid": "SAM7233" }, "primary": true },
            { "id": "second", "match": { "edid": "NSL2711" }, "orientation": "vertical" }
          ],
          "workspaces": [
            { "name": "11", "monitor": "main" },
            { "name": "12", "monitor": "main" },
            { "name": "21", "monitor": "second" }
          ],
          "rules": [
            { "id": "r-tg", "name": "Telegram", "enabled": true, "match": [ { "process": "Telegram" } ], "actions": [ "float", "sticky" ], "future_key": "kept", "updated_at": 1 },
            { "id": "r-zebar", "name": "Zebar", "enabled": true, "match": [ { "process": "zebar" } ], "actions": [ "ignore" ], "updated_at": 1 }
          ],
          "shortcuts": [
            { "id": "k-tg", "keys": "Hyper+L", "kind": "app", "app": "Telegram.exe", "command": "%APPDATA%\\Telegram Desktop\\Telegram.exe", "category": "Apps", "name": "Telegram", "enabled": true },
            { "id": "k-next", "keys": "Hyper+W", "kind": "wm", "command": "workspace next", "category": "Workspaces", "name": "next workspace", "enabled": true },
            { "id": "k-dup", "keys": "Hyper+Shift+W", "kind": "exec", "command": "notepad.exe", "category": "Other", "enabled": true }
          ],
          "startup": [
            { "id": "u-a", "name": "A", "command": "a.exe", "after": "now", "enabled": true },
            { "id": "u-b", "name": "B", "command": "b.exe", "after": "ipc", "delay_ms": 500, "enabled": true }
          ],
          "tools": [
            { "id": "t-disp", "name": "Display settings", "command": "ms-settings:display", "icon": "▭", "enabled": true }
          ],
          "apps": { "catalogue": "../winget-packages.json" }
        }
        """;

    public const string Profile = """
        {
          "version": 1,
          "rules": [
            { "id": "r-tg", "enabled": false, "notes": "off on this box" },
            { "id": "r-only", "name": "Only here", "match": [ { "class": "Local" } ], "actions": [ "float" ] }
          ]
        }
        """;
}
