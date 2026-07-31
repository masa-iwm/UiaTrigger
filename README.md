# UiaTrigger

*[日本語版 README](README.ja.md)*

A .NET library that watches other applications' UI elements for appearance, removal and property
changes through UI Automation, and raises an event when a condition is met.

- C# / .NET 10 / WinUI 3 (Windows App SDK 2.3) / **Native AOT publishing supported**
- COM interop: hand-written `[GeneratedComInterface]` (UIA) + CsWin32 (Win32, picker layer)
- All UIA access is funnelled onto a single dedicated MTA thread owned by `UiaSession`, so recording,
  inspection and monitoring can share one session
- Monitoring is event driven by default (PropertyChanged / StructureChanged / WindowOpened・Closed).
  **The library never polls on its own initiative.**
  **The flip side: a trigger fires only if the watched application raises the UI Automation event.**
  Some applications never raise `PropertyChanged` for a given property, and nothing this library does
  can make them — so verify a new trigger against the application you actually mean to watch, rather
  than assuming a property that changes on screen also reports that it changed
- **When an application turns out to be one of those, set `TriggerDefinition.PollInterval`** and that
  trigger's already-resolved elements are re-read on that cadence. It is off by default, it is set per
  trigger rather than globally, and it costs one cross-process read per element per round —
  `TriggerMonitorDiagnostics.PollCount` and `PolledReadCount` report what it costs. Finding elements
  stays event driven either way; polling only re-reads ones already found

## Layout

| Project | Contents |
|---|---|
| `UiaTrigger.Core` | UI-independent class library. `UiaSession` (element search and recording), the model (POCOs), beam-search element resolution, `TriggerMonitor`, JSON context |
| `UiaTrigger.Picker.Core` | UI-independent picker proper. `TriggerPickerPresenter` and `TriggerListEditorPresenter` (behaviour), the overlay, and the seams to the views (`IPickerView` and friends) |
| `UiaTrigger.Picker.WinUI` | The WinUI 3 views: `TriggerPickerWindow` and `TriggerListEditorWindow`. Hold no behaviour. Carries two dependencies the other views do not — see below |
| `UiaTrigger.Picker.Wpf` | The WPF views: `TriggerPickerWindow` and `TriggerListEditorWindow`. Hold no behaviour |
| `UiaTrigger.Picker.WinForms` | The Windows Forms views: `TriggerPickerForm` and `TriggerListEditorForm`, plus `TriggerListEditor` for a `PropertyGrid`. Hold no behaviour |
| `UiaTrigger.App.WinUI` | Sample host (WinUI 3). Launches the picker and saves `List<TriggerDefinition>` as JSON |
| `UiaTrigger.App.Wpf` | Sample host (WPF). Same |
| `UiaTrigger.App.WinForms` | Sample host (Windows Forms). Same |
| `UiaTrigger.TestHost` | Console app for exercising the library. `record` / `monitor` commands |

### What each package pulls in

| Package | Depends on |
|---|---|
| `UiaTrigger.Core` | `Microsoft.Extensions.Logging.Abstractions` |
| `UiaTrigger.Picker.Core` | `UiaTrigger.Core` |
| `UiaTrigger.Picker.Wpf` / `.WinForms` | `UiaTrigger.Picker.Core` |
| `UiaTrigger.Picker.WinUI` | `UiaTrigger.Picker.Core`, `Microsoft.WindowsAppSDK`, `CommunityToolkit.WinUI.Controls.Sizers` |

`Microsoft.Extensions.Logging.Abstractions` reaches every package, because the library logs through
`ILogger` and brings no logging implementation of its own. `CommunityToolkit.WinUI.Controls.Sizers` is
the only dependency from outside Microsoft, and only the WinUI 3 view needs it: it supplies the splitter
that WPF and Windows Forms have built in and WinUI 3 does not.

### Two windows: the picker and the list editor

