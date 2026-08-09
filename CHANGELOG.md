# Changelog

What changed, for the people using this library. The reasoning behind each decision lives in
[docs/DESIGN.md](docs/DESIGN.md) instead — that file answers "why", this one answers "what".

Versions follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html). While the version is
`0.x`, the public API is still moving and a minor bump can break you.

## 0.1.0

The first public release. Everything below is new, so this entry describes what the library does
rather than what changed.

### Watching elements

- **`TriggerMonitor`** watches UI elements in other applications and raises an event when a
  condition is met. Monitoring is event driven — the monitor subscribes to the UI Automation events
  that can affect its triggers and never polls on its own initiative. **The flip side: a trigger
  fires only if the watched application raises the event**, and some applications never do.
- **`On` and the clauses are independent.** `ElementAppeared` / `ElementRemoved` / `PropertyChanged`
  / `WhileMatching` say *when* a trigger is evaluated; the clauses say *what must hold*. That lets
  you write "the element appeared **and** its value is X", which a single enumeration cannot.
- **One trigger can span several elements and several windows.** A clause carries its own
  `Window` / `Locator`, and clauses naming the same element share one resolution and one
  subscription. `Expression` combines the clauses with `&&`, `||`, `!` and parentheses when
  `Combine` alone is not enough; `Watch = false` narrows without being able to fire.
- **`NotifyOnStoppedMatching`** adds the falling edge to a `WhileMatching` trigger: the event is
  raised again, with `On = StoppedMatching`, when the condition stops holding. `StoppedMatching` is
  event-only — a definition cannot declare it.
- **`PollInterval` for applications that never report a change.** Setting an interval re-reads that
  trigger's already-resolved elements on that cadence. It is off by default and set per trigger;
  finding elements stays event driven either way. `MinInterval` rate-limits firing, for properties
  that change in bursts.
- **A trigger that loses its subscription is repaired.** Subscribing can fail and leave a trigger
  resolved but permanently silent; that state is recognised and re-subscribed, backing off rather
  than hammering the machine.
- **`GetDiagnostics()`** reports what the monitor is actually doing — how widely it is subscribed,
  how many elements resolved, how much polling costs. These are the numbers to read when a trigger
  never fires.
- **A consumer's exception cannot take the process down.** Anything thrown from an event handler —
  including from the `UnhandledException` handler itself — is caught and reported rather than
  escaping the automation thread.

### The trigger model and its file

- **A trigger is a plain POCO** (`TriggerDefinition`), so any serializer can round-trip it.
  `UiaTrigger.Serialization.TriggerJsonContext` is provided source-generated for hosts that want it,
  and `TriggerStore` reads and writes a file of them if you would rather not.
- **The trigger file has a JSON Schema.** `TriggerStore.Save` writes `triggers.schema.json` beside
  the file and stamps `$schema` into it, so an editor completes and validates the file with no
  per-user configuration. `TriggerJson.Schema` returns the same text for hosts that store triggers
  somewhere else. **The schema is generated from the model**, so it cannot drift from what the
  library actually reads.
- **Definitions are validated in one place, with a reason.** Out-of-range enum values, a null
  `Window.ProcessName`, a clause name containing a comma, a composite whose clause names collide, a
  negative `Debounce` — all are refused when the definition is added or read, rather than becoming a
  trigger that silently never matches.
- **Definitions are copied when you hand them over.** `AddAsync` / `StartAsync` and the picker's
  `TriggerCommitted` carry a copy, so editing the object afterwards does not change what is being
  monitored.
- **`StartAsync` can be called again after it failed** — a failure leaves the monitor exactly as it
  was, rather than half-started.
- **`TriggerComposer`** combines several triggers into one composite and takes it apart again,
  refusing an impossible combination with a reason instead of producing a definition that
  `TriggerMonitor` rejects later.

### Finding and recording elements

