# UiaTrigger — 設計と判断の台帳

この文書は UiaTrigger の設計の不変条件と、その判断の根拠を現在形で保存する正典である。「どう動くか」はコードが語るが、「なぜそうなっているか」「どう変えると何が壊れるか」はここにしか残らない。§13 の台帳は所見 ID (A / B / C / D / L / S) を安定アンカーとして全て保存しており、コード・テスト・他文書からの参照はこの ID と本文書の節番号に対して行う。検証の層 (T1〜T6) と入力の政策は docs/TESTING.md、ローカライズは docs/LOCALIZATION.md が正である。

---

## §1 決定事項

| 項目 | 決定 |
|---|---|
| 対象 SKU | C# / .NET 10 / WinUI 3 / Native AOT 発行対応の、**他アプリ UI 要素監視ライブラリ**。変更しない |
| 配布形態 | **NuGet パッケージ 5 つ**: `UiaTrigger.Core` (依存なし) / `UiaTrigger.Picker.Core` (→ Core) / `UiaTrigger.Picker.WinUI` / `.Wpf` / `.WinForms` (→ Picker.Core)。README が「自分のアプリと同じ UI フレームワークの `Picker.*` を参照する」と案内している以上、5 つとも配らないと案内が嘘になる。サンプルホスト 3 つと `TestHost` は NuGet では配れないので GitHub Releases の zip で配る |
| パッケージの中身 | **5 つとも MSIL (AnyCPU)**。`dotnet pack` は AnyCPU で建て直すため、ソリューションビルドの `bin\x64` を見て「x64 のパッケージだ」と結論しないこと。利用者に x64 の制約は掛からない |
| 版数 | `0.1.0-preview.1` から。**`1.0.0` で出すことは「安定している」という約束**であり、公開 API の再構成が続くうちは出さない。プレリリース札があれば利用者が明示的に選ばない限り復元されない |
| 過去互換 | **0.x のあいだは持たない** (安定版で互換方針を立て直す)。旧形式の判別処理そのものを持たない — 読み替えるべき過去のファイルが公開版には存在しないため。代わりに**版数だけは最初から書き込む** (`TriggerJson.FormatVersion = 1`)。版数の無いファイルは後から見分けようがなく、それを増やさないことだけが将来の移行を可能にする。`FormatVersion` は 1 のままでよく、「既存 JSON がバイト単位で変わらない」は目標にしない |
| `InternalsVisibleTo` | **製品アセンブリ向けは持たない** (テストアセンブリに限る)。詳細は §12 |
| ライセンス | MIT |
| ローカライズ | **en-US をプライマリ (neutral)、ja-JP をサテライトで追加**。公開 API の XML doc・例外メッセージ・UI 文字列すべてが対象。`README.md` = 英語 (正) / `README.ja.md` = 日本語。詳細は docs/LOCALIZATION.md |

---

## §2 維持する設計

以下は一般的な .NET / UIA コードより進んでおり、**変更しない**。

| 項目 | 根拠 |
|---|---|
| 手書き `[GeneratedComInterface]` による UIA interop | `UIAutomationClient.h` と vtable 順を突き合わせて検証済み。`IUIAutomation` (55 メソッド = IUnknown 込み 58 slot) / `IUIAutomationElement` (82 メソッド = 85 slot)。未使用 slot は `nint` で埋めて ABI 整合を保つ。`IUIAutomation2` を継承で足す都合上、数が 1 つずれると**別スロットを呼ぶ**のにコンパイルも AOT 発行も通る — `InteropShapeTests` が数と順序を固定する |
| CsWin32 の COM 生成を使わない | CsWin32 は `[ComImport]` を出力し Native AOT で機能しない |
| `DisableRuntimeMarshalling` を Core に閉じ、Picker を別アセンブリにする | CsWin32 の `SetLastError=true` な `DllImport` と非互換。Core は `LibraryImport` 手書き、Picker 側は CsWin32 (`allowMarshaling:false`) という住み分け。属性は**アセンブリ単位**なので、宣言しない `Picker.Core` は CsWin32 を保ったまま AnyCPU でいられる (§12) |
| `ComVariant` を保持せず要素から再読取する | `HandlePropertyChangedEvent` の VARIANT はコールバック復帰後に解放される。保持すると use-after-free になる |
| `EnumWindows` の `[UnmanagedCallersOnly]` 関数ポインタ + `GCHandle` | デリゲートを使わない AOT 適合の形 |
| `Marshal.Release` を `GetOrCreateObjectForComInstance` と `finally` で対にする規律 | 参照カウント漏れを作らない |
| Regex は `NonBacktracking` + タイムアウト | ReDoS 対策。ただし `NonBacktracking` は後方参照・先読み・後読みに対し「文法としては正しいが使えない」ため **`ArgumentException` ではなく `NotSupportedException` を投げる** (A20)。検証はこれも定義の誤りとして捕まえる — `ArgumentException` だけを捕まえる形に戻すと、ローカライズされない別種の例外が呼び出し元へ漏れる |
| 検証・Regex コンパイルを呼び出し元スレッドで前倒し | 設定ミスが `StartAsync` で即座に `ArgumentException` になる |
| Picker の各種ワークアラウンド | `args.AddedItems` 優先 / `IsExpanded` 駆動の遅延列挙 / 重なり切替で `NormalizeElement` を意図的に飛ばす — いずれも根拠コメント付きで再発防止価値が高い |

---

## §3 セッションと監視の構造

### UiaSession — 1 セッション = 1 MTA

Core は「監視ライブラリ」ではなく「**UIA セッション + 監視**」である (C2)。

```
UiaSession (public, IAsyncDisposable)   … 1 セッション = 1 MTA ディスパッチャ + IUIAutomation
  ├─ UiaElement (public, IDisposable)   … RCW を包む不透明ハンドル。COM 型は公開しない
  ├─ ElementFromPointAsync / ElementFromCursorAsync / GetRootAsync
  ├─ GetChildrenAsync / GetAncestorChainAsync / GetOverlapStackAsync
  ├─ IndexOfAsync / AreSameAsync
  ├─ ReadSnapshotAsync (CacheRequest 1 往復)
  ├─ BuildDefinitionAsync / BuildDefinitionFromPointAsync / …FromCursorAsync
  ├─ CoordinateProblem                  … ホストが PerMonitorV2 でないことの実行時診断 (§9)
  └─ CreateMonitor(options) → TriggerMonitor    … セッションを共有できる
```

- 要素ツリーの探索・ヒットテスト・スナップショット・座標からの記録は Core の公開機能である。これが無いと第三者は自前のピッカー / インスペクタを作れない。専用の型や MTA スレッドを別に立てる理由は無く、**MTA スレッドはプロセスに 1 本**である (オーバーレイも同じ判断を守る — §10)
- **公開メンバーのシグネチャに interop 型は現れない。**`UiaTrigger.Interop` / `UiaTrigger.Threading` は公開型を持たない (C7)。「不透明ハンドル」という設計は 1 箇所漏れれば意味を失うので `PublicApiTests` が縛る
- interop の out は **nullable** で宣言する (A15)。UIA は `ElementFromPoint` / `ElementFromHandle` で null を返しうるため、非 null 宣言は例外化の経路を作る
- `StartAsync` の `CancellationToken` は「実行開始前のみキャンセル可」として honor する (A16)。偽の affordance を置かない

### 往復の規律 (B1) と候補キャッシュ (B4)

- プロパティ読取は個別のクロスプロセス呼び出しにせず **`CacheRequest`** でまとめる。`FindAllBuildCache(TreeScope.Children)` + `get_Cached*` で**段あたり 1 往復**、`BuildUpdatedCache` でスナップショット **1 往復**。ピッカーの要素取得も識別 + 表示用プロパティをまとめて読み、要素あたり 1 往復である
- ウィンドウ列挙 + `OpenProcess` は sweep ごと・トリガーごとに繰り返さず、同一 `WindowIdentity` のトリガー間で **`WindowCandidateCache`** を共有する。**照合の強さ (`MatchStrength`) はキャッシュのキーに含める** — 同じ文字列でも Required と Preferred では候補集合が違い、含め忘れると別条件のトリガーが他人の候補リストを使う
- 昇格プロセスは黙って落とさない (A10)。`InaccessibleProcessCount` を数え、`ResolutionChanged.Message` に理由として載せ、`ILogger` にも出す。非昇格クライアントからは `OpenProcess` が失敗して候補から消えるだけになり、理由の出口が他に無い
- 兄弟走査はキャッシュ済みの識別属性で絞ってから `CompareElements` を呼ぶ (B9)

### 応答しないアプリへの耐性 (B5)

- `IUIAutomation2.put_TransactionTimeout` を既定で設定する。`IUIAutomation` しか QI しないとここに到達できず、**応答しないアプリ 1 つで全トリガーが無期限停止**する。設定口は `UiaSessionOptions` に集約し、効いているかは `GetSupportsTimeoutsAsync()` で確認できる
- **別アプリのトリガーの発火経路は、塞がれたアプリを一度も通らない。**発火はプロパティ変化のコールバックで届きその場で句が評価されるので、ディスパッチャーが別アプリへの呼び出しで塞がっていても発火はその後ろに並ばない (実測: 一方を 10 秒塞いでも他方は 19〜119ms で発火)
- 「塞がれたトリガーが未解決になる」性質は**持っていない**。持たせるかどうかは設計判断であり、テストで要求すべきものではない
- 塞がれたことは解決を恒久的に汚染しない。塞ぎが明けて要素が戻れば解決して発火する

### トリガーモデル (C3〜C6)

```csharp
public sealed class TriggerDefinition
{
    public required string Id { get; set; }                         // キーを内包
    public string? DisplayName { get; set; }
    public WindowIdentity Window { get; set; }
    public ElementLocator Locator { get; set; }
    public TriggerOn On { get; set; }                               // ライフサイクル
    public ClauseCombinator Combine { get; set; }
    public List<PropertyClause> Clauses { get; set; }               // 複数条件 (平坦)
    public string? Expression { get; set; }                         // 入れ子は文字列で (§4)
    public TimeSpan? MinInterval { get; set; }                      // 発火レート制限 (C11)
    public TimeSpan? PollInterval { get; set; }                     // §5
}
```

- ライフサイクル (`TriggerOn`) と値の述語 (`PropertyClause`) は分離する。混ぜると「要素が出現し、かつ Value が X のとき」が表現できない
- 句リストは**非再帰・平坦**にとどめる。「属性・コンバータ不要の POCO」を保ち STJ source-gen / AOT 適合を維持するためで、入れ子が要る場合も POCO の入れ子にしない (§4)
- `TriggerProperty.Custom` + `CustomPropertyId` が UIA の数百のプロパティへの逃げ道である
- **列挙の永続形式はモデル自身に持たせる** (C8)。`JsonSourceGenerationOptions` の設定はホストが `TypeInfoResolverChain` で合成した経路には引き継がれない (実測) ため、列挙型に `[JsonConverter(typeof(JsonStringEnumConverter<T>))]` を付ける。POCO 方針からの唯一の逸脱だが、永続形式は「誰がシリアライズするか」ではなくモデルの性質であり、数値のままだと**列挙メンバーを 1 つ挿入しただけで保存済みファイルの意味が変わる**
- 数値の `Equals` は `PropertyClause.Tolerance` を持つ (A12) — double の厳密比較は実用上一致しない。bool の比較形は `true` / `false` で、大小文字を問わず bool として解釈する (A13)
- パスワード欄は伏せる (C12)。`IsPassword` なら `Value` と `Name` を伏字化する — プロバイダーによっては `Name` 側に値が出る。伏字化はスナップショット生成の中で行うので往復は増えず、条件評価もスナップショット経由なので伏せた値が比較経路で復活しない
- 保存は `AtomicFile.Write` (temp + `File.Replace`) で行う (A17)。truncate-in-place は書込中クラッシュで全トリガーを失う

