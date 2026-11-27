// Production HTTP server in Go: the standard library's net/http.
//
//	GET / -> 200 "Hello, World!"; other method on / -> 405; other path -> 404.
//
// Usage: server [port] [threads]   (threads = 1: GOMAXPROCS=1; 0 = one per CPU)
package main

import (
	"fmt"
	"net/http"
	"os"
	"runtime"
	"strconv"
	"time"
)

func main() {
	port, threads := "8100", 1
	if len(os.Args) > 1 {
		port = os.Args[1]
	}
	if len(os.Args) > 2 {
		threads, _ = strconv.Atoi(os.Args[2])
	}
	if threads <= 0 {
		threads = runtime.NumCPU()
	}
	runtime.GOMAXPROCS(threads)

	hello := []byte("Hello, World!")
	mux := http.NewServeMux()
	mux.HandleFunc("GET /{$}", func(w http.ResponseWriter, r *http.Request) {
		h := w.Header()
		h["Content-Type"] = []string{"text/plain"}
		h["Server"] = []string{"go"}
		w.Write(hello)
	})
	srv := &http.Server{
		Addr:              ":" + port,
		Handler:           mux,
		ReadHeaderTimeout: 10 * time.Second,
		IdleTimeout:       30 * time.Second,
		MaxHeaderBytes:    8 << 10,
	}
	fmt.Printf("Go server on port %s (GOMAXPROCS %d)\n", port, threads)
	if err := srv.ListenAndServe(); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
}
