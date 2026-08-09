# UiaTrigger

*[English README](README.md)*

他アプリの UI 要素の出現・削除・プロパティ変化を UI Automation で監視し、条件成立時にイベントを発火する .NET ライブラリ。

- C# / .NET 10 / WinUI 3 (Windows App SDK 2.3) / **Native AOT 発行対応**
- COM 相互運用: 手書き `[GeneratedComInterface]` (UIA) + CsWin32 (Win32、Picker 層)
- UIA アクセスはすべて `UiaSession` の専用 MTA スレッド上に一元化 (記録・調査・監視で 1 本を共有できる)
- 監視は既定でイベント購読式 (PropertyChanged / StructureChanged / WindowOpened・Closed)。
  **ライブラリが自分の判断でポーリングすることはない。**
  **裏返すと、監視するアプリが UI Automation のイベントを上げなければ発火しない。**
  プロパティによっては `PropertyChanged` を一切上げないアプリがあり、それはこのライブラリの側では
  どうにもならない。画面上で値が変わって見えることと、変わったと通知されることは別なので、
  新しいトリガーは**実際に監視したいアプリで**確かめること
- **そういうアプリだと分かったら `TriggerDefinition.PollInterval` を指定する。**
  そのトリガーの解決済み要素だけが、その周期で読み直される。既定は無効、指定は全体ではなく
  **トリガーごと**、費用は 1 周につき要素 1 つあたりクロスプロセス読み 1 回で、
  `TriggerMonitorDiagnostics.PollCount` と `PolledReadCount` に出る。
  どちらの場合も**要素を見つけるところはイベント駆動のまま**で、
  ポーリングが読み直すのは既に見つかっている要素だけである

## 導入

```powershell
# 自分のアプリと同じ UI フレームワークのものを 1 つ選ぶ。
dotnet add package UiaTrigger.Core             # ライブラリ本体 (UI なし)
dotnet add package UiaTrigger.Picker.WinUI     # + 要素ピッカー (WinUI3)
dotnet add package UiaTrigger.Picker.Wpf       # + 要素ピッカー (WPF)
dotnet add package UiaTrigger.Picker.WinForms  # + 要素ピッカー (Windows Forms)
```

コードからトリガーを定義して監視するだけなら `UiaTrigger.Core` で足りる。`Picker.*` は
UI フレームワーク 1 つぶんの要素ピッカーを足すもので、`UiaTrigger.Core` を連れてくるので
両方を参照する必要は無い。5 つとも Windows 専用 (`net10.0-windows`。WinUI3 のものだけ
Windows 10 build 19041 以降)。配るアセンブリは**すべて AnyCPU** なので、パッケージ側が
アプリのアーキテクチャを縛ることはない。

## 構成

| プロジェクト | 内容 |
|---|---|
| `UiaTrigger.Core` | UI 非依存のクラスライブラリ。`UiaSession` (要素の探索・記録)・モデル (POCO)・ビーム探索式要素解決・`TriggerMonitor`・JSON context |
| `UiaTrigger.Picker.Core` | UI 非依存のピッカー本体。`TriggerPickerPresenter` と `TriggerListEditorPresenter` (振る舞い)・オーバーレイ・View との継ぎ目 (`IPickerView` ほか) |
| `UiaTrigger.Picker.WinUI` | WinUI3 の View。`TriggerPickerWindow` と `TriggerListEditorWindow`。振る舞いは持たない。他の View に無い依存を 2 つ持つ (下記) |
| `UiaTrigger.Picker.Wpf` | WPF の View。`TriggerPickerWindow` と `TriggerListEditorWindow`。振る舞いは持たない |
| `UiaTrigger.Picker.WinForms` | Windows Forms の View。`TriggerPickerForm` と `TriggerListEditorForm`、加えて `PropertyGrid` 用の `TriggerListEditor`。振る舞いは持たない |
| `UiaTrigger.App.WinUI` | サンプルホスト (WinUI3)。Picker を起動し `List<TriggerDefinition>` を JSON 保存 |
| `UiaTrigger.App.Wpf` | サンプルホスト (WPF)。同上 |
| `UiaTrigger.App.WinForms` | サンプルホスト (Windows Forms)。同上 |
| `UiaTrigger.TestHost` | ライブラリ検証用コンソール。`record` / `monitor` コマンド |