### 要素識別 — 必須述語 + ランキング + ビーム探索 (A3〜A7)

**揮発性の属性 (タイトル) と安定属性 (クラス名) と位置情報 (兄弟インデックス) を同じスコア空間に足し込み、合計に閾値を置いてはならない。**その形は「安定属性が 1 つ変わっただけで恒久的に解決不能」(A4: WinForms のクラス名は起動ごとに変わる token を含む) と「兄弟が 1 個増えただけで全体が失敗」(A3) を作る。

- **ウィンドウ照合は `MatchStrength { Required, Preferred, Ignored }` の宣言**。スコアは候補の順位付けにのみ使い、足切りは Required の属性だけで行う。`Required` の属性が空の定義は「条件が無いので全ウィンドウが一致」ではなく**候補なし**とする (`HasUnsatisfiableRequirement`)。開いた側に倒すと別のウィンドウを静かに掴む
- **合否は「経路全体の合計スコア」ではない。**合計に閾値を置くと、深い経路で 1 段だけ完全に外れても他の段の得点で埋め合わされる (実測 17 段の経路では 1 段の減点が合計比でほぼ効かない)。採っているのは**候補ごとの足切り** `StepAcceptScore = 0` — 「記録された属性がその候補を肯定する度合いが差し引き非負か」だけを見る
- `ControlType` 不一致は除外ではなく重い減点 (A5)。`AutomationId` 一致が上回ることは重みの不等式 `StepAutomationIdScore + StepControlTypeMismatchPenalty >= StepAcceptScore` に依存しており、崩れると A5 が黙って元に戻る (`BeamSearchTests.ScoreStep_AutomationIdMatchOutweighsAControlTypeMismatch` が固定)
- `SiblingIndex` は**同点時のタイブレークのみ**。「不明」は `-1` で表現し、黙って 0 を記録しない (A7)。`ElementPathStep.ClassName` を持つ — Win32 では極めて安定した識別子で、`AutomationId` も `Name` も無い段の唯一の手掛かりになる
- **ビーム探索** (既定幅 3) でバックトラック可能にする (A6)。対価は「段あたり往復数」が「候補 1 件あたり 1 往復」に緩むこと (幅 3 × 深さ 15 で最悪 45 往復)。ビームは経路の前半を共有するので、段ごとに配ったノードをその場で解放すると**二重解放**になる — 落とした候補はその場で、生き残りは探索終了時に「勝ち残った経路以外」をまとめて解放する

### Search 方式

対象がウィンドウ内で一意な `AutomationId` を持つなら `FindAll` 1 発で解決する。実装の規律:

| 判断 | 理由 |
|---|---|
| `FindFirst` ではなく **`FindAll` の上限 2 件** | 一意性は解決時にも確かめる。0 件と 2 件以上はどちらも Search を使わない |
| 一致が 2 件以上なら**経路方式に落ちる** | 先頭を採ると「記録時は一意だった id が重複するようになった」ときに黙って別の要素を掴む。例外にもならず最も見つけにくい壊れ方になる |
| 記録時も**実際に数えて**一意性を確かめる | 「一意だろう」で記録した定義は解決時に静かに間違う。記録側でしか防げない |
| **経路も残す** | Search は速くて上の構造変化に強いだけで、一意性は将来にわたって保証されない。退避路が要る |
| 祖先を `GetParent` で遡り、**経路方式と同じ形の鎖を返す** | 経路購読 (§6) は各段を要求する。ここで手を抜いて「Search のときはウィンドウ全体を購読」にすると、購読スコープで得たものを静かに失う |
| ウィンドウに行き着かなければ**採らない** | 別ウィンドウの要素が引っかかった場合にウィンドウ照合を素通りさせない |

往復回数は `1 + 深さ` (経路方式は最悪 ビーム幅 × 深さ)。使わない永続化フィールドは置かない — 使われないフィールドは、あとで意味が変わる余地になる。

### 発火経路 — 直列 + 例外隔離 (A1 / A2)

- 発火・解決通知は**単一のバックグラウンドワーカー**が順に配る。発火ごとに独立の work item を投げる形は順序を保証しない (A2) うえ、ハンドラの例外が未処理例外としてプロセスを落とす (A1)
- ハンドラの例外は捕捉して `UnhandledException` へ回す
- `IAsyncEnumerable` 版は持たない。順序保証と例外隔離は満たしており、背圧が要る用途が現れてから非破壊に足せる
- イベントは UI スレッドでは来ない。ホストは Dispatcher へ渡し直す (§12)

### 停止・ログ・時計

- **停止は張った単位で外す** (C1)。`RemoveAllEventHandlers()` は使えない — セッションを共有すると他の購読者 (ピッカーや別の `TriggerMonitor`) の購読まで一緒に消える。トリガーの増減は `AddAsync` / `RemoveAsync` で行い、全停止 → 全再開を要求しない
- ログは `Microsoft.Extensions.Logging.Abstractions` を**唯一の実行時依存**として取る (C9)。独自のログ抽象はホストの `ILogger` への橋渡しを全利用者に押し付ける。`[LoggerMessage]` 生成でログ無効時の割当ゼロ、AOT でも動く。**ログのメッセージは英語固定** — ユーザーに見せる文字列 (`ResolutionChanged.Message` 等) はリソース経由で、この 2 つを混ぜない (docs/LOCALIZATION.md)
- 時計は `UiaSessionOptions.TimeProvider` を注入する (C10)。発火時刻・レート制限・デバウンスがすべてこの時計に従う。デバウンスは `TimeProvider.CreateTimer` (B8)。値 (timestamp) だけ渡す形にしない — 経過時間の計算は時計の周波数を知っている側でしかできない
- sweep のデバウンス状態は `SweepDebouncer` に封じ、停止時に `Reset()` する (A14)。Stop → 即 Start でデバウンス窓内の要求を落とさない
- `COMException` は判別する (A11)。「要素消滅」の判定は `ComErrors.IsElementGone` に集約し、`UIA_E_ELEMENTNOTAVAILABLE` だけでなく `RPC_E_DISCONNECTED` 等も含める。**「比較できない」は「死んでいる」ではない** — `COMException` は相手が塞がれているだけでも出る (§8 の再購読の規律がこれに依存する)

### 診断

`TriggerMonitor.GetDiagnostics()` → `TriggerMonitorDiagnostics` は public である。このライブラリは `COMException` を握り潰す設計なので、「トリガーが鳴らない」ときに利用者が見られるのは購読の形と受信件数しかない。ログは流れていく情報で「今どうなっているか」は答えられない。`StructureEventCount` は**否定形の性質 (余分なイベントが来ないこと) を外から観測する唯一の手段**であり、`OrphanedTriggerCount` は §8 の閉路を一目で読むための値である。

---

## §4 複合条件とスロット

### 解決の単位 = 実効 (Window, Locator) = ElementSlot

句は実効 `(Window, Locator)` (句が持てば句のもの、null ならトリガーの既定) でまとめ、その単位 `ElementSlot` ごとに解決・購読する。**まとめ方を間違えると、1 要素にプロパティハンドラが 2 本立って 1 回の変化で 2 回発火する** — ただの無駄ではなく誤りである。

- **参照同値は鍵に使えない。**`WindowIdentity` / `ElementLocator` は `Equals` を持たない可変クラスなので、JSON から読んだ「同じ要素を指す 2 つの句」は別オブジェクトになる
- **手書きの構造比較も使えない。**プロパティが 1 つ増えた日に、増えたぶんを見落として「違う要素を同じと見なす」形で黙って腐る
- 鍵は実効 `(Window, Locator)` を `TriggerJsonContext` で**文字列化**して作る。全プロパティを自動で覆い、型を増やさない。`EveryWindowIdentityProperty_TakesPartInTheSlotKey` (リフレクションで各プロパティを 1 つずつ変えてスロットが割れることを見る) がプロパティの増加に対して腐らない網である

### 入れ子は式、多要素は句の上書き

- 「(A∧B)∨(C∧¬D)」の類は **`TriggerDefinition.Expression` という 1 本の文字列**で書く。`&&` / `||` / `!` と括弧、優先順位は `!` > `&&` > `||`。木は実行開始時に組んで**永続化しない**ので、判別子もコンバータも `[JsonDerivedType]` も増えない — §3 の平坦の判断は覆っていない
- 多要素 (「A のボタンが有効 かつ B のラベルが完了」) は `PropertyClause.Window?` / `Locator?` の null 許容の上書きで書く
- 採らなかった案と理由 (再検討時にここから読むこと):
  - 入れ子の POCO + `[JsonDerivedType]` — 避けたはずの属性・コンバータが戻り source-gen に乗らない
  - トリガー合成 (名前で他トリガーを束ねる) — 循環検出・位相順序・「参照中の削除」の規則が丸ごと増える。式なら 1 トリガーの中で閉じる
  - `ClauseCombinator.NotAny` — `!` が式にあるので要らない。JSON の `"Combine": "None"` が「結合しない」と誤読される
  - `+ * !` / `AND(OR(0,1))` 構文 — 説明されずに正しく推測できるのは `&&` `||` `!` だけである
  - **句を位置で指す** (`(0+1)*(2+!3)`) — 句を 1 つ削っただけで式は妥当なまま別の意味になり、例外も警告も出ない。名前なら並べ替えにも削除にも耐える

### Watch — 発火源か、絞るだけか

`On` を句に降ろす形は採らない。`On` は「トリガーが評価され発火しうる**瞬間**」であり、句に降ろすと「いま成立しているか」という**水準**へ意味が変わる。1 つの列挙が場所によって別の意味を持つのは負債である。presence / absence は `!` で書けるので水準としての `On` は要らない。

それだけでは穴が残る。購読は `clause.Property` から作られ **`clause.Op` を見ない**ので、「A が存在している」を絞りのつもりで書いた句も**そのプロパティの変化ごとに発火源になる**。足りないのは瞬間でも水準でもなく、**この句の変化が発火源になるのか、絞るだけなのか**という直交した軸 — それが `PropertyClause.Watch` である。

> **`Watch` は購読を減らすだけの最適化ではない。**そう読むと「どうせ購読は安い」と言って
> 消される。`Watch=false` の句を消すと「絞るだけのつもりの句が発火源に変わる」形で
> 定義の意味が黙って変わる。

### 未解決スロットを待たない / 評価は原子的ではない

- 全スロットが揃うまで評価を止めると **`!` が死ぬ** — `a && !b` の `!b` は「b が解決していないこと」そのものが条件である。未解決スロットの句は不成立になるだけで、評価は待たない。「解決した」の**通知**だけが全スロット揃いを待つ
- `HandleRemoved` の `LastMatch` は代入ではなく**評価から求める**。`false` を入れると「b が消えたら成立」の立ち上がりが起きた瞬間に潰される。その立ち上がりは `HandleRemoved` でしか起きないので、`WhileMatching` の発火はそこにも置いてある
- **評価は各スロットの最新スナップショットに対して行う。同時に読むわけではない。**別プロセスを原子的にスナップショットする方法は無く、イベントごとに全スロットを読み直せばクロスプロセス呼び出しが要素数倍になる。ここから鋭い角が 1 つ出る — **`!b` は b のアプリが起動していない間ずっと成立する。**回避は `a && !b`

