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

    // ---- collections and strings ------------------------------------------------------
    public const string CollectionsSource = @"
module __stdlib_collections;

void __fail(string msg) { Console.WriteLine(msg); exit(1); }

// ---- hashing and ordering, picked by type: __hash(x) and __less(a, b) are resolved
// by the type checker to the right function for the argument type
long __hash_int(long x) {
    x = x ^ (x >> 33);
    x = x * -49064778989728563;           // 0xff51afd7ed558ccd (MurmurHash3 fmix64)
    x = x ^ (x >> 33);
    return x;
}
long __hash_str(string s) {                // FNV-1a
    long h = -3750763034362895579;
    for (long i = 0; i < s.length; i++) { h = (h ^ s[i]) * 1099511628211; }
    return h;
}
long __hash_dbl(double d) { if (d == 0.0) { return 0; } return __hash_int(*(long*)&d); }   // 0.0 == -0.0
bool __num_less<T>(T a, T b) { return a < b; }
bool __str_less(string a, string b) {      // ordinal comparison, like string.CompareOrdinal
    long n = a.length;
    if (b.length < n) { n = b.length; }
    for (long i = 0; i < n; i++) { if (a[i] != b[i]) { return a[i] < b[i]; } }
    return a.length < b.length;
}
void __qsort<T>(T[] a, long lo, long hi) {
    while (lo < hi) {
        T p = a[(lo + hi) / 2];
        long i = lo;
        long j = hi;
        while (i <= j) {
            while (__less(a[i], p)) { i++; }
            while (__less(p, a[j])) { j--; }
            if (i <= j) { T t = a[i]; a[i] = a[j]; a[j] = t; i++; j--; }
        }
        if (j - lo < hi - i) { __qsort(a, lo, j); lo = i; }   // recurse into the smaller half
        else { __qsort(a, i, hi); hi = j; }
    }
}

// ---- List<T>: a growable array
struct List<T> {
    T[] items;
    int Count;

    void Reserve(long need) {
        long cap = 0;
        if (items != null) { cap = items.length; }
        if (need <= cap) { return; }
        if (cap < 4) { cap = 4; }
        while (cap < need) { cap = cap * 2; }
        T[] bigger = new T[cap];
        for (long i = 0; i < Count; i++) { bigger[i] = items[i]; }
        items = bigger;
    }
    void Check(long i) { if (i < 0 || i >= Count) { __fail($""List index {i} is out of range (Count is {Count})""); } }
    void Add(T x) { Reserve(Count + 1); items[Count] = x; Count++; }
    T Get(long i) { Check(i); return items[i]; }
    void Set(long i, T x) { Check(i); items[i] = x; }
    void Insert(long i, T x) {
        if (i < 0 || i > Count) { __fail($""List.Insert index {i} is out of range (Count is {Count})""); }
        Reserve(Count + 1);
        for (long k = Count; k > i; k--) { items[k] = items[k - 1]; }
        items[i] = x;
        Count++;
    }
    void RemoveAt(long i) {
        Check(i);
        for (long k = i; k < Count - 1; k++) { items[k] = items[k + 1]; }
        Count--;
    }
    long IndexOf(T x) {
        for (long i = 0; i < Count; i++) { if (items[i] == x) { return i; } }
        return -1;
    }
    bool Contains(T x) { return IndexOf(x) >= 0; }
    bool Remove(T x) {
        long i = IndexOf(x);
        if (i < 0) { return false; }
        RemoveAt(i);
        return true;
    }
    void Clear() { Count = 0; }
    void Reverse() {
        long a = 0;
        long b = Count - 1;
        while (a < b) { T t = items[a]; items[a] = items[b]; items[b] = t; a++; b--; }
    }
    void Sort() { if (Count > 1) { __qsort(items, 0, Count - 1); } }
    T[] ToArray() {
        T[] r = new T[Count];
        for (long i = 0; i < Count; i++) { r[i] = items[i]; }
        return r;
    }
}

// ---- Dictionary<K, V>: a hash table (open addressing, linear probing)
struct Dictionary<K, V> {
    K[] keys;
    V[] vals;
    long[] state;                          // 0 empty, 1 in use, 2 removed
    int Count;
    long filled;                           // in use + removed slots

