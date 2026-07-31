# ローカライズ規律

新しい文字列を書く前の即席判定 (正典は docs/LOCALIZATION.md §3):

1. **ユーザーの画面に出るか?** → `CurrentUICulture`。必ず `.resx` / `.resw` 経由。
   ハードコードは lint (`NoSourceAssignsAUserFacingLiteral`) が検出する。
   `en-us` / `ja-jp` のキー集合一致はテストが縛る。
2. **比較・永続化に入るか?** → 常に `InvariantCulture` / `Ordinal`。ローカライズしない。
   条件評価の値・JSON・キー・`UiaControlTypeNames.GetName` の戻り値がこれ。
   表示と比較を混ぜると条件評価が**例外を出さずに**壊れる。
3. **開発者向けログか?** → 英語固定 (共有・grep のため)。時刻は invariant の `HH:mm:ss.fff`。
4. **公開 API の XML doc か?** → 英語で書く。日本語は `Resources/<アセンブリ名>.ja.xml` に
   書き足す。

表示にも永続化にも見えるものは 2 (永続化側) に倒す — `GetName` の戻り値が実例で、
リソース経由にすると保存済み定義の意味が変わる (docs/LOCALIZATION.md §6)。

ja.xml は公開 API と 1:1 (docs/LOCALIZATION.md §5):

- `ja/` の doc ファイルは**丸ごと**使われる。無いメンバーは英語に落ちるのではなく
  **説明が消える**。
- 過不足なしを `TheJapaneseDocumentationCoversExactlyThePublicApi` が両方向で縛る —
  公開 API を足したら ja.xml も書き足さないと CI が落ちる。

`InvariantGlobalization` (プロセス全体のモード) は必ず `false` — `true` にするとサテライト
そのものが使えなくなる。invariant にするのは**箇所ごとの書式選択**である
(docs/LOCALIZATION.md §7)。
