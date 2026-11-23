using System;
using Tesserafin.Model.IO;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.IO;

public class FilePathTests
{
    [Theory]
    [InlineData("/Volumes/Library/Sample/Music/Playlists/", "../Beethoven/Misc/Moonlight Sonata.mp3", "/Volumes/Library/Sample/Music/Beethoven/Misc/Moonlight Sonata.mp3")]
    [InlineData("/Volumes/Library/Sample/Music/Playlists/", "../../Beethoven/Misc/Moonlight Sonata.mp3", "/Volumes/Library/Sample/Beethoven/Misc/Moonlight Sonata.mp3")]
    [InlineData("/Volumes/Library/Sample/Music/Playlists/", "Beethoven/Misc/Moonlight Sonata.mp3", "/Volumes/Library/Sample/Music/Playlists/Beethoven/Misc/Moonlight Sonata.mp3")]
    [InlineData("/Volumes/Library/Sample/Music/Playlists/", "/mnt/Beethoven/Misc/Moonlight Sonata.mp3", "/mnt/Beethoven/Misc/Moonlight Sonata.mp3")]
    public void MakeAbsolutePathCorrectlyHandlesRelativeFilePathsOnUnixLike(
        string folderPath,
        string filePath,
        string expectedAbsolutePath)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix-only test");

        var generatedPath = FilePath.MakeAbsolute(folderPath, filePath);
        Assert.Equal(expectedAbsolutePath, generatedPath);
    }

    [Theory]
    [InlineData(@"C:\\Volumes\Library\Sample\Music\Playlists\", @"..\Beethoven\Misc\Moonlight Sonata.mp3", @"C:\Volumes\Library\Sample\Music\Beethoven\Misc\Moonlight Sonata.mp3")]
    [InlineData(@"C:\\Volumes\Library\Sample\Music\Playlists\", @"..\..\Beethoven\Misc\Moonlight Sonata.mp3", @"C:\Volumes\Library\Sample\Beethoven\Misc\Moonlight Sonata.mp3")]
    [InlineData(@"C:\\Volumes\Library\Sample\Music\Playlists\", @"Beethoven\Misc\Moonlight Sonata.mp3", @"C:\Volumes\Library\Sample\Music\Playlists\Beethoven\Misc\Moonlight Sonata.mp3")]
    [InlineData(@"C:\\Volumes\Library\Sample\Music\Playlists\", @"D:\\Beethoven\Misc\Moonlight Sonata.mp3", @"D:\\Beethoven\Misc\Moonlight Sonata.mp3")]
    public void MakeAbsolutePathCorrectlyHandlesRelativeFilePathsOnWindows(
        string folderPath,
        string filePath,
        string expectedAbsolutePath)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only test");

        var generatedPath = FilePath.MakeAbsolute(folderPath, filePath);

        Assert.Equal(expectedAbsolutePath, generatedPath);
    }
}
