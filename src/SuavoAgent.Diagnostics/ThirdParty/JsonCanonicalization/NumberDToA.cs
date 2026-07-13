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
        public static int JS_dtoa(double d, int mode, bool biasUp, int ndigits,
                                  bool[] sign, StringBuilder buf)
        {
            /*  Arguments ndigits, decpt, sign are similar to those
                of ecvt and fcvt; trailing zeros are suppressed from
                the returned string.  If not null, *rve is set to point
                to the end of the return value.  If d is +-Infinity or NaN,
                then *decpt is set to 9999.

                mode:
                0 ==> shortest string that yields d when read in
                and rounded to nearest.
                1 ==> like 0, but with Steele & White stopping rule;
                e.g. with IEEE P754 arithmetic , mode 0 gives
                1e23 whereas mode 1 gives 9.999999999999999e22.
                2 ==> max(1,ndigits) significant digits.  This gives a
                return value similar to that of ecvt, except
                that trailing zeros are suppressed.
                3 ==> through ndigits past the decimal point.  This
                gives a return value similar to that from fcvt,
                except that trailing zeros are suppressed, and
                ndigits can be negative.
                4-9 should give the same return values as 2-3, i.e.,
                4 <= mode <= 9 ==> same return as mode
                2 + (mode & 1).  These modes are mainly for
                debugging; often they run slower but sometimes
                faster than modes 2-3.
                4,5,8,9 ==> left-to-right digit generation.
                6-9 ==> don't try fast floating-point estimate
                (if applicable).

                Values of mode other than 0-9 are treated as mode 0.

                Sufficient space is allocated to the return value
                to hold the suppressed trailing zeros.
            */

            int b2, b5, i, ieps, ilim, ilim0, ilim1,
                    j, j1, k, k0, m2, m5, s2, s5;
            char dig;
            long L;
            long x;
            BigInteger b, b1, delta, mlo, mhi, S;
            int[] be = new int[1];
            int[] bbits = new int[1];
            double d2, ds, eps;
            bool spec_case, denorm, k_check, try_quick, leftright;

            if ((Word0(d) & Sign_bit) != 0)
            {
                /* set sign for everything, including 0's and NaNs */
                sign[0] = true;
                // Word0(d) &= ~Sign_bit;  /* clear sign bit */
                d = SetWord0(d, Word0(d) & ~Sign_bit);
            }
            else
                sign[0] = false;

            if ((Word0(d) & Exp_mask) == Exp_mask)
            {
                /* Infinity or NaN */
                buf.Append(((Word1(d) == 0) && ((Word0(d) & Frac_mask) == 0)) ? "Infinity" : "NaN");
                return 9999;
            }
            if (d == 0)
            {
                //          no_digits:
                buf.Length = 0;
                buf.Append('0');        /* copy "0" to buffer */
                return 1;
            }

            b = D2B(d, be, bbits);
            if ((i = ((int)(((uint)Word0(d)) >> Exp_shift1) & (Exp_mask >> Exp_shift1))) != 0)
            {
                d2 = SetWord0(d, (Word0(d) & Frac_mask1) | Exp_11);
                /* log(x)   ~=~ log(1.5) + (x-1.5)/1.5
                 * log10(x)  =  log(x) / log(10)
                 *      ~=~ log(1.5)/log(10) + (x-1.5)/(1.5*log(10))
                 * log10(d) = (i-Bias)*log(2)/log(10) + log10(d2)
                 *
                 * This suggests computing an approximation k to log10(d) by
                 *
                 * k = (i - Bias)*0.301029995663981
                 *  + ( (d2-1.5)*0.289529654602168 + 0.176091259055681 );
                 *
                 * We want k to be too large rather than too small.
                 * The error in the first-order Taylor series approximation
                 * is in our favor, so we just round up the constant enough
                 * to compensate for any error in the multiplication of
                 * (i - Bias) by 0.301029995663981; since |i - Bias| <= 1077,
                 * and 1077 * 0.30103 * 2^-52 ~=~ 7.2e-14,
                 * adding 1e-13 to the constant term more than suffices.
                 * Hence we adjust the constant term to 0.1760912590558.
                 * (We could get a more accurate k by invoking log10,
                 *  but this is probably not worthwhile.)
                 */
                i -= Bias;
                denorm = false;
            }
            else
            {
                /* d is denormalized */
                i = bbits[0] + be[0] + (Bias + (P - 1) - 1);
                x = (i > 32)
                        ? ((long)Word0(d)) << (64 - i) | (((uint)Word1(d)) >> (i - 32))
                        : ((long)Word1(d)) << (32 - i);
                //            d2 = x;
                //            Word0(d2) -= 31*Exp_msk1; /* adjust exponent */
                d2 = SetWord0(x, Word0(x) - 31 * Exp_msk1);
                i -= (Bias + (P - 1) - 1) + 1;
                denorm = true;
            }
            /* At this point d = f*2^i, where 1 <= f < 2.  d2 is an approximation of f. */
            ds = (d2 - 1.5) * 0.289529654602168 + 0.1760912590558 + i * 0.301029995663981;
            k = (int)ds;
            if (ds < 0.0 && ds != k)
                k--;    /* want k = floor(ds) */
            k_check = true;
            if (k >= 0 && k <= Ten_pmax) {
                if (d < tens[k])
                    k--;
                k_check = false;
            }
            /* At this point floor(log10(d)) <= k <= floor(log10(d))+1.
               If k_check is zero, we're guaranteed that k = floor(log10(d)). */
            j = bbits[0] - i - 1;
            /* At this point d = b/2^j, where b is an odd integer. */
            if (j >= 0)
            {
                b2 = 0;
                s2 = j;
            }
            else
            {
                b2 = -j;
                s2 = 0;
            }
            if (k >= 0)
            {
                b5 = 0;
                s5 = k;
                s2 += k;
            }
            else
            {
                b2 -= k;
                b5 = -k;
                s5 = 0;
            }
            /* At this point d/10^k = (b * 2^b2 * 5^b5) / (2^s2 * 5^s5), where b is an odd integer,
               b2 >= 0, b5 >= 0, s2 >= 0, and s5 >= 0. */
            if (mode < 0 || mode > 9)
                mode = 0;
            try_quick = true;
            if (mode > 5)
            {
                mode -= 4;
                try_quick = false;
            }
            leftright = true;
            ilim = ilim1 = 0;
            switch (mode)
            {
                case 0:
                case 1:
                    ilim = ilim1 = -1;
                    i = 18;
                    ndigits = 0;
                    break;
                case 2:
                    leftright = false;
                    goto case 4;
                /* no break */
                case 4:
                    if (ndigits <= 0)
                        ndigits = 1;
                    ilim = ilim1 = i = ndigits;
                    break;
                case 3:
                    leftright = false;
                    goto case 5;
                /* no break */
                case 5:
                    i = ndigits + k + 1;
                    ilim = i;
                    ilim1 = i - 1;
                    if (i <= 0)
                        i = 1;
                    break;
            }
            /* ilim is the maximum number of significant digits we want, based on k and ndigits. */
            /* ilim1 is the maximum number of significant digits we want, based on k and ndigits,
               when it turns out that k was computed too high by one. */

            bool fast_failed = false;
            if (ilim >= 0 && ilim <= Quick_max && try_quick) {

                /* Try to get by with floating-point arithmetic. */

                i = 0;
                d2 = d;
                k0 = k;
                ilim0 = ilim;
                ieps = 2; /* conservative */
                          /* Divide d by 10^k, keeping track of the roundoff error and avoiding overflows. */
                if (k > 0)
                {
                    ds = tens[k & 0xf];
                    j = k >> 4;
                    if ((j & Bletch) != 0)
                    {
                        /* prevent overflows */
                        j &= Bletch - 1;
                        d /= bigtens[n_bigtens - 1];
                        ieps++;
                    }
                    for (; (j != 0); j >>= 1, i++)
                        if ((j & 1) != 0) {
                            ieps++;
                            ds *= bigtens[i];
                        }
                    d /= ds;
                }
                else if ((j1 = -k) != 0)
                {
                    d *= tens[j1 & 0xf];
                    for (j = j1 >> 4; (j != 0); j >>= 1, i++)
                        if ((j & 1) != 0)
                        {
                            ieps++;
                            d *= bigtens[i];
                        }
                }
                /* Check that k was computed correctly. */
                if (k_check && d < 1.0 && ilim > 0)
                {
                    if (ilim1 <= 0)
                        fast_failed = true;
                    else
                    {
                        ilim = ilim1;
                        k--;
                        d *= 10.0;
                        ieps++;
                    }
                }
                /* eps bounds the cumulative error. */
                //            eps = ieps*d + 7.0;
                //            Word0(eps) -= (P-1)*Exp_msk1;
                eps = ieps * d + 7.0;
                eps = SetWord0(eps, Word0(eps) - (P - 1) * Exp_msk1);
                if (ilim == 0)
                {
// Java                    S = mhi = null;
                    d -= 5.0;
                    if (d > eps)
                    {
                        buf.Append('1');
                        k++;
                        return k + 1;
                    }
                    if (d < -eps)
                    {
                        buf.Length = 0;
                        buf.Append('0');        /* copy "0" to buffer */
                        return 1;
                    }
                    fast_failed = true;
                }
                if (!fast_failed)
                {
                    fast_failed = true;
                    if (leftright)
                    {
                        /* Use Steele & White method of only
                         * generating digits needed.
                         */
                        eps = 0.5 / tens[ilim - 1] - eps;
                        for (i = 0; ;) {
                            L = (long)d;
                            d -= L;
                            buf.Append((char)('0' + L));
                            if (d < eps)
                            {
                                return k + 1;
                            }
                            if (1.0 - d < eps)
                            {
                                //                            goto bump_up;
                                char lastCh;
                                while (true)
                                {
                                    lastCh = buf[buf.Length - 1];
                                    buf.Length = buf.Length - 1;
                                    if (lastCh != '9') break;
                                    if (buf.Length == 0)
                                    {
                                        k++;
                                        lastCh = '0';
                                        break;
                                    }
                                }
                                buf.Append((char)(lastCh + 1));
                                return k + 1;
                            }
                            if (++i >= ilim)
                                break;
                            eps *= 10.0;
                            d *= 10.0;
                        }
                    }
                    else
                    {
                        /* Generate ilim digits, then fix them up. */
                        eps *= tens[ilim - 1];
                        for (i = 1; ; i++, d *= 10.0) {
                            L = (long)d;
                            d -= L;
                            buf.Append((char)('0' + L));
                            if (i == ilim) {
                                if (d > 0.5 + eps)
                                {
                                    //                                goto bump_up;
                                    char lastCh;
                                    while (true) {
                                        lastCh = buf[buf.Length - 1];
                                        buf.Length = buf.Length - 1;
                                        if (lastCh != '9') break;
                                        if (buf.Length == 0)
                                        {
                                            k++;
                                            lastCh = '0';
                                            break;
                                        }
                                    }
                                    buf.Append((char)(lastCh + 1));
                                    return k + 1;
                                }
                                else if (d < 0.5 - eps)
                                {
                                    StripTrailingZeroes(buf);
                                    //                                    while(*--s == '0') ;
                                    //                                    s++;
                                    return k + 1;
                                }
                                break;
                            }
                        }
                    }
                }
                if (fast_failed)
                {
                    buf.Length = 0;
                    d = d2;
                    k = k0;
                    ilim = ilim0;
                }
            }

            /* Do we have a "small" integer? */

            if (be[0] >= 0 && k <= Int_max)
            {
                /* Yes. */
                ds = tens[k];
                if (ndigits < 0 && ilim <= 0)
                {
                    if (ilim < 0 || d < 5 * ds || (!biasUp && d == 5 * ds))
                    {
                        buf.Length = 0;
                        buf.Append('0');        /* copy "0" to buffer */
                        return 1;
                    }
                    buf.Append('1');
                    k++;
                    return k + 1;
                }
                for (i = 1; ; i++)
                {
                    L = (long)(d / ds);
                    d -= L * ds;
                    buf.Append((char)('0' + L));
                    if (i == ilim)
                    {
                        d += d;
                        if ((d > ds) || (d == ds && (((L & 1) != 0) || biasUp)))
                        {
                            //                    bump_up:
                            //                        while(*--s == '9')
                            //                            if (s == buf) {
                            //                                k++;
                            //                                *s = '0';
                            //                                break;
                            //                            }
                            //                        ++*s++;
                            char lastCh;
                            while (true)
                            {
                                lastCh = buf[buf.Length - 1];
                                buf.Length = buf.Length - 1;
                                if (lastCh != '9') break;
                                if (buf.Length == 0)
                                {
                                    k++;
                                    lastCh = '0';
                                    break;
                                }
                            }
                            buf.Append((char)(lastCh + 1));
                        }
                        break;
                    }
                    d *= 10.0;
                    if (d == 0)
                        break;
                }
                return k + 1;
            }

            m2 = b2;
            m5 = b5;
            mhi = mlo = 0;
            if (leftright)
            {
                if (mode < 2)
                {
                    i = (denorm) ? be[0] + (Bias + (P - 1) - 1 + 1) : 1 + P - bbits[0];
                    /* i is 1 plus the number of trailing zero bits in d's significand. Thus,
                       (2^m2 * 5^m5) / (2^(s2+i) * 5^s5) = (1/2 lsb of d)/10^k. */
                }
                else
                {
                    j = ilim - 1;
                    if (m5 >= j)
                        m5 -= j;
                    else
                    {
                        s5 += j -= m5;
                        b5 += j;
                        m5 = 0;
                    }
                    if ((i = ilim) < 0)
                    {
                        m2 -= i;
                        i = 0;
                    }
                    /* (2^m2 * 5^m5) / (2^(s2+i) * 5^s5) = (1/2 * 10^(1-ilim))/10^k. */
                }
                b2 += i;
                s2 += i;
                mhi = 1;
                /* (mhi * 2^m2 * 5^m5) / (2^s2 * 5^s5) = one-half of last printed (when mode >= 2) or
                   input (when mode < 2) significant digit, divided by 10^k. */
            }
            /* We still have d/10^k = (b * 2^b2 * 5^b5) / (2^s2 * 5^s5).  Reduce common factors in
               b2, m2, and s2 without changing the equalities. */
            if (m2 > 0 && s2 > 0)
            {
                i = (m2 < s2) ? m2 : s2;
                b2 -= i;
                m2 -= i;
                s2 -= i;
            }

            /* Fold b5 into b and m5 into mhi. */
            if (b5 > 0)
            {
                if (leftright)
                {
                    if (m5 > 0)
                    {
                        mhi = Pow5mult(mhi, m5);
                        b1 = mhi * b;
                        b = b1;
                    }
                    if ((j = b5 - m5) != 0)
                        b = Pow5mult(b, j);
                }
                else
                    b = Pow5mult(b, b5);
            }
            /* Now we have d/10^k = (b * 2^b2) / (2^s2 * 5^s5) and
               (mhi * 2^m2) / (2^s2 * 5^s5) = one-half of last printed or input significant digit, divided by 10^k. */

            S = 1;
            if (s5 > 0)
                S = Pow5mult(S, s5);
            /* Now we have d/10^k = (b * 2^b2) / (S * 2^s2) and
               (mhi * 2^m2) / (S * 2^s2) = one-half of last printed or input significant digit, divided by 10^k. */

            /* Check for special case that d is a normalized power of 2. */
            spec_case = false;
            if (mode < 2)
            {
                if ((Word1(d) == 0) && ((Word0(d) & Bndry_mask) == 0)
                        && ((Word0(d) & (Exp_mask & Exp_mask << 1)) != 0)
                        )
                {
                    /* The special case.  Here we want to be within a quarter of the last input
                       significant digit instead of one half of it when the decimal output string's value is less than d.  */
                    b2 += Log2P;
                    s2 += Log2P;
                    spec_case = true;
                }
            }

            /* Arrange for convenient computation of quotients:
             * shift left if necessary so divisor has 4 leading 0 bits.
             *
             * Perhaps we should just compute leading 28 bits of S once
             * and for all and pass them and a shift to quorem, so it
             * can do shifts and ors to compute the numerator for q.
             */
            byte[] S_bytes = S.ToByteArray();
            Array.Reverse(S_bytes);  // Note: Opposite to java
            int S_hiWord = 0;
            for (int idx = 0; idx < 4; idx++)
            {
                S_hiWord = (S_hiWord << 8);
                if (idx < S_bytes.Length)
                    S_hiWord |= (S_bytes[idx] & 0xFF);
            }
            if ((i = (((s5 != 0) ? 32 - Hi0bits(S_hiWord) : 1) + s2) & 0x1f) != 0)
                i = 32 - i;
            /* i is the number of leading zero bits in the most significant word of S*2^s2. */
            if (i > 4)
            {
                i -= 4;
                b2 += i;
                m2 += i;
                s2 += i;
            }
            else if (i < 4)
            {
                i += 28;
                b2 += i;
                m2 += i;
                s2 += i;
            }
            /* Now S*2^s2 has exactly four leading zero bits in its most significant word. */
            if (b2 > 0)
                b <<= b2;
            if (s2 > 0)
                S <<= s2;
            /* Now we have d/10^k = b/S and
               (mhi * 2^m2) / S = maximum acceptable error, divided by 10^k. */
            if (k_check)
            {
                if (b.CompareTo(S) < 0) {
                    k--;
                    b *= 10;  /* we botched the k estimate */
                    if (leftright)
                        mhi *= 10;
                    ilim = ilim1;
                }
            }
            /* At this point 1 <= d/10^k = b/S < 10. */

            if (ilim <= 0 && mode > 2)
            {
                /* We're doing fixed-mode output and d is less than the minimum nonzero output in this mode.
                   Output either zero or the minimum nonzero output depending on which is closer to d. */
                if ((ilim < 0)
                        || ((i = b.CompareTo(S *= 5)) < 0)
                        || ((i == 0 && !biasUp))) {
                    /* Always emit at least one digit.  If the number appears to be zero
                       using the current mode, then emit one '0' digit and set decpt to 1. */
                    /*no_digits:
                        k = -1 - ndigits;
                        goto ret; */
                    buf.Length = 0;
                    buf.Append('0');        /* copy "0" to buffer */
                    return 1;
                    //                goto no_digits;
                }
                //        one_digit:
                buf.Append('1');
                k++;
                return k + 1;
            }
            if (leftright)
            {
                if (m2 > 0)
                    mhi <<= m2;

                /* Compute mlo -- check for special case
                 * that d is a normalized power of 2.
                 */

                mlo = mhi;
                if (spec_case)
                {
                    mhi = mlo;
                    mhi <<= Log2P;
                }
                /* mlo/S = maximum acceptable error, divided by 10^k, if the output is less than d. */
                /* mhi/S = maximum acceptable error, divided by 10^k, if the output is greater than d. */

                for (i = 1; ; i++)
                {
                    BigInteger quotient = BigInteger.DivRem(b, S, out BigInteger remainder);
                    b = remainder;
                    dig = (char)((int)quotient + '0');
                    /* Do we yet have the shortest decimal string
                     * that will round to d?
                     */
                    j = b.CompareTo(mlo);
                    /* j is b/S compared with mlo/S. */
                    delta = S - mhi;
                    j1 = (delta <= 0) ? 1 : b.CompareTo(delta);
                    /* j1 is b/S compared with 1 - mhi/S. */
                    if ((j1 == 0) && (mode == 0) && ((Word1(d) & 1) == 0))
                    {
                        if (dig == '9')
                        {
                            buf.Append('9');
                            if (RoundOff(buf))
                            {
                                k++;
                                buf.Append('1');
                            }
                            return k + 1;
                            //                        goto round_9_up;
                        }
                        if (j > 0)
                            dig++;
                        buf.Append(dig);
                        return k + 1;
                    }
                    if ((j < 0)
                            || ((j == 0)
                            && (mode == 0)
                            && ((Word1(d) & 1) == 0)
                    ))
                    {
                        if (j1 > 0)
                        {
                            /* Either dig or dig+1 would work here as the least significant decimal digit.
                               Use whichever would produce a decimal value closer to d. */
                            b <<= 1;
                            j1 = b.CompareTo(S);
                            if (((j1 > 0) || (j1 == 0 && (((dig & 1) == 1) || biasUp)))
                                    && (dig++ == '9'))
                            {
                                buf.Append('9');
                                if (RoundOff(buf))
                                {
                                    k++;
                                    buf.Append('1');
                                }
                                return k + 1;
                                //                                goto round_9_up;
                            }
                        }
                        buf.Append(dig);
                        return k + 1;
                    }
                    if (j1 > 0)
                    {
                        if (dig == '9')
                        {  /* possible if i == 1 */
                                          //                    round_9_up:
                                          //                        *s++ = '9';
                                          //                        goto roundoff;
                            buf.Append('9');
                            if (RoundOff(buf))
                            {
                                k++;
                                buf.Append('1');
                            }
                            return k + 1;
                        }
                        buf.Append((char)(dig + 1));
                        return k + 1;
                    }
                    buf.Append(dig);
                    if (i == ilim)
                        break;
                    b *= 10;
                    if (mlo == mhi)
                        mlo = mhi = mhi * 10;
                    else
                    {
                        mlo *= 10;
                        mhi *= 10;
                    }
                }
            }
            else
                for (i = 1; ; i++)
                {
                    //                (char)(dig = quorem(b,S) + '0');
                    BigInteger quotient = BigInteger.DivRem(b, S, out BigInteger remainder);
                    b = remainder;
                    dig = (char)((int)quotient + '0');

                    buf.Append(dig);
                    if (i >= ilim)
                        break;
                    b *= 10;
                }

            /* Round off last digit */

            b <<= 1;
            j = b.CompareTo(S);
            if ((j > 0) || (j == 0 && (((dig & 1) == 1) || biasUp)))
            {
                //        roundoff:
                //            while(*--s == '9')
                //                if (s == buf) {
                //                    k++;
                //                    *s++ = '1';
                //                    goto ret;
                //                }
                //            ++*s++;
                if (RoundOff(buf))
                {
                    k++;
                    buf.Append('1');
                    return k + 1;
                }
            }
            else
            {
                StripTrailingZeroes(buf);
                //            while(*--s == '0') ;
                //            s++;
            }
            //      ret:
            //        Bfree(S);
            //        if (mhi) {
            //            if (mlo && mlo != mhi)
            //                Bfree(mlo);
            //            Bfree(mhi);
            //        }
            //      ret1:
            //        Bfree(b);
            //        JS_ASSERT(s < buf + bufsize);
            return k + 1;
        }
    }
}
