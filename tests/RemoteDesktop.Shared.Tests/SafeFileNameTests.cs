using RemoteDesktop.Shared.Util;
using Xunit;

namespace RemoteDesktop.Shared.Tests;

public class SafeFileNameTests
{
    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("..\\..\\windows\\system32\\cmd.exe", "cmd.exe")]
    [InlineData("normal.txt", "normal.txt")]
    public void StripsDirectoryComponents(string input, string expected)
        => Assert.Equal(expected, SafeFileName.Sanitize(input));

    [Theory]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("LPT1.log")]
    public void ReservedNames_ArePrefixed(string input)
        => Assert.StartsWith("_", SafeFileName.Sanitize(input));

    [Fact]
    public void TrailingDotsAndSpaces_AreStripped()
        => Assert.Equal("evil.txt", SafeFileName.Sanitize("evil.txt. . "));

    [Fact]
    public void AlternateDataStreamColon_IsRemoved()
        => Assert.DoesNotContain(":", SafeFileName.Sanitize("a.txt:secret"));

    [Fact]
    public void EmptyAfterSanitize_UsesFallback()
        => Assert.Equal("download", SafeFileName.Sanitize("...."));

    [Fact]
    public void ResolveCollision_AppendsSuffix()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rd-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string first = SafeFileName.ResolveCollision(dir, "file.txt");
            File.WriteAllText(first, "x");
            string second = SafeFileName.ResolveCollision(dir, "file.txt");
            Assert.EndsWith("file (1).txt", second);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
