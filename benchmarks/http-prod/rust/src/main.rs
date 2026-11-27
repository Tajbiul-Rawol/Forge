// Production HTTP server in Rust: hyper 1 on the tokio runtime.
//
//   GET / -> 200 "Hello, World!"; other method on / -> 405; other path -> 404.
//
// Usage: server [port] [threads]   (threads = 1: tokio current-thread runtime;
//                                   0 = multi-thread runtime, one worker per CPU)
use std::convert::Infallible;
use std::net::SocketAddr;
use std::time::Duration;

use bytes::Bytes;
use http_body_util::Full;
use hyper::header::{HeaderValue, CONTENT_TYPE, SERVER};
use hyper::server::conn::http1;
use hyper::service::service_fn;
use hyper::{Method, Request, Response, StatusCode};
use hyper_util::rt::{TokioIo, TokioTimer};
use tokio::net::TcpListener;

async fn handle(req: Request<hyper::body::Incoming>) -> Result<Response<Full<Bytes>>, Infallible> {
    let (status, body): (StatusCode, &'static str) = match (req.method(), req.uri().path()) {
        (&Method::GET, "/") => (StatusCode::OK, "Hello, World!"),
        (_, "/") => (StatusCode::METHOD_NOT_ALLOWED, "Method Not Allowed"),
        _ => (StatusCode::NOT_FOUND, "Not Found"),
    };
    let mut resp = Response::new(Full::new(Bytes::from_static(body.as_bytes())));
    *resp.status_mut() = status;
    let h = resp.headers_mut();
    h.insert(SERVER, HeaderValue::from_static("rust"));
    h.insert(CONTENT_TYPE, HeaderValue::from_static("text/plain"));
    // hyper adds a cached Date header itself
    Ok(resp)
}

async fn serve(port: u16) {
    let addr = SocketAddr::from(([0, 0, 0, 0], port));
    let listener = TcpListener::bind(addr).await.expect("bind");
    loop {
        let (stream, _) = match listener.accept().await {
            Ok(s) => s,
            Err(_) => continue,
        };
        let _ = stream.set_nodelay(true);
        tokio::spawn(async move {
            let _ = http1::Builder::new()
                .timer(TokioTimer::new())
                .header_read_timeout(Duration::from_secs(10))
                .max_buf_size(16 * 1024)
                .pipeline_flush(true)
                .serve_connection(TokioIo::new(stream), service_fn(handle))
                .await;
        });
    }
}

fn main() {
    let args: Vec<String> = std::env::args().collect();
    let port: u16 = args.get(1).and_then(|s| s.parse().ok()).unwrap_or(8100);
    let mut threads: usize = args.get(2).and_then(|s| s.parse().ok()).unwrap_or(1);
    if threads == 0 {
        threads = std::thread::available_parallelism().map(|n| n.get()).unwrap_or(1);
    }
    let rt = if threads == 1 {
        tokio::runtime::Builder::new_current_thread().enable_all().build()
    } else {
        tokio::runtime::Builder::new_multi_thread().worker_threads(threads).enable_all().build()
    }
    .expect("runtime");
    println!("Rust (hyper) server on port {} ({} thread{})", port, threads, if threads == 1 { "" } else { "s" });
    rt.block_on(serve(port));
}
