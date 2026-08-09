# UiaTrigger パッケージ・CI・リリース

配布パッケージの構成、CI ジョブの構成、リリース手順と、その周辺の罠の正典である。
テスト層 (T1〜T6 / S4) の定義は docs/TESTING.md §1、設計判断そのものは docs/DESIGN.md にある。

## §1 パッケージ構成

配る NuGet パッケージは 5 つ。ライセンスは MIT。版数の正は `Directory.Build.props` の
`<Version>` である (§3 の版数ゲートがこれを前提にする)。

| パッケージ | 依存 |
|---|---|
| `UiaTrigger.Core` | `Microsoft.Extensions.Logging.Abstractions` |
| `UiaTrigger.Picker.Core` | → `UiaTrigger.Core` |
| `UiaTrigger.Picker.Wpf` / `.WinForms` | → `UiaTrigger.Picker.Core` |
| `UiaTrigger.Picker.WinUI` | → `UiaTrigger.Picker.Core` + `Microsoft.WindowsAppSDK` + `CommunityToolkit.WinUI.Controls.Sizers` |

**この表と README の依存表は同じものを指し、nuget.org の Dependencies 欄とも一致する。**
README は利用者に「何が入ってくるか」を案内しており、ずれると案内が嘘になる。
`Microsoft.Extensions.Logging.Abstractions` は `Core` 経由で**5 つ全部に届く** —
「第三者依存を持つのは `Picker.WinUI` だけ」と書くと、Microsoft 以外という意味であっても
利用者はそう読み分けない。

**この一致は `CentralPackageTransitivePinningEnabled` を切っていることで保たれている**
(docs/DESIGN.md D10)。有効にすると、推移的に届いたパッケージのうち `PackageVersion` の項が
あるものが直接参照へ昇格し、`Microsoft.Extensions.Logging.Abstractions` が 5 つ全部の
**直接依存として nuspec に載る** — nuget.org は同梱 README の依存表と Dependencies 欄を
同じページに並べるので、利用者の目の前で食い違う。切っても**利用者が入れるものは変わらない**
(実測: 復元は同じ版に解決する)。配る nuspec の依存集合は release.yml が数える (§3)。

**5 つ全部を配る。**README が「自分のアプリと同じ UI フレームワークの `Picker.*` を
参照すればよい」と案内している以上、どれかを欠くと案内が嘘になる。

**nuget.org へは札なしの `0.1.0` から出す。**プレリリース札 (`-preview.*`) は
**ドラフト Release と GitHub Packages での検証にだけ使う**もので、そこまでで役目を終える —
公開の Release を 1 つも出していないので、利用者から見れば `0.1.0` が最初の版である。

**`1.0.0` は出さない。**あれは「安定している」という約束であり、公開 API の再構成が
続くうちは名乗れない。`0.x` のあいだ破壊的変更がありうることは README と CHANGELOG が
告知しており、SemVer もそう読ませる。**`0.x` に札は要らない** — 札の役目
(「明示的に選ばない限り復元しない」) は、安定版と紛らわしい版を隠すことであって、
`0.x` そのものが既にその告知になっている。

### 5 つとも MSIL (AnyCPU) である

配るアセンブリはサテライトも含めて 7 つとも MSIL であり、利用者にアーキテクチャの
制約は掛からない。**これは `UiaTrigger.slnx` が配るプロジェクトを `Platform` に
固定していないことで保たれている。**

- **配布物は `dotnet pack UiaTrigger.slnx` (ソリューション pack) が作る。**slnx の
  `<Platform Project="x64" />` は pack にも効くので、**配るプロジェクトに付けた固定は
  そのまま `lib/` の dll になる**。`lib/` は RID を持たないため、ARM64 / x86 の利用者は
  **復元だけ通って読み込みで `BadImageFormatException`** になる — 復元時には警告も出ない。
  固定してよいのは配らないアプリ (`App.WinUI`) だけである。
  入口の網は T1 (`BuildInvariantTests.NoPackableProjectIsPinnedToAPlatformInTheSolution`)、
  出口の網は §3 の PE 検査である。
