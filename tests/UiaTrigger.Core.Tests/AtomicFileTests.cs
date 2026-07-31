using System.Text;
using UiaTrigger.Persistence;
using Xunit;

namespace UiaTrigger.Tests;

/// <summary>
/// トリガー定義の保存が原子的であることの回帰テスト (docs/DESIGN.md A17)。
///
/// 修正前のホストは File.Create による truncate-in-place で保存していたため、
/// 書き込み中にプロセスが落ちると全トリガー定義が失われた。
/// </summary>
public sealed class AtomicFileTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "UiaTrigger.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string PathFor(string name) => Path.Combine(_directory, name);

    private static void WriteText(Stream stream, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
    }

    [Fact]
    public void Write_CreatesTheFile_AndItsDirectory()
    {
        string path = PathFor("nested/triggers.json");

        AtomicFile.Write(path, stream => WriteText(stream, "hello"));

        Assert.Equal("hello", File.ReadAllText(path));
    }

    [Fact]
    public void Write_ReplacesExistingContentEntirely()
    {
        string path = PathFor("triggers.json");
        AtomicFile.Write(path, stream => WriteText(stream, "the previous, much longer content"));

        AtomicFile.Write(path, stream => WriteText(stream, "short"));

        Assert.Equal("short", File.ReadAllText(path));
    }

    /// <summary>
    /// A17 そのもの: 書き込みが途中で失敗しても、既存ファイルが壊れないこと。
    /// truncate-in-place だとここで内容が消える (または途中まで書かれたゴミが残る)。
    /// </summary>
    [Fact]
    public void Write_LeavesTheOriginalIntact_WhenTheWriterThrows()
    {
        string path = PathFor("triggers.json");
        const string Original = """{"trigger":"important"}""";
        AtomicFile.Write(path, stream => WriteText(stream, Original));

        var boom = new InvalidOperationException("serialization blew up halfway");
        var thrown = Assert.Throws<InvalidOperationException>(() =>
            AtomicFile.Write(path, stream =>
            {
                WriteText(stream, "partially written garbage");
                throw boom;
            }));

        Assert.Same(boom, thrown);
        Assert.Equal(Original, File.ReadAllText(path));
    }

    /// <summary>失敗しても一時ファイルを残さないこと (残すと保存先が汚れ続ける)。</summary>
    [Fact]
    public void Write_RemovesItsTemporaryFile_OnFailure()
    {
        string path = PathFor("triggers.json");
        AtomicFile.Write(path, stream => WriteText(stream, "original"));

        Assert.Throws<InvalidOperationException>(() =>
            AtomicFile.Write(path, _ => throw new InvalidOperationException("boom")));

        string[] expected = [path];
        Assert.Equal(expected, Directory.GetFiles(_directory));
    }

    [Fact]
    public void Write_LeavesNoTemporaryFile_OnSuccess()
    {
        string path = PathFor("triggers.json");

        AtomicFile.Write(path, stream => WriteText(stream, "content"));

        string[] expected = [path];
        Assert.Equal(expected, Directory.GetFiles(_directory));
    }

    /// <summary>大きな内容でも欠けずに置換されること (バッファ境界の取りこぼし検出)。</summary>
    [Fact]
    public void Write_HandlesContentLargerThanTheStreamBuffer()
    {
        string path = PathFor("triggers.json");
        string large = new('x', 1_000_000);

        AtomicFile.Write(path, stream => WriteText(stream, large));

        Assert.Equal(large, File.ReadAllText(path));
    }

    [Fact]
    public void Write_RejectsInvalidArguments()
    {
        Assert.Throws<ArgumentNullException>(() => AtomicFile.Write(null!, _ => { }));
        Assert.Throws<ArgumentException>(() => AtomicFile.Write(string.Empty, _ => { }));
        Assert.Throws<ArgumentNullException>(() => AtomicFile.Write(PathFor("x.json"), null!));
    }
}
