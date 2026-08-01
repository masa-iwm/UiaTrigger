# LOCALIZATION — ローカライズ / 国際化の正典

方針・仕組み・実測で確かめた挙動・踏みやすい穴をこのファイルに集約する。
検査そのものの配置と層の定義は docs/TESTING.md §1 を見ること。

## §1 方針

**en-US がプライマリ (neutral resources language)、ja-JP がサテライト。**
`[assembly: NeutralResourcesLanguage("en-US")]` を宣言し、日本語は `ja` サテライトで供給する。
公開 API の XML doc・例外メッセージ・UI 文字列のすべてが対象である。

文書の言語は読者で決まる:

| 対象 | 言語 |
|---|---|
| `README.md` | **英語。こちらが正** (NuGet の `PackageReadmeFile` にもこれを使う) |
| `README.ja.md` | 同内容の日本語訳。ずれたら英語版へ寄せる (docs/LOCALIZATION.md §8) |
| `docs/` 配下 | 日本語 (保守者向けの内部文書) |
| 公開 API の XML doc | **英語必須**。日本語はサテライト doc で供給する (docs/LOCALIZATION.md §5) |
| 実装内部のコメント | 日本語のまま。保守者向けであり、判断の記録として価値がある |

公開 API の XML doc が英語であることは CI がゲートする。ここには成立条件がある:
csc の出す `.xml` には internal / private のメンバーも入り、内部コメントは日本語のままなので、
**「英語であること」の検査は公開 API のぶんだけを対象にしなければ成立しない**
(`PublicApiDoc` がドキュメント ID からリフレクションで可視性を判定している)。

`GenerateDocumentationFile` を立てているため、CS1591 (公開メンバーの doc 欠落) は
警告エラーになる。NuGet 配布では「埋まっていること」自体が要件なので、これは得た側である。

## §2 AOT とサテライトの実測

サテライトが Native AOT で動くかは「AOT 発行時にしか露見しない類の問題」なので、
方式は推測ではなくスパイクの実測で決めてある:

| 条件 | 結果 |
|---|---|
| 単体プロジェクトに `.resx`、`PublishAot=true`、`InvariantGlobalization=false`、`en-US` | neutral リソースを取得 |
| 同上、`ja-JP` | 日本語を取得。publish 出力に `ja/<asm>.resources.dll` が生成される |
| 同上だが `InvariantGlobalization=true`、`ja-JP` | `CultureNotFoundException: Only the invariant culture is supported in globalization-invariant mode` |
| 本リポジトリの構成 (`.resx` は `UiaTrigger.Core`、それを参照するホストを AOT 発行) | **`publish/` に `ja/` フォルダは生成されないが、ja-JP で日本語メッセージが出る** |

最後の行は AOT 発行済みの実バイナリで確認したもの:

```
> UiaTrigger.TestHost.exe monitor --file broken.json --culture en-US
定義エラー: Trigger 'bad': Window.ProcessName is required.

> UiaTrigger.TestHost.exe monitor --file broken.json --culture ja-JP
定義エラー: トリガー 'bad': Window.ProcessName は必須です。
```

結論:

- **標準の `.resx` + `ResourceManager` 方式で問題ない。**ソース生成ルックアップへの
  フォールバックは不要
- **`InvariantGlobalization` は必ず `false`。**`true` のプロジェクトでは ja-JP リソースが
  一切使えない (上表 3 行目の例外)
- **プロジェクト参照のサテライトは、AOT 発行時にネイティブイメージへ取り込まれる。**
  `publish/` に `ja/` フォルダは出力されず、同梱も不要
- ただし**発行するプロジェクト自身が `.resx` を持つ場合は `ja/*.resources.dll` が
  別ファイルとして出力される** (上表 2 行目)
- 「AOT だから常に単一ファイル」でも「常に別ファイル」でもない。**配布形態は publish 出力を
  実際に見て決める**
- `SatelliteResourceLanguages` で .NET サテライトの出力言語を絞れる (既定は全言語。
  本リポジトリは `en;ja`)。ただし効くのは `*.resources.dll` だけである —
  依存パッケージが運ぶ `.mui` には効かない (docs/LOCALIZATION.md §9)