- **「WinUI 3 はアーキテクチャ固定を要求する」は誤りである** (実測)。WinUI 3 の
  **ライブラリ**にその要求は無く、`Microsoft.WinUI.dll` 自身も CommunityToolkit の Sizers も
  MSIL である。CI が毎 push で AOT 発行して起動している `App.WinUI` も、MSIL の
  `Picker.WinUI` の上に建っている。
- **測るなら配布物そのものを測る。**「`dotnet pack` は AnyCPU で建て直す」という実測が
  かつてここに書かれていたが、それは**パイプラインが使っていない pack 経路**で取った
  値だった。検査は必ず `.nupkg` の中身に掛ける。
- 同じ罠の裏返し: App を `-p:Platform=ARM64` で建てると `Platform` が `ProjectReference` を
  通って伝わり、`bin\ARM64\...\UiaTrigger.Core.dll` は ARM64 になる。これは配布物には
  掛からない (slnx が配るプロジェクトを固定していないため)。
  **`bin\ARM64` や `bin\x64` を見て配布物の機種を結論しないこと。**

### README の同梱は props ではなく targets で行う

`PackageReadmeFile` を立てるだけでは **NU5039** で pack が失敗する。あのプロパティは
「パッケージ内のどのファイルか」を指すだけで、`Pack="true"` でファイル自体を入れないと
中身が無い。同梱は `Directory.Build.targets` に置いてある — `IsPackable` を条件にするためで、
**props はプロジェクト本体より先に評価される**ので、そこではこの条件が読めない。

### 検証はプロパティではなく中身で行う

プロパティを立てただけでは検証にならない。pack して `.nupkg` を開き、license / readme の
同梱、パッケージ間依存 (project 参照が package 参照に変換されていること)、同梱アセンブリの
PE 種別を見る。件数と `.xml` の中身は release.yml が機械で検査する (§3)。

## §2 CI の構成

`.github/workflows/ci.yml` は 7 ジョブ。push / PR / `workflow_dispatch` で走る。

**外部の action は SHA で指す** (`actions/checkout@3d3c… # v7`)。タグは可変で、
乗っ取られたタグは**気づかれずに別のコードを走らせる** — CI は書き込み権限こそ持たないが、
ソースとビルド成果物には触れる。`# v7` のコメントは人が読むためのもので、解決には使われない。
SHA pin だけを入れると今度は更新が止まるので、`.github/dependabot.yml` を対で置く
(週 1 で SHA とコメントの両方を上げる PR が来る)。**片方だけにしないこと。**

| ジョブ | 内容 | 扱い |
|---|---|---|
| `build-and-test` | ソリューションビルド (`TreatWarningsAsErrors`) + T1/T2 | 必須 |
| `real-uia` | T3 (実 UIA) | 必須 (揺れ始めたら観測へ戻してよい — 1 行である) |
| `picker-ui` | T4 (ピッカー UI) | 必須 (同上) |
| `input` | T5 (合成入力) | 必須 (同上) |
| `aot` | AOT 発行 + ローカライズの実行検証 + S4 | 必須 |
| `arm64-build` | ARM64 のビルドのみ (§5) | 必須 |
| `lint` | 合成入力の規約 grep 3 本 (docs/TESTING.md §3) | 必須 |

対話セッションを要するジョブ (`real-uia` / `picker-ui` / `input`) も**必須である**。
新しく足すあいだだけ `continue-on-error: true` で観測し、連続実行の実測を根拠に必須化する
(docs/TESTING.md §1)。**観測扱いのジョブがあるあいだは、緑の一覧だけを見て通ったと
結論しないこと** — 観測ジョブの赤は CI 全体を止めないので、チェックマークには出ない。

なお、**既定ブランチに無いワークフローは `workflow_dispatch` の一覧に出ず**、`push` の
対象ブランチに入っていなければ push しても何も走らない。作業ブランチで回すあいだは
`push: branches` にそのブランチを足しておく (既定ブランチへ入ったら消してよい)。
タグ push は別の規則で動く (§3)。

### real-uia / picker-ui / input を別ジョブに分ける理由 — 同じデスクトップで互いを壊す

- **T3 と T4 は別ランナー。**どちらも画面に窓を出して座標から要素を掴むので、
  同じデスクトップで同時に走らせると互いの窓を掴む。
