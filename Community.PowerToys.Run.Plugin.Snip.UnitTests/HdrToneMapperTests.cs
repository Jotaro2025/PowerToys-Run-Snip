// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Buffers.Binary;

using Community.PowerToys.Run.Plugin.Snip.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public sealed class HdrToneMapperTests
{
    [TestMethod]
    public void EncodeToSrgb_MatchesKnownTransferFunctionPoints()
    {
        Assert.AreEqual(0.0, HdrToneMapper.EncodeToSrgb(0.0), 1e-9);
        Assert.AreEqual(1.0, HdrToneMapper.EncodeToSrgb(1.0), 1e-9);
        Assert.AreEqual(128.0, HdrToneMapper.EncodeToSrgbByte(0.2140), 1.0);
        Assert.AreEqual(188.0, HdrToneMapper.EncodeToSrgbByte(0.5), 1.0);
        Assert.AreEqual(255, HdrToneMapper.EncodeToSrgbByte(4.0), 0);
        Assert.AreEqual(0, HdrToneMapper.EncodeToSrgbByte(-0.5), 0);
        Assert.AreEqual(0, HdrToneMapper.EncodeToSrgbByte(double.NaN), 0);
    }

    [TestMethod]
    public void ConvertRgba16ToBgra8_OnSdr_ReversesGammaAndSwapsChannels()
    {
        // R = linear(0.5 sRGB), G = 0, B = white; scale 1 keeps sRGB values.
        byte[] source = Rgba16(0.2140f, 0.0f, 1.0f, 1.0f);
        var destination = new byte[4];

        HdrToneMapper.ConvertRgba16ToBgra8(source, sourceStride: 8, destination, width: 1, height: 1, whiteScale: 1.0);

        Assert.AreEqual(255, destination[0], "B");
        Assert.AreEqual(0, destination[1], "G");
        Assert.AreEqual(128.0, destination[2], 1.0, "R");
        Assert.AreEqual(255, destination[3], "A");
    }

    [TestMethod]
    public void ConvertRgba16ToBgra8_OnHdr_MapsSdrWhiteToFullWhite()
    {
        // SDR white on a 200-nit HDR desktop is scRGB 2.5; scale 0.4 → 1.0.
        byte[] source = Rgba16(2.5f, 2.5f, 2.5f, 1.0f);
        var destination = new byte[4];

        HdrToneMapper.ConvertRgba16ToBgra8(source, sourceStride: 8, destination, width: 1, height: 1, whiteScale: 0.4);

        Assert.AreEqual(255, destination[0]);
        Assert.AreEqual(255, destination[1]);
        Assert.AreEqual(255, destination[2]);
    }

    [TestMethod]
    public void ConvertRgba16ToBgra8_OnHdr_RestoresMidtone()
    {
        // scRGB 0.535 * 0.4 = linear 0.214 → sRGB ~128.
        byte[] source = Rgba16(0.535f, 0.535f, 0.535f, 1.0f);
        var destination = new byte[4];

        HdrToneMapper.ConvertRgba16ToBgra8(source, sourceStride: 8, destination, width: 1, height: 1, whiteScale: 0.4);

        Assert.AreEqual(128.0, destination[0], 1.0);
        Assert.AreEqual(128.0, destination[1], 1.0);
        Assert.AreEqual(128.0, destination[2], 1.0);
    }

    [TestMethod]
    public void ConvertRgba16ToBgra8_ClipsHdrHighlightsAboveSdrWhite()
    {
        byte[] source = Rgba16(10.0f, 10.0f, 10.0f, 1.0f);
        var destination = new byte[4];

        HdrToneMapper.ConvertRgba16ToBgra8(source, sourceStride: 8, destination, width: 1, height: 1, whiteScale: 0.4);

        Assert.AreEqual(255, destination[0]);
        Assert.AreEqual(255, destination[1]);
        Assert.AreEqual(255, destination[2]);
    }

    [TestMethod]
    public void ConvertRgba16ToBgra8_HandlesNegativeAndNaNChannels()
    {
        byte[] source = Rgba16(-1.0f, float.NaN, 0.0f, 1.0f);
        var destination = new byte[4];

        HdrToneMapper.ConvertRgba16ToBgra8(source, sourceStride: 8, destination, width: 1, height: 1, whiteScale: 1.0);

        Assert.AreEqual(0, destination[0]);
        Assert.AreEqual(0, destination[1]);
        Assert.AreEqual(0, destination[2]);
    }

    [TestMethod]
    public void ConvertRgba16ToBgra8_RespectsSourceStrideAndMultipleRows()
    {
        // Two rows, each padded with 4 unused bytes.
        const int rowBytes = 8;
        const int stride = rowBytes + 4;
        var source = new byte[stride * 2];
        WriteHalf(source, 0, 1.0f);
        WriteHalf(source, 2, 0.0f);
        WriteHalf(source, 4, 0.0f);
        WriteHalf(source, 6, 1.0f);
        WriteHalf(source, stride, 0.0f);
        WriteHalf(source, stride + 2, 0.0f);
        WriteHalf(source, stride + 4, 1.0f);
        WriteHalf(source, stride + 6, 1.0f);

        var destination = new byte[2 * 4];
        HdrToneMapper.ConvertRgba16ToBgra8(source, stride, destination, width: 1, height: 2, whiteScale: 1.0);

        Assert.AreEqual(0, destination[0], "row 0 B");
        Assert.AreEqual(255, destination[2], "row 0 R");
        Assert.AreEqual(255, destination[4], "row 1 B");
        Assert.AreEqual(0, destination[6], "row 1 R");
    }

    [TestMethod]
    public void ConvertRgba16ToBgra8_RejectsInvalidBuffers()
    {
        byte[] source = Rgba16(0.0f, 0.0f, 0.0f, 1.0f);
        var destination = new byte[4];

        _ = Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => HdrToneMapper.ConvertRgba16ToBgra8(source, sourceStride: 8, destination, width: 0, height: 1, whiteScale: 1.0));
        _ = Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => HdrToneMapper.ConvertRgba16ToBgra8(source, sourceStride: 4, destination, width: 1, height: 1, whiteScale: 1.0));
        _ = Assert.ThrowsException<ArgumentException>(
            () => HdrToneMapper.ConvertRgba16ToBgra8(source.AsSpan(0, 4), sourceStride: 8, destination, width: 1, height: 1, whiteScale: 1.0));
        _ = Assert.ThrowsException<ArgumentException>(
            () => HdrToneMapper.ConvertRgba16ToBgra8(source, sourceStride: 8, destination.AsSpan(0, 2), width: 1, height: 1, whiteScale: 1.0));
    }

    private static byte[] Rgba16(float r, float g, float b, float a)
    {
        var buffer = new byte[8];
        WriteHalf(buffer, 0, r);
        WriteHalf(buffer, 2, g);
        WriteHalf(buffer, 4, b);
        WriteHalf(buffer, 6, a);
        return buffer;
    }

    private static void WriteHalf(byte[] buffer, int offset, float value)
        => BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset, 2), BitConverter.HalfToUInt16Bits((Half)value));
}
