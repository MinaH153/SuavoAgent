/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/. */

/****************************************************************
 * The author of this software is David M. Gay.
 * <p>
 * Copyright (c) 1991, 2000, 2001 by Lucent Technologies.
 * <p>
 * Permission to use, copy, modify, and distribute this software for any
 * purpose without fee is hereby granted, provided that this entire notice
 * is included in all copies of any software which is or includes a copy
 * or modification of this software and in all copies of the supporting
 * documentation for such software.
 * <p>
 * THIS SOFTWARE IS BEING PROVIDED "AS IS", WITHOUT ANY EXPRESS OR IMPLIED
 * WARRANTY.  IN PARTICULAR, NEITHER THE AUTHOR NOR LUCENT MAKES ANY
 * REPRESENTATION OR WARRANTY OF ANY KIND CONCERNING THE MERCHANTABILITY
 * OF THIS SOFTWARE OR ITS FITNESS FOR ANY PARTICULAR PURPOSE.
 ***************************************************************/

// Ported to C# from the Mozilla "Rhino" project by Anders Rundgren.

using System;
using System.Numerics;
using System.Diagnostics;
using System.Text;

/// <summary>
/// This is an internal part of a ES6 compatible JSON Number serializer.
/// </summary>

namespace Org.Webpki.Es6NumberSerialization
{
    partial class NumberDToA {
        public const int
                DTOSTR_STANDARD = 0,              /* Either fixed or exponential format; round-trip */
                DTOSTR_STANDARD_EXPONENTIAL = 1,  /* Always exponential format; round-trip */
                DTOSTR_FIXED = 2,                 /* Round to <precision> digits after the decimal point; exponential if number is large */
                DTOSTR_EXPONENTIAL = 3,           /* Always exponential format; <precision> significant digits */
                DTOSTR_PRECISION = 4;             /* Either fixed or exponential format; <precision> significant digits */


        private const int Frac_mask = 0xfffff;
        private const int Exp_shift = 20;
        private const int Exp_msk1 = 0x100000;

        private const long Frac_maskL = 0xfffffffffffffL;
        private const int Exp_shiftL = 52;
        private const long Exp_msk1L = 0x10000000000000L;

        private const int Bias = 1023;
        private const int P = 53;

        private const int Exp_shift1 = 20;
        private const int Exp_mask = 0x7ff00000;
        private const int Exp_mask_shifted = 0x7ff;
        private const int Bndry_mask = 0xfffff;
        private const int Log2P = 1;

        private const int Sign_bit = -0x80000000;
        private const int Exp_11 = 0x3ff00000;
        private const int Ten_pmax = 22;
        private const int Quick_max = 14;
        private const int Bletch = 0x10;
        private const int Frac_mask1 = 0xfffff;
        private const int Int_max = 14;
        private const int n_bigtens = 5;


        private static double[] tens = 
        {
            1e0, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9,
            1e10, 1e11, 1e12, 1e13, 1e14, 1e15, 1e16, 1e17, 1e18, 1e19,
            1e20, 1e21, 1e22
        };

        private static double[] bigtens = { 1e16, 1e32, 1e64, 1e128, 1e256 };

        private static int Lo0bits(int inty)
        {
            uint y = (uint)inty;
            uint k;
            uint x = y;

            if ((x & 7) != 0)
            {
                if ((x & 1) != 0)
                    return 0;
                if ((x & 2) != 0)
                {
                    return 1;
                }
                return 2;
            }
            k = 0;
            if ((x & 0xffff) == 0)
            {
                k = 16;
                x >>= 16;
            }
            if ((x & 0xff) == 0)
            {
                k += 8;
                x >>= 8;
            }
            if ((x & 0xf) == 0)
            {
                k += 4;
                x >>= 4;
            }
            if ((x & 0x3) == 0)
            {
                k += 2;
                x >>= 2;
            }
            if ((x & 1) == 0)
            {
                k++;
                x >>= 1;
                if ((x & 1) == 0)
                    return 32;
            }
            return (int)k;
        }

        /* Return the number (0 through 32) of most significant zero bits in x. */
        private static int Hi0bits(int x)
        {
            int k = 0;

            if ((x & 0xffff0000) == 0)
            {
                k = 16;
                x <<= 16;
            }
            if ((x & 0xff000000) == 0)
            {
                k += 8;
                x <<= 8;
            }
            if ((x & 0xf0000000) == 0)
            {
                k += 4;
                x <<= 4;
            }
            if ((x & 0xc0000000) == 0)
            {
                k += 2;
                x <<= 2;
            }
            if ((x & 0x80000000) == 0)
            {
                k++;
                if ((x & 0x40000000) == 0)
                    return 32;
            }
            return k;
        }

        private static void StuffBits(byte[] bits, int offset, int val)
        {
            bits[offset] = (byte)(val >> 24);
            bits[offset + 1] = (byte)(val >> 16);
            bits[offset + 2] = (byte)(val >> 8);
            bits[offset + 3] = (byte)(val);
        }