**NuGet で配るのは上の 5 つだけである。**サンプルホスト 3 つと `UiaTrigger.TestHost` は
読んで写すためのもので、ソースから実行するか
[Releases](https://github.com/masa-iwm/UiaTrigger/releases) の zip をそのまま動かす。

### 各パッケージが引き込むもの

| パッケージ | 依存 |
|---|---|
| `UiaTrigger.Core` | `Microsoft.Extensions.Logging.Abstractions` |
| `UiaTrigger.Picker.Core` | `UiaTrigger.Core` |
| `UiaTrigger.Picker.Wpf` / `.WinForms` | `UiaTrigger.Picker.Core` |
| `UiaTrigger.Picker.WinUI` | `UiaTrigger.Picker.Core`・`Microsoft.WindowsAppSDK`・`CommunityToolkit.WinUI.Controls.Sizers` |

`Microsoft.Extensions.Logging.Abstractions` は**全パッケージに届く** — ライブラリは `ILogger`
経由でログを出し、実装は持たないためである。**Microsoft 以外の依存は
`CommunityToolkit.WinUI.Controls.Sizers` だけ**で、必要とするのは WinUI3 の View だけである
(WPF と Windows Forms が標準で持つ区切りを、WinUI3 だけが持たない)。

### 窓が 2 つある — ピッカーと一覧エディタ

ピッカーは**要素 1 つ**を録って**条件 1 件**にするところまでを担う。一覧エディタはその残り半分で、
リスト全体を扱う — 追加 (ピッカー経由)・既存の条件の編集・削除・複数トリガーのまとめ・
まとめたトリガーのほどき。

```csharp
// いま持っているリストを渡すと、編集後のリストが返る。取り消されたときは null。
IReadOnlyList<TriggerDefinition>? edited = await TriggerListEditorWindow.EditAsync(owner, triggers);
if (edited is not null)
{
    TriggerStore.Save(path, edited);   // どこに置くかはホストの自由という方針は変わらない
}
```

**写しの上で動く。**渡したリストには一切触れず、返るのは新しい写しなので、取り消せば元のままである
— ダイアログの中でトリガーを録ったり削除したりした後でも変わらない。エディタはトリガーの保存先を
知らず、監視を開始も停止もしない。

シグネチャが 3 変種とも非同期なのは、**WinUI 3に窓単位のモーダルが無い**からである。あちらの
エディタは非モーダルで、窓が閉じたときに完了する — 開いているあいだ、ユーザーが 2 枚目を
開けないようにすること。WPF / Windows Forms は本物のモーダルなので、返る時点でタスクは完了している。

Windows Forms にはさらに `UITypeEditor` 派生の `TriggerListEditor` がある。
`PropertyGrid` からそのまま編集できる:

```csharp
[Editor(typeof(TriggerListEditor), typeof(UITypeEditor))]
public List<TriggerDefinition> Triggers { get; set; } = [];
```

**ピッカーで編集できるもの・できないもの**は `TriggerPickerPresenter.CanEdit` が答える —
編集を提示する前に訊くこと。ピッカーが編集するのは素の条件 1 件なので、まとめたトリガーは断る
(先にほどく)。条件の下書きに運ぶ場所が無いものを持っている場合も断る: 自前の要素・
切ってある `Watch`・カスタムプロパティ id である。確定は下書きから条件を作り直すので、
これらは黙って落ちてしまう。

### View が 3 変種あることについて

振る舞い (`TriggerPickerPresenter` / `TriggerListEditorPresenter`) は
`UiaTrigger.Picker.Core` に 1 つだけ在り、View は「そのフレームワークでしかそうならないこと」だけを
持つ薄い層である。自分のアプリと同じ UI フレームワークの `Picker.*` を参照すればよい。

**意図的に非対称な点が 2 つある**:

- **サンプルホストの `App.WinUI` だけがショーケースを兼ねる** (ピッカー → 監視の E2E)。
  3 重化しても得るものが無いため、`App.Wpf` / `App.WinForms` はトリガーを録って
  JSON に保存するところまでにしてある。WinUI ホストは自前の「まとめる」欄も持ち続けており、
  まとめたトリガーが実際に発火することの E2E テストはそこを通っている
- **Windows Forms の View だけ、行の中に確定ボタンが無い** (「選択中の行を確定する」
  ボタン 1 つに置き換えている)。Windows Forms の `TreeView` は行に任意のコントロールを
  置けないためで、他の 2 変種と UI の形が違う唯一の箇所である

ユーザー向け文字列のキーは窓ごとに 2 つの表 (`PickerStringKeys` / `EditorStringKeys`) に集約してあり、
どちらも同じ 2 経路で供給される (WinUI は `.resw` + MRT Core、WPF / Windows Forms は
`Picker.Core` の `.resx` を共有)。

### 設計メモ

- **永続化モデルは `TriggerDefinition`** (キー `Id` を内包)。ファイル形式はホストの自由という方針は
  変えていないが、ライブラリが `UiaTrigger.Serialization.TriggerJsonContext` (source-gen) を*提供*するので、
  ホストは `TypeInfoResolverChain` に足すだけでよい。トリガーだけを 1 ファイルに置くなら
  `UiaTrigger.Persistence.TriggerStore` がそのまま使える (パスは呼び出し側が渡す)。サンプルホストは `%LOCALAPPDATA%\UiaTrigger\triggers.json` を使う
- **トリガーファイルには JSON Schema がある。**`TriggerStore.Save` は `triggers.schema.json` を
  隣に書き出し、ファイルへ `$schema` を書き込むので、利用者側の設定なしにエディタの補完と検証が効く。
  別の場所へ保存するなら `TriggerJson.Schema` が同じテキストを返す。schema は**モデルから生成する**
  ので、ライブラリが読む形からずれない
- **トリガーの形**: `On` (`ElementAppeared` / `ElementRemoved` / `PropertyChanged` / `WhileMatching`) と
  `Clauses` (プロパティごとの述語のリスト、`Combine` で `All` / `Any`) が独立している。
  「要素が出現し、**かつ** Value が X」のような組み合わせが書ける。`MinInterval` で発火レート制限
- **1 つのトリガーが複数の要素・複数のウィンドウにまたがれる**。句は自前の `Window` と `Locator` を
  持てて、両方 null ならトリガーの既定を使う — 「A のボタンが有効 **かつ** B のラベルが完了」が
  1 つのトリガーになる。同じ要素を指す句は解決も購読も 1 つを共有する。
  `Combine` 1 つでは言えない条件は `Expression` に `&&` / `||` / `!` と括弧で書く:
  `(ready || idle) && !busy`。**オブジェクトの木ではなく文字列なのは意図的で**、
  モデルはどのシリアライザーでも往復できる素の POCO のままでいられる
- **`Clause.Watch`** は、その句のプロパティの変化がトリガーを**発火させうる**のか、
  それとも条件を**絞るだけ**なのかを決める。「A が存在していて、**かつ** B の値が変化したとき」を
  表すのがこれである — 購読はプロパティから作られ演算子を見ないので、
  要素を要求するだけのつもりの句が、そのままだと変化のたびに発火してしまう
- **発火イベント** `TriggerFired`: `TriggerId` / `On` / `OldValue` / `NewValue` (いずれも `ComparisonString`) /
  `Properties` (**観測できるプロパティすべて**のスナップショット — 監視対象のものだけではない。
  1 往復で全部読めるので絞る意味が無い) / `Timestamp`。
  **これらが語るのは 1 つの要素についてである — 最初の句が読む要素。**複数の要素にまたがるトリガーでは
  *変化した*句が別の句かもしれないので、何がトリガーを鳴らしたかとイベントが何を報告するかは別の問いである。
  3 つは常に互いに整合している。
  **残りが読めるようになるのが `Clauses`** — 句 1 つにつき `ClauseReading` 1 件で、
  それぞれが自分の要素から読んだ値と、`Matched` / `NotMatched` / `Unreadable` / `NotEvaluated` の
  行き先を持つ。最後のものを別の状態にしてあるのは意図で、式は短絡するので
  評価されない側の句は**一度も見ていない** — 「成立しなかった」とは別である。
  単一ワーカー上で**検出順に 1 件ずつ**配送され、ハンドラの例外は `UnhandledException` へ回る
- **要素の識別**: 「スコア合計 + 閾値」ではなく**必須述語 + ランキング + ビーム探索**。
  トップレベルウィンドウは属性ごとの `MatchStrength` (`Required` / `Preferred` / `Ignored`) で照合し、
  足切りは `Required` の属性だけが行う。配下は各段の ControlType / AutomationId / Name / ClassName を
  「記録された属性がその候補を肯定する度合い」として採点し、上位 K 件を残して探索する
  (行き止まりから後退できる)。兄弟インデックスは同点時のタイブレークのみ (`ResolverOptions` で調整可)
- **`UiaSession` が公開の継ぎ目**: 1 セッション = 1 MTA スレッド + 1 `IUIAutomation`。
  座標からの要素取得 (`ElementFromPointAsync`)・子の列挙・祖先チェーン・重なりスタック・
  スナップショット・定義の記録がここに揃っており、`CreateMonitor()` で監視も同じスレッドに載る。
  COM 型は `UiaElement` という不透明ハンドルの裏に閉じてあるので、第三者が同じ API で
  自前のピッカー / インスペクタを書ける (本リポジトリの `UiaTrigger.Picker.Core` がその実例)。
  呼び出し上限時間・時計 (`TimeProvider`)・ログ (`ILogger`) は `UiaSessionOptions` に集約
- **トリガーは動かしたまま増減できる**: `AddAsync` / `RemoveAsync` は該当トリガーの購読だけを
  張り替える。`TriggerMonitor.GetDiagnostics()` で購読数・受信イベント数・解決済み件数が読める
- **座標の前提**: 座標を扱う API はホストが `PerMonitorV2` を宣言していることを前提とする。
  DPI 非認識のプロセスでは Windows が座標を仮想化し、**例外にならずに別の要素が返る**。
  `UiaSession.CoordinateProblem` が理由を返すので、ホストはこれを見て警告すること
  (`DpiAwareness.TryEnablePerMonitorV2()` は manifest を持てないホスト向け)
- **interop の経緯**: CsWin32 0.3.x の COM 生成は `[ComImport]` (AOT 不可) のため、UIA の COM インターフェースは
  UIAutomationClient.h の vtable 順で手書きした `[GeneratedComInterface]` を使用。この方式が要求する
  `DisableRuntimeMarshalling` は CsWin32 生成の `SetLastError=true` な DllImport と非互換のため、
  Core 内の Win32 関数は `LibraryImport` 手書き。Picker (別アセンブリ) は CsWin32 (`allowMarshaling:false`) を使用

## 使い方

```powershell
# ビルド (要 .NET 10 SDK / Windows App SDK はNuGet復元)
dotnet build

# 1. トリガー定義の作成
#   a) GUI: サンプルホストを起動 → [ピッカーで追加] (3 変種のどれでもよい)
dotnet run --project src/UiaTrigger.App.WinUI
dotnet run --project src/UiaTrigger.App.Wpf
dotnet run --project src/UiaTrigger.App.WinForms
#   b) CLI: カーソル下の要素を記録 (3 秒後に捕捉)
dotnet run --project src/UiaTrigger.TestHost -- record my-trigger --on PropertyChanged --prop Name --op Always

# 2. 監視 (条件成立で発火ログ)
dotnet run --project src/UiaTrigger.TestHost -- monitor

# Native AOT 発行 (要 VS C++ ツールチェーン。vswhere が PATH に必要な場合あり)
dotnet publish src/UiaTrigger.TestHost -c Release -r win-x64 -o publish/TestHost
dotnet publish src/UiaTrigger.App.WinUI -c Release -o publish/App
```

### ピッカーの操作

- **マウス自動選択 ON**: カーソルを 1 秒静止すると要素を捕捉し、赤枠オーバーレイ (クリックスルー) を表示
- **枠右上の ✓ アイコン**または**ツリー各行の ✓** クリックで要素を確定
- **←/→**: 同一座標に重なる要素の切替 (別ウィンドウ・別プロセスも対象。← 下 / → 上)
- ツリー: プロセス → 要素の直系チェーンを表示。行クリック / 展開で子を全列挙 (遅延取得)。
  階層ビュー (Raw / Control / Content) 切替、取得済みノードの検索に対応
- 確定後、発火契機・プロパティ・条件・最小発火間隔・Id を設定して「トリガーを追加」→ ホストが JSON 保存

### 発火契機 (`TriggerOn`) と条件 (`ComparisonOp`)

発火契機は 4 種:

| `On` | 発火するとき |
|---|---|
| `ElementAppeared` | 対象要素が解決できたとき (句があれば、それも満たしているとき) |
| `ElementRemoved` | 対象要素が消えた / 別物に置き換わったとき |
| `PropertyChanged` | 監視中のプロパティが変化し、句を満たすたび |
| `WhileMatching` | 句が成立し始めた瞬間だけ (立ち上がりエッジ) |

`WhileMatching` は**立ち下がり**も通知できる。定義に `NotifyOnStoppedMatching` を立てると、
句が成立しなくなった瞬間にもう一度発火し、イベント側の `On` が `TriggerOn.StoppedMatching` になる:

```csharp
var definition = new TriggerDefinition
{
    On = TriggerOn.WhileMatching,
    NotifyOnStoppedMatching = true,   // 成立しなくなったときにも発火する
    // …
};
```

`StoppedMatching` はイベントが報告する値であって、記録する値ではない — `On` に書くと拒否される。
`WhileMatching` 以外にこのフラグを立てるのも拒否される (効かない定義の上に黙って残らないため)。
立ち下がりは `MinInterval` の対象外である。落とすと、条件がまだ成立していると受け手に思わせるからである。

`Always` の句は要素が解決できている間だけ成立するので、`WhileMatching` + このフラグで
「要素が出た / 消えた」をトリガー 1 つで受け取れる。

句 (`PropertyClause`) の比較演算子:

- `Always` — 値を見ない (プロパティを購読したいだけのとき)
- 数値: `Between` / `NotBetween` / `GreaterThan` / `LessThan` / `LessOrEqual` / `GreaterOrEqual`。
  `Tolerance` で「等しいとみなす帯」の幅を指定できる (`RangeValue` のような double 用)
- 文字列: `Equals` / `NotEquals` / `RegexMatch` / `RegexNotMatch` (NonBacktracking, タイムアウト付き)。
  比較値は常に `InvariantCulture` / `Ordinal`。`bool` は `true` / `false` で、大小文字は問わない
- `Property = Custom` + `CustomPropertyId` で、列挙にない UIA プロパティも対象にできる

## ライブラリ利用例

```csharp
// 座標の要素からトリガー定義を記録し、同じセッションで監視まで行う
await using var session = new UiaSession();
if (session.CoordinateProblem is { } problem)
{
    Console.Error.WriteLine(problem);   // ホストが PerMonitorV2 でない
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
// 「進捗バーが 100% になったら 1 回だけ」
var trigger = new TriggerDefinition
{
    Id = "download-done",
    Window = new WindowIdentity { ProcessName = "myapp.exe" },
    Locator = locator,                       // ピッカー / UiaSession.BuildDefinitionAsync が記録したもの
    On = TriggerOn.WhileMatching,
    Clauses = [new PropertyClause
    {
        Property = TriggerProperty.RangeValue,
        Op = ComparisonOp.GreaterOrEqual,
        Value = 100,
        Tolerance = 0.001,                   // double の厳密比較は当てにしない
    }],
    MinInterval = TimeSpan.FromSeconds(1),
};
```

## 既知の注意点

- UWP 系アプリはトップレベルが `ApplicationFrameHost.exe` になる (記録・解決とも同一規則なので一致はする)
- `WhileMatching` はエッジ検出 (false→true で発火、true 継続中は再発火しない)。
  開始時に既に条件成立している場合の発火は `TriggerMonitorOptions.FireOnInitialMatch` (既定 true)
- パスワード欄 (`IsPassword`) の `Value` と `Name` はスナップショットから伏せられる
- 重なり切替の同一ウィンドウ内 Z 順は Raw ビューのヒットテストチェーン+文書順による近似
- 選択モード中は低レベルフックで ←/→ を監視するが、キー自体は他アプリへパススルーする (奪わない)

## ドキュメント

| 文書 | 内容 |
|---|---|
| [CHANGELOG.md](CHANGELOG.md) | 版ごとの変更点 (利用者向け。英語) |
| [docs/DESIGN.md](docs/DESIGN.md) | 設計: アーキテクチャ・不変条件・判断の台帳 (理由付き) |
| [docs/TESTING.md](docs/TESTING.md) | テスト層 (T1〜T6)・横断ルール・合成入力の政策と、その背後の検証哲学 |
| [docs/LOCALIZATION.md](docs/LOCALIZATION.md) | ローカライズ: 方針・文字列の分類・供給経路・XML doc・配布物に入るもの |
| [docs/RELEASING.md](docs/RELEASING.md) | パッケージ構成・CI の構成・リリース手順・パッケージ化の罠の台帳 |
| [docs/MANUAL-CHECKS.md](docs/MANUAL-CHECKS.md) | 自動化できない範囲の手動確認チェックリスト |

**リリース**には、サンプルホスト 3 つとコンソール版が展開してすぐ動く zip として
添付される。ビルドせずにピッカーを試せる。ライブラリ本体は NuGet から取ること。

> **注意**: 版数が `0.x` のあいだ、公開 API と `triggers.json` の形式は**まだ安定していません**。
> マイナー更新でも破壊的に変わりえます (旧ファイルの移行コードはありません)。
> 変更点は [CHANGELOG](CHANGELOG.md) を参照してください。

## 開発

```powershell
dotnet build UiaTrigger.slnx -c Release   # 警告は 1 つでもエラーになる
dotnet test  UiaTrigger.slnx -c Release   # 先に build が必要 (manifest 検査がビルド成果物を読むため)
```

- ユーザーに見える文字列は `.resx` 経由にすること。プライマリは **en-US**、日本語は `Strings.ja.resx`
- 比較・永続化に使う文字列は**必ず InvariantCulture / Ordinal** ([docs/LOCALIZATION.md](docs/LOCALIZATION.md) §3)
- **`tests/UiaTrigger.Input.Tests` の外のテストで擬似入力 (`SendInput` 等) を使わないこと** —
  CI が検出して失敗させる。理由は [docs/TESTING.md](docs/TESTING.md) §4、唯一許可された
  プロジェクトの政策は同 §3

---

**正は英語版 ([README.md](README.md)) である。**食い違ったら英語版が勝ち、こちらを合わせる。
`docs/` 配下の 5 文書は保守者向けの内部文書なので日本語のままとする。

> このファイルの `docs/` へのリンクは**相対パス**である。英語版は NuGet の
> `PackageReadmeFile` であり、nuget.org では相対リンクが 404 になるため
> **絶対 URL** を使っている。同じに揃えないこと。
