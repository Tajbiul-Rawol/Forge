// JSON throughput - same hand-written algorithm as json.fg, idiomatic Go.
package main

import "fmt"

type writer struct {
	b []byte
	p int
}

func (w *writer) ch(c byte)     { w.b[w.p] = c; w.p++ }
func (w *writer) str(s string) { w.p += copy(w.b[w.p:], s) }
func (w *writer) num(v int64) {
	if v < 0 {
		w.ch('-')
		v = -v
	}
	n := 1
	for t := v; t >= 10; t /= 10 {
		n++
	}
	e := w.p + n
	for q := e - 1; q >= w.p; q-- {
		w.b[q] = byte('0' + v%10)
		v /= 10
	}
	w.p = e
}

func serialize(buf []byte, rec []int64, n int) int {
	w := writer{b: buf}
	w.ch('[')
	for i := 0; i < n; i++ {
		r := rec[i*6 : i*6+6]
		if i > 0 {
			w.ch(',')
		}
		w.ch('{')
		w.str(`"id":`)
		w.num(r[0])
		w.str(`,"name":"user`)
		w.num(r[0])
		w.str(`","score":`)
		w.num(r[1])
		if r[2] != 0 {
			w.str(`,"active":true`)
		} else {
			w.str(`,"active":false`)
		}
		w.str(`,"tags":[`)
		w.num(r[3])
		w.ch(',')
		w.num(r[4])
		w.ch(',')
		w.num(r[5])
		w.str(`]}`)
	}
	w.ch(']')
	return w.p
}

type parser struct {
	b []byte
	p int
}

func (ps *parser) ws() {
	for c := ps.b[ps.p]; c == ' ' || c == '\n' || c == '\r' || c == '\t'; c = ps.b[ps.p] {
		ps.p++
	}
}

func (ps *parser) str() (int, int, bool) {
	if ps.b[ps.p] != '"' {
		return 0, 0, false
	}
	ps.p++
	s := ps.p
	for c := ps.b[ps.p]; c != '"'; c = ps.b[ps.p] {
		if c == '\\' {
			ps.p++
		}
		if c == 0 {
			return 0, 0, false
		}
		ps.p++
	}
	l := ps.p - s
	ps.p++
	return s, l, true
}

func (ps *parser) num() (int64, bool) {
	neg := false
	if ps.b[ps.p] == '-' {
		neg = true
		ps.p++
	}
	c := ps.b[ps.p]
	if c < '0' || c > '9' {
		return 0, false
	}
	var v int64
	for ; c >= '0' && c <= '9'; c = ps.b[ps.p] {
		v = v*10 + int64(c-'0')
		ps.p++
	}
	if neg {
		v = -v
	}
	return v, true
}

func (ps *parser) boolean() (int64, bool) {
	b, p := ps.b, ps.p
	if b[p] == 't' && b[p+1] == 'r' && b[p+2] == 'u' && b[p+3] == 'e' {
		ps.p += 4
		return 1, true
	}
	if b[p] == 'f' && b[p+1] == 'a' && b[p+2] == 'l' && b[p+3] == 's' && b[p+4] == 'e' {
		ps.p += 5
		return 0, true
	}
	return 0, false
}

func (ps *parser) record(o []int64) bool {
	ps.ws()
	if ps.b[ps.p] != '{' {
		return false
	}
	ps.p++
	ps.ws()
	if ps.b[ps.p] == '}' {
		ps.p++
		return true
	}
	for {
		koff, klen, ok := ps.str()
		if !ok {
			return false
		}
		ps.ws()
		if ps.b[ps.p] != ':' {
			return false
		}
		ps.p++
		ps.ws()
		switch string(ps.b[koff : koff+klen]) { // compiler does not allocate for this
		case "id":
			o[0], ok = ps.num()
		case "name":
			var off, l int
			off, l, ok = ps.str()
			o[6], o[7] = int64(off), int64(l)
		case "score":
			o[1], ok = ps.num()
		case "active":
			o[2], ok = ps.boolean()
		case "tags":
			if ps.b[ps.p] != '[' {
				return false
			}
			ps.p++
			ps.ws()
			k := 0
			for ps.b[ps.p] != ']' {
				v, ok2 := ps.num()
				if !ok2 {
					return false
				}
				if k < 3 {
					o[3+k] = v
				}
				k++
				ps.ws()
				if ps.b[ps.p] == ',' {
					ps.p++
					ps.ws()
				}
			}
			ps.p++
		default:
			return false
		}
		if !ok {
			return false
		}
		ps.ws()
		switch ps.b[ps.p] {
		case ',':
			ps.p++
			ps.ws()
		case '}':
			ps.p++
			return true
		default:
			return false
		}
	}
}

func (ps *parser) all(out []int64) int {
	ps.ws()
	if ps.b[ps.p] != '[' {
		return -1
	}
	ps.p++
	ps.ws()
	count := 0
	for ps.b[ps.p] != ']' {
		if !ps.record(out[count*8 : count*8+8]) {
			return -1
		}
		count++
		ps.ws()
		if ps.b[ps.p] == ',' {
			ps.p++
			ps.ws()
		}
	}
	return count
}

func main() {
	const n, iters = 20000, 100
	rec := make([]int64, n*6)
	for i := 0; i < n; i++ {
		ii := int64(i)
		r := rec[i*6 : i*6+6]
		r[0] = ii
		r[1] = (ii*7919)%200001 - 100000
		if i%3 == 0 {
			r[2] = 1
		}
		r[3], r[4], r[5] = ii%10, ii%1000, ii*31%100000
	}
	buf := make([]byte, 4194304)
	out := make([]int64, n*8)
	var bytes, check int64
	for it := 0; it < iters; it++ {
		rec[0] = int64(it)
		l := serialize(buf, rec, n)
		buf[l] = 0
		ps := parser{b: buf}
		count := ps.all(out)
		if count != n {
			fmt.Println(-1)
			return
		}
		bytes += int64(l)
		for i := 0; i < count; i++ {
			o := out[i*8 : i*8+8]
			check += o[0] + o[1]*3 + o[2]*5 + o[3] + o[4] + o[5] + o[7] + int64(buf[o[6]+4])
		}
	}
	fmt.Printf("%d\n%d\n", bytes, check)
}
