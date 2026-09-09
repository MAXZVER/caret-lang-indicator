# Caret Language Indicator

**English** · [Русский](README.ru.md)

A small badge that shows the current keyboard layout and Caps Lock state right
next to the text caret, the way macOS shows its input source.

![layout badge](docs/state-ru.png)

![caps lock](docs/state-caps.png)

*Illustrations are rendered by [`docs/make-illustration.ps1`](docs/make-illustration.ps1),
not photographed from a desktop — same shapes and colours the tool draws.*

Windows has no such indicator. PowerToys does not have one either; the feature
requests are still open. The language bar sits in the taskbar, far from where
you are actually typing, so you notice the wrong layout only after a line of
gibberish.

## Why another one

Existing layout indicators locate the caret through the classic Win32 API,
`GetGUIThreadInfo`. That works in Notepad, Office and native controls — and
returns nothing at all in Chromium and Electron apps, which draw their own
caret. Chrome, VS Code, Telegram, Discord, Slack: precisely where most typing
happens today.

This one falls back to UI Automation and reads the caret from
`TextPattern.GetSelection().GetBoundingRectangles()`, which Chromium does
report. Three levels, in order of precision:

1. `GetGUIThreadInfo` — the real Win32 caret.
2. `TextPattern` selection rectangle — Chromium, Electron, UWP.
3. Bounding box of the focused text control — anything else; the badge then
   sits beside the field rather than at the cursor.

## Install

Download `CaretLangIndicator.exe` from the
[latest release](../../releases/latest) and double-click it. That is the whole
procedure — one file, no scripts, no installer bundle.

It asks whether to install. Say yes and it copies itself to
`%LOCALAPPDATA%\CaretLangIndicator` and starts with Windows from then on. Say
no and it just runs from where it is, changing nothing.

No admin rights, no service, nothing written outside your own profile.

```powershell
CaretLangIndicator.exe -Install -OnlyWhenCaps   # install silently with options
CaretLangIndicator.exe -Uninstall               # remove it again
CaretLangIndicator.exe -NoPrompt                # run once, never ask
```

Options given to `-Install` are stored in the Startup shortcut, so they survive
reboots.

The exe is unsigned, so SmartScreen will warn on first run — "More info", then
"Run anyway". Or build it yourself, which takes one command.

## Options

| Flag | Values | Default | Meaning |
|---|---|---|---|
| `-Anchor` | `Caret` `Field` `Corner` | `Caret` | what the badge is pinned to |
| `-VAlign` | `Below` `Center` | `Below` | under the caret, or level with the line |
| `-Side` | `Left` `Right` | `Left` | which side of the field, when pinned to a field |
| `-OffsetX` | pixels | `8` | gap from the caret |
| `-OffsetY` | pixels | `2` | vertical nudge |
| `-FieldGap` | pixels | `12` | gap from the edge of a field |
| `-OnlyWhenCaps` | — | off | show only while Caps Lock is on |
| `-OnlyOnChange` | — | off | stay hidden, appear for a moment when the layout changes |
| `-ShowMs` | ms | `1200` | how long it stays visible in that mode |
| `-Switcher` | — | off | show every installed layout in a row with a sliding selection, like the macOS input source HUD |
| `-Glass` | — | off | Windows 11 acrylic instead of the flat dark pill |
| `-Interval` | ms | `120` | polling interval (`15` when `-OnlyOnChange` is on) |

The macOS-like setup is `-OnlyOnChange -Switcher`: nothing on screen while you
type, and a HUD with the selection sliding to the new layout at the moment you
switch. It also costs almost nothing at rest, since UI Automation is only
touched when the layout actually changes.

`-Anchor Corner` parks the badge in a fixed screen corner, where it never
covers anything. `-OnlyWhenCaps` gives the macOS arrangement: layout in a menu
bar somewhere, the badge only for Caps Lock.

## Build

Nothing to install — the C# compiler ships with Windows:

```powershell
.\build.ps1
```

It compiles `src/Indicator.cs` with `csc.exe` from
`C:\Windows\Microsoft.NET\Framework64\v4.0.30319` and writes a 17 KB exe.
Roughly 80 MB of working set and well under 1% of one core at rest.

## The PowerShell version

`caret-lang-indicator.ps1` is the same thing as a script — slower and heavier
(about 165 MB and noticeably more CPU), but it has two switches that help when
placement misbehaves in some app:

```powershell
.\caret-lang-indicator.ps1 -Diagnose          # prints what it detects for 10 s
.\caret-lang-indicator.ps1 -Log detect.log    # logs detection while you work
```

The log names which of the three levels answered (`caret`, `textpattern`,
`field`) and for which application.

## Known limits

- Some apps expose neither a caret nor a sane focused element. There the badge
  falls back to the field rectangle, and if that is missing too, to nothing —
  use `-Fallback Mouse` or `-Anchor Corner`.
- An app with buttons hugging its input box (Telegram) will have the badge land
  near them. No position next to a field is safe in every application;
  `-Anchor Corner` is the way out.
- Positions are taken in physical pixels; on a mixed-DPI multi-monitor setup
  expect drift.

## License

MIT.