### 在否と値は別の軸 — Always は presence である (C16)

`ClauseValue` は**値の軸** (`IsSupported` + 値) と**在否の軸** (`IsAbsent` = スロットに要素が居ない) を別に持つ。ここを 1 本に潰すと、どちらかの意味論が必ず壊れる:

- **`Op = Always` は在否で決まる** — 「その要素が在ること」(presence)。`TriggerComposer` が句なしトリガーから作る代替句と、`!presence` (消えたら成立) の意味の正体である。パターン非対応 (要素は居るが値が無い) では従来どおり成立する — あちらは「プロパティを購読する意図」であって存在の主張ではない
- **値の述語は「最後に見えた値」で評価され続ける** — 消えたスロットでも `LastSnapshot` の値で判定する。こうでないと、**成立したまま要素が入れ替わっただけ** (ツリー再構築) で `WhileMatching` の水準が落ちて戻り、立ち下がり + 立ち上がりが鳴る洪水になる。要素の消滅そのものを知りたいなら presence 句か `ElementRemoved` で書く
- **`ElementRemoved` の絞り込みは要素を手放す前に「最後に見えていた状態」で評価する** (C15)。句付き `ElementRemoved` は監視プロパティを購読して `LastSnapshot` を最新に保つ — 購読しないと「消滅直前の値」のつもりの比較が**解決時の値**との比較になる (実際にそうなっていた不具合)。この購読は発火源ではない (`OnPropertyChanged` は `ElementRemoved` では鳴らさない)

### 立ち下がり通知 — NotifyOnStoppedMatching (C14)

`TriggerDefinition.NotifyOnStoppedMatching` を立てた `WhileMatching` トリガーは、条件が成立しなくなった瞬間 (立ち下がり) にも発火し、イベントは `TriggerFiredEventArgs.On = StoppedMatching` で立ち上がりと見分けられる。

- **`StoppedMatching` はイベント専用の値**である。定義の `On` としては拒否する — 受けると「立ち下がりだけの WhileMatching」という二重の書き方が生まれ、以後すべての `On` 分岐が 2 値を見ることになる
- **フラグは `WhileMatching` 専用**。他の lifecycle では追加時に拒否する (`PollInterval` と同じ「黙って効かない設定を残さない」)。`Apply` も `On` を変えた確定でフラグを落とす
- **立ち下がりは `MinInterval` の対象外**で、`SuppressedFireCount` にも数えない。窓で落とすとホストは「まだ成立中」と信じ続ける — 状態の嘘になる。逆向きの非対称 (立ち上がりが落とされた後の立ち下がり) は冗長だが嘘ではない
- **停止・削除では鳴らさない**。`ReleaseTrigger` は水準を代入で戻すだけで発火経路を通らない — ホストの後始末のたびに偽の立ち下がりが混ざる形にしない
- 評価の 3 箇所 (`TryResolve` / `OnPropertyChanged` / `HandleRemoved`) すべてに立ち上がりと対で置く。ポーリングは `OnPropertyChanged` に合流するので自動で継承する
- **出現/消滅レシピ**: `WhileMatching` + `Op=Always` の句 + このフラグで、要素の出現 (立ち上がり) と消滅 (立ち下がり) が 1 つのトリガーで届く (presence — C16)

### 発火イベントの整合

- **イベントの 3 つの値 (`NewValue` / `OldValue` / `Properties`) は必ず同じ要素を指す** — 先頭の句が読む要素 (句が無ければトリガー自身の要素)。ここを崩すと複合条件で「`Properties` は B の要素、`NewValue` は A の値」という 1 つのイベントが 2 つの要素を指す状態になる。「変化した要素の値が取れない」ことは確定するが、そちらは句ごとの値 (下記) が引き取る
- **出現・消滅のトリガーでは、句が自前の要素を名指せない** (`ArgumentException`)。この組み合わせだけ「消えたのはスロット 0」「値は先頭の句の要素」が別物になりうるためで、弾いた結果、出現・消滅では両者が構造的に一致する
- doc の語は「変化後の値」ではなく「**このイベント時点での値**」。複合では変化した値とは限らないので前者は端的に嘘である。`OldValue` は「このイベントがその値の変化そのものでない限り値なし」

### 句ごとの値 — ClauseReading と作業領域

`TriggerFiredEventArgs.Clauses` = `IReadOnlyList<ClauseReading>`。`ClauseReading` は `(Name, Value, Outcome)`、`ClauseOutcome` は 4 状態である:

| 状態 | 意味 |
|---|---|
| `NotEvaluated` | **結合が短絡して読まなかった。**この句については何も分かっていない |
| `Unreadable` | 要素がそのプロパティを持たない (未解決・パターン非対応)。否定形も成立しない |
| `NotMatched` | 読めたが成立しなかった |
| `Matched` | 読めて成立した |

- **`NotEvaluated` と `NotMatched` を混ぜないことが要点である。**混ぜると「読んだが偽だった」と「見ていない」が同じ値になり、短絡という**公開済みの約束**がホストから観測できなくなる。短絡は `Expression` だけの話ではない — `Any` / `All` の結合も結果が決まった時点で止まる
- 短絡の観測は評価器を触らず、`Evaluate` の中の `Read` 閉包に記録を挟む。読まれなかった句は閉包に来ないので「来たかどうか」がそのまま「評価されたかどうか」になる。**記録を「評価の前に全句へ入れる」形に変えると、短絡した句が `Unreadable` として報告される** — 退行の形として実測済み
- **句の値の作業領域は使い回す。**`Evaluate` は発火しない周でもプロパティ変化ごとに走るので、公開用のリストをそこで作ると鳴らない周のぶんまで確保する。`TriggerRuntime` に配列を持ち、**公開用に写すのは `Fire` の中だけ** — 配送は別スレッドの単一ワーカーなので、渡す時点で固めないと「配られたときには次の評価で上書きされている」
- **評価を飛ばす経路では作業領域をリセットする** (`ResetReadings`)。`HandleRemoved` はスナップショットが無いとき `Evaluate` を呼ばないのに `ElementRemoved` は鳴りうるので、リセットしないと前の周の句の値が載る
- 成立判定は `Fire` で再計算する (記録した値へ同じ述語をもう一度当てる)。どちらも純粋なので結果は変わらず、プロセス間呼び出しも増えない
- スナップショットは句ごとに出さない。同じ要素を指す句はスナップショットを共有するので「同じものが n 個」になる。要るとなったら**スロット単位で**足すこと

### TriggerComposer — まとめる・ほどく

複数トリガーを 1 つの複合条件にまとめる規則は UI に依らない純粋な処理であり、`UiaTrigger.Core` の `TriggerComposer` が持つ。ホストは委譲するだけで、規則を写経しない。

| 論点 | 契約 |
|---|---|
| `unwatched` の語彙 | **元トリガーの id で照合する**。意味的な単位は「この元トリガーを絞るだけにする」であり句ではない。式のほうは実効名 (`login-1`) を要求する — **非対称は意図**である。実効名の `-N` はライブラリが導出するもので、利用者に予測させるべきではない |
| 句を持たない元トリガー | `Property = ControlType, Op = Always` の代替句 = 「その要素が在ること」 |
| 複合の `On` | `WhileMatching` 固定 (成立した瞬間に 1 回) |
| 複合の既定要素 | 先頭の元トリガーに合わせる。全句が要素を上書きするので解決には使われないが、モデルとしては空にできない |
| 複合の `PollInterval` | 「まとめる」時に指定できる (`Compose` の `pollInterval`)。0 / null は未設定 (イベント駆動のまま)、負値の拒否は **`Compose` が持つ** — 呼び出し元に置くと自前の「まとめる」UI を持つホストが規則を写経することになる |
| `Decompose` が戻さないもの | 元の `On`・`MinInterval`・`PollInterval` (複合が記録していない)。**複合自身の `PollInterval` も戻さない** — まとめた条件を読み直すための費用判断であり、どれか 1 つの句のものではない。式も捨て、`Watch` は既定へ戻す — 「絞るだけ」は複合の中でだけ意味を持つ |
| `Decompose` が付ける id | **句の実効名** = 複合の式がその句を指していた名前。分解 → まとめ直しで同じ式がそのまま使える |
| 非複合の `Decompose` | `ArgumentException` (プログラマ向けの契約)。呼び出し側が選択を先にガードする |
| どちらも**非破壊** | まとめても・ほどいても元は一覧に残る。取り消し機能を持たずに取り消せる形で、2 つの操作の線が揃う |

検証文字列は性質で置き場が割れる: **検証理由は Core の `Strings.resx`** (`Compose_NeedsTwo` / `Compose_UnknownName` / `Compose_PollIntervalNegative`)、**操作の結果報告はホスト** (`CombineFailed` / `CombineDone`)。

### トリガ一覧エディタ — 値渡し・値返し

公開形は 3 変種とも `Task<IReadOnlyList<TriggerDefinition>?>` (null = キャンセル)。**エディタは保存先 (`TriggerStore` の場所) も監視 (`TriggerMonitor`) も所有しない** — 渡されたものの写しを編集し、写しを返すだけである。同期シグネチャにしないのは、**WinUI3 の `Window` に窓単位のモーダル API が無い**ため (律速は WinUI 側。WPF / WinForms は内部で `ShowDialog`、WinUI は `TaskCompletionSource` + `Closed` の modeless)。

- **深いコピーは `TriggerJsonContext` の往復**で取る。手で写すとモデルにプロパティが増えたとき黙って欠け、しかも欠けるのは「エディタを通したときだけ」なので保存されたファイルを見るまで分からない。source-generated なので AOT でも動く
- **写しは 3 か所**: コンストラクタ (渡されたリスト) / `Snapshot` (返すリスト) / 子ピッカーへ渡す定義。3 つ目を忘れるとキャンセルしても作業用リストが変わる — ピッカーは渡された実体へ書き戻す契約だからである
- 編集中の差し替えは「**その位置で**」。末尾へ移すと利用者が並べた順序が編集で崩れる。id を変えて確定したときは衝突する行を先に消す (残すと id が重複したリストを返し `AddAsync` が投げる)
- 子ピッカーは同時 1 枚。2 枚開けると「どちらのコミットがどの行か」が決まらない
- **行のダブルクリック = [条件を編集]** (`NotifyEditRequested` に写像する)。編集できない選択 (複合・複数選択) はボタンと同じ理由をステータスへ出す。一覧の空白部分は無視する — でないと、選択済みの行が空白のダブルクリックで編集され始める。空白の判定はフレームワーク固有で View が持つ (WinUI は `DataContext`、WPF は `ContainerFromElement`、Windows Forms は `IndexFromPoint`)
- **ダブルクリックのハンドラの中で子ピッカーを直接開かない** — 入力が掃けた後にディスパッチャで回す。二打目の入力系列が残ったまま `Activate` すると、残りの入力処理がエディタを前面へ戻す。実測: WinUI は所有関係が無いので**ピッカーが窓ごと後ろに出る** (picker BEHIND editor / foreground=editor → 遅延で ABOVE / foreground=picker)。WPF / Windows Forms は所有関係が重なりを保つが、フォーカスはエディタへ戻る — 3 変種とも同じ形で遅らせる
- **WinUI の一覧は横スクロールを自分で有効にする** (実測 175%: 既定は `H-scrollable=False` で複合の長い行が右で黙って切れる。WPF / Windows Forms は元から出る)。`ListView` は自分に付いた `ScrollViewer.*` 添付プロパティをテンプレートへ中継するので、ピッカーの `TreeView` (`OnElementTreeLoaded`) のような迂回は要らない
- **下段 (まとめる / 状態 / 確定) は ScrollViewer + 上限** (`OnRootSizeChanged` が渡す — ピッカーの `ConditionScroll` と同じ理由: Auto の行は子に「欲しいだけ」与える。実測 175%: 上限なしでは WinUI の窓 760x640 で OK が高さ 8px に潰れ、幅 760 で複合のポーリング欄が右に切れて消えた。WPF も同じ形で下段が窓の外へ切れる — T6 で実際に出た)。WinUI は縦横 (WrapPanel が無い)、WPF は縦のみ (WrapPanel が横を折り返す)、Windows Forms は Dock がバーを常に確保するので不要。**ScrollViewer を入れ子にしないこと** — 実測では内側を挟むと外側の縦の可動域が消えた。**素の ScrollViewer の縦スクロール成立は UIA では確かめられない** — `ScrollPattern` が extent をビューポートと同値に過小報告し、`SetScrollPercent` も実可動域に届かない (実測: 人が動かして動くと確認済みのピッカーの `ConditionScroll` も同じ数字を返す)。上限の配線は T1 が縛り (`TheWpfEditorsLowerPane_IsCappedToTheHeightTheWindowActuallyHas`)、実際に縮めた画面は T6 が見る
- `UITypeEditor` 派生 (`TriggerListEditor`) は WinForms だけが持つ (相当物が他に無い)。キャンセル時は元の value をそのまま返す — それがグリッドに「設定しない」と伝える手立てである。VS デザイナーは対象外 (デザイナーはエディターを自分のプロセスで動かし、ピッカーはいま画面にあるものを録るので、録れるのはデザイナー自身になる)
- 置き場所は既存の `Picker.*` に同居する。新パッケージを起こすとパッケージ数・リソース対応表・doc 検査が全部連動して動き、「ピッカー」の語義が広がる代償のほうが安い

