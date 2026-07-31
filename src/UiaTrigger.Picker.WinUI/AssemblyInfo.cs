// AOT 発行のための WinRT vtable 登録 (docs/DESIGN.md §12)。
//
// <b>A23: 発行物 (Native AOT) では GridSplitter を掴んでも動かなかった。</b>
// カーソルは境界で ↔ に変わり、ドラッグもキーボード (Tab + ←/→) も反応しない —
// つまりポインターは届いており、<b>寸法を書き戻すところで落ちていた</b>。
//
// CommunityToolkit の Sizer は寸法変更を `ColumnDefinition.Width = new GridLength(...)` で
// 行う。`GridLength` は<b>自分たちのアセンブリの外にある値型</b>なので、
// AOT では CsWinRT が vtable を生成せず、WinRT の ABI を越える代入が成立しない。
// <b>例外は出ず、ただ動かない。</b>
//
// <b>ここに置くのは利用者のためである。</b>この属性はアセンブリ単位で効き、
// 生成された登録は実行時に集約される。`UiaTrigger.Picker.WinUI` に置いておけば、
// このパッケージを参照して<b>自分のアプリを AOT 発行する利用者</b>も同じ穴を踏まない。
// ホスト側 (`UiaTrigger.App.WinUI`) に置くと、直るのはサンプルだけになる。
//
// <b>この形の不具合は「発行して人が掴む」まで表に出ない。</b>
// 同じ層をビルドで捕まえるために、両プロジェクトで `CsWinRTAotWarningLevel` を 3 にしてある。
using Microsoft.UI.Xaml;

[assembly: WinRT.GeneratedWinRTExposedExternalType(typeof(GridLength))]
