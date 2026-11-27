// Production-style HTTP/1.1 server in C++17: non-blocking sockets and one poll()
// event loop per thread (the same design as ../c/server.c, written as C++).
// Keep-alive, pipelining, Content-Length bodies, idle timeouts, lingering close,
// header limits, a cached Date header and correct status codes.
//
//   GET / -> 200 "Hello, World!"; other method on / -> 405; other path -> 404;
//   malformed -> 400; headers > 8 KB -> 431; body > 1 MB -> 413; Transfer-Encoding -> 501.
//
// Usage: server [port] [threads]      (threads = 1: single-threaded; 0 = one per CPU)
// Build: cl /O2 /EHsc /std:c++17 server.cpp   |   g++ -O2 -std=c++17 -pthread server.cpp
#include <array>
#include <chrono>
#include <cstdio>
#include <cstring>
#include <ctime>
#include <memory>
#include <string_view>
#include <thread>
#include <vector>
#ifdef _WIN32
  #define WIN32_LEAN_AND_MEAN
  #define NOMINMAX
  #include <winsock2.h>
  #include <ws2tcpip.h>
  #pragma comment(lib, "ws2_32.lib")
  using sock_t = SOCKET;
  constexpr sock_t kBadSock = INVALID_SOCKET;
  inline int poll_fds(pollfd* p, size_t n, int ms) { return WSAPoll(p, static_cast<ULONG>(n), ms); }
  inline bool would_block() { return WSAGetLastError() == WSAEWOULDBLOCK; }
  inline void close_sock(sock_t s) { closesocket(s); }
  inline void set_nonblocking(sock_t s) { u_long one = 1; ioctlsocket(s, FIONBIO, &one); }
  inline void shut_write(sock_t s) { shutdown(s, SD_SEND); }
  inline void to_gmt(std::time_t t, std::tm* tm) { gmtime_s(tm, &t); }
  constexpr int kNoSignal = 0;
#else
  #include <cerrno>
  #include <csignal>
  #include <fcntl.h>
  #include <netinet/in.h>
  #include <netinet/tcp.h>
  #include <poll.h>
  #include <sys/socket.h>
  #include <unistd.h>
  using sock_t = int;
  constexpr sock_t kBadSock = -1;
  inline int poll_fds(pollfd* p, size_t n, int ms) { return ::poll(p, n, ms); }
  inline bool would_block() { return errno == EAGAIN || errno == EWOULDBLOCK || errno == EINTR; }
  inline void close_sock(sock_t s) { ::close(s); }
  inline void set_nonblocking(sock_t s) { fcntl(s, F_SETFL, fcntl(s, F_GETFL) | O_NONBLOCK); }
  inline void shut_write(sock_t s) { shutdown(s, SHUT_WR); }
  inline void to_gmt(std::time_t t, std::tm* tm) { gmtime_r(&t, tm); }
  constexpr int kNoSignal = MSG_NOSIGNAL;
#endif

namespace {

constexpr int kInCap = 8192;           // max request head; also the read buffer
constexpr int kOutCap = 16384;         // pending responses (pipelining backpressure)
constexpr size_t kMaxConns = 4096;     // per thread
constexpr long long kMaxBody = 1 << 20;
constexpr long long kIdleMs = 30000;
constexpr long long kDrainMs = 2000;

int g_port = 8100;

long long now_ms() {
    using namespace std::chrono;
    return duration_cast<milliseconds>(steady_clock::now().time_since_epoch()).count();
}

bool ieq(std::string_view a, std::string_view lit) {   // lit is lower case
    if (a.size() != lit.size()) return false;
    for (size_t i = 0; i < a.size(); i++) {
        char c = a[i];
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c + 32);
        if (c != lit[i]) return false;
    }
    return true;
}

bool has_token(std::string_view v, std::string_view tok) {
    for (size_t i = 0; i + tok.size() <= v.size(); i++) {
        if (ieq(v.substr(i, tok.size()), tok) && (i == 0 || v[i - 1] == ' ' || v[i - 1] == ',')
            && (i + tok.size() == v.size() || v[i + tok.size()] == ' ' || v[i + tok.size()] == ','))
            return true;
    }
    return false;
}

std::string_view trim(std::string_view s) {
    while (!s.empty() && (s.front() == ' ' || s.front() == '\t')) s.remove_prefix(1);
    while (!s.empty() && (s.back() == ' ' || s.back() == '\t')) s.remove_suffix(1);
    return s;
}

struct Conn {
    sock_t fd;
    int inlen = 0, scan = 0, outlen = 0, outoff = 0;
    long long skip = 0, last = 0;
    bool close_after = false, draining = false;
    std::array<char, kInCap> in;
    std::array<char, kOutCap> out;
    explicit Conn(sock_t s, long long now) : fd(s), last(now) {}
    ~Conn() { close_sock(fd); }

    void consume(int k) {
        std::memmove(in.data(), in.data() + k, static_cast<size_t>(inlen - k));
        inlen -= k; scan = 0;
    }
};

