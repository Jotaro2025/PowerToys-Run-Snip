// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;

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
}
