# Changelog

What changed, for the people using this library. The reasoning behind each decision lives in
[docs/DESIGN.md](docs/DESIGN.md) instead — that file answers "why", this one answers "what".

Versions follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html). While the version is
`0.x`, the public API is still moving and a minor bump can break you.

## 0.1.0-preview.6

### Fixed

- **Enter in the picker's search box no longer commits and closes the window.** A picker opened to
  edit a recorded trigger puts "commit" on Enter, and that was beating the search box's own use of
  it: on WPF and WinUI the tree was searched *and* the window closed, and on Windows Forms the
  search never ran at all — a single-line text box hands Enter to the dialog before its `KeyDown`
  handler ever sees it. Enter now finds the next match and stops there, whichever button is the
  default.
- **Enter in the trigger-list editor's combine fields now combines instead of accepting.** The
  expression, the "only narrow" list and the poll interval take effect when you press *Combine the
  selected* / *Update the composite*; sending Enter to OK discarded whatever was half-typed and
  closed the dialog. Enter in those three fields now does what the button does. Enter anywhere else
  in the editor — the list, OK, Cancel — still means OK.
- **A composite whose clause names collide is now refused when it is built,** not when monitoring
  starts. `TriggerComposer.Compose` skipped the clause-name check whenever the expression was blank
  ("require them all"), so a colliding pair could be saved to the trigger file and only fail later,
  from `TriggerMonitor.AddAsync`. Names are checked either way now. (Collisions are reachable: a
  trigger whose id cannot be a clause name contributes a positional name, `c1`/`c2`, which another
  trigger may already be called.)
- **A regular expression that could not be evaluated no longer satisfies `RegexNotMatch`.** A
  pattern that ran out of its time limit was treated as "did not match", which the negated operator
  then read as a match — so the trigger fired every time the value could not be tested. Both
  directions now treat "could not evaluate" as not satisfied, matching what an unsupported property
  already did.
- **`TriggerFiredEventArgs.Clauses` no longer reports a clause as unevaluated** when a definition
  happens to hold the same `PropertyClause` instance in two positions.
- **The overlay no longer leaks its low-level keyboard hook** when its windows could not be created.
  Disposing the picker in that state never reached the overlay thread, which then kept the
  desktop-wide hook until the process ended.

## 0.1.0-preview.5

### Added

- **A composite can now be changed without taking it apart.** Select exactly one composite in the
  trigger-list editor and the fields under the list fill in from it — the expression, the clauses
  that only narrow, the poll interval and the new "Also notify when it stops matching" checkbox —
  while "Combine the selected" becomes **Update the composite**. Pressing it rewrites those four
  things on that trigger *in place*: its id, its clauses' elements and comparisons, and its
  position in the list all stay put, and it stays selected afterwards so you can change it again.
  `TriggerComposer.Update` carries the rule and `TriggerComposer.UnwatchedNames` reads the
  narrowing clauses back out, so a host with its own combine UI gets both.

  The fields follow the selection: choosing anything else empties them, so what is in them always
  describes what pressing the button would do. **Fill them in after choosing the rows** — choosing
  rows clears what is there.

  One asymmetry to know about: `Update` matches `unwatchedNames` against **clause names**
  (`login-1`), where `Compose` matches **source trigger ids** (`login`). A composite no longer
  records which source a clause came from. `UnwatchedNames` hands back exactly the names `Update`
  expects, so reading a composite's settings and pressing Update without editing changes nothing.
- **Combining can now set `NotifyOnStoppedMatching`.** A composite always fires on
  `WhileMatching`, which is the one lifecycle that flag applies to — but nothing in any UI could
  reach it: `Compose` never set it, and the picker refuses to edit composites. The new checkbox is
  read both when combining and when updating. It is the composite's own setting: combining does
  not carry the flag over from the triggers being combined.

- **Esc closes the picker**, in all three variants. Triggers already committed stay committed;
  only the draft you were filling in is discarded. Esc while a combo box has its list open closes
  the list and leaves the window alone, and Esc while a picker opened from the trigger-list editor
  is up closes that picker only — never the editor with it.

### Fixed

- **Keys did nothing in a picker opened from the WinUI trigger-list editor.** The picker came up in
  front but the editor took keyboard focus back a moment later, so Enter and Esc reached neither
  window — most visibly when the picker was opened by double-clicking a row. The editor now hands
  activation back to its child picker, and forwards Esc to it. Only the WinUI editor was affected:
  WPF's `Owner` and Windows Forms' `Show(owner)` already cover activation, while WinUI 3's window
  ownership fixes the z-order alone.
- **Enter commits in a picker opened to edit a recorded trigger**, and closes it — the same as
  pressing "Update trigger". A picker opened to record does not do this: it stays open for the
  next trigger, so Enter there would commit something half-filled.
- **Enter and Esc work in the WinUI trigger-list editor**, matching the other two: Enter accepts
  the edited list, Esc discards it. WPF and Windows Forms already had this through their default
  and cancel buttons; WinUI 3 has no equivalent, so it needed wiring.

### Breaking (for `ITriggerListEditorView` implementers)

- `ITriggerListEditorView`: `ExpressionText`, `UnwatchedText` and `CombinePollIntervalSeconds`
  gained setters — the presenter fills them in from the selected composite and empties them when
  the selection is anything else. The interface also gained
  `CombineNotifyOnStoppedMatching { get; set; }` and `CombineCaption { set; }`.