WinUI3 アプリ (`UiaTrigger.App`) の発行出力はさらに形が違う:

- `.resw` は **`UiaTrigger.App.pri` に統合される**。en-us / ja-jp 両方の値と、
  クラスライブラリ側 (`UiaTrigger.Picker.WinUI`) の値も同じファイルに入る
  (発行後の `.pri` のバイト列で確認済み)。リソースマップ名はアセンブリ名に追随させてある
- ただし `UiaTrigger.Picker.WinUI.pri` **も**発行フォルダに残る。害は無いが単一ファイルではない
- Core の `ja/*.resources.dll` はネイティブイメージへ取り込まれ、発行フォルダの `ja/` に
  残るのはサテライト doc の `UiaTrigger.Core.xml` だけになる

**結論: WinUI3 アプリの配布は単一ファイルにならない。`*.pri` は exe と同じフォルダに必ず
同梱すること — 欠けても例外は出ず、UI の文字列が空欄になるだけである。**

この失敗形は開発ビルドの単体テストでは捕まらない。実行時解決の単体テストはソースの形しか
見ておらず、**`.pri` が発行から落ちても `ResourceMap` 名が違っても全件緑のまま**である。
WPF の `ja/` サテライトが発行から落ちる形も同じで、どちらも**発行してからでないと現れない**。
だから発行レイアウトそのものを起動して、キーごとにリソースファイルの値と一致することを見る
検査が別に在る (`PublishedResourceTests` — ホストの MainWindow は
`HostPublishedResourceTests`。docs/TESTING.md §1)。

AOT 発行済みバイナリを ja-JP で実行して日本語メッセージが出ることは、
CI の AOT + サテライト検査が固定している (docs/TESTING.md §1 の T2)。

## §3 文字列の分類

文字列は 3 (+1) 種類に分類し、**混ぜない**:

| # | 分類 | カルチャ | 例 |
|---|---|---|---|
| 1 | 比較・永続化用 | 常に `InvariantCulture` / `Ordinal`。ローカライズしない | 条件評価の値、JSON、キー、`UiaControlTypeNames.GetName` の戻り値 |
| 2 | ユーザー向け表示用 | `CurrentUICulture` | UI ラベル、例外メッセージ、`ResolutionChanged.Message` |
| 3 | 開発者向けログ用 | 英語固定 | ログを共有・検索できるようにするため |
| +1 | API ドキュメント | 英語 + `ja` サテライト doc (docs/LOCALIZATION.md §5) | 公開 API の XML doc |

+1 を 1 と混同しないこと — doc は表示物だが、`UiaControlTypeNames.GetName` の**戻り値**は
永続化に入るので 1 に属する (docs/LOCALIZATION.md §6)。

**混ぜると条件評価が静かに壊れる。**表示文字列と比較文字列が同一経路にあると、
「表示は現在カルチャで」と手を入れた瞬間に `Equals` / `RegexMatch` の意味が変わる —
ja-JP で `1234.5` が同じ表記のままでも、区切りやマイナス記号が変わる書式では破綻し、
**例外は出ない**。そのため比較形は型レベルで分離してある: 比較値は `ComparisonString`
(常に Invariant。`From*` ファクトリでのみ生成) に載せ、ユーザー向け表示の文字列
(CurrentUICulture) と分け、条件評価は `ComparisonString` しか受け取らない。`ja-JP` を `CurrentCulture` に設定した
状態で条件評価が不変であることは回帰テストが固定している (docs/TESTING.md §1 の T1)。

運用規則:

- 新しいユーザー向け文字列は**必ず `.resx` / `.resw` 経由**。ハードコードは T1 の単体テスト
  (`NoSourceAssignsAUserFacingLiteral` — `XamlLocalizationTests`) が検出する
- `en-us` と `ja-jp` の**キー集合が一致していること**を単体テストでアサートする
  (翻訳漏れの自動検出)。2 言語の組は放っておくと必ずずれるためで、この「規律で解かず
  機械で縛る」形は本ファイルの各所で繰り返し使う

## §4 供給経路と x:Uid

同じ「ローカライズ」でも、**仕組みの異なる 3 つの経路**が並んでいる。混同すると
「片方だけ翻訳されている」状態が静かに出来上がる:

| 対象 | 供給元 | 実行時の仕組み | 出力の形 |
|---|---|---|---|
| Core のユーザー向け文字列 (例外・診断を含む) | `Strings.resx` / `Strings.ja.resx` | `ResourceManager` + サテライトアセンブリ | `ja/UiaTrigger.Core.resources.dll` (AOT ではネイティブイメージに取り込まれる) |
| Picker / App の UI 文字列 | `Strings/en-us/Resources.resw` (既定) / `Strings/ja-jp/Resources.resw` | MRT Core (`resources.pri`) + `x:Uid` / `ResourceLoader` | `<アプリ名>.pri` に**マージされる** |
| 公開 API の IntelliSense | `Resources/<アセンブリ名>.ja.xml` | IDE / Roslyn の XML doc 探索 | `ja/<アセンブリ名>.xml` |

3 つ目は置き方がサテライトアセンブリと同じでも**仕組みは別物**で、`ResourceManager` は
一切関与しない (docs/LOCALIZATION.md §5)。WPF / WinForms のピッカーは resx 経路
(`ResxPickerStrings`)、WinUI のピッカーは resw 経路 (`MrtPickerStrings`) を使う。

### x:Uid の解決規則

- resw のキーは `<Uid>.<プロパティ名>` 形式 (`ConditionHeading.Text` など)。attached property は
  `<Uid>.AutomationProperties.Name` のようにフルパスで書く。**形式が違っても `x:Uid` は
  黙って解決せず、値が空になるだけである**
- `x:Uid` が与えるのは**静的な**値である。状態で表示を切り替えるものは要素を分ける
  (例: 開始/停止はトグル 1 つでなくボタン 2 つ) か、コードから `GetString` で引く
- `Window` は `FrameworkElement` ではないので `x:Uid` が効かない。ウィンドウタイトルなどは
  コードから `ResourceLoader` で引く
- 失敗形は経路で症状が違う: **`GetString` が解決できないとキー名がそのまま返り、
  `x:Uid` が解決できないとラベルが空になる**。どちらも画面には出るので、検査は
  「空でない」ではなく**リソースファイルから読んだ値と一致すること**を見る
- **「キー名がそのまま返る」は、供給側が受け止めた*あと*の姿である。**素の API は投げうる:
  MRT Core の `ResourceLoader.GetString` は**キーが 1 つ無いだけで**投げる (空文字を返した
  同名の UWP API とは違う)。`ResourceManager` のほうはキー欠落なら `null` を返すが、
  リソース集合ごと見つからなければ `MissingManifestResourceException` を投げる。
  だから 3 つの供給元 (`MrtPickerStrings` / `AppStrings` / `ResxPickerStrings`) が
  そろって受け止めている — **外すと翻訳の 1 つの抜けが窓ごと落とす**
- 「どの `x:Uid` からも参照されていないキー」は `XamlLocalizationTests` が許さない。
  キーを足すなら使う場所と同じコミットに入れる。この帰結として、resw に触る変更では
  WinUI の View を後回しにできない (キーだけ先に入れると赤になる)
- WinUI の View がハードコード検出テスト (`NoSourceAssignsAUserFacingLiteral`) の対象外なのは
  `x:Uid` で解決するからであって、**「ソースへ直書きしてよい」からではない**

### キー表は窓ごとに分け、重ねない

供給経路 (resx × 2 / resw × 2) は実行時には **1 つの辞書**である。ピッカーとエディタの
キー表 (`PickerStringKeys` / `EditorStringKeys`) を重ねると「どちらの窓のものか」が決まらず、
一方の文言を直したときに他方が黙って変わる。`TheTwoKeyTablesDoNotOverlap` がこれを禁じ、
キー集合の過不足検査は 2 表の**和**に対して行う。

### 言語の決まり方 — MRT は `CurrentUICulture` を見ない

実測 (ネガティブコントロール込み) で確定している:

- resx 経路は `CurrentUICulture` で切り替わる
- **MRT (resw 経路) は `CurrentUICulture` をまったく見ていない。**
  `PrimaryLanguageOverride` が単独で効き、アンパッケージのホストでも効く
