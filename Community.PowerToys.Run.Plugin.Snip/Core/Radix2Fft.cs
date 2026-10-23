// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;

namespace Community.PowerToys.Run.Plugin.Snip.Core;

/// <summary>
/// In-place radix-2 Cooley–Tukey FFT matching MathNet <c>FourierOptions.NoScaling</c>:
/// forward uses <c>exp(-2πi kn/N)</c>, inverse uses <c>exp(+2πi kn/N)</c>, neither multiplies by 1/N.
/// Length must be a power of two (callers pad via NextPow2).
/// </summary>
internal static class Radix2Fft
{
    /// <summary>In-place forward DFT without 1/N scaling.</summary>
    public static void Forward(ComplexF[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        Transform(data.AsSpan(), inverse: false);
    }

    /// <summary>In-place forward DFT on a span without 1/N scaling.</summary>
    public static void Forward(Span<ComplexF> data) => Transform(data, inverse: false);

    /// <summary>In-place inverse DFT without 1/N scaling (forward then inverse yields N·x).</summary>
    public static void Inverse(ComplexF[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        Transform(data.AsSpan(), inverse: true);
    }

    /// <summary>In-place inverse DFT on a span without 1/N scaling.</summary>
    public static void Inverse(Span<ComplexF> data) => Transform(data, inverse: true);

    private static void Transform(Span<ComplexF> data, bool inverse)
    {
        int n = data.Length;
        if (n == 0)
        {
            return;
        }

        if ((n & (n - 1)) != 0)
        {
            throw new ArgumentException("FFT length must be a power of two.", nameof(data));
        }

        // Bit-reversal permutation.
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;
            if (i < j)
            {
                (data[i], data[j]) = (data[j], data[i]);
            }
        }

        float sign = inverse ? 1f : -1f;
        for (int len = 2; len <= n; len <<= 1)
        {
            float angle = sign * 2f * MathF.PI / len;
            float wlenRe = MathF.Cos(angle);
            float wlenIm = MathF.Sin(angle);
            int half = len >> 1;
            for (int i = 0; i < n; i += len)
            {
                float wr = 1f;
                float wi = 0f;
                for (int j = 0; j < half; j++)
                {
                    int u = i + j;
                    int v = u + half;
                    float vr = data[v].Real;
                    float vi = data[v].Imaginary;
                    float tr = (wr * vr) - (wi * vi);
                    float ti = (wr * vi) + (wi * vr);
                    float ur = data[u].Real;
                    float ui = data[u].Imaginary;
                    data[u] = new ComplexF(ur + tr, ui + ti);
                    data[v] = new ComplexF(ur - tr, ui - ti);

                    float nextWr = (wr * wlenRe) - (wi * wlenIm);
                    wi = (wr * wlenIm) + (wi * wlenRe);
                    wr = nextWr;
                }
            }
        }
    }
}
