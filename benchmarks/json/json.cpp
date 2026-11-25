// JSON throughput - same hand-written algorithm as json.fg, written as idiomatic C++
// (std::vector, std::string_view, references instead of out-pointers).
#include <cstdint>
#include <cstdio>
#include <string_view>
#include <vector>
using i64 = std::int64_t;
using u8 = unsigned char;

struct Writer {
    u8* b; i64 p = 0;
    void ch(char c) { b[p++] = (u8)c; }
    void str(std::string_view s) { for (char c : s) b[p++] = (u8)c; }
    void num(i64 v) {
        if (v < 0) { ch('-'); v = -v; }
        i64 n = 1;
        for (i64 t = v; t >= 10; t /= 10) n++;
        i64 e = p + n;
        for (i64 q = e - 1; q >= p; q--) { b[q] = (u8)('0' + v % 10); v /= 10; }
        p = e;
    }
};

static i64 serialize(u8* buf, const std::vector<i64>& rec, i64 n) {
    Writer w{buf};
    w.ch('[');
    for (i64 i = 0; i < n; i++) {
        const i64* r = &rec[i * 6];
        if (i > 0) w.ch(',');
        w.ch('{');
        w.str("\"id\":");          w.num(r[0]);
        w.str(",\"name\":\"user"); w.num(r[0]);
        w.str("\",\"score\":");    w.num(r[1]);
        w.str(r[2] ? ",\"active\":true" : ",\"active\":false");
        w.str(",\"tags\":[");
        w.num(r[3]); w.ch(',');
        w.num(r[4]); w.ch(',');
        w.num(r[5]);
        w.str("]}");
    }
    w.ch(']');
    return w.p;
}

struct Parser {
    const u8* b; i64 p = 0;
    void ws() { while (b[p] == ' ' || b[p] == '\n' || b[p] == '\r' || b[p] == '\t') p++; }
    bool str(i64& off, i64& len) {
        if (b[p] != '"') return false;
        i64 s = ++p;
        for (u8 c = b[p]; c != '"'; c = b[p]) { if (c == '\\') p++; if (c == 0) return false; p++; }
        off = s; len = p - s; p++;
        return true;
    }
    bool num(i64& out) {
        bool neg = false;
        if (b[p] == '-') { neg = true; p++; }
        if (b[p] < '0' || b[p] > '9') return false;
        i64 v = 0;
        while (b[p] >= '0' && b[p] <= '9') { v = v * 10 + (b[p] - '0'); p++; }
        out = neg ? -v : v;
        return true;
    }
    bool boolean(i64& out) {
        if (b[p] == 't' && b[p+1] == 'r' && b[p+2] == 'u' && b[p+3] == 'e') { out = 1; p += 4; return true; }
        if (b[p] == 'f' && b[p+1] == 'a' && b[p+2] == 'l' && b[p+3] == 's' && b[p+4] == 'e') { out = 0; p += 5; return true; }
        return false;
    }
    bool key_is(i64 off, i64 len, std::string_view s) const {
        if ((i64)s.size() != len) return false;
        for (i64 i = 0; i < len; i++) if (b[off + i] != (u8)s[i]) return false;
        return true;
    }
    bool record(i64* o) {
        ws();
        if (b[p] != '{') return false;
        p++; ws();
        if (b[p] == '}') { p++; return true; }
        for (;;) {
            i64 koff, klen;
            if (!str(koff, klen)) return false;
            ws();
            if (b[p] != ':') return false;
            p++; ws();
            bool ok;
            if (key_is(koff, klen, "id"))          ok = num(o[0]);
            else if (key_is(koff, klen, "name"))   ok = str(o[6], o[7]);
            else if (key_is(koff, klen, "score"))  ok = num(o[1]);
            else if (key_is(koff, klen, "active")) ok = boolean(o[2]);
            else if (key_is(koff, klen, "tags")) {
                if (b[p] != '[') return false;
                p++; ws();
                i64 k = 0;
                while (b[p] != ']') {
                    i64 v;
                    if (!num(v)) return false;
                    if (k < 3) o[3 + k] = v;
                    k++;
                    ws();
                    if (b[p] == ',') { p++; ws(); }
                }
                p++; ok = true;
            } else return false;
            if (!ok) return false;
            ws();
            if (b[p] == ',') { p++; ws(); }
            else if (b[p] == '}') { p++; return true; }
            else return false;
        }
    }
    i64 all(std::vector<i64>& out) {
        ws();
        if (b[p] != '[') return -1;
        p++; ws();
        i64 count = 0;
        while (b[p] != ']') {
            if (!record(&out[count * 8])) return -1;
            count++;
            ws();
            if (b[p] == ',') { p++; ws(); }
        }
        return count;
    }
};

int main() {
    const i64 n = 20000, iters = 100;
    std::vector<i64> rec(n * 6), out(n * 8);
    for (i64 i = 0; i < n; i++) {
        i64* r = &rec[i * 6];
        r[0] = i; r[1] = (i * 7919) % 200001 - 100000; r[2] = i % 3 == 0;
        r[3] = i % 10; r[4] = i % 1000; r[5] = i * 31 % 100000;
    }
    std::vector<u8> buf(4194304);
    i64 bytes = 0, check = 0;
    for (i64 it = 0; it < iters; it++) {
        rec[0] = it;
        i64 len = serialize(buf.data(), rec, n);
        buf[len] = 0;
        i64 count = Parser{buf.data()}.all(out);
        if (count != n) { std::puts("-1"); return 1; }
        bytes += len;
        for (i64 i = 0; i < count; i++) {
            const i64* o = &out[i * 8];
            check += o[0] + o[1] * 3 + o[2] * 5 + o[3] + o[4] + o[5] + o[7] + buf[o[6] + 4];
        }
    }
    std::printf("%lld\n%lld\n", (long long)bytes, (long long)check);
}