- **T5 は T4 とも別ランナー。**低レベルフックはグローバルで、撃ったキーは同じデスクトップの
  すべてのプロセスに届く。窓を分けても分離できない。

### picker-ui は表示解像度を 1920x1080 に設定する

- ランナー既定の 1024x768 では **T4 は原理的に成立しない**。`DesktopLayout.NarrowHost` は
  幅 1100px 固定 (デスクトップの広さで結果が変わるのを止めるための固定) で、制約は上下
  両方からある: 1024px 幅で使えるホスト幅は 620px、ツリーは約 322px となり「畳んだ 1 行が
  収まる」下限 346px を割るので、対照 (畳めばスクロール不要) のほうが壊れる。
  縮めた先に成立する幅が無いのでテスト側では直せず、**画面のほうを変える**。
  必要な幅は **1100 + 380 + 1 = 1,481px 以上**。余裕を見て 1920x1080 にする。
- 狭いまま走らせたときの失敗形: ホストの窓が pick 点を覆い、ピッカーは自プロセスを掴んで
  捕捉を飛ばし、`_lastCapturedPoint` により二度と捕捉しない = **原因を名指ししない
  30 秒タイムアウト**になる。
- 設定は `Set-DisplayResolution` (Windows Server 付属の ServerCore モジュール。`-Force` で
  非対話)。**読み取りは `GetSystemMetrics`** — ランナーの仮想ディスプレイでは
  `EnumDisplaySettings(ENUM_CURRENT_SETTINGS)` が 0x0 を返し、現在値を答えない (実測)。
  変更前と変更後を必ず印字する (「設定したつもり」を残さないため)。設定に失敗しても警告に
  留める — T4 側の `RequireTheNarrowHostFitsOnThisScreen` が必要な幅を px で名指しして
  落とすので、失敗の説明は失われない。

### aot ジョブの中身

- 4 つのホスト (TestHost / App.WinUI / App.Wpf / App.WinForms) を publish する。
  **4 つ揃っていることに意味がある** — 1 つでも欠くと、GitHub Release が「サンプルホスト
  3 つ」と称して **CI が一度も通していない経路の成果物**を配ることになる
  (「どの表にも載っていないから緑」の形)。
- AOT バイナリを実際に起動して、ローカライズ (neutral / ja サテライト)・カルチャと
  invariant の使い分け・`ILogger` 経由の診断ログを出力で検査する。ファイルの有無では
  判定しない — AOT はサテライトをネイティブイメージへ取り込み、`ja/` フォルダが
  出力されないことがある。
- S4 (`PublishedResourceTests` / `HostPublishedResourceTests`) をここで回す。**このジョブだけが `publish/` を作る**ので、
  発行物そのものが検査対象になる。picker-ui ではなくここに置くのは、S4 が座標も対象アプリも
  使わない — 画面を出すジョブの非決定性を継承する理由が無い — からである。
- 注意: GitHub Actions の pwsh シェルは、最後に走ったネイティブコマンドの `$LASTEXITCODE` を
  そのままステップの結果にする。**意図的に失敗させる定義を渡すステップは、明示的に
  `exit 0` で終わらないと、検証が全部通っていてもステップが赤になる。**

## §3 リリース手順

`.github/workflows/release.yml` が、タグ push (または `workflow_dispatch` + ref) で配布物を
建てて**ドラフトの** GitHub Release を作る。`ci.yml` と分けてあるのはトリガも権限も
違うためである (あちらは push / PR で走り、書き込み権限を要らない)。

順序の原則: **Release は消せる。NuGet publish は消せない。**Release は版数解決 → pack →
publish → 添付 → notes という配布の全工程を、後戻りできる形で一度通す。新しい秘密情報も
要らない (`GITHUB_TOKEN` で足りる)。だから NuGet へ push する前に必ず Release を通す。

- **Release はドラフトで止める。**公開は人が中身を見てから押す。
- **公開も NuGet publish もワークフローに入れない。**取り消せないものを「タグを打ったら
  自動で走る」経路に載せない。NuGet への push は、ドラフトの中身を人が確かめたあとに
  別途実行する。

