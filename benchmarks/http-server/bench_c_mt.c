// Multithreaded HTTP server: one worker thread per CPU, all blocking in accept()
// on the same listening socket. Builds on Linux (gcc -pthread) and Windows (MSVC).
#include <stdio.h>
#include <string.h>
#ifdef _WIN32
  #include <winsock2.h>
  #include <windows.h>
  #pragma comment(lib, "ws2_32.lib")
  typedef SOCKET sock_t;
  #define CLOSE closesocket
  #define BAD(c) ((c) == INVALID_SOCKET)
#else
  #include <netinet/in.h>
  #include <pthread.h>
  #include <sys/socket.h>
  #include <unistd.h>
  typedef int sock_t;
  #define CLOSE close
  #define BAD(c) ((c) < 0)
#endif
static sock_t s;
static const char resp[] = "HTTP/1.1 200 OK\r\nContent-Length: 13\r\nConnection: close\r\n\r\nHello, World!";

static void serve(void) {
    char buf[1024];
    for (;;) {
        sock_t c = accept(s, 0, 0);
        if (BAD(c)) continue;
        recv(c, buf, sizeof(buf), 0); send(c, resp, (int)sizeof(resp) - 1, 0); CLOSE(c);
    }
}
#ifdef _WIN32
static DWORD WINAPI worker(LPVOID _) { serve(); return 0; }
#else
static void* worker(void* _) { serve(); return 0; }
#endif

int main(void) {
    int n;
#ifdef _WIN32
    WSADATA w; WSAStartup(MAKEWORD(2, 2), &w);
    SYSTEM_INFO si; GetSystemInfo(&si); n = (int)si.dwNumberOfProcessors;
#else
    n = (int)sysconf(_SC_NPROCESSORS_ONLN);
#endif
    s = socket(AF_INET, SOCK_STREAM, 0);
    int one = 1; setsockopt(s, SOL_SOCKET, SO_REUSEADDR, (const char*)&one, sizeof(one));
    struct sockaddr_in a; memset(&a, 0, sizeof(a));
    a.sin_family = AF_INET; a.sin_port = htons(8094); a.sin_addr.s_addr = htonl(0x7f000001);
    if (bind(s, (struct sockaddr*)&a, sizeof(a)) < 0) { perror("bind"); return 1; }
    listen(s, 512);
    printf("C multithreaded server on 127.0.0.1:8094 (%d threads)\n", n); fflush(stdout);
    for (int i = 1; i < n; i++) {
#ifdef _WIN32
        CloseHandle(CreateThread(NULL, 0, worker, NULL, 0, NULL));
#else
        pthread_t t; pthread_create(&t, NULL, worker, NULL);
#endif
    }
    serve();
}