- **順序が効く。**MRT のローダーは一度でも文字列を読んだら決着する (`static readonly Lazy`)。
  上書きは `App` のコンストラクターで `InitializeComponent()` より**前**に行う。
  順序を間違えると「効かない」と**誤って測る**

WinUI ホスト自身のラベルも `.resw` / MRT なので、WinUI スタック全体が 1 つの仕組みで決まる。

## §5 XML ドキュメント

`Resources/<アセンブリ名>.ja.xml` — 手書きの日本語 IntelliSense ファイル — が
**公開 API の日本語 doc の正**である。

IDE は参照アセンブリの XML doc を探すとき、まず
`<アセンブリのフォルダ>\<カルチャ>\<アセンブリ名>.xml` を見て、無ければ隣の
`<アセンブリ名>.xml` に落ちる。

**ロールアップの単位は「メンバー」ではなく「ファイル」である。**`ja/` のファイルが存在する
限りそちらが**丸ごと**使われ、そこに無いメンバーは英語に落ちるのではなく**説明が消える**。
resx のキー集合一致 (docs/LOCALIZATION.md §3) と同じ理由で、ja.xml と公開 API の過不足を
`TheJapaneseDocumentationCoversExactlyThePublicApi` が見ている。
`JsonSerializerContext` のメンバーは STJ のソースジェネレーターが doc ごと生成するので
翻訳対象から外してある — `[JsonSerializable]` を 1 行足すたびに ja を書き足すことになるため。

### 英語側は「絞り込み前」を読む — でないと不足を検出できない

**`bin` に並ぶ `.xml` を英語側として読んではならない。**あれは
`KeepDistributedDocumentationPublic` が**ja のキー集合で絞ったあと**の姿なので、ja から
メンバーを 1 つ落とすと英語側からも同時に消え、突き合わせても常に一致する。この形では
「ja に在って公開 API に無いもの」しか捕まらず、**「公開 API に在って ja に無いもの」は
原理的に検出できない** — ja が絞り込みの正であると同時に検査の対象でもある循環である。

そのため絞り込み前の姿を `obj\<構成>\<TFM>\<アセンブリ名>.unfiltered.xml` に控え、
公開 API の側を見る検査 (英語であること / ja の過不足 / 実際に翻訳されていること) は
**すべてそちらを読む**。非公開メンバーが混ざったままの控えを使えるのは、
`PublicApiDoc.ReadPublicEntries` が可視性をリフレクションで判定するためである。

- **控えは `obj` だけに置き、`bin` へは出さない。**中身は非公開メンバーの日本語コメントを
  含む絞り込み前のものであり、配ってはいけないものそのものである
  (実測: `UiaTrigger.Core` は 620 対 317)。名前が違うので他のどの検査にも掛からず、
  混ざっても静かに配られる — `TheDocumentationFilesAreBuiltAndCopied` が出力側を数えている。
- **控えを書き直すかどうかは更新時刻で決める。**csc が `.xml` を書き直したなら絞り込み前、
  書き直していないなら中身は前回の絞り込み済みなので触ってはいけない。
  **絞り込み済みを保存したあとに控えを必ず押し直す** — でないと `.xml` のほうが新しくなり、
  次のビルドが「csc が書き直した」と誤判定して控えを絞り込み済みで潰す。
  この退行は**1 ビルド遅れて効く**ので、ja を削る回帰テストを 1 回やっただけでは捕まらない
  (退行 → 復元 → 2 ビルド → 退行、の順で確かめること)。
- **`Removed > 0` では判定できない。**ja からメンバーを落として再ビルドすると、
  再コンパイルが起きていなくても `Removed` は 1 になる (実測)。

### 配布する `.xml` は公開 API だけに絞る

**csc は可視性で絞らない。**`///` を書いた private / internal のメンバーも公開 API と同じ
`.xml` に並ぶ。内部コメントは日本語のままでよいと決めてある (docs/LOCALIZATION.md §1) 以上、
放っておくと**配布物に日本語の内部コメントが同梱される**。公開面だけを見る
`PublicApiDocumentationTests` はこれを捕まえない。

