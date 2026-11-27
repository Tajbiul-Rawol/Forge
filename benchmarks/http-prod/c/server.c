// Production-style HTTP/1.1 server in C: non-blocking sockets and one poll() event
// loop per thread. Keep-alive, pipelining, request bodies (Content-Length), idle
// timeouts, header size limits, a cached Date header and correct status codes.
//
//   GET /          -> 200 "Hello, World!"
//   other method / -> 405, other path -> 404, malformed -> 400 (then close),
//   headers > 8 KB -> 431, body > 1 MB -> 413, Transfer-Encoding -> 501.
//
// Usage: server [port] [threads]      (threads = 1: single-threaded; 0 = one per CPU)
// Build: cl /O2 server.c   |   gcc -O2 -pthread server.c -o server
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#ifdef _WIN32
  #define WIN32_LEAN_AND_MEAN
  #include <winsock2.h>
  #include <ws2tcpip.h>
  #include <windows.h>
  #pragma comment(lib, "ws2_32.lib")
  typedef SOCKET sock_t;
  #define BADSOCK INVALID_SOCKET
  #define CLOSESOCK closesocket
  #define POLL WSAPoll
  #define WOULDBLOCK() (WSAGetLastError() == WSAEWOULDBLOCK)
  static void set_nonblocking(sock_t s) { u_long one = 1; ioctlsocket(s, FIONBIO, &one); }
  static long long now_ms(void) { return (long long)GetTickCount64(); }
  static void gmt(time_t t, struct tm* tm) { gmtime_s(tm, &t); }
#else
  #include <errno.h>
  #include <fcntl.h>
  #include <netinet/in.h>
  #include <netinet/tcp.h>
  #include <poll.h>
  #include <pthread.h>
  #include <signal.h>
  #include <sys/socket.h>
  #include <unistd.h>
  typedef int sock_t;
  #define BADSOCK (-1)
  #define CLOSESOCK close
  #define POLL poll
  #define WOULDBLOCK() (errno == EAGAIN || errno == EWOULDBLOCK || errno == EINTR)
  static void set_nonblocking(sock_t s) { fcntl(s, F_SETFL, fcntl(s, F_GETFL) | O_NONBLOCK); }
  static long long now_ms(void) {
      struct timespec ts; clock_gettime(CLOCK_MONOTONIC, &ts);
      return (long long)ts.tv_sec * 1000 + ts.tv_nsec / 1000000;
  }
  static void gmt(time_t t, struct tm* tm) { gmtime_r(&t, tm); }
#endif

#define IN_CAP      8192            // max request head; also the read buffer
#define OUT_CAP     16384           // pending responses (pipelining backpressure)
#define MAX_CONNS   4096            // per thread
#define MAX_BODY    (1 << 20)
#define IDLE_MS     30000
#define DRAIN_MS    2000            // lingering close: how long to wait for the peer

typedef struct {
    sock_t fd;
    int inlen, scan;                // bytes buffered; where to resume looking for \r\n\r\n
    int outlen, outoff;
    long long skip;                 // request-body bytes still to discard
    int close_after;                // close once the output is flushed
    int draining;                   // lingering close: output shut down, discarding input
    long long last;                 // last activity, for the idle timeout
    char in[IN_CAP];
    char out[OUT_CAP];
} conn_t;

typedef struct {
    sock_t listener;
    struct pollfd pfd[MAX_CONNS + 1];
    conn_t* conns[MAX_CONNS];
    int n;
    time_t date_sec;
    char date[48];                  // "Date: Thu, 26 Nov 2026 12:00:00 GMT\r\n"
} loop_t;

static int port = 8100;

static void update_date(loop_t* L) {
    time_t t = time(NULL);
    if (t == L->date_sec) return;
    L->date_sec = t;
    struct tm tm; gmt(t, &tm);
    strftime(L->date, sizeof L->date, "Date: %a, %d %b %Y %H:%M:%S GMT\r\n", &tm);
}