class Loop {
public:
    explicit Loop(sock_t listener) : listener_(listener) {
        pfd_.reserve(kMaxConns + 1);
        pfd_.push_back(pollfd{listener, POLLIN, 0});
    }

    [[noreturn]] void run() {
        long long last_sweep = now_ms();
        for (;;) {
            int r = poll_fds(pfd_.data(), pfd_.size(), 1000);
            long long now = now_ms();
            update_date();
            if (r > 0) {
                if (pfd_[0].revents & POLLIN) accept_all(now);
                for (size_t i = conns_.size(); i-- > 0;) {     // backwards: remove() moves the last one here
                    short ev = pfd_[i + 1].revents;
                    if (!ev) continue;
                    pfd_[i + 1].revents = 0;
                    if (!service(*conns_[i], ev, now)) { remove(i); continue; }
                    const Conn& c = *conns_[i];
                    pfd_[i + 1].events = c.outlen > c.outoff ? POLLOUT : POLLIN;
                }
            }
            if (now - last_sweep >= 1000) {                    // idle and lingering-close timeouts
                last_sweep = now;
                for (size_t i = conns_.size(); i-- > 0;) {
                    const Conn& c = *conns_[i];
                    if (now - c.last > (c.draining ? kDrainMs : kIdleMs)) remove(i);
                }
            }
        }
    }

private:
    sock_t listener_;
    std::vector<pollfd> pfd_;                    // [0] is the listener, [i+1] is conns_[i]
    std::vector<std::unique_ptr<Conn>> conns_;
    std::time_t date_sec_ = 0;
    char date_[48] = {};

    void update_date() {
        std::time_t t = std::time(nullptr);
        if (t == date_sec_) return;
        date_sec_ = t;
        std::tm tm{}; to_gmt(t, &tm);
        std::strftime(date_, sizeof date_, "Date: %a, %d %b %Y %H:%M:%S GMT\r\n", &tm);
    }

    void remove(size_t i) {
        conns_[i] = std::move(conns_.back()); conns_.pop_back();
        pfd_[i + 1] = pfd_.back(); pfd_.pop_back();
    }

    void accept_all(long long now) {
        for (;;) {
            sock_t s = accept(listener_, nullptr, nullptr);
            if (s == kBadSock) return;
            if (conns_.size() == kMaxConns) { close_sock(s); continue; }
            set_nonblocking(s);
            int one = 1;
            setsockopt(s, IPPROTO_TCP, TCP_NODELAY, reinterpret_cast<const char*>(&one), sizeof one);
            conns_.push_back(std::make_unique<Conn>(s, now));
            pfd_.push_back(pollfd{s, POLLIN, 0});
        }
    }

    // Returns false when the connection must be closed now.
    bool service(Conn& c, short ev, long long now) {
        if (ev & (POLLERR | POLLNVAL)) return false;
        if (ev & (POLLIN | POLLHUP)) {
            for (;;) {                                         // read until it would block
                int room = kInCap - c.inlen;
                if (room == 0) break;
                int k = static_cast<int>(recv(c.fd, c.in.data() + c.inlen, room, 0));
                if (k == 0) return false;
                if (k < 0) { if (!would_block()) return false; break; }
                c.inlen += k;
                if (k < room) break;
            }
            if (c.draining) c.inlen = 0;                       // lingering close: discard
            else { c.last = now; process(c); }
        }
        if (c.outlen > 0 || (c.close_after && !c.draining)) {
            if (!flush(c, now)) return false;
            if (!c.draining && c.outlen == 0 && c.inlen > 0) {
                process(c);
                if (c.outlen && !flush(c, now)) return false;
            }
        }
        return true;
    }

    static bool flush(Conn& c, long long now) {
        while (c.outoff < c.outlen) {
            int k = static_cast<int>(send(c.fd, c.out.data() + c.outoff, c.outlen - c.outoff, kNoSignal));
            if (k < 0) return would_block();
            c.outoff += k;
        }
        c.outoff = c.outlen = 0;
        if (c.close_after && !c.draining) {                    // lingering close
            shut_write(c.fd);
            c.draining = true; c.inlen = 0; c.last = now;
        }
        return true;
    }

    void respond(Conn& c, int status, const char* reason, std::string_view body, bool keep) {
        int room = kOutCap - c.outlen;
        int n = std::snprintf(c.out.data() + c.outlen, static_cast<size_t>(room),
            "HTTP/1.1 %d %s\r\nServer: cpp\r\n%sContent-Type: text/plain\r\nContent-Length: %d\r\n%s\r\n%.*s",
            status, reason, date_, static_cast<int>(body.size()), keep ? "" : "Connection: close\r\n",
            static_cast<int>(body.size()), body.data());
        if (n > 0 && n < room) c.outlen += n;
        if (!keep) c.close_after = true;
    }

    void fail(Conn& c, int status, const char* reason) { respond(c, status, reason, reason, false); }

