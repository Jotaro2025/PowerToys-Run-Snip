// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Linq;
using Community.PowerToys.Run.Plugin.Snip.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public class QrAndBarcodeScannerTests
{
    [TestMethod]
    public void ScanBgraDecodesVersion1QrCodeWithHttpsUrl()
    {
        const string expectedUrl = "https://example.com";
        byte[] bgra = QrAndBarcodeScanner.RenderQrBgra(
            expectedUrl,
            modulePixels: 6,
            quietZoneModules: 4,
            out int width,
            out int height,
            out int stride);

        var results = QrAndBarcodeScanner.ScanBgra(bgra, width, height, stride);
        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("QR", results[0].Format);
        Assert.AreEqual(expectedUrl, results[0].Text);
        Assert.IsNotNull(results[0].HttpUri);
        Assert.AreEqual(new Uri(expectedUrl), results[0].HttpUri);
    }

    [TestMethod]
    public void ScanBgraDecodesCode128Barcode()
    {
        const string expectedPayload = "SNIP-2026";
        byte[] bgra = QrAndBarcodeScanner.RenderCode128Bgra(
            expectedPayload,
            moduleWidth: 4,
            barHeight: 60,
            quietZoneModules: 10,
            out int width,
            out int height,
            out int stride);

        var results = QrAndBarcodeScanner.ScanBgra(bgra, width, height, stride);
        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("CODE-128", results[0].Format);
        Assert.AreEqual(expectedPayload, results[0].Text);
        Assert.IsNull(results[0].HttpUri);
    }


    [TestMethod]
    public void ScanBgraFindsTwoSideBySideQrCodes()
    {
        byte[] first = QrAndBarcodeScanner.RenderQrBgra(
            "https://example.com/a", modulePixels: 4, quietZoneModules: 4,
            out int w1, out int h1, out int s1);
        byte[] second = QrAndBarcodeScanner.RenderQrBgra(
            "https://example.com/b", modulePixels: 4, quietZoneModules: 4,
            out int w2, out int h2, out int s2);

        int canvasW = w1 + w2;
        int canvasH = Math.Max(h1, h2);
        int canvasStride = canvasW * 4;
        var canvas = new byte[canvasStride * canvasH];
        for (int y = 0; y < h1; y++)
        {
            Array.Copy(first, y * s1, canvas, (y * canvasStride), s1);
        }

        for (int y = 0; y < h2; y++)
        {
            Array.Copy(second, y * s2, canvas, (y * canvasStride) + w1 * 4, s2);
        }

        var results = QrAndBarcodeScanner.ScanBgra(canvas, canvasW, canvasH, canvasStride);
        var urls = results.Select(r => r.Text).ToHashSet();
        Assert.IsTrue(urls.Contains("https://example.com/a"), $"first code missing, got: {string.Join("; ", results.Select(r => r.Text))}");
        Assert.IsTrue(urls.Contains("https://example.com/b"), $"second code missing, got: {string.Join("; ", results.Select(r => r.Text))}");
    }

    [TestMethod]
    public void TryNormalizeHttpUrlAcceptsOnlyHttpAndHttps()
    {
        Assert.IsTrue(QrAndBarcodeScanner.TryNormalizeHttpUrl("  https://github.com/Jotaro2025  ", out Uri httpsUri));
        Assert.AreEqual("https://github.com/Jotaro2025", httpsUri.AbsoluteUri);

        Assert.IsTrue(QrAndBarcodeScanner.TryNormalizeHttpUrl("http://example.org/test", out _));
        Assert.IsFalse(QrAndBarcodeScanner.TryNormalizeHttpUrl("javascript:alert(1)", out _));
        Assert.IsFalse(QrAndBarcodeScanner.TryNormalizeHttpUrl("plain text", out _));
    }
}
