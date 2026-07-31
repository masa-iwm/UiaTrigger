# Changelog

What changed, for the people using this library. The reasoning behind each decision lives in
[docs/DESIGN.md](docs/DESIGN.md) instead — that file answers "why", this one answers "what".

Versions follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html). While the version is
`0.x`, the public API is still moving and a minor bump can break you.

## 0.1.0-preview.1 — unreleased

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
