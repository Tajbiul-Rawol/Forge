use std::net::TcpListener;
use std::io::{Read, Write};
fn main() {
    let ln = TcpListener::bind("127.0.0.1:8082").unwrap();
    let resp = b"HTTP/1.1 200 OK\r\nContent-Length: 13\r\nConnection: close\r\n\r\nHello, World!";
    let mut buf = [0u8; 1024];
    for s in ln.incoming() {
        if let Ok(mut c) = s {
            let _ = c.read(&mut buf);
            let _ = c.write_all(resp);
        }
    }
}
