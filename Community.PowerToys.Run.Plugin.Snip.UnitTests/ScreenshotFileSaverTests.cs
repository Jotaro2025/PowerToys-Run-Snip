// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Threading.Tasks;

using Community.PowerToys.Run.Plugin.Snip.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public class ScreenshotFileSaverTests
{
    private string _tempDirectory;

    [TestInitialize]
    public void Setup()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "SnipTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, true);
        }
    }

    [TestMethod]
    public void ResolveDirectoryFallsBackWhenEmpty()
    {
        Assert.AreEqual(@"C:\fallback", ScreenshotFileSaver.ResolveDirectory(null, @"C:\fallback"));
        Assert.AreEqual(@"C:\fallback", ScreenshotFileSaver.ResolveDirectory("   ", @"C:\fallback"));
    }

    [TestMethod]
    public void ResolveDirectoryTrimsAndStripsTrailingSlashes()
    {
        Assert.AreEqual(@"C:\shots", ScreenshotFileSaver.ResolveDirectory(@"  C:\shots\\  ", @"C:\fallback"));
    }

    [TestMethod]
    public void ResolveDirectoryFallsBackOnInvalidOrRelativePaths()
    {
        // Invalid path characters degrade to the fallback instead of failing every save.
        Assert.AreEqual(@"C:\fallback", ScreenshotFileSaver.ResolveDirectory(@"C:\Sho<ts", @"C:\fallback"));
        Assert.AreEqual(@"C:\fallback", ScreenshotFileSaver.ResolveDirectory("C:\\a\"b", @"C:\fallback"));

        // Relative paths and stray drive colons are not usable as save targets.
        Assert.AreEqual(@"C:\fallback", ScreenshotFileSaver.ResolveDirectory("shots", @"C:\fallback"));
        Assert.AreEqual(@"C:\fallback", ScreenshotFileSaver.ResolveDirectory(@"C:\dir\we:rd", @"C:\fallback"));

        // Well-formed directories still resolve (trailing separators are trimmed).
        Assert.AreEqual(@"D:\screenshots", ScreenshotFileSaver.ResolveDirectory(@"D:\screenshots\", @"C:\fallback"));
        Assert.AreEqual(@"C:\fallback", ScreenshotFileSaver.ResolveDirectory(null, @"C:\fallback"));
    }

    [TestMethod]
    public void BuildFileNameUsesSnapshotPrefixAndPngExtension()
    {
        var name = ScreenshotFileSaver.BuildFileName();

        StringAssert.StartsWith(name, "snapshot_");
        StringAssert.EndsWith(name, ".png");
    }

    [TestMethod]
    public void BuildTranslatedFileNameUsesTranslatePrefixAndPngExtension()
    {
        var name = ScreenshotFileSaver.BuildTranslatedFileName();

        StringAssert.StartsWith(name, "translate_");
        StringAssert.EndsWith(name, ".png");
    }

    [TestMethod]
    public void BuildUniquePathDoesNotOverwriteExistingFile()
    {
        var first = ScreenshotFileSaver.BuildUniquePath(_tempDirectory);
        File.WriteAllBytes(first, new byte[] { 1, 2, 3 });

        var second = ScreenshotFileSaver.BuildUniquePath(_tempDirectory);

        Assert.AreNotEqual(first, second);
        Assert.IsFalse(File.Exists(second));
    }

    [TestMethod]
    public async Task SavePngAsyncWritesFileAndCreatesDirectory()
    {
        var target = Path.Combine(_tempDirectory, "sub", "dir");
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47 };

        var (ok, path, error) = await ScreenshotFileSaver.SavePngAsync(png, target, _tempDirectory);

        Assert.IsTrue(ok, error);
        Assert.IsTrue(File.Exists(path));
        Assert.HasCount(4, await File.ReadAllBytesAsync(path));
    }

    [TestMethod]
    public async Task SavePngAsyncUsesFallbackDirectoryWhenNotConfigured()
    {
        var png = new byte[] { 1 };

        var (ok, path, error) = await ScreenshotFileSaver.SavePngAsync(png, null, _tempDirectory);

        Assert.IsTrue(ok, error);
        Assert.AreEqual(_tempDirectory, Path.GetDirectoryName(path));
    }

    [TestMethod]
    public async Task SavePngAsyncReportsErrorForUnwritableDirectory()
    {
        // A path whose parent is an existing file can never become a directory.
        var filePath = Path.Combine(_tempDirectory, "afile");
        File.WriteAllBytes(filePath, new byte[] { 1 });
        var invalid = Path.Combine(filePath, "sub");

        var (ok, _, error) = await ScreenshotFileSaver.SavePngAsync(new byte[] { 1 }, invalid, _tempDirectory);

        Assert.IsFalse(ok);
        Assert.IsFalse(string.IsNullOrEmpty(error));
    }
}
