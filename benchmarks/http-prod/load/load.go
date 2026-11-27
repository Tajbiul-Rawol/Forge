// HTTP load generator and conformance check for the http-prod servers.
//
//	load bench <host:port> <seconds> <connections> keepalive|close|pipeline [depth]
//	load check <host:port>
//
// bench: every connection sends GET / and reads the full response, then repeats.
// keepalive reuses each connection; close sends "Connection: close" and opens a new
// connection per request. pipeline writes <depth> requests at once (default 16) and
// then reads all <depth> responses, as TechEmpower's plaintext test does. Every response is checked (status 200, body
// "Hello, World!"). Prints one line: "<req/s> req/s p50 <us> p99 <us> errors <n>".
//
// check: the HTTP behaviour every server must have; prints PASS/FAIL lines and exits
// non-zero on any failure.
package main

import (
	"bufio"
	"bytes"
	"fmt"
	"io"
	"net"
	"os"
	"sort"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"time"
)

const want = "Hello, World!"

type resp struct {
	status int
	header map[string]string
	body   string
}

// readResp reads one HTTP/1.1 response (Content-Length bodies only, which is all the
// servers produce for this endpoint).
func readResp(r *bufio.Reader, head bool) (resp, error) {
	var out resp
	line, err := r.ReadString('\n')
	if err != nil {
		return out, err
	}
	f := strings.SplitN(strings.TrimRight(line, "\r\n"), " ", 3)
	if len(f) < 2 || !strings.HasPrefix(f[0], "HTTP/1.") {
		return out, fmt.Errorf("bad status line %q", line)
	}
	out.status, _ = strconv.Atoi(f[1])
	out.header = map[string]string{}
	for {
		l, err := r.ReadString('\n')
		if err != nil {
			return out, err
		}
		l = strings.TrimRight(l, "\r\n")
		if l == "" {
			break
		}
		if i := strings.IndexByte(l, ':'); i > 0 {
			out.header[strings.ToLower(l[:i])] = strings.TrimSpace(l[i+1:])
		}
	}
	if head {
		return out, nil
	}
	if te := out.header["transfer-encoding"]; strings.Contains(strings.ToLower(te), "chunked") {
		var body bytes.Buffer
		for {
			l, err := r.ReadString('\n')
			if err != nil {
				return out, err
			}
			n, err := strconv.ParseInt(strings.TrimSpace(strings.SplitN(l, ";", 2)[0]), 16, 64)
			if err != nil {
				return out, err
			}
			if n == 0 {
				r.ReadString('\n')
				break
			}
			if _, err := io.CopyN(&body, r, n); err != nil {
				return out, err
			}
			r.ReadString('\n')
		}
		out.body = body.String()
		return out, nil
	}
	cl, ok := out.header["content-length"]
	if !ok { // no length: the body runs to the end of the connection (valid HTTP/1.1)
		b, err := io.ReadAll(r)
		out.body = string(b)
		return out, err
	}
	n, err := strconv.Atoi(cl)
	if err != nil {
		return out, fmt.Errorf("bad Content-Length %q", cl)
	}
	b := make([]byte, n)
	if _, err := io.ReadFull(r, b); err != nil {
		return out, err
	}
	out.body = string(b)
	return out, nil
}

func bench(addr string, secs float64, conns int, keepalive bool, depth int) {
	reqKA := []byte("GET / HTTP/1.1\r\nHost: bench\r\n\r\n")
	reqClose := []byte("GET / HTTP/1.1\r\nHost: bench\r\nConnection: close\r\n\r\n")
	reqPipe := bytes.Repeat(reqKA, depth)
	var errs int64
	lat := make([][]int64, conns)
	deadline := time.Now().Add(time.Duration(secs * float64(time.Second)))
	var wg sync.WaitGroup
	start := time.Now()
	for w := 0; w < conns; w++ {
		wg.Add(1)
		go func(w int) {
			defer wg.Done()
			mine := make([]int64, 0, 1<<16)
			var c net.Conn
			var r *bufio.Reader
			for time.Now().Before(deadline) {
				t0 := time.Now()
				if c == nil {
					var err error
					c, err = net.Dial("tcp", addr)
					if err != nil {
						atomic.AddInt64(&errs, 1)
						time.Sleep(time.Millisecond)
						continue
					}
					c.(*net.TCPConn).SetNoDelay(true)
					r = bufio.NewReaderSize(c, 4096)
				}
				req := reqKA
				if !keepalive {
					req = reqClose
				} else if depth > 1 {
					req = reqPipe
				}
				_, err := c.Write(req)
				var p resp
				okAll := true
				for k := 0; err == nil && k < depth; k++ {
					if p, err = readResp(r, false); err == nil && (p.status != 200 || p.body != want) {
						okAll = false
					}
				}
				if err != nil || !okAll {
					atomic.AddInt64(&errs, 1)
					c.Close()
					c = nil
					continue
				}
				us := int64(time.Since(t0) / time.Microsecond)
				for k := 0; k < depth; k++ {
					mine = append(mine, us)
				}
				if !keepalive {
					c.Close()
					c = nil
				}
			}
			if c != nil {
				c.Close()
			}
			lat[w] = mine
		}(w)
	}
	wg.Wait()
	el := time.Since(start).Seconds()
	var all []int64
	for _, l := range lat {
		all = append(all, l...)
	}
	sort.Slice(all, func(i, j int) bool { return all[i] < all[j] })
	pct := func(p float64) int64 {
		if len(all) == 0 {
			return 0
		}
		return all[int(float64(len(all)-1)*p)]
	}
	fmt.Printf("%.0f req/s p50 %d p99 %d errors %d\n", float64(len(all))/el, pct(0.50), pct(0.99), errs)
}