static int ieq(const char* a, int alen, const char* lit) {     // case-insensitive match
    int n = (int)strlen(lit);
    if (alen != n) return 0;
    for (int i = 0; i < n; i++) {
        char c = a[i]; if (c >= 'A' && c <= 'Z') c += 32;
        if (c != lit[i]) return 0;
    }
    return 1;
}

static int has_token(const char* v, int vlen, const char* tok) { // "keep-alive, Upgrade"
    int n = (int)strlen(tok);
    for (int i = 0; i + n <= vlen; i++) {
        if (ieq(v + i, n, tok) && (i == 0 || v[i-1] == ' ' || v[i-1] == ',')
            && (i + n == vlen || v[i+n] == ' ' || v[i+n] == ',')) return 1;
    }
    return 0;
}

static void respond(loop_t* L, conn_t* c, int status, const char* reason, const char* body, int keep) {
    char* o = c->out + c->outlen;
    int room = OUT_CAP - c->outlen;
    int n = snprintf(o, room,
        "HTTP/1.1 %d %s\r\nServer: c\r\n%sContent-Type: text/plain\r\nContent-Length: %d\r\n%s\r\n%s",
        status, reason, L->date, (int)strlen(body), keep ? "" : "Connection: close\r\n", body);
    if (n > 0 && n < room) c->outlen += n;
    if (!keep) c->close_after = 1;
}

static void consume(conn_t* c, int k) {
    memmove(c->in, c->in + k, c->inlen - k); c->inlen -= k; c->scan = 0;
}

// Answer every complete request in c->in. Returns when more input is needed, the
// output buffer is nearly full (backpressure), or the connection is closing.
static void process(loop_t* L, conn_t* c) {
    while (!c->close_after) {
        if (c->skip > 0) {                              // discard a request body
            int k = c->inlen < c->skip ? c->inlen : (int)c->skip;
            consume(c, k); c->skip -= k;
            if (c->skip > 0) return;
        }
        if (OUT_CAP - c->outlen < 512) return;
        const char* h = c->in;
        int end = -1;
        for (int i = c->scan > 3 ? c->scan - 3 : 0; i + 3 < c->inlen; i++) {
            if (h[i] == '\r' && h[i+1] == '\n' && h[i+2] == '\r' && h[i+3] == '\n') { end = i; break; }
        }
        if (end < 0) {
            c->scan = c->inlen;
            if (c->inlen == IN_CAP) respond(L, c, 431, "Request Header Fields Too Large", "Request Header Fields Too Large", 0);
            return;
        }
        // request line: METHOD SP TARGET SP HTTP/1.x
        int le = 0; while (le < end && h[le] != '\r') le++;
        int sp1 = 0; while (sp1 < le && h[sp1] != ' ') sp1++;
        int sp2 = sp1 + 1; while (sp2 < le && h[sp2] != ' ') sp2++;
        if (sp1 == 0 || sp2 >= le || sp2 == sp1 + 1 || le - sp2 - 1 != 8 || memcmp(h + sp2 + 1, "HTTP/1.", 7) != 0
            || (h[le-1] != '0' && h[le-1] != '1')) {
            respond(L, c, 400, "Bad Request", "Bad Request", 0); return;
        }
        int keep = h[le-1] == '1';                      // HTTP/1.1 defaults to keep-alive
        int is_get = sp1 == 3 && memcmp(h, "GET", 3) == 0;
        int is_root = sp2 - sp1 - 1 == 1 && h[sp1+1] == '/';
        long long clen = 0; int te = 0, bad = 0;
        for (int p = le + 2; p < end; ) {               // headers
            int e = p; while (e < end && h[e] != '\r') e++;
            int colon = p; while (colon < e && h[colon] != ':') colon++;
            if (colon == e || colon == p) { bad = 1; break; }
            int v = colon + 1; while (v < e && (h[v] == ' ' || h[v] == '\t')) v++;
            int ve = e; while (ve > v && (h[ve-1] == ' ' || h[ve-1] == '\t')) ve--;
            if (ieq(h + p, colon - p, "connection")) {
                if (has_token(h + v, ve - v, "close")) keep = 0;
                else if (has_token(h + v, ve - v, "keep-alive")) keep = 1;
            } else if (ieq(h + p, colon - p, "content-length")) {
                if (v == ve) bad = 1;
                clen = 0;
                for (int i = v; i < ve; i++) {
                    if (h[i] < '0' || h[i] > '9' || clen > MAX_BODY) { bad = 1; break; }
                    clen = clen * 10 + (h[i] - '0');
                }
            } else if (ieq(h + p, colon - p, "transfer-encoding")) te = 1;
            p = e + 2;
        }
        consume(c, end + 4);
        if (bad)             { respond(L, c, 400, "Bad Request", "Bad Request", 0); return; }
        if (te)              { respond(L, c, 501, "Not Implemented", "Not Implemented", 0); return; }
        if (clen > MAX_BODY) { respond(L, c, 413, "Content Too Large", "Content Too Large", 0); return; }
        c->skip = clen;
        if (!is_root)        respond(L, c, 404, "Not Found", "Not Found", keep);
        else if (!is_get)    respond(L, c, 405, "Method Not Allowed", "Method Not Allowed", keep);
        else                 respond(L, c, 200, "OK", "Hello, World!", keep);
    }
}

