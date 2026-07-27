using CpuAffinityManager.Engine;

namespace CpuAffinityManager.Tests;

public class WildcardTests
{
    [Theory]
    [InlineData("game.exe", "game.exe", true)]
    [InlineData("game.exe", "*.exe", true)]
    [InlineData("game.dll", "*.exe", false)]
    [InlineData("GAME.EXE", "game.exe", true)]  // case insensitive
    [InlineData("game2024.exe", "game*.exe", true)]
    [InlineData("gametest.exe", "game*.exe", true)]
    [InlineData("app1.exe", "app?.exe", true)]
    [InlineData("app12.exe", "app?.exe", false)]
    [InlineData("appx.exe", "app?.exe", true)]
    [InlineData("test", "*", true)]
    [InlineData("", "*.exe", false)]
    [InlineData("game.exe", "", false)]
    public void Match_SinglePattern_ReturnsExpected(string input, string pattern, bool expected)
    {
        bool result = Wildcard.Match(input, pattern);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("game.exe", "*.dll|*.exe", true)]
    [InlineData("test.dll", "*.dll|*.exe", true)]
    [InlineData("test.txt", "*.dll|*.exe", false)]
    [InlineData("cpuz.exe", "cpuz*.exe|cpu-z*.exe", true)]
    [InlineData("cpu-z_x64.exe", "cpuz*.exe|cpu-z*.exe", true)]
    public void Match_OrPattern_ReturnsExpected(string input, string pattern, bool expected)
    {
        bool result = Wildcard.Match(input, pattern);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("app1.exe", "app[0-9].exe", true)]
    [InlineData("app9.exe", "app[0-9].exe", true)]
    [InlineData("appx.exe", "app[0-9].exe", false)]
    [InlineData("app10.exe", "app[0-9].exe", false)] // single char match only
    [InlineData("appA.exe", "app[A-F].exe", true)]
    [InlineData("appG.exe", "app[A-F].exe", false)]
    [InlineData("test.txt", "test[!a-z].txt", false)]
    [InlineData("test1.txt", "test[!a-z].txt", true)]
    public void Match_CharClass_ReturnsExpected(string input, string pattern, bool expected)
    {
        bool result = Wildcard.Match(input, pattern);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void MatchPath_ExactPath_ReturnsTrue()
    {
        bool result = Wildcard.MatchPath(@"C:\Windows\System32\svchost.exe",
                                         @"C:\Windows\System32\svchost.exe");
        Assert.True(result);
    }

    [Fact]
    public void MatchPath_DoubleStar_MatchesMultiLevel()
    {
        bool result = Wildcard.MatchPath(@"D:\Games\Steam\Game\bin\game.exe",
                                         @"D:\Games\**\*.exe");
        Assert.True(result);
    }

    [Fact]
    public void MatchPath_DoubleStar_DoesNotMatchWrongRoot()
    {
        bool result = Wildcard.MatchPath(@"C:\Other\game.exe",
                                         @"D:\Games\**\*.exe");
        Assert.False(result);
    }

    [Fact]
    public void MatchPath_SingleSegment_MatchesFilename()
    {
        bool result = Wildcard.Match(@"svchost.exe", "svchost.exe");
        Assert.True(result);
    }
}