### ピッカーの編集モード — ShowDraft

`IPickerView` の条件欄は `ReadDraft` (読む) と `ShowDraft` (入れる) の**両向き**を持ち、presenter に `LoadDefinition` / `CanEdit` がある。既存トリガーのしきい値を 1 つ変えるのに要素をホバーで捕まえ直す必要は無い。

| 論点 | 決定 |
|---|---|
| **要素は読み直さない** | 記録済みの `Window` / `Locator` をそのまま保つ。ホバー捕捉自体は生きており、確定すれば要素は差し替わる |
| 読み込みは AutoSelect を切ってから | 切らないと、マウスがどこかの要素の上に静止しているだけで**編集対象が別の要素に差し替わる** |
| `_suggestedId` を空にする | 空にしておくと、編集中に要素を捕まえ直しても id が黙って提案 id へ置き換わらない — 置き換わると編集したつもりで別のトリガーが増える |
| プロパティ一覧は「いま入っているものを必ず含める」 | 読み込みでは要素を読まないのでパターン対応を見るものが無い。一覧に無いとコンボは選択なしになり、確定で**別のプロパティの条件に化ける** |
| 許容差は演算子が使うときだけ入れる | `Tolerance` の 0 は既定であって「未入力」ではない。使わない演算子で 0 を書くと、出ていない欄の値が見える |
| **下書きが運べないものを持つ句は断る** | 確定は句を作り直すので、`TriggerDraft` に場所が無いものは落ちる: 句の `Window`/`Locator` → 別の要素を監視し始める / `Watch=false` → 絞るだけのつもりが発火源になる / `CustomPropertyId` → 0 に化ける。どれも例外にならず保存された JSON を見るまで分からない。`CanEdit` は複合だけでなくこの 3 つも false にし、`LoadDefinition` は `ArgumentException` で断る |
| ピッカーで編集できるのは単純トリガーのみ | ピッカーは「条件 1 件」の編集器である。複合は分解してから編集する |
| **編集セッションは確定 1 回で閉じる** | `LoadDefinition(definition, editSession: true)` は確定ボタンの文言を `CommitButtonUpdate` に差し替え、確定が成立したら `IPickerView.Close` を呼ぶ — 条件の編集時に追加でトリガーを設定することはない。新規追加の「開いたまま何件でもコミット」は変えない (App ホストの明文化されたワークフロー)。検証失敗では閉じない |
| `Close` は View への**全書き込みの後** | WinForms の `Form.Close` は Show で出した Form を **Dispose する**。presenter は `TriggerCommitted` → `CommitStatus` の後にしか `Close` を呼ばず、以後 View に触らない。親エディタも `Close` を呼ばない — 既存の `Closed` → `NotifyPickerClosed` 経路だけが動く |

`TriggerDraftValidator` は第三者のピッカーが同じ検証規則を得るために public であり、式を入力させるピッカーのために `ValidateExpression` / `IsValidClauseName` も持つ。**`Apply` は新しい形で意味を失った値を残さない** — 句を作り直すとき `Expression` を落とし、ポーリングできない `On` では `PollInterval` も落とす。個別規則の検査に加えて「確定できた下書きから作った定義は必ず監視開始まで通る」(`Apply_AlwaysProducesADefinitionTheMonitorAccepts`) という**規則が増えても腐らない形**の検査がある — 実行時検証 (`CreateRuntime`) に拒否理由を足して `Apply` 側を忘れると、ホストの録り直し (`RemoveAsync` → `AddAsync` の投げっぱなし) では画面に何も出ないままトリガーが消えるからである。

エディタとピッカーのラベルは**キー表を 2 つに割って**持つ (`PickerStringKeys` / `EditorStringKeys`)。1 つの表に混ぜると「View はキー表のラベルをすべて要求する」検査が、ピッカーに存在しないエディタのコントロールを要求するはめになる。2 表は重ならない — 供給経路は 1 つの辞書なので、重なると一方の文言を直したとき他方が黙って変わる。

---

## §5 ポーリング

### 不変条件

> **ライブラリは自分の判断でポーリングしない。**
> `SubscriptionRepair` (§8) が回るのは購読を失っている間だけ、
> `SlotPoller` が回るのは**利用者が明示的に頼んだときだけ**である。

「ポーリングが存在しない」ではなく「ライブラリの判断では回らない」 — これは言葉の綾ではなく設計上の区別である: 前者はライブラリが費用を決め、後者は利用者が決める。

動機は実測から出ている: イベント駆動の裏返しとして、**アプリが UIA の `PropertyChanged` を上げなければ永久に鳴らない**。無通知の変化は WinForms (MSAA ブリッジ) でも WPF (ネイティブ UIA) でも構成できる (実測: `Name` 相当の差し替えはどちらも通知を上げない) ので、ポーリングの価値はプロバイダーに依らない。

### PollInterval の意味論

- **粒度はトリガー単位**で、モデル (`TriggerDefinition.PollInterval`) に置き**永続化される**。鳴らないのは実行ごとの事情ではなく対象の性質だからである。「壊れているアプリのトリガーだけ」が書け、費用が要素数全体に比例しない
- 未指定 / 0 はイベント駆動のまま。0 や負値を「間隔 0 でポーリング」と解釈しない (拒否する)。`ElementAppeared` / `ElementRemoved` とは組めない (実行時検証で拒否し、`Apply` も落とす — §4)
- 対象は**解決済みの要素の読み直しだけ**。`Sweep()` は呼ばない — 掃引は全ウィンドウ列挙 + `OpenProcess` をピッカーと共有する MTA スレッドで走らせるので、タイマーから叩けば**解決経路そのものをライブラリの判断でポーリングする**ことになる
- **周期タイマーにしない。**1 周ぶんの仕事が終わってから再武装する。周期にすると、相手が `TransactionTimeout` ぶん止まっている間に周が積み上がる
- 周の中身は `OnPropertyChanged` をそのまま呼ぶ。立ち上がり判定・値が実際に変わったかの判定・要素消滅の処理が既に揃っており、**書き直すことが「毎周期鳴る」の作り方そのものである**。発火経路は反復に対して元から冪等である: `WhileMatching` は `matched && !wasMatching`、`PropertyChanged` は `matched && WatchedValuesChanged(...)`
- ポーリング中のトリガーは**孤児と数えない** (§8 の `SubscriptionRepair` に載せない)。区別が壊れると「利用者が頼んだ固定間隔」と「復旧の backoff」が混線する
- 間隔を実行中に変える手段は無い (変えるには `RemoveAsync` → `AddAsync`)。必要になればモニター側の上書きとして非破壊に足せる

### Custom の角 — ポーリングが新しく作る唯一の失敗形

`WatchedValuesChanged` は `TriggerProperty.Custom` に対して無条件に true を返す (スナップショットに入らないので前回値を持てない)。素直にポーリングすると**監視付き Custom 句が毎周期鳴る**。`ElementSlot.CustomValues` に直前値を残し、**ポーリング経路だけ**で突き合わせる。

> イベント経路にキャッシュを効かせない本当の理由は「間隔 0 のときの発火意味を変えないこと」
> である。「イベント経路では UIA がその Custom ID の変化を伝えてきている」は誤り —
> プロパティハンドラは購読集合の配列全体に 1 本張られ、コールバックは event args を捨てるので、
> 分かるのは「どれかが変わった」まで。イベント経路の `return true` は正しさではなく今日の挙動である。

---

## §6 UIA イベントスコープの原則

**スコープは「狭めれば安全」ではない。**誤った縮小は「イベントが来なくなる」= トリガーが黙って動かなくなる形で壊れ、擬似ツリーでは検出できない (実 UIA でしか分からない)。

### WindowClosed は Subtree でなければ届かない (B2)

`WindowClosed` はイベントが届く時点で発生源のウィンドウが既に消えているため、UIA が「これは root の子か」を評価できず、`TreeScope.Children` では**イベントが 1 件も配送されない**。この理由はプロバイダーに依らない (WinForms / WPF とも実測)。参照実装の `System.Windows.Automation` はこの制約を明示的な例外にしている:

```
WindowClosed event is only applicable to RootElement and TreeScope.Subtree
or an element that implements WindowPattern.
```

手書き interop にはこの検証が無いので、**`AddAutomationEventHandler` は成功し、ただ何も来なくなる**。`WindowOpened` は発生源が生きているので `Children` でよい。`WindowClosed` のハンドラはデバウンス付き sweep を投げるだけなので、Subtree で受けるコストは限定的である。

### 経路購読 — 各段を Element | Children で (B3)

解決済みトリガーの `StructureChanged` 購読は、**ウィンドウ要素から対象の親までの経路上の各段**に張る。ウィンドウ全体の `Subtree` は動的な Chromium 系アプリで毎秒数百イベントを拾う。

- 「対象の親 1 段だけ」は採らない。親 P の消滅は P の親 G が発生源になるため、P しか購読していないと**その親自身が消えたときに何も届かない**。経路を壊しうる構造変化は必ず経路上のいずれかの段が発生源になるので、各段購読なら取りこぼしが無い
- スコープは `Element` ではなく **`Element | Children`**。「`StructureChanged` の発生源は子が変わった側の親」という規約だけでなく、**実 UIA では追加・削除された子自身が発生源になる場合がある** (WinForms の子コントロール追加)。`Element` だけだと構造変化を 1 件も受け取らない
- 未解決の間は「どこに出現するか分からない」ためウィンドウ全体の `Subtree` で張る。経路購読は解決 1 回につき 1 回しか張り替わらない。購読数は経路の深さ (通常 5〜15)

### プロバイダー差 — 再解決は二本立て

