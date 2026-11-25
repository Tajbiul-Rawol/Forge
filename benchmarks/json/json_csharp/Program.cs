// JSON throughput - same hand-written algorithm as json.fg, idiomatic C#
// (Span<byte>, ref struct parser, u8 literals). No System.Text.Json.
using System;

const int n = 20000, iters = 100;
var rec = new long[n * 6];
for (int i = 0; i < n; i++) {
    long ii = i; int r = i * 6;
    rec[r] = ii; rec[r + 1] = ii * 7919 % 200001 - 100000; rec[r + 2] = i % 3 == 0 ? 1 : 0;
    rec[r + 3] = ii % 10; rec[r + 4] = ii % 1000; rec[r + 5] = ii * 31 % 100000;
}
var buf = new byte[4194304];
var outv = new long[n * 8];
long bytes = 0, check = 0;
for (int it = 0; it < iters; it++) {
    rec[0] = it;
    int len = Json.Serialize(buf, rec, n);
    buf[len] = 0;
    var ps = new Parser(buf);
    int count = ps.All(outv);
    if (count != n) { Console.WriteLine(-1); return; }
    bytes += len;
    for (int i = 0; i < count; i++) {
        int o = i * 8;
        check += outv[o] + outv[o + 1] * 3 + outv[o + 2] * 5 + outv[o + 3] + outv[o + 4] + outv[o + 5]
               + outv[o + 7] + buf[outv[o + 6] + 4];
    }
}
Console.WriteLine(bytes);
Console.WriteLine(check);

static class Json {
    public static int Serialize(byte[] buf, long[] rec, int n) {
        var w = new Writer(buf);
        w.Ch((byte)'[');
        for (int i = 0; i < n; i++) {
            int r = i * 6;
            if (i > 0) w.Ch((byte)',');
            w.Ch((byte)'{');
            w.Str("\"id\":"u8);          w.Num(rec[r]);
            w.Str(",\"name\":\"user"u8); w.Num(rec[r]);
            w.Str("\",\"score\":"u8);    w.Num(rec[r + 1]);
            w.Str(rec[r + 2] != 0 ? ",\"active\":true"u8 : ",\"active\":false"u8);
            w.Str(",\"tags\":["u8);
            w.Num(rec[r + 3]); w.Ch((byte)',');
            w.Num(rec[r + 4]); w.Ch((byte)',');
            w.Num(rec[r + 5]);
            w.Str("]}"u8);
        }
        w.Ch((byte)']');
        return w.P;
    }
}

ref struct Writer {
    readonly Span<byte> b; public int P;
    public Writer(Span<byte> b) { this.b = b; P = 0; }
    public void Ch(byte c) { b[P++] = c; }
    public void Str(ReadOnlySpan<byte> s) { s.CopyTo(b.Slice(P)); P += s.Length; }
    public void Num(long v) {
        if (v < 0) { Ch((byte)'-'); v = -v; }
        int n = 1;
        for (long t = v; t >= 10; t /= 10) n++;
        int e = P + n;
        for (int q = e - 1; q >= P; q--) { b[q] = (byte)('0' + v % 10); v /= 10; }
        P = e;
    }
}

ref struct Parser {
    readonly ReadOnlySpan<byte> b; int p;
    public Parser(ReadOnlySpan<byte> b) { this.b = b; p = 0; }
    void Ws() { while (b[p] == ' ' || b[p] == '\n' || b[p] == '\r' || b[p] == '\t') p++; }
    bool Str(out int off, out int len) {
        off = len = 0;
        if (b[p] != '"') return false;
        int s = ++p;
        for (byte c = b[p]; c != '"'; c = b[p]) { if (c == '\\') p++; if (c == 0) return false; p++; }
        off = s; len = p - s; p++;
        return true;
    }
    bool Num(out long v) {
        v = 0;
        bool neg = false;
        if (b[p] == '-') { neg = true; p++; }
        if (b[p] < '0' || b[p] > '9') return false;
        while (b[p] >= '0' && b[p] <= '9') { v = v * 10 + (b[p] - '0'); p++; }
        if (neg) v = -v;
        return true;
    }
    bool Bool(out long v) {
        var rest = b.Slice(p);
        if (rest.StartsWith("true"u8)) { v = 1; p += 4; return true; }
        if (rest.StartsWith("false"u8)) { v = 0; p += 5; return true; }
        v = 0; return false;
    }
    bool Record(Span<long> o) {
        Ws();
        if (b[p] != '{') return false;
        p++; Ws();
        if (b[p] == '}') { p++; return true; }
        while (true) {
            if (!Str(out int koff, out int klen)) return false;
            Ws();
            if (b[p] != ':') return false;
            p++; Ws();
            var key = b.Slice(koff, klen);
            bool ok;
            if (key.SequenceEqual("id"u8)) ok = Num(out o[0]);
            else if (key.SequenceEqual("name"u8)) { ok = Str(out int off, out int len); o[6] = off; o[7] = len; }
            else if (key.SequenceEqual("score"u8)) ok = Num(out o[1]);
            else if (key.SequenceEqual("active"u8)) ok = Bool(out o[2]);
            else if (key.SequenceEqual("tags"u8)) {
                if (b[p] != '[') return false;
                p++; Ws();
                int k = 0;
                while (b[p] != ']') {
                    if (!Num(out long v)) return false;
                    if (k < 3) o[3 + k] = v;
                    k++;
                    Ws();
                    if (b[p] == ',') { p++; Ws(); }
                }
                p++; ok = true;
            } else return false;
            if (!ok) return false;
            Ws();
            if (b[p] == ',') { p++; Ws(); }
            else if (b[p] == '}') { p++; return true; }
            else return false;
        }
    }
    public int All(long[] outv) {
        Ws();
        if (b[p] != '[') return -1;
        p++; Ws();
        int count = 0;
        while (b[p] != ']') {
            if (!Record(outv.AsSpan(count * 8, 8))) return -1;
            count++;
            Ws();
            if (b[p] == ',') { p++; Ws(); }
        }
        return count;
    }
}
