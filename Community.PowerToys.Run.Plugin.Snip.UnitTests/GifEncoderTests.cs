// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using Community.PowerToys.Run.Plugin.Snip.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public class GifEncoderTests
{
    private static byte[] SolidFrame(int width, int height, byte b, byte g, byte r, int stride = -1)
    {
        stride = stride < 0 ? width * 4 : stride;
        var frame = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int o = (y * stride) + (x * 4);
                frame[o] = b;
                frame[o + 1] = g;
                frame[o + 2] = r;
                frame[o + 3] = 255;
            }
        }

        return frame;
    }

    [TestMethod]
    public void Encode_ProducesGif89aHeaderAndTrailer()
    {
        var frames = new List<byte[]> { SolidFrame(8, 8, 10, 20, 30), SolidFrame(8, 8, 200, 100, 50) };
        var delays = new List<int> { 100, 100 };
        byte[] gif = GifEncoder.Encode(frames, delays, 8, 8, 8 * 4);

        Assert.IsTrue(gif.Length > 32);
        Assert.AreEqual((byte)'G', gif[0]);
        Assert.AreEqual((byte)'I', gif[1]);
        Assert.AreEqual((byte)'F', gif[2]);
        Assert.AreEqual((byte)'8', gif[3]);
        Assert.AreEqual((byte)'9', gif[4]);
        Assert.AreEqual((byte)'a', gif[5]);
        Assert.AreEqual(0x3B, gif[^1]); // trailer
    }

    [TestMethod]
    public void Encode_EmbedsNetscapeLoopAndFrameCount()
    {
        var frames = new List<byte[]>();
        var delays = new List<int>();
        for (int i = 0; i < 3; i++)
        {
            frames.Add(SolidFrame(8, 8, (byte)i, 0, 0));
            delays.Add(50);
        }

        byte[] gif = GifEncoder.Encode(frames, delays, 8, 8, 32, loopForever: true);
        string ascii = System.Text.Encoding.ASCII.GetString(gif);
        Assert.IsTrue(ascii.Contains("NETSCAPE2.0"), "loop extension missing");

        // One Graphic Control Extension (0x21 0xF9) per frame.
        int gceCount = 0;
        for (int i = 0; i < gif.Length - 1; i++)
        {
            if (gif[i] == 0x21 && gif[i + 1] == 0xF9)
            {
                gceCount++;
            }
        }

        Assert.AreEqual(3, gceCount);
    }

    [TestMethod]
    public void Encode_SingleColorFrame_RoundTripsPaletteOfOne()
    {
        // A solid frame must compress to a tiny stream: palette has exactly one
        // used color and LZW collapses runs. The fixed 256-entry local color
        // table (768 B) dominates the size, so bound under ~1 KB (raw is 16 KB).
        byte[] gif = GifEncoder.Encode(
            new List<byte[]> { SolidFrame(64, 64, 1, 2, 3) },
            new List<int> { 100 }, 64, 64, 64 * 4, loopForever: false);
        Assert.IsTrue(gif.Length < 1024, $"solid 64x64 GIF too large: {gif.Length}");
    }

    [TestMethod]
    public void Encode_ClampsDelaysToGifRange()
    {
        var frames = new List<byte[]> { SolidFrame(4, 4, 0, 0, 0), SolidFrame(4, 4, 9, 9, 9) };
        var delays = new List<int> { 1, 5000 }; // below min and above max
        byte[] gif = GifEncoder.Encode(frames, delays, 4, 4, 16);

        // Find the first GCE and verify its centisecond payload is >= 1.
        for (int i = 0; i < gif.Length - 5; i++)
        {
            if (gif[i] == 0x21 && gif[i + 1] == 0xF9 && gif[i + 2] == 4)
            {
                int centis = gif[i + 4] | (gif[i + 5] << 8);
                Assert.IsTrue(centis >= 1 && centis <= 100, $"delay out of clamp: {centis}");
                return;
            }
        }

        Assert.Fail("no GCE found");
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(16)]
    [DataRow(256)]
    public void Encode_DecoderPreservesPixelsAcrossPaletteAndLzwBoundaries(int colors)
    {
        const int width = 257;
        const int height = 259;
        const int stride = (width * 4) + 8;
        var random = new Random(1742);
        var frames = new List<byte[]>();
        for (int f = 0; f < 2; f++)
        {
            var frame = new byte[stride * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int color = random.Next(colors);
                    int offset = (y * stride) + (x * 4);
                    frame[offset] = (byte)((color & 15) * 16);
                    frame[offset + 1] = (byte)((color >> 4) * 16);
                    frame[offset + 2] = (byte)(16 + ((color % 15) * 16));
                    frame[offset + 3] = 255;
                }
            }

            frames.Add(frame);
        }

        using var stream = new MemoryStream(GifEncoder.Encode(frames, new[] { 70, 130 }, width, height, stride));
        var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        Assert.AreEqual(2, decoder.Frames.Count);
        for (int f = 0; f < frames.Count; f++)
        {
            var decoded = new FormatConvertedBitmap(decoder.Frames[f], PixelFormats.Bgra32, null, 0);
            Assert.AreEqual(width, decoded.PixelWidth);
            Assert.AreEqual(height, decoded.PixelHeight);
            var actual = new byte[stride * height];
            decoded.CopyPixels(actual, stride, 0);
            CollectionAssert.AreEqual(frames[f], actual, $"Frame {f}, palette size {colors}");
            var metadata = (BitmapMetadata)decoder.Frames[f].Metadata;
            Assert.AreEqual((ushort)(f == 0 ? 7 : 13), (ushort)metadata.GetQuery("/grctlext/Delay"));
        }
    }

    [TestMethod]
    public void EncodeToStream_CancelledBeforeStartDoesNotWrite()
    {
        using var stream = new MemoryStream();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => GifEncoder.EncodeToStream(
            stream, new[] { SolidFrame(4, 4, 1, 2, 3) }, new[] { 80 }, 4, 4, 16, cancellationToken: cts.Token));
        Assert.AreEqual(0L, stream.Length);
    }

    [TestMethod]
    public void EncodeToStream_CancellationInterruptsStartedEncoding()
    {
        using var cts = new CancellationTokenSource();
        using var stream = new CancellingStream(cts);
        Assert.ThrowsExactly<OperationCanceledException>(() => GifEncoder.EncodeToStream(
            stream, new[] { SolidFrame(128, 128, 1, 2, 3) }, new[] { 80 }, 128, 128, 512, cancellationToken: cts.Token));
        Assert.IsTrue(stream.Length > 0, "the encoder must have started writing before cancellation");
        Assert.AreNotEqual((byte)0x3B, stream.ToArray()[^1], "a cancelled encode must not finish a GIF");
    }

    private sealed class CancellingStream : MemoryStream
    {
        private readonly CancellationTokenSource _cts;

        public CancellingStream(CancellationTokenSource cts) => _cts = cts;

        public override void WriteByte(byte value)
        {
            base.WriteByte(value);
            if (Length == 13)
            {
                _cts.Cancel();
            }
        }
    }
}