- **WinForms (MSAA ブリッジ) では、子の削除は `StructureChanged` として届かず `WindowClosed` (Subtree) 経由でのみ観測できる。**つまり再解決は「追加は `StructureChanged`・削除は `WindowClosed`」の二本立てで成立しており、**どちらか一方を塞ぐと WinForms 系アプリで再解決が止まる**
- WPF (ネイティブ UIA) では対象そのものの削除も経路 (`Element | Children`) で拾える。一方で削除された要素は `ReadSnapshot` に値を返し続け、`GetParent` 鎖はウィンドウまで届く — この非対称が A21 の実体である (§8)
- `ControlType` は要素を作り直さずに変わる (両プロバイダー)。識別が即除外にできない理由 (A5) もここにある

回帰の網は `EventScopeTests` (経路の内と外で同じ形の構造変化を起こし、内側では届き外側では 0 件であることを対で見る)。スコープを触るときは必ず実 UIA (docs/TESTING.md §1 の T3) を通すこと。

---

## §7 要素ハンドルの借用規律

### use-after-free — 規律ではなく型で縛る

生の COM ポインターを取り出す `Unwrap()` の形は禁止である。取り出した時点から持ち主の `UiaElement` は到達不能とみなされうる (JIT は「最後に使った後」で生存を打ち切ってよい)。そうなるとファイナライザーが **GC のファイナライザースレッド**で RCW を解放し、UIA スレッドが解放済みの COM オブジェクトを呼ぶ — 例外ではなく**アクセス違反でプロセスごと落ちる**。

- `UiaElement` は生ポインターを返すメンバーを 1 つも持たない。`Borrow()` が返す `ref struct` の借用スコープだけがあり、`Borrowed.Dispose` が `GC.KeepAlive(_owner)` するので `using` の範囲では解放されない
- この不変条件は実行時テストにできない (再現が GC のタイミングに依存する)。だから**危険な書き方ができないこと**を源泉で固定する (`ElementBorrowTests`): 借用が必ず `using` に入っていること。「`.Borrow()` の個数を数える」形は、その場で使う `a.Borrow().Element` を素通りさせるので検査にならない

### 解決層に同じ道具は要らない — 不在を不変条件にする

解決層の `UiaElementNode` には**ファイナライザーが無く `IDisposable` も実装しない**。解放を呼ぶ主体は解決ループだけで、それは同期に走る。**非同期に回収する主体が居なければ、所有者の到達可能性は問題にならない** — `Borrow()` は到達可能性を延ばす道具であり、延ばすべき相手が居ないところに同じ道具は要らない。

この不在は手抜きではなく設計である。だから不在そのものを不変条件にしてある (`TheResolutionLayerNodeHasNoAsynchronousReclaimer`)。ファイナライザーか `IDisposable` が足された時点でこの根拠は消え、`Unwrap` を借用スコープへ変える必要が生じる — その時に気づけるようにしておくことがこの検査の目的である。

- `Release()` は `Interlocked.Exchange` で冪等である。これは**証拠の無い保険**であり、冪等になったからといって二重解放を書いてよいことにはならない。`ElementResolver.ReleaseLevels` の生き残り走査は設計上の理由 (ビームが経路の前半を共有する) であって、冪等化の代わりではない
- `UiaElementNode` は解放後に参照をヌル化**しない**。ヌル化で得るものが無い (回収者が居ない) 一方、`Unwrap` の呼び出し元は `COMException` しか捕まえないディスパッチャー上の仕事なので、`ObjectDisposedException` 化は**誰も観測しない faulted Task** を新しく作る

### FinalRelease と UniqueInstance (B6 / B7)

- `StrategyBasedComWrappers` の RCW は `IDisposable` ではない。公開された解放 API は `ComObject.FinalRelease()` で、しかも**共有 RCW に対しては安全でない** — 同一性テーブルはネイティブポインタをキーに RCW を弱参照で保持し、`FinalRelease()` はエントリを外さない。解放済みの RCW がテーブルに残ったまま同じアドレスに新しい COM オブジェクトが割り当てられると、**死んだ RCW が返ってくる**
- そのため解決ループが大量に作る要素は `CreateObjectFlags.UniqueInstance` で包む (同一性テーブルに載らないので `FinalRelease()` が安全)。interop 側の `FindAllBuildCache` / `GetElement` / `BuildUpdatedCache` / `ElementFromHandleBuildCache` の out は生ポインタ (`nint`) で、RCW の生成を自前に寄せている。**この宣言が既定のマーシャリングに差し戻されると解放が静かに効かなくなる**ため `InteropShapeTests` で固定してある
- 実測メモ: UIA は要素を返すたびに別のネイティブポインタを返す (同じ `GetRootElement` を 2 回呼んでもポインタは異なる)。共有 RCW を解放したときの事故は「起きにくいが起きうる」類であり、一意インスタンス化で確実に避けている。`UniqueInstance` の RCW はそのまま COM 呼び出しの引数に渡せる (実機で確認済み)
- RCW の生成は `ComInterfaceMarshaller<T>` 経由に統一する (B7)。独自の `StrategyBasedComWrappers` と生成 stub の `DefaultMarshallingInstance` が並ぶと、RCW の同一性テーブルが 2 系統に分裂する

### ピッカー側の決定的解放 — 小さな GC

presenter が受け取る要素ハンドルの解放は「捨てる場所を数え上げる」形では破綻する (`_selectionOrigin` のような独立した所有根が、木に 1 度も現れないまま使われる経路がある)。所有根を `Roots` ∪ `{_selectionOrigin}` ∪ `{_currentNode?.Element}` と定め、**根から到達できないものを掃き出す**。不変条件は 2 つ:

1. 所有根が変わったら掃く
2. 受け取ったものは**掃く前に** `Own` する — 逆順にすると、まだ木へ入れていないチェーンを到達不能とみなして即座に解放する

- **解放しすぎは静かに間違わない**方向に倒れている。`IPickerElement` の値のメンバーは生成時のスナップショットなので解放後も読め、壊れるのは継ぎ目へ渡し返す経路だけで、そこは `Borrow()` が `ObjectDisposedException` にする
- fire-and-forget の更新経路 (`RefreshPropsAsync` 等) には `ObjectDisposedException` の `catch` がある。握り潰しではない — 条件の意味が「出す先の行がもう無い」であり、代わりに表示すべきものが存在しない
- **解放は呼び出しスレッドで同期に行う。**UIA ディスパッチャーへ投げる形 (アパートメント的に正しい形) は退けた: 実測で STA / MTA どちらからも例外は出ず速度も同じ桁 (1 件 0.1ms 級) である一方、ディスパッチャーは相手アプリが固まると塞がるので、そこへ解放を積むと**いちばん手放したいときに手放せなくなる**。解放コストはそのハンドルを生んだ列挙コストの 3% でしかない (実測)
- `PickerTreeNode` に `Dispose` は無い。ノードは借りているだけで、持ち主は presenter である

---

## §8 生存判定と再購読

### ゾンビ要素 (A8 / A21)

`CheckAlive` の 2 つの判定 — (i) 属性の読み直しが `ElementNotAvailable` になるか、(ii) 安定属性の組が変わったか (A8: 「生きているが別物」の検出) — だけでは、ネイティブ UIA プロバイダーの**切り離された要素**を検出できない。WPF では切り離された要素がそのまま応答し続け、`GetParent` で遡ると peer が親をキャッシュしているためウィンドウまで届く。**上向きの navigation では削除を検出できない。**結果、対象を削除しても未解決にならず、同名の要素が出現しても一度も発火しない — 例外もログも出ない、最も避けたい壊れ方である。

対応は 2 つある。**壊れ方が 2 通りあるためで、片方だけでは塞げない**:

1. **経路がある場合 — 下向きの到達性** (`TriggerMonitor.IsStillOnThePath`)。経路購読 (§6) で保持している各段について「次の段が本当にその段の子か」を確かめ、どこかで切れていれば `Status_ElementDetached` として手放し、再解決に回す。祖先ごとまとめて消えた場合も切れた段で false になる。費用は段数ぶんの子列挙 (段あたり 1 往復 — B1)。`CompareElements` は識別属性で絞ってから呼ぶ (B9 と同じ手)
2. **対象がウィンドウ自身の場合 — `IsWindow` を OS に訊く。**経路が空なので 1 が使えない。破棄されたウィンドウの要素が属性を答え続けるかはプロバイダーの解体タイミング次第の**競合**であり、`IsWindow` は往復ゼロで決定的に答えられる

HWND の再利用にも注意が要る (A9): 購読の張り替え判定を「HWND 値の一致」で行ってはならない。ウィンドウが閉じて同じ HWND 値が再利用されると、死んだ要素への購読が残る。要素の同一性 (`CompareElements`) まで確認する。

### 再購読の閉路 — 「購読が無い → イベントが来ない → 掃引が走らない」

掃引を予約する経路は 4 つしかない: ルートの `WindowOpened` / ルートの `WindowClosed` / トリガーごとの `StructureChanged` / `CheckAlive` の要素消滅。未解決のトリガーが構造購読を失うと、このどれも起きない状況では**購読が無い → イベントが来ない → 掃引が走らない → 購読が張り直されない**という閉路に入り、例外も通知も出ないままそのトリガーは永久に解決しなくなる。これを塞ぐ規律は 3 つ:

1. **張り替えられると分かるまで壊さない。**購読の張り替えは「新しい購読を先に立て、古いものを外すのは立ったと分かってから」。1 つも張れなかったときは古い購読をそのまま残す — 相手が忙しいだけかもしれず、壊すと戻る道が無くなる。`targets` が空のときは 2 つの意味を分ける: 解決した結果として経路が空 (対象がウィンドウ自身) なら広いウィンドウ購読は外す (残すと B2 で潰した状態に戻る)。未解決でウィンドウを特定できなかっただけなら残す
2. **購読を失ったトリガーを拾い直す** (`SubscriptionRepair`)。最初の購読からして失敗した場合は古い購読も無く 0 件のまま閉路に入るので、1 だけでは足りない。判定は純関数 `SubscriptionHealth.IsOrphaned` = 「購読 0 件 **かつ** 張ろうとしたウィンドウが記録されている」。**`Count == 0` だけにすると誤爆する** — まだ起動していないアプリのトリガーが常に「壊れている」と見なされ、再試行が回りっぱなしになる = §5 の「ライブラリが自分の判断でポーリングしない」を、直したつもりで壊す。`SubscriptionRepair` は壊れている間だけ動き、健全なときはタイマーが 1 本も armed にならない。間隔は 2 秒から倍々で 30 秒頭打ち、復旧したら戻す — 再試行 1 回 = 掃引 1 回で、掃引はピッカーと共有する MTA スレッドの上で相手ごとに待たされうるから、機械が苦しいときほど激しく叩く形にしない
3. **診断を黙らせない。**`TriggerMonitorDiagnostics.OrphanedTriggerCount` がこの閉路の観測点である。`StructureIsPathScoped` は購読を持たないトリガーを動かさない (「出現待ちのトリガーがあれば false」ではない)

`IsSameElement` が `COMException` を「死んでいる」と読む判断には根拠が要る — **比較できないことと死んでいることは別**で、相手が塞がれているだけでも同じ例外が出る (`COMException` は相手が忙しいときほど出る = いちばん診断が要る場面で出る)。

この領域には**未修正の既知不具合が 1 件残っている** (「購読が失われたら実際に張り直る」の end-to-end は決定的に構成できず、実地の検証が限られる)。現状と経緯は docs/TESTING.md §5 が正である。

---

## §9 DPI と座標

### 座標系の不変条件

