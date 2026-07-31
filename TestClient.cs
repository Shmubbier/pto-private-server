using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

// Drives two clients through a full multi-wave match and logs all packets.

class TestClient
{
    const int Port = 51338;
    static readonly object _l = new object();
    static void Log(string m) { lock (_l) Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + m); }

    class Conn : IDisposable
    {
        public TcpClient C; public NetworkStream Ns; public string Name; public int P;
        private readonly List<byte> _buf = new List<byte>();
        public int PlayerIndex; // 0 or 1

        public Conn(string name, int playerIndex) { Name = name; PlayerIndex = playerIndex; }

        public byte[] ReadPacket(int timeoutMs = 15000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                // Non-blocking: only read when data is available
                if (C.Available > 0)
                {
                    byte[] chunk = new byte[C.Available];
                    Ns.Read(chunk, 0, chunk.Length);
                    _buf.AddRange(chunk);
                }
                if (_buf.Count >= 7)
                {
                    uint len = BitConverter.ToUInt32(_buf.ToArray(), 3);
                    if (len < 7) { _buf.Clear(); continue; }
                    // Read remaining body bytes
                    while (_buf.Count < len && C.Available > 0)
                    {
                        int need = (int)len - _buf.Count;
                        byte[] tmp = new byte[need];
                        int sub = Ns.Read(tmp, 0, need);
                        if (sub > 0) _buf.AddRange(new ArraySegment<byte>(tmp, 0, sub));
                        else break;
                    }
                    if (_buf.Count >= len)
                    {
                        byte[] pkt = _buf.GetRange(0, (int)len).ToArray();
                        _buf.RemoveRange(0, (int)len);
                        return pkt;
                    }
                }
                Thread.Sleep(20);
            }
            return null;
        }

        public void Send(byte[] data) { if (Ns != null) { Ns.Write(data, 0, data.Length); Ns.Flush(); } }

        public void Dispose() { try { C.Close(); } catch { } }

        public byte[] Frame(byte op, byte[] pl)
        {
            uint t = (uint)(7 + pl.Length);
            var ms = new MemoryStream(); var bw = new BinaryWriter(ms);
            bw.Write(op); bw.Write((ushort)1374); bw.Write(t); if (pl.Length > 0) bw.Write(pl);
            bw.Flush(); return ms.ToArray();
        }
        byte[] U16(ushort v) { return BitConverter.GetBytes(v); }

        byte[] BuildLogin(string user, string pass)
        {
            var ms = new MemoryStream(); ms.WriteByte(0);
            byte[] ub = Encoding.UTF8.GetBytes(user ?? ""); ms.Write(ub, 0, ub.Length); ms.WriteByte(0);
            byte[] pb = Encoding.UTF8.GetBytes(pass ?? ""); ms.Write(pb, 0, pb.Length); ms.WriteByte(0);
            ms.Write(U16(72), 0, 2);
            return ms.ToArray();
        }

        public void Login()
        {
            // Login
            C = new TcpClient(); C.Connect("127.0.0.1", Port); C.NoDelay = true; Ns = C.GetStream();
            byte[] loginBody = BuildLogin(Name, "pw");
            Send(Frame(46, loginBody));
            // Wait for login response
            var deadline = DateTime.UtcNow.AddMilliseconds(5000);
            byte[] resp = null;
            while (resp == null && DateTime.UtcNow < deadline) { resp = ReadPacket(100); Thread.Sleep(20); }
            if (resp == null) throw new Exception("No login response");
            Log(Name + " logged in");

            // Drain remaining login setup packets (account data, loaded)
            deadline = DateTime.UtcNow.AddMilliseconds(2000);
            while (DateTime.UtcNow < deadline) { var p = ReadPacket(100); if (p == null) Thread.Sleep(20); }
        }

        public void SaveDeck(ushort back, ushort land, ushort[] cards)
        {
            var ms = new MemoryStream();
            ms.WriteByte(0); // register=0 (save)
            byte[] nameB = Encoding.UTF8.GetBytes(Name + "Deck"); ms.Write(nameB, 0, nameB.Length); ms.WriteByte(0);
            ms.WriteByte(0); // deck id = 0
            ms.Write(U16(back), 0, 2);
            ms.Write(U16(land), 0, 2);
            // Server expects exactly 31 card slots
            for (int i = 0; i < 31; i++)
                ms.Write(U16(i < cards.Length ? cards[i] : (ushort)0), 0, 2);
            Send(Frame(47, ms.ToArray()));
            // Drain any lingering packets
            Thread.Sleep(300);
            var dl = DateTime.UtcNow.AddMilliseconds(1000);
            while (DateTime.UtcNow < dl) { var p = ReadPacket(100); if (p == null) Thread.Sleep(20); }
            Log(Name + " deck saved (" + cards.Length + " cards)");
        }

        public void Ready() { Thread.Sleep(200); Send(Frame(20, new byte[0])); Log(Name + " ready"); }

        public void Mulligan(byte[] keeps)
        {
            Thread.Sleep(300);
            // Wait for battle data / mulligan
            var dl = DateTime.UtcNow.AddMilliseconds(2000);
            while (DateTime.UtcNow < dl) { var p = ReadPacket(100); if (p == null) Thread.Sleep(20); }
            // Send mulligan (keep all = send self/other cancel array)
            Send(Frame(37, keeps ?? new byte[] { 0, 0, 0, 0 }));
            Log(Name + " mulligan sent");
        }

        public void Summon(byte gx, byte gy, byte handIndex)
        {
            Send(Frame(10, new byte[] { 0, gx, gy, handIndex }));
            Log(Name + " summon (" + gx + "," + gy + ") hand=" + handIndex);
        }

        public void EndTurn() { Send(Frame(14, new byte[0])); Log(Name + " end turn"); }

        public void Attack(byte ax, byte ay, byte tx, byte ty)
        {
            Send(Frame(22, new byte[] { 0, ax, ay, tx, ty }));
            Log(Name + " attack (" + ax + "," + ay + ") -> (" + tx + "," + ty + ")");
        }
    }

    // Wait for a specific opcode from a connection
    static byte[] WaitForOp(Conn c, byte expectedOp, int timeoutMs = 15000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var pkt = c.ReadPacket(500);
            if (pkt == null) continue;
            if (pkt[0] == expectedOp) return pkt;
        }
        return null;
    }

    static void DrainAll(Conn[] conns, int timeoutMs = 1500)
    {
        var dl = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < dl)
        {
            bool any = false;
            foreach (var c in conns)
            {
                var p = c.ReadPacket(50);
                if (p != null) any = true;
            }
            if (!any) Thread.Sleep(50);
        }
    }

    static void Main()
    {
        Log("TestClient: connecting two players...");

        using (var p0 = new Conn("Tester", 0))
        using (var p1 = new Conn("Bot", 1))
        {
            p0.Login(); p1.Login();

            // Both need a saved deck before queueing (server loads it for battle setup)
            ushort[] cards = { 102, 78, 72, 84, 180, 52, 54, 56, 58, 60, 62, 64, 66, 68, 70 };
            p0.SaveDeck(1, 2, cards);
            p1.SaveDeck(1, 2, cards);

            // Queue both before waiting for match
            p0.Send(p0.Frame(0, new byte[] { 0 }));
            Log("Tester queued");
            p1.Send(p1.Frame(0, new byte[] { 0 }));
            Log("Bot queued");

            // Both wait for op 2 (BattleStart)
            if (WaitForOp(p0, 2) == null || WaitForOp(p1, 2) == null) { Log("Match failed"); return; }
            Log("Both matched");

            // Both send op 20 ready
            p0.Ready(); p1.Ready();

            // Wait for battle data to arrive (both sides)
            Thread.Sleep(500); DrainAll(new[] { p0, p1 });

            // Mulligan — both keep all
            p0.Mulligan(new byte[] { 0, 0, 0, 0 });
            p1.Mulligan(new byte[] { 0, 0, 0, 0 });

            // Drain post-mulligan packets (board init)
            Thread.Sleep(1000); DrainAll(new[] { p0, p1 });

            Log("=== ROUND 1, WAVE 2 (Vanguard) ===");

            // P0 summons 3 units at (2,0), (2,1), (2,2)
            p0.Summon(2, 0, 0);
            Thread.Sleep(800);
            p0.Summon(2, 1, 0);
            Thread.Sleep(800);
            p0.Summon(2, 2, 0);
            Thread.Sleep(800);

            // P0 ends turn
            p0.EndTurn();
            Thread.Sleep(2000); DrainAll(new[] { p0, p1 });

            // P1 gets turn — summon + end turn
            p1.Summon(2, 0, 0);
            Thread.Sleep(800);
            p1.EndTurn();
            Thread.Sleep(2000); DrainAll(new[] { p0, p1 });

            // Wave 1 (Flank) — both just end turn
            Log("=== WAVE 1 (Flank) ===");
            p0.EndTurn();
            Thread.Sleep(2000); DrainAll(new[] { p0, p1 });
            p1.EndTurn();
            Thread.Sleep(2000); DrainAll(new[] { p0, p1 });

            // Wave 0 (Rear) — both end turn
            Log("=== WAVE 0 (Rear) ===");
            p0.EndTurn();
            Thread.Sleep(2000); DrainAll(new[] { p0, p1 });
            p1.EndTurn();
            Thread.Sleep(2000); DrainAll(new[] { p0, p1 });

            // Round 2, Wave 2 (Vanguard) — attack phase
            Log("=== ROUND 2, WAVE 2 (Vanguard) — attacks enabled ===");
            Thread.Sleep(3000); DrainAll(new[] { p0, p1 });

            // Figure out whose turn it is and try to attack with leader
            var turnDl = DateTime.UtcNow.AddMilliseconds(5000);
            while (DateTime.UtcNow < turnDl)
            {
                foreach (var p in new[] { p0, p1 })
                {
                    var pkt = p.ReadPacket(100);
                    if (pkt != null && pkt[0] == 14)
                    {
                        ushort player = BitConverter.ToUInt16(pkt, 7);
                        Log(p.Name + " got TurnGet for player=" + player);
                        if (player == p.PlayerIndex)
                        {
                            Thread.Sleep(500);
                            p.Attack(1, 1, 2, 0);
                            Thread.Sleep(1000);
                        }
                    }
                }
                Thread.Sleep(50);
            }

            Thread.Sleep(2000); DrainAll(new[] { p0, p1 });

            Log("=== DONE ===");
        }
    }
}
