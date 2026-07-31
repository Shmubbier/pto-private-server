using System;
using System.Net.Sockets;
using System.Threading;

class Program
{
    static int Port = 51338;
    static class Op { public const byte Login = 46; public const byte Queue = 0; public const byte BattleReady = 20; }

    static void Main(string[] args)
    {
        while (true)
        {
            try
            {
                var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, Port);
                listener.Start();
                Console.WriteLine("Harness: Server listening on " + Port);
                Thread.Sleep(Timeout.Infinite);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Harness: " + ex.Message);
                Thread.Sleep(5000);
            }
        }
    }
}