The picker records **one element** and turns it into **one condition**. The list editor is the other half:
it works on the whole list — add (through the picker), edit an existing condition, remove, combine several
triggers into one, and take a combined trigger apart again.

```csharp
// Hand it the list you have; get the edited list back, or null when the user cancelled.
IReadOnlyList<TriggerDefinition>? edited = await TriggerListEditorWindow.EditAsync(owner, triggers);
if (edited is not null)
{
    TriggerStore.Save(path, edited);   // where they live stays your business
}
```

**It works on copies.** The list you pass in is never touched, and what comes back is a fresh copy, so
cancelling leaves you exactly where you were — even after triggers were recorded and removed inside the
dialog. The editor does not know where triggers are stored and never starts or stops a monitor.

The signature is asynchronous in all three variants because **WinUI 3 has no window-modal dialog**: its
editor is modeless and completes when the window closes, so keep the user from opening a second one while
it is up. WPF and Windows Forms show a real modal dialog, so their task is already complete when it
returns.

Windows Forms additionally gets `TriggerListEditor`, a `UITypeEditor`, so a list of triggers can be edited
straight from a `PropertyGrid`:

```csharp
[Editor(typeof(TriggerListEditor), typeof(UITypeEditor))]
public List<TriggerDefinition> Triggers { get; set; } = [];
```

**What the picker can and cannot edit.** `TriggerPickerPresenter.CanEdit` answers that — ask before you
offer to edit. It edits one plain condition, so it declines a combined trigger (take it apart first) and
anything carrying what a condition draft has no place for: an element of its own, `Watch` turned off, or a
custom property id. Committing rebuilds the condition from the draft, so those would otherwise be dropped
silently.

### Why there are three view variants

The behaviour (`TriggerPickerPresenter`, `TriggerListEditorPresenter`) exists exactly once, in
`UiaTrigger.Picker.Core`. Each view is a thin layer that holds only what is genuinely specific to its
framework. Reference whichever `Picker.*` matches the UI framework your own app already uses.

**Two asymmetries are deliberate:**

- **Only the `App.WinUI` sample host doubles as a showcase** (picker → monitoring, end to end).
  Triplicating that gains nothing, so `App.Wpf` and `App.WinForms` stop at recording triggers and
  saving to JSON. The WinUI host also keeps its own "combine" bar, which is what drives the
  end-to-end test of a combined trigger firing
- **Only the Windows Forms view has no confirm button inside each row** (it is replaced by a single
  "confirm the selected row" button). The Windows Forms `TreeView` cannot host arbitrary controls in a
  row; this is the one place where the UI shape differs between the three variants

The keys for user-facing strings are collected in two tables — `PickerStringKeys` and `EditorStringKeys`,
one per window — and both are supplied through the same two routes (WinUI uses `.resw` + MRT Core; WPF and
Windows Forms share the `.resx` in `Picker.Core`).

### Design notes

- **The persistence model is `TriggerDefinition`** (it carries its own `Id` key). The file format is still
  the host's business, but the library *provides* `UiaTrigger.Serialization.TriggerJsonContext`
  (source-generated), so a host only has to add it to `TypeInfoResolverChain`. If you want triggers alone
  in a single file, `UiaTrigger.Persistence.TriggerStore` works as-is. Default path:
  `%LOCALAPPDATA%\UiaTrigger\triggers.json`
- **The shape of a trigger**: `On` (`ElementAppeared` / `ElementRemoved` / `PropertyChanged` /
  `WhileMatching`) and `Clauses` (a list of per-property predicates, combined with `Combine` = `All` or
  `Any`) are independent. That lets you write "the element appeared **and** Value is X". `MinInterval`
  rate-limits firing
- **One trigger can span several elements, and several windows**. A clause names its own `Window` and
  `Locator`, or leaves both null to use the trigger's — so "the button in A is enabled **and** the label
  in B says Done" is one trigger. Clauses that name the same element share one resolution and one
  subscription. `Expression` combines the clauses by name with `&&`, `||`, `!` and parentheses when a
  single `Combine` cannot say it: `(ready || idle) && !busy`. **It is text rather than a tree of
  objects on purpose** — the model stays a plain POCO that any serializer can round-trip