- `ITriggerListEditorView` also gained `SelectRow(int)`, used to put the selection back after a
  composite is rewritten. A view must not report that back as a selection change.
- `TriggerListEditorPresenter` gained `NotifySelectionChanged()`. A view must call it when the
  **user** changes the selection, and must not call it while it is replacing the rows itself.
- `TriggerComposer.Compose` gained an optional `notifyOnStoppedMatching` parameter, and
  `TriggerComposer` gained `Update` and `UnwatchedNames`.
- `EditorStringKeys` gained `CombineStoppedMatchingCheckContent`, `CombineButtonCombine`,
  `CombineButtonUpdate`, `UpdateDone` and `UpdateFailed` — a host supplying its own
  `IPickerStrings` must supply all five.

## 0.1.0-preview.4

### Added

- **A trigger can now tell you when its condition stops holding.** Set
  `TriggerDefinition.NotifyOnStoppedMatching` on a `WhileMatching` trigger and the monitor also
  fires on the falling edge, with `TriggerFiredEventArgs.On = TriggerOn.StoppedMatching` so the two
  edges are distinguishable. The flag is rejected on any other lifecycle, the falling edge is
  exempt from `MinInterval` (dropping it would leave you believing the condition still holds), and
  stopping or removing the trigger raises nothing. The picker offers the flag as an
  "Also notify when it stops matching" checkbox, shown for `WhileMatching` only.
- **An `Always` clause now means "the element is there".** It is satisfied exactly while the
  clause's element is resolved, which makes three documented shapes actually work: the composite
  clauses `TriggerComposer` builds from clause-less sources require the element's presence,
  `!name` in an expression rises when that element disappears, and `WhileMatching` + an `Always`
  clause + `NotifyOnStoppedMatching` reports an element appearing and disappearing with a single
  trigger. Value predicates are unchanged: they keep evaluating against the last-seen value, so a
  satisfied trigger does not flap when the UI tree is merely rebuilt.
- **A composite can be given a poll interval when it is combined.** The trigger-list editor grew a
  "Poll interval (s)" field next to the combine controls, and `TriggerComposer.Compose` takes an
  optional `pollInterval` (a negative value is a reason not to combine; zero and null mean
  event-driven, as everywhere else). `Decompose` does not carry the composite's interval into the
  recovered triggers — it paid for re-reading the combined condition, not for any one clause.
- **Editing a trigger now looks and ends like editing.** A picker opened from the trigger-list
  editor's "Edit condition" shows **Update trigger** on the commit button instead of
  "Add trigger", and closes as soon as the commit succeeds. Recording new triggers is unchanged:
  that picker stays open so you can commit as many as you like.
- **Double-clicking a row in the trigger-list editor edits it**, in all three editors — the same
  as pressing "Edit condition", including the same refusal message for a composite.

### Fixed

- **The trigger-list editor is usable when its content overflows.** The WinUI list now scrolls
  horizontally, so a long composite row can be read to the end (the WPF and Windows Forms lists
  already did); and in both the WinUI and WPF editors the band below the list — the combine
  fields, the status line and OK/Cancel — scrolls when the window is small. It used to clip
  silently: at 175% scale a 760px-wide WinUI window cut the composite poll-interval field off
  entirely, and a short window squashed OK/Cancel to a sliver.
- **Hovering over your own window no longer disturbs it.** With hover auto-select on, every dwell
  used to run a UI Automation hit test against the calling process's own provider and then throw
  the result away — the elements were skipped only after the call. In a WinUI 3 host that query
  activated the host's main window about a second after the pointer came to rest, pushing the
  picker behind it; the highlight did not move, so it looked like nothing had happened. Points
  over the calling process are now rejected before UI Automation is asked (which also covers the
  overlap stack the arrow keys build). Nothing changes for other processes.
- **Double-clicking a row opens the picker in front, with focus.** Opening it directly inside the
  double-click handler let the tail of the click sequence re-activate the editor — in WinUI the
  picker ended up behind the editor window outright. The WinUI child picker is now also
  Win32-owned by the editor (the same relationship the WPF and Windows Forms editors already
  had), so it stays above the editor structurally, whichever window activation lands on. As with
  any owned window, it no longer gets its own taskbar button and minimizes with the editor.

- **`ElementRemoved` conditions now compare against the value just before removal.** They used to
  be evaluated against the values captured when the element was first resolved, so
  "fires when the element named *ready* disappears" silently matched the name the element had
  when monitoring started. Triggers with watched clauses on `ElementRemoved` now keep that
  snapshot fresh by subscribing to the watched properties (the subscription never fires the
  trigger by itself).

### Breaking (for `IPickerView` / `ITriggerListEditorView` implementers)

- `IPickerView` gained `CommitCaption { set; }` and `Close()`.
- `ITriggerListEditorView` gained `CombinePollIntervalSeconds { get; }`.
- `OperandVisibility` gained a `StoppedMatching` flag; the positional constructor changed.
- `TriggerDraft` gained `NotifyOnStoppedMatching`; `TriggerDraftValidator.Apply` clears the
  definition's flag when the draft's lifecycle is not `WhileMatching`.
- `TriggerComposer.Compose` gained an optional `pollInterval` parameter.
- `TriggerOn` gained the event-only member `StoppedMatching`. Enums are persisted as names, so
  saved trigger files are unaffected; a definition using it as its lifecycle is rejected when the
  trigger is added.

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
