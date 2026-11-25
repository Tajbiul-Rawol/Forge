#!/usr/bin/env python3
# HTTP load generator (fallback). Same idea as load.go.
# Usage: python3 load.py <host> <port> <total_requests> <concurrency>
import socket, sys, threading, time
host = sys.argv[1]; port = int(sys.argv[2])
total = int(sys.argv[3]); conc = int(sys.argv[4])
req = b"GET / HTTP/1.1\r\nHost: x\r\n\r\n"
lock = threading.Lock(); state = {"done": 0, "ok": 0}
def worker():
    while True:
        with lock:
            if state["done"] >= total: return
            state["done"] += 1
        try:
            s = socket.create_connection((host, port), timeout=3)
            s.sendall(req)
            if s.recv(512): 
                with lock: state["ok"] += 1
            s.close()
        except Exception:
            pass
t0 = time.perf_counter()
ts = [threading.Thread(target=worker) for _ in range(conc)]
for t in ts: t.start()
for t in ts: t.join()
el = time.perf_counter() - t0
print(f"{state['ok']/el:.0f} req/s  ({state['ok']} ok / {total} in {el:.2f}s)")
