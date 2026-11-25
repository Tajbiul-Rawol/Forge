// Multithreaded HTTP server: one accept loop per CPU (goroutines on the Go
// runtime's threads), all on the same listening socket - the same model as the
// other *_mt servers. Port 8091.
package main

import (
	"fmt"
	"net"
	"runtime"
)

func main() {
	ln, err := net.Listen("tcp", "127.0.0.1:8091")
	if err != nil {
		panic(err)
	}
	resp := []byte("HTTP/1.1 200 OK\r\nContent-Length: 13\r\nConnection: close\r\n\r\nHello, World!")
	n := runtime.NumCPU()
	fmt.Printf("Go multithreaded server on 127.0.0.1:8091 (%d accept loops)\n", n)
	done := make(chan struct{})
	for i := 0; i < n; i++ {
		go func() {
			buf := make([]byte, 1024)
			for {
				c, err := ln.Accept()
				if err != nil {
					continue
				}
				c.Read(buf)
				c.Write(resp)
				c.Close()
			}
		}()
	}
	<-done
}
