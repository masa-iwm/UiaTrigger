namespace UiaTrigger.Tests;

/// <summary>
/// リポジトリ内のファイルを参照するテスト用のパス解決。
/// ビルド出力ディレクトリから上に辿って UiaTrigger.slnx を探す。
/// </summary>
internal static class RepoPaths
{
    private static readonly Lazy<DirectoryInfo> RootLazy = new(FindRoot);

    public static DirectoryInfo Root => RootLazy.Value;

    public static string Combine(params string[] relativeParts)
        => Path.Combine([Root.FullName, .. relativeParts]);

    private static DirectoryInfo FindRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "UiaTrigger.slnx")))
            {
                return dir;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            $"UiaTrigger.slnx が見つかりません (探索起点: {AppContext.BaseDirectory})。");
    }
}
