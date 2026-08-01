#define PACKET_LOG
// ---------------------------------------------------------------------------
//  PTO_C private server  --  battle engine v2 (full combat overhaul)
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
//  Combat implements the Pixel Tactics ruleset:
//    - 3x3 grid per player, leader at center (1,1)
//    - Waves: Vanguard(2) -> Flank(1) -> Rear(0)
//    - 2 actions per wave (recruit, attack, draw, restructure, clear corpse, order)
//    - Melee: frontmost alive unit in column; blocked by friendly in front
//    - Ranged: any target; blocked by Intercept
//    - Counter-attacks on melee hits
//    - Casualties checked at end of wave (damage >= life -> corpse)
//    - Round 1 ceasefire (no attacks)
//    - Rout: leader with lethal damage at end of wave = match end
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
        public const byte Login   = 46;
        public const byte AddDeck = 47;
        public const byte AddCard = 49;
        public const byte Loaded  = 48;
        public const byte Ping    = 52;
        public const byte Stages  = 60;
        public const byte Orbs    = 62;  // battle: set your own orb count (container_orbs, u8)
        public const byte OrbsGet = 63;  // battle: opponent's orb count (cosmetic)

        // matchmaking / battle
        public const byte Queue        = 0;
        public const byte CancelQueue  = 1;
        public const byte BattleStart  = 2;
        public const byte BattleData   = 4;
        public const byte BattleReady  = 20;
        public const byte BattleDetails= 50;
        public const byte Mulligan     = 37;
        public const byte TurnGet      = 14;
        public const byte Summon       = 10;
        public const byte SummonUnit   = 5;
        public const byte SummonUnitGet= 6;
        public const byte DrawCard     = 8;
        public const byte DrawCardGet  = 9;
        public const byte Attack       = 22;
        public const byte AttackOut    = 35;
        public const byte AttackGet    = 36;
        public const byte UpdateUnit   = 18;
        public const byte UpdateUnitGet= 19;
        public const byte UpdateBuff   = 38;  // battle: per-unit buff/state (atktype, incorp, adpx, ...) 20 bytes
        public const byte UpdateBuffGet= 39;  // battle: same, opponent view (yy mirrored client-side)
        public const byte BattleEnd    = 3;
        public const byte CanAction    = 40;
        public const byte Action       = 15;  // battle: set your remaining action count (u8); orders need >=1
        public const byte ActionGet    = 16;  // battle: opponent's remaining action count (cosmetic)
        public const byte HandCardRemove = 12;
        public const byte WaveUpdate   = 17;
        public const byte BattleAttackPhase = 21;
        public const byte BattleCasualties = 23;
        public const byte ClearCorpse  = 24;
        public const byte ClearCorpseGet = 25;
        public const byte Move         = 26;
        public const byte MoveGet      = 27;
        public const byte Order        = 28;  // client->server: cast an order / targeted spell
        public const byte DeckUpdate   = 54;
        public const byte CardHover    = 7;   // client->server: card hover (cosmetic)
        public const byte ArrowPos     = 34;  // client->server: arrow position (cosmetic)
        public const byte SlotHover    = 64;  // client->server: slot hover (cosmetic)
    }

    // Login response status bytes (first u8 of an Op.Login reply), from container_login.
    static class LoginResult
    {
        public const byte UsernameExists   = 0;
        public const byte NotRegistered    = 1;
        public const byte BadPassword      = 2;
        public const byte Success          = 3;
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
            if (_p < _b.Length) _p++;
            return s;
        }
    }

    // Unit ability flags (position-dependent per Pixel Tactics rules)
    [Flags]
    enum UnitAbility : byte
    {
        None        = 0,
        Intercept   = 1,  // blocks enemy ranged attacks in this column
        RangedAttack= 2,  // can target any enemy regardless of column
    }

    class Program
    {
        static int Port = 51338;
        const ushort ClientVersion = 72;
        static bool Verbose = true;
        static bool PacketLog = true;
        const int ActionsPerWave = 2;
        const int OpeningHand = 5;
        const byte OrbPool = 20;  // orbs granted to each player at the start of every turn (for orders)

        static readonly object _logLock = new object();
        static StreamWriter _logFile;
        internal static void Log(string msg)
        {
            string line = "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + msg;
            lock (_logLock)
            {
                Console.WriteLine(line);
                if (_logFile == null)
                {
                    try { _logFile = new StreamWriter("server_log.txt", true) { AutoFlush = true }; } catch { }
                }
                try { if (_logFile != null) _logFile.WriteLine(line); } catch { }
            }
        }


        static void Main(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--quiet") Verbose = false;
                else if (args[i] == "--port" && i + 1 < args.Length) int.TryParse(args[++i], out Port);
            }

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
                    if (!ReadExact(ns, header, 0, 7)) break;
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

                    if (PacketLog)
                    {
                        if (opcode == Op.UpdateUnit || opcode == Op.UpdateUnitGet)
                        {
                            // Decode activate field (byte 10 = index 7+10 = index 17)
                            string ann = "";
                            if (payload.Length >= 11) ann = " activate=" + payload[10];
                            Log("<- " + (opcode == Op.UpdateUnit ? "UpdateUnit" : "UpdateUnitGet")
                                + " (" + payload.Length + "B)" + ann + " " + Hex(payload));
                        }
                        else if (opcode != Op.Ping && opcode != Op.SlotHover && opcode != Op.ArrowPos && opcode != Op.CardHover)
                        {
                            Log("<- op=" + opcode + " len=" + length + " payload=" + Hex(payload));
                        }
                    }

                    switch (opcode)
                    {
                        case Op.Login:      HandleLogin(ns, payload, ref username); break;
                        case Op.AddDeck:    HandleDeckSave(payload, username); break;
                        case Op.Queue:      Matchmaker.Join(ns, username, payload.Length > 0 ? payload[0] : (byte)0); break;
                        case Op.CancelQueue:Matchmaker.Cancel(ns); break;
                        case Op.BattleReady:SendBattleSetup(ns); break;
                        case Op.Mulligan:   HandleMulligan(ns, payload); break;
                        case Op.Summon:     HandleSummon(ns, payload); break;
                        case Op.Attack:     HandleAttack(ns, payload); break;
                        case Op.Move:       HandleMove(ns, payload); break;
                        case Op.Order:      HandleOrder(ns, payload); break;
                        case Op.DrawCard:   HandleDraw(ns); break;
                        case Op.TurnGet:    HandleEndTurn(ns); break;
                        case Op.ClearCorpse:HandleClearCorpse(ns, payload); break;
                        case Op.Ping:       Send(ns, new PacketWriter().Frame(Op.Ping)); break;
                        case Op.CardHover:
                        case Op.ArrowPos:
                        case Op.SlotHover:   break; // cosmetic client->server packets, silently ignore
                        default:
                            if (Verbose) Log("   (unhandled opcode " + opcode + ")");
                            break;
                    }
                }
            }
            catch (Exception ex) { Log("Client " + who + " error: " + ex.Message); }
            finally
            {
                Matchmaker.Cancel(ns);
                ForgetBattle(ns);
                Log("Client disconnected: " + who + (username != null ? " (" + username + ")" : ""));
                try { client.Close(); } catch { }
            }
        }

        // ---- login / account data --------------------------------------------

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

            username = string.IsNullOrEmpty(user) ? "Player" : user;

            Send(ns, new PacketWriter().WriteU8(LoginResult.Success).WriteString(username).Frame(Op.Login));
            Log("-> login success as '" + username + "'");
            SendAccountData(ns, username);
            Send(ns, new PacketWriter().WriteBool(false).WriteU16(0).Frame(Op.Loaded));
            Log("-> loaded (door open -> lobby)");
        }

        const int CardDbCount = 232;
        const int BackCount   = 11;
        const int LandCount   = 5;
        const int StageCount  = 49;
        const byte CardCopies = 3;

        static void SendAccountData(NetworkStream ns, string username)
        {
            var ms = new MemoryStream();
            for (int id = 0; id < CardDbCount; id++)
            {
                byte[] p = new PacketWriter()
                    .WriteBool(false).WriteBool(false).WriteU16((ushort)id).WriteU8(CardCopies)
                    .Frame(Op.AddCard);
                ms.Write(p, 0, p.Length);
            }
            for (int id = 0; id < BackCount; id++)
            {
                byte[] p = new PacketWriter()
                    .WriteBool(true).WriteBool(false).WriteU16((ushort)id).WriteU8(1)
                    .Frame(Op.AddCard);
                ms.Write(p, 0, p.Length);
            }
            for (int id = 0; id < LandCount; id++)
            {
                byte[] p = new PacketWriter()
                    .WriteBool(false).WriteBool(true).WriteU16((ushort)id).WriteU8(1)
                    .Frame(Op.AddCard);
                ms.Write(p, 0, p.Length);
            }

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

            var st = new PacketWriter();
            for (int i = 0; i < StageCount; i++) st.WriteBool(false).WriteBool(true);
            byte[] stagePkt = st.Frame(Op.Stages);
            ms.Write(stagePkt, 0, stagePkt.Length);

            byte[] blob = ms.ToArray();
            Send(ns, blob);
            Log("-> account data: " + CardDbCount + " cards + " + BackCount + " backs + " +
                LandCount + " lands + " + deckCount + " decks + " + StageCount + " stages (" +
                blob.Length + " bytes)");
        }

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

        // ---- card stats (auto-generated from the client's card_init) -----------

        static readonly int[] CardAtk = new int[]{ 0,2,4,4,6,8,4,5,5,4,4,2,0,0,5,5,4,4,0,1,4,5,4,3,2,1,4,6,3,2,2,5,1,3,4,3,1,1,2,3,4,3,1,3,2,0,2,1,4,2,2,3,3,2,3,4,2,2,3,3,1,1,1,2,3,2,3,0,6,4,5,2,6,3,1,4,5,1,5,3,3,3,3,3,3,3,3,0,1,2,1,1,2,3,4,0,2,5,3,4,4,0,0,0,0,4,0,3,1,2,1,4,3,4,3,3 };
        static readonly int[] CardLife = new int[]{ 0,17,19,20,17,15,23,18,18,18,19,19,19,21,21,24,17,19,19,17,23,19,18,20,20,22,9,4,7,2,5,5,5,8,6,8,5,8,5,8,7,8,8,4,6,1,6,7,6,7,4,3,5,5,6,4,3,7,6,4,5,7,4,5,7,5,8,5,4,4,20,24,19,19,16,23,16,18,18,24,8,6,6,8,7,6,6,7,4,6,5,5,6,4,4,4,5,4,6,23,22,0,0,0,4,3,6,16,16,6,6,20,7,4,4,5 };
        static int AtkOf(ushort dbId)  { int c = dbId / 2; return (c >= 0 && c < CardAtk.Length)  ? CardAtk[c]  : 1; }
        static int LifeOf(ushort dbId) { int c = dbId / 2; return (c >= 0 && c < CardLife.Length) ? CardLife[c] : 10; }

        // ---- ability lookup (position-dependent per PT rules) -----------------
        // Returns the abilities a card has when placed in the given wave.
        // Wave: 2=Vanguard, 1=Flank, 0=Rear.
        // TODO: populate from data.win reverse-engineering for full accuracy.
        static UnitAbility GetUnitAbilities(ushort card, int wave)
        {
            int id = card / 2;
            // Heuristic based on common PT1 archetypes.
            // Vanguard: defensive cards tend to get intercept
            if (wave == 2)
            {
                // Cards with high life relative to attack tend to be interceptors
                int atk = AtkOf(card); int life = LifeOf(card);
                if (life >= 20 && atk <= 3) return UnitAbility.Intercept;
            }
            // Rear: support/ranged cards
            if (wave == 0)
            {
                // Cards with moderate stats and low life tend to have ranged attack
                int atk = AtkOf(card); int life = LifeOf(card);
                if (atk >= 3 && life >= 16 && life <= 20) return UnitAbility.RangedAttack;
            }
            return UnitAbility.None;
        }

        // Ranged-attack per wave, indexed by REAL card id (card/2). Bit W set => ranged at wave W
        // (wave: 2=Vanguard, 1=Flank, 0=Rear). Derived from the client card_init descriptions
        // ("R.Attack" at a wave). Used for BOTH the client atktype (update_buff) and the server's
        // no-counter/ranged-targeting, so the two always agree.
        static readonly byte[] RangedWaves = BuildRangedWaves();
        static byte[] BuildRangedWaves()
        {
            var a = new byte[128];
            a[27] = 0x1; // Dragon Mage  - Rear R.Attack
            a[31] = 0x7; // Gunner       - all waves R.Attack
            a[41] = 0x7; // Planestalker - all waves R.Attack
            a[43] = 0x2; // Pyromancer   - Flank R.Attack
            a[52] = 0x2; // Fire Elem    - Flank R.Attack
            a[54] = 0x2; // Air Elem     - Flank R.Attack
            a[55] = 0x2; // Dark Elem    - Flank R.Attack
            return a;
        }
        static bool IsRangedAtWave(ushort card, int wave)
        {
            int c = card / 2;
            if (c < 0 || c >= RangedWaves.Length || wave < 0 || wave > 2) return false;
            return (RangedWaves[c] & (1 << wave)) != 0;
        }

        // Build a 20-byte update_buff payload for a unit at (x, y). All fields are 1 byte. s8 fields
        // default to -1 (sent as 255). We only vary atktype (ranged), inter(cept), and adpx (= grid_x,
        // which drives the client's wave-spell selection); everything else is the no-buff default.
        static PacketWriter BuildBuff(int x, int y, byte atktype, bool intercept)
        {
            return new PacketWriter()
                .WriteU8((byte)x).WriteU8((byte)y).WriteU8(atktype)
                .WriteBool(intercept)     // inter
                .WriteU8(255)             // ongo (s8 -1)
                .WriteBool(false)         // covered
                .WriteU8(255).WriteU8(255)// coveredx, coveredy (s8 -1)
                .WriteBool(false)         // incorp (0 -> no crash on return_noone_infront)
                .WriteBool(false)         // shield
                .WriteBool(false)         // silence
                .WriteBool(false)         // rev
                .WriteBool(false)         // cnter
                .WriteBool(false)         // immort
                .WriteBool(false)         // deathpro
                .WriteU8((byte)x)         // adpx = grid_x (wave) -> spellid follows the unit on move
                .WriteBool(true)          // can_attack
                .WriteU8(99)              // m_actions
                .WriteU8(0)               // actions
                .WriteBool(false);        // dec
        }

        // Send a unit's buff/state to both clients (op38 to owner raw-Y, op39 to opponent Y-mirrored).
        static void SendUnitBuff(BattleSlot ownerSlot, BattleSlot oppSlot, int x, int y, BUnit unit)
        {
            if (unit == null || unit.IsCorpse) return;
            byte atktype = (byte)(IsRangedAtWave(unit.Card, x) ? 1 : 0);
            bool intercept = (unit.Abilities & UnitAbility.Intercept) != 0;
            Send(ownerSlot.Me.Ns, BuildBuff(x, y, atktype, intercept).Frame(Op.UpdateBuff));
            if (oppSlot != null) Send(oppSlot.Me.Ns, BuildBuff(x, y, atktype, intercept).Frame(Op.UpdateBuffGet));
        }

        static int GetUnitArmor(ushort card, int wave)
        {
            int id = card / 2;
            if (wave == 2)
            {
                int life = LifeOf(card);
                if (life >= 22) return 1; // heavy vanguard units get 1 armor
            }
            return 0;
        }

        static int GetUnitStrength(ushort card, int wave)
        {
            return 0; // base implementation: no strength bonuses
        }

        // ---- matchmaking / battle bootstrap -----------------------------------

        static readonly Random _rng = new Random();

        class BattleSlot
        {
            public Waiting Me;
            public Waiting Opp;
            public bool FirstPlayer;
            public bool Sent;
            public bool Mulliganed;
            public List<ushort> Hand = new List<ushort>();
            public Battle Battle;
            public int P; // index into Battle.P[]
        }

        internal class Battle
        {
            public PlayerState[] P = new PlayerState[] { new PlayerState(), new PlayerState() };
            public bool Over;
            public bool Started;
            public int Wave = 2;   // 2=Vanguard, 1=Flank, 0=Rear
            public int Round = 1;  // ceasefire during round 1
            public int First = 0;  // absolute player index holding first-player token
            public int Active = 0; // absolute player index whose turn it is
        }

        internal class PlayerState
        {
            public Dictionary<int, BUnit> Units = new Dictionary<int, BUnit>();
            public int LeaderLife = 20;
            public int LeaderMax = 20;
            public ushort LeaderCard;
            public List<ushort> Deck = new List<ushort>(); // draw pile (hero cards remaining)
            public int ActionsRemaining;
        }

        internal class BUnit
        {
            public ushort Card;
            public int Atk;
            public int Max;           // max life (total)
            public int Damage;        // accumulated damage (for wave-end casualties)
            public int Armor;         // flat damage reduction
            public int Strength;      // bonus attack damage
            public UnitAbility Abilities;
            public bool IsCorpse;     // dead unit occupying space
            public bool RecruitedThisWave;  // cannot attack on the turn recruited
            public bool HasAttackedThisWave; // can only attack once per wave
        }

        static int Key(int x, int y) { return x * 10 + y; }
        static readonly object _battleLock = new object();
        static readonly Dictionary<NetworkStream, BattleSlot> _battles = new Dictionary<NetworkStream, BattleSlot>();

        internal static void StartBattle(Waiting a, Waiting b, int battleId)
        {
            Log("MATCH: " + a.User + " vs " + b.User + " (battle " + battleId + ")");
            var battle = new Battle();
            lock (_battleLock)
            {
                _battles[a.Ns] = new BattleSlot { Me = a, Opp = b, FirstPlayer = false, Battle = battle, P = 1 };
                _battles[b.Ns] = new BattleSlot { Me = b, Opp = a, FirstPlayer = true,  Battle = battle, P = 0 };
            }
            // __other_player is always 1 for both players:
            // grid[0] = self, grid[1] = opponent. The reference server sends 1 to both.
            byte[] startA = new PacketWriter().WriteU16(1).WriteU16((ushort)battleId).Frame(Op.BattleStart);
            byte[] startB = new PacketWriter().WriteU16(1).WriteU16((ushort)battleId).Frame(Op.BattleStart);
            Send(a.Ns, startA);
            Send(b.Ns, startB);
        }

        internal static void ForgetBattle(NetworkStream ns)
        {
            BattleSlot slot;
            lock (_battleLock)
            {
                if (!_battles.TryGetValue(ns, out slot)) return;
                _battles.Remove(ns);
                if (slot.Opp != null) _battles.Remove(slot.Opp.Ns);
            }
            if (slot.Opp != null && slot.Battle != null && !slot.Battle.Over)
            {
                slot.Battle.Over = true;
                try { Send(slot.Opp.Ns, new PacketWriter().WriteBool(true).WriteU16(0).Frame(Op.BattleEnd)); } catch { }
                Log("BATTLE END: " + slot.Me.User + " disconnected -> " + slot.Opp.User + " wins");
            }
        }

        // ---- battle setup + mulligan ------------------------------------------

        static void SendBattleSetup(NetworkStream ns)
        {
            BattleSlot slot;
            lock (_battleLock) { if (!_battles.TryGetValue(ns, out slot) || slot.Sent) return; slot.Sent = true; }

            Waiting me = slot.Me, opp = slot.Opp;
            Deck md = DeckStore.Load(me.User)[me.DeckId];
            Deck od = DeckStore.Load(opp.User)[opp.DeckId];

            // Initialize leader stats
            if (slot.Battle != null && md != null)
            {
                ushort leader = md.Cards.Length > 0 ? md.Cards[0] : (ushort)0;
                var ps = slot.Battle.P[slot.P];
                ps.LeaderCard = leader;
                ps.LeaderMax = ps.LeaderLife = Math.Max(1, LifeOf(leader));
            }
            ushort myBack = md != null ? md.Back : (ushort)0, myLand = md != null ? md.Land : (ushort)0;
            ushort opBack = od != null ? od.Back : (ushort)0, opLand = od != null ? od.Land : (ushort)0;

            var ms = new MemoryStream();

            byte[] d1 = new PacketWriter().WriteBool(true).WriteU16(myBack).WriteU16(myLand)
                .WriteString(me.User).WriteBool(false).WriteU16(0).WriteBool(false).WriteBool(true)
                .Frame(Op.BattleDetails);
            byte[] d2 = new PacketWriter().WriteBool(false).WriteU16(opBack).WriteU16(opLand)
                .WriteString(opp.User).WriteBool(false).WriteU16(0).WriteBool(false).WriteBool(true)
                .Frame(Op.BattleDetails);
            ms.Write(d1, 0, d1.Length);
            ms.Write(d2, 0, d2.Length);

            // Deal opening hand and initialize deck
            ushort[] hand = InitializeDeckAndDrawHand(md, slot);
            // The client draws the opening hand by front-inserting each card (container_battle_data ->
            // anim_draw_card_self: ds_list_insert(hand, 0, ...)), so its displayed hand is the REVERSE
            // of the order we send. Store the reverse here so the hand index the client sends on summon
            // maps to the same card. (Draws below likewise Insert(0) to match.)
            slot.Hand = new List<ushort>(hand);
            slot.Hand.Reverse();
            var dw = new PacketWriter().WriteU16((ushort)(slot.FirstPlayer ? 0 : 1)).WriteU8((byte)hand.Length);
            foreach (ushort c in hand) dw.WriteU16(c);
            byte[] d3 = dw.Frame(Op.BattleData);
            ms.Write(d3, 0, d3.Length);

            Send(ns, ms.ToArray());
            Log("-> battle setup to " + me.User + " (firstPlayer=" + slot.FirstPlayer + ", hand=[" +
                string.Join(",", Array.ConvertAll(hand, x => x.ToString())) + "])");

            // Send initial TurnGet (show_msg=false) to trigger black screen lighten and start action queue
            // This allows the mulligan UI to appear
            // Client needs this to set next_que=1 and process the draw card actions
            // Based on SendTurn(), the first player receives player=0, second player receives player=1
            byte[] turnGetPrime = new PacketWriter().WriteU16((ushort)(slot.FirstPlayer ? 0 : 1)).WriteBool(false).Frame(Op.TurnGet);
            Send(ns, turnGetPrime);
            Log("-> sent initial TurnGet (show_msg=false) to " + me.User);

            // Spawn both leaders at (1,1) — but ONLY once BOTH clients have entered the battle room
            // (both did SendBattleSetup). SendLeaderSummon sends a SummonUnitGet (op6) to the
            // opponent, which crashes a client whose obj_battle_control doesn't exist yet. The
            // second client to finish setup triggers the leader summons for both.
            BattleSlot oppSlot = null;
            lock (_battleLock) { if (slot.Opp != null) _battles.TryGetValue(slot.Opp.Ns, out oppSlot); }
            if (slot.Battle != null && oppSlot != null && oppSlot.Sent)
            {
                SendLeaderSummon(slot, slot.Battle);
                SendLeaderSummon(oppSlot, slot.Battle);
                Log("-> both clients in battle; spawned leaders for " + slot.Me.User + " and " + oppSlot.Me.User);
            }
        }

        // Shuffle hero cards (excluding leader at slot 0), draw opening hand,

        // Shuffle hero cards (excluding leader at slot 0), draw opening hand,
        // put the rest into PlayerState.Deck.
        static ushort[] InitializeDeckAndDrawHand(Deck d, BattleSlot slot)
        {
            var heroes = new List<ushort>();
            if (d != null) for (int i = 1; i < d.Cards.Length; i++) if (d.Cards[i] != 0) heroes.Add(d.Cards[i]);
            lock (_rng) for (int i = heroes.Count - 1; i > 0; i--)
            {
                int j = _rng.Next(i + 1);
                var tmp = heroes[i]; heroes[i] = heroes[j]; heroes[j] = tmp;
            }
            int n = Math.Min(OpeningHand, heroes.Count);
            ushort[] hand = heroes.GetRange(0, n).ToArray();
            // Remaining cards go to the draw pile
            if (slot.Battle != null)
                slot.Battle.P[slot.P].Deck = heroes.GetRange(n, heroes.Count - n);
            return hand;
        }

        // Mulligan (op 37): 4x bool (which of the first 4 hand cards to redraw).
        static void HandleMulligan(NetworkStream ns, byte[] payload)
        {
            BattleSlot mine, theirs = null;
            lock (_battleLock)
            {
                if (!_battles.TryGetValue(ns, out mine)) return;
                if (mine.Opp != null) _battles.TryGetValue(mine.Opp.Ns, out theirs);
            }

            // Process redraws
            var r = new PacketReader(payload, 0);
            bool[] redraw = new bool[4];
            for (int i = 0; i < 4 && i < payload.Length; i++) redraw[i] = r.ReadBool();

            // NOTE: mulligan redraw is intentionally DISABLED (keep all cards). Redrawing requires
            // the server to tell the client to delete the mulliganed card (anim_mull_back) and draw
            // its replacement; without that response the client keeps showing the OLD card while the
            // server holds the NEW one, so the hand index the client sends on summon maps to the wrong
            // card. Until that response protocol is implemented, we leave the hand untouched so the
            // server's hand and the client's displayed hand stay identical.
            int marked = 0;
            for (int i = 0; i < 4; i++) if (redraw[i]) marked++;
            if (marked > 0)
                Log("MULLIGAN " + mine.Me.User + ": " + marked + " card(s) marked but redraw is disabled (kept in sync)");

            mine.Mulliganed = true;
            Log("MULLIGAN done: " + mine.Me.User);

            if (theirs != null && theirs.Mulliganed)
            {
                Log("Both mulligans in -> starting turn 1 (" +
                    (mine.FirstPlayer ? mine.Me.User : theirs.Me.User) + " first)");
                BattleSlot first = mine.FirstPlayer ? mine : theirs;
                BattleSlot second = mine.FirstPlayer ? theirs : mine;
                Battle b = mine.Battle;
                b.Wave = 2; b.Round = 1; b.First = first.P; b.Active = first.P;
                b.P[b.Active].ActionsRemaining = ActionsPerWave;
                b.Started = true;

                // Turn 1 ONLY: send a "prime" turn_get first. anim_turn's body (which DESTROYS
                // obj_mulligan) runs only on the 2nd turn_get (start is 0 on the 1st, set to 1).
                // If obj_mulligan is never destroyed it freezes obj_unit.mouse_over, so units can
                // never be clicked to attack/move. SendTurn below sends the real (2nd) turn_get.
                Send(first.Me.Ns,  new PacketWriter().WriteU16(0).WriteBool(false).Frame(Op.TurnGet));
                Send(second.Me.Ns, new PacketWriter().WriteU16(1).WriteBool(false).Frame(Op.TurnGet));
                SendTurn(first, second, b.First);
            }
        }

        static void GrantAction(BattleSlot slot)
        {
            Send(slot.Me.Ns, new PacketWriter().Frame(Op.CanAction));
            // Also push the remaining action count (global.__player_actions). The order slot is only
            // valid when this is >= 1 (summon_ui_script), so without it orders can never be played.
            int actions = (slot.Battle != null) ? slot.Battle.P[slot.P].ActionsRemaining : 0;
            Send(slot.Me.Ns, new PacketWriter().WriteU8((byte)Math.Max(0, Math.Min(255, actions))).Frame(Op.Action));
        }

        // ---- draw a card (op 8) ----------------------------------------------
        // Client sends op 8 (empty) when the deck is clicked and sets can_do_action=0. If we don't
        // respond, the player is frozen. Draw the top card into the hand, tell the actor (op 8) and
        // the opponent (op 9), then consume/restore the action. Every path grants so we never stick.
        static void HandleDraw(NetworkStream ns)
        {
            BattleSlot mine, theirs = null;
            lock (_battleLock)
            {
                if (!_battles.TryGetValue(ns, out mine)) return;
                if (mine.Opp != null) _battles.TryGetValue(mine.Opp.Ns, out theirs);
            }
            Battle b = mine.Battle;
            if (b == null || b.Over || !b.Started) { GrantAction(mine); return; }
            if (mine.P != b.Active) { Log("DRAW from non-active player (ignored)"); GrantAction(mine); return; }
            PlayerState ps = b.P[mine.P];
            if (ps.ActionsRemaining <= 0) { Log("DRAW rejected: no actions remaining"); GrantAction(mine); return; }
            if (ps.Deck == null || ps.Deck.Count == 0) { Log("DRAW: deck empty for " + mine.Me.User); GrantAction(mine); return; }

            ushort card = ps.Deck[0];
            ps.Deck.RemoveAt(0);
            mine.Hand.Insert(0, card); // client draws to the front of its hand (anim_draw_card_self); mirror it
            byte deckLeft = (byte)Math.Min(255, ps.Deck.Count);
            byte handSize = (byte)Math.Min(255, mine.Hand.Count);
            Log("DRAW " + mine.Me.User + ": card " + card + " (deck now " + ps.Deck.Count + ", hand " + mine.Hand.Count + ")");

            // op 8 to actor: bool scry, u8 deckSize, u16 card, bool phantom, u8 fromOrder, bool gridSelf, u8 x, u8 y
            Send(mine.Me.Ns, new PacketWriter()
                .WriteBool(false).WriteU8(deckLeft).WriteU16(card).WriteBool(false)
                .WriteU8(0).WriteBool(false).WriteU8(0).WriteU8(0).Frame(Op.DrawCard));
            // op 9 to opponent: bool scry, u8 deckSize, u8 handSize, u8 fromOrder, bool gridSelf, u8 x, u8 y
            if (theirs != null)
                Send(theirs.Me.Ns, new PacketWriter()
                    .WriteBool(false).WriteU8(deckLeft).WriteU8(handSize).WriteU8(0)
                    .WriteBool(false).WriteU8(0).WriteU8(0).Frame(Op.DrawCardGet));

            ConsumeAction(mine, theirs, b);
        }

        // ---- order / targeted spell (op 28) -----------------------------------
        // Client sends: u8 card, bool grid, u8 x, u8 y, u16 cardId (see battle_order_attack).
        // cardId == 0 is a cancel (player clicked empty). Effects are server-authoritative: we
        // mutate the data model, then SyncUnitStates pushes the new life/state to both clients
        // (no dedicated cast animation yet — the life bars just update). Orders/spells we haven't
        // implemented fall through to a graceful no-op (action refunded) so nothing freezes.
        enum OrderKind { None, DamageSingle, DamageRow, DamageColumn, DamageBlast, DamageAll, HealSingle, HealAll, HealLeader }

        struct OrderEffect { public OrderKind Kind; public int Amount; }

        // Effects for the tester Arena deck (card id = REAL*2). Damage orders hit the enemy grid,
        // heals hit the caster's grid. Unimplemented cards return None (graceful no-op).
        static OrderEffect OrderOf(ushort cardId)
        {
            switch (cardId / 2)
            {
                case 26: return new OrderEffect { Kind = OrderKind.DamageSingle, Amount = 5 }; // Fighter: Thunder 5
                case 28: return new OrderEffect { Kind = OrderKind.DamageSingle, Amount = 8 }; // Berserker: Bombard 8
                case 30: return new OrderEffect { Kind = OrderKind.DamageSingle, Amount = 3 }; // Alchemist: Poison 3 (simplified)
                case 31: return new OrderEffect { Kind = OrderKind.DamageRow,    Amount = 5 }; // Gunner: Bombard Row 5
                case 32: return new OrderEffect { Kind = OrderKind.HealAll,      Amount = 4 }; // Healer: Cure All 4
                case 35: return new OrderEffect { Kind = OrderKind.DamageBlast,  Amount = 4 }; // Knight: Blast 4
                case 41: return new OrderEffect { Kind = OrderKind.DamageColumn, Amount = 5 }; // Planestalker: Bombard Column 5
                case 43: return new OrderEffect { Kind = OrderKind.DamageRow,    Amount = 5 }; // Pyromancer: Fire 5
                case 52: return new OrderEffect { Kind = OrderKind.DamageRow,    Amount = 4 }; // Fire Elemental: Fire 4
                case 53: return new OrderEffect { Kind = OrderKind.HealSingle,   Amount = 5 }; // Water Elemental: Cure 5
                case 56: return new OrderEffect { Kind = OrderKind.DamageSingle, Amount = 4 }; // Lightning Elemental: Thunder 4
                default: return new OrderEffect { Kind = OrderKind.None };
            }
        }

        static void HandleOrder(NetworkStream ns, byte[] payload)
        {
            BattleSlot mine, theirs = null;
            lock (_battleLock)
            {
                if (!_battles.TryGetValue(ns, out mine)) return;
                if (mine.Opp != null) _battles.TryGetValue(mine.Opp.Ns, out theirs);
            }

            Battle b = mine.Battle;
            if (b == null || b.Over || !b.Started) { GrantAction(mine); return; }

            var r = new PacketReader(payload, 0);
            byte card = r.ReadU8();
            bool grid = r.ReadBool();
            byte x = r.ReadU8(), y = r.ReadU8();
            ushort cardId = r.ReadU16();

            // cardId 0 = the player cancelled (clicked empty). Restore the action, do nothing.
            if (cardId == 0) { Log("ORDER cancelled by " + mine.Me.User); GrantAction(mine); return; }
            if (mine.P != b.Active) { Log("ORDER from non-active player (ignored)"); GrantAction(mine); return; }

            PlayerState ps = b.P[mine.P];
            if (ps.ActionsRemaining <= 0) { Log("ORDER rejected: no actions remaining"); GrantAction(mine); return; }

            OrderEffect eff = OrderOf(cardId);
            Log("ORDER " + mine.Me.User + ": card " + cardId + " kind=" + eff.Kind + " amt=" + eff.Amount +
                " grid=" + grid + " target(" + x + "," + y + ")");
            if (eff.Kind == OrderKind.None)
            {
                Log("  -> order effect not implemented yet; no-op (action refunded)");
                GrantAction(mine);
                return;
            }

            // Playing a hand card as an order discards it. `card` is the hand index the client sent;
            // remove it (and tell the client) only if that slot really holds this card, so board-unit
            // orders (where the card isn't in hand) are left alone.
            if (card < mine.Hand.Count && mine.Hand[card] == cardId)
            {
                mine.Hand.RemoveAt(card);
                Send(mine.Me.Ns, new PacketWriter().WriteU8(card).WriteU16(cardId).WriteBool(true).Frame(Op.HandCardRemove));
                Log("  -> discarded order card " + cardId + " from hand[" + card + "]");
            }

            ResolveEffect(eff, mine, theirs, b, x, y);
        }

        // Apply an order/spell effect and push results to both clients. (x,y) are the raw client
        // target coords: damage effects hit the enemy grid (gy mirrored 2-y), heals hit the caster's
        // grid (gy = y). Shared by HandleOrder (op28) and the isSpell path of HandleAttack (op22).
        static void ResolveEffect(OrderEffect eff, BattleSlot mine, BattleSlot theirs, Battle b, byte x, byte y)
        {
            PlayerState ps = b.P[mine.P];
            PlayerState opPs = b.P[1 - mine.P];
            int gx = x;
            int enemyGy = y;       // opponent slot grid_y already equals the server lane (no mirror)
            int selfGy = y;

            switch (eff.Kind)
            {
                case OrderKind.DamageSingle:
                    DamageEnemyAt(opPs, gx, enemyGy, eff.Amount);
                    break;
                case OrderKind.DamageRow:      // "in a row" = fixed gy, all gx (see order_fire_4)
                    for (int cx = 0; cx <= 2; cx++) DamageEnemyAt(opPs, cx, enemyGy, eff.Amount);
                    break;
                case OrderKind.DamageColumn:   // fixed gx, all gy
                    for (int cy = 0; cy <= 2; cy++) DamageEnemyAt(opPs, gx, cy, eff.Amount);
                    break;
                case OrderKind.DamageBlast:    // target + its column-neighbours
                    DamageEnemyAt(opPs, gx, enemyGy, eff.Amount);
                    DamageEnemyAt(opPs, gx, enemyGy - 1, eff.Amount);
                    DamageEnemyAt(opPs, gx, enemyGy + 1, eff.Amount);
                    break;
                case OrderKind.DamageAll:
                    foreach (var kv in opPs.Units) { BUnit u = kv.Value; if (u != null && !u.IsCorpse) u.Damage += Math.Max(1, eff.Amount - u.Armor); }
                    break;
                case OrderKind.HealSingle:
                    HealOwnAt(ps, gx, selfGy, eff.Amount);
                    break;
                case OrderKind.HealAll:
                    foreach (var kv in ps.Units) { BUnit u = kv.Value; if (u != null && !u.IsCorpse) u.Damage = Math.Max(0, u.Damage - eff.Amount); }
                    ps.LeaderLife = Math.Min(ps.LeaderMax, ps.LeaderLife + eff.Amount);
                    break;
                case OrderKind.HealLeader:
                    ps.LeaderLife = Math.Min(ps.LeaderMax, ps.LeaderLife + eff.Amount);
                    break;
            }

            // A single-target damage can hit the enemy leader at (1,1) — check for a win.
            if (opPs.LeaderLife <= 0)
            {
                b.Over = true;
                Log("BATTLE END: " + mine.Me.User + " wins (order/spell lethal)");
                Send(mine.Me.Ns, new PacketWriter().WriteBool(true).WriteU16(0).Frame(Op.BattleEnd));
                if (theirs != null) Send(theirs.Me.Ns, new PacketWriter().WriteBool(false).WriteU16(0).Frame(Op.BattleEnd));
                return;
            }

            // Push updated life/state to both clients, then spend the action.
            BattleSlot p0slot = mine.P == 0 ? mine : theirs;
            BattleSlot p1slot = mine.P == 0 ? theirs : mine;
            if (p0slot != null && p1slot != null) SyncUnitStates(p0slot, p1slot, b);
            ConsumeAction(mine, theirs, b);
        }

        // Wave-position spells (v/f/r) for the deck. wave: 2=Vanguard,1=Flank,0=Rear (=caster grid_x).
        static OrderEffect SpellOf(ushort cardId, int wave)
        {
            switch (cardId / 2)
            {
                case 28: if (wave == 0) return new OrderEffect { Kind = OrderKind.DamageSingle, Amount = 5 }; break; // Berserker R: Bombard 5
                case 29: if (wave == 2) return new OrderEffect { Kind = OrderKind.DamageSingle, Amount = 4 };        // Assassin V: Backstab 4
                         if (wave == 0) return new OrderEffect { Kind = OrderKind.DamageSingle, Amount = 2 }; break; // Assassin R: Backstab 2
                case 30: if (wave == 0) return new OrderEffect { Kind = OrderKind.DamageSingle, Amount = 1 }; break; // Alchemist R: Poison 1
                case 32: if (wave == 1) return new OrderEffect { Kind = OrderKind.HealSingle,   Amount = 4 };        // Healer F: Cure 4
                         if (wave == 0) return new OrderEffect { Kind = OrderKind.HealAll,      Amount = 2 }; break; // Healer R: Cure All 2
                case 36: if (wave == 2) return new OrderEffect { Kind = OrderKind.HealSingle,   Amount = 4 }; break; // Mascot V: Cure 4
                case 37: if (wave == 2) return new OrderEffect { Kind = OrderKind.HealSingle,   Amount = 4 }; break; // Mystic V: Cure 4
                case 42: if (wave == 2) return new OrderEffect { Kind = OrderKind.HealSingle,   Amount = 4 };        // Priestess V: Cure 4
                         if (wave == 1) return new OrderEffect { Kind = OrderKind.HealLeader,   Amount = 2 }; break; // Priestess F: Cure Leader 2
                case 43: if (wave == 2) return new OrderEffect { Kind = OrderKind.DamageBlast,  Amount = 3 };        // Pyromancer V: Blast 3
                         if (wave == 0) return new OrderEffect { Kind = OrderKind.DamageRow,    Amount = 3 }; break; // Pyromancer R: Fire 3
                case 52: if (wave == 0) return new OrderEffect { Kind = OrderKind.DamageRow,    Amount = 2 }; break; // Fire Elem R: Fire 2
                case 53: if (wave == 0) return new OrderEffect { Kind = OrderKind.HealSingle,   Amount = 3 }; break; // Water Elem R: Cure 3
                case 56: if (wave == 1) return new OrderEffect { Kind = OrderKind.DamageSingle, Amount = 2 }; break; // Lightning Elem F: Thunder 2
            }
            return new OrderEffect { Kind = OrderKind.None };
        }

        // Apply order damage to an enemy unit (or the enemy leader at 1,1). Unit death is resolved
        // at wave end by ProcessCasualties, exactly like attack damage.
        static void DamageEnemyAt(PlayerState opPs, int gx, int gy, int dmg)
        {
            if (gx < 0 || gx > 2 || gy < 0 || gy > 2) return;
            if (gx == 1 && gy == 1) { opPs.LeaderLife -= dmg; Log("    damage -> enemy LEADER -" + dmg + " (life " + opPs.LeaderLife + ")"); return; }
            BUnit u;
            if (opPs.Units.TryGetValue(Key(gx, gy), out u) && u != null && !u.IsCorpse)
            {
                int actual = Math.Max(1, dmg - u.Armor);
                u.Damage += actual;
                Log("    damage -> enemy (" + gx + "," + gy + ") card " + u.Card + " -" + actual + " (now " + Math.Max(0, u.Max - u.Damage) + "/" + u.Max + ")");
            }
            else Log("    damage -> no enemy unit at (" + gx + "," + gy + ")");
        }

        static void HealOwnAt(PlayerState ps, int gx, int gy, int amount)
        {
            if (gx < 0 || gx > 2 || gy < 0 || gy > 2) return;
            if (gx == 1 && gy == 1) { ps.LeaderLife = Math.Min(ps.LeaderMax, ps.LeaderLife + amount); Log("    heal -> own LEADER +" + amount + " (life " + ps.LeaderLife + ")"); return; }
            BUnit u;
            if (ps.Units.TryGetValue(Key(gx, gy), out u) && u != null && !u.IsCorpse)
            {
                int before = Math.Max(0, u.Max - u.Damage);
                u.Damage = Math.Max(0, u.Damage - amount);
                int after = Math.Max(0, u.Max - u.Damage);
                Log("    heal -> own (" + gx + "," + gy + ") card " + u.Card + " " + before + "->" + after + "/" + u.Max + (before == u.Max ? " (was already full — nothing to heal)" : ""));
            }
            else Log("    heal -> no own unit at (" + gx + "," + gy + ")");
        }

        // ---- summon (op 10) ---------------------------------------------------

        static void HandleSummon(NetworkStream ns, byte[] payload)
        {
            BattleSlot mine, theirs = null;
            lock (_battleLock)
            {
                if (!_battles.TryGetValue(ns, out mine)) return;
                if (mine.Opp != null) _battles.TryGetValue(mine.Opp.Ns, out theirs);
            }

            Battle b = mine.Battle;
            if (b == null || b.Over || !b.Started) return;
            if (mine.P != b.Active) { Log("SUMMON from non-active player (ignored)"); GrantAction(mine); return; }
            PlayerState ps = b.P[mine.P];
            if (ps.ActionsRemaining <= 0) { Log("SUMMON rejected: no actions remaining for " + mine.Me.User); GrantAction(mine); return; }

            var r = new PacketReader(payload, 0);
            byte who = r.ReadU8();
            byte gx = r.ReadU8();
            byte gy = r.ReadU8();
            byte handIndex = r.ReadU8();

            // Validate hand index
            if (handIndex >= mine.Hand.Count) { Log("SUMMON rejected: invalid hand index " + handIndex); GrantAction(mine); return; }

            // Validate target cell is in current wave (gx = wave position: 0=Rear, 1=Flank, 2=Vanguard)
            if (gx != b.Wave) { Log("SUMMON rejected: target x=" + gx + " not in wave " + b.Wave); GrantAction(mine); return; }

            // Validate target cell is empty (no unit, no corpse)
            int key = Key(gx, gy);
            if (ps.Units.ContainsKey(key)) { Log("SUMMON rejected: cell (" + gx + "," + gy + ") occupied"); GrantAction(mine); return; }

            // Validate leader cell is not targeted (leader is always at 1,1 = Flank center)
            if (gx == 1 && gy == 1) { Log("SUMMON rejected: cannot place on leader cell"); GrantAction(mine); return; }

            ushort card = mine.Hand[handIndex];
            Log("SUMMON " + mine.Me.User + ": hand[" + handIndex + "]=card " + card + " -> (" + gx + "," + gy + ")");

            // Create unit with stats and abilities
            var unit = new BUnit
            {
                Card = card,
                Atk = AtkOf(card) + GetUnitStrength(card, gy),
                Max = LifeOf(card),
                Damage = 0,
                Armor = GetUnitArmor(card, gy),
                Strength = GetUnitStrength(card, gy),
                Abilities = GetUnitAbilities(card, gy),
                IsCorpse = false,
                RecruitedThisWave = true,
                HasAttackedThisWave = false,
            };
            ps.Units[key] = unit;

            // Remove card from hand
            mine.Hand.RemoveAt(handIndex);
            Send(mine.Me.Ns, new PacketWriter().WriteU8(handIndex).WriteU16(card).WriteBool(true).Frame(Op.HandCardRemove));

            // Relay summon to both clients
            byte[] toActor = new PacketWriter().WriteU16(card).WriteU8(gx).WriteU8(gy).WriteBool(false).Frame(Op.SummonUnit);
            Send(mine.Me.Ns, toActor);

            // Send UpdateUnit BEFORE SummonUnitGet so the owner's anim_update_unit fires
            // ahead of the second script_summon (opponent visual). On the owner's queue:
            //   SummonUnit [stall] → UpdateUnit(activate=1) → SummonUnitGet [stall]
            // rather than stacking two stalls before activation.
            if (theirs != null)
            {
                SendUnitUpdateToBoth(mine, theirs, gx, gy, unit, b);

                // Client mirrors Y in container_summon_unit_get: yy = 2 - buffer_read
                byte[] toOpp = new PacketWriter().WriteU16(card).WriteU8(gx).WriteU8(gy).WriteBool(false).Frame(Op.SummonUnitGet);
                Send(theirs.Me.Ns, toOpp);
            }

            // Consume action
            ConsumeAction(mine, theirs, b);
        }

        // ---- attack (op 22) ---------------------------------------------------

        static void HandleAttack(NetworkStream ns, byte[] payload)
        {
            BattleSlot mine, theirs = null;
            lock (_battleLock)
            {
                if (!_battles.TryGetValue(ns, out mine)) return;
                if (mine.Opp != null) _battles.TryGetValue(mine.Opp.Ns, out theirs);
            }

            Battle b = mine.Battle;
            if (b == null || b.Over || !b.Started) return;
            if (mine.P != b.Active) { Log("ATTACK from non-active player (ignored)"); return; }
            if (b.Round <= 1) { Log("ATTACK rejected: Round 1 ceasefire"); GrantAction(mine); return; }

            PlayerState ps = b.P[mine.P];
            if (ps.ActionsRemaining <= 0) { Log("ATTACK rejected: no actions remaining"); GrantAction(mine); return; }

            var r = new PacketReader(payload, 0);
            bool isSpell = r.ReadBool();
            bool selectGrid = r.ReadBool();
            byte ax = r.ReadU8(), ay = r.ReadU8(), tx = r.ReadU8(), ty = r.ReadU8();
            // The client's opponent slots are registered under key slot_get_id(x, loopYY) but their
            // grid_y is set to (2 - loopYY) (obj_battle_control Create), so a relayed enemy unit sits
            // on a slot whose grid_y ALREADY equals its server lane. The click therefore sends the
            // real server lane in ty — do NOT mirror it again. (The attack-response container_attack
            // does its own 2-y to recover the slot key, so echoing ty back is also correct.)
            byte serverTy = ty;

            Log("ATTACK " + mine.Me.User + ": spell=" + isSpell + " from(" + ax + "," + ay + ") -> (" + tx + "," + serverTy + ")");

            // Spells (instant self-cast OR targeted) reuse op 22 with the spell flag set. The packet
            // carries the caster's coords (ax,ay) and a target (tx,ty). We identify the caster's card
            // + wave (wave = ax = the caster's grid_x column), resolve its wave-spell (SpellOf), and
            // apply it to the target. MUST NOT fall through to attack logic (that would make the
            // caster melee the mirrored slot and get counter-killed). Unimplemented spells no-op.
            if (isSpell)
            {
                ushort casterCard = 0;
                if (ax == 1 && ay == 1) casterCard = ps.LeaderCard;
                else { BUnit cu; if (ps.Units.TryGetValue(Key(ax, ay), out cu) && cu != null && !cu.IsCorpse) casterCard = cu.Card; }

                OrderEffect seff = (casterCard != 0) ? SpellOf(casterCard, ax) : new OrderEffect { Kind = OrderKind.None };
                Log("SPELL " + mine.Me.User + ": card " + casterCard + " wave " + ax + " kind=" + seff.Kind + " target(" + tx + "," + ty + ")");
                if (seff.Kind == OrderKind.None)
                {
                    Log("  -> spell not implemented yet; no-op (action refunded)");
                    GrantAction(mine);
                    BattleSlot q0 = mine.P == 0 ? mine : theirs, q1 = mine.P == 0 ? theirs : mine;
                    if (q0 != null && q1 != null) SyncUnitStates(q0, q1, b);
                    return;
                }
                ResolveEffect(seff, mine, theirs, b, tx, ty);
                return;
            }

            // Find attacker
            BUnit attacker;
            bool attackerIsLeader = (ax == 1 && ay == 1);
            if (attackerIsLeader)
            {
                // Leader is not in the Units dictionary; create a virtual BUnit for it
                attacker = new BUnit
                {
                    Card = ps.LeaderCard,
                    Atk = AtkOf(ps.LeaderCard),
                    Max = ps.LeaderMax,
                    Damage = ps.LeaderMax - ps.LeaderLife,
                    Armor = 0,
                    Strength = 0,
                    Abilities = UnitAbility.None,
                };
            }
            else
            {
                if (!ps.Units.TryGetValue(Key(ax, ay), out attacker) || attacker.IsCorpse)
                { Log("ATTACK rejected: no valid attacker at (" + ax + "," + ay + ")"); GrantAction(mine); return; }
            }

            // Validate attacker is in current wave (ax = wave position: 0=Rear, 1=Flank, 2=Vanguard)
            if (!attackerIsLeader && ax != b.Wave)
            { Log("ATTACK rejected: attacker at x=" + ax + " not in wave " + b.Wave); GrantAction(mine); return; }

            // Validate attacker hasn't already attacked this wave
            if (!attackerIsLeader && attacker.HasAttackedThisWave)
            { Log("ATTACK rejected: attacker already attacked this wave"); GrantAction(mine); return; }

            // Validate attacker was not recruited this wave
            if (!attackerIsLeader && attacker.RecruitedThisWave)
            { Log("ATTACK rejected: attacker was recruited this wave"); GrantAction(mine); return; }

            // Determine attack type: ranged if unit has RangedAttack ability
            // Ranged is decided by the same table the client's atktype (update_buff) uses, so the
            // two never disagree. Leaders melee. ax is the attacker's wave (grid_x).
            bool isRanged = !attackerIsLeader && IsRangedAtWave(attacker.Card, ax);

            // Find target (using server Y coordinate)
            PlayerState opPs = b.P[1 - mine.P];
            int targetKey = Key(tx, serverTy);

            // Check if target is the opponent's leader
            bool targetIsLeader = (tx == 1 && serverTy == 1);

            // --- Melee targeting validation ---
            if (!isRanged && !targetIsLeader)
            {
                BUnit targetUnit;
                if (!opPs.Units.TryGetValue(targetKey, out targetUnit) || targetUnit.IsCorpse)
                {
                    // No alive unit at the target cell. The only thing that can be hit through an
                    // empty cell is the leader, and the leader sits behind the CENTER lane (gy=1).
                    // So this is only valid when the TARGET lane is the center and its Vanguard is
                    // clear. (Bug fix: this used the attacker's lane 'ay' instead of the target
                    // lane 'serverTy', which let empty side tiles wrongly hit or wrongly reject.)
                    if (!CanMeleeTargetLeader(serverTy, opPs, b))
                    { Log("ATTACK rejected: no valid melee target at (" + tx + "," + serverTy + ")"); GrantAction(mine); return; }
                    targetIsLeader = true;
                }
                else
                {
                    // Validate target is the frontmost alive unit in its column
                    if (!IsFrontmostAliveInColumn(tx, serverTy, opPs))
                    { Log("ATTACK rejected: target at (" + tx + "," + serverTy + ") is not frontmost in column"); GrantAction(mine); return; }
                }
            }

            // --- Ranged + Intercept validation ---
            if (isRanged && !targetIsLeader)
            {
                // Check if an intercepting unit blocks this attack
                int interceptWave = FindInterceptInColumn(serverTy, opPs, b.Wave);
                if (interceptWave >= 0 && interceptWave != tx)
                {
                    // Intercept redirects attack to the interceptor
                    Log("  -> INTERCEPT: ranged attack redirected to (" + interceptWave + "," + serverTy + ")");
                    tx = (byte)interceptWave;
                    targetKey = Key(tx, serverTy);
                    targetIsLeader = false;
                }
            }

            // --- Resolve damage ---
            int rawDmg = Math.Max(1, attacker.Atk);
            bool targetDied = false;
            bool leaderDied = false;

            if (targetIsLeader)
            {
                // Damage goes to opponent's leader
                opPs.LeaderLife -= rawDmg;
                leaderDied = opPs.LeaderLife <= 0;
                Log("  -> LEADER takes " + rawDmg + " (life " + opPs.LeaderLife + (leaderDied ? ", DEAD" : "") + ")");
                // Target the leader's real slot (1,1), NOT the empty front slot the client aimed at.
                // The client's attack animation looks up a unit at the target coords; sending the
                // empty slot makes that lookup undefined and crashes (and mis-addresses the leader's
                // HP update). The leader is always stored at (1,1) on both grids.
                SendAttackAndLeaderUpdate(mine, theirs, ax, ay, 1, 1, rawDmg, attackerIsLeader, opPs, isRanged, false, b);
            }
            else
            {
                BUnit target;
                if (opPs.Units.TryGetValue(targetKey, out target) && !target.IsCorpse)
                {
                    int actualDmg = Math.Max(1, rawDmg - target.Armor);
                    target.Damage += actualDmg;
                    targetDied = target.Damage >= target.Max;
                    Log("  -> unit(" + tx + "," + serverTy + ") card " + target.Card + " takes " + actualDmg +
                        " (damage " + target.Damage + "/" + target.Max + (targetDied ? ", WILL DIE AT WAVE END" : "") + ")");
                    SendAttackAndUpdate(mine, theirs, ax, ay, tx, serverTy, actualDmg, attackerIsLeader, target, isRanged, false, b);

                    // --- Counter-attack (melee only) ---
                    if (!isRanged && !targetDied && target.Atk > 0 && !target.RecruitedThisWave)
                    {
                        // Target counter-attacks if it's alive, melee, in the current wave, and was not recruited this wave
                        int counterKey = Key(tx, serverTy);
                        int counterDmg = Math.Max(1, target.Atk - attacker.Armor);
                        attacker.Damage += counterDmg;
                        bool attackerDied = attacker.Damage >= attacker.Max;
                        Log("  -> COUNTER-ATTACK: unit(" + tx + "," + serverTy + ") hits back for " + counterDmg +
                            " (attacker damage " + attacker.Damage + "/" + attacker.Max + (attackerDied ? ", WILL DIE" : "") + ")");
                        SendAttackAndUpdate(theirs, mine, tx, serverTy, ax, ay, counterDmg, false, attacker, false, true, b);
                        // Note: counter-attack from attacker's perspective uses theirs/mine swapped
                    }
                }
                else
                {
                    // No unit at target, damage goes to leader
                    opPs.LeaderLife -= rawDmg;
                    leaderDied = opPs.LeaderLife <= 0;
                    Log("  -> LEADER (no unit at target) takes " + rawDmg + " (life " + opPs.LeaderLife + (leaderDied ? ", DEAD" : "") + ")");
                    // Target the leader's real slot (1,1) — see note above; empty-slot coords crash the client.
                    SendAttackAndLeaderUpdate(mine, theirs, ax, ay, 1, 1, rawDmg, attackerIsLeader, opPs, isRanged, false, b);
                }
            }

            // Mark attacker as having attacked
            if (!attackerIsLeader)
                attacker.HasAttackedThisWave = true;

            if (leaderDied)
            {
                b.Over = true;
                Log("BATTLE END: " + mine.Me.User + " wins");
                Send(mine.Me.Ns, new PacketWriter().WriteBool(true).WriteU16(0).Frame(Op.BattleEnd));
                if (theirs != null) Send(theirs.Me.Ns, new PacketWriter().WriteBool(false).WriteU16(0).Frame(Op.BattleEnd));
                return;
            }

            // Consume action
            ConsumeAction(mine, theirs, b);
        }

        // Melee: can the attacker target the leader at (1,1)?
        // col = attacker's column (grid_y). Leader is at wave=1, col=1.
        // Only if no alive unit is in front of the leader (wave > 1) in the same column.
        // The leader sits at the center (1,1), behind the center-lane Vanguard (2,1). It can be
        // melee'd only by attacking the CENTER lane (targetCol == 1) once that Vanguard is gone.
        static bool CanMeleeTargetLeader(int targetCol, PlayerState opPs, Battle b)
        {
            if (targetCol != 1) return false; // only the center lane leads to the leader
            BUnit u;
            if (opPs.Units.TryGetValue(Key(2, 1), out u) && u != null && !u.IsCorpse) return false; // center Vanguard blocks
            return true;
        }

        // Is the unit at (x, y) the frontmost alive unit in its column?
        // x = wave position (0-2), y = column (0-2)
        // Frontmost = highest wave number (closest to Vanguard)
        static bool IsFrontmostAliveInColumn(int x, int y, PlayerState opPs)
        {
            for (int wave = 2; wave > x; wave--)
            {
                int k = Key(wave, y);
                BUnit u;
                if (opPs.Units.TryGetValue(k, out u) && !u.IsCorpse) return false;
            }
            return true;
        }

        // Find the frontmost intercept unit in a column.
        // col = column (grid_y). Returns the wave position, or -1 if none.
        static int FindInterceptInColumn(int col, PlayerState opPs, int attackWave)
        {
            for (int wave = 2; wave >= 0; wave--)
            {
                int k = Key(wave, col);
                BUnit u;
                if (opPs.Units.TryGetValue(k, out u) && !u.IsCorpse && (u.Abilities & UnitAbility.Intercept) != 0)
                    return wave;
            }
            return -1;
        }

        // Send attack animation + leader update to both clients
        static void SendAttackAndLeaderUpdate(BattleSlot actor, BattleSlot opp, byte ax, byte ay, byte tx, byte ty,
                                                int dmg, bool attackerIsLeader, PlayerState opPs, bool isRanged,
                                                bool counter, Battle b)
        {
            byte atype = (byte)(isRanged ? 1 : 0);
            // AttackOut to actor — raw coords (container_attack_out reads raw yy)
            Send(actor.Me.Ns, new PacketWriter()
                .WriteU8(ax).WriteU8(ay).WriteU8(tx).WriteU8(ty)
                .WriteU16((ushort)dmg).WriteU8(atype).WriteBool(true).WriteBool(counter)
                .Frame(Op.AttackOut));
            // AttackGet to opponent — mirror attacker's Y (container_attack_get: yy = 2 - read)
            if (opp != null)
                Send(opp.Me.Ns, new PacketWriter()
                    .WriteU8(ax).WriteU8((byte)(2 - ay)).WriteU8(tx).WriteU8(ty)
                    .WriteU16((ushort)dmg).WriteU8(atype).WriteBool(true).WriteBool(counter)
                    .Frame(Op.AttackGet));

            // Leader update
            byte leaderAtk = (byte)AtkOf(opPs.LeaderCard);
            uint leaderLife = (uint)Math.Max(0, opPs.LeaderLife);
            bool leaderDead = opPs.LeaderLife <= 0;
            ushort leaderMax = (ushort)opPs.LeaderMax;
            // UpdateUnit to opponent (leader's owner) — raw Y
            if (opp != null)
                Send(opp.Me.Ns, new PacketWriter()
                    .WriteU8(tx).WriteU8(ty).WriteU8(leaderAtk).WriteU16((ushort)leaderLife)
                    .WriteU8(0).WriteU8(0).WriteU8(0).WriteBool(true)
                    .WriteBool(leaderDead).WriteU8((byte)leaderMax)
                    .Frame(Op.UpdateUnit));
            // UpdateUnitGet to actor — raw Y (container_update_unit_get: yy = 2 - read)
            Send(actor.Me.Ns, new PacketWriter()
                .WriteU8(tx).WriteU8(ty).WriteU8(leaderAtk).WriteU16((ushort)leaderLife)
                .WriteU8(0).WriteU8(0).WriteU8(0).WriteBool(true)
                .WriteBool(leaderDead).WriteU8((byte)leaderMax)
                .Frame(Op.UpdateUnitGet));
        }

        // Send attack animation + unit stat update to both clients
        static void SendAttackAndUpdate(BattleSlot actor, BattleSlot opp, byte ax, byte ay, byte tx, byte ty,
                                          int dmg, bool attackerIsLeader, BUnit target, bool isRanged,
                                          bool counter, Battle b)
        {
            byte atype = (byte)(isRanged ? 1 : 0);

            // AttackOut to actor — raw coords
            Send(actor.Me.Ns, new PacketWriter()
                .WriteU8(ax).WriteU8(ay).WriteU8(tx).WriteU8(ty)
                .WriteU16((ushort)dmg).WriteU8(atype).WriteBool(true).WriteBool(counter)
                .Frame(Op.AttackOut));
            // AttackGet to opponent — mirror attacker Y
            if (opp != null)
                Send(opp.Me.Ns, new PacketWriter()
                    .WriteU8(ax).WriteU8((byte)(2 - ay)).WriteU8(tx).WriteU8(ty)
                    .WriteU16((ushort)dmg).WriteU8(atype).WriteBool(true).WriteBool(counter)
                    .Frame(Op.AttackGet));

            // Unit update
            if (target != null)
            {
                SendUnitUpdateToBoth(opp, actor, tx, ty, target, b);
            }
        }

        // Send update_unit / update_unit_get for a unit at (x, y)
        static void SendUnitUpdateToBoth(BattleSlot ownerSlot, BattleSlot oppSlot, int x, int y, BUnit unit, Battle b)
        {
            if (unit == null) return;
            // A corpse must always render dead with 0 HP. ProcessCasualties resets Damage=0 on
            // corpses, so we can't infer death from Damage alone — check IsCorpse.
            bool dead = unit.IsCorpse || unit.Damage >= unit.Max;
            int curLife = dead ? 0 : Math.Max(0, unit.Max - unit.Damage);
            // Ready = alive, hasn't acted, not summon-sick (see SyncPlayerUnits). A freshly summoned
            // unit is RecruitedThisWave=true, so it correctly shows greyed until the next wave.
            bool active = !unit.IsCorpse && !unit.HasAttackedThisWave && !unit.RecruitedThisWave;
            if (PacketLog)
                Log("[SendUnitUpdateToBoth] -> (" + x + "," + y + ") active=" + (active ? 1 : 0)
                    + " wave=" + b.Wave + " ux=" + x + " isCorpse=" + unit.IsCorpse);

            // UpdateUnit to owner — raw Y (unit stored at slot_get_id(x, y))
            Send(ownerSlot.Me.Ns, new PacketWriter()
                .WriteU8((byte)x).WriteU8((byte)y).WriteU8((byte)unit.Atk).WriteU16((ushort)curLife)
                .WriteU8((byte)unit.Strength).WriteU8(0).WriteU8((byte)unit.Armor)
                .WriteBool(active).WriteBool(dead).WriteU8((byte)unit.Max)
                .Frame(Op.UpdateUnit));
            // UpdateUnitGet to opponent — raw Y (container_update_unit_get: yy = 2 - read)
            if (oppSlot != null)
                Send(oppSlot.Me.Ns, new PacketWriter()
                    .WriteU8((byte)x).WriteU8((byte)y).WriteU8((byte)unit.Atk).WriteU16((ushort)curLife)
                    .WriteU8((byte)unit.Strength).WriteU8(0).WriteU8((byte)unit.Armor)
                    .WriteBool(active).WriteBool(dead).WriteU8((byte)unit.Max)
                    .Frame(Op.UpdateUnitGet));

            // Buff/state (atktype for ranged, incorp=0, adpx=wave). x is the unit's wave (grid_x).
            SendUnitBuff(ownerSlot, oppSlot, x, y, unit);
        }

        // ---- move (op 26) -----------------------------------------------------

        static void HandleMove(NetworkStream ns, byte[] payload)
        {
            BattleSlot mine, theirs = null;
            lock (_battleLock)
            {
                if (!_battles.TryGetValue(ns, out mine)) return;
                if (mine.Opp != null) _battles.TryGetValue(mine.Opp.Ns, out theirs);
            }

            Battle b = mine.Battle;
            if (b == null || b.Over || !b.Started) return;
            if (mine.P != b.Active) { Log("MOVE from non-active player (ignored)"); return; }
            PlayerState ps = b.P[mine.P];
            if (ps.ActionsRemaining <= 0) { Log("MOVE rejected: no actions remaining"); GrantAction(mine); return; }

            var r = new PacketReader(payload, 0);
            byte x1 = r.ReadU8(), y1 = r.ReadU8(), x2 = r.ReadU8(), y2 = r.ReadU8();
            Log("MOVE " + mine.Me.User + ": (" + x1 + "," + y1 + ") -> (" + x2 + "," + y2 + ")");

            // Validate source unit
            int srcKey = Key(x1, y1);
            BUnit unit;
            if (!ps.Units.TryGetValue(srcKey, out unit) || unit.IsCorpse)
            { Log("MOVE rejected: no unit at (" + x1 + "," + y1 + ")"); GrantAction(mine); return; }

            // Validate target cell is empty
            int dstKey = Key(x2, y2);
            if (ps.Units.ContainsKey(dstKey))
            { Log("MOVE rejected: destination (" + x2 + "," + y2 + ") occupied"); GrantAction(mine); return; }

            // Cannot move leader
            if (x1 == 1 && y1 == 1)
            { Log("MOVE rejected: cannot move leader"); GrantAction(mine); return; }

            // Move unit
            ps.Units.Remove(srcKey);
            ps.Units[dstKey] = unit;

            // Relay to both clients
            Send(mine.Me.Ns, new PacketWriter().WriteU8(x1).WriteU8(y1).WriteU8(x2).WriteU8(y2).Frame(Op.Move));
            if (theirs != null)
                // container_move_unit_get mirrors Y: y1 = 2 - read, y2 = 2 - read
                Send(theirs.Me.Ns, new PacketWriter().WriteU8(x1).WriteU8(y1).WriteU8(x2).WriteU8(y2).Frame(Op.MoveGet));

            ConsumeAction(mine, theirs, b);
        }

        // ---- clear corpse (op 24) ---------------------------------------------

        static void HandleClearCorpse(NetworkStream ns, byte[] payload)
        {
            BattleSlot mine, theirs = null;
            lock (_battleLock)
            {
                if (!_battles.TryGetValue(ns, out mine)) return;
                if (mine.Opp != null) _battles.TryGetValue(mine.Opp.Ns, out theirs);
            }

            Battle b = mine.Battle;
            if (b == null || b.Over || !b.Started) return;
            if (mine.P != b.Active) { Log("CLEAR_CORPSE from non-active player (ignored)"); return; }
            PlayerState ps = b.P[mine.P];
            if (ps.ActionsRemaining <= 0) { Log("CLEAR_CORPSE rejected: no actions remaining"); GrantAction(mine); return; }

            var r = new PacketReader(payload, 0);
            byte cx = r.ReadU8(), cy = r.ReadU8();
            int key = Key(cx, cy);
            BUnit unit;
            if (!ps.Units.TryGetValue(key, out unit) || !unit.IsCorpse)
            { Log("CLEAR_CORPSE rejected: no corpse at (" + cx + "," + cy + ")"); GrantAction(mine); return; }

            ps.Units.Remove(key);
            Log("CLEAR_CORPSE " + mine.Me.User + ": removed corpse at (" + cx + "," + cy + ")");

            // Notify both clients
            Send(mine.Me.Ns, new PacketWriter().WriteU8(cx).WriteU8(cy).Frame(Op.ClearCorpse));
            if (theirs != null)
                // container_clear_corpse_get mirrors Y: yy = 2 - buffer_read
                Send(theirs.Me.Ns, new PacketWriter().WriteU8(cx).WriteU8(cy).Frame(Op.ClearCorpseGet));

            ConsumeAction(mine, theirs, b);
        }

        // ---- action management ------------------------------------------------

        static void ConsumeAction(BattleSlot mine, BattleSlot theirs, Battle b)
        {
            PlayerState ps = b.P[mine.P];
            ps.ActionsRemaining--;
            Log("  actions remaining for " + mine.Me.User + ": " + ps.ActionsRemaining);
            GrantAction(mine);
        }

        // ---- end turn (op 14 empty = end turn) --------------------------------

        static void HandleEndTurn(NetworkStream ns)
        {
            BattleSlot mine, theirs = null;
            lock (_battleLock)
            {
                if (!_battles.TryGetValue(ns, out mine)) return;
                if (mine.Opp != null) _battles.TryGetValue(mine.Opp.Ns, out theirs);
            }
            Battle b = mine.Battle;
            if (b == null || b.Over || theirs == null || !b.Started) return;
            if (mine.P != b.Active) { Log("END TURN from non-active player (ignored)"); return; }

            BattleSlot p0 = mine.P == 0 ? mine : theirs;
            BattleSlot p1 = mine.P == 1 ? mine : theirs;

            Log("END TURN: " + mine.Me.User + " (wave " + b.Wave + ", round " + b.Round + ")");

            if (b.Active == b.First)
            {
                // First player done -> second player's turn (same wave)
                b.Active = 1 - b.First;
                b.P[b.Active].ActionsRemaining = ActionsPerWave;
                Log("  -> 2nd player's turn (wave " + b.Wave + ")");
            }
            else if (b.Wave > 0)
            {
                // Both players done this wave -> process casualties, advance wave
                ProcessCasualties(b, p0, p1);
                if (b.Over) return; // match ended from casualties
                b.Wave--;
                b.Active = b.First;
                b.P[b.Active].ActionsRemaining = ActionsPerWave;
                ResetWaveFlags(b);
                Log("  -> wave complete -> wave " + b.Wave + " (" + WaveName(b.Wave) + ")");
                SendWave(p0, p1, b);
            }
            else
            {
                // Rear wave done -> process casualties, new round
                ProcessCasualties(b, p0, p1);
                if (b.Over) return;
                b.Round++;
                b.First = 1 - b.First;
                b.Wave = 2;
                b.Active = b.First;
                b.P[b.Active].ActionsRemaining = ActionsPerWave;
                ResetWaveFlags(b);
                Log("  -> round complete -> ROUND " + b.Round + " (attacks " + (b.Round >= 2 ? "ENABLED" : "ceasefire") + "), first=P" + b.First);
                SendWave(p0, p1, b); // op21 (attack phase) is now sent from SendTurn for round >= 2
            }
            SendTurn(p0, p1, b.Active);
        }

        // ---- casualties (end of wave) -----------------------------------------

        static void ProcessCasualties(Battle b, BattleSlot p0, BattleSlot p1)
        {
            Log("PROCESSING CASUALTIES (wave " + b.Wave + ", round " + b.Round + ")");
            bool anyCasualties = false;

            // Pass 1: Find and process dead units (mark corpse, send UpdateUnit)
            for (int pi = 0; pi < 2; pi++)
            {
                PlayerState ps = b.P[pi];
                List<int> deadKeys = new List<int>();

                foreach (var kvp in ps.Units)
                {
                    BUnit u = kvp.Value;
                    if (u == null || u.IsCorpse) continue;
                    if (u.Damage >= u.Max)
                    {
                        deadKeys.Add(kvp.Key);
                        anyCasualties = true;
                    }
                }

                foreach (int key in deadKeys)
                {
                    BUnit u = ps.Units[key];
                    int ux = key / 10, uy = key % 10;
                    Log("  CASUALTY: player " + pi + " unit(" + ux + "," + uy + ") card " + u.Card + " (damage " + u.Damage + " >= life " + u.Max + ")");
                    u.IsCorpse = true;
                    u.Damage = 0;

                    SendUnitUpdateToBoth(
                        pi == 0 ? p0 : p1,
                        pi == 0 ? p1 : p0,
                        ux, uy, u, b);
                }
            }

            // Pass 2: Send single batched casualties packet (8 entries = 32 bytes payload)
            // Client reads 2×2×2 = 8 entries. We send full state for current wave + leader.
            // Must check IsCorpse (not Damage) because Pass 1 sets Damage=0 on corpses.
            {
                var pw = new PacketWriter();
                for (int pi = 0; pi < 2; pi++)
                {
                    PlayerState ps = b.P[pi];
                    for (int col = 0; col < 3; col++)
                    {
                        int key = Key(b.Wave, col);
                        BUnit u;
                        bool dead = ps.Units.TryGetValue(key, out u) && u.IsCorpse;
                        pw.WriteU8((byte)pi).WriteU8((byte)b.Wave).WriteU8((byte)col).WriteBool(dead);
                    }
                    pw.WriteU8((byte)pi).WriteU8(1).WriteU8(1).WriteBool(ps.LeaderLife <= 0);
                }
                byte[] pkt = pw.Frame(Op.BattleCasualties);
                Send(p0.Me.Ns, pkt);
                Send(p1.Me.Ns, pkt);
            }

            // Check leader rout
            for (int pi = 0; pi < 2; pi++)
            {
                PlayerState ps = b.P[pi];
                if (ps.LeaderLife <= 0)
                {
                    b.Over = true;
                    int winner = 1 - pi;
                    BattleSlot winnerSlot = pi == 0 ? p1 : p0;
                    BattleSlot loserSlot = pi == 0 ? p0 : p1;
                    Log("BATTLE END: player " + pi + " leader routed -> player " + winner + " wins");
                    Send(winnerSlot.Me.Ns, new PacketWriter().WriteBool(true).WriteU16(0).Frame(Op.BattleEnd));
                    Send(loserSlot.Me.Ns, new PacketWriter().WriteBool(false).WriteU16(0).Frame(Op.BattleEnd));
                    return;
                }
            }

            if (!anyCasualties)
                Log("  No casualties this wave.");
        }

        // Reset wave-specific flags on all units when advancing to a new wave
        static void ResetWaveFlags(Battle b)
        {
            for (int pi = 0; pi < 2; pi++)
            {
                foreach (var kvp in b.P[pi].Units)
                {
                    BUnit u = kvp.Value;
                    if (u != null && !u.IsCorpse)
                    {
                        u.RecruitedThisWave = false;
                        u.HasAttackedThisWave = false;
                    }
                }
            }
        }

        // ---- wave / turn management -------------------------------------------

        static string WaveName(int w) { return w == 2 ? "Vanguard" : (w == 1 ? "Flank" : "Rear"); }

        static void SendTurn(BattleSlot p0, BattleSlot p1, int active)
        {
            Send(p0.Me.Ns, new PacketWriter().WriteU16((ushort)(active == 0 ? 0 : 1)).WriteBool(true).Frame(Op.TurnGet));
            Send(p1.Me.Ns, new PacketWriter().WriteU16((ushort)(active == 1 ? 0 : 1)).WriteBool(true).Frame(Op.TurnGet));
            BattleSlot act = active == 0 ? p0 : p1;
            GrantAction(act);
            SyncUnitStates(p0, p1, act.Battle);

            // Grant a generous orb pool each turn so orders (which cost orbs) are always affordable.
            // op62 sets the recipient's OWN orb count; op63 shows the opponent's count (cosmetic).
            // Orbs start at 0 (container_battle_start) and only change via these packets, so without
            // this the player can never afford any order.
            Send(p0.Me.Ns, new PacketWriter().WriteU8(OrbPool).Frame(Op.Orbs));
            Send(p1.Me.Ns, new PacketWriter().WriteU8(OrbPool).Frame(Op.Orbs));
            Send(p0.Me.Ns, new PacketWriter().WriteU8(OrbPool).Frame(Op.OrbsGet));
            Send(p1.Me.Ns, new PacketWriter().WriteU8(OrbPool).Frame(Op.OrbsGet));

            // End the ceasefire from Round 2 on. op 21 sets canatk=1 (never reset) and fades the
            // "no-attack" label. Sent every turn (after the wave update) so the queued wave_update
            // can't resurrect the label, and both clients stay unlocked. Idempotent.
            Battle b = act.Battle;
            if (b != null && b.Round >= 2)
            {
                Send(p0.Me.Ns, new PacketWriter().Frame(Op.BattleAttackPhase));
                Send(p1.Me.Ns, new PacketWriter().Frame(Op.BattleAttackPhase));
            }
        }

        static void SendWave(BattleSlot p0, BattleSlot p1, Battle b)
        {
            Send(p0.Me.Ns, new PacketWriter().WriteU8((byte)b.Wave).WriteU16((ushort)(b.First == 0 ? 0 : 1)).Frame(Op.WaveUpdate));
            Send(p1.Me.Ns, new PacketWriter().WriteU8((byte)b.Wave).WriteU16((ushort)(b.First == 1 ? 0 : 1)).Frame(Op.WaveUpdate));
            // Note: SyncUnitStates is intentionally NOT called here — SendTurn (called right after
            // SendWave) syncs unit states once. Calling it in both floods the client's action queue.
        }

        static void SendLeaderSummon(BattleSlot slot, Battle b)
        {
            PlayerState ps = b.P[slot.P];
            ushort leaderCard = ps.LeaderCard;
            if (leaderCard == 0) return;

            // SummonUnit to the owner (creates obj_unit on their grid)
            Send(slot.Me.Ns, new PacketWriter()
                .WriteU16(leaderCard).WriteU8(1).WriteU8(1).WriteBool(false)
                .Frame(Op.SummonUnit));

            // SummonUnitGet to the opponent (mirrored coords — (1,1) mirrors to (1,1))
            Send(slot.Opp.Ns, new PacketWriter()
                .WriteU16(leaderCard).WriteU8(1).WriteU8(1).WriteBool(false)
                .Frame(Op.SummonUnitGet));
        }

        static void SyncUnitStates(BattleSlot p0, BattleSlot p1, Battle b)
        {
            if (b == null || p0 == null || p1 == null) return;
            SyncPlayerUnits(p0, p1, b, 0);
            SyncPlayerUnits(p1, p0, b, 1);
        }

        static void SyncPlayerUnits(BattleSlot ownerSlot, BattleSlot oppSlot, Battle b, int ownerP)
        {
            var ps = b.P[ownerP];
            if (PacketLog) Log("[SyncPlayerUnits] player=" + ownerP + " wave=" + b.Wave);

            // 1. Leader at (1,1) — active in all waves
            byte leaderAtk = (byte)AtkOf(ps.LeaderCard);
            uint leaderLife = (uint)Math.Max(0, ps.LeaderLife);
            bool leaderDead = ps.LeaderLife <= 0;
            ushort leaderMax = (ushort)ps.LeaderMax;

            // UpdateUnit to owner — raw Y
            Send(ownerSlot.Me.Ns, new PacketWriter()
                .WriteU8(1).WriteU8((byte)1).WriteU8(leaderAtk).WriteU16((ushort)leaderLife)
                .WriteU8(0).WriteU8(0).WriteU8(0).WriteBool(true)
                .WriteBool(leaderDead).WriteU8((byte)leaderMax)
                .Frame(Op.UpdateUnit));
            // UpdateUnitGet to opponent — raw Y (container_update_unit_get: yy = 2 - read)
            Send(oppSlot.Me.Ns, new PacketWriter()
                .WriteU8(1).WriteU8(1).WriteU8(leaderAtk).WriteU16((ushort)leaderLife)
                .WriteU8(0).WriteU8(0).WriteU8(0).WriteBool(true)
                .WriteBool(leaderDead).WriteU8((byte)leaderMax)
                .Frame(Op.UpdateUnitGet));

            // 2. Recruited units on grid
            foreach (var kvp in ps.Units)
            {
                BUnit u = kvp.Value;
                if (u == null) continue;

                int ux = kvp.Key / 10;
                int uy = kvp.Key % 10;
                // "active" (client: not greyed / can be ordered) means simply READY: alive, hasn't
                // acted this wave, and isn't summon-sick. It must NOT be tied to the current wave
                // column — the client already restricts attacks to the active wave via
                // (global.__wave == grid_x) in the unit action menu. Greying by wave here overrides
                // anim_wave_update's activate=1 refresh and paints the whole board exhausted.
                bool unitActive = !u.IsCorpse && !u.HasAttackedThisWave && !u.RecruitedThisWave;
                // Corpses always render dead with 0 HP (ProcessCasualties zeroes their Damage, so we
                // must key off IsCorpse, not Damage, or the HP bar shows full life on a dead sprite).
                bool unitDead = u.IsCorpse || u.Damage >= u.Max;
                int curLife = unitDead ? 0 : Math.Max(0, u.Max - u.Damage);
                if (PacketLog)
                    Log("  unit (" + ux + "," + uy + ") active=" + (unitActive ? 1 : 0)
                        + " wave=" + b.Wave + " ux=" + ux + " isCorpse=" + u.IsCorpse);

                    // UpdateUnit to owner — raw Y
                Send(ownerSlot.Me.Ns, new PacketWriter()
                    .WriteU8((byte)ux).WriteU8((byte)uy).WriteU8((byte)u.Atk).WriteU16((ushort)curLife)
                    .WriteU8((byte)u.Strength).WriteU8(0).WriteU8((byte)u.Armor)
                    .WriteBool(unitActive).WriteBool(unitDead).WriteU8((byte)u.Max)
                    .Frame(Op.UpdateUnit));
                // UpdateUnitGet to opponent — raw Y
                Send(oppSlot.Me.Ns, new PacketWriter()
                    .WriteU8((byte)ux).WriteU8((byte)uy).WriteU8((byte)u.Atk).WriteU16((ushort)curLife)
                    .WriteU8((byte)u.Strength).WriteU8(0).WriteU8((byte)u.Armor)
                    .WriteBool(unitActive).WriteBool(unitDead).WriteU8((byte)u.Max)
                    .Frame(Op.UpdateUnitGet));

                // Buff/state (atktype for ranged, incorp=0, adpx=wave). ux is the unit's wave (grid_x).
                SendUnitBuff(ownerSlot, oppSlot, ux, uy, u);
            }
        }

        // ---- helpers ----------------------------------------------------------

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
        internal static void Send(NetworkStream ns, byte[] data)
        {
            lock (_sendLock)
            {
                if (PacketLog)
                {
                    byte opcode = data[0];
                    if (opcode == Op.UpdateUnit || opcode == Op.UpdateUnitGet)
                    {
                        string ann = "";
                        // payload layout: x,y,atk,life(u16),str,mstr,arm,activ,dead,max(u8).
                        // 7-byte frame header + activ at payload index 8 => data[15].
                        if (data.Length >= 16) ann = " activate=" + data[15];
                        Log("-> " + (opcode == Op.UpdateUnit ? "UpdateUnit" : "UpdateUnitGet")
                            + " (" + data.Length + "B)" + ann + " " + Hex(data, 0));
                    }
                    else if (opcode != Op.Ping)
                    {
                        Log("-> op=" + opcode + " (" + data.Length + "B) " + Hex(data, 0));
                    }
                }
                ns.Write(data, 0, data.Length); ns.Flush();
            }
        }

        static string Hex(byte[] b, int offset = 0)
        {
            int n = b.Length - offset;
            if (n <= 0) return "";
            var sb = new StringBuilder();
            for (int i = offset; i < b.Length && i < offset + 64; i++) sb.Append(b[i].ToString("X2")).Append(' ');
            if (b.Length - offset > 64) sb.Append("...");
            return sb.ToString().TrimEnd();
        }
    }

    // A player waiting in the Arena queue.
    class Waiting
    {
        public NetworkStream Ns;
        public string User;
        public byte DeckId;
    }

    // Simple 1v1 matchmaking: hold at most one waiting player; the next joiner is paired with them.
    static class Matchmaker
    {
        static readonly object _lock = new object();
        static Waiting _waiting;
        static int _nextBattleId = 1;

        public static void Join(NetworkStream ns, string user, byte deckId)
        {
            if (string.IsNullOrEmpty(user)) return;
            Waiting me = new Waiting { Ns = ns, User = user, DeckId = deckId };
            Waiting opp = null; int bid = 0;
            lock (_lock)
            {
                if (_waiting == null || ReferenceEquals(_waiting.Ns, ns)) { _waiting = me; Program.Log("QUEUE: " + user + " waiting (deck " + deckId + ")"); return; }
                opp = _waiting; _waiting = null; bid = _nextBattleId++;
            }
            Program.StartBattle(opp, me, bid);
        }

        public static void Cancel(NetworkStream ns)
        {
            lock (_lock) { if (_waiting != null && ReferenceEquals(_waiting.Ns, ns)) _waiting = null; }
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

    static class DeckStore
    {
        const int MaxDecks = 12;
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
