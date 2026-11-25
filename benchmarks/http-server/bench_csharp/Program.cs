using System.Net; using System.Net.Sockets; using System.Text;
var ln = new TcpListener(IPAddress.Parse("127.0.0.1"), 8083);
ln.Start();
byte[] resp = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 13\r\nConnection: close\r\n\r\nHello, World!");
byte[] buf = new byte[1024];
while (true) {
    using var c = ln.AcceptSocket();
    c.Receive(buf);
    c.Send(resp);
}