    // Answer every complete request buffered in c.in.
    void process(Conn& c) {
        while (!c.close_after) {
            if (c.skip > 0) {                                  // discard a request body
                int k = c.inlen < c.skip ? c.inlen : static_cast<int>(c.skip);
                c.consume(k); c.skip -= k;
                if (c.skip > 0) return;
            }
            if (kOutCap - c.outlen < 512) return;              // backpressure
            std::string_view buf(c.in.data(), static_cast<size_t>(c.inlen));
            size_t end = buf.find("\r\n\r\n", c.scan > 3 ? static_cast<size_t>(c.scan - 3) : 0);
            if (end == std::string_view::npos) {
                c.scan = c.inlen;
                if (c.inlen == kInCap) fail(c, 431, "Request Header Fields Too Large");
                return;
            }
            std::string_view head = buf.substr(0, end);
            size_t le = head.find("\r\n");
            std::string_view line = head.substr(0, le);
            size_t sp1 = line.find(' ');
            size_t sp2 = sp1 == std::string_view::npos ? sp1 : line.find(' ', sp1 + 1);
            std::string_view version = sp2 == std::string_view::npos ? std::string_view() : line.substr(sp2 + 1);
            if (sp1 == 0 || sp2 == std::string_view::npos || sp2 == sp1 + 1
                || (version != "HTTP/1.1" && version != "HTTP/1.0")) {
                fail(c, 400, "Bad Request"); return;
            }
            std::string_view method = line.substr(0, sp1);
            std::string_view target = line.substr(sp1 + 1, sp2 - sp1 - 1);
            bool keep = version == "HTTP/1.1";
            bool is_get = method == "GET", is_root = target == "/";
            long long clen = 0;
            bool te = false, bad = false;
            size_t p = le == std::string_view::npos ? head.size() : le + 2;
            while (p < head.size()) {                          // headers
                size_t e = head.find("\r\n", p);
                if (e == std::string_view::npos) e = head.size();
                std::string_view h = head.substr(p, e - p);
                size_t colon = h.find(':');
                if (colon == std::string_view::npos || colon == 0) { bad = true; break; }
                std::string_view name = h.substr(0, colon), value = trim(h.substr(colon + 1));
                if (ieq(name, "connection")) {
                    if (has_token(value, "close")) keep = false;
                    else if (has_token(value, "keep-alive")) keep = true;
                } else if (ieq(name, "content-length")) {
                    if (value.empty()) bad = true;
                    clen = 0;
                    for (char d : value) {
                        if (d < '0' || d > '9' || clen > kMaxBody) { bad = true; break; }
                        clen = clen * 10 + (d - '0');
                    }
                } else if (ieq(name, "transfer-encoding")) {
                    te = true;
                }
                p = e + 2;
            }
            c.consume(static_cast<int>(end + 4));              // invalidates the views above
            if (bad) { fail(c, 400, "Bad Request"); return; }
            if (te) { fail(c, 501, "Not Implemented"); return; }
            if (clen > kMaxBody) { fail(c, 413, "Content Too Large"); return; }
            c.skip = clen;
            if (!is_root) respond(c, 404, "Not Found", "Not Found", keep);
            else if (!is_get) respond(c, 405, "Method Not Allowed", "Method Not Allowed", keep);
            else respond(c, 200, "OK", "Hello, World!", keep);
        }
    }
};

sock_t make_listener() {
    sock_t s = socket(AF_INET, SOCK_STREAM, 0);
    int one = 1;
    setsockopt(s, SOL_SOCKET, SO_REUSEADDR, reinterpret_cast<const char*>(&one), sizeof one);
#ifdef SO_REUSEPORT
    setsockopt(s, SOL_SOCKET, SO_REUSEPORT, reinterpret_cast<const char*>(&one), sizeof one);
#endif
    sockaddr_in a{};
    a.sin_family = AF_INET;
    a.sin_port = htons(static_cast<unsigned short>(g_port));
    a.sin_addr.s_addr = htonl(INADDR_ANY);
    if (bind(s, reinterpret_cast<sockaddr*>(&a), sizeof a) != 0 || listen(s, 4096) != 0) {
        std::fprintf(stderr, "cannot listen on port %d\n", g_port);
        std::exit(1);
    }
    set_nonblocking(s);
    return s;
}

}  // namespace

int main(int argc, char** argv) {
    if (argc > 1) g_port = std::atoi(argv[1]);
    int threads = argc > 2 ? std::atoi(argv[2]) : 1;
    if (threads <= 0) threads = static_cast<int>(std::thread::hardware_concurrency());
#ifdef _WIN32
    WSADATA w; WSAStartup(MAKEWORD(2, 2), &w);
    sock_t shared = make_listener();                           // Windows: every loop polls one listener
    auto listener_for_thread = [&] { return shared; };
#else
    std::signal(SIGPIPE, SIG_IGN);
    sock_t first = make_listener();                            // Linux: one SO_REUSEPORT listener per loop
    auto listener_for_thread = [] { return make_listener(); };
    sock_t shared = first;
#endif
    std::printf("C++ server on port %d (%d thread%s)\n", g_port, threads, threads == 1 ? "" : "s");
    std::fflush(stdout);
    std::vector<std::thread> workers;
    for (int i = 1; i < threads; i++) {
        sock_t l = listener_for_thread();
        workers.emplace_back([l] { Loop(l).run(); });
    }
    Loop(shared).run();
}
