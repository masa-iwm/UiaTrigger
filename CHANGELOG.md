# Changelog

What changed, for the people using this library. The reasoning behind each decision lives in
[docs/DESIGN.md](docs/DESIGN.md) instead — that file answers "why", this one answers "what".

Versions follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html). While the version is
`0.x`, the public API is still moving and a minor bump can break you.

## 0.1.0-preview.3

### Added

- **The picker can now set `PollInterval`.** A "Poll interval (s)" field sits next to the
  minimum-interval field. It only appears for the lifecycles that can poll (`PropertyChanged` /
  `WhileMatching`) — the monitor rejects polling for `ElementAppeared` / `ElementRemoved`, so the
  field hides rather than letting you type a value that would fail on commit.
- **The picker can now edit the trigger's display name.** The recorded name is suggested on
  confirm, exactly like the id: typing your own keeps it, and re-confirming another element only
  replaces a suggestion you did not touch. Leaving the field blank keeps the recorded name.

### Changed

- **The picker windows use a two-band layout.** The element tree and the property list now sit
  side by side in the upper band, and the condition fields span the full window width below them.
  The condition rows are the widest content in the window, so they get the width.

### Breaking (for `IPickerView` implementers and draft consumers)

- `OperandVisibility` gained a `PollInterval` flag; the positional constructor changed.
- `IPickerView` gained `DisplayNameText { get; set; }`.
- `TriggerDraft` gained `PollIntervalSeconds` and `DisplayName`; `TriggerDraftResult` gained a
  `PollInterval` positional parameter.
- `TriggerDraftValidator.Apply` now writes the definition's `PollInterval` from the draft — an
  empty field clears it, the same meaning the minimum interval already had. A draft that does not
  set `PollIntervalSeconds` therefore clears a recorded poll interval on commit; the picker itself
  round-trips the value through its new field, so nothing is lost when editing there.

## 0.1.0-preview.2

### Fixed

- **The WinUI 3 windows no longer open at whatever size Windows chooses.** They now open at the
  same default size as the WPF and Windows Forms ones — picker 1100x700, trigger-list editor
  900x560 — scaled by the display scale. On a high-resolution display the WinUI picker used to
  fill most of the screen, so the same picker looked like a different tool depending on which
  package you referenced.
- **A single missing resource key no longer takes the picker down.**
  `MrtPickerStrings.GetString` is documented to return the key itself when a string cannot be
  found, but MRT Core throws for a key that is not there — unlike the UWP API of the same name,
  which returned an empty string. It now keeps that promise.

### IntelliSense

- **Japanese IntelliSense ships in the packages** (`lib/<TFM>/ja/<name>.xml`), next to the English
  documentation, for all five packages.
- **`UiaTrigger.Picker.WinUI` ships XML documentation at all** — it was the one package without
  any, so its members had no IntelliSense in either language.
- **Emphasis in the documentation is no longer shown as raw markup.** XML documentation has no
  element for emphasis, so the `<b>` tags that had crept in were displayed literally.
- `ElementRect.Contains` was absent from the Japanese documentation, which meant it had no
  description at all there rather than falling back to English.

### Documentation

- The dependency table in the README now says what the packages actually pull in.
  `Microsoft.Extensions.Logging.Abstractions` reaches all five through `UiaTrigger.Core`.

## 0.1.0-preview.1

The first published version. Everything below is new, so this entry describes what the library
does rather than what changed.

### The library

- **`TriggerMonitor`** watches UI elements in other applications and raises an event when a
  condition is met. Monitoring is event driven — the monitor subscribes to the UI Automation events
  that can affect its triggers and never polls on its own initiative.
- **A trigger is a plain POCO** (`TriggerDefinition`), so any serializer can round-trip it.
  `UiaTrigger.Serialization.TriggerJsonContext` is provided source-generated for hosts that want it,
  and `TriggerStore` reads and writes a file of them if you would rather not.
- **`On` and the clauses are independent.** `ElementAppeared` / `ElementRemoved` / `PropertyChanged`
  / `WhileMatching` say *when* a trigger is evaluated; the clauses say *what must hold*. That lets
  you write "the element appeared **and** its value is X", which a single enumeration cannot.
- **One trigger can span several elements and several windows.** A clause carries its own
  `Window` / `Locator`, and clauses naming the same element share one resolution and one
  subscription. `Expression` combines the clauses with `&&`, `||`, `!` and parentheses when
  `Combine` alone is not enough.
- **`PollInterval` for applications that never report a change.** A trigger fires only if the
  watched application raises the UI Automation event, and some applications never do. Setting an
  interval re-reads that trigger's already-resolved elements on that cadence. It is off by default
  and set per trigger; finding elements stays event driven either way.
- **`MinInterval`** rate-limits firing, for properties that change in bursts.
- **`GetDiagnostics()`** reports what the monitor is actually doing — how widely it is subscribed,
  how many elements resolved, how much polling costs. These are the numbers to read when a trigger
  never fires.
- **Native AOT publishing is supported**, and all UI Automation work is funnelled onto a single
  dedicated MTA thread owned by `UiaSession`, so recording, inspection and monitoring can share one.

### The picker

- **`UiaTrigger.Picker.Core`** holds the behaviour exactly once, with a thin view per UI framework:
  `Picker.WinUI`, `Picker.Wpf` and `Picker.WinForms`. Reference whichever matches the framework your
  own application already uses.
- `TriggerDraftValidator` is public, so a third-party picker gets the same validation rules.
- **The panes are separated by splitters** — tree against properties, and properties against the
  condition fields — so a narrow window is still usable: you decide which side gives up the space.
  They move with the keyboard as well as the mouse, and the condition fields scroll rather than
  clip once you shrink them. The position is not persisted; the picker keeps no settings of its own.
  `Picker.WinUI` depends on `CommunityToolkit.WinUI.Controls.Sizers` for this, because WinUI 3 is
  the one framework of the three with no splitter of its own.

### Localization

- Messages and IntelliSense ship in English, with Japanese satellites (`ja`).

### Known limitations

- **Windows only**, and **win-x64 only** for the sample hosts attached to a release. The packages
  themselves are AnyCPU, so your own application is not constrained; ARM64 publishing of the *hosts*
  has not been verified.
- **An elevated application cannot be inspected** by a client that is not itself elevated. When that
  is why a trigger never resolves, `ResolutionChanged` says so.
