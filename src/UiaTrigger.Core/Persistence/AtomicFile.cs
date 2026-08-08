// クラッシュ安全なファイル置換 (docs/DESIGN.md A17)。
//
// File.Create による truncate-in-place の保存では、書き込み中にプロセスが落ちると
// 全トリガー定義が失われる。
// 同一ディレクトリの一時ファイルに書き切ってから 1 回の置換操作で差し替える。
using System.Globalization;

namespace UiaTrigger.Persistence;

/// <summary>
/// Replaces a file's contents without ever leaving a truncated or partially written file behind.
/// </summary>
public static class AtomicFile
{
    /// <summary>
    /// Writes the content produced by <paramref name="writeContent"/> to <paramref name="path"/>,
    /// replacing any existing file in a single operation.
    /// </summary>
    /// <param name="path">Destination file path. Its directory is created if it does not exist.</param>
    /// <param name="writeContent">
    /// Callback that writes the complete new content to the supplied stream. If it throws, the
    /// destination file is left untouched and the temporary file is removed.
    /// </param>
    /// <remarks>
    /// The content is first written to a temporary file in the destination directory, flushed to
    /// disk, and only then swapped in. A crash at any point leaves either the previous content or
    /// the new content — never a mixture of the two. The temporary file must live on the same
    /// volume as the destination, which is why the destination directory is used.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null or empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="writeContent"/> is null.</exception>
    public static void Write(string path, Action<Stream> writeContent)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(writeContent);

        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException($"'{path}' has no directory component.", nameof(path));
        Directory.CreateDirectory(directory);

        // 一時ファイルは必ず同一ディレクトリに作る (別ボリュームだと置換が原子的にならない)。
        // 名前を一意にして、同じファイルへの同時書き込みが互いの一時ファイルを壊さないようにする。
        string tempPath = Path.Combine(
            directory,
            $"{Path.GetFileName(fullPath)}.{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)}.tmp");

        // 前回のクラッシュが残した一時ファイルを回収する。名前が一意なぶん、掃除する主体が
        // 居ないと保存のたびに 1 つずつ増え続ける (誰も消さないので永久に残る)。
        // **今回の一時ファイルを作る前に**やること — 自分の書き込みを消さないため
        CleanUpOrphanedTempFiles(directory, Path.GetFileName(fullPath), tempPath);

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                writeContent(stream);
                // 置換前にディスクまで書き切る。ここを省くと、置換自体は成功しても
                // 直後の電源断で「空の新ファイル」が残りうる。
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(fullPath))
            {
                try
                {
                    // File.Replace は置換に失敗しても元ファイルを残す (ReplaceFile API)
                    File.Replace(tempPath, fullPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                catch (FileNotFoundException)
                {
                    // Exists 判定と Replace の間に他者が宛先を消した (Move 側の逆向きの競合)。
                    // 置換すべきものが無いだけなので、そのまま名前を付ける
                    File.Move(tempPath, fullPath);
                }
            }
            else
            {
                try
                {
                    File.Move(tempPath, fullPath);
                }
                catch (IOException) when (File.Exists(fullPath))
                {
                    // Exists 判定と Move の間に他者が作成した場合
                    File.Replace(tempPath, fullPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
            }
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>
    /// 同じ宛先に対する、この書き込み以外の一時ファイルを消す。
    /// </summary>
    /// <remarks>
    /// 掃除に失敗しても保存は続ける (残骸は次回また試せる)。他のプロセスが書いている最中の
    /// 一時ファイルは <see cref="FileShare.None"/> で開かれているので削除に失敗し、
    /// そのまま残る — 消してよいものだけが消える形になっている。
    /// </remarks>
    private static void CleanUpOrphanedTempFiles(string directory, string fileName, string currentTempPath)
    {
        try
        {
            foreach (string candidate in Directory.EnumerateFiles(directory, $"{fileName}.*.tmp"))
            {
                if (!string.Equals(candidate, currentTempPath, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(candidate);
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // 一時ファイルの後始末に失敗しても、本来の失敗理由を覆い隠さない
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
