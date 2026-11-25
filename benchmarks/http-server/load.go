// HTTP load generator. Opens a new connection per request (servers send
// Connection: close), fires N requests across C concurrent workers, reports req/s.
// Usage: go run load.go <host:port> <total_requests> <concurrency>
package main
import ( "fmt"; "net"; "os"; "strconv"; "sync"; "sync/atomic"; "time" )
func main() {
    if len(os.Args) < 4 { fmt.Println("usage: load <host:port> <total> <concurrency>"); return }
    addr := os.Args[1]
    total, _ := strconv.Atoi(os.Args[2])
    conc, _ := strconv.Atoi(os.Args[3])
    req := []byte("GET / HTTP/1.1\r\nHost: x\r\n\r\n")
    var done, ok int64
    var wg sync.WaitGroup
    start := time.Now()
    for w := 0; w < conc; w++ {
        wg.Add(1)
        go func() {
            defer wg.Done()
            buf := make([]byte, 512)
            for atomic.AddInt64(&done, 1) <= int64(total) {
                c, err := net.Dial("tcp", addr)
                if err != nil { continue }
                c.Write(req)
                n, _ := c.Read(buf)
                if n > 0 { atomic.AddInt64(&ok, 1) }
                c.Close()
            }
        }()
    }
    wg.Wait()
    el := time.Since(start).Seconds()
    fmt.Printf("%.0f req/s  (%d ok / %d in %.2fs)\n", float64(ok)/el, ok, total, el)
}