禁じているのは**自動で走ること**であって、ワークフローを使うことではない。GitHub Packages
への発行は `workflow_dispatch` 専用のワークフローが行う (§6) — 押すのは人のままで、
`GITHUB_TOKEN` で足りるので新しい秘密情報を持たずに済む。**長期の NuGet API キーは
秘密情報として持たない。**将来 nuget.org への push を CI から出す必要が生じたときも、キーを
置くのではなく nuget.org の Trusted Publishing (GitHub Actions の OIDC を 1 時間だけ有効な
API キーへ交換する) を使う。あちらのポリシーは**ワークフローのファイル名**に紐づくので、
使う日には「どのファイルが押してよいか」がレジストリ側にも書かれることになる。

タグ push でワークフローが走るのは、ワークフローが**タグの指すコミットから**読まれる
ためで、既定ブランチに在る必要は無い (実測)。§2 の `workflow_dispatch` の制限と混同しないこと。
また、**履歴を書き換える rebase より前に打ったタグは、捨てられるコミットを指したまま残る**。
タグは履歴が確定してから打つ。

### 版数ゲート — タグ名は選べない

「タグ名 − 先頭 `v`」が `Directory.Build.props` の `<Version>` と一致しなければ落ち、
**以降のステップは全部スキップされる**。捨て名のタグ (`v0.0.0-smoke` など) は必ず落ちる。
正は props 側である。タグと props の二重管理を規律で解かない、というのがこのリポジトリの
流儀で (resx のキー集合一致などと同じ)、ずれたら機械が落とす。タグは消して打ち直せるので、
落ちて困ることは何も無い。

### 同名タグを打ち直すときは、古いドラフト Release を先に消す

タグ名は版数ゲートで固定なので、同じ版で建て直すには同名タグを動かすことになる。
**その前に古いドラフト Release を消すこと。**消さないと最後の `gh release create` が
「既に在る」で落ち、**添付が 1 つも付かないドラフトのまま**になる。ビルドも pack も
通ってしまうので、気づくのはダウンロードしようとしたときである。

### 配布物の検査

- **配布する `.nupkg` 内の `.xml` を、2 種類に分けて両方向から検査する。**ビルド出力側の
  検査 (T1) とは別に**実際に配る成果物の側**にも置く — 「絞ったファイルとは別のファイルが
  pack された」形は出力側の検査では捕まらない。

  | 配置 | 要求 |
  |---|---|
  | `lib/<TFM>/<名前>.xml` | 日本語が**1 文字も無い**こと。公開 API の doc は英語、非公開のコメントは日本語のままでよいと決めてあるので、この 1 つが「非公開メンバーの絞り込みが効いたか」をそのまま表す |
  | `lib/<TFM>/ja/<名前>.xml` | 日本語が**在る**こと。無ければ中身が入れ替わっている |

  **名前だけで数えない。**neutral と ja はファイル名が同じなので、1 つの集合で数えると
  同じキーになり、neutral が消えて ja だけ残っても「在る」で通る。分類できない配置の
  `.xml` は緑にせず名指しで落とす。両方の集合について、**5 パッケージ全部**が揃って
  いることを数える (「1 つも見つからなかったから緑」を塞ぐ)。
- **添付は 14 個ちょうど**: zip 4 (サンプルホスト 3 + TestHost) + nupkg 5 + snupkg 5 を
  件数検査する。§1 の「5 つ全部を配る」という決定が、ここで機械が確かめる形になっている。
  zip はホストごとに分ける — App.WinUI は self-contained + AOT で単体で大きく、まとめると
  「WPF だけ欲しい人が WinUI ぶんも落とす」ことになる。
- **配る `.dll` の PE ヘッダを読んで、7 つとも MSIL であることを数える** (docs/DESIGN.md D4/D5)。
  **件数の下限ではなく、期待する集合を名指しで数える** — lib の dll 5 + `Core` と
  `Picker.Core` の ja サテライト 2 で 7 である。「5 件以上」にすると、ja サテライトが
  黙って落ちても 5 件は残るので通ってしまう。欠けても余っても名指しで落とす。
  アーキテクチャの取り違えは復元した先で `BadImageFormatException` という無関係な顔で
  出るので、失敗メッセージには**パッケージ名だけでなく中のファイル名**を出す。
