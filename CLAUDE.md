# UiaTrigger — 作業規約

## §1 概要

UiaTrigger は、UI Automation で他アプリの UI 要素の出現・削除・プロパティ変化を監視し、
条件成立時にイベントを上げる C# / .NET 10 のライブラリ群である。UI 非依存の `Core` と、
振る舞い (`Picker.Core`) + View 3 変種 (WinUI3 / WPF / WinForms) に分かれたピッカー 4 つ、
サンプルホスト 3 種、CLI の `TestHost` で構成し、Native AOT 発行に対応する。
UIA アクセスは `UiaSession` の専用 MTA スレッド 1 本に一元化し、監視は既定でイベント購読式
(ライブラリが自分の判断でポーリングすることはない)。設計の正は docs/DESIGN.md。

## §2 プロジェクト役割表

| プロジェクト | 役割 |
|---|---|
| `src/UiaTrigger.Core` | UI 非依存ライブラリ。`UiaSession` (公開 API)・モデル (POCO)・ビーム探索式要素解決・`TriggerMonitor`・JSON context |
| `src/UiaTrigger.Picker.Core` | UI 非依存のピッカー本体 (`net10.0-windows` / AnyCPU / CsWin32 のみ)。`TriggerPickerPresenter`・オーバーレイ・継ぎ目 (`IPickerView` ほか) |
| `src/UiaTrigger.Picker.WinUI` | WinUI3 の View。`TriggerPickerWindow` と `.resw`。振る舞いは持たない |
| `src/UiaTrigger.Picker.Wpf` | WPF の View。`TriggerPickerWindow`。振る舞いは持たない |
| `src/UiaTrigger.Picker.WinForms` | Windows Forms の View。`TriggerPickerForm` と `TreeMirror`。振る舞いは持たない |
| `src/UiaTrigger.App.Shared` | サンプルホスト 3 つが共有するコマンドライン読み取り (`HostOptions` / `HostWindowPlacer`)。AnyCPU・**配らない** (公開型を持たず `InternalsVisibleTo` で見せる) |
| `src/UiaTrigger.App.WinUI` | サンプルホスト (WinUI3)。Picker 起動 + JSON 保存 + 監視のショーケース (docs/DESIGN.md D9) |
| `src/UiaTrigger.App.Wpf` | サンプルホスト (WPF)。Picker 起動 + JSON 保存 |
| `src/UiaTrigger.App.WinForms` | サンプルホスト (Windows Forms)。Picker 起動 + JSON 保存 |
| `src/UiaTrigger.TestHost` | ライブラリ検証用コンソール (`record` / `monitor`) |
| `tests/UiaTrigger.Core.Tests` | 単体テスト (xunit v3)。T1 / T2。ローカライズ (resx / resw / XML doc) の回帰もここ |
| `tests/UiaTrigger.RealUia.Tests` | 実 UIA テスト (xunit v3)。T3 — 直列実行・CI では別ジョブ (`real-uia`) |
| `tests/UiaTrigger.TestTarget` | T3 用の WinForms 対象アプリ (MSAA ブリッジ)。stdin のコマンドで UI を変化させる |
| `tests/UiaTrigger.TestTarget.Wpf` | T3 用の WPF 対象アプリ (ネイティブ UIA プロバイダー)。protocol は同じ (`ok`/`err` の 1 行応答) だが動詞は部分集合 — `place` / `hang` など一部は WinForms 側のみ |
| `tests/UiaTrigger.Picker.UiTests` | ピッカーの UI テスト (xunit v3)。T4 — ホストを子プロセスで起動し `System.Windows.Automation` で駆動。直列実行・CI では別ジョブ (`picker-ui`) |
| `tests/UiaTrigger.Input.Tests` | 合成入力のテスト (xunit v3)。T5 — このディレクトリだけ `SendInput` が許可されている (docs/TESTING.md §3)。実機のキーボードとマウスを奪うので直列実行・CI では別ランナー (`input`) |

## §3 ビルド

```powershell
dotnet build UiaTrigger.slnx -c Release   # 警告 1 つで失敗 (TreatWarningsAsErrors)
dotnet test  UiaTrigger.slnx -c Release   # 先に build (manifest 検査がビルド成果物を読む)
```

**単体プロジェクトのビルドは禁止。**`dotnet build src/<プロジェクト>` は `bin\Debug` という
別出力を作り、T4 が「いちばん新しい exe」として黙って掴む。復旧手順とローカル実行の注意は
.claude/rules/build.md と docs/TESTING.md §6。

## §4 テスト層 (詳細は docs/TESTING.md)

- T1 — UIA 非依存の純ロジック。GUI 由来のロジックも Presenter / `OverlayGeometry` として引き下ろす
- T2 — 構成・環境の不変条件 (interop の形・DPI・座標系・AOT 発行)。最重要
- T3 — 専用対象アプリ 2 種への実 UIA テスト。擬似入力なし、stdin コマンドで UI を変化させる
- T4 — ピッカーの UI 自体を UIA のコントロールパターンで駆動。入力の配送は主張できない
- T5 — `SendInput` で最下層から駆動。低レベルフックとヒットテストはここでしか通らない
- T6 — 手動チェックリスト (docs/MANUAL-CHECKS.md)。物理入力の差と見た目の最終判断

## §5 文書地図

- docs/DESIGN.md — 設計の不変条件と判断の台帳。所見 ID (A/B/C/D/L/S) の正は §13
- docs/TESTING.md — テスト層 T1〜T6・横断ルール・合成入力の政策・既知の不安定テスト
- docs/LOCALIZATION.md — ローカライズの正典。文字列分類・供給経路・XML doc・invariant の 2 種類
- docs/RELEASING.md — パッケージ構成・CI の構成・リリース手順・罠の台帳
- docs/MANUAL-CHECKS.md — 手動チェックリスト (T6)

文書は不変条件を現在形で書く。経緯・日付・Phase は書かない (.claude/rules/docs-style.md)。

## §6 規律ポインタ

- 合成入力: `keybd_event` / `mouse_event` / `PostMessage` / `SendMessage` は `tests/**` で全面禁止
  (コメントも lint が検出)。`SendInput` は `Input.Tests` のみ — docs/TESTING.md §3
- 文字列: 表示は `.resx` / `.resw` + `CurrentUICulture`、比較・永続化は `InvariantCulture` /
  `Ordinal`、ログは英語固定 — docs/LOCALIZATION.md §3 / .claude/rules/localization.md
- 公開 API の XML doc は英語必須。日本語は `Resources/<アセンブリ名>.ja.xml` で供給し、
  公開 API と 1:1 の同期をテストが縛る — docs/LOCALIZATION.md §5
- 実装内部のコメントは日本語のまま — docs/LOCALIZATION.md §1