- **`Clause.Watch`** decides whether a change to that clause's property can *fire* the trigger, or
  whether the clause only *narrows* it. This is what expresses "element A exists **and** B's value
  changed": subscriptions are derived from the property, never from the operator, so a clause meant to
  require an element would otherwise fire on every change to it
- **The fired event** `TriggerFired`: `TriggerId` / `On` / `OldValue` / `NewValue` (all
  `ComparisonString`) / `Properties` (a snapshot of **every observable** property, not only the
  watched ones — one round trip reads them all) / `Timestamp`.
  **Those values describe one element: the one the first clause reads.** For a trigger spanning several
  elements the clause that *changed* may be a different one, so what fired the trigger and what the
  event reports are separate questions; the three are always consistent with each other.
  **`Clauses` is where the rest becomes readable** — one `ClauseReading` per clause, each with the
  value read from its own element and an outcome of `Matched` / `NotMatched` / `Unreadable` /
  `NotEvaluated`. The last is distinct on purpose: expressions short-circuit, so a clause on the
  unevaluated side was never looked at, which is not the same as "did not hold".
  Deliveries happen **one at a time, in detection order**, on a single worker; exceptions from your
  handler go to `UnhandledException`
- **Element identification** is not "sum of scores against a threshold" but **required predicates +
  ranking + beam search**. Top-level windows are matched with a per-attribute `MatchStrength`
  (`Required` / `Preferred` / `Ignored`), and only `Required` attributes can eliminate a candidate.
  Below that, each level scores ControlType / AutomationId / Name / ClassName by "how strongly the
  recorded attribute affirms this candidate", keeping the top K so the search can back out of dead ends.
  Sibling index is only a tie-break (tunable via `ResolverOptions`)
- **`UiaSession` is the public seam**: one session = one MTA thread + one `IUIAutomation`. Getting an
  element from a point (`ElementFromPointAsync`), enumerating children, ancestor chains, the overlap
  stack, snapshots and recording a definition all live there, and `CreateMonitor()` puts monitoring on
  that same thread. COM types are sealed behind an opaque handle called `UiaElement`, so a third party
  can write their own picker or inspector against the same API — `UiaTrigger.Picker.Core` in this
  repository is exactly that. Call time limits, the clock (`TimeProvider`) and logging (`ILogger`) are
  collected in `UiaSessionOptions`
- **Triggers can be added and removed while running**: `AddAsync` / `RemoveAsync` re-wire only the
  subscriptions belonging to that trigger. `TriggerMonitor.GetDiagnostics()` reports subscription count,
  events received and how many resolved
- **Coordinate assumption**: every API that deals in coordinates assumes the host declares
  `PerMonitorV2`. In a DPI-unaware process Windows virtualises coordinates and **a different element is
  returned, without any exception**. `UiaSession.CoordinateProblem` tells you why, and hosts should
  surface it as a warning. (`DpiAwareness.TryEnablePerMonitorV2()` exists for hosts that cannot ship a
  manifest.)
- **Interop background**: CsWin32 0.3.x generates COM as `[ComImport]`, which rules out AOT, so the UIA
  COM interfaces are hand-written as `[GeneratedComInterface]` following the vtable order in
  UIAutomationClient.h. That approach requires `DisableRuntimeMarshalling`, which is incompatible with
  the `SetLastError=true` DllImports CsWin32 generates, so Win32 functions inside Core are hand-written
  `LibraryImport`. The picker (a separate assembly) does use CsWin32 (`allowMarshaling:false`)

## Getting started