- **`UiaSession`** is the entry point for search, inspection and recording. All UI Automation work
  is funnelled onto a single dedicated MTA thread it owns, so recording, inspection and monitoring
  share one session.
- **Your process must be `PerMonitorV2` DPI aware.** In a DPI-unaware process Windows virtualises
  coordinates and a *different element* is returned, with no exception. `UiaSession.CoordinateProblem`
  says when that is the case, and `DpiAwareness.TryEnablePerMonitorV2()` exists for hosts that cannot
  ship a manifest.
- **`GetChildrenAsync`, `GetAncestorChainAsync` and `GetOverlapStackAsync` return `null` when the
  lookup fails**, and an empty list only when there genuinely is nothing — so a caller can tell
  "nothing there" from "could not ask".
- **An unresponsive application cannot hang the session indefinitely.**
  `UiaSessionOptions.TransactionTimeout` and `ConnectionTimeout` bound how long one call may hold up
  the shared thread; `GetSupportsTimeoutsAsync()` reports whether Windows is honouring them, because
  where it is not, UI Automation's own 20-second default applies instead.
- **A `UiaElement` is an opaque handle that you dispose.** No COM type appears on the public API.
  Its values are read in one cross-process call and are a snapshot; compare two elements with
  `AreSameAsync` rather than `==`, because UI Automation hands out a different object each time.
- **Point lookups ignore your own windows by default** (`SkipOwnProcessElements`), and windows
  cloaked by the Desktop Window Manager are skipped — they report visible bounds while nothing is on
  screen, so a definition could otherwise resolve against a window nobody can see.
- **Password values never reach a recorded definition or a fired event.** Redaction covers every
  read path, including the name of the step being recorded and reads of a `Custom` property.
- **Native AOT publishing is supported.** The UI Automation interop is hand-written
  `[GeneratedComInterface]` for that reason.

### The picker

- **`UiaTrigger.Picker.Core`** holds the behaviour exactly once, with a thin view per UI framework:
  `Picker.WinUI`, `Picker.Wpf` and `Picker.WinForms`. Reference whichever matches the framework your
  own application already uses.
- **Two windows.** The picker records one element and turns it into one condition; the list editor
  (`TriggerListEditorWindow` / `TriggerListEditorForm`) works on the whole list — add through the
  picker, edit, remove, combine several triggers into one, and take a combined trigger apart again.
- `TriggerDraftValidator` is public, so a third-party picker gets the same validation rules.
- **The panes are separated by splitters** — tree against properties, and properties against the
  condition fields — so a narrow window is still usable: you decide which side gives up the space.
  They move with the keyboard as well as the mouse, and the condition fields scroll rather than
  clip once you shrink them. The position is not persisted; the picker keeps no settings of its own.
  `Picker.WinUI` depends on `CommunityToolkit.WinUI.Controls.Sizers` for this, because WinUI 3 is
  the one framework of the three with no splitter of its own.
- **Enter does the job of the field you are in.** In the search box it finds the next match; in the
  editor's combine fields it combines; it commits only where committing is what the field is for.

### Localization

- Messages and IntelliSense ship in English, with Japanese satellites (`ja`). Strings used for
  comparison or persistence are invariant and are never localized, so a stored definition means the
  same thing on every machine.

### Known limitations

- **Windows only.** Every package targets `net10.0-windows`; the WinUI 3 one needs Windows 10 build
  19041 or later. All shipped assemblies are AnyCPU, so the packages put no architecture constraint
  on your application — but the sample hosts attached to a release are built **win-x64 only**, and
  ARM64 publishing of the *hosts* has not been verified.
- **An elevated application cannot be inspected** by a client that is not itself elevated. When that
  is why a trigger never resolves, `ResolutionChanged` says so.
- **A trigger fires only if the watched application raises the UI Automation event.** Verify a new
  trigger against the application you actually mean to watch rather than assuming that a property
  which changes on screen also reports that it changed; `PollInterval` is the fallback when it does
  not.