`Directory.Build.targets` の `KeepDistributedDocumentationPublic` が
`AfterTargets="CoreCompile"` で obj の doc を書き換え、**ja.xml に在る項目名だけを残す**。
「ja に在る項目 = 公開 API」は上記テストで CI が保証済みの集合なので、可視性を
リフレクションやメタデータ解析で作り直す必要が無く、**正しさを既存のテストから継承する**。

- **obj を書き換えるので bin / publish / nupkg のすべてに効く。**pack のときだけ絞る形にすると、
  発行物の zip (ホストの発行フォルダに `UiaTrigger.Core.xml` が並ぶ) が漏れたままになる
- 引き換えに、IVT を持つテストアセンブリ側の IDE で internal メンバーの説明が出なくなる。
  internal は他プロジェクトから見えないので、失うのはそこだけである
- 1 件も残らなかったら**ビルドを落とす**。黙って空の `.xml` を配ると利用者の IntelliSense から
  説明が全部消える。ja.xml が無いまま新しい packable プロジェクトが現れた場合も `Error` で
  止める — その `Error` の文言がこの節を指している。テスト表の側は
  `EveryAssemblyThatShipsXmlDocumentationIsCovered` が縛っており、対になっている

検査は 2 段で、片方だけでは塞げない: `TheDistributedDocumentationContainsNoNonPublicMembers`
がビルド出力の `.xml` に非公開項目が 0 件であることを見て、リリースワークフローが
**実際に配る `.nupkg` を開いて**中身を数える (docs/RELEASING.md §3)。
「絞ったファイルとは別のファイルが pack された」形は出力側の検査では捕まらないからである。

### ja.xml は nupkg にも入れる

`CopyToOutputDirectory` は bin に置くだけで **pack には効かない**。各 csproj の
`<None Link="ja\....xml">` だけだと、日本語の説明は ProjectReference の利用者にしか出ない。
NuGet の利用者に届けるため、`Directory.Build.targets` の `AddJapaneseDocumentationToPackage`
が `Resources\<アセンブリ名>.ja.xml` を `lib/<TFM>/ja/<アセンブリ名>.xml` として同梱する。

- **置き場所は dll の隣でなければならない。**IDE は
  `<アセンブリのフォルダ>\<カルチャ>\<アセンブリ名>.xml` を見るので、外れると
  **エラーも警告も出さずにただ出ない**。
- そのため `TargetsForTfmSpecificContentInPackage` ではなく
  **`TargetsForTfmSpecificBuildOutput`** を使う。あちらは `PackagePath` を自分で組むことになり、
  TFM のフォルダ名 (`net10.0-windows` ではなく **`net10.0-windows7.0`**) を外しても
  pack は成功して**静かに間違った場所に入る**。こちらは NuGet が dll を置く場所が基準で、
  `%(TargetPath)` がその下の相対パスになる。
- 配るのは**絞り込みの正そのもの**である。英語側は ja のキー集合で絞られるので、
  ja 自身に同じ絞り込みを掛ける意味は無い。

### Picker.WinUI だけは参照せずにメタデータで読む

上の検査は 5 つとも同じに掛かるが、**`Picker.WinUI` は T1 から参照できない。**あれは
`UiaTrigger.slnx` で `Platform=x64` に固定されており、参照すると AnyCPU の
`Core.Tests` ごと x64 になって Windows App SDK を引き込む。T1 は「UIA にも GUI にも
依らない」層なので、そこは崩さない (docs/TESTING.md §1)。

- 代わりに `MetadataLoadContext` でビルド出力を**読む**。コードは動かさないので x64 でも
  構わない — 見るのは可視性と属性だけである。
- そのため `PublicApiDoc` は **`typeof` との比較を使わない**。`IsDefined(typeof(...))` も
  `typeof(JsonSerializerContext).IsAssignableFrom(...)` も、実行中のランタイムに読み込んだ
  型としか照合できず、`MetadataLoadContext` から読んだアセンブリでは成立しない
  (**黙って false になる**)。属性も基底も**名前で**照合する。