static void close_conn(loop_t* L, int i) {               // i indexes conns; pfd is i+1
    CLOSESOCK(L->conns[i]->fd);
    free(L->conns[i]);
    L->n--;
    L->conns[i] = L->conns[L->n];
    L->pfd[i + 1] = L->pfd[L->n + 1];
}

#ifdef _WIN32
  #define SHUT_WR SD_SEND
  #define MSG_NOSIGNAL 0
#endif

// Send what is pending. Returns 0 if the connection must close now. A connection
// that is closing gets a lingering close: shut down our side, then discard input
// until the peer closes, so the peer reads the final response instead of a reset.
static int flush(conn_t* c, long long now) {
    while (c->outoff < c->outlen) {
        int k = send(c->fd, c->out + c->outoff, c->outlen - c->outoff, MSG_NOSIGNAL);
        if (k < 0) return WOULDBLOCK();
        c->outoff += k;
    }
    c->outoff = c->outlen = 0;
    if (c->close_after && !c->draining) {
        shutdown(c->fd, SHUT_WR);
        c->draining = 1; c->inlen = 0; c->last = now;
    }
    return 1;
}

static void accept_all(loop_t* L) {
    for (;;) {
        sock_t s = accept(L->listener, NULL, NULL);
        if (s == BADSOCK) return;
        if (L->n == MAX_CONNS) { CLOSESOCK(s); continue; }
        conn_t* c = (conn_t*)malloc(sizeof(conn_t));
        if (!c) { CLOSESOCK(s); continue; }
        set_nonblocking(s);
        int one = 1; setsockopt(s, IPPROTO_TCP, TCP_NODELAY, (const char*)&one, sizeof one);
        c->fd = s; c->inlen = c->scan = c->outlen = c->outoff = c->close_after = c->draining = 0; c->skip = 0;
        c->last = now_ms();
        L->conns[L->n] = c;
        L->pfd[L->n + 1].fd = s; L->pfd[L->n + 1].events = POLLIN; L->pfd[L->n + 1].revents = 0;
        L->n++;
    }
}

static sock_t make_listener(int reuseport) {
    sock_t s = socket(AF_INET, SOCK_STREAM, 0);
    int one = 1;
    setsockopt(s, SOL_SOCKET, SO_REUSEADDR, (const char*)&one, sizeof one);
#ifdef SO_REUSEPORT
    if (reuseport) setsockopt(s, SOL_SOCKET, SO_REUSEPORT, (const char*)&one, sizeof one);
#else
    (void)reuseport;
#endif
    struct sockaddr_in a; memset(&a, 0, sizeof a);
    a.sin_family = AF_INET; a.sin_port = htons((unsigned short)port); a.sin_addr.s_addr = htonl(INADDR_ANY);
    if (bind(s, (struct sockaddr*)&a, sizeof a) != 0 || listen(s, 4096) != 0) {
        fprintf(stderr, "cannot listen on port %d\n", port); exit(1);
    }
    set_nonblocking(s);
    return s;
}

