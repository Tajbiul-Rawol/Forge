using System;
using System.Collections.Generic;
using System.Linq;

namespace ForgeC;

// The standard library, written in Forge and added to a program only as far as it
// uses it (each function it calls, and what those call in turn).
//
// Math: C#-style Math.Sqrt / Pow / Exp / Log / Sin / ... The parser turns Math.Name(..)
// into a call named "Math.Name"; the type checker maps it to the functions below
// (picking the int or double version of Abs/Min/Max/Sign/Clamp by argument type).
// Sqrt is the sqrtsd instruction (exact); exp, log, sin, cos, atan follow fdlibm
// (the reference libm behind most C libraries and Java): argument reduction plus
// minimax polynomials, accurate to about one unit in the last place.
static class StdLib {
    public const string MathSource = @"
module __stdlib_math;

// ---- bits of a double (sign + exponent + top of the mantissa / the low word)
long __hi(double x) {
    long b = *(long*)&x;
    long h = b >> 32;
    if (h >= 2147483648) { h -= 4294967296; }
    return h;
}
long __lo(double x) { long b = *(long*)&x; return b & 4294967295; }
double __make(long hi, long lo) { long b = (hi << 32) | (lo & 4294967295); return *(double*)&b; }
double __math_inf() { return __make(2146435072, 0); }
double __math_nan() { return __make(2146959360, 0); }
double __negzero() { return __make(-2147483648, 0); }
// t with the sign of x when t is zero (Math.Ceiling(-0.5) is -0, as in C#)
double __zsign(double t, double x) { if (t == 0.0 && __hi(x) < 0) { return __negzero(); } return t; }

// ---- rounding (exact, SSE2 only)
double __math_truncate(double x) {
    if (x != x || x >= 4503599627370496.0 || x <= -4503599627370496.0) { return x; }   // NaN, inf, |x| >= 2^52
    return __zsign((double)(long)x, x);
}
double __math_floor(double x) {
    if (x != x || x >= 4503599627370496.0 || x <= -4503599627370496.0) { return x; }
    double t = (double)(long)x;
    if (t > x) { t = t - 1.0; }
    return __zsign(t, x);
}
double __math_ceiling(double x) {
    if (x != x || x >= 4503599627370496.0 || x <= -4503599627370496.0) { return x; }
    double t = (double)(long)x;
    if (t < x) { t = t + 1.0; }
    return __zsign(t, x);
}
// to the nearest integer; halves go to the even neighbour (C#'s default)
double __math_round(double x) {
    if (x != x || x >= 4503599627370496.0 || x <= -4503599627370496.0) { return x; }
    double f = __math_floor(x);
    double d = x - f;
    double r = f;
    if (d > 0.5) { r = f + 1.0; }
    else if (d == 0.5) {
        double h = f / 2.0;
        if (__math_floor(h) != h) { r = f + 1.0; }
    }
    return __zsign(r, x);
}

// ---- Abs / Min / Max / Sign / Clamp, double and integer versions
double __math_abs(double x) { return __make(__hi(x) & 2147483647, __lo(x)); }
long __math_abs_i(long x) { if (x < 0) { return -x; } return x; }
double __math_max(double a, double b) {
    if (a != a) { return a; }
    if (b != b) { return b; }
    if (a > b) { return a; }
    if (b > a) { return b; }
    if (__hi(a) < 0) { return b; }                  // equal: max(-0, 0) is 0
    return a;
}
double __math_min(double a, double b) {
    if (a != a) { return a; }
    if (b != b) { return b; }
    if (a < b) { return a; }
    if (b < a) { return b; }
    if (__hi(a) < 0) { return a; }                  // equal: min(-0, 0) is -0
    return b;
}
long __math_max_i(long a, long b) { if (a > b) { return a; } return b; }
long __math_min_i(long a, long b) { if (a < b) { return a; } return b; }
long __math_sign(double x) { if (x > 0.0) { return 1; } if (x < 0.0) { return -1; } return 0; }
long __math_sign_i(long x) { if (x > 0) { return 1; } if (x < 0) { return -1; } return 0; }
double __math_clamp(double v, double lo, double hi) { if (v < lo) { return lo; } if (v > hi) { return hi; } return v; }
long __math_clamp_i(long v, long lo, long hi) { if (v < lo) { return lo; } if (v > hi) { return hi; } return v; }

// ---- exp (fdlibm e_exp.c): x = k*ln2 + r, exp(r) by a rational approximation
double __math_exp(double x) {
    long hx = __hi(x);
    long xsb = 0;
    if (hx < 0) { xsb = 1; }
    hx = hx & 2147483647;
    double hi = 0.0;
    double lo = 0.0;
    long k = 0;
    if (hx >= 1082535490) {                                 // |x| >= 709.78
        if (hx >= 2146435072) {
            if (((hx & 1048575) | __lo(x)) != 0) { return x + x; }   // NaN
            if (xsb == 0) { return x; }                             // exp(+inf) = inf
            return 0.0;                                             // exp(-inf) = 0
        }
        if (x > 709.782712893383973096) { return __math_inf(); }
        if (x < -745.13321910194110842) { return 0.0; }
    }
    if (hx > 1071001154) {                                  // |x| > 0.5 ln2
        if (hx < 1072734898) {                              // and < 1.5 ln2
            if (xsb == 0) { hi = x - 0.693147180369123816490; lo = 0.000000000190821492927058770002; k = 1; }
            else { hi = x + 0.693147180369123816490; lo = -0.000000000190821492927058770002; k = -1; }
        } else {
            double half = 0.5;
            if (xsb == 1) { half = -0.5; }
            k = (long)(1.44269504088896338700 * x + half);
            double tk = (double)k;
            hi = x - tk * 0.693147180369123816490;
            lo = tk * 0.000000000190821492927058770002;
        }
        x = hi - lo;
    } else if (hx < 1043333120) {                           // |x| < 2^-28
        return 1.0 + x;
    }
    double t = x * x;
    double c = x - t * (0.166666666666666019037 + t * (-0.00277777777770155933842 + t * (0.0000661375632143793436117
        + t * (-0.00000165339022054652515390 + t * 0.0000000413813679705723846039))));
    if (k == 0) { return 1.0 - ((x * c) / (c - 2.0) - x); }
    double y = 1.0 - ((lo - (x * c) / (2.0 - c)) - hi);
    if (k >= -1021) { return __make(__hi(y) + (k << 20), __lo(y)); }
    return __make(__hi(y) + ((k + 1000) << 20), __lo(y)) * __make(24117248, 0);     // * 2^-1000
}

// ---- log (fdlibm e_log.c): x = 2^k * (1+f), log(1+f) = f - s*(f - R(s*s)), s = f/(2+f)
double __math_log(double x) {
    long hx = __hi(x);
    long lx = __lo(x);
    long k = 0;
    if (hx < 1048576) {                                     // x < 2^-1022, or negative
        if (((hx & 2147483647) | lx) == 0) { return -__math_inf(); }   // log(+-0) = -inf
        if (hx < 0) { return __math_nan(); }                             // log(negative) = NaN
        k = -54;
        x = x * 18014398509481984.0;                        // 2^54: scale a subnormal up
        hx = __hi(x);
    }
    if (hx >= 2146435072) { return x + x; }                 // inf, NaN
    k = k + ((hx >> 20) - 1023);
    hx = hx & 1048575;
    long i = (hx + 614244) & 1048576;
    x = __make(hx | (i ^ 1072693248), __lo(x));             // normalize x or x/2
    k = k + (i >> 20);
    double f = x - 1.0;
    double dk = (double)k;
    if ((1048575 & (2 + hx)) < 3) {                         // -2^-20 <= f < 2^-20
        if (f == 0.0) {
            if (k == 0) { return 0.0; }
            return dk * 0.693147180369123816490 + dk * 0.000000000190821492927058770002;
        }
        double r0 = f * f * (0.5 - 0.33333333333333333 * f);
        if (k == 0) { return f - r0; }
        return dk * 0.693147180369123816490 - ((r0 - dk * 0.000000000190821492927058770002) - f);
    }
    double s = f / (2.0 + f);
    double z = s * s;
    long i2 = hx - 398458;
    double w = z * z;
    long j = 440401 - hx;
    double t1 = w * (0.3999999999940941908 + w * (0.2222219843214978396 + w * 0.1531383769920937332));
    double t2 = z * (0.6666666666666735130 + w * (0.2857142874366239149 + w * (0.1818357216161805012 + w * 0.1479819860511658591)));
    i2 = i2 | j;
    double r = t2 + t1;
    if (i2 > 0) {
        double hfsq = 0.5 * f * f;
        if (k == 0) { return f - (hfsq - s * (hfsq + r)); }
        return dk * 0.693147180369123816490 - ((hfsq - (s * (hfsq + r) + dk * 0.000000000190821492927058770002)) - f);
    }
    if (k == 0) { return f - s * (f - r); }
    return dk * 0.693147180369123816490 - ((s * (f - r) - dk * 0.000000000190821492927058770002) - f);
}

// x = 2^k * m with m in [1, 2) (or [0.5, 1) for k < 0, which keeps log(m) accurate)
// log10(x) = k*log10(2) + log(m)/ln(10);  log2(x) = k + log(m)/ln(2). Exact for powers of 2.
double __math_log10(double x) {
    long hx = __hi(x);
    long lx = __lo(x);
    long k = 0;
    if (hx < 1048576) {
        if (((hx & 2147483647) | lx) == 0) { return -__math_inf(); }
        if (hx < 0) { return __math_nan(); }
        k = -54;
        x = x * 18014398509481984.0;
        hx = __hi(x);
    }
    if (hx >= 2146435072) { return x + x; }
    k = k + ((hx >> 20) - 1023);
    long i = 0;
    if (k < 0) { i = 1; }
    hx = (hx & 1048575) | ((1023 - i) << 20);
    double y = (double)(k + i);
    x = __make(hx, __lo(x));
    double z = y * 0.000000000000369423907715893078616 + 0.434294481903251816668 * __math_log(x);
    return z + y * 0.301029995663611771306;
}
double __math_log2(double x) {
    long hx = __hi(x);
    long lx = __lo(x);
    long k = 0;
    if (hx < 1048576) {
        if (((hx & 2147483647) | lx) == 0) { return -__math_inf(); }
        if (hx < 0) { return __math_nan(); }
        k = -54;
        x = x * 18014398509481984.0;
        hx = __hi(x);
    }
    if (hx >= 2146435072) { return x + x; }
    k = k + ((hx >> 20) - 1023);
    long i = 0;
    if (k < 0) { i = 1; }
    hx = (hx & 1048575) | ((1023 - i) << 20);
    double y = (double)(k + i);
    x = __make(hx, __lo(x));
    return y + 1.44269504088896340736 * __math_log(x);
}
double __math_logb(double a, double b) { return __math_log(a) / __math_log(b); }

// ---- pow (fdlibm e_pow.c): log2(|x|) to about 70 bits as t1 + t2, y * log2(x) as
// p_h + p_l, then 2^(p_h + p_l). Accurate to about one unit in the last place.
double __scalbn(double z, long n) {
    if (n >= -1022) { return z * __make((n + 1023) << 20, 0); }
    return z * __make((n + 1023 + 100) << 20, 0) * __make((1023 - 100) << 20, 0);
}
double __math_pow(double x, double y) {
    long hx = __hi(x);
    long lx = __lo(x);
    long hy = __hi(y);
    long ly = __lo(y);
    long ix = hx & 2147483647;
    long iy = hy & 2147483647;
    if ((iy | ly) == 0) { return 1.0; }                     // x^0 = 1 (even NaN)
    if (x == 1.0) { return 1.0; }                           // 1^y = 1 (even NaN)
    if (ix > 2146435072 || (ix == 2146435072 && lx != 0) || iy > 2146435072 || (iy == 2146435072 && ly != 0)) { return x + y; }
    // yisint: 0 = y not an integer, 1 = odd integer, 2 = even integer (only needed for x < 0)
    long yisint = 0;
    if (hx < 0) {
        if (iy >= 1128267776) { yisint = 2; }                // |y| >= 2^53: even
        else if (iy >= 1072693248) {
            long ke = (iy >> 20) - 1023;
            if (ke > 20) {
                long jj = ly >> (52 - ke);
                if ((jj << (52 - ke)) == ly) { yisint = 2 - (jj & 1); }
            } else if (ly == 0) {
                long jj = iy >> (20 - ke);
                if ((jj << (20 - ke)) == iy) { yisint = 2 - (jj & 1); }
            }
        }
    }
    if (ly == 0) {
        if (iy == 2146435072) {                             // y = +-inf
            if (((ix - 1072693248) | lx) == 0) { return 1.0; }   // (+-1)^inf = 1
            if (ix >= 1072693248) { if (hy >= 0) { return y; } return 0.0; }
            if (hy < 0) { return -y; }
            return 0.0;
        }
        if (iy == 1072693248) { if (hy < 0) { return 1.0 / x; } return x; }   // y = +-1
        if (hy == 1073741824) { return x * x; }             // y = 2
        if (hy == 1071644672 && hx >= 0) { return __sqrt(x); }   // y = 0.5
    }
    double ax = __math_abs(x);
    if (lx == 0 && (ix == 2146435072 || ix == 0 || ix == 1072693248)) {   // x = +-0, +-inf, +-1
        double z0 = ax;
        if (hy < 0) { z0 = 1.0 / z0; }
        if (hx < 0) {
            if (((ix - 1072693248) | yisint) == 0) { z0 = __math_nan(); }   // (-1)^non-int
            else if (yisint == 1) { z0 = -z0; }
        }
        return z0;
    }
    long n = 1;
    if (hx < 0) { n = 0; }
    if ((n | yisint) == 0) { return __math_nan(); }         // (x < 0)^non-int
    double s = 1.0;
    if ((n | (yisint - 1)) == 0) { s = -1.0; }              // (x < 0)^odd
    double t1 = 0.0;
    double t2 = 0.0;
    if (iy > 1105199104) {                                  // |y| > 2^31
        if (iy > 1139802112) {                              // |y| > 2^64: must over/underflow
            if (ix <= 1072693247) { if (hy < 0) { return __math_inf(); } return 0.0; }
            if (ix >= 1072693248) { if (hy > 0) { return __math_inf(); } return 0.0; }
        }
        if (ix < 1072693247) { if (hy < 0) { return s * __math_inf(); } return s * 0.0; }
        if (ix > 1072693248) { if (hy > 0) { return s * __math_inf(); } return s * 0.0; }
        // |1 - x| <= 2^-20: log(x) by x - x^2/2 + x^3/3 - x^4/4
        double t = ax - 1.0;
        double w = (t * t) * (0.5 - t * (0.3333333333333333333333 - t * 0.25));
        double u = 1.44269502162933349609 * t;
        double v = t * 0.0000000192596299112661746887 - w * 1.44269504088896338700;
        t1 = __make(__hi(u + v), 0);
        t2 = v - (t1 - u);
    } else {
        long nn = 0;
        if (ix < 1048576) { ax = ax * 9007199254740992.0; nn = nn - 53; ix = __hi(ax); }   // subnormal
        nn = nn + ((ix >> 20) - 1023);
        long j = ix & 1048575;
        ix = j | 1072693248;                                // normalize
        long k = 0;
        if (j <= 235662) { k = 0; }                         // |x| < sqrt(3/2)
        else if (j < 767610) { k = 1; }                     // |x| < sqrt(3)
        else { k = 0; nn = nn + 1; ix = ix - 1048576; }
        ax = __make(ix, __lo(ax));
        double bp = 1.0;
        double dph = 0.0;
        double dpl = 0.0;
        if (k == 1) { bp = 1.5; dph = 0.584962487220764160156; dpl = 0.0000000135003920212974897128; }
        // ss = s_h + s_l = (x - bp)/(x + bp)
        double u = ax - bp;
        double v = 1.0 / (ax + bp);
        double ss = u * v;
        double sh = __make(__hi(ss), 0);
        double th = __make(((ix >> 1) | 536870912) + 524288 + (k << 18), 0);
        double tl = ax - (th - bp);
        double sl = v * ((u - sh * th) - sh * tl);
        // log(ax)
        double s2 = ss * ss;
        double r = s2 * s2 * (0.599999999999994648725 + s2 * (0.428571428578550184252 + s2 * (0.333333329818377432918
            + s2 * (0.272728123808534006489 + s2 * (0.230660745775561754067 + s2 * 0.206975017800338417784)))));
        r = r + sl * (sh + ss);
        s2 = sh * sh;
        th = __make(__hi(3.0 + s2 + r), 0);
        tl = r - ((th - 3.0) - s2);
        u = sh * th;
        v = sl * th + tl * ss;
        double ph = __make(__hi(u + v), 0);
        double pl = v - (ph - u);
        double zh = 0.961796700954437255859 * ph;            // cp_h + cp_l = 2/(3 ln 2)
        double zl = -0.00000000702846165095275826516 * ph + pl * 0.961796693925975554329 + dpl;
        double tn = (double)nn;
        t1 = __make(__hi(((zh + zl) + dph) + tn), 0);
        t2 = zl - (((t1 - tn) - dph) - zh);
    }
    // (y1 + y2) * (t1 + t2)
    double y1 = __make(__hi(y), 0);
    double p_l = (y - y1) * t1 + y * t2;
    double p_h = y1 * t1;
    double z = p_l + p_h;
    long jz = __hi(z);
    long iz = __lo(z);
    if (jz >= 1083179008) {                                 // z >= 1024
        if (((jz - 1083179008) | iz) != 0) { return s * __math_inf(); }
        if (p_l + 0.000000000000000080085662595372944372 > z - p_h) { return s * __math_inf(); }
    } else if ((jz & 2147483647) >= 1083231232) {           // z <= -1075
        if ((((jz & 4294967295) - 3230714880) | iz) != 0) { return s * 0.0; }
        if (p_l <= z - p_h) { return s * 0.0; }
    }
    // 2^(p_h + p_l)
    long i = jz & 2147483647;
    long k2 = (i >> 20) - 1023;
    long n2 = 0;
    if (i > 1071644672) {                                   // |z| > 0.5: n2 = [z + 0.5]
        n2 = jz + (1048576 >> (k2 + 1));
        k2 = ((n2 & 2147483647) >> 20) - 1023;
        double tt = __make(n2 & (-1 - (1048575 >> k2)), 0);        // n2 with its low mantissa bits cleared
        n2 = ((n2 & 1048575) | 1048576) >> (20 - k2);
        if (jz < 0) { n2 = -n2; }
        p_h = p_h - tt;
    }
    double t = __make(__hi(p_l + p_h), 0);
    double u2 = t * 0.693147182464599609375;
    double v2 = (p_l - (t - p_h)) * 0.693147180559945286227 + t * -0.00000000190465429995776804525;
    z = u2 + v2;
    double w2 = v2 - (z - u2);
    t = z * z;
    double tt1 = z - t * (0.166666666666666019037 + t * (-0.00277777777770155933842 + t * (0.0000661375632143793436117
        + t * (-0.00000165339022054652515390 + t * 0.0000000413813679705723846039))));
    double rr = (z * tt1) / (tt1 - 2.0) - (w2 + z * w2);
    z = 1.0 - (rr - z);
    long jj2 = __hi(z) + (n2 << 20);
    if ((jj2 >> 20) <= 0 || jj2 < 0) { z = __scalbn(z, n2); }   // subnormal result
    else { z = __make(jj2, __lo(z)); }
    return s * z;
}

// ---- sin / cos / tan (fdlibm k_sin.c, k_cos.c, e_rem_pio2.c)
// reduce x by n*pi/2 to y0 + y1 in [-pi/4, pi/4] (Cody-Waite, three rounds); returns n.
// Accurate while |x| < 2^20 * pi/2 (about 1.6 million); beyond that precision drops.
long __rem_pio2(double x, double* y0, double* y1) {
    long ix = __hi(x) & 2147483647;
    double t = x * 0.636619772367581382433;
    double rnd = 0.5;
    if (t < 0.0) { rnd = -0.5; }
    double fn = (double)(long)(t + rnd);
    long n = (long)fn;
    double r = x - fn * 1.57079632673412561417;
    double w = fn * 0.0000000000607710050650619224932;
    long j = ix >> 20;
    *y0 = r - w;
    long i = j - ((__hi(*y0) >> 20) & 2047);
    if (i > 16) {
        double t2 = r;
        w = fn * 0.0000000000607710050630396597660;
        r = t2 - w;
        w = fn * 0.00000000000000000000202226624879595063154 - ((t2 - r) - w);
        *y0 = r - w;
        i = j - ((__hi(*y0) >> 20) & 2047);
        if (i > 49) {
            t2 = r;
            w = fn * 0.00000000000000000000202226624871116645580;
            r = t2 - w;
            w = fn * 0.0000000000000000000000000000000847842766036889956997 - ((t2 - r) - w);
            *y0 = r - w;
        }
    }
    *y1 = (r - *y0) - w;
    return n;
}
double __k_sin(double x, double y, long iy) {
    double z = x * x;
    double v = z * x;
    double r = 0.00833333333332248946124 + z * (-0.000198412698298579493134 + z * (0.00000275573137070700676789
        + z * (-0.0000000250507602534068634195 + z * 0.000000000158969099521155010221)));
    if (iy == 0) { return x + v * (-0.166666666666666324348 + z * r); }
    return x - ((z * (0.5 * y - v * r) - y) - v * -0.166666666666666324348);
}
double __k_cos(double x, double y) {
    double z = x * x;
    double w = z * z;
    double r = z * (0.0416666666666666019037 + z * (-0.00138888888888741095749 + z * 0.0000248015872894767294178))
        + w * w * (-0.000000275573143513906633035 + z * (0.00000000208757232129817482790 + z * -0.0000000000113596475577881948265));
    double hz = 0.5 * z;
    w = 1.0 - hz;
    return w + (((1.0 - w) - hz) + (z * r - x * y));
}
double __math_sin(double x) {
    long ix = __hi(x) & 2147483647;
    if (ix < 1044381696) { return x; }                      // |x| < 2^-27: sin(x) = x (keeps -0)
    if (ix <= 1072243195) { return __k_sin(x, 0.0, 0); }    // |x| <= pi/4
    if (ix >= 2146435072) { return x - x; }                 // inf, NaN -> NaN
    double y0 = 0.0;
    double y1 = 0.0;
    long n = __rem_pio2(x, &y0, &y1) & 3;
    if (n == 0) { return __k_sin(y0, y1, 1); }
    if (n == 1) { return __k_cos(y0, y1); }
    if (n == 2) { return -__k_sin(y0, y1, 1); }
    return -__k_cos(y0, y1);
}
double __math_cos(double x) {
    long ix = __hi(x) & 2147483647;
    if (ix <= 1072243195) { return __k_cos(x, 0.0); }
    if (ix >= 2146435072) { return x - x; }
    double y0 = 0.0;
    double y1 = 0.0;
    long n = __rem_pio2(x, &y0, &y1) & 3;
    if (n == 0) { return __k_cos(y0, y1); }
    if (n == 1) { return -__k_sin(y0, y1, 1); }
    if (n == 2) { return -__k_cos(y0, y1); }
    return __k_sin(y0, y1, 1);
}
double __math_tan(double x) { return __math_sin(x) / __math_cos(x); }

// ---- atan (fdlibm s_atan.c), atan2, asin, acos
double __math_atan(double x) {
    long hx = __hi(x);
    long ix = hx & 2147483647;
    if (ix >= 1141899264) {                                 // |x| >= 2^66 (or NaN)
        if (x != x) { return x + x; }
        if (hx > 0) { return 1.57079632679489655800 + 0.0000000000000000612323399573676603587; }
        return -1.57079632679489655800 - 0.0000000000000000612323399573676603587;
    }
    long id = -1;
    if (ix < 1071382528) {                                  // |x| < 0.4375
        if (ix < 1044381696) { return x; }                  // |x| < 2^-27
    } else {
        x = __math_abs(x);
        if (ix < 1072889856) {                              // |x| < 1.1875
            if (ix < 1072037888) { id = 0; x = (2.0 * x - 1.0) / (2.0 + x); }   // 7/16 <= |x| < 11/16
            else { id = 1; x = (x - 1.0) / (x + 1.0); }                        // 11/16 <= |x| < 19/16
        } else {
            if (ix < 1073971200) { id = 2; x = (x - 1.5) / (1.0 + 1.5 * x); }  // |x| < 2.4375
            else { id = 3; x = -1.0 / x; }                                     // 2.4375 <= |x| < 2^66
        }
    }
    double z = x * x;
    double w = z * z;
    double s1 = z * (0.333333333333329318027 + w * (0.142857142725034663711 + w * (0.0909088713343650656196
        + w * (0.0666107313738753120669 + w * (0.0497687799461593236017 + w * 0.0162858201153657823623)))));
    double s2 = w * (-0.199999999998764832476 + w * (-0.111111104054623557880 + w * (-0.0769187620504482999495
        + w * (-0.0583357013379057348645 + w * -0.0365315727442169155270))));
    if (id < 0) { return x - x * (s1 + s2); }
    double hi = 1.57079632679489655800;
    double lo = 0.0000000000000000612323399573676603587;
    if (id == 0) { hi = 0.463647609000806093515; lo = 0.0000000000000000226987774529616870924; }
    else if (id == 1) { hi = 0.785398163397448278999; lo = 0.0000000000000000306161699786838301793; }
    else if (id == 2) { hi = 0.982793723247329054082; lo = 0.0000000000000000139033110312309984516; }
    z = hi - ((x * (s1 + s2) - lo) - x);
    if (hx < 0) { return -z; }
    return z;
}
double __math_atan2(double y, double x) {
    if (x != x || y != y) { return x + y; }
    double pi = 3.1415926535897931160;
    double pilo = 0.00000000000000012246467991473531772;
    if (x == 1.0) { return __math_atan(y); }
    bool yneg = __hi(y) < 0;
    bool xneg = __hi(x) < 0;
    if (y == 0.0) {
        if (!xneg) { return y; }                            // atan2(+-0, +x) = +-0
        if (yneg) { return -pi - pilo; }
        return pi + pilo;
    }
    if (x == 0.0) { if (yneg) { return -1.57079632679489661923; } return 1.57079632679489661923; }
    bool xinf = x - x != 0.0;
    bool yinf = y - y != 0.0;
    if (xinf) {
        double a = 0.0;
        if (yinf) { a = 0.785398163397448309616; if (xneg) { a = 2.356194490192344928846; } }
        else if (xneg) { a = pi + pilo; }
        if (yneg) { return -a; }
        return a;
    }
    if (yinf) { if (yneg) { return -1.57079632679489661923; } return 1.57079632679489661923; }
    double z = __math_atan(__math_abs(y / x));
    if (!xneg) { if (yneg) { return -z; } return z; }
    if (yneg) { return (z - pilo) - pi; }
    return pi - (z - pilo);
}
double __math_asin(double x) {
    if (x > 1.0 || x < -1.0) { return __math_nan(); }
    return __math_atan2(x, __sqrt((1.0 - x) * (1.0 + x)));
}
double __math_acos(double x) {
    if (x > 1.0 || x < -1.0) { return __math_nan(); }
    return __math_atan2(__sqrt((1.0 - x) * (1.0 + x)), x);
}
";