- Windows App SDK 本体 (`Microsoft.WinUI.dll` / `WinRT.Runtime.dll`) は
  `Picker.WinUI` の `bin` には並ばない (実測で 5 ファイルのみ)。属性の型を解決するのに
  要るので、ソリューションビルドがそれらを置く唯一の場所である `App.WinUI` の出力から補う。
- **型ごと生成される道具の出力は対象にしない。**XAML マークアップコンパイラの
  `XamlMetaDataProvider` と CsWinRT の起動用の型は**公開**で出るので、落とさないと
  「ソースに存在しない型に日本語 doc を書け」と要求することになる。判定は
  `[GeneratedCode]` の**道具名**で行う — 属性の有無では割れない。`TriggerJsonContext` の
  ように手で書いた partial に生成された partial が付く型にも属性は付いてしまい、
  有無で判定すると本物の公開 API まで落ちる (実測)。

## §6 表示名と安定名

`UiaControlTypeNames.GetName` をリソース経由にしてはならない。戻り値は
`TriggerDefinition.DisplayName` と `TriggerProperty.ControlType` の比較形に入り、
どちらも**永続化される**。ここをローカライズすると、**表示を直したつもりで保存済みの定義の
意味が変わる** — 表示と比較の混在 (docs/LOCALIZATION.md §3) とまったく同じ壊れ方になる。

表示用の値は UIA の `LocalizedControlType` を別に持つ:

| | 識別用 | 表示用 |
|---|---|---|
| API | `ControlTypeName` / `UiaElement.Label` | `LocalizedControlType` / `UiaElement.DisplayLabel` |
| 出所 | `UiaControlTypeNames.GetName` (英語固定表) | UIA の `LocalizedControlType` プロパティ |
| 言語 | 常に英語 | **相手アプリのロケール** (こちらの UI カルチャではない) |
| 永続化 | する | **してはならない** |

「相手アプリのロケール」であることは直感に反するが、UIA の仕様どおりである。
ピッカーのプロパティ一覧が **2 つの名前を併記する**のはこのためで、表示名しか出さないと
「画面に出ている名前で `Equals` を書いたのに一致しない」が起きる。

往復回数は増やしていない。`LocalizedControlTypeProperty` は既存の `CacheRequest` に
足しただけで、`ReadSnapshot` も `UiaElement` の生成も 1 往復のままである。

回帰は 2 段構え: `ControlTypeNameTests` が分離の規則を両方向で固定し、
`ControlTypeNameScenarioTests` が**実 UIA が本当に別の文字列を返すこと**を確かめる。
後者が無いと、表示名が安定名と同じ文字列しか返さない環境で「2 つ目の名前を持っているが
誰も使っていない」状態に静かに戻れてしまう。

## §7 invariant の 2 種類

「invariant」という語が 2 つの別物を指すので区別する:

1. **`InvariantGlobalization` (プロセス全体のモード)** — これは必ず `false`。
   `true` にするとサテライトそのものが使えなくなる (docs/LOCALIZATION.md §2)
2. **`InvariantCulture` (呼び出しごとの書式選択)** — モードが `false` でも、
   invariant にすべき文字列は**箇所ごとに**明示的に固定する

後者の「invariant にすべきもの」も 1 種類ではない。ホストのコードには次の 3 つが
**同じ 1 つの文字列の中に混ざりうる**:

- **オプションの解釈** (`--duration 1.5` / `--point x,y`) — invariant。
  コマンドラインの意味が実行環境の言語で変わってはならない
- **ログの時刻** (`HH:mm:ss.fff`) — invariant。開発者向けログ (docs/LOCALIZATION.md §3 の 3)
  であり、grep して突き合わせるものである。カスタム書式指定子の `:` はカルチャの時刻区切りに
  置換されうるので、`string.Create(CultureInfo.InvariantCulture, ...)` で固定する
- **画面に出す数値** (「1.5 秒後に終了します」) — 現在のカルチャに従わせる

この 3 つは規則が違い、**混ぜても例外が出ない**。CI の aot ジョブが
`--culture de-DE --duration 1.5` を実行し、表示が `1,5` になり、同じ実行のログの時刻が
`HH:mm:ss.fff` のままであることを見ている。

