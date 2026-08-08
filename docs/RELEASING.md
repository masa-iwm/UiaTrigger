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

**この表と README の依存表は同じものを指す。**README は利用者に「何が入ってくるか」を
案内しており、ずれると案内が嘘になる。`Microsoft.Extensions.Logging.Abstractions` は
`Core` 経由で**5 つ全部に届く** — 「第三者依存を持つのは `Picker.WinUI` だけ」と書くと、
Microsoft 以外という意味であっても利用者はそう読み分けない。

**5 つ全部を配る。**README が「自分のアプリと同じ UI フレームワークの `Picker.*` を
参照すればよい」と案内している以上、どれかを欠くと案内が嘘になる。

版数にはプレリリース札 (`-preview.*`) を付けたまま出す。公開 API の再構成が続いており、
**`1.0.0` は「安定している」という約束**なので出さない。プレリリース札があれば、
利用者が明示的に選ばない限り NuGet は復元しない。

### 5 つとも MSIL (AnyCPU) である

- `UiaTrigger.slnx` は WinUI を `Platform=x64` に固定しているため、ソリューションビルドの
  出力 (`bin\x64\...`) は Amd64 である。しかし `dotnet pack` は AnyCPU で建て直すので、
  **パッケージの中身は 5 つとも MSIL** (実測)。利用者に x64 の制約は掛からない。
  **`bin\x64` を見て「x64 のパッケージだ」と結論しないこと。**
- 同じ罠の裏返し: App を `-p:Platform=ARM64` で建てると `Platform` が `ProjectReference` を
  通って伝わり、`bin\ARM64\...\UiaTrigger.Core.dll` は ARM64 になる。これも配布物には
  掛からない — パッケージは `Platform` 無指定の `dotnet pack` が作るので MSIL のままである。
  **`bin\ARM64` を見て「Core が ARM64 になってしまった」と結論しないこと。**

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
| `lint` | 合成入力の規約 grep 2 本 (docs/TESTING.md §3) | 必須 |

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
- **win-x64 のみ。**ARM64 は実行未確認なので配らない (§5)。ここで建てていないものは
  配らない。notes にもそう書く。
- リリースノートは英語で書く (公開面の言語は README と同じ規則)。

## §4 罠の台帳

- **S4 (発行レイアウトの 20 件 — `PublishedResourceTests` 14 + `HostPublishedResourceTests` 6) の緑は、発行物の新しさについて何も言わない。**
  S4 は `publish/` を起動するが、見るのはローカライズと発行レイアウトであり、枠も
  アイコンも押さない。**古い発行物のままでも緑になる。**一方、オーバーレイの検査 54 件は
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