    // Math.Name -> the runtime function(s) a call may need (both versions where the
    // argument types pick one). Sqrt is an instruction (__sqrt), not a function.
    public static readonly Dictionary<string, string[]> MathMembers = new() {
        ["Sqrt"] = new string[0],
        ["Abs"] = new[] { "__math_abs", "__math_abs_i" },
        ["Min"] = new[] { "__math_min", "__math_min_i" },
        ["Max"] = new[] { "__math_max", "__math_max_i" },
        ["Sign"] = new[] { "__math_sign", "__math_sign_i" },
        ["Clamp"] = new[] { "__math_clamp", "__math_clamp_i" },
        ["Floor"] = new[] { "__math_floor" },
        ["Ceiling"] = new[] { "__math_ceiling" },
        ["Round"] = new[] { "__math_round" },
        ["Truncate"] = new[] { "__math_truncate" },
        ["Exp"] = new[] { "__math_exp" },
        ["Log"] = new[] { "__math_log", "__math_logb" },
        ["Log10"] = new[] { "__math_log10" },
        ["Log2"] = new[] { "__math_log2" },
        ["Pow"] = new[] { "__math_pow" },
        ["Sin"] = new[] { "__math_sin" },
        ["Cos"] = new[] { "__math_cos" },
        ["Tan"] = new[] { "__math_tan" },
        ["Asin"] = new[] { "__math_asin" },
        ["Acos"] = new[] { "__math_acos" },
        ["Atan"] = new[] { "__math_atan" },
        ["Atan2"] = new[] { "__math_atan2" },
    };

