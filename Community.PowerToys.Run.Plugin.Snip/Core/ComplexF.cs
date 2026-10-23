// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;

namespace Community.PowerToys.Run.Plugin.Snip.Core;

/// <summary>
/// Single-precision complex value for the in-repo radix-2 FFT (phase correlator).
/// </summary>
internal readonly struct ComplexF : IEquatable<ComplexF>
{
    public ComplexF(float real, float imaginary)
    {
        Real = real;
        Imaginary = imaginary;
    }

    public float Real { get; }

    public float Imaginary { get; }

    public static ComplexF Zero { get; } = new ComplexF(0, 0);

    public float Magnitude => MathF.Sqrt((Real * Real) + (Imaginary * Imaginary));

    public ComplexF Conjugate => new ComplexF(Real, -Imaginary);

    public static ComplexF operator *(ComplexF left, ComplexF right)
    {
        return new ComplexF(
            (left.Real * right.Real) - (left.Imaginary * right.Imaginary),
            (left.Real * right.Imaginary) + (left.Imaginary * right.Real));
    }

    public static ComplexF operator /(ComplexF value, float divisor)
    {
        return new ComplexF(value.Real / divisor, value.Imaginary / divisor);
    }

    public bool Equals(ComplexF other) => Real.Equals(other.Real) && Imaginary.Equals(other.Imaginary);

    public override bool Equals(object obj) => obj is ComplexF other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Real, Imaginary);

    public static bool operator ==(ComplexF left, ComplexF right) => left.Equals(right);

    public static bool operator !=(ComplexF left, ComplexF right) => !left.Equals(right);
}