    void Rehash(long cap) {
        K[] ok = keys;
        V[] ov = vals;
        long[] os = state;
        keys = new K[cap];
        vals = new V[cap];
        state = new long[cap];
        Count = 0;
        filled = 0;
        if (ok != null) {
            for (long i = 0; i < ok.length; i++) { if (os[i] == 1) { Set(ok[i], ov[i]); } }
        }
    }
    long Find(K key) {                     // the slot holding key, or -1
        if (keys == null) { return -1; }
        long mask = keys.length - 1;
        long i = __hash(key) & mask;
        while (state[i] != 0) {
            if (state[i] == 1 && keys[i] == key) { return i; }
            i = (i + 1) & mask;
        }
        return -1;
    }
    void Set(K key, V value) {
        if (keys == null) { Rehash(8); }
        if ((filled + 1) * 4 > keys.length * 3) {           // keep it at most 3/4 full
            long cap = keys.length;
            if ((Count + 1) * 2 > cap) { cap = cap * 2; }   // (else: just clear out removed slots)
            Rehash(cap);
        }
        long mask = keys.length - 1;
        long i = __hash(key) & mask;
        long free = -1;
        while (state[i] != 0) {
            if (state[i] == 1 && keys[i] == key) { vals[i] = value; return; }
            if (state[i] == 2 && free < 0) { free = i; }
            i = (i + 1) & mask;
        }
        if (free >= 0) { i = free; } else { filled++; }
        keys[i] = key;
        vals[i] = value;
        state[i] = 1;
        Count++;
    }
    V Get(K key) {
        long i = Find(key);
        if (i < 0) { __fail(""Dictionary: the key was not found""); }
        return vals[i];
    }
    bool TryGetValue(K key, V* value) {
        long i = Find(key);
        if (i < 0) { return false; }
        *value = vals[i];
        return true;
    }
    bool ContainsKey(K key) { return Find(key) >= 0; }
    bool Remove(K key) {
        long i = Find(key);
        if (i < 0) { return false; }
        state[i] = 2;
        Count--;
        return true;
    }
    void Clear() { keys = null; vals = null; state = null; Count = 0; filled = 0; }
    List<K> Keys() {
        var r = new List<K> { };
        if (keys != null) { for (long i = 0; i < keys.length; i++) { if (state[i] == 1) { r.Add(keys[i]); } } }
        return r;
    }
    List<V> Values() {
        var r = new List<V> { };
        if (keys != null) { for (long i = 0; i < keys.length; i++) { if (state[i] == 1) { r.Add(vals[i]); } } }
        return r;
    }
}

// ---- StringBuilder: append without copying the whole string each time
struct StringBuilder {
    u8* buf;
    long Length;
    long cap;

    void Reserve(long need) {
        if (need + 1 <= cap) { return; }
        long c = 16;
        if (cap > c) { c = cap; }
        while (c < need + 1) { c = c * 2; }
        u8* nb = malloc(c);
        for (long i = 0; i < Length; i++) { nb[i] = buf[i]; }
        buf = nb;
        cap = c;
    }
    void Append(string s) {
        long n = s.length;
        Reserve(Length + n);
        for (long i = 0; i < n; i++) { buf[Length + i] = s[i]; }
        Length = Length + n;
    }
    void AppendLine(string s) { Append(s); Append(""\n""); }
    void Clear() { Length = 0; }
    string ToString() {
        u8* r = malloc(Length + 1);
        for (long i = 0; i < Length; i++) { r[i] = buf[i]; }
        r[Length] = 0;
        return (string)r;
    }
}

