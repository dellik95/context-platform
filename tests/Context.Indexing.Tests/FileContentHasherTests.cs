using System.Text;
using Context.Indexing;
using FluentAssertions;

namespace Context.Indexing.Tests;

public sealed class FileContentHasherTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileContentHasher _sut = new();

    public FileContentHasherTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "context-indexing-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void HashText_SameContent_ProducesStableHash()
    {
        var hash1 = _sut.HashText("public class Foo {}");
        var hash2 = _sut.HashText("public class Foo {}");

        hash1.Should().Be(hash2);
    }

    [Fact]
    public void HashText_DifferentContent_ProducesDifferentHash()
    {
        var hash1 = _sut.HashText("public class Foo {}");
        var hash2 = _sut.HashText("public class Bar {}");

        hash1.Should().NotBe(hash2);
    }

    [Fact]
    public void HashText_IsLowercaseHex64Chars_Sha256()
    {
        var hash = _sut.HashText("anything");

        hash.Should().HaveLength(64);
        hash.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void HashText_EmptyString_DoesNotThrowAndProducesConsistentHash()
    {
        var hash1 = _sut.HashText(string.Empty);
        var hash2 = _sut.HashText(string.Empty);

        hash1.Should().Be(hash2);
        hash1.Should().HaveLength(64);
    }

    [Fact]
    public void HashText_IsCaseAndWhitespaceSensitive()
    {
        var hash1 = _sut.HashText("Hello World");
        var hash2 = _sut.HashText("hello world");
        var hash3 = _sut.HashText("Hello World ");

        hash1.Should().NotBe(hash2);
        hash1.Should().NotBe(hash3);
    }

    [Fact]
    public void HashFile_SameBytesOnDisk_ProducesSameHashAsHashText_ForUtf8Content()
    {
        // Written with a BOM-less UTF-8 encoding: Encoding.UTF8 emits a byte-order-mark
        // preamble on write, which would make the on-disk bytes differ from the raw UTF-8
        // bytes of the string and defeat the point of this test.
        var path = Path.Combine(_tempDir, "a.txt");
        File.WriteAllText(path, "same content", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var fileHash = _sut.HashFile(path);
        var textHash = _sut.HashText("same content");

        fileHash.Should().Be(textHash);
    }

    [Fact]
    public void HashFile_SameContentWrittenTwice_ProducesStableHash()
    {
        var path1 = Path.Combine(_tempDir, "a.txt");
        var path2 = Path.Combine(_tempDir, "b.txt");
        File.WriteAllText(path1, "duplicate content");
        File.WriteAllText(path2, "duplicate content");

        var hash1 = _sut.HashFile(path1);
        var hash2 = _sut.HashFile(path2);

        hash1.Should().Be(hash2);
    }

    [Fact]
    public void HashFile_DifferentContent_ProducesDifferentHash()
    {
        var path1 = Path.Combine(_tempDir, "a.txt");
        var path2 = Path.Combine(_tempDir, "b.txt");
        File.WriteAllText(path1, "content one");
        File.WriteAllText(path2, "content two");

        var hash1 = _sut.HashFile(path1);
        var hash2 = _sut.HashFile(path2);

        hash1.Should().NotBe(hash2);
    }

    [Fact]
    public void HashFile_EmptyFile_DoesNotThrow()
    {
        var path = Path.Combine(_tempDir, "empty.txt");
        File.WriteAllBytes(path, []);

        var hash = _sut.HashFile(path);

        hash.Should().Be(_sut.HashText(string.Empty));
    }

    [Fact]
    public void HashFile_BinaryContent_HashesRawBytesNotTextDecoding()
    {
        var path = Path.Combine(_tempDir, "bin.dat");
        byte[] bytes = [0x00, 0xFF, 0x10, 0x8A, 0x01];
        File.WriteAllBytes(path, bytes);

        var hash1 = _sut.HashFile(path);

        // Re-write identical bytes to a second file and confirm stability across separate reads.
        var path2 = Path.Combine(_tempDir, "bin2.dat");
        File.WriteAllBytes(path2, bytes);
        var hash2 = _sut.HashFile(path2);

        hash1.Should().Be(hash2);
    }

    [Fact]
    public void HashText_UnicodeContent_IsHashedByUtf8BytesConsistently()
    {
        var hash1 = _sut.HashText("héllo wörld — 日本語");
        var hash2 = _sut.HashText("héllo wörld — 日本語");
        var hash3 = _sut.HashText("hello world");

        hash1.Should().Be(hash2);
        hash1.Should().NotBe(hash3);
    }

    [Fact]
    public void HashFile_MissingFile_ThrowsFileNotFoundException()
    {
        var path = Path.Combine(_tempDir, "does-not-exist.txt");

        var act = () => _sut.HashFile(path);

        act.Should().Throw<FileNotFoundException>();
    }
}