- **配る nuspec の依存とメタデータも数える。**依存は csproj からは読めない
  (推移的ピン留めで昇格したものが載るため — §1 / docs/DESIGN.md D10)。メタデータは
  `title` / `readme` / `releaseNotes` / `description` / `projectUrl` / ライセンス /
  `repository/@commit` と、`<version>` がタグと一致すること。**nuget.org は公開済みの版の
  表示を後から直せない**ので、空のまま出すとその版は永久に空である。
- **win-x64 のみ。**ARM64 は実行未確認なので配らない (§5)。ここで建てていないものは
  配らない。notes にもそう書く。
- リリースノートは英語で書く (公開面の言語は README と同じ規則)。

## §4 罠の台帳

- **S4 (発行レイアウトの 24 件 — `PublishedResourceTests` 18 + `HostPublishedResourceTests` 6) の緑は、発行物の新しさについて何も言わない。**
  S4 は `publish/` を起動するが、見るのはローカライズと発行レイアウトであり、枠も
  アイコンも押さない。**古い発行物のままでも緑になる。**一方、オーバーレイの検査 8 件 (`OverlayTests` 7 + `OverlayShadowTests` 1) は
  `bin/` 起動 = **AOT ではない**。つまり「T4 全緑」の内訳のうち、発行物 (AOT) の
  オーバーレイ挙動を裏付ける件数は 0 である。オーバーレイを AOT で確かめる道は、
  実マウスでの目視 (docs/MANUAL-CHECKS.md) か、オーバーレイの検査に発行物を起動する
  経路を新しく足すかの 2 つしかない。
- **手動確認は発行物 (AOT) で行う。**`bin/` は AOT ではないので、AOT でしか出ない不具合は
  原理的に出ない。実例: 自アセンブリの外にある値型 (`GridLength`) は AOT では CsWinRT が
  vtable を生成せず、WinRT の ABI を越える代入が**例外を出さずにただ動かない** —
  GridSplitter がカーソルは ↔ に変わるのにドラッグが効かない、という形で発行物でだけ現れる。
- **確認に使う zip が目当ての直しを含むことは、中身のバイト列で確かめる。**タグ →
  コミットの鎖だけで済ませない (例: 新設したウィンドウクラス名が exe に入っていることを見る)。
- **オーバーレイのクラス名は完全一致で絞ること。**`"UiaTriggerOverlay"` は
  `"UiaTriggerOverlayIcon"` の接頭辞なので、前方一致にすると枠を数えるつもりで
  アイコンまで数える (オーバーレイが 2 窓である理由は docs/DESIGN.md §10)。
- **zip 内の DOS タイムスタンプは時差を持たない。**ランナー (UTC) が書いた値を JST の
  機械で読むと、そのまま現地時刻として表示される。表示の `+09:00` を信じて 9 時間引くと、
  建ったばかりの成果物を古いと誤る。
- **GitHub のドラフト Release の `createdAt` は成果物の時刻ではない。**ドラフトはタグを
  実際には作らないので、`gh release view` には**対象コミットの時刻**が出る。建った時刻は
  zip の中のファイルで見ること (前項の罠に注意)。

## §5 ARM64

ビルドは CI で常設 (`arm64-build`)、配布はしない。

- `App.WinUI` を `-p:Platform=ARM64` で建てる。`RuntimeIdentifier` はハードコードではなく
  `Platform` から導く:

  ```xml
  <RuntimeIdentifier Condition="'$(RuntimeIdentifier)' == '' and '$(Platform)' == 'ARM64'">win-arm64</RuntimeIdentifier>
  <RuntimeIdentifier Condition="'$(RuntimeIdentifier)' == ''">win-x64</RuntimeIdentifier>
  ```

- **「建った」では検査にならない。**RID の導出が効かないと「ARM64 を指定したのに win-x64 が
  出る」で緑になるので、apphost の **PE ヘッダの機種を実際に読んで `0xAA64` (ARM64) を
  確かめる**。この導出は元へ戻しても x64 のビルドもテストも全部緑のままなので、
  このジョブを建てておかないと誰も気づかない。
