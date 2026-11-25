// Multithreaded HTTP server, standard .NET: one Thread per CPU, all blocking in
// Socket.Accept() on the same listening socket. Port 8093.
using System.Net; using System.Net.Sockets; using System.Text;
var ln = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
ln.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
ln.Bind(new IPEndPoint(IPAddress.Loopback, 8093));
ln.Listen(512);
byte[] resp = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 13\r\nConnection: close\r\n\r\nHello, World!");
int n = Environment.ProcessorCount;
Console.WriteLine($"C# multithreaded server on 127.0.0.1:8093 ({n} threads)");
var workers = new Thread[n];
for (int i = 0; i < n; i++) {
    workers[i] = new Thread(() => {
        byte[] buf = new byte[1024];
        while (true) {
            using var c = ln.Accept();
            c.Receive(buf);
            c.Send(resp);
        }
    });
    workers[i].Start();
}
foreach (var w in workers) w.Join();