// ---- conformance ----

var failed bool

func report(name string, err error) {
	if err != nil {
		failed = true
		fmt.Printf("FAIL  %s: %v\n", name, err)
	} else {
		fmt.Printf("PASS  %s\n", name)
	}
}

// advise is for SHOULD-level behaviour (RFC 9112 lets a server simply close on a
// bad request): a miss prints WARN and does not fail the check.
func advise(name string, err error) {
	if err != nil {
		fmt.Printf("WARN  %s: %v\n", name, err)
	} else {
		fmt.Printf("PASS  %s\n", name)
	}
}

func dial(addr string) (net.Conn, *bufio.Reader, error) {
	c, err := net.DialTimeout("tcp", addr, 3*time.Second)
	if err != nil {
		return nil, nil, err
	}
	c.SetDeadline(time.Now().Add(5 * time.Second))
	return c, bufio.NewReader(c), nil
}

// one sends raw bytes on a new connection and reads one response.
func one(addr, raw string, head bool) (resp, net.Conn, *bufio.Reader, error) {
	c, r, err := dial(addr)
	if err != nil {
		return resp{}, nil, nil, err
	}
	if _, err := c.Write([]byte(raw)); err != nil {
		c.Close()
		return resp{}, nil, nil, err
	}
	p, err := readResp(r, head)
	return p, c, r, err
}

func expectStatus(addr, name, raw string, status int, rep func(string, error)) {
	p, c, _, err := one(addr, raw, false)
	if c != nil {
		c.Close()
	}
	if err == nil && p.status != status {
		err = fmt.Errorf("status %d, want %d", p.status, status)
	}
	rep(name, err)
}

// closed reports whether the server closes the connection (EOF) within 3 seconds.
func closed(c net.Conn, r *bufio.Reader) bool {
	c.SetReadDeadline(time.Now().Add(3 * time.Second))
	_, err := r.ReadByte()
	return err == io.EOF || (err != nil && !isTimeout(err))
}

func isTimeout(err error) bool {
	ne, ok := err.(net.Error)
	return ok && ne.Timeout()
}