- **座標はすべて物理ピクセルである。**`GetWindowRect` (物理) と UIA の `BoundingRectangle` (物理) と Picker が使う座標は一致する。この不一致はホストが DPI 非認識である証拠であり、それ自体が診断の手がかりになる
- **ホストは PerMonitorV2 を宣言する。**宣言の経路は 2 つに割れている: manifest (WinUI / WPF) と `SetHighDpiMode` (WinForms — アナライザー WFO0003 が manifest の高 DPI 設定をエラーにする)。経路が割れている以上、**どちらの経路にも載っていないホストが出ないこと**を別に見る必要がある — 表に載っていないホストは検査を素通りし、全部緑のまま DPI 非認識のホストが増える
- `DpiAwareness` は public である。ホストが PerMonitorV2 であることはこのライブラリの前提であり、前提を確かめる手段を隠す理由が無い。宣言できていないことは `UiaSession.CoordinateProblem` として必ず報告され、ピッカーはこれを画面 (ヒント欄) に出す (C13)。**DPI 認識は最初の UIA 呼び出しで固定される** — `RootElement` を読んだ時点で `System` に固定され、以後変えられない。宣言は何よりも先に行うこと

### A19 — 座標仮想化は別の要素を静かに返す

`ElementFromPoint` / `GetCursorPos` は物理スクリーン座標を扱うが、DPI 非認識のプロセスでは Windows が座標を仮想化するため、渡した座標がスケール分ずれた位置として解釈される。175% の画面では (174,149) が (304,260) になり、狙った子要素の外側にある親要素が返る。**例外にはならず、間違った定義が出来上がる。**この壊れ方はマウスホイールバグ (docs/TESTING.md §4) と同型である — 症状は入力や記録の失敗に見えるが、真因は座標系の宣言にある。

### OverlayGeometry と DPI

- **`OverlayGeometry` は純関数のままにする — これは譲らない。**中で DPI を引きにいくと T1 が固定できなくなり、T4 / T5 の期待矩形も計算できなくなる。`dpi` は引数である
- 定数 (枠線太さ・アイコン寸法) は「96 DPI での値」と定義し、`MulDiv(値, dpi, 96)` で出す。枠線は最低 1px を保証する
- **`IconHalf` と `IconOutside` だけをスケールし、`IconSize` / `IconInset` は導出する。**当たり判定は中心から `IconHalf` で広がり、描画は左上から `IconSize` で広がる — 一致するのは `IconSize == IconHalf * 2` のときだけである。`IconSize` を直接スケールすると 175% で奇数 (35) になり、35px の絵に対して 34px しか押せない 1px の帯が「見えているのに押せない」。一辺は偶数でなければならない
- DPI の出どころは**要素が乗っているモニター** (`MonitorFromPoint` + `GetDpiForMonitor`)。`GetDpiForWindow(オーバーレイ)` は使えない — 寸法を決める時点でまだ動かしていないので、初回は別のモニターの DPI を返しうる
- **オーバーレイとプレゼンターは同じ `IDpiSource` を共有する。**別々に引くと、絵と当たり判定が食い違う経路を作り込める (どちらも例外を出さない)。presenter に直接 P/Invoke を書くと T1 が走っている機械の表示スケールで結果が変わるため、継ぎ目 (`IDpiSource`) で注入する
- 96 に決め打つと**高 DPI の機械でだけ**壊れる。期待値の側も同じ DPI を渡すこと

### ヒットテストの矛盾 — 引き直しの規律

シェル (デスクトップのアイコン等) のヒットテストは、**問い合わせた点を含まない要素**を返すことがある (実測: ピッカーが隣のアイコンを掴んだ後、その点を含まない前の要素が返る。無関係な第三のプロセスから引いても同じ)。「その点の要素」を訊いた答えとして矛盾しており、プロバイダー側は直せないが、**矛盾はこちらで検出できる**。`UiaSession.ElementFromPointCore`:

1. 返ってきた要素の矩形が**その点を含むか**を見る (`ElementRect.Contains`)
2. 含まないときだけ `DeepestContaining` で引き直す — 点を含む最前面の窓から下りる、**プロバイダのヒットテストに依存しない**経路 (重なりスタックの構築と同じもの)
3. 引き直しても見つからなければ**元の答えを返す**。`null` にはしない — 「何も無い」は「捕捉しない」を意味し、症状が変わらないまま原因が隠れる

**矩形の判定は Win32 と同じ半開区間である。**アイコンは辺を共有して並ぶので、閉区間にすると境界の x を両隣が「含む」と答え、問い自体が答えを失う (`HitTestSanityTests` が固定)。

なお `ElementFromPointCore` は**自プロセスの要素に null を返す** (ピッカーが自分を捕捉しないための仕様)。呼び出し側から見ると「例外も診断も出さずに何もしない」形になることを忘れないこと。

---

## §10 オーバーレイの構造

### 窓は 2 枚 — 枠と確定アイコン

| 窓 | クラス名 | 中身 | 拡張スタイル | ヒットテスト |
|---|---|---|---|---|
| 枠 | `UiaTriggerOverlay` | 枠線だけ | `WS_EX_TRANSPARENT` ほか | 窓ごとヒットテストから外れる = **常にクリックスルー** |
| アイコン | `UiaTriggerOverlayIcon` | 確定アイコン (`IconSize` 角) | `WS_EX_TRANSPARENT` なし | 全ピクセル不透明なので既定の `HTCLIENT` で受け取る |

**1 枚に戻してはならない。**`UpdateLayeredWindow` のレイヤードウィンドウでは、ヒットテストが**ピクセルごとのアルファ**で決まる。不透明なピクセルはそのウィンドウのものになり、そこで `WM_NCHITTEST` に `HTTRANSPARENT` を返しても**クリックは下へ渡されず消える** (自分も受け取らず、下のアプリにも届かない — 実測)。透明な内側 (アルファ 0) だけがクリックスルーする。つまり「不透明な枠線は透過・不透明なアイコンは押せる」を 1 枚の窓で作ることは**原理的にできない**。`WS_EX_TRANSPARENT` は窓ごとヒットテストから外すので、枠側の `WM_NCHITTEST` はもう要らない。

- **逆向きの壊れ方**: アイコンの窓に `WS_EX_TRANSPARENT` を付けると確定アイコンが押せなくなる。1 語の変更で起き、枠側だけを見る検査は緑のままである — 原因のほう (拡張スタイル) を固定する検査を別に置く (`OnlyTheFrame_IsTransparentToHitTesting`)
- **スレッドは増やさない。**2 つの窓は同じオーバーレイスレッド / 同じメッセージループに載せる (§3 の「MTA 1 本」と同じ、スレッドを増やさない決定)
- `WM_DESTROY` で `PostQuitMessage` を投げるのは**枠のときだけ**。どちらでも投げると、片方を壊した時点でループが抜けてもう片方が残る。終了はアイコンを先に壊す
- クラス名で外から絞るときは**完全一致**で: `"UiaTriggerOverlay"` は `"UiaTriggerOverlayIcon"` の接頭辞なので、前方一致は枠を数えるつもりでアイコンまで数える

### IconRect が当たり判定そのもの

「絵の中のアイコンの位置」と当たり判定 (`IsInIconZone`) を別の式で書くと、片方だけ直したときに「見えているのに押せない」という例外の出ない壊れ方になる。`IsInIconZone` は `OverlayGeometry.IconRect` から導かれ、`IconRect` はアイコンの窓の矩形そのものである — **ずれる余地が構造から消えている**。

極小要素では枠を最低アイコン 1 個分へ広げる。当たり判定の基準も**広げたあとの枠の右上** (`frameRight = r.Left + Math.Max(r.Width, IconSize)`) であり、`r.Right` に戻すとアイコンの右半分が「見えているのに押せない」に戻る。要素がアイコン以上の大きさなら両者は一致するので、通常の要素の挙動には影響しない。

### Z オーダー

- `WS_EX_TOPMOST` は「トップモーストの帯に入る」だけで、**帯の中の順位は最後に主張したものが勝つ**。既に可視なウィンドウへの `ShowWindow` は Z を動かさないので、一度下へ回ったらそれだけでは二度と上がらない — 症状は「枠は見えるのに確定アイコンだけが押せない」で、例外は出ない
- そのため `Redraw()` の末尾で `SetWindowPos(HWND_TOPMOST, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE)` を呼ぶ (両方の窓に対して)。`SWP_NOACTIVATE` は必須 — 付けないとオーバーレイがフォーカスを奪う。この形は「次に描き直したとき」に帯の先頭へ戻すのであって、瞬時に戻すのではない (実操作ではホバーのたびに描き直すので実用上は戻る)。「常に最前面」と読まないこと
- オーバーレイは `WS_EX_NOACTIVATE` を持つ。それ自身を押しても前面は変わらない

### 複数ピッカーと登録表 (A18)

オーバーレイの状態は static singleton ではなく **HWND / フックスレッド ID を鍵にした登録表**で持つ。ピッカーは複数開ける。閉じる側が登録表を丸ごと消す形 (共有状態) に戻すと、1 枚を閉じたとき残った 1 枚のフックまで死ぬ。

**枠は UIA のヒットテストでも対象を隠さない** (`WS_EX_TRANSPARENT` が尊重される)。これは満たしていなければならない不変条件である — 塞いだ瞬間、ピッカー自身がホバーで対象を拾えなくなる (§9 の自プロセス除外により、黙って何も起きない形で壊れる)。

---

## §11 フックの不変条件

### HookProc は入力の出どころで分岐してはいけない

合成した入力 (`SendInput`) には**例外なく**注入ビットが立つ (実測: キー・マウスとも)。`HookProc` が `->flags` で分岐すると、**合成入力と物理入力は必ず別の経路を通る**:

- 注入を**捨てる**分岐 → 合成入力のテストが赤くなる (見える壊れ方)
- 注入だけを**通す**分岐 → **テストは緑のまま実機だけ死ぬ** (最も警戒すべき向き)

不変条件は向きに依らず両方を塞ぐ — `HookProc` 本体に識別子 `flags` の**参照そのものが無い**ことを固定する (`HookPolicyTests`)。定数名 (`LLKHF_INJECTED`) で探す形は、その定数が生成されていなければ空振りして緑になるので使わない。塞げていない穴 (構造体を別ファイルのヘルパーへ渡して向こうで読む形) はクラス doc に明示してある。テスト側の入力政策 (lint と T5 の解禁条件) は docs/TESTING.md §3 が正である。

### ←/→ は飲み込まずパススルー

フックはキーを吸収せず `CallNextHookEx` で流す。これが崩れると、ピッカーを開いているあいだ**システム全体で ←/→ が効かなくなる** — 症状がピッカーの外に出るので、ピッカーのテストでは絶対に捕まらない類である。

### 譲る条件は「ツリーがキーボードフォーカスを持つか」

←/→ をツリーに譲る判定は「ウィンドウがアクティブか」ではなく**「ツリーがキーボードフォーカスを持つか」** (`SetTreeHasFocus`) である。アクティブ判定だと重なり切替が事実上使えない — 他アプリの上にカーソルを重ねてもフォーカスは動かない (Windows がフォーカスを移すのはクリックのとき) ので、ホバーしているあいだはずっとアクティブ = ずっと譲る、になる。重なり要素を送りたいのはまさにそのときである。ツリーがフォーカスを持つときは `MoveStackAsync` を呼ばない (意図的な分岐)。

`MoveStackAsync` は重なりスタックの端で clamp して**何もせずに返す**。捕捉直後の位置はスタックの終端なので、端に居るものへ端向きの操作をして「効かない」のは不具合ではない。

### フックの寿命

