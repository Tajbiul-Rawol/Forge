// JSON throughput - same hand-written algorithm as json.fg (see that file for the format).
#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
typedef int64_t i64;
typedef unsigned char u8;

static i64 put_str(u8* b, i64 p, const char* s) { while (*s) b[p++] = (u8)*s++; return p; }

static i64 put_int(u8* b, i64 p, i64 v) {
    if (v < 0) { b[p++] = '-'; v = -v; }
    i64 n = 1, t = v;
    while (t >= 10) { t /= 10; n++; }
    i64 e = p + n;
    for (i64 q = e - 1; q >= p; q--) { b[q] = (u8)('0' + v % 10); v /= 10; }
    return e;
}

static i64 serialize(u8* b, const i64* rec, i64 n) {
    i64 p = 0;
    b[p++] = '[';
    for (i64 i = 0; i < n; i++) {
        const i64* r = rec + i * 6;
        if (i > 0) b[p++] = ',';
        b[p++] = '{';
        p = put_str(b, p, "\"id\":");          p = put_int(b, p, r[0]);
        p = put_str(b, p, ",\"name\":\"user"); p = put_int(b, p, r[0]);
        p = put_str(b, p, "\",\"score\":");    p = put_int(b, p, r[1]);
        p = put_str(b, p, r[2] ? ",\"active\":true" : ",\"active\":false");
        p = put_str(b, p, ",\"tags\":[");
        p = put_int(b, p, r[3]); b[p++] = ',';
        p = put_int(b, p, r[4]); b[p++] = ',';
        p = put_int(b, p, r[5]);
        p = put_str(b, p, "]}");
    }
    b[p++] = ']';
    return p;
}

static i64 ws(const u8* b, i64 p) { while (b[p] == ' ' || b[p] == '\n' || b[p] == '\r' || b[p] == '\t') p++; return p; }

static i64 read_str(const u8* b, i64 p, i64* off, i64* len) {
    if (b[p] != '"') return -1;
    i64 s = ++p;
    for (u8 c = b[p]; c != '"'; c = b[p]) {
        if (c == '\\') p++;
        if (c == 0) return -1;
        p++;
    }
    *off = s; *len = p - s;
    return p + 1;
}

static i64 read_int(const u8* b, i64 p, i64* out) {
    int neg = 0;
    if (b[p] == '-') { neg = 1; p++; }
    if (b[p] < '0' || b[p] > '9') return -1;
    i64 v = 0;
    while (b[p] >= '0' && b[p] <= '9') { v = v * 10 + (b[p] - '0'); p++; }
    *out = neg ? -v : v;
    return p;
}

static i64 read_bool(const u8* b, i64 p, i64* out) {
    if (b[p] == 't' && b[p+1] == 'r' && b[p+2] == 'u' && b[p+3] == 'e') { *out = 1; return p + 4; }
    if (b[p] == 'f' && b[p+1] == 'a' && b[p+2] == 'l' && b[p+3] == 's' && b[p+4] == 'e') { *out = 0; return p + 5; }
    return -1;
}

static int key_is(const u8* b, i64 off, i64 len, const char* s) {
    for (i64 i = 0; i < len; i++) if (b[off + i] != (u8)s[i]) return 0;
    return s[len] == 0;
}

static i64 parse_record(const u8* b, i64 p, i64* o) {
    p = ws(b, p);
    if (b[p] != '{') return -1;
    p = ws(b, p + 1);
    if (b[p] == '}') return p + 1;
    for (;;) {
        i64 koff, klen;
        p = read_str(b, p, &koff, &klen);
        if (p < 0) return -1;
        p = ws(b, p);
        if (b[p] != ':') return -1;
        p = ws(b, p + 1);
        if (key_is(b, koff, klen, "id"))          p = read_int(b, p, &o[0]);
        else if (key_is(b, koff, klen, "name"))   p = read_str(b, p, &o[6], &o[7]);
        else if (key_is(b, koff, klen, "score"))  p = read_int(b, p, &o[1]);
        else if (key_is(b, koff, klen, "active")) p = read_bool(b, p, &o[2]);
        else if (key_is(b, koff, klen, "tags")) {
            if (b[p] != '[') return -1;
            p = ws(b, p + 1);
            i64 k = 0;
            while (b[p] != ']') {
                i64 v;
                p = read_int(b, p, &v);
                if (p < 0) return -1;
                if (k < 3) o[3 + k] = v;
                k++;
                p = ws(b, p);
                if (b[p] == ',') p = ws(b, p + 1);
            }
            p++;
        } else return -1;
        if (p < 0) return -1;
        p = ws(b, p);
        if (b[p] == ',') p = ws(b, p + 1);
        else if (b[p] == '}') return p + 1;
        else return -1;
    }
}

static i64 parse(const u8* b, i64* out) {
    i64 p = ws(b, 0);
    if (b[p] != '[') return -1;
    p = ws(b, p + 1);
    i64 count = 0;
    while (b[p] != ']') {
        p = parse_record(b, p, out + count * 8);
        if (p < 0) return -1;
        count++;
        p = ws(b, p);
        if (b[p] == ',') p = ws(b, p + 1);
    }
    return count;
}

static i64 checksum(const u8* b, const i64* out, i64 count) {
    i64 s = 0;
    for (i64 i = 0; i < count; i++) {
        const i64* o = out + i * 8;
        s += o[0] + o[1] * 3 + o[2] * 5 + o[3] + o[4] + o[5];
        s += o[7] + b[o[6] + 4];
    }
    return s;
}

int main(void) {
    const i64 n = 20000, iters = 100;
    i64* rec = malloc(sizeof(i64) * n * 6);
    for (i64 i = 0; i < n; i++) {
        i64* r = rec + i * 6;
        r[0] = i; r[1] = (i * 7919) % 200001 - 100000; r[2] = i % 3 == 0;
        r[3] = i % 10; r[4] = i % 1000; r[5] = i * 31 % 100000;
    }
    u8* buf = malloc(4194304);
    i64* out = calloc(n * 8, sizeof(i64));
    i64 bytes = 0, check = 0;
    for (i64 it = 0; it < iters; it++) {
        rec[0] = it;
        i64 len = serialize(buf, rec, n);
        buf[len] = 0;
        i64 count = parse(buf, out);
        if (count != n) { puts("-1"); return 1; }
        bytes += len;
        check += checksum(buf, out, count);
    }
    printf("%lld\n%lld\n", (long long)bytes, (long long)check);
    return 0;
}
