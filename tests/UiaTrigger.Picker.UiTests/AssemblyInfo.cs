using Xunit;

// サンプルホストと対象アプリを実プロセスで起動し、画面に出たウィンドウを UIA で駆動する。
// 並列に走らせるとウィンドウが重なり、固定座標のホバー捕捉が別のテストの対象を掴む。
//
// これはアセンブリ単位の指定なので、別プロセスで走る UiaTrigger.RealUia.Tests までは止められない
// (csproj の先頭コメントを参照)。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