// ---- string methods: s.Contains(x) is __str_contains(s, x)
long __str_indexof_from(string s, string p, long from) {
    long n = p.length;
    long m = s.length;
    for (long i = from; i + n <= m; i++) {
        long k = 0;
        while (k < n && s[i + k] == p[k]) { k++; }
        if (k == n) { return i; }
    }
    return -1;
}
long __str_indexof(string s, string p) { return __str_indexof_from(s, p, 0); }
bool __str_contains(string s, string p) { return __str_indexof_from(s, p, 0) >= 0; }
bool __str_startswith(string s, string p) {
    if (p.length > s.length) { return false; }
    for (long i = 0; i < p.length; i++) { if (s[i] != p[i]) { return false; } }
    return true;
}
bool __str_endswith(string s, string p) {
    long n = p.length;
    long m = s.length;
    if (n > m) { return false; }
    for (long i = 0; i < n; i++) { if (s[m - n + i] != p[i]) { return false; } }
    return true;
}
string __str_substring2(string s, long start, long len) {
    if (start < 0 || len < 0 || start + len > s.length) { __fail($""Substring({start}, {len}) is out of range for a string of length {s.length}""); }
    return substr(s, start, len);
}
string __str_substring1(string s, long start) { return __str_substring2(s, start, s.length - start); }
string __str_case(string s, long lo, long hi, long delta) {
    long n = s.length;
    u8* r = malloc(n + 1);
    for (long i = 0; i < n; i++) {
        long c = s[i];
        if (c >= lo && c <= hi) { c = c + delta; }
        r[i] = c;
    }
    r[n] = 0;
    return (string)r;
}
string __str_toupper(string s) { return __str_case(s, 97, 122, -32); }
string __str_tolower(string s) { return __str_case(s, 65, 90, 32); }
bool __is_space(long c) { return c == 32 || c == 9 || c == 10 || c == 13; }
string __str_trim(string s) {
    long a = 0;
    long b = s.length;
    while (a < b && __is_space(s[a])) { a++; }
    while (b > a && __is_space(s[b - 1])) { b--; }
    return substr(s, a, b - a);
}
string __str_replace(string s, string a, string b) {
    if (a.length == 0) { __fail(""Replace: the string to replace is empty""); }
    var sb = new StringBuilder { };
    long pos = 0;
    while (true) {
        long i = __str_indexof_from(s, a, pos);
        if (i < 0) { break; }
        sb.Append(substr(s, pos, i - pos));
        sb.Append(b);
        pos = i + a.length;
    }
    sb.Append(substr(s, pos, s.length - pos));
    return sb.ToString();
}
List<string> __str_split(string s, string sep) {
    var parts = new List<string> { };
    if (sep.length == 0) { parts.Add(s); return parts; }
    long pos = 0;
    while (true) {
        long i = __str_indexof_from(s, sep, pos);
        if (i < 0) { break; }
        parts.Add(substr(s, pos, i - pos));
        pos = i + sep.length;
    }
    parts.Add(substr(s, pos, s.length - pos));
    return parts;
}
";

    // s.Name(args) on a string -> (function, number of arguments after s)
    public static readonly Dictionary<string, (string Fn, int Args)[]> StringMethods = new() {
        ["Contains"] = new[] { ("__str_contains", 1) },
        ["IndexOf"] = new[] { ("__str_indexof", 1) },
        ["StartsWith"] = new[] { ("__str_startswith", 1) },
        ["EndsWith"] = new[] { ("__str_endswith", 1) },
        ["Substring"] = new[] { ("__str_substring1", 1), ("__str_substring2", 2) },
        ["ToUpper"] = new[] { ("__str_toupper", 0) },
        ["ToLower"] = new[] { ("__str_tolower", 0) },
        ["Trim"] = new[] { ("__str_trim", 0) },
        ["Replace"] = new[] { ("__str_replace", 2) },
        ["Split"] = new[] { ("__str_split", 1) },
    };
    static readonly string[] LibTypes = { "List", "Dictionary", "StringBuilder" };
    static readonly Dictionary<string, string[]> Dispatch = new() {
        ["__hash"] = new[] { "__hash_int", "__hash_str", "__hash_dbl" },
        ["__less"] = new[] { "__num_less", "__str_less" },
    };

    // Add the library the module uses: Math functions, string methods and the List /
    // Dictionary / StringBuilder types, plus everything those use in turn.
    public static void Inject(ModuleNode m) {
        var wantFns = new HashSet<string>();
        var wantTypes = new HashSet<string>();
        var own = new HashSet<string>(m.Structs.Select(s => s.Name));
        void Calls(string name) {
            if (name.StartsWith("Math.") && MathMembers.TryGetValue(name[5..], out var fs)) wantFns.UnionWith(fs);
            else if (name.StartsWith(".") && StringMethods.TryGetValue(name[1..], out var sm)) wantFns.UnionWith(sm.Select(x => x.Fn));
            else if (Dispatch.TryGetValue(name, out var ds)) wantFns.UnionWith(ds);
            else wantFns.Add(name);
        }
        void Types(string t) {
            foreach (System.Text.RegularExpressions.Match id in System.Text.RegularExpressions.Regex.Matches(t, @"[A-Za-z_]\w*"))
                if (LibTypes.Contains(id.Value) && !own.Contains(id.Value)) wantTypes.Add(id.Value);
        }
        void Scan(Func f) {
            Types(f.Ret);
            foreach (var (t, _) in f.Params) Types(t);
            Walk(f.Body, Calls);
            new GenericCloner(t => t, "") { OnType = (t, _) => Types(t) }.CloneBody(f.Body);
        }
        foreach (var f in m.Funcs) Scan(f);
        foreach (var s in m.Structs) foreach (var (t, _) in s.Fields) Types(t);
        // a foreach can iterate a List<T> without naming the type, but then the List came from somewhere that does

        bool anyLib = wantTypes.Count > 0 || wantFns.Any(n => n.StartsWith("__math_") || n.StartsWith("__str_") || n == "__sqrt");
        if (!anyLib) return;
        var libMods = new[] { MathSource, CollectionsSource }.Select(src => new Parser(new Lexer(src, Sources.NoFile).Lex()).ParseModule()).ToList();
        var libFuncs = libMods.SelectMany(x => x.Funcs).Where(f => f.GenericOwner == null && !f.Name.Contains('.')).ToDictionary(f => f.Name);
        var libStructs = libMods.SelectMany(x => x.Structs).ToDictionary(s => s.Name);
        var libMethods = libMods.SelectMany(x => x.Funcs).Where(f => f.GenericOwner != null || f.Name.Contains('.')).ToList();
        var haveFns = new HashSet<string>(m.Funcs.Select(f => f.Name));
        var haveTypes = new HashSet<string>();
        bool progress = true;
        while (progress) {
            progress = false;
            foreach (var tn in wantTypes.ToList()) {
                if (haveTypes.Contains(tn) || own.Contains(tn) || !libStructs.TryGetValue(tn, out var sd)) continue;
                haveTypes.Add(tn); progress = true;
                m.Structs.Add(sd);
                foreach (var meth in libMethods.Where(f => f.Name.StartsWith(tn + "."))) {
                    meth.IsRuntime = true; meth.Line = 0;
                    m.Funcs.Add(meth); haveFns.Add(meth.Name);
                    Scan(meth);
                }
                foreach (var (t, _) in sd.Fields) Types(t);
            }
            foreach (var fn in wantFns.ToList()) {
                if (haveFns.Contains(fn) || !libFuncs.TryGetValue(fn, out var f)) continue;
                haveFns.Add(fn); progress = true;
                f.IsRuntime = true; f.Line = 0;
                m.Funcs.Add(f);
                Scan(f);
            }
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

sealed partial class CodeGen {
    // s.Name(args) on a string -> the library function (__str_contains(s, x), ...)
    void ResolveStringMethod(CallExpr c) {
        string name = c.Name[1..];
        if (!StdLib.StringMethods.TryGetValue(name, out var forms))
            throw new ForgeException($"string has no method '{name}'" + Suggest.Hint(name, StdLib.StringMethods.Keys), c.Line);
        int n = c.Args.Count - 1;
        var form = forms.FirstOrDefault(f => f.Args == n);
        if (form.Fn == null)
            throw new ForgeException($"string.{name} takes {string.Join(" or ", forms.Select(f => f.Args))} argument(s), got {n}", c.Line);
        c.Name = form.Fn;
    }

    // __hash(x) / __less(a, b) inside the library: the version for the argument type
    void ResolveLibDispatch(CallExpr c) {
        var t = TypeOf(c.Args[0]);
        if (c.Name == "__hash") {
            if (IsStr(t)) c.Name = "__hash_str";
            else if (t == "double") c.Name = "__hash_dbl";
            else {
                if (!IsInt(t)) c.Args[0] = new CastExpr { Line = c.Line, TargetType = "long", E = c.Args[0] };   // references hash by identity
                c.Name = "__hash_int";
            }
        } else {
            c.Name = IsStr(t) ? "__str_less" : "__num_less";
        }
    }
}