- **実行確認が無いので配布しない。**`windows-latest` は x64 なので ARM64 バイナリは
  動かせない。ジョブ名に `no execution` と書いてあるのはそのためである
  (「建つこと」と「動くこと」を混同しない)。
- Native AOT の ARM64 クロス発行は ILCompiler が **MSVC の ARM64 ツールチェーン**を要求し、
  未確認のまま (無い環境では `Platform linker not found` で落ちる — 製品側の問題ではない)。
  マネージドのビルドと非 AOT の発行は通り、Windows App SDK の ARM64 資産も復元できる
  (実測)。**「ARM64 で AOT 発行できる」は測っていないので、そう書かない。**

## §6 GitHub Packages

nuget.org へ出す前に、レジストリ → 復元 → IntelliSense という**取得の経路**を実際のアプリで
一度通すための工程である。`.github/workflows/publish-packages.yml` が
`workflow_dispatch` だけで走り、**ドラフト Release に添付された `.nupkg` をそのまま押す**。
pack し直さない — 評価する意味があるのは nuget.org へ出す当のバイト列である (§4)。

- 認証は `GITHUB_TOKEN` で足りる。**新しい秘密情報を持たない。**パッケージとリポジトリの
  紐づけは nuspec の `RepositoryUrl` (`Directory.Build.props`) で決まる。
- 権限は `packages: write` に加えて **`contents: write`** が要る。**ドラフト Release は push
  権限を持つ相手にしか見えない**ので、`contents: read` では添付を数えるどころか一覧に出ず、
  `gh` が `release not found` で落ちる (実測)。読むだけなのに write が要る。
- `.snupkg` は押さない (`--no-symbols`)。GitHub Packages にシンボルサーバーは無い。
- `--skip-duplicate` は付けない。既に在る版を黙って飛ばすと「いま評価しているのはこのバイト列だ」
  という保証が消える。
- push のあと、**レジストリの応答で** 5 つとも当の版が引けることを数える。push が 1 件も
  走らなくても緑になる形を塞ぐため (§1 の「検証はプロパティではなく中身で行う」と同じ)。

### 版数は使い切りである

同じ版を上書きできない。消したあとに同じ版で出し直せるという保証も無い (GitHub の文書は
republish について何も書いていない)。**直したければ `Directory.Build.props` の `<Version>` を
上げて、タグから通し直す。**preview 札の番号は安いので、迷ったら上げる。

### 読むのにも認証が要る — ここの緑は「誰でも取れる」を意味しない

**リポジトリが公開でも、認証なしでは取れない。**`https://nuget.pkg.github.com/<owner>/index.json`
は service index からして **401** である (実測)。利用者側には `read:packages` を持つ
personal access token (classic) と、それを渡す `nuget.config` が要る:

```xml
<packageSources>
  <add key="github" value="https://nuget.pkg.github.com/<owner>/index.json" />
</packageSources>
<packageSourceCredentials>
  <github>
    <add key="Username" value="<owner>" />
    <add key="ClearTextPassword" value="<read:packages を持つ PAT>" />
  </github>
</packageSourceCredentials>
```

つまり**匿名で取得する経路は、nuget.org へ出すまで一度も通らない。**ここが緑でも
「利用者が何もせずに復元できる」は測れていないので、そう書かない。

### 配るものを直したら版を上げる

`.nupkg` の中身に効く直しは、**版を上げないと利用者に届かない**。GitHub Packages も
nuget.org も同じ版を上書きできないので、直した時点で次の版が要る。

**強調は `**…**` で書く。**コメントにも `///` doc にも HTML のタグを置かない。XML doc に
強調の記法は無く、未知のタグは中身だけが表示されるので、**混ざると利用者の IntelliSense に
そのまま出る**。

## §7 nuget.org

配布の最後の工程である。**§1〜§6 を全部通してからでないと押さない。**

順序の根拠は §3 と同じ「後戻りできる側から通す」で、ここが終端である:
Release は消せる → GitHub Packages は消せるが**版は使い切り** → nuget.org は
**消せず、ID も版も使い切り**。unlist は検索と既定の復元から外すだけで、
**版を明示した復元は通り続ける**。「unlist できるから消せる」と読まないこと。