```powershell
# Build (needs the .NET 10 SDK; the Windows App SDK comes back via NuGet restore)
dotnet build

# 1. Create a trigger definition
#   a) GUI: start a sample host, then [Add with picker] (any of the three variants will do)
dotnet run --project src/UiaTrigger.App.WinUI
dotnet run --project src/UiaTrigger.App.Wpf
dotnet run --project src/UiaTrigger.App.WinForms
#   b) CLI: record the element under the cursor (captured after 3 seconds)
dotnet run --project src/UiaTrigger.TestHost -- record my-trigger --on PropertyChanged --prop Name --op Always

# 2. Monitor (logs a firing when the condition is met)
dotnet run --project src/UiaTrigger.TestHost -- monitor

# Native AOT publish (needs the VS C++ toolchain; vswhere may have to be on PATH)
dotnet publish src/UiaTrigger.TestHost -c Release -r win-x64 -o publish/TestHost
dotnet publish src/UiaTrigger.App.WinUI -c Release -o publish/App
```

### Using the picker

- **Automatic selection on**: hold the cursor still for one second to capture the element and show a
  red frame overlay (click-through)
- Click **the ✓ icon at the top-right of the frame**, or **the ✓ on a tree row**, to confirm the element
- **←/→**: step through elements stacked at the same point (other windows and other processes included;
  ← goes down, → goes up)
- Tree: shows process → the direct chain down to the element. Clicking or expanding a row enumerates all
  of its children (fetched lazily). Supports switching the hierarchy view (Raw / Control / Content) and
  searching the nodes fetched so far
- Once confirmed, set the firing trigger, property, condition, minimum interval and Id, then
  "Add trigger" — the host saves it as JSON

### Firing triggers (`TriggerOn`) and conditions (`ComparisonOp`)

There are four firing triggers:

| `On` | Fires when |
|---|---|
| `ElementAppeared` | The target element resolves (and satisfies the clauses, if any) |
| `ElementRemoved` | The target element disappears, or is replaced by something else |
| `PropertyChanged` | Every time a watched property changes and the clauses hold |
| `WhileMatching` | Only at the moment the clauses start holding (rising edge) |

Comparison operators for a clause (`PropertyClause`):

- `Always` — ignores the value (for when you only want to subscribe to the property)
- Numeric: `Between` / `NotBetween` / `GreaterThan` / `LessThan` / `LessOrEqual` / `GreaterOrEqual`.
  `Tolerance` sets the width of the band treated as equal (for doubles such as `RangeValue`)
- String: `Equals` / `NotEquals` / `RegexMatch` / `RegexNotMatch` (NonBacktracking, with a timeout).
  Compared values are always `InvariantCulture` / `Ordinal`. `bool` is `true` / `false`, case-insensitive
- `Property = Custom` plus `CustomPropertyId` targets UIA properties that are not in the enum

## Using it as a library

```csharp
// Record a trigger definition from the element at a point, and monitor it in the same session
await using var session = new UiaSession();
if (session.CoordinateProblem is { } problem)
{
    Console.Error.WriteLine(problem);   // the host is not PerMonitorV2
}

TriggerDefinition definition = await session.BuildDefinitionFromCursorAsync();
definition.Id = "watch-me";
definition.Clauses.Add(new PropertyClause { Property = TriggerProperty.Name, Op = ComparisonOp.Always });

await using TriggerMonitor monitor = session.CreateMonitor();
await monitor.StartAsync([definition]);
```

```csharp
await using var monitor = new TriggerMonitor();
monitor.TriggerFired += (_, e) =>
    Console.WriteLine($"[{e.TriggerId}] {e.On}: {e.OldValue} -> {e.NewValue} (Name={e.Properties?.Name})");
await monitor.StartAsync(triggers); // IEnumerable<TriggerDefinition>
```

```csharp
// "Once the progress bar reaches 100%, exactly once"
var trigger = new TriggerDefinition
{
    Id = "download-done",
    Window = new WindowIdentity { ProcessName = "myapp.exe" },
    Locator = locator,                       // recorded by the picker or UiaSession.BuildDefinitionAsync
    On = TriggerOn.WhileMatching,
    Clauses = [new PropertyClause
    {
        Property = TriggerProperty.RangeValue,
        Op = ComparisonOp.GreaterOrEqual,
        Value = 100,
        Tolerance = 0.001,                   // never trust exact comparison of doubles
    }],
    MinInterval = TimeSpan.FromSeconds(1),
};
```

