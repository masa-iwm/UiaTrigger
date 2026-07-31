using Xunit;

// 実プロセスを起動し、ウィンドウを画面に出して座標から要素を記録するテスト群である。
// 並列に走らせるとウィンドウが重なり、ElementFromPoint が別のテストのウィンドウを掴む。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
