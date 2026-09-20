; Excerpt of templates/windows/DESK_W11/hyper-desktops.ahk: the raise-or-launch
; table, which is the only part AkuWM imports. The decoy lines around it are
; there so the importer has to pick the table out rather than take every line.
^!#q:: WorkspacePrev()
^!#+t:: SomethingElse("not a toggle")
^!#t:: AppToggle("WindowsTerminal.exe", "wt.exe")           ; sway: kitty
^!#r:: AppToggle("alacritty.exe", A_ProgramFiles "\Alacritty\alacritty.exe")   ; sway: Alacritty
^!#z:: AppToggle("zen.exe", A_ProgramFiles "\Zen Browser\zen.exe")
^!#v:: AppToggle("vivaldi.exe", EnvGet("LOCALAPPDATA") "\Vivaldi\Application\vivaldi.exe")
^!#l:: AppToggle("Telegram.exe", A_AppData "\Telegram Desktop\Telegram.exe")
^!#d:: AppToggle("Obsidian.exe", EnvGet("LOCALAPPDATA") "\Programs\Obsidian\Obsidian.exe")
^!#c:: AppToggle("Code.exe", "code")
^!#p:: AppToggle("Bitwarden.exe", EnvGet("LOCALAPPDATA") "\Programs\Bitwarden\Bitwarden.exe")
^!#o:: AppToggle("Element.exe", EnvGet("LOCALAPPDATA") "\element-desktop\Element.exe")
^!#y:: AppToggle("Spotify.exe", A_AppData "\Spotify\Spotify.exe")
^!#x:: AppToggle("CalculatorApp.exe", "calc")
^!#e:: AppToggle("explorer.exe", "explorer")
^!#u:: AppToggle("dbeaver.exe", EnvGet("LOCALAPPDATA") "\DBeaver\dbeaver.exe")
^!#f:: Glaze("command toggle-fullscreen")