### §7.1 押す前に揃っていること

- ドラフト Release を人が確認して**公開済み**である。ドラフトのままだと、nuget.org の
  ページから来た利用者にリリースノートが**一切見えない** (ドラフトは push 権限を持つ相手に
  しか見えない — §6 で実測した性質と同じ)
- 添付は 14 個ちょうど / CI が緑 (**観測扱いのジョブが 1 つも無いことも見る** — §2)
- 同じ版を GitHub Packages 経由で実アプリから復元して評価済み (§6)
- T6 を**発行物 (AOT)** で実施済み (§4 — `bin/` では原理的に出ない不具合がある)
- `Directory.Build.props` の `<Version>` = タグ名 − 先頭 `v` = これから押す版
- **作業ディレクトリに古い `.nupkg` が無い。**`artifacts-*/` のような置き場を作らない —
  `dotnet nuget push <dir>/*.nupkg` は打ち間違えても止まらない

### §7.2 押すバイト列を取る

**再 pack しない。**評価する意味があるのは出す当のバイト列である (§6 と同じ規律)。

```powershell
gh release download v0.1.0 --repo masa-iwm/UiaTrigger `
  --pattern '*.nupkg' --pattern '*.snupkg' --dir <空のディレクトリ>
```

- **`.snupkg` も落とす。**`dotnet nuget push a.nupkg` が隣の `a.snupkg` を一緒に押すのは
  **同じディレクトリに在るときだけ**である。落とし忘れると、**その版にシンボルは永久に付かない**。
  `publish-packages.yml` の `--pattern '*.nupkg'` を写さないこと — あちらが `.snupkg` を
  除いているのは GitHub Packages にシンボルサーバーが無いからで、nuget.org では逆になる。
- 件数 (nupkg 5 + snupkg 5) と、ファイル名の版が `<Version>` と一致することを数える。
- `Get-FileHash -Algorithm SHA256` で 5 つのハッシュを控える。**押したあとレジストリから
  落とし直して突き合わせる**ため (§7.6)。

### §7.3 押す口 — Trusted Publishing

**API キーをリポジトリにも GitHub Secrets にも置かない** (方針は §3 のまま)。押すのは
`.github/workflows/publish-nuget.yml` で、GitHub の OIDC トークンを nuget.org の
**短命の API キー**へ交換する (`NuGet/login`)。秘密情報は増えない — 交換を許すかどうかは
nuget.org 側のポリシーが決める。

- **`workflow_dispatch` 専用である。**タグ push では走らない。禁じているのは
  **自動で走ること**であってワークフローを使うことではない (§3)。押すのは人のままで、
  タグと nuget.org のユーザー名を入力する。
- **ポリシーはワークフローの*ファイル名*に紐づく。**nuget.org 側で
  「owner/repo の `publish-nuget.yml` からの発行を許す」と登録する。つまり
  **「どのファイルが押してよいか」がレジストリ側にも書かれる** — これが手キーより強い点である。
  **ファイル名を変えると発行できなくなる**ので、動かすときはポリシーも直すこと。
- 権限は `id-token: write` (OIDC) と **`contents: write`**。ドラフト Release は push 権限を
  持つ相手にしか見えないので、読むだけなのに write が要る (§6 と同じ実測)。
- **「まだ存在しない ID の初回 push」も通る** (実測)。`UiaTrigger.*` の 5 つはどれも
  nuget.org に存在しない状態から、ポリシーだけで発行できた — 手キーは一度も使っていない。
  手キーが要るのはポリシーを作れない事情があるときだけで、そのときも
  `dotnet nuget setapikey` は使わない (`%APPDATA%\NuGet\NuGet.Config` に平文で残る)。

### §7.4 押す

ワークフローがやることは次のとおりで、手で押すときも同じ順である。

- **V3 の口へ押す** (`https://api.nuget.org/v3/index.json`)。**シンボルの push は V3 でしか
  動かない** — V2 (`www.nuget.org/api/v2/package`) へ押すと nupkg だけが上がり、`.snupkg` は
  **エラーも出さずに落ちる**。
- **`--no-symbols` を付けない。**`publish-packages.yml` から写さないこと (§7.2 の理由)。
- **`--skip-duplicate` を付けない。**既に在る版を黙って飛ばすと「いま出しているのは
  このバイト列だ」という保証が消える。
