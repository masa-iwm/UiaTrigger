using Xunit;

// T5 は実機のキーボードとマウスそのものを奪う。並列に走らせると、あるテストが撃った ←/→ を
// 別のテストのピッカーが受け取る — 低レベルフックはグローバルなので、
// ウィンドウを分けても分離できない (docs/TESTING.md §3)。
//
// これはアセンブリ単位の指定なので、別プロセスで走る T3 / T4 までは止められない
// (csproj の先頭コメントを参照)。CI でも picker-ui と同じランナーに載せないこと。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
