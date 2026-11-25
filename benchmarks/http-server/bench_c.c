// Single-threaded HTTP server: accept, read request, send fixed response, close.
// Builds on Linux/macOS (gcc) and Windows (MSVC, Winsock).
#include <stdio.h>
#include <string.h>
#ifdef _WIN32
  #include <winsock2.h>
  #pragma comment(lib, "ws2_32.lib")
  typedef SOCKET sock_t;
  #define CLOSE closesocket
#else
  #include <netinet/in.h>
  #include <sys/socket.h>
  #include <unistd.h>
  typedef int sock_t;
  #define CLOSE close
#endif
int main(void){
#ifdef _WIN32
    WSADATA w; WSAStartup(MAKEWORD(2,2), &w);
#endif
    sock_t s=socket(AF_INET,SOCK_STREAM,0);
    int one=1; setsockopt(s,SOL_SOCKET,SO_REUSEADDR,(const char*)&one,sizeof(one));
    struct sockaddr_in a; memset(&a,0,sizeof(a)); a.sin_family=AF_INET; a.sin_port=htons(8084); a.sin_addr.s_addr=htonl(0x7f000001);  // 127.0.0.1
    if(bind(s,(struct sockaddr*)&a,sizeof(a))<0){perror("bind");return 1;}
    listen(s,128);
    const char*resp="HTTP/1.1 200 OK\r\nContent-Length: 13\r\nConnection: close\r\n\r\nHello, World!";
    int rlen=(int)strlen(resp);
    char buf[1024];
    for(;;){ sock_t c=accept(s,0,0);
#ifdef _WIN32
        if(c==INVALID_SOCKET)continue;
#else
        if(c<0)continue;
#endif
        recv(c,buf,sizeof(buf),0); send(c,resp,rlen,0); CLOSE(c); }
    return 0;
}