        /* Convert d into the form b*2^e, where b is an odd integer.  b is the returned
         * Bigint and e is the returned binary exponent.  Return the number of significant
         * bits in b in bits.  d must be finite and nonzero. */
        private static BigInteger D2B(double d, int[] e, int[] bits)
        {
            byte[] dbl_bits;
            int i, k, y, z, de;
            ulong dBits = (ulong)BitConverter.DoubleToInt64Bits(d);
            int d0 = (int)(dBits >> 32);
            int d1 = (int)(dBits);

            z = d0 & Frac_mask;
            d0 &= 0x7fffffff;   /* clear sign bit, which we ignore */

            if ((de = (d0 >> Exp_shift)) != 0)
                z |= Exp_msk1;

            if ((y = d1) != 0)
            {
                dbl_bits = new byte[8];
                k = Lo0bits(y);
                y = (int)((uint)y >> k);
                if (k != 0)
                {
                    StuffBits(dbl_bits, 4, y | z << (32 - k));
                    z >>= k;
                }
                else
                    StuffBits(dbl_bits, 4, y);
                StuffBits(dbl_bits, 0, z);
                i = (z != 0) ? 2 : 1;
            }
            else
            {
                //        JS_ASSERT(z);
                dbl_bits = new byte[4];
                k = Lo0bits(z);
                z >>= k;
                z &= 0x7fffffff;
                StuffBits(dbl_bits, 0, z);
                k += 32;
                i = 1;
            }
            if (de != 0)
            {
                e[0] = de - Bias - (P - 1) + k;
                bits[0] = P - k;
            }
            else
            {
                e[0] = de - Bias - (P - 1) + 1 + k;
                bits[0] = 32 * i - Hi0bits(z);
            }
            byte[] reverse = new byte[dbl_bits.Length];
            int q = dbl_bits.Length;
            foreach (byte b in dbl_bits)
            {
                reverse[--q] = b;
            }
            return new BigInteger(reverse);
        }

        /* dtoa for IEEE arithmetic (dmg): convert double to ASCII string.
         *
         * Inspired by "How to Print Floating-Point Numbers Accurately" by
         * Guy L. Steele, Jr. and Jon L. White [Proc. ACM SIGPLAN '90, pp. 92-101].
         *
         * Modifications:
         *  1. Rather than iterating, we use a simple numeric overestimate
         *     to determine k = floor(log10(d)).  We scale relevant
         *     quantities using O(log2(k)) rather than O(k) multiplications.
         *  2. For some modes > 2 (corresponding to ecvt and fcvt), we don't
         *     try to generate digits strictly left to right.  Instead, we
         *     compute with fewer bits and propagate the carry if necessary
         *     when rounding the final digit up.  This is often faster.
         *  3. Under the assumption that input will be rounded nearest,
         *     mode 0 renders 1e23 as 1e23 rather than 9.999999999999999e22.
         *     That is, we allow equality in stopping tests when the
         *     round-nearest rule will give the same floating-point value
         *     as would satisfaction of the stopping test with strict
         *     inequality.
         *  4. We remove common factors of powers of 2 from relevant
         *     quantities.
         *  5. When converting floating-point integers less than 1e16,
         *     we use floating-point arithmetic rather than resorting
         *     to multiple-precision integers.
         *  6. When asked to produce fewer than 15 digits, we first try
         *     to get by with floating-point arithmetic; we resort to
         *     multiple-precision integer arithmetic only if we cannot
         *     guarantee that the floating-point calculation has given
         *     the correctly rounded result.  For k requested digits and
         *     "uniformly" distributed input, the probability is
         *     something like 10^(k-15) that we must resort to the Long
         *     calculation.
         */

        static int Word0(double d)
        {
            long dBits = BitConverter.DoubleToInt64Bits(d);
            return (int)(dBits >> 32);
        }

        private static double SetWord0(double d, int i)
        {
            long dBits = BitConverter.DoubleToInt64Bits(d);
            dBits = ((long)i << 32) | (dBits & 0x0FFFFFFFFL);
            return BitConverter.Int64BitsToDouble(dBits);
        }

        private static int Word1(double d)
        {
            long dBits = BitConverter.DoubleToInt64Bits(d);
            return (int)(dBits);
        }

        /* Return b * 5^k.  k must be nonnegative. */
        // XXXX the C version built a cache of these
        private static BigInteger Pow5mult(BigInteger b, int k)
        {
            return b * BigInteger.Pow(5, k);
        }

        private static bool RoundOff(StringBuilder buf)
        {
            int i = buf.Length;
            while (i != 0)
            {
                --i;
                char c = buf[i];
                if (c != '9')
                {
                    buf[i] = (char)(c + 1);
                    buf.Length = i + 1;
                    return false;
                }
            }
            buf.Length = 0;
            return true;
        }

        /* Always emits at least one digit. */
        /* If biasUp is set, then rounding in modes 2 and 3 will round away from zero
         * when the number is exactly halfway between two representable values.  For example,
         * rounding 2.5 to zero digits after the decimal point will return 3 and not 2.
         * 2.49 will still round to 2, and 2.51 will still round to 3. */
        /* bufsize should be at least 20 for modes 0 and 1.  For the other modes,
         * bufsize should be two greater than the maximum number of output characters expected. */
    }
}
