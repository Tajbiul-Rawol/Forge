// JSON throughput - same hand-written algorithm as json.fg, idiomatic safe Rust
// (bounds-checked slices, a parser struct, Option for errors).

struct Writer<'a> { b: &'a mut [u8], p: usize }
impl<'a> Writer<'a> {
    #[inline] fn ch(&mut self, c: u8) { self.b[self.p] = c; self.p += 1; }
    #[inline] fn str(&mut self, s: &[u8]) { self.b[self.p..self.p + s.len()].copy_from_slice(s); self.p += s.len(); }
    #[inline] fn num(&mut self, mut v: i64) {
        if v < 0 { self.ch(b'-'); v = -v; }
        let mut n = 1; let mut t = v;
        while t >= 10 { t /= 10; n += 1; }
        let e = self.p + n;
        let mut q = e;
        while q > self.p { q -= 1; self.b[q] = b'0' + (v % 10) as u8; v /= 10; }
        self.p = e;
    }
}

fn serialize(buf: &mut [u8], rec: &[i64], n: usize) -> usize {
    let mut w = Writer { b: buf, p: 0 };
    w.ch(b'[');
    for i in 0..n {
        let r = &rec[i * 6..i * 6 + 6];
        if i > 0 { w.ch(b','); }
        w.ch(b'{');
        w.str(b"\"id\":"); w.num(r[0]);
        w.str(b",\"name\":\"user"); w.num(r[0]);
        w.str(b"\",\"score\":"); w.num(r[1]);
        w.str(if r[2] != 0 { b",\"active\":true" } else { b",\"active\":false" });
        w.str(b",\"tags\":[");
        w.num(r[3]); w.ch(b',');
        w.num(r[4]); w.ch(b',');
        w.num(r[5]);
        w.str(b"]}");
    }
    w.ch(b']');
    w.p
}

struct Parser<'a> { b: &'a [u8], p: usize }
impl<'a> Parser<'a> {
    #[inline] fn cur(&self) -> u8 { self.b[self.p] }
    #[inline] fn ws(&mut self) { while matches!(self.cur(), b' ' | b'\n' | b'\r' | b'\t') { self.p += 1; } }
    fn str(&mut self) -> Option<(usize, usize)> {
        if self.cur() != b'"' { return None; }
        self.p += 1;
        let s = self.p;
        loop {
            let c = self.cur();
            if c == b'"' { break; }
            if c == b'\\' { self.p += 1; }
            if c == 0 { return None; }
            self.p += 1;
        }
        let r = (s, self.p - s);
        self.p += 1;
        Some(r)
    }
    fn num(&mut self) -> Option<i64> {
        let neg = self.cur() == b'-';
        if neg { self.p += 1; }
        if !self.cur().is_ascii_digit() { return None; }
        let mut v: i64 = 0;
        while self.cur().is_ascii_digit() { v = v * 10 + (self.cur() - b'0') as i64; self.p += 1; }
        Some(if neg { -v } else { v })
    }
    fn boolean(&mut self) -> Option<i64> {
        let rest = &self.b[self.p..];
        if rest.starts_with(b"true") { self.p += 4; Some(1) }
        else if rest.starts_with(b"false") { self.p += 5; Some(0) }
        else { None }
    }
    fn record(&mut self, o: &mut [i64]) -> Option<()> {
        self.ws();
        if self.cur() != b'{' { return None; }
        self.p += 1; self.ws();
        if self.cur() == b'}' { self.p += 1; return Some(()); }
        loop {
            let (koff, klen) = self.str()?;
            self.ws();
            if self.cur() != b':' { return None; }
            self.p += 1; self.ws();
            match &self.b[koff..koff + klen] {
                b"id" => o[0] = self.num()?,
                b"name" => { let (off, len) = self.str()?; o[6] = off as i64; o[7] = len as i64; }
                b"score" => o[1] = self.num()?,
                b"active" => o[2] = self.boolean()?,
                b"tags" => {
                    if self.cur() != b'[' { return None; }
                    self.p += 1; self.ws();
                    let mut k = 0;
                    while self.cur() != b']' {
                        let v = self.num()?;
                        if k < 3 { o[3 + k] = v; }
                        k += 1;
                        self.ws();
                        if self.cur() == b',' { self.p += 1; self.ws(); }
                    }
                    self.p += 1;
                }
                _ => return None,
            }
            self.ws();
            match self.cur() {
                b',' => { self.p += 1; self.ws(); }
                b'}' => { self.p += 1; return Some(()); }
                _ => return None,
            }
        }
    }
    fn all(&mut self, out: &mut [i64]) -> Option<usize> {
        self.ws();
        if self.cur() != b'[' { return None; }
        self.p += 1; self.ws();
        let mut count = 0;
        while self.cur() != b']' {
            self.record(&mut out[count * 8..count * 8 + 8])?;
            count += 1;
            self.ws();
            if self.cur() == b',' { self.p += 1; self.ws(); }
        }
        Some(count)
    }
}

fn main() {
    let n: usize = 20000; let iters = 100;
    let mut rec = vec![0i64; n * 6];
    for i in 0..n {
        let ii = i as i64;
        let r = &mut rec[i * 6..i * 6 + 6];
        r[0] = ii; r[1] = (ii * 7919) % 200001 - 100000; r[2] = (i % 3 == 0) as i64;
        r[3] = ii % 10; r[4] = ii % 1000; r[5] = ii * 31 % 100000;
    }
    let mut buf = vec![0u8; 4194304];
    let mut out = vec![0i64; n * 8];
    let (mut bytes, mut check) = (0i64, 0i64);
    for it in 0..iters {
        rec[0] = it;
        let len = serialize(&mut buf, &rec, n);
        buf[len] = 0;
        let count = Parser { b: &buf, p: 0 }.all(&mut out);
        if count != Some(n) { println!("-1"); std::process::exit(1); }
        bytes += len as i64;
        for o in out.chunks_exact(8) {
            check += o[0] + o[1] * 3 + o[2] * 5 + o[3] + o[4] + o[5] + o[7] + buf[o[6] as usize + 4] as i64;
        }
    }
    println!("{}\n{}", bytes, check);
}
