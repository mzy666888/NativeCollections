﻿using System.Runtime.CompilerServices;
#if NET5_0_OR_GREATER
using System;
#endif

// ReSharper disable ALL

namespace NativeCollections
{
    /// <summary>
    ///     Math helpers
    /// </summary>
    internal static class MathHelpers
    {
        /// <summary>Produces the full product of two unsigned 64-bit numbers.</summary>
        /// <param name="a">The first number to multiply.</param>
        /// <param name="b">The second number to multiply.</param>
        /// <param name="low">The low 64-bit of the product of the specified numbers.</param>
        /// <returns>The high 64-bit of the product of the specified numbers.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong BigMul(ulong a, ulong b, out ulong low)
        {
#if NET5_0_OR_GREATER
            return Math.BigMul(a, b, out low);
#else
            var al = (uint)a;
            var ah = (uint)(a >> 32);
            var bl = (uint)b;
            var bh = (uint)(b >> 32);
            var mull = (ulong)al * bl;
            var t = (ulong)ah * bl + (mull >> 32);
            var tl = (ulong)al * bh + (uint)t;
            low = (tl << 32) | (uint)mull;
            return (ulong)ah * bh + (t >> 32) + (tl >> 32);
#endif
        }

        /// <summary>Produces the quotient and the remainder of two signed native-size numbers.</summary>
        /// <param name="left">The dividend.</param>
        /// <param name="right">The divisor.</param>
        /// <returns>The quotient and the remainder of the specified numbers.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static (nint Quotient, nint Remainder) DivRem(nint left, nint right)
        {
            var quotient = left / right;
            return (quotient, left - quotient * right);
        }

        /// <summary>Produces the quotient and the remainder of two unsigned 32-bit numbers.</summary>
        /// <param name="left">The dividend.</param>
        /// <param name="right">The divisor.</param>
        /// <returns>The quotient and the remainder of the specified numbers.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static (nuint Quotient, nuint Remainder) DivRem(nuint left, nuint right)
        {
            var num = left / right;
            return (num, left - num * right);
        }
    }
}