package main
import ( "net"; "bufio" )
func main() {
    ln, err := net.Listen("tcp", "127.0.0.1:8081")
    if err != nil { panic(err) }
    resp := []byte("HTTP/1.1 200 OK\r\nContent-Length: 13\r\nConnection: close\r\n\r\nHello, World!")
    buf := make([]byte, 1024)
    for {
        c, err := ln.Accept()
        if err != nil { continue }
        bufio.NewReader(c).Read(buf)
        c.Write(resp)
        c.Close()
    }
}