時刻の書式はソースに残さない: ユーザー向け表示は `{0:T}` のように**書式指定子ごと
リソースに置く**。ソース側に `ToString("HH:mm")` と書くと、ja でも英語圏の時刻表記が出る。

## §8 README とリンクの非対称

**nuget.org は README を単独で描画するので、`docs/...` のような相対リンクは 404 になる。**
GitHub でしか読まないうちは気づけない類である。そのため:

- **英語版 (パッケージに入るほう) だけ
  `https://github.com/masa-iwm/UiaTrigger/blob/main/docs/...` の絶対 URL にする**
- 日本語版はリポジトリ内でしか読まれないので相対のままでよい
- **両者を「揃える」方向で直さないこと** — 意図的に違う。両方のファイルにその旨が書いてある

パッケージに入れるのは英語版だけである (`README.ja.md` は pack しない)。
`PackageReadmeFile` を立てるだけでは足りず、ファイル自体を Pack しないと NU5039 で
失敗する — この配線は `Directory.Build.targets` にあり、props でなく targets に置くのは
`IsPackable` がプロジェクト側で立つより後に評価される必要があるためである。

README の 2 言語には自動検査が無い。代わりに**どちらが正か** (英語版が正) を両方の末尾に
1 行で書いてある。ずれたときに「どちらへ寄せるか」で迷わないようにするためである。

## §9 配布から落とすもの

**Windows App SDK は自前の `.mui` (Win32 の MUI リソース) を 87 のカルチャフォルダで配る。**
`SatelliteResourceLanguages` が絞るのは .NET のサテライト (`*.resources.dll`) だけで、
`.mui` は管轄外である。放っておくと `App.WinUI` の発行物に en-us・ja-jp 以外の
**85 言語ぶん / 3.3 MB** が入り、その言語しか読めない利用者に
**対応しているように見える** (実際は WinUI のフレームワーク文字列だけがその言語で出る)。

落とし方は上流が用意している逃げ道を使う:
`Microsoft.WindowsAppSDK.SelfContained.targets` の
`AddMicrosoftWindowsAppSDKPayloadFilesFromComponents` が `**\*.mui` を `None` 項目として
集める前に、次の 1 行で除外を受け付けている:

```xml
<MicrosoftWindowsAppSDKFiles Remove="@(MicrosoftWindowsAppSDKFilesExcluded)"/>
```

**`MicrosoftWindowsAppSDKFilesExcluded` に入れる。`None` を後から削る形は取らない** —
あの一覧は `.dll` / `.winmd` / `.xbf` も運んでおり、後追いで消す形は取りこぼしと
行き過ぎの両方を作る。`Directory.Build.targets` の
`ExcludeUnsupportedWindowsAppSdkLanguages` がこれを実装している。
条件は `WindowsAppSdkComponentPackages` の有無で見るので、WinUI を参照しない
プロジェクトでは何もしない。

**列挙の向きが要点である。落とす側 (`**\*.mui` 全部) を先に列挙してから、残したい言語
(`en-us` / `ja-jp`) を差し戻す。**「残す側を列挙する」形にすると、言語フォルダの命名が
変わった日に**黙って全部落ちる**。こちらの形なら、変わったときに増えるのは「落とし損ね」で
あって「消しすぎ」ではない — 落とし損ねはサイズで気づけるが、消しすぎは UI が壊れて初めて分かる。

効果と範囲:

| 対象 | `.mui` |
|---|---|
| NuGet パッケージ 5 つ | 0 (最初から無関係) |
| `App.Wpf` / `App.WinForms` / `TestHost` | 0 (サテライトは `ja/` のみ、AOT は取り込み) |
| `App.WinUI` (絞る前) | 172 ファイル / 3.4 MB |
| `App.WinUI` (絞った後) | **4 ファイル / 0.07 MB** (`en-us` と `ja-jp` だけ) |

確かめていないことも記録する: `.mui` の中身は WinUI 自身のフレームワーク文字列
(組み込みコントロールのアクセシビリティ名など) であり、それが ja-JP で正しく出ることを
見る検査は無い。また OS の表示言語が落とした 85 言語のどれかのとき WinUI は `en-us` へ
フォールバックする — en-US と ja-JP しか持たないアプリなので、**それは意図した取引である**。
