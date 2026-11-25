// Multithreaded HTTP server, standard Rust: one std::thread per CPU, all blocking
// in accept() on the same listening socket. Port 8092.
use std::io::{Read, Write};
use std::net::TcpListener;
use std::sync::Arc;
use std::thread;

fn main() {
    let ln = Arc::new(TcpListener::bind("127.0.0.1:8092").unwrap());
    let n = thread::available_parallelism().map(|n| n.get()).unwrap_or(1);
    println!("Rust multithreaded server on 127.0.0.1:8092 ({} threads)", n);
    let workers: Vec<_> = (0..n).map(|_| {
        let ln = Arc::clone(&ln);
        thread::spawn(move || {
            let resp = b"HTTP/1.1 200 OK\r\nContent-Length: 13\r\nConnection: close\r\n\r\nHello, World!";
            let mut buf = [0u8; 1024];
            loop {
                if let Ok((mut c, _)) = ln.accept() {
                    let _ = c.read(&mut buf);
                    let _ = c.write_all(resp);
                }
            }
        })
    }).collect();
    for w in workers { let _ = w.join(); }
}