閉じたピッカーは二度と枠を出さない。閉じるときに枠を出す経路は 3 重に切れる — (1) `ArrowKeyPressed` のハンドラ解除、(2) オーバーレイのウィンドウ破棄、(3) 登録表からの `Unregister()` (以後 `HookProc` の `self` が null)。フック解除 (`_hook?.Dispose()`) は `Unregister()` の**後**にあり、いちばん外側の防壁である。つまり**フック解除そのものの退行は外から観測できない** — フックのハンドルの漏れは人手でしか見られない差として残る。1 枚を閉じても残ったピッカーは ←/→ に反応し続け、←/→ が届くのは**フックを有効にしているピッカーだけ**である。

---

## §12 ホストと CLI の継ぎ目

### Picker の分割 — 振る舞いと View

ピッカーは `UiaTrigger.Picker.Core` (振る舞い: presenter / オーバーレイ / フック / `net10.0-windows` / AnyCPU / CsWin32) と、薄い View 3 変種 (`Picker.WinUI` / `.Wpf` / `.WinForms`) に割れている。分割が成立する前提は `DisableRuntimeMarshalling` が**アセンブリ単位**であること (§2)。

継ぎ目 (`IPickerView` / `IUiDispatcher` / `IUiTimer` / `ICursorSource` / `IPickerStrings` / `IDpiSource`) は「**UI フレームワークごとに答えが違うもの**」だけに切る。コンテナ実体化を待つリトライ・「空欄」の表し方 (`NumberBox` = `NaN`、`TextBox` = 空文字)・アクティベーション猶予のような「そのフレームワークの事実」は View に置く。共通化すると 3 つとも間違った最小公倍数になる。

- **行を開くことには 2 つの意味がある**ことを型に出す: `IsExpanded = true` (ユーザーが開いた → 子を全列挙する) と `ExpandForDisplay()` (ピッカーが組んだ部分木を表示のためだけに開く)。presenter は `IsExpanded` を書かない — View は TwoWay で束縛しているので、書くと書き戻しが抽象を貫通する。取り違えるとホバーのたびに経路上の全段が兄弟を取りに行く
- 公開面は絞る。View / ホストが実装または供給するものだけを公開し、内部の継ぎ目 (`IPickerElement` / `IPickerServices` / `IOverlay` / `OverlayGeometry`) は internal + IVT にとどめる。「使わないフィールドは置かない」(§3 の Search 方式と同じ判断)
- プロパティ欄の更新 (`RefreshPropsAsync`) は、対象が読めなければ**一覧を空にする**。「古いまま」は「選択と赤枠は新しいのにプロパティ欄だけ別の要素」という静かに間違う症状そのものであり、「理由を出す」はヒント欄が捕捉のエラー通知と競合する。枠は消さない — 矩形は手元の値で出せており、消すと捕捉ごと壊れたように見える

### InternalsVisibleTo と公開面

- **製品アセンブリ向けの `InternalsVisibleTo` は無い。**製品アセンブリが Core の internal な COM 型を掴む状態は「第三者には同じことができない = ライブラリとして使えない」と同義である。Picker は公開 API (`UiaSession`) だけで書ける
- 一方、探索アルゴリズムの継ぎ目 (`IElementTree` / `ElementResolver` / `WindowCandidateCache` / `ElementIdentity`) は public にしない。公開すると「解決の内部表現」が API 契約になり、interop や探索方式の変更が破壊的変更に変わる。テストアセンブリへの IVT に限る
- `PublicApiTests` が縛るもの: IVT の宛先がテストアセンブリだけであること / `UiaTrigger.Interop` / `UiaTrigger.Threading` に公開型が無いこと / **公開メンバーのシグネチャに interop 型が現れないこと** (3 つ目が本質)
- IVT はプロセス境界を越えない。ホストを子プロセスとして駆動するテストは、IVT を足しても相手側の実体には触れず、自分のプロセスに別のコピーを読み込むだけである

### 3 変種の対称性と、意図的な非対称

- **`App.WinUI` だけがショーケースを兼ねる** (ピッカー → 監視の E2E / 複合条件をまとめる UI / ログ一覧)。3 重化しても得るものが無い。README の "Two asymmetries are deliberate" が公開済みの決定であり、ホスト側の文字列検査は明示の例外表 (WinUI にだけ在るキー) を持つ — 例外表は「在る」の主張なので、表のキーが実在することも別に assert しないと腐る
- 条件欄のスクロールは 3 変種で機構が違う: WPF は `WrapPanel` が折り返して縦に伸び (縦スクロールが受け止める)、WinForms は `AutoScroll` が両方向のバーを出し、WinUI は明示の高さ上限 + 横スクロール + 折り返し幅の供給が要る。**「揃っていないから揃える」で直さないこと** — 行の並べ方がそれぞれの枠組みの流儀で違うのが原因で、壊れていない 2 つを触ることになる。WinUI / WPF の条件欄の高さ上限は XAML 側を正とする (定数で持つと XAML を変えた日に黙ってずれる)。**WinUI の `HorizontalScrollMode` と `HorizontalScrollBarVisibility` は独立していない** — 後者が `Auto` なら前者を `Disabled` にしてもスクロールは生きたままで、片方だけの退行は振る舞いに出ない (実測)。対で書き、対で縛る。縦バーのぶんは出ていなくても常に幅から引く — 足りないと、バーの出入りと折り返しが互いを駆動する振動になる
- ホストの監視の作法 (サンプルは読まれて写されるので、作法そのものが仕様である): 録り直しは **`RemoveAsync(id)` → `AddAsync(def)`** の順 (`AddAsync` は id 重複で `ArgumentException`)。イベントハンドラー内の非同期呼び出しを投げっぱなしにしない (誰も観測しない faulted Task になる)。イベントは別スレッドで来るので Dispatcher で渡し直す。`UnhandledException` はログ一覧へ出す (握り潰すサンプルは見えない失敗形を隠すことで実演する)。ログ一覧には上限を置く。ホストは `CreateMonitor` ではなく単体の `TriggerMonitor` を使う — 要素を調べているのはピッカーであってホストではなく、自前の `UiaSession` を作ると 3 本目の UIA スレッドが立つ
- 窓の既定サイズは 3 変種で同じ値にする (ピッカー 1100×700 / エディタ 900×560 / サンプルホスト 900×600)。**WinUI3 の `Window` だけは XAML で宣言できない**ので、`AppWindow.ResizeClient` を**コンストラクターで** (窓を出す前に) 呼ぶ。与えないと OS 既定 = 画面に比例する大きさで開き、3840×2160 では窓が画面をほぼ埋める。**`ResizeClient` が取るのは物理ピクセルである** — WPF の `Width="1100"` は DIP なので、表示スケールを掛けないと 175% の画面で他の 2 変種の 57% の大きさになる (例外も警告も出ず、ただ小さい)。掛け算のために DPI が要り、それは窓を出す前 = `XamlRoot.RasterizationScale` がまだ無い時点なので、View 側に `GetDpiForWindow` の P/Invoke が 1 つだけ在る。**この既定サイズは T4 からは観測できない** — ハーネスがホストの窓を割り付けの矩形へ退かすときに寸法も合わせるためで、網はソースの形 (`PickerWindowDefaultSizeTests`) に置く
- 区切り (GridSplitter): WinUI3 だけ標準の区切りを持たないので Toolkit の Sizers を使う (自作しない)。位置は永続化しない (設定を持たない道具として一貫)。`ResizeDirection` / `ResizeBehavior` は明示する — 既定の推測は外れても例外にならず、掴んでも何も起きない。両側に `MinWidth` / `MinHeight` (無いと戻すための掴み代ごと畳める)。WinForms の `SplitterDistance` は**比率で、ドッキングが済んでから**入れる — 絶対値は狭い画面で setter が黙って詰め、変種間の見た目が崩れる。オブジェクト初期化子の時点では既定幅しか無い

### AnyCPU と x64、AOT

- ライブラリは**純 IL / AnyCPU** である。WinUI3 View だけが x64 / WindowsAppSDK の制約を持ち、そのため T1 から参照できない。**「View はテストできない」は誤り** — 制約は WinUI3 に固有で、WPF / WinForms の View は普通に `ProjectReference` できる
- App の `RuntimeIdentifier` はハードコードせず `Platform` から導く (ARM64 指定で `win-arm64`)。App を ARM64 で建てると参照するライブラリ側も ARM64 で建つが、配布物には掛からない — パッケージは `Platform` 無指定の `dotnet pack` が作るので MSIL のままである (§1)。`bin\ARM64` や `bin\x64` の中身から配布物の機種を結論しないこと
- **WinRT の ABI に静的な型がそのまま載る層は黙って失敗する**: `ItemsSource` (ABI 上 `object`) へ `IReadOnlyList<素の列挙型>` を渡すと CCW を組めず `E_INVALIDARG` (捕まえるなら `ArgumentException`、`COMException` ではない)。配列に具象化して渡す。発行済みバイナリでは `VisualTreeHelper` が返す型が基底に落ち、`TreeView` は `ScrollViewer.*` 添付プロパティを中継しない — いずれも例外もバインドエラーも出ない
- **AOT 発行でだけ壊れる層がある** (A23)。自アセンブリ外の WinRT 値型 (`GridLength` 等) は CsWinRT が vtable を生成せず、ABI を越える代入が**例外なく**動かなくなる。`[assembly: WinRT.GeneratedWinRTExposedExternalType(typeof(GridLength))]` は**`Picker.WinUI` に置く** — ライブラリ側に置けば、参照して自分のアプリを AOT 発行する利用者も同じ穴を踏まない。`CsWinRTAotWarningLevel=3` (前提: `WindowsSdkPackageVersion` を明示 — 既定 SDK の CsWinRT はこのプロパティを黙って無視する) はこの層をビルドで止めるが、**外部型は生成器の視野に入らないので A23 の代替にはならない。2 つは別の網である**

### CLI の継ぎ目

3 ホストが読む引数は `--pick-at` (繰り返し可 — n 枚目のピッカーに n 番目を配る) / `--culture` / `--triggers` で揃っている。

- `--triggers` が無いホストは、自動テストが開発機の実ファイル (`%LOCALAPPDATA%\UiaTrigger\triggers.json`) を書き換える経路になる。読み書きは 1 つのフィールドを通し、上書きの口を必ず持つ
- WinUI の表示言語は `PrimaryLanguageOverride` が**単独で**効く (`CurrentUICulture` を MRT は見ない)。上書きは `InitializeComponent()` より**前** — リソースローダーは Lazy で、一度でも文字列を読んだら決着する。順序を間違えると「効かない」と誤って測る
- `HostOptions.cs` は 3 ホストで共有しない (名前空間・ログの出口・言語上書きの有無が違い、共有版は注入と初期化順の呼び忘れを新しく作る)。**4 つ目のオプションか 4 つ目のホストが要るときは共有プロジェクトへ移す。**ソースのリンク共有へは戻さない
- `TriggerPickerWindow(ICursorSource)` は公開されたコンストラクタである。**捕捉 (ホバー滞留) に合成入力は使わない** — 実カーソルを動かすと捕捉の経路に座標の円環が戻り、滞留がわずかな移動でリセットされる。差し替えるのは入力イベントではなくカーソルの取得元である。静的な既定値の差し替え口は置かない — プロセス全体で 1 つの状態になり、ピッカーを 2 枚開いたときに別々の座標を指せない

### 捕捉の門