func check(addr string) {
	// 200 with the right body and headers
	{
		p, c, _, err := one(addr, "GET / HTTP/1.1\r\nHost: t\r\n\r\n", false)
		if c != nil {
			c.Close()
		}
		if err == nil && (p.status != 200 || p.body != want) {
			err = fmt.Errorf("got %d %q", p.status, p.body)
		}
		if err == nil && p.header["date"] == "" {
			err = fmt.Errorf("no Date header")
		}
		if err == nil && !strings.HasPrefix(p.header["content-type"], "text/plain") {
			err = fmt.Errorf("Content-Type %q", p.header["content-type"])
		}
		report("GET / -> 200 Hello, World! (Date, Content-Type)", err)
	}
	// keep-alive: 3 sequential requests on one connection
	{
		c, r, err := dial(addr)
		for i := 0; err == nil && i < 3; i++ {
			if _, err = c.Write([]byte("GET / HTTP/1.1\r\nHost: t\r\n\r\n")); err == nil {
				var p resp
				if p, err = readResp(r, false); err == nil && p.body != want {
					err = fmt.Errorf("request %d: body %q", i+1, p.body)
				}
			}
		}
		if c != nil {
			c.Close()
		}
		report("keep-alive: 3 requests on one connection", err)
	}
	// pipelining: 3 requests in one write, 3 responses in order
	{
		c, r, err := dial(addr)
		if err == nil {
			_, err = c.Write([]byte("GET / HTTP/1.1\r\nHost: t\r\n\r\nGET /missing HTTP/1.1\r\nHost: t\r\n\r\nGET / HTTP/1.1\r\nHost: t\r\n\r\n"))
		}
		wantSt := []int{200, 404, 200}
		for i := 0; err == nil && i < 3; i++ {
			var p resp
			if p, err = readResp(r, false); err == nil && p.status != wantSt[i] {
				err = fmt.Errorf("response %d: status %d, want %d", i+1, p.status, wantSt[i])
			}
		}
		if c != nil {
			c.Close()
		}
		report("pipelining: 3 requests in one packet answered in order", err)
	}
	// a request split across several writes
	{
		c, r, err := dial(addr)
		for _, part := range []string{"GE", "T / HTTP/1.1\r\nHo", "st: t\r\n", "\r\n"} {
			if err == nil {
				_, err = c.Write([]byte(part))
				time.Sleep(20 * time.Millisecond)
			}
		}
		if err == nil {
			var p resp
			if p, err = readResp(r, false); err == nil && p.body != want {
				err = fmt.Errorf("body %q", p.body)
			}
		}
		if c != nil {
			c.Close()
		}
		report("request split across 4 writes", err)
	}
	// a request body is read and skipped, and the next request still works
	{
		c, r, err := dial(addr)
		if err == nil {
			_, err = c.Write([]byte("POST /missing HTTP/1.1\r\nHost: t\r\nContent-Length: 11\r\n\r\nhello worldGET / HTTP/1.1\r\nHost: t\r\n\r\n"))
		}
		wantSt := []int{404, 200}
		for i := 0; err == nil && i < 2; i++ {
			var p resp
			if p, err = readResp(r, false); err == nil && p.status != wantSt[i] {
				err = fmt.Errorf("response %d: status %d, want %d", i+1, p.status, wantSt[i])
			}
		}
		if c != nil {
			c.Close()
		}
		report("Content-Length body skipped, next request answered", err)
	}
	// Connection: close is honoured
	{
		p, c, r, err := one(addr, "GET / HTTP/1.1\r\nHost: t\r\nConnection: close\r\n\r\n", false)
		if err == nil && p.status != 200 {
			err = fmt.Errorf("status %d", p.status)
		}
		if err == nil && !closed(c, r) {
			err = fmt.Errorf("connection still open")
		}
		if c != nil {
			c.Close()
		}
		report("Connection: close closes after the response", err)
	}
	// HTTP/1.0 without keep-alive closes
	{
		p, c, r, err := one(addr, "GET / HTTP/1.0\r\n\r\n", false)
		if err == nil && (p.status != 200 || p.body != want) {
			err = fmt.Errorf("got %d %q", p.status, p.body)
		}
		if err == nil && !closed(c, r) {
			err = fmt.Errorf("connection still open")
		}
		if c != nil {
			c.Close()
		}
		report("HTTP/1.0 request answered and closed", err)
	}
	expectStatus(addr, "unknown path -> 404", "GET /nope HTTP/1.1\r\nHost: t\r\n\r\n", 404, report)
	expectStatus(addr, "POST / -> 405", "POST / HTTP/1.1\r\nHost: t\r\nContent-Length: 0\r\n\r\n", 405, report)
	expectStatus(addr, "malformed request line -> 400", "BLAH\r\n\r\n", 400, advise)
	{
		big := "GET / HTTP/1.1\r\nHost: t\r\nX-Big: " + strings.Repeat("a", 70000) + "\r\n\r\n"
		c, r, err := dial(addr)
		if err == nil {
			c.Write([]byte(big)) // the server may close before reading all of it
			var p resp
			if p, err = readResp(r, false); err == nil && p.status != 431 && p.status != 400 {
				err = fmt.Errorf("status %d, want 431 (or 400)", p.status)
			}
			c.Close()
		}
		advise("70 KB of headers -> 431", err)
	}
	if failed {
		os.Exit(1)
	}
}

func main() {
	if len(os.Args) >= 3 && os.Args[1] == "check" {
		check(os.Args[2])
		return
	}
	if len(os.Args) < 6 || os.Args[1] != "bench" {
		fmt.Println("usage: load bench <host:port> <seconds> <connections> keepalive|close|pipeline [depth]\n       load check <host:port>")
		os.Exit(2)
	}
	secs, _ := strconv.ParseFloat(os.Args[3], 64)
	conns, _ := strconv.Atoi(os.Args[4])
	mode, depth := os.Args[5], 1
	if mode == "pipeline" {
		depth = 16
		if len(os.Args) > 6 {
			depth, _ = strconv.Atoi(os.Args[6])
		}
	}
	bench(os.Args[2], secs, conns, mode != "close", depth)
}
