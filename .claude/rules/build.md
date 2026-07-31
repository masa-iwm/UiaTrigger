# ビルド規律

- **ビルドはソリューション経由のみ**:

  ```powershell
  dotnet build UiaTrigger.slnx -c Release
  dotnet test  UiaTrigger.slnx -c Release   # 先に build (manifest 検査がビルド成果物を読む)
  ```

- **単体プロジェクトのビルド禁止。**`dotnet build src/<プロジェクト>` や `dotnet build tests/…` は
  `bin\Debug\...` という別出力を作り、ソリューションビルドの `bin\x64\...` と並存する。
  T4 の `PickerHostProcess` は「いちばん新しい exe」を採るので、単体ビルドの直後から
  別レイアウトのホストを黙って起動しはじめる。踏んだら `bin\Debug` (x64 無しの側) を消して
  ソリューションで建て直す (docs/TESTING.md §6)。サンプルホストは `dotnet test` のビルドでも
  更新されない。
- `TreatWarningsAsErrors=true`。警告 1 つでビルドが失敗する。抑制で通さず原因を直す。
- T4 をローカルで走らせる前に、前回の実行が残した窓を掃除する:

  ```powershell
  Get-Process | Where-Object { $_.Name -like "UiaTrigger*" } | Stop-Process -Force
  ```

  残った窓 (対象アプリ / ホスト / ランナー) は無関係なテストを座標で落とし、落ちる顔ぶれは
  実行のたびに変わる。**ランナーが緑で手元が赤いときは、まずこれを疑う。**
- **`gh run watch --exit-status` の終了コードを信用しない** — 赤い実行に対して 0 を返した
  実績がある。CI の判定は `gh run list` / `gh run view` で取る (docs/TESTING.md §6)。