    // Add the library functions the module needs (and what they call).
    public static void Inject(ModuleNode m) {
        var wanted = new HashSet<string>();
        foreach (var f in m.Funcs)
            Walk(f.Body, name => {
                if (name.StartsWith("Math.") && MathMembers.TryGetValue(name[5..], out var fs)) wanted.UnionWith(fs);
            });
        if (wanted.Count == 0) return;
        var lib = new Parser(new Lexer(MathSource).Lex()).ParseModule().Funcs.ToDictionary(f => f.Name);
        var have = new HashSet<string>(m.Funcs.Select(f => f.Name));
        var queue = new Queue<string>(wanted);
        while (queue.Count > 0) {
            var n = queue.Dequeue();
            if (have.Contains(n) || !lib.TryGetValue(n, out var f)) continue;
            have.Add(n);
            f.IsRuntime = true; f.Line = 0;
            m.Funcs.Add(f);
            Walk(f.Body, callee => { if (lib.ContainsKey(callee) && !have.Contains(callee)) queue.Enqueue(callee); });
        }
    }

    static void Walk(List<Stmt> body, Action<string> onCall) =>
        Walker.Walk(body, e => { if (e is CallExpr c) onCall(c.Name); }, null);
}

sealed partial class CodeGen {
    // Math.Name(args) -> the instruction or library function that implements it.
    // Abs/Min/Max/Sign/Clamp keep integers integral: Math.Abs(-3) is the int 3.
    void ResolveMathCall(CallExpr c) {
        string member = c.Name[5..];
        if (!StdLib.MathMembers.ContainsKey(member))
            throw new ForgeException($"Math has no member '{member}'" + Suggest.Hint(member, StdLib.MathMembers.Keys.Concat(new[] { "PI", "E", "Tau" })), c.Line);
        int want = member switch {
            "Clamp" => 3,
            "Min" or "Max" or "Pow" or "Atan2" => 2,
            "Log" => c.Args.Count == 2 ? 2 : 1,
            _ => 1,
        };
        if (c.Args.Count != want)
            throw new ForgeException($"Math.{member} takes {want} argument(s), got {c.Args.Count}", c.Line);
        foreach (var a in c.Args) {
            var t = TypeOf(a);
            if (!IsNum(t)) throw new ForgeException($"Math.{member} takes numbers, got {t}", c.Line);
        }
        bool allInt = c.Args.All(a => IsInt(TypeOf(a)));
        c.Name = member switch {
            "Sqrt" => "__sqrt",
            "Abs" or "Min" or "Max" or "Sign" or "Clamp" => "__math_" + member.ToLowerInvariant() + (allInt ? "_i" : ""),
            "Log" when c.Args.Count == 2 => "__math_logb",
            _ => "__math_" + member.ToLowerInvariant(),
        };
    }
}