- **ワイルドカードを渡さない** (展開されないのは §6 で実測)。1 件ずつ、どれで落ちたかが
  読める形で押す。
- **依存の順に押す**: `Core` → `Picker.Core` → `Picker.Wpf` → `Picker.WinForms` →
  `Picker.WinUI`。逆にすると、索引に出た直後に依存が解決できないパッケージが公開される
  窓ができる。

### §7.5 押したあとに起きること

- **nupkg は即座に永久である。**削除はできない。unlist しても版を明示した復元は通る。
- **検証は非同期である。**push が返った時点ではまだ検証中で、索引に出るまで時間が要る。
  **検証に失敗しても nupkg は戻らない。**
- **`.snupkg` の検証は nupkg が確定したあとに走る。**シンボルだけが落ちて
  (非ポータブル PDB / SourceLink 欠落 / nupkg と不一致)、**シンボルの無い版が永久に残る**
  ことがある。**この経路はこのリポジトリで一度も通っていない** (§6 は `--no-symbols`) —
  初通しであることを承知して押す。
- **押した瞬間に ID の所有権と綴りが確定する。**打ち間違えた ID は取り戻せない。
  押す前にファイル名を読み上げること。
- 表示される README は**パッケージに入っているもの**である。あとでリポジトリの README を
  直しても、公開済みの版の表示は変わらない。
- **検索索引は遅れる。**検索に出ないことを「押せていない」と読まない —
  registration / flat container が真である。

### §7.6 押したあとに確かめること

- `https://api.nuget.org/v3-flatcontainer/<id を小文字>/index.json` に **5 つとも**当の版が在る。
- **落とし直して中身が §7.2 と一致する。**「押したものが配られている」をバイト列で確かめる。
  **パッケージ全体のハッシュは一致しない** — nuget.org は配信時に**リポジトリ署名**を足すので、
  `.signature.p7s` が 1 つ増えて大きさも変わる (実測: 215,347 → 228,433 バイト)。
  比べるのは**エントリの集合 (`.signature.p7s` を除く) と中身のハッシュ**である
  (実測: `lib` の dll は完全に一致した)。全体のハッシュで比べると毎回誤報になる。
- 5 つのページで: README が描画される / **Dependencies パネルと README の依存表が一致する** /
  ライセンスが MIT と出る / Source Repository のリンクが効く。
- **シンボルはページの表示ではなくシンボルサーバーの応答で確かめる。**「登録されたように見える」と「デバッガがソースへ入れる」は別のことである。
  配る `.dll` のデバッグディレクトリから鍵を組んで引く:
  `https://symbols.nuget.org/download/symbols/<pdb 名>/<CodeView の GUID を 32 桁大文字>FFFFFFFF/<pdb 名>`
  (portable PDB の age は常に `FFFFFFFF`)。**`SymbolChecksum` ヘッダーが要る** —
  付けないと **403 が返る**。あれは「無い」ではなく「その形では答えない」である (実測)。
  値はデバッグディレクトリの `PdbChecksum` から `<アルゴリズム>:<16 進>` で作る。
  さらに取れた PDB の SourceLink の地図を読み、**その URL が実際に引けること**まで見る —
  地図がタグの指すコミットを指していなければ、シンボルは在ってもソースへは入れない。
  落ちていたら**次の版で直す** (その版のシンボルは戻らない)。
- **匿名で復元できること。GitHub Packages では一度も測れなかった経路である** (§6)。
  素のディレクトリに新規プロジェクトを作り、nuget.org だけを見る `nuget.config` で
  `dotnet add package UiaTrigger.Core` が通ること。**`--packages` で別のフォルダを指すか、
  先にキャッシュを外す** — `%USERPROFILE%\.nuget\packages` に同じ版が残っていると、
  レジストリから取れなくても緑になる。

### §7.7 出したあとに直すには

版は使い切りである。上書きも再 push もできない。**`Directory.Build.props` の `<Version>` を
上げて §3 → §6 → §7 を通し直す。**誤って出したものは unlist する —
unlist は「無かったこと」ではない。
