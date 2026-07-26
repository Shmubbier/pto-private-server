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
                        case Op.Login:   HandleLogin(ns, payload, ref username); break;
                        case Op.AddDeck: HandleDeckSave(payload, username); break; // client->server deck save
                        case Op.Ping:    Send(ns, new PacketWriter().Frame(Op.Ping)); break; // echo for latency
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

            // 2) account data: full collection + saved decks + stages.
            SendAccountData(ns, username);

            // 3) loaded -> flips the login door open and builds the lobby menu
            Send(ns, new PacketWriter().WriteBool(false).WriteU16(0).Frame(Op.Loaded));
            Log("-> loaded (door open -> lobby)");
        }

        // Card DB has 232 entries (116 cards x {normal, holographic}); backs 0..10; lands 0..4;
        // stages 0..48. See docs/PROTOCOL.md. The client filters what is displayable, so granting
        // every id is safe and simply unlocks the whole collection.
        const int CardDbCount = 232;
        const int BackCount   = 11;
        const int LandCount   = 5;
        const int StageCount  = 49;
        const byte CardCopies = 3;

        static void SendAccountData(NetworkStream ns, string username)
        {
            var ms = new MemoryStream();

            // --- collection: every card (op 49: bool back, bool land, u16 id, u8 amount) ---
            for (int id = 0; id < CardDbCount; id++)
            {
                byte[] p = new PacketWriter()
                    .WriteBool(false).WriteBool(false).WriteU16((ushort)id).WriteU8(CardCopies)
                    .Frame(Op.AddCard);
                ms.Write(p, 0, p.Length);
            }
            // --- card backs (back=true) ---
            for (int id = 0; id < BackCount; id++)
            {
                byte[] p = new PacketWriter()
                    .WriteBool(true).WriteBool(false).WriteU16((ushort)id).WriteU8(1)
                    .Frame(Op.AddCard);
                ms.Write(p, 0, p.Length);
            }
            // --- lands (land=true) ---
            for (int id = 0; id < LandCount; id++)
            {
                byte[] p = new PacketWriter()
                    .WriteBool(false).WriteBool(true).WriteU16((ushort)id).WriteU8(1)
                    .Frame(Op.AddCard);
                ms.Write(p, 0, p.Length);
            }

            // --- saved decks (op 47 S->C: u8 id, str name, u16 back, u16 land, 31x u16 cards) ---
            Deck[] decks = DeckStore.Load(username);
            int deckCount = 0;
            foreach (Deck d in decks)
            {
                if (d == null) continue;
                var dw = new PacketWriter().WriteU8(d.Id).WriteString(d.Name)
                                           .WriteU16(d.Back).WriteU16(d.Land);
                for (int i = 0; i < 31; i++) dw.WriteU16(i < d.Cards.Length ? d.Cards[i] : (ushort)0);
                byte[] dp = dw.Frame(Op.AddDeck);
                ms.Write(dp, 0, dp.Length);
                deckCount++;
            }

            // --- stages (op 60: StageCount x (bool completed, bool unlocked)) ---
            var st = new PacketWriter();
            for (int i = 0; i < StageCount; i++) st.WriteBool(false).WriteBool(true); // all unlocked
            byte[] stagePkt = st.Frame(Op.Stages);
            ms.Write(stagePkt, 0, stagePkt.Length);

            byte[] blob = ms.ToArray();
            Send(ns, blob);
            Log("-> account data: " + CardDbCount + " cards + " + BackCount + " backs + " +
                LandCount + " lands + " + deckCount + " decks + " + StageCount + " stages (" +
                blob.Length + " bytes)");
        }

        // Client saving a deck (op 47 C->S: bool flag, str name, u8 id, u16 back, u16 land, 31x u16).
        static void HandleDeckSave(byte[] payload, string username)
        {
            if (string.IsNullOrEmpty(username)) { Log("! deck save with no user -- ignored"); return; }
            var r = new PacketReader(payload, 0);
            var d = new Deck();
            d.Flag = r.ReadBool();
            d.Name = r.ReadString();
            d.Id   = r.ReadU8();
            d.Back = r.ReadU16();
            d.Land = r.ReadU16();
            d.Cards = new ushort[31];
            int nonEmpty = 0;
            for (int i = 0; i < 31; i++) { d.Cards[i] = r.ReadU16(); if (d.Cards[i] != 0) nonEmpty++; }
            DeckStore.Save(username, d);
            Log("-> saved deck #" + d.Id + " '" + d.Name + "' (" + nonEmpty + " cards) for " + username);
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

    class Deck
    {
        public byte Id;
        public bool Flag;
        public string Name = "";
        public ushort Back, Land;
        public ushort[] Cards = new ushort[31];
    }

    // Per-user deck persistence. Decks live in memory and are mirrored to
    // data/<user>.decks so they survive server restarts. One line per deck:
    //   id|flag|back|land|c0,c1,...,c30|name
    static class DeckStore
    {
        const int MaxDecks = 12; // client has 12 deck slots (load_blank_decks)
        static readonly object _lock = new object();
        static readonly Dictionary<string, Deck[]> _mem = new Dictionary<string, Deck[]>();

        static string Dir { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data"); } }
        static string FileFor(string user)
        {
            var safe = new StringBuilder();
            foreach (char c in user.ToLowerInvariant())
                safe.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
            return Path.Combine(Dir, safe.ToString() + ".decks");
        }

        public static Deck[] Load(string user)
        {
            lock (_lock)
            {
                Deck[] cached;
                if (_mem.TryGetValue(user, out cached)) return cached;
                var arr = new Deck[MaxDecks];
                try
                {
                    string path = FileFor(user);
                    if (File.Exists(path))
                    {
                        foreach (string line in File.ReadAllLines(path))
                        {
                            if (line.Length == 0) continue;
                            string[] f = line.Split('|');
                            if (f.Length < 6) continue;
                            var d = new Deck();
                            d.Id   = byte.Parse(f[0]);
                            d.Flag = f[1] == "1";
                            d.Back = ushort.Parse(f[2]);
                            d.Land = ushort.Parse(f[3]);
                            string[] cs = f[4].Split(',');
                            for (int i = 0; i < 31 && i < cs.Length; i++)
                                ushort.TryParse(cs[i], out d.Cards[i]);
                            d.Name = f[5];
                            if (d.Id < MaxDecks) arr[d.Id] = d;
                        }
                    }
                }
                catch (Exception e) { Console.WriteLine("DeckStore load error: " + e.Message); }
                _mem[user] = arr;
                return arr;
            }
        }

        public static void Save(string user, Deck deck)
        {
            lock (_lock)
            {
                Deck[] arr = Load(user);
                if (deck.Id < MaxDecks) arr[deck.Id] = deck;
                try
                {
                    Directory.CreateDirectory(Dir);
                    var sb = new StringBuilder();
                    foreach (Deck d in arr)
                    {
                        if (d == null) continue;
                        var cards = new StringBuilder();
                        for (int i = 0; i < 31; i++) { if (i > 0) cards.Append(','); cards.Append(d.Cards[i]); }
                        sb.Append(d.Id).Append('|').Append(d.Flag ? '1' : '0').Append('|')
                          .Append(d.Back).Append('|').Append(d.Land).Append('|')
                          .Append(cards).Append('|').Append(d.Name).Append('\n');
                    }
                    File.WriteAllText(FileFor(user), sb.ToString());
                }
                catch (Exception e) { Console.WriteLine("DeckStore save error: " + e.Message); }
            }
        }
    }
}
