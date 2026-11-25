using System.Collections.Generic;
using System.Linq;

namespace ForgeC;

// Text for doubles, written in Forge and added to programs that use double (like the
// async runtime). Console.WriteLine(d), str(d) and $"{d}" call __dtoa; $"{d:F2}"
// calls __dtoaf.
//
// __dtoa prints 15 significant digits with trailing zeros removed (C#'s "G15"):
// fixed notation when the decimal exponent is -5..14, otherwise d.dddE+XX.
// The number is scaled to a 15-digit integer as a double-double (Dekker's exact
// product, no FMA needed), so the last digit is rounded from the exact value.
static class DoubleRuntime {
    public const string Prelude = @"
module __double_rt;

double __pow10(long k) {
    double r = 1.0;
    while (k > 0) { r = r * 10.0; k--; }
    return r;
}

// Dekker: the high half of a (a = hi + lo, each with at most 26 significant bits)
double __split_hi(double a) {
    double c = 134217729.0 * a;
    return c - (c - a);
}

// the rounding error of p = a * b, exactly: a * b == p + err
double __prod_err(double a, double b, double p) {
    double ah = __split_hi(a);
    double al = a - ah;
    double bh = __split_hi(b);
    double bl = b - bh;
    return ((ah * bh - p) + ah * bl + al * bh) + al * bl;
}

// y[0] + y[1] = x * 10^k as a double-double (about 30 significant digits), in
// steps of at most 10^22, each of which is an exact double
void __scale10(double x, long k, double[] y) {
    double hi = x;
    double lo = 0.0;
    long j = 0;
    double p = 0.0;
    while (k > 0) {
        j = k; if (j > 22) { j = 22; }
        p = __pow10(j);
        double ph = hi * p;
        double pe = __prod_err(hi, p, ph) + lo * p;
        hi = ph + pe;
        lo = pe - (hi - ph);
        k -= j;
    }
    while (k < 0) {
        j = -k; if (j > 22) { j = 22; }
        p = __pow10(j);
        double q1 = hi / p;
        double th = q1 * p;
        double q2 = (((hi - th) - __prod_err(q1, p, th)) + lo) / p;
        hi = q1 + q2;
        lo = q2 - (hi - q1);
        k += j;
    }
    y[0] = hi;
    y[1] = lo;
}

// the integer nearest to y[0] + y[1], for 0 <= y < 2^63; an exact half rounds to
// even, as .NET does (2.5 -> 2, 3.5 -> 4)
long __round_dd(double[] y) {
    long m = (long)y[0];
    double fr = y[0] - (double)m;          // exact (0 when y[0] is above 2^53)
    // above 2^53 the low half can exceed 1: take its whole part too
    long li = (long)y[1];
    double lf = y[1] - (double)li;         // exact, in (-1, 1)
    m += li;
    // the fraction is fr + lf; both comparisons below are exact
    if (fr - 0.5 > -lf || (fr - 0.5 == -lf && m % 2 != 0)) { m++; }
    else if (fr + 0.5 < -lf || (fr + 0.5 == -lf && m % 2 != 0)) { m--; }
    return m;
}

// the digits of n >= 0, zero-padded to at least width
string __digits(long n, long width) {
    string s = str(n);
    while (s.length < width) { s = ""0"" + s; }
    return s;
}

string __dtoa(double x) {
    if (x != x) { return ""NaN""; }
    if (x - x != 0.0) { if (x > 0.0) { return ""Infinity""; } return ""-Infinity""; }
    if (x == 0.0) {
        long bits = *(long*)&x;
        if (bits < 0) { return ""-0""; }
        return ""0"";
    }
    string sign = """";
    if (x < 0.0) { sign = ""-""; x = -x; }
    // e = decimal exponent of the first significant digit
    long e = 0;
    double t = x;
    while (t >= 10.0) { t = t / 10.0; e++; }
    while (t < 1.0) { t = t * 10.0; e--; }
    // m = the 15 significant digits as an integer, 10^14 <= m < 10^15
    double[] y = new double[2];
    __scale10(x, 14 - e, y);
    if (y[0] >= 1000000000000000.0) { e++; __scale10(x, 14 - e, y); }
    else if (y[0] < 100000000000000.0) { e--; __scale10(x, 14 - e, y); }
    long m = __round_dd(y);
    if (m >= 1000000000000000) { m = m / 10; e++; }
    string ds = str(m);
    long n = ds.length;
    while (n > 1 && ds[n - 1] == 48) { n--; }
    ds = substr(ds, 0, n);
    if (e >= 15 || e < -5) {
        string mant = substr(ds, 0, 1);
        if (n > 1) { mant = mant + ""."" + substr(ds, 1, n - 1); }
        string es = ""+"";
        long ae = e;
        if (e < 0) { es = ""-""; ae = -e; }
        return sign + mant + ""E"" + es + __digits(ae, 2);
    }
    if (e < 0) {
        string z = ""0."";
        long i = -1;
        while (i > e) { z = z + ""0""; i--; }
        return sign + z + ds;
    }
    if (n <= e + 1) {
        while (n <= e) { ds = ds + ""0""; n++; }
        return sign + ds;
    }
    return sign + substr(ds, 0, e + 1) + ""."" + substr(ds, e + 1, n - e - 1);
}

// x with exactly `places` decimals ($""{x:F2}"")
string __dtoaf(double x, long places) {
    if (x != x) { return ""NaN""; }
    if (x - x != 0.0) { if (x > 0.0) { return ""Infinity""; } return ""-Infinity""; }
    string sign = """";
    if (x < 0.0) { sign = ""-""; x = -x; }
    double[] y = new double[2];
    __scale10(x, places, y);
    if (y[0] >= 9000000000000000000.0) { return sign + __dtoa(x); }
    long m = __round_dd(y);
    string ds = __digits(m, places + 1);
    long n = ds.length;
    if (places == 0) { return sign + ds; }
    return sign + substr(ds, 0, n - places) + ""."" + substr(ds, n - places, places);
}
";

    // Line of the first use of double in the program, or 0 if it uses none.
    public static int FirstUse(ModuleNode m) {
        static bool D(string? t) => t != null && t.Contains("double");
        foreach (var s in m.Structs) if (s.Fields.Any(f => D(f.Type))) return s.Line;
        foreach (var f in m.Funcs) {
            if (D(f.Ret) || f.Params.Any(p => D(p.Type))) return f.Line;
            int line = 0;
            Walker.Walk(f.Body,
                e => { if (line == 0 && (e is FloatLit || e is CastExpr { TargetType: var tt } && D(tt) || e is NewArray { ElemType: var et } && D(et))) line = e.Line; },
                s => { if (line == 0 && s is VarDecl { DeclType: var dt } && D(dt)) line = s.Line; });
            if (line != 0) return line;
        }
        return 0;
    }

    public static void Inject(ModuleNode m) {
        if (m.Funcs.Any(f => f.Name == "__dtoa")) return;
        var rt = new Parser(new Lexer(Prelude).Lex()).ParseModule();
        foreach (var f in rt.Funcs) { f.IsRuntime = true; f.Line = 0; m.Funcs.Add(f); }
    }
}
