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
        private static void StripTrailingZeroes(StringBuilder buf)
        {
            //      while(*--s == '0') ;
            //      s++;
            int bl = buf.Length;
            while (bl-- > 0 && buf[bl] == '0')
            {
                // empty
            }
            buf.Length = bl + 1;
        }

        /* Mapping of JSDToStrMode -> JS_dtoa mode */
        private static int[] dtoaModes = {
            0,   /* DTOSTR_STANDARD */
            0,   /* DTOSTR_STANDARD_EXPONENTIAL, */
            3,   /* DTOSTR_FIXED, */
            2,   /* DTOSTR_EXPONENTIAL, */
            2};  /* DTOSTR_PRECISION */

        public static void JS_dtostr(StringBuilder buffer, int mode, int precision, double d)
        {
            int decPt;                                    /* Position of decimal point relative to first digit returned by JS_dtoa */
            bool[] sign = new bool[1];            /* true if the sign bit was set in d */
            int nDigits;                                /* Number of significand digits returned by JS_dtoa */

            //        JS_ASSERT(bufferSize >= (size_t)(mode <= DTOSTR_STANDARD_EXPONENTIAL ? DTOSTR_STANDARD_BUFFER_SIZE :
            //                DTOSTR_VARIABLE_BUFFER_SIZE(precision)));

            if (mode == DTOSTR_FIXED && (d >= 1e21 || d <= -1e21))
                mode = DTOSTR_STANDARD; /* Change mode here rather than below because the buffer may not be large enough to hold a large integer. */

            decPt = JS_dtoa(d, dtoaModes[mode], mode >= DTOSTR_FIXED, precision, sign, buffer);
            nDigits = buffer.Length;

            /* If Infinity, -Infinity, or NaN, return the string regardless of the mode. */
            if (decPt != 9999)
            {
                bool exponentialNotation = false;
                int minNDigits = 0;         /* Minimum number of significand digits required by mode and precision */
                int p;

                switch (mode)
                {
                    case DTOSTR_STANDARD:
                        if (decPt < -5 || decPt > 21)
                            exponentialNotation = true;
                        else
                            minNDigits = decPt;
                        break;

                    case DTOSTR_FIXED:
                        if (precision >= 0)
                            minNDigits = decPt + precision;
                        else
                            minNDigits = decPt;
                        break;

                    case DTOSTR_EXPONENTIAL:
                        //                    JS_ASSERT(precision > 0);
                        minNDigits = precision;
                        /* Fall through */
                        goto case DTOSTR_STANDARD_EXPONENTIAL;
                    case DTOSTR_STANDARD_EXPONENTIAL:
                        exponentialNotation = true;
                        break;

                    case DTOSTR_PRECISION:
                        //                    JS_ASSERT(precision > 0);
                        minNDigits = precision;
                        if (decPt < -5 || decPt > precision)
                            exponentialNotation = true;
                        break;
                }

                /* If the number has fewer than minNDigits, pad it with zeros at the end */
                if (nDigits < minNDigits)
                {
                    p = minNDigits;
                    nDigits = minNDigits;
                    do
                    {
                        buffer.Append('0');
                    } while (buffer.Length != p);
                }

                if (exponentialNotation)
                {
                    /* Insert a decimal point if more than one significand digit */
                    if (nDigits != 1)
                    {
                        buffer.Insert(1, '.');
                    }
                    buffer.Append('e');
                    if ((decPt - 1) >= 0)
                        buffer.Append('+');
                    buffer.Append(decPt - 1);
                    //                JS_snprintf(numEnd, bufferSize - (numEnd - buffer), "e%+d", decPt-1);
                }
                else if (decPt != nDigits)
                {
                    /* Some kind of a fraction in fixed notation */
                    //                JS_ASSERT(decPt <= nDigits);
                    if (decPt > 0)
                    {
                        /* dd...dd . dd...dd */
                        buffer.Insert(decPt, '.');
                    }
                    else
                    {
                        /* 0 . 00...00dd...dd */
                        for (int i = 0; i < 1 - decPt; i++)
                            buffer.Insert(0, '0');
                        buffer.Insert(1, '.');
                    }
                }
            }

            /* If negative and neither -0.0 nor NaN, output a leading '-'. */
            if (sign[0] &&
                    !(Word0(d) == Sign_bit && Word1(d) == 0) &&
                    !((Word0(d) & Exp_mask) == Exp_mask &&
                            ((Word1(d) != 0) || ((Word0(d) & Frac_mask) != 0))))
            {
                buffer.Insert(0, '-');
            }
        }
    }
}
