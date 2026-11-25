// Multithreaded HTTP server, standard C++: one std::thread per CPU, all blocking
// in accept() on the same listening socket. Linux (g++ -pthread) and Windows (MSVC).
#include <cstdio>
#include <cstring>
#include <thread>
#include <vector>
#ifdef _WIN32
  #include <winsock2.h>
  #pragma comment(lib, "ws2_32.lib")
  using sock_t = SOCKET;
  static void close_sock(sock_t c) { closesocket(c); }
  static bool bad(sock_t c) { return c == INVALID_SOCKET; }
#else
  #include <netinet/in.h>
  #include <sys/socket.h>
  #include <unistd.h>
  using sock_t = int;
  static void close_sock(sock_t c) { close(c); }
  static bool bad(sock_t c) { return c < 0; }
#endif

int main() {
#ifdef _WIN32
    WSADATA w; WSAStartup(MAKEWORD(2, 2), &w);
#endif
    sock_t s = socket(AF_INET, SOCK_STREAM, 0);
    int one = 1; setsockopt(s, SOL_SOCKET, SO_REUSEADDR, (const char*)&one, sizeof(one));
    sockaddr_in a{}; a.sin_family = AF_INET; a.sin_port = htons(8095); a.sin_addr.s_addr = htonl(0x7f000001);
    if (bind(s, (sockaddr*)&a, sizeof(a)) < 0) { std::perror("bind"); return 1; }
    listen(s, 512);
    static const char resp[] = "HTTP/1.1 200 OK\r\nContent-Length: 13\r\nConnection: close\r\n\r\nHello, World!";
    unsigned n = std::thread::hardware_concurrency();
    std::printf("C++ multithreaded server on 127.0.0.1:8095 (%u threads)\n", n); std::fflush(stdout);
    std::vector<std::thread> pool;
    for (unsigned i = 0; i < n; i++)
        pool.emplace_back([s] {
            char buf[1024];
            for (;;) {
                sock_t c = accept(s, nullptr, nullptr);
                if (bad(c)) continue;
                recv(c, buf, sizeof(buf), 0); send(c, resp, (int)sizeof(resp) - 1, 0); close_sock(c);
            }
        });
    for (auto& t : pool) t.join();
}
