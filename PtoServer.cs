// ---------------------------------------------------------------------------
//  PTO_C private server  --  login + lobby milestone (proof of concept)
//
//  Reverse-engineered from the PTO_C151 GameMaker client (data.win).
//  Wire format (little-endian), identical in both directions:
//      u8   opcode
//      u16  magic  (always 1374, ignored by client on read)
//      u32  length (TOTAL bytes of the packet, header included)
//      ...  payload
//
//  buffer_string values are raw bytes terminated by a single 0x00.
//  buffer type codes seen in the client: u16=3, bool=10, string=11.
//
//  This build accepts any credentials and pushes the client straight into
//  the lobby.  It is deliberately small and heavily commented so it can grow
//  into a full game server.
// ---------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace PtoServer
{
    // Opcodes (client<->server), resolved from the client's packet_init map.
    static class Op
    {
        public const byte Login   = 46; // login / register (bool regFlag, str user, str pass, u16 version)
        public const byte AddDeck = 47; // u8 id, str name, u16 back, u16 land, 31x u16 cards
        public const byte AddCard = 49; // bool back, bool land, u16 cardId, u8 amount
        public const byte Loaded  = 48; // bool legend, u16 rank   <-- opens the door -> lobby
        public const byte Ping    = 52; // empty
        public const byte Stages  = 60; // per stage: bool completed, bool unlocked
        public const byte Orbs    = 62; // u8 amount
    }

    // Login response status bytes (first u8 of an Op.Login reply), from container_login.
    static class LoginResult
    {
        public const byte UsernameExists   = 0; // register: name taken
        public const byte NotRegistered    = 1; // login: unknown user
        public const byte BadPassword      = 2;
        public const byte Success          = 3; // followed by string(username)
        public const byte IncorrectVersion = 4;
        public const byte AlreadyLoggedIn  = 5;
    }

    // Builds an outgoing packet body, then frames it with the 7-byte header.
    class PacketWriter
    {
        private readonly MemoryStream _ms = new MemoryStream();
        public PacketWriter WriteBool(bool v) { _ms.WriteByte((byte)(v ? 1 : 0)); return this; }
        public PacketWriter WriteU8(byte v)   { _ms.WriteByte(v); return this; }
        public PacketWriter WriteU16(ushort v){ _ms.Write(BitConverter.GetBytes(v), 0, 2); return this; }
        public PacketWriter WriteU32(uint v)  { _ms.Write(BitConverter.GetBytes(v), 0, 4); return this; }
        // GameMaker buffer_string == UTF-8 bytes + a single null terminator.
        public PacketWriter WriteString(string s)
        {
            byte[] b = Encoding.UTF8.GetBytes(s ?? "");
            _ms.Write(b, 0, b.Length);
            _ms.WriteByte(0);
            return this;
        }

        public byte[] Frame(byte opcode)
        {
            byte[] payload = _ms.ToArray();
            uint total = (uint)(7 + payload.Length);
            byte[] pkt = new byte[total];
            pkt[0] = opcode;
            BitConverter.GetBytes((ushort)1374).CopyTo(pkt, 1);
            BitConverter.GetBytes(total).CopyTo(pkt, 3);
            Buffer.BlockCopy(payload, 0, pkt, 7, payload.Length);
            return pkt;
        }
    }

    // Reads values out of a received payload (little-endian, GM buffer semantics).
    class PacketReader
    {
        private readonly byte[] _b; private int _p;
        public PacketReader(byte[] b, int offset) { _b = b; _p = offset; }
        public bool   ReadBool() { return _b[_p++] != 0; }
        public byte   ReadU8()   { return _b[_p++]; }
        public ushort ReadU16()  { ushort v = BitConverter.ToUInt16(_b, _p); _p += 2; return v; }
        public uint   ReadU32()  { uint v = BitConverter.ToUInt32(_b, _p); _p += 4; return v; }
        public string ReadString()
        {
            int start = _p;
            while (_p < _b.Length && _b[_p] != 0) _p++;
            string s = Encoding.UTF8.GetString(_b, start, _p - start);
            if (_p < _b.Length) _p++; // skip null terminator
            return s;
        }
    }

    class Program
    {
        const int Port = 51338;
        const ushort ClientVersion = 72;   // client sends this; set 0 to accept anything
        static bool Verbose = true;

        static readonly object _logLock = new object();
        static void Log(string msg)
        {
            lock (_logLock)
                Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + msg);
        }

        static void Main(string[] args)
        {
            foreach (var a in args) if (a == "--quiet") Verbose = false;

            var listener = new TcpListener(IPAddress.Any, Port);
            listener.Start();
            Log("PTO_C private server listening on 0.0.0.0:" + Port);
            Log("Point the client at this machine via settings.ini  ->  [NETWORK] IP=<server ip>");
            Log("Waiting for connections... (Ctrl+C to stop)");

            while (true)
            {
                TcpClient c = listener.AcceptTcpClient();
                var t = new Thread(() => HandleClient(c)) { IsBackground = true };
                t.Start();
            }
        }

        static void HandleClient(TcpClient client)
        {
            string who = client.Client.RemoteEndPoint.ToString();
            Log("Client connected: " + who);
            client.NoDelay = true;
            NetworkStream ns = client.GetStream();
            string username = null;

            try
            {
                byte[] header = new byte[7];
                while (true)
                {
                    if (!ReadExact(ns, header, 0, 7)) break;   // connection closed
                    byte opcode = header[0];
                    ushort magic = BitConverter.ToUInt16(header, 1);
                    uint length = BitConverter.ToUInt32(header, 3);
                    if (length < 7 || length > 1 << 20)
                    {
                        Log("! Bad length " + length + " (magic=" + magic + ", op=" + opcode + ") -- dropping");
                        break;
                    }
                    byte[] payload = new byte[length - 7];
                    if (!ReadExact(ns, payload, 0, payload.Length)) break;

                    if (Verbose && opcode != Op.Ping)
                        Log("<- op=" + opcode + " len=" + length + " payload=" + Hex(payload));

                    switch (opcode)
                    {
                        case Op.Login: HandleLogin(ns, payload, ref username); break;
                        case Op.Ping:  Send(ns, new PacketWriter().Frame(Op.Ping)); break; // echo for latency
                        default:
                            if (Verbose) Log("   (unhandled opcode " + opcode + ")");
                            break;
                    }
                }
            }
            catch (Exception ex) { Log("Client " + who + " error: " + ex.Message); }
            finally
            {
                Log("Client disconnected: " + who + (username != null ? " (" + username + ")" : ""));
                try { client.Close(); } catch { }
            }
        }

        static void HandleLogin(NetworkStream ns, byte[] payload, ref string username)
        {
            var r = new PacketReader(payload, 0);
            bool register = r.ReadBool();
            string user = r.ReadString();
            string pass = r.ReadString();
            ushort version = r.ReadU16();
            Log((register ? "REGISTER" : "LOGIN") + " user='" + user + "' pass='" + pass + "' version=" + version);

            if (ClientVersion != 0 && version != ClientVersion)
            {
                Send(ns, new PacketWriter().WriteU8(LoginResult.IncorrectVersion).Frame(Op.Login));
                Log("-> rejected: incorrect version");
                return;
            }

            // Milestone: accept everyone. (Real server would check a user database here.)
            username = string.IsNullOrEmpty(user) ? "Player" : user;

            // 1) login success + echo username
            Send(ns, new PacketWriter().WriteU8(LoginResult.Success).WriteString(username).Frame(Op.Login));
            Log("-> login success as '" + username + "'");

            // 2) (optional account data would go here: AddCard / AddDeck / Stages / Orbs)

            // 3) loaded -> flips the login door open and builds the lobby menu
            Send(ns, new PacketWriter().WriteBool(false).WriteU16(0).Frame(Op.Loaded));
            Log("-> loaded (door open -> lobby)");
        }

        // --- helpers ---------------------------------------------------------
        static bool ReadExact(NetworkStream ns, byte[] buf, int off, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = ns.Read(buf, off + got, count - got);
                if (n <= 0) return false;
                got += n;
            }
            return true;
        }

        static readonly object _sendLock = new object();
        static void Send(NetworkStream ns, byte[] data)
        {
            lock (_sendLock) { ns.Write(data, 0, data.Length); ns.Flush(); }
        }

        static string Hex(byte[] b)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < b.Length && i < 64; i++) sb.Append(b[i].ToString("X2")).Append(' ');
            if (b.Length > 64) sb.Append("...");
            return sb.ToString().TrimEnd();
        }
    }
}