static void run_loop(sock_t listener) {
    loop_t* L = (loop_t*)calloc(1, sizeof(loop_t));
    L->listener = listener;
    L->pfd[0].fd = listener; L->pfd[0].events = POLLIN;
    long long last_sweep = now_ms();
    for (;;) {
        int r = POLL(L->pfd, (unsigned)(L->n + 1), 1000);
        long long now = now_ms();
        update_date(L);
        if (r > 0) {
            if (L->pfd[0].revents & POLLIN) accept_all(L);
            for (int i = L->n - 1; i >= 0; i--) {       // backwards: close_conn moves the last one here
                short ev = L->pfd[i + 1].revents;
                if (!ev) continue;
                L->pfd[i + 1].revents = 0;
                conn_t* c = L->conns[i];
                int alive = 1;
                if (ev & (POLLERR | POLLNVAL)) alive = 0;
                else if (ev & (POLLIN | POLLHUP)) {
                    for (;;) {                          // read until it would block
                        int room = IN_CAP - c->inlen;
                        if (room == 0) break;
                        int k = recv(c->fd, c->in + c->inlen, room, 0);
                        if (k == 0) { alive = 0; break; }
                        if (k < 0) { if (!WOULDBLOCK()) alive = 0; break; }
                        c->inlen += k;
                        if (k < room) break;
                    }
                    if (c->draining) c->inlen = 0;     // discard
                    else { c->last = now; if (alive) process(L, c); }
                }
                if (alive && (c->outlen > 0 || (c->close_after && !c->draining))) {
                    alive = flush(c, now);
                    if (alive && !c->draining && c->outlen == 0 && c->inlen > 0) { process(L, c); if (c->outlen) alive = flush(c, now); }
                }
                if (!alive) { close_conn(L, i); continue; }
                L->pfd[i + 1].events = c->outlen > c->outoff ? POLLOUT : POLLIN;
            }
        }
        if (now - last_sweep >= 1000) {                 // idle timeout
            last_sweep = now;
            for (int i = L->n - 1; i >= 0; i--)
                if (now - L->conns[i]->last > (L->conns[i]->draining ? DRAIN_MS : IDLE_MS)) close_conn(L, i);
        }
    }
}

static sock_t shared_listener;
#ifdef _WIN32
static DWORD WINAPI worker(LPVOID arg) { (void)arg; run_loop(shared_listener); return 0; }
#else
static void* worker(void* arg) { (void)arg; run_loop(make_listener(1)); return 0; }
#endif

int main(int argc, char** argv) {
    if (argc > 1) port = atoi(argv[1]);
    int threads = argc > 2 ? atoi(argv[2]) : 1;
#ifdef _WIN32
    WSADATA w; WSAStartup(MAKEWORD(2, 2), &w);
    if (threads <= 0) { SYSTEM_INFO si; GetSystemInfo(&si); threads = (int)si.dwNumberOfProcessors; }
    shared_listener = make_listener(0);                 // Windows: every loop polls one listener
#else
    signal(SIGPIPE, SIG_IGN);
    if (threads <= 0) threads = (int)sysconf(_SC_NPROCESSORS_ONLN);
    shared_listener = make_listener(1);                 // Linux: one SO_REUSEPORT listener per loop
#endif
    printf("C server on port %d (%d thread%s)\n", port, threads, threads == 1 ? "" : "s"); fflush(stdout);
    for (int i = 1; i < threads; i++) {
#ifdef _WIN32
        CloseHandle(CreateThread(NULL, 0, worker, NULL, 0, NULL));
#else
        pthread_t t; pthread_create(&t, NULL, worker, NULL);
#endif
    }
    run_loop(shared_listener);
    return 0;
}