`TriggerPickerPresenter.TickAsync` の捕捉には 4 つの門がある: 移動許容 / 静止時間 / **同じ場所は再捕捉しない** / **アイコン領域では捕捉しない**。加えて自プロセスの要素は捕捉しない (§9 — 黙って何もしない)。「同じ場所は再捕捉しない」により、一度自プロセスに覆われた座標は**二度と捕捉されない** — 症状は「待っても選択されない」で、原因が別のものに見える。ピッカーやホストの窓が捕捉点を覆う配置は、この形で壊れる。

**実カーソル (共有のポインター) を読むピッカー同士だけ調停する。**マウス自動選択が入っているピッカーは全部が同じ点を捕捉するので、ホストは新しいピッカーを開く前に、共有ポインターを読んでいる既存のピッカーの自動選択を切る (`StopAutoSelect`)。`ICursorSource` を注入されたピッカー同士はそもそも競合しないので止めない — 止めると、位置を注入する公開された使い方 (複数ピッカーが独立に追従する) が原理的に成立しなくなる。これは開いた瞬間の一度きりの調停であって不変条件ではない — カーソルが 1 つである以上、両方の自動選択を手で戻せばまた同じ点を見る。

---

## §13 判断の台帳

所見 ID (A / B / C / D / L / S) を安定アンカーとして保存する。各行は現在形の不変条件 1 つと、詳細を書いた節を指す。T1〜T6 (検証の層) と K1〜K5 / M1〜M2 (合成入力の検査項目) は docs/TESTING.md が正である。

| ID | 不変条件 | 詳細 |
|---|---|---|
| A1 | コンシューマの例外はプロセスを落とさない。発火配送は捕捉して `UnhandledException` へ回す | §3 |
| A2 | 発火イベントは検出順に届く。配送は単一ワーカーの直列である | §3 |
| A3 | 兄弟の増減で解決は壊れない。`SiblingIndex` は同点タイブレークのみに使う | §3 |
| A4 | 揮発属性と安定属性を同じスコア空間で合計して閾値に掛けない。足切りは `Required` 宣言の属性だけ | §3 |
| A5 | `ControlType` 不一致は除外ではなく重い減点。`AutomationId` 一致が上回る (重みの不等式で固定) | §3 |
| A6 | 段ごとの貪欲選択をしない。ビーム探索 (既定幅 3) でバックトラックできる | §3 |
| A7 | 兄弟インデックスの「不明」は -1 で表現する。誤った index を黙って永続化しない | §3 |
| A8 | 解決済み要素は安定属性の組を 1 往復で突合し、「生きているが別物」を検出する | §8 |
| A9 | 購読の張り替え判定に HWND 値の一致だけを使わない。HWND は再利用される — 要素同一性まで確認する | §8 |
| A10 | 昇格プロセスの除外は通知する。`InaccessibleProcessCount` を数え、`ResolutionChanged.Message` とログに理由を載せる | §3 |
| A11 | `COMException` は判別する。「要素消滅」は `ComErrors.IsElementGone` に集約し、`RPC_E_DISCONNECTED` 等も含める | §3 |
| A12 | 数値比較は `PropertyClause.Tolerance` を持つ。double の厳密比較は実用上一致しない | §3 |
| A13 | bool の比較形は `true` / `false`。`Equals` は bool として大小文字を問わず解釈する | §3 |
| A14 | sweep のデバウンス状態は `SweepDebouncer` に封じ、停止時に `Reset()` する | §3 |
| A15 | UIA の out は nullable で宣言する。`ElementFromPoint` / `ElementFromHandle` は null を返しうる | §3 |
| A16 | `StartAsync` の `CancellationToken` は「実行開始前のみキャンセル可」で honor する。偽の affordance を置かない | §3 |
| A17 | トリガーファイルの保存は temp + `File.Replace` の原子的書き込み。truncate-in-place しない | §3 |
| A18 | オーバーレイの状態は HWND / フックスレッド索引の登録表で持つ。ピッカーは複数開ける | §10 |
| A19 | DPI 非認識のホストでは座標仮想化により別の要素が静かに記録される。PerMonitorV2 を前提とし、宣言できていなければ `CoordinateProblem` として必ず報告する | §9 |
| A20 | `NonBacktracking` は後方参照・先読み等に `NotSupportedException` を投げる (`ArgumentException` ではない)。検証はそれも定義の誤りとして捕まえる | §2 |
| A21 | ネイティブ UIA プロバイダーの切り離された要素はゾンビとして応答し続ける。生存判定は下向きの到達性 (`IsStillOnThePath`) と、ウィンドウ自身には `IsWindow` を使う | §8 |
| A22 | (欠番 — この ID の所見は定義されていない) | — |
| A23 | 自アセンブリ外の WinRT 値型は AOT で vtable が生成されず、例外なく動かなくなる。`GeneratedWinRTExposedExternalType` はライブラリ (`Picker.WinUI`) 側に置く | §12 |
| B1 | プロパティ読取は `CacheRequest` でまとめる。段あたり 1 往復・スナップショット 1 往復 | §3 |
| B2 | `WindowOpened` は `Children`。`WindowClosed` は `Subtree` でなければ 1 件も届かない | §6 |
| B3 | 解決済みの構造購読は経路上の各段を `Element \| Children` で張る。ウィンドウ全体の `Subtree` にしない | §6 |
| B4 | ウィンドウ候補は `WindowCandidateCache` で共有する。照合の強さもキャッシュのキーに含める | §3 |
| B5 | `IUIAutomation2.put_TransactionTimeout` を既定で設定する。応答しないアプリ 1 つが他のトリガーを止めない | §3 |
| B6 | COM の決定的解放は `FinalRelease()` で、`UniqueInstance` の RCW に限る。共有 RCW には安全でない | §7 |
| B7 | RCW の生成は `ComInterfaceMarshaller<T>` に統一する。同一性テーブルを 2 系統に分裂させない | §7 |
| B8 | デバウンスは `TimeProvider.CreateTimer`。時計は注入される | §3 |
| B9 | 兄弟走査はキャッシュ済み属性で絞ってから `CompareElements` を呼ぶ | §3 |
| C1 | トリガーは動的に増減できる (`AddAsync` / `RemoveAsync`)。停止は張った単位で外し、セッション共有時に他の購読者を巻き添えにしない | §3 |
| C2 | Core は「UIA セッション + 監視」である。`UiaSession` が要素 API を公開し、第三者が自前のピッカー / インスペクタを作れる | §3 |
| C3〜C6 | トリガーモデル: キー (`Id`) はモデルに内包する / ライフサイクル (`TriggerOn`) と値の述語 (`PropertyClause`) を分離する / 句は複数持てて平坦に結合する / `TriggerProperty.Custom` + `CustomPropertyId` が任意プロパティへの逃げ道である | §3 |
| C7 | 実装詳細は公開しない。`UiaTrigger.Interop` / `UiaTrigger.Threading` は公開型ゼロ、公開型は `UiaTrigger` 名前空間に置く | §3 |
| C8 | 列挙の永続形式はモデル自身が持つ (`[JsonConverter(JsonStringEnumConverter<T>)]`)。ホストが合成した serializer 経路には設定が引き継がれない | §3 |
| C9 | ログは `ILogger` (`Microsoft.Extensions.Logging.Abstractions` が唯一の実行時依存)。ログのメッセージは英語固定 | §3 |
| C10 | 時刻は `TimeProvider` 注入。発火時刻・レート制限・デバウンスがすべて同じ時計に従う | §3 |
| C11 | `TriggerDefinition.MinInterval` が発火レート制限。`BoundingRectangle` の比較文字列は `ElementRect.ToString` の invariant な `(L,T)-(R,B)` 形式で、表示と比較で同一 | §3 |
| C12 | `IsPassword` の要素は `Value` と `Name` を伏字化する。条件評価もスナップショット経由なので伏せた値は復活しない | §3 |
| C13 | ピッカーはホストの PerMonitorV2 を実行時に確かめ、`CoordinateProblem` を画面に出す | §9 |
| C14 | 立ち下がり通知 (`NotifyOnStoppedMatching` → イベントは `On=StoppedMatching`) は `WhileMatching` 専用で、`MinInterval` の対象外。停止・削除では通知しない | §4 |
| C15 | `ElementRemoved` の条件は消滅直前の値で評価する。句付き `ElementRemoved` は監視プロパティを購読して `LastSnapshot` を最新に保つ (発火源にはしない) | §4 |
| C16 | 在否 (`IsAbsent`) と値は別の軸。`Op=Always` は「要素が在ること」(presence) で成立し、値の述語は消えた要素でも最後に見えた値で評価され続ける | §4 |
| D1 | 純ロジック層は UIA 非依存の継ぎ目を持ち、COM 無しでテストできる | docs/TESTING.md §1 |
| D2 | CI が常時走る。AOT 発行の破壊は interop の変更で AOT 発行時にしか失敗しないものがあるため、発行までを CI が通す | docs/TESTING.md §1 |
| D3 | `TreatWarningsAsErrors=true`。警告 0 がビルドの不変条件である | — |
| D4 | NuGet 5 パッケージ / MIT / プレリリース版数から。パッケージは全て MSIL | §1 |
| D5 | ライブラリは AnyCPU。App の RID は `Platform` から導き、ARM64 でも建つ | §12 |
| D6 | README は実装と一致させる。英語版が正である | docs/LOCALIZATION.md |
| D7 | サンプルは XAML 未処理例外を握り潰さない。`UnhandledException` はログへ出す | §12 |
| D8 | 昇格アプリを監視できない制約は文書と実行時通知 (A10) の両方で明示する | §3 |
| D9 | `App.WinUI` だけが Picker → Monitor の E2E ショーケースを兼ねる。意図的な非対称である | §12 |
| L1 | 公開 API の XML doc は英語。実装内部のコメントは日本語のままでよい (経緯の記録として価値がある) | docs/LOCALIZATION.md |
| L2 | 例外・診断メッセージはリソース経由 (en-US 中立 + ja サテライト)。ハードコードしない | docs/LOCALIZATION.md |
| L3 | WinUI の UI 文字列は `.resw` + `x:Uid` + MRT Core | docs/LOCALIZATION.md |
| L4 | 比較文字列 (`ComparisonString` / invariant) と表示文字列 (`CurrentCulture`) は型で分離し、条件評価は前者しか受けない | docs/LOCALIZATION.md |
| L5 | `InvariantGlobalization` は false。サテライトが要る | docs/LOCALIZATION.md |
| L6 | 識別用の安定名 (英語固定・永続化する) と表示用 `LocalizedControlType` (相手アプリのロケール・永続化しない) を分離する | docs/LOCALIZATION.md |
| L7 | invariant にすべきもの (オプション解釈・ログ) とカルチャに従うもの (画面表示) を 1 つの文字列に混ぜない | docs/LOCALIZATION.md |
| L8 | `README.md` = 英語 (正・パッケージ同梱・リンクは絶対 URL) / `README.ja.md` = 日本語 | docs/LOCALIZATION.md |
| S1 | オーバーレイは UIA から観測できる実ウィンドウであり、2 枚のピッカーは独立の枠を出す | §10 |
| S2 | 確定 → 条件設定 → コミットの 1 往復。真偽の根拠は画面の文字列ではなくトリガーファイルに置く | docs/TESTING.md §1 |
| S3 | 表示名と安定名の分離 (L6 と同じ対象) | docs/LOCALIZATION.md |
| S4 | リソース解決は発行レイアウトで確かめる。`.pri` / サテライトは発行してからでないと落ちない | docs/TESTING.md §1 |
| T1〜T6 | 検証の層の定義と分担 | → docs/TESTING.md §1 |
| K1〜K5 / M1〜M2 | 合成入力 (T5) の検査項目と入力政策 | → docs/TESTING.md §3 |