## Known caveats

- For UWP-style apps the top level becomes `ApplicationFrameHost.exe` (recording and resolution follow
  the same rule, so they do still match)
- `WhileMatching` is edge triggered (fires on false→true, does not re-fire while it stays true).
  Whether it fires when the condition already holds at startup is controlled by
  `TriggerMonitorOptions.FireOnInitialMatch` (default true)
- For password fields (`IsPassword`), `Value` and `Name` are suppressed in snapshots
- Within one window, the Z order used for overlap stepping is approximated from the Raw-view hit-test
  chain plus document order
- While selection mode is active a low-level hook watches ←/→, but the keys themselves are passed
  through to other applications (they are not swallowed)

## Documentation

| Document | Contents |
|---|---|
| [CHANGELOG.md](https://github.com/masa-iwm/UiaTrigger/blob/main/CHANGELOG.md) | What changed in each version, for the people using the library |
| [docs/DESIGN.md](https://github.com/masa-iwm/UiaTrigger/blob/main/docs/DESIGN.md) | The design: architecture, invariants, and the ledger of design decisions with their reasons |
| [docs/TESTING.md](https://github.com/masa-iwm/UiaTrigger/blob/main/docs/TESTING.md) | The test layers (T1–T6), cross-cutting rules, the synthetic-input policy, and the verification philosophy behind them |
| [docs/LOCALIZATION.md](https://github.com/masa-iwm/UiaTrigger/blob/main/docs/LOCALIZATION.md) | Localization: policy, string classification, supply routes, XML documentation, and what ships in the packages |
| [docs/RELEASING.md](https://github.com/masa-iwm/UiaTrigger/blob/main/docs/RELEASING.md) | Package layout, CI structure, the release procedure, and the ledger of packaging traps |
| [docs/MANUAL-CHECKS.md](https://github.com/masa-iwm/UiaTrigger/blob/main/docs/MANUAL-CHECKS.md) | Checklist for the checks that cannot be automated |

> The five documents under `docs/` are **maintainer-facing and written in Japanese**, and stay that
> way by design. This README, `README.ja.md` and the changelog are the user-facing ones.

**Releases** attach the three sample hosts and the console tool as ready-to-run zips (win-x64), so
you can try the picker without building anything. The library itself comes from NuGet.

> **Note**: while the version is `0.x`, the public API and the `triggers.json` format are
> **not yet stable** — a minor bump can change them in breaking ways, and there is no migration
> code for old files. See the
> [CHANGELOG](https://github.com/masa-iwm/UiaTrigger/blob/main/CHANGELOG.md) for what changed.

## Development

```powershell
dotnet build UiaTrigger.slnx -c Release   # a single warning fails the build
dotnet test  UiaTrigger.slnx -c Release   # build first (the manifest checks read build output)
```

- User-visible strings must go through `.resx`. The primary language is **en-US**; Japanese lives in
  `Strings.ja.resx`
- Strings used for comparison or persistence must **always** be `InvariantCulture` / `Ordinal`
  ([docs/LOCALIZATION.md](https://github.com/masa-iwm/UiaTrigger/blob/main/docs/LOCALIZATION.md) §3)
- **Do not use synthetic input (`SendInput` and friends) in tests outside
  `tests/UiaTrigger.Input.Tests`** — CI detects it and fails the build. The reasoning is in
  [docs/TESTING.md](https://github.com/masa-iwm/UiaTrigger/blob/main/docs/TESTING.md) §4, and the
  policy for the one permitted project in its §3

---

**This English README is the source; `README.ja.md` is its translation.** When they disagree, this file
wins, and `README.ja.md` should be updated to match.
