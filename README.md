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

Download `CaretLangIndicator.exe` and `install.ps1` from the
[latest release](../../releases/latest), then:

```powershell
.\install.ps1
```

It copies the exe to `%LOCALAPPDATA%\CaretLangIndicator`, registers it in
Startup and launches it. No admin rights, no services, nothing written outside
your own profile.

```powershell
.\install.ps1 -Arguments '-OnlyWhenCaps'   # pass your own options
.\install.ps1 -NoAutostart                 # install without adding to Startup
.\uninstall.ps1                            # remove everything
```

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
| `-Glass` | — | off | Windows 11 acrylic instead of the flat dark pill |
| `-Interval` | ms | `120` | polling interval |

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
