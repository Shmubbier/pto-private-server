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
        Counter     = 4,  // deals its attack back when hit by a melee attack
        HeroKiller  = 8,  // deals double damage to enemy heroes
        Vamp        = 16, // when it deals damage, heal your leader by that amount
        Deathproof  = 32, // not defeated when lethal damage would defeat it
        Ephemeral   = 64, // defeated automatically at end of wave
    }

    class Program
    {
        static int Port = 51338;
        const ushort ClientVersion = 72;
        static bool Verbose = true;
        static bool PacketLog = true;
        const int ActionsPerWave = 3;  // actions granted at the start of each wave (Haste/effects add on top)
        const int OpeningHand = 5;
        const int OrbGainPerRound = 1;  // order resource gained at the start of each round
        const int OrbMax = 3;           // ...capped at this
        const int TurnSeconds = 60;     // a turn auto-advances after this many seconds of inactivity

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

            // Turn-timeout watchdog: force-advance a turn when the active player exceeds TurnSeconds.
            new Thread(TurnTimeoutLoop) { IsBackground = true }.Start();

            while (true)
            {
                TcpClient c = listener.AcceptTcpClient();
                var t = new Thread(() => HandleClient(c)) { IsBackground = true };
                t.Start();
            }
        }

        // Periodically scan live battles and auto-advance any turn past its deadline. Runs under
        // _battleLock so it can't race the client threads' turn handling.
        static void TurnTimeoutLoop()
        {
            while (true)
            {
                Thread.Sleep(2000);
                try
                {
                    var expired = new List<BattleSlot>();
                    lock (_battleLock)
                    {
                        // Select each battle's ACTIVE player's slot if its deadline has passed. Only one
                        // slot per battle has P == b.Active, so this is inherently one-per-battle — do NOT
                        // dedup by Battle first, or the inactive slot (iterated first) would mask the
                        // active one and the turn would never advance.
                        foreach (var kv in _battles)
                        {
                            BattleSlot slot = kv.Value;
                            Battle b = slot.Battle;
                            if (b == null || !b.Started || b.Over) continue;
                            if (slot.P == b.Active && b.TurnDeadline != default(DateTime)
                                && DateTime.UtcNow > b.TurnDeadline)
                                expired.Add(slot);
                        }
                        // Advance inside the same lock so state stays consistent with client threads.
                        foreach (var active in expired)
                        {
                            BattleSlot opp = null;
                            if (active.Opp != null) _battles.TryGetValue(active.Opp.Ns, out opp);
                            Battle b = active.Battle;
                            if (opp == null || b == null || b.Over || active.P != b.Active) continue;
                            Log("TURN TIMEOUT: auto-advancing " + active.Me.User + " (" + TurnSeconds + "s)");
                            try { AdvanceTurn(active, opp, b); } catch (Exception ex) { Log("timeout advance error: " + ex.Message); }
                        }
                    }
                }
                catch (Exception ex) { Log("TurnTimeoutLoop error: " + ex.Message); }
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
            SelfPassive p = PassiveOf(card, wave);
            UnitAbility a = UnitAbility.None;
            if (p.Intercept)  a |= UnitAbility.Intercept;
            if (p.Counter)    a |= UnitAbility.Counter;
            if (p.HeroKiller) a |= UnitAbility.HeroKiller;
            if (p.Vamp)       a |= UnitAbility.Vamp;
            if (p.Deathproof) a |= UnitAbility.Deathproof;
            if (p.Ephemeral)  a |= UnitAbility.Ephemeral;
            if (IsRangedAtWave(card, wave)) a |= UnitAbility.RangedAttack;
            return a;
        }

        // ---- data-driven self passives (parsed from client card_init.gml power text) -----------
        // "Self" passives apply to the hero that has them (no Forerunner:/Supporter:/Unit:/Leader:/
        // Vanguard: prefix — those are auras, a later tier). Strength here folds in "Melee Strength"
        // (nearly all our melee units), so it's added to the unit's attack stat. Armor is flat damage
        // reduction. Intercept/Counter are combat flags. wave index: 2=Vanguard, 1=Flank, 0=Rear.
        struct SelfPassive { public bool Intercept; public bool Counter; public bool HeroKiller; public bool Vamp; public bool Deathproof; public bool Ephemeral; public CoverType Cover; public int Strength; public int Armor; }
        static readonly SelfPassive[,] _passive = BuildPassives();
        static SelfPassive[,] BuildPassives()
        {
            var p = new SelfPassive[128, 3];
            // -- Intercept (plain; NOT "Intercept Killer") --
            int[] iceptV = { 26,30,32,33,35,36,37,39,40,41,48,53,57,61,66,67,80,82,83,90,91,92,98,109,115 };
            foreach (int r in iceptV) p[r, 2].Intercept = true;
            p[45, 2].Intercept = p[45, 1].Intercept = p[45, 0].Intercept = true; // Force Cube: all waves

            // -- Counter (retaliates against melee) --
            p[28, 1].Counter = true; // Berserker  (Flank)
            p[35, 2].Counter = true; // Knight      (Vanguard)
            p[61, 2].Counter = true; // Curse Knight (Vanguard)
            p[84, 2].Counter = true; // Fencer      (Vanguard)

            // -- Hero Killer (double damage to enemy heroes) --
            p[29, 1].HeroKiller = true; // Assassin      (Flank)
            p[52, 2].HeroKiller = true; // Fire Elemental (Vanguard)

            // -- Vamp (heal your leader by damage dealt) --
            p[49, 2].Vamp = true; // Vampire    (Vanguard)
            p[97, 2].Vamp = true; // Dark Knight (Vanguard)

            // -- Deathproof (survive lethal at wave end) -- (plain self only; auras are a later tier)
            p[109, 2].Deathproof = true; // Druid (Vanguard)

            // -- Ephemeral (defeated at end of wave) --
            p[51, 2].Ephemeral = p[51, 1].Ephemeral = p[51, 0].Ephemeral = true; // Zombie (all waves)
            p[54, 2].Ephemeral = true;                                           // Air Elemental (Vanguard)
            p[59, 2].Ephemeral = p[59, 1].Ephemeral = p[59, 0].Ephemeral = true; // Ghost (all waves)

            // -- Cover:X (takes damage aimed at the covered position) -- (self "Cover:", NOT aura "X: Cover")
            p[35, 0].Cover = CoverType.Forerunner; // Knight  Rear:  Cover: Forerunner
            p[49, 1].Cover = CoverType.Forerunner; // Vampire Flank: Cover: Forerunner
            p[47, 0].Cover = CoverType.Vanguard;   // Templar Rear:  Cover: Vanguard
            p[96, 1].Cover = CoverType.Leader;     // Squire  Flank: Cover: Leader
            p[96, 0].Cover = CoverType.Forerunner; // Squire  Rear:  Cover: Forerunner
            p[98, 1].Cover = CoverType.Forerunner; // White Knight Flank: Cover: Forerunner

            // -- Strength (folds in Melee Strength) --
            p[26, 1].Strength = 2; p[26, 0].Strength = 3;                 // Fighter
            p[28, 2].Strength = 2;                                        // Berserker  (Melee Strength 2)
            p[33, 2].Strength = 2;                                        // Homunculus (Melee Strength 2)
            p[56, 2].Strength = 2;                                        // Lightning Elemental
            p[66, 0].Strength = 5;                                        // Relic Hunter (Melee Strength 5)
            p[80, 2].Strength = 2; p[80, 0].Strength = 3;                 // Warrior
            p[84, 1].Strength = 2;                                        // Fencer
            p[88, 2].Strength = 3;                                        // Biomancer
            p[89, 2].Strength = 2;                                        // Legionnaire
            p[92, 2].Strength = 1; p[92, 1].Strength = 3; p[92, 0].Strength = 3; // Treasure Hunter
            p[96, 2].Strength = 1;                                        // Squire

            // -- Armor (flat damage reduction) --
            p[28, 2].Armor = 1; // Berserker
            p[30, 2].Armor = 2; // Alchemist
            p[32, 2].Armor = 2; // Healer
            return p;
        }
        static SelfPassive PassiveOf(ushort card, int wave)
        {
            int r = card / 2;
            if (r < 0 || r >= 128 || wave < 0 || wave > 2) return default(SelfPassive);
            return _passive[r, wave];
        }

        // ---- auras: passives that grant stats/abilities to OTHER units ("X: Y" in the card text) -----
        // A source at (sx,sy) projects each aura onto a set of recipient cells:
        //   Forerunner -> (sx+1,sy)   Supporter -> (sx-1,sy)   Vanguard -> all (2,*)   Rear -> all (0,*)
        //   Unit -> all your heroes + leader   Leader -> the leader (1,1)
        enum AuraTarget : byte { Forerunner, Supporter, Vanguard, Rear, Unit, Leader }
        struct Aura { public AuraTarget Target; public int Strength; public int Armor; public UnitAbility Grant; }

        // Aura sources by card+wave. R.Immunity / Cover-grant auras are omitted (no underlying ability yet).
        static List<Aura> AurasOf(ushort card, int wave)
        {
            var list = new List<Aura>();
            switch (card / 2)
            {
                case 27: if (wave == 1) list.Add(new Aura { Target = AuraTarget.Supporter, Strength = 3 }); break; // Dragon Mage F
                case 30: if (wave == 1) list.Add(new Aura { Target = AuraTarget.Leader, Armor = 2 }); break;        // Alchemist F
                case 31: if (wave == 1) list.Add(new Aura { Target = AuraTarget.Supporter, Grant = UnitAbility.RangedAttack }); break; // Gunner F
                case 33: if (wave == 1) list.Add(new Aura { Target = AuraTarget.Leader, Armor = 2 }); break;        // Homunculus F
                case 37: if (wave == 1) list.Add(new Aura { Target = AuraTarget.Forerunner, Strength = 2, Armor = 1 }); break; // Mystic F
                case 41: if (wave == 1) { list.Add(new Aura { Target = AuraTarget.Forerunner, Grant = UnitAbility.RangedAttack });
                                          list.Add(new Aura { Target = AuraTarget.Supporter, Grant = UnitAbility.RangedAttack }); } break; // Planestalker F
                case 48: if (wave == 1) { list.Add(new Aura { Target = AuraTarget.Forerunner, Grant = UnitAbility.Intercept });
                                          list.Add(new Aura { Target = AuraTarget.Supporter, Grant = UnitAbility.RangedAttack }); } break; // Trapper F
                case 57: if (wave == 1) list.Add(new Aura { Target = AuraTarget.Forerunner, Armor = 2 });
                         if (wave == 0) list.Add(new Aura { Target = AuraTarget.Leader, Armor = 1 }); break;        // Earth Elemental F / R
                case 58: if (wave == 1) list.Add(new Aura { Target = AuraTarget.Forerunner, Strength = 2 });
                         if (wave == 0) list.Add(new Aura { Target = AuraTarget.Leader, Strength = 2 }); break;     // Adventure Ranger F / R
                case 60: if (wave == 1) list.Add(new Aura { Target = AuraTarget.Leader, Armor = 2 }); break;        // Chronicler F
                case 61: if (wave == 1) list.Add(new Aura { Target = AuraTarget.Forerunner, Grant = UnitAbility.Counter });
                         if (wave == 0) list.Add(new Aura { Target = AuraTarget.Vanguard, Grant = UnitAbility.Counter }); break; // Curse Knight F / R
                case 66: if (wave == 1) list.Add(new Aura { Target = AuraTarget.Unit, Strength = 1 });
                         if (wave == 2) list.Add(new Aura { Target = AuraTarget.Vanguard, Grant = UnitAbility.Deathproof }); break; // Relic Hunter F / V
                case 80: if (wave == 1) list.Add(new Aura { Target = AuraTarget.Forerunner, Strength = 2 }); break; // Warrior F
                case 83: if (wave == 0) list.Add(new Aura { Target = AuraTarget.Vanguard, Grant = UnitAbility.Intercept }); break; // Defender R
                case 94: if (wave == 0) list.Add(new Aura { Target = AuraTarget.Forerunner, Strength = 2 }); break; // Magus R
            }
            return list;
        }

        // Recompute every unit's effective stats = its own base passives + all ally auras. Also fills the
        // leader stat bonuses. Call after any board change (summon/move/casualty) and before combat.
        static void RecomputeAuras(PlayerState ps)
        {
            ps.LeaderArmorBonus = 0; ps.LeaderStrBonus = 0;
            // 1. Reset each unit to its OWN base passives for its current wave.
            foreach (var kv in ps.Units)
            {
                BUnit u = kv.Value; if (u == null) continue;
                int wave = kv.Key / 10;
                u.Strength = GetUnitStrength(u.Card, wave);
                u.Atk = AtkOf(u.Card) + u.Strength;
                u.Armor = GetUnitArmor(u.Card, wave);
                u.Abilities = GetUnitAbilities(u.Card, wave);
                u.Cover = PassiveOf(u.Card, wave).Cover;
            }
            // 2. Apply each source's auras onto its recipients.
            foreach (var kv in ps.Units)
            {
                BUnit src = kv.Value; if (src == null || src.IsCorpse) continue;
                int sx = kv.Key / 10, sy = kv.Key % 10;
                foreach (Aura a in AurasOf(src.Card, sx))
                    foreach (int rk in AuraRecipients(a.Target, sx, sy, ps))
                    {
                        if (rk < 0) { ps.LeaderArmorBonus += a.Armor; ps.LeaderStrBonus += a.Strength; continue; }
                        BUnit r; if (!ps.Units.TryGetValue(rk, out r) || r == null || r.IsCorpse) continue;
                        r.Strength += a.Strength; r.Atk += a.Strength; r.Armor += a.Armor; r.Abilities |= a.Grant;
                    }
            }
        }

        // Recipient keys for an aura target relative to a source at (sx,sy). -1 means "the leader".
        static IEnumerable<int> AuraRecipients(AuraTarget t, int sx, int sy, PlayerState ps)
        {
            switch (t)
            {
                case AuraTarget.Forerunner: if (sx < 2) yield return Key(sx + 1, sy); break;
                case AuraTarget.Supporter:  if (sx > 0) yield return Key(sx - 1, sy); break;
                case AuraTarget.Vanguard:   for (int y = 0; y < 3; y++) yield return Key(2, y); break;
                case AuraTarget.Rear:       for (int y = 0; y < 3; y++) yield return Key(0, y); break;
                case AuraTarget.Leader:     yield return -1; break;
                case AuraTarget.Unit:
                    foreach (var kv in ps.Units) yield return kv.Key;
                    yield return -1; // the leader is part of the unit
                    break;
            }
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
        static PacketWriter BuildBuff(int x, int y, byte atktype, bool intercept, bool counter)
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
                .WriteBool(counter)       // cnter
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
            // Use the EFFECTIVE ranged ability (own R.Attack OR an aura-granted one), not just the card's
            // own wave table — otherwise a "Supporter: R.Attack" grantee never shows/uses ranged.
            byte atktype = (byte)(((unit.Abilities & UnitAbility.RangedAttack) != 0) ? 1 : 0);
            bool intercept = (unit.Abilities & UnitAbility.Intercept) != 0;
            bool counter = (unit.Abilities & UnitAbility.Counter) != 0;
            Send(ownerSlot.Me.Ns, BuildBuff(x, y, atktype, intercept, counter).Frame(Op.UpdateBuff));
            if (oppSlot != null) Send(oppSlot.Me.Ns, BuildBuff(x, y, atktype, intercept, counter).Frame(Op.UpdateBuffGet));
        }

        static int GetUnitArmor(ushort card, int wave)    { return PassiveOf(card, wave).Armor; }
        static int GetUnitStrength(ushort card, int wave) { return PassiveOf(card, wave).Strength; }

        // ---- matchmaking / battle bootstrap -----------------------------------

        static readonly Random _rng = new Random();

        class BattleSlot
        {
            public Waiting Me;
            public Waiting Opp;
            public bool FirstPlayer;
            public bool Sent;
            public bool DataSent; // battle_data (opening hand) has been sent to this client
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
            public bool LeadersSpawned; // guard so both leaders are summoned exactly once
            public DateTime TurnDeadline; // wall-clock time the active player's turn auto-advances
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
            public int Orbs;  // order resource: +1 each round, capped at OrbMax; spent on orders
            public int LeaderArmorBonus; // from "Leader: Armor N" auras (recomputed each RecomputeAuras)
            public int LeaderStrBonus;   // from "Leader: Strength N" auras
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
            public CoverType Cover;   // what this unit covers (redirects damage aimed there to itself)
            public bool IsCorpse;     // dead unit occupying space
            public bool RecruitedThisWave;  // cannot attack on the turn recruited
            public bool HasAttackedThisWave; // can only attack once per wave
        }

        // What a Cover:X unit protects. It takes damage that would land on the covered position.
        internal enum CoverType : byte { None = 0, Forerunner = 1, Leader = 2, Vanguard = 3 }

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

            // Both the prime turn_get AND the leader summons are sent together from the single thread
            // that sees BOTH clients' battle_data delivered (below). This is deliberate: the prime
            // turn_get MUST be queued on each client BEFORE that client's leader summon. The leader
            // summon's landing animation does not release the action queue during the mulligan phase,
            // so anything queued after it (like the prime turn_get) never runs. If the prime runs late,
            // anim_turn never sets global.__turn and BOTH players see "You are going second!". Sending
            // both from one thread, prime-first, guarantees the order on each socket (and avoids the
            // cross-thread write race that previously interleaved the leader packets with the prime).
            lock (_battleLock) { slot.DataSent = true; }

            // Spawn leaders exactly ONCE per battle, only after BOTH clients have their battle_data.
            // SendLeaderSummon sends a SummonUnitGet (op6) to the opponent, which crashes a client whose
            // obj_battle_control doesn't exist yet. Guard with Battle.LeadersSpawned under the lock so
            // both threads can't each spawn (that duplicated the leaders).
            BattleSlot oppSlot = null;
            bool doSpawn = false;
            lock (_battleLock)
            {
                if (slot.Opp != null) _battles.TryGetValue(slot.Opp.Ns, out oppSlot);
                if (slot.Battle != null && oppSlot != null && oppSlot.DataSent && !slot.Battle.LeadersSpawned)
                {
                    slot.Battle.LeadersSpawned = true;
                    doSpawn = true;
                }
            }
            if (doSpawn)
            {
                // Prime turn_get first (sets global.__turn for the first/second indicator), then leaders.
                // first player receives player=0, second player receives player=1 (matches SendTurn()).
                SendPrimeTurn(slot);
                SendPrimeTurn(oppSlot);
                SendLeaderSummon(slot, slot.Battle);
                SendLeaderSummon(oppSlot, slot.Battle);
                Log("-> both clients in battle; primed turn + spawned leaders for " + slot.Me.User + " and " + oppSlot.Me.User);
            }
        }

        // Prime turn_get (show_msg=false): sets global.__turn on the client (via anim_turn) so the
        // mulligan "You are going first/second!" indicator is correct. player=0 to the first player,
        // player=1 to the second. Must be sent before that client's leader summon (see SendBattleSetup).
        static void SendPrimeTurn(BattleSlot s)
        {
            Send(s.Me.Ns, new PacketWriter().WriteU16((ushort)(s.FirstPlayer ? 0 : 1)).WriteBool(false).Frame(Op.TurnGet));
            Log("-> primed turn_get (player=" + (s.FirstPlayer ? 0 : 1) + ", show=false) to " + s.Me.User);
        }

        // Shuffle hero cards (excluding leader at slot 0), draw opening hand,

        // Shuffle hero cards (excluding leader at slot 0), draw opening hand,
        // put the rest into PlayerState.Deck.
        static ushort[] InitializeDeckAndDrawHand(Deck d, BattleSlot slot)
        {
            var heroes = new List<ushort>();
            if (d != null) for (int i = 1; i < d.Cards.Length; i++) if (d.Cards[i] != 0) heroes.Add(d.Cards[i]);
            // Testing aid: PTO_NOSHUFFLE=1 keeps the deck in .decks order so the opening hand is
            // deterministic (front-load the cards you want to test). Production shuffles as normal.
            if (Environment.GetEnvironmentVariable("PTO_NOSHUFFLE") != "1")
                lock (_rng) for (int i = heroes.Count - 1; i > 0; i--)
                {
                    int j = _rng.Next(i + 1);
                    var tmp = heroes[i]; heroes[i] = heroes[j]; heroes[j] = tmp;
                }
            else Log("PTO_NOSHUFFLE=1: deck kept in fixed order for deterministic testing");
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

            // Read which of the first 4 displayed hand cards the player wants to shuffle back and redraw.
            // _cancel[i] (a bool) toggles displayed hand position i. Because we deal the opening hand
            // REVERSED (client front-inserts each), slot.Hand[i] == the client's displayed card at i, so
            // position i maps directly to slot.Hand[i].
            var r = new PacketReader(payload, 0);
            bool[] redraw = new bool[4];
            for (int i = 0; i < 4 && i < payload.Length; i++) redraw[i] = r.ReadBool();

            var positions = new List<int>();
            for (int i = 0; i < 4; i++) if (redraw[i] && i < mine.Hand.Count) positions.Add(i);

            if (positions.Count > 0)
            {
                PlayerState ps = mine.Battle.P[mine.P];

                // Remove the marked cards from the hand in DESCENDING position order so an earlier
                // removal doesn't shift the index of a later one. The client mirrors this exact order
                // (anim_mull_back does ds_list_delete(hand, pos)), so both stay identical.
                positions.Sort(); positions.Reverse();
                var returned = new List<ushort>();
                foreach (int pos in positions) { returned.Add(mine.Hand[pos]); mine.Hand.RemoveAt(pos); }

                // Shuffle the returned cards back into the deck so replacements aren't the same cards.
                ps.Deck.AddRange(returned);
                lock (_rng) for (int i = ps.Deck.Count - 1; i > 0; i--)
                {
                    int j = _rng.Next(i + 1);
                    var t = ps.Deck[i]; ps.Deck[i] = ps.Deck[j]; ps.Deck[j] = t;
                }

                // Tell the client to remove each marked card (op37 back): bool scry, bool mul_self,
                // u8 pos, bool deckback. Descending pos to match our removals above.
                foreach (int pos in positions)
                    Send(mine.Me.Ns, new PacketWriter()
                        .WriteBool(false).WriteBool(true).WriteU8((byte)pos).WriteBool(true).Frame(Op.Mulligan));

                // Draw the replacements. Each is front-inserted on the client (anim_draw_card_self does
                // ds_list_insert(hand, 0, ...)), so mirror with Insert(0, ...). op8: bool scry, u8 deck,
                // u16 card, bool phantom, u8 fromOrder, bool gridSelf, u8 x, u8 y.
                int toDraw = positions.Count;
                for (int k = 0; k < toDraw && ps.Deck.Count > 0; k++)
                {
                    ushort nc = ps.Deck[0]; ps.Deck.RemoveAt(0);
                    mine.Hand.Insert(0, nc);
                    byte deckLeft = (byte)Math.Min(255, ps.Deck.Count);
                    Send(mine.Me.Ns, new PacketWriter()
                        .WriteBool(false).WriteU8(deckLeft).WriteU16(nc).WriteBool(false)
                        .WriteU8(0).WriteBool(false).WriteU8(0).WriteU8(0).Frame(Op.DrawCard));
                }
                // NOTE: we intentionally do NOT send the opponent a view of this redraw. A mulligan
                // removes N and draws N, so the opponent's face-down hand count is unchanged anyway;
                // and sending the opponent an anim_mull_back would set THEIR obj_hand.startmull=0 and
                // corrupt their own in-progress mulligan.
                Log("MULLIGAN " + mine.Me.User + ": redrew " + positions.Count + " card(s); hand now [" +
                    string.Join(",", mine.Hand) + "]");
            }
            else
            {
                Log("MULLIGAN " + mine.Me.User + ": kept all cards");
            }

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
                GrantRoundOrbs(b); // round 1 order resource

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

        // Order resource. Each player gains OrbGainPerRound at the start of every round (capped at
        // OrbMax) and spends OrbCostOf(card) when an order is played.
        static void GrantRoundOrbs(Battle b)
        {
            for (int pi = 0; pi < 2; pi++)
                b.P[pi].Orbs = Math.Min(OrbMax, b.P[pi].Orbs + OrbGainPerRound);
        }

        // op62 = recipient's OWN orb count; op63 = the opponent's count (cosmetic display).
        static void SendOrbs(BattleSlot p0, BattleSlot p1, Battle b)
        {
            int o0 = Math.Max(0, b.P[0].Orbs), o1 = Math.Max(0, b.P[1].Orbs);
            Send(p0.Me.Ns, new PacketWriter().WriteU8((byte)o0).Frame(Op.Orbs));
            Send(p0.Me.Ns, new PacketWriter().WriteU8((byte)o1).Frame(Op.OrbsGet));
            Send(p1.Me.Ns, new PacketWriter().WriteU8((byte)o1).Frame(Op.Orbs));
            Send(p1.Me.Ns, new PacketWriter().WriteU8((byte)o0).Frame(Op.OrbsGet));
        }

        // Orb cost of a card's order (card_init orb_cost; most are 1, a few are 2).
        // Order orb costs, indexed by REAL (cardId/2). Data-driven from the game DB (PTOdbDump.txt /
        // card_init.gml OrderOrbCost). Default is 1; only the non-1 costs are listed.
        static readonly int[] _orbCost = BuildOrbCosts();
        static int[] BuildOrbCosts()
        {
            var a = new int[128];
            for (int i = 0; i < a.Length; i++) a[i] = 1;
            int[] free = { 51, 92, 115 };                                   // Zombie, Treasure Hunter, Plague Doctor
            int[] two  = { 29,39,47,49,50,64,83,85,90,94,95,98,109 };       // 2-orb orders
            int[] three= { 61,63,97 };                                      // Curse Knight, Divinity, Dark Knight
            foreach (int r in free)  a[r] = 0;
            foreach (int r in two)   a[r] = 2;
            foreach (int r in three) a[r] = 3;
            return a;
        }
        static int OrbCostOf(ushort cardId)
        {
            int r = cardId / 2;
            return (r >= 0 && r < _orbCost.Length) ? _orbCost[r] : 1;
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
        enum OrderKind { None, DamageSingle, DamageRow, DamageColumn, DamageBlast, DamageAll, HealSingle, HealAll, HealLeader, DrawCards, KillHero, GainActions, Inspire }

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
                case 29: return new OrderEffect { Kind = OrderKind.KillHero };                  // Assassin: Assassinate
                case 35: return new OrderEffect { Kind = OrderKind.DamageBlast,  Amount = 4 }; // Knight: Blast 4
                case 36: return new OrderEffect { Kind = OrderKind.DrawCards,    Amount = 2 }; // Mascot: Inspire, Draw 2 (Draw part)
                case 44: return new OrderEffect { Kind = OrderKind.GainActions,  Amount = 2 }; // Scientist: Haste 2
                case 41: return new OrderEffect { Kind = OrderKind.DamageColumn, Amount = 5 }; // Planestalker: Bombard Column 5
                case 43: return new OrderEffect { Kind = OrderKind.DamageRow,    Amount = 5 }; // Pyromancer: Fire 5
                case 52: return new OrderEffect { Kind = OrderKind.DamageRow,    Amount = 4 }; // Fire Elemental: Fire 4
                case 53: return new OrderEffect { Kind = OrderKind.HealSingle,   Amount = 5 }; // Water Elemental: Cure 5
                case 56: return new OrderEffect { Kind = OrderKind.DamageSingle, Amount = 4 }; // Lightning Elemental: Thunder 4
                default: return new OrderEffect { Kind = OrderKind.None };
            }
        }

        // Effects that resolve against the ENEMY board (so the order must be aimed at the enemy).
        static bool IsEnemyTargeting(OrderKind k)
        {
            return k == OrderKind.DamageSingle || k == OrderKind.DamageRow || k == OrderKind.DamageColumn
                || k == OrderKind.DamageBlast || k == OrderKind.KillHero;
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

            // The client's positional targeting (o_type 0) lets you click EITHER board, but the effect
            // must land on the correct one. `grid` (isgrid) is 1 when the player clicked their OWN board,
            // 0 for the enemy. Damage/kill orders MUST target an enemy hero; a single-target heal must
            // target your own hero. Reject a wrong-board click (and refund) instead of resolving it
            // against the mirrored cell (which let Assassinate kill an enemy by clicking your own unit).
            if (IsEnemyTargeting(eff.Kind) && grid)
            {
                Log("  -> ORDER rejected: " + eff.Kind + " must target an ENEMY hero (own board clicked)");
                GrantAction(mine);
                return;
            }
            if (eff.Kind == OrderKind.HealSingle && !grid)
            {
                Log("  -> ORDER rejected: heal must target your OWN hero (enemy board clicked)");
                GrantAction(mine);
                return;
            }

            // Order resource: must be able to afford it, then spend (client already gates on this,
            // but the server is authoritative). Push the new orb counts so both clients stay synced.
            int orbCost = OrbCostOf(cardId);
            if (ps.Orbs < orbCost)
            {
                Log("  -> ORDER rejected: not enough orbs (" + ps.Orbs + "/" + orbCost + ")");
                GrantAction(mine);
                return;
            }
            ps.Orbs -= orbCost;
            {
                BattleSlot q0 = mine.P == 0 ? mine : theirs, q1 = mine.P == 0 ? theirs : mine;
                if (q0 != null && q1 != null) SendOrbs(q0, q1, b);
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
            RecomputeAuras(ps); RecomputeAuras(opPs); // fresh leader-armor / unit stats for the effect
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
                case OrderKind.DrawCards:
                    DrawCardsFor(mine, theirs, b, eff.Amount);
                    break;
                case OrderKind.KillHero:
                    KillEnemyAt(opPs, gx, enemyGy);
                    break;
                case OrderKind.GainActions:
                    // Haste N: +N actions this turn. ConsumeAction below spends 1 for playing the order,
                    // and GrantAction pushes the new count (op15) to the client.
                    ps.ActionsRemaining += eff.Amount;
                    Log("  gain actions +" + eff.Amount + " (now " + ps.ActionsRemaining + " before order cost)");
                    break;
                case OrderKind.Inspire:
                    // Refresh a friendly hero: clear both summoning-sickness and the attacked flag so it
                    // can act/attack again this wave. SyncUnitStates below re-marks it active (ungreyed).
                    {
                        if (gx == 1 && selfGy == 1) { Log("  inspire: leader ignored"); break; }
                        BUnit iu;
                        if (ps.Units.TryGetValue(Key(gx, selfGy), out iu) && iu != null && !iu.IsCorpse)
                        {
                            iu.RecruitedThisWave = false;
                            iu.HasAttackedThisWave = false;
                            Log("  inspire -> refreshed own (" + gx + "," + selfGy + ") card " + iu.Card);
                        }
                        else Log("  inspire -> no own unit at (" + gx + "," + selfGy + ")");
                    }
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
                case 36: if (wave == 2) return new OrderEffect { Kind = OrderKind.HealSingle,   Amount = 4 };        // Mascot V: Cure 4
                         return new OrderEffect { Kind = OrderKind.Inspire };                                       // Mascot F/R: Inspire
                case 37: if (wave == 2) return new OrderEffect { Kind = OrderKind.HealSingle,   Amount = 4 }; break; // Mystic V: Cure 4
                case 39: if (wave == 1 || wave == 0) return new OrderEffect { Kind = OrderKind.Inspire }; break;     // Overlord F/R: Inspire
                case 42: if (wave == 2) return new OrderEffect { Kind = OrderKind.HealSingle,   Amount = 4 };        // Priestess V: Cure 4
                         if (wave == 1) return new OrderEffect { Kind = OrderKind.HealLeader,   Amount = 2 }; break; // Priestess F: Cure Leader 2
                case 43: if (wave == 2) return new OrderEffect { Kind = OrderKind.DamageBlast,  Amount = 3 };        // Pyromancer V: Blast 3
                         if (wave == 0) return new OrderEffect { Kind = OrderKind.DamageRow,    Amount = 3 }; break; // Pyromancer R: Fire 3
                case 44: if (wave == 1) return new OrderEffect { Kind = OrderKind.DrawCards,    Amount = 2 };        // Scientist F: Draw 2
                         if (wave == 0) return new OrderEffect { Kind = OrderKind.Inspire };                 break; // Scientist R: Inspire
                case 52: if (wave == 0) return new OrderEffect { Kind = OrderKind.DamageRow,    Amount = 2 }; break; // Fire Elem R: Fire 2
                case 53: if (wave == 0) return new OrderEffect { Kind = OrderKind.HealSingle,   Amount = 3 }; break; // Water Elem R: Cure 3
                case 56: if (wave == 1) return new OrderEffect { Kind = OrderKind.DamageSingle, Amount = 2 }; break; // Lightning Elem F: Thunder 2
            }
            return new OrderEffect { Kind = OrderKind.None };
        }

        // Defeat an enemy hero outright (Assassinate / Hero Killer effects). Applies lethal damage so
        // the unit renders dead during the turn and is finalized as a corpse at wave end by
        // ProcessCasualties. Leaders are not heroes and cannot be targeted.
        static void KillEnemyAt(PlayerState opPs, int gx, int gy)
        {
            if (gx < 0 || gx > 2 || gy < 0 || gy > 2) return;
            if (gx == 1 && gy == 1) { Log("    kill -> leader is not a hero, ignored"); return; }
            BUnit u;
            if (opPs.Units.TryGetValue(Key(gx, gy), out u) && u != null && !u.IsCorpse)
            {
                u.Damage = u.Max;
                Log("    kill -> enemy (" + gx + "," + gy + ") card " + u.Card + " DEFEATED");
            }
            else Log("    kill -> no enemy unit at (" + gx + "," + gy + ")");
        }

        // Draw n cards for `mine` as an effect (ignores the draw-action hand limit). Mirrors the client
        // front-insert (Hand.Insert(0,...)) and notifies the opponent (op9) like HandleDraw does.
        static void DrawCardsFor(BattleSlot mine, BattleSlot theirs, Battle b, int n)
        {
            PlayerState ps = b.P[mine.P];
            for (int i = 0; i < n; i++)
            {
                if (ps.Deck == null || ps.Deck.Count == 0) { Log("  draw (effect): deck empty for " + mine.Me.User); break; }
                ushort card = ps.Deck[0]; ps.Deck.RemoveAt(0);
                mine.Hand.Insert(0, card);
                byte deckLeft = (byte)Math.Min(255, ps.Deck.Count);
                Send(mine.Me.Ns, new PacketWriter()
                    .WriteBool(false).WriteU8(deckLeft).WriteU16(card).WriteBool(false)
                    .WriteU8(0).WriteBool(false).WriteU8(0).WriteU8(0).Frame(Op.DrawCard));
                if (theirs != null)
                    Send(theirs.Me.Ns, new PacketWriter()
                        .WriteBool(false).WriteU8(deckLeft).WriteU8((byte)Math.Min(255, mine.Hand.Count)).WriteU8(0)
                        .WriteBool(false).WriteU8(0).WriteU8(0).Frame(Op.DrawCardGet));
                Log("  draw (effect): " + mine.Me.User + " drew card " + card + " (deck now " + ps.Deck.Count + ")");
            }
        }

        // Apply order damage to an enemy unit (or the enemy leader at 1,1). Unit death is resolved
        // at wave end by ProcessCasualties, exactly like attack damage.
        static void DamageEnemyAt(PlayerState opPs, int gx, int gy, int dmg)
        {
            if (gx < 0 || gx > 2 || gy < 0 || gy > 2) return;
            // Cover: redirect this damage to a living coverer of the position (order/spell damage, too).
            bool isLeader = (gx == 1 && gy == 1);
            {
                int cvx, cvy;
                if (TryCover(opPs, gx, gy, isLeader, out cvx, out cvy))
                { Log("    COVER: order damage on (" + gx + "," + gy + ") redirected to (" + cvx + "," + cvy + ")"); gx = cvx; gy = cvy; isLeader = false; }
            }
            if (isLeader) { int ld = Math.Max(1, dmg - opPs.LeaderArmorBonus); opPs.LeaderLife -= ld; Log("    damage -> enemy LEADER -" + ld + " (life " + opPs.LeaderLife + ")"); return; }
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
                Atk = AtkOf(card) + GetUnitStrength(card, gx),
                Max = LifeOf(card),
                Damage = 0,
                Armor = GetUnitArmor(card, gx),
                Strength = GetUnitStrength(card, gx),
                Abilities = GetUnitAbilities(card, gx),
                Cover = PassiveOf(card, gx).Cover,
                IsCorpse = false,
                RecruitedThisWave = true,
                HasAttackedThisWave = false,
            };
            ps.Units[key] = unit;
            RecomputeAuras(ps); // the new unit may grant/receive auras -> refresh effective stats

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

                // Refresh all of the owner's units so aura recipients (neighbors/leader) update on screen.
                SyncUnitStates(mine.P == 0 ? mine : theirs, mine.P == 0 ? theirs : mine, b);
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

            // Fold ally auras into effective stats for BOTH sides before resolving combat.
            RecomputeAuras(b.P[mine.P]); RecomputeAuras(b.P[1 - mine.P]);

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
                    Atk = AtkOf(ps.LeaderCard) + ps.LeaderStrBonus, // + "Leader: Strength N" auras
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
            // Ranged if the attacker has the RangedAttack ability (own OR aura-granted; auras were
            // recomputed above), not just the card's own wave table.
            bool isRanged = !attackerIsLeader && (attacker.Abilities & UnitAbility.RangedAttack) != 0;

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
                // Check if an intercepting unit blocks this attack. It only blocks when it stands IN
                // FRONT of the target in the same column (higher wave = closer to the enemy); a ranged
                // shot passes over allies/enemies but cannot pass over an interceptor. An interceptor
                // behind (or at) the target does not block.
                int interceptWave = FindInterceptInColumn(serverTy, opPs, b.Wave);
                if (interceptWave > tx)
                {
                    // Intercept redirects attack to the interceptor
                    Log("  -> INTERCEPT: ranged attack redirected to (" + interceptWave + "," + serverTy + ")");
                    tx = (byte)interceptWave;
                    targetKey = Key(tx, serverTy);
                    targetIsLeader = false;
                }
            }

            // The unit ORIGINALLY targeted (before any cover redirect) is the one that reacts with
            // Counter: a covered hero still counters even though the coverer absorbs the damage, because
            // Counter reacts to being ATTACKED, not to taking damage. Capture it now, pre-redirect.
            byte origTx = tx, origTy = serverTy;
            BUnit counterUnit = null;
            if (!targetIsLeader) opPs.Units.TryGetValue(Key(origTx, origTy), out counterUnit);

            // --- Cover: a living coverer takes damage aimed at a covered position (its forerunner, the
            // leader, or vanguard). Applies to melee AND ranged; redirect the hit to the coverer. (No
            // jump-in animation yet — the damage just lands on the coverer; animation is a later pass.)
            {
                int cvx, cvy;
                if (TryCover(opPs, targetIsLeader ? 1 : tx, targetIsLeader ? 1 : serverTy, targetIsLeader, out cvx, out cvy))
                {
                    Log("  -> COVER: hit on " + (targetIsLeader ? "leader" : ("(" + tx + "," + serverTy + ")")) +
                        " redirected to coverer (" + cvx + "," + cvy + ")");
                    tx = (byte)cvx; serverTy = (byte)cvy;
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
                // Damage goes to opponent's leader (reduced by "Leader: Armor N" auras).
                int ldmg = Math.Max(1, rawDmg - opPs.LeaderArmorBonus);
                opPs.LeaderLife -= ldmg;
                leaderDied = opPs.LeaderLife <= 0;
                Log("  -> LEADER takes " + ldmg + " (life " + opPs.LeaderLife + (leaderDied ? ", DEAD" : "") + ")");
                // Target the leader's real slot (1,1), NOT the empty front slot the client aimed at.
                // The client's attack animation looks up a unit at the target coords; sending the
                // empty slot makes that lookup undefined and crashes (and mis-addresses the leader's
                // HP update). The leader is always stored at (1,1) on both grids.
                SendAttackAndLeaderUpdate(mine, theirs, ax, ay, 1, 1, ldmg, attackerIsLeader, opPs, isRanged, false, b);
                ApplyVamp(mine, theirs, b, attacker, attackerIsLeader, ldmg);
            }
            else
            {
                BUnit target;
                if (opPs.Units.TryGetValue(targetKey, out target) && !target.IsCorpse)
                {
                    // Hero Killer: this attacker deals DOUBLE damage to enemy heroes (every unit is a
                    // hero; leaders are handled in the targetIsLeader branch and are unaffected).
                    int baseDmg = rawDmg;
                    if (!attackerIsLeader && (attacker.Abilities & UnitAbility.HeroKiller) != 0)
                    {
                        baseDmg = rawDmg * 2;
                        Log("  -> HERO KILLER: double damage vs hero (" + rawDmg + " -> " + baseDmg + ")");
                    }
                    int actualDmg = Math.Max(1, baseDmg - target.Armor);
                    target.Damage += actualDmg;
                    targetDied = target.Damage >= target.Max;
                    Log("  -> unit(" + tx + "," + serverTy + ") card " + target.Card + " takes " + actualDmg +
                        " (damage " + target.Damage + "/" + target.Max + (targetDied ? ", WILL DIE AT WAVE END" : "") + ")");
                    SendAttackAndUpdate(mine, theirs, ax, ay, tx, serverTy, actualDmg, attackerIsLeader, target, isRanged, false, b);
                    ApplyVamp(mine, theirs, b, attacker, attackerIsLeader, actualDmg);
                }
                else
                {
                    // No unit at target, damage goes to leader (reduced by "Leader: Armor N" auras).
                    int ldmg2 = Math.Max(1, rawDmg - opPs.LeaderArmorBonus);
                    opPs.LeaderLife -= ldmg2;
                    leaderDied = opPs.LeaderLife <= 0;
                    Log("  -> LEADER (no unit at target) takes " + ldmg2 + " (life " + opPs.LeaderLife + (leaderDied ? ", DEAD" : "") + ")");
                    // Target the leader's real slot (1,1) — see note above; empty-slot coords crash the client.
                    SendAttackAndLeaderUpdate(mine, theirs, ax, ay, 1, 1, ldmg2, attackerIsLeader, opPs, isRanged, false, b);
                    ApplyVamp(mine, theirs, b, attacker, attackerIsLeader, ldmg2);
                }
            }

            // --- Counter-attack (melee only). The ORIGINALLY-targeted unit counters if it has Counter,
            // is alive (a covered unit is undamaged and still counters), was not recruited this wave, and
            // has attack power. Counter comes from that unit's own cell (origTx, origTy). ---
            if (!isRanged && counterUnit != null && !counterUnit.IsCorpse && counterUnit.Damage < counterUnit.Max
                && counterUnit.Atk > 0 && !counterUnit.RecruitedThisWave
                && (counterUnit.Abilities & UnitAbility.Counter) != 0 && attacker != null && !attackerIsLeader)
            {
                int counterDmg = Math.Max(1, counterUnit.Atk - attacker.Armor);
                attacker.Damage += counterDmg;
                bool attackerDied = attacker.Damage >= attacker.Max;
                Log("  -> COUNTER-ATTACK: unit(" + origTx + "," + origTy + ") hits back for " + counterDmg +
                    " (attacker damage " + attacker.Damage + "/" + attacker.Max + (attackerDied ? ", WILL DIE" : "") + ")");
                SendAttackAndUpdate(theirs, mine, origTx, origTy, ax, ay, counterDmg, false, attacker, false, true, b);
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

        // Cover: if a living unit on ps's board covers the position (gx,gy) (or the leader), return true
        // and output the coverer's cell. The coverer takes the damage aimed at the covered position.
        //   Forerunner -> the unit directly BEHIND the target covers it (leader's "behind" is 0,1)
        //   Leader     -> any Cover:Leader unit protects the leader
        //   Vanguard   -> any Cover:Vanguard unit protects any vanguard hero
        static bool TryCover(PlayerState ps, int gx, int gy, bool isLeader, out int cx, out int cy)
        {
            cx = gx; cy = gy;
            int tx = isLeader ? 1 : gx;
            int ty = isLeader ? 1 : gy;
            BUnit c;
            if (tx - 1 >= 0 && ps.Units.TryGetValue(Key(tx - 1, ty), out c) && c != null && !c.IsCorpse
                && c.Cover == CoverType.Forerunner)
            { cx = tx - 1; cy = ty; return true; }
            if (isLeader)
                foreach (var kv in ps.Units)
                { BUnit u = kv.Value; if (u != null && !u.IsCorpse && u.Cover == CoverType.Leader) { cx = kv.Key / 10; cy = kv.Key % 10; return true; } }
            if (!isLeader && gx == 2)
                foreach (var kv in ps.Units)
                { BUnit u = kv.Value; if (u != null && !u.IsCorpse && u.Cover == CoverType.Vanguard) { cx = kv.Key / 10; cy = kv.Key % 10; return true; } }
            return false;
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
            // AttackGet to opponent — send RAW attacker Y. container_attack_get ALREADY mirrors it
            // (yy = 2 - read), so pre-mirroring here double-mirrors and, for a non-center attacker,
            // makes the opponent look up the attacker at the wrong slot -> no attack animation, and
            // for ranged the projectile that releases the queue never fires (turn freezes).
            if (opp != null)
                Send(opp.Me.Ns, new PacketWriter()
                    .WriteU8(ax).WriteU8(ay).WriteU8(tx).WriteU8(ty)
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
            // AttackGet to opponent — RAW attacker Y (container_attack_get already does yy = 2 - read;
            // pre-mirroring double-mirrors and freezes the opponent on non-center-row attacks).
            if (opp != null)
                Send(opp.Me.Ns, new PacketWriter()
                    .WriteU8(ax).WriteU8(ay).WriteU8(tx).WriteU8(ty)
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
            // Render dead as soon as a unit has lethal damage (or is a corpse). The client's death
            // animation holds the action queue (deathdontunque) until it finishes, so we want it to
            // play during the attacker's turn — NOT batched into the wave-advance burst, where it
            // would block the incoming turn_get and freeze both clients. (Corpses keep Damage=0 after
            // ProcessCasualties, hence the IsCorpse check for 0 HP.)
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

            // Passives depend on the wave (grid_x), so recompute them for the destination wave x2.
            unit.Atk = AtkOf(unit.Card) + GetUnitStrength(unit.Card, x2);
            unit.Strength = GetUnitStrength(unit.Card, x2);
            unit.Armor = GetUnitArmor(unit.Card, x2);
            unit.Abilities = GetUnitAbilities(unit.Card, x2);
            unit.Cover = PassiveOf(unit.Card, x2).Cover;

            // Moving is one of the once-per-wave actions (move / attack / cast) — a hero that moves
            // cannot attack again this wave. Mark it exhausted so it greys out and can't attack until
            // the next wave (or an Inspire refreshes it).
            unit.HasAttackedThisWave = true;

            // Relay to both clients
            Send(mine.Me.Ns, new PacketWriter().WriteU8(x1).WriteU8(y1).WriteU8(x2).WriteU8(y2).Frame(Op.Move));
            if (theirs != null)
                // container_move_unit_get mirrors Y: y1 = 2 - read, y2 = 2 - read
                Send(theirs.Me.Ns, new PacketWriter().WriteU8(x1).WriteU8(y1).WriteU8(x2).WriteU8(y2).Frame(Op.MoveGet));

            // Push the recomputed stats/buff (attack, armor, intercept/counter/ranged icons) for the
            // unit's new wave so the client display matches the server after the move.
            SendUnitUpdateToBoth(mine, theirs, x2, y2, unit, b);
            // Moving changes aura relationships (this unit as source and as recipient), so refresh all.
            SyncUnitStates(mine.P == 0 ? mine : theirs, mine.P == 0 ? theirs : mine, b);

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

            // Notify both clients. Payload is x, y, AND a `goup` bool (container_clear_corpse reads
            // all three) — omitting it made the client read past the buffer end and abort the clear,
            // desyncing the boards. goup=false = the corpse fades in place (normal clear).
            Send(mine.Me.Ns, new PacketWriter().WriteU8(cx).WriteU8(cy).WriteBool(false).Frame(Op.ClearCorpse));
            if (theirs != null)
                // container_clear_corpse_get mirrors Y: yy = 2 - buffer_read
                Send(theirs.Me.Ns, new PacketWriter().WriteU8(cx).WriteU8(cy).WriteBool(false).Frame(Op.ClearCorpseGet));

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

        // Push an owner's leader HP (slot 1,1) to both clients. Used when the leader's life changes
        // outside the normal attack path (e.g. Vamp healing your own leader mid-attack).
        static void SendLeaderHp(BattleSlot ownerSlot, BattleSlot oppSlot, PlayerState ps)
        {
            byte atk = (byte)AtkOf(ps.LeaderCard);
            ushort life = (ushort)Math.Max(0, ps.LeaderLife);
            ushort max = (ushort)ps.LeaderMax;
            bool dead = ps.LeaderLife <= 0;
            Send(ownerSlot.Me.Ns, new PacketWriter()
                .WriteU8(1).WriteU8(1).WriteU8(atk).WriteU16(life)
                .WriteU8(0).WriteU8(0).WriteU8(0).WriteBool(true).WriteBool(dead).WriteU8((byte)max)
                .Frame(Op.UpdateUnit));
            if (oppSlot != null)
                Send(oppSlot.Me.Ns, new PacketWriter()
                    .WriteU8(1).WriteU8(1).WriteU8(atk).WriteU16(life)
                    .WriteU8(0).WriteU8(0).WriteU8(0).WriteBool(true).WriteBool(dead).WriteU8((byte)max)
                    .Frame(Op.UpdateUnitGet));
        }

        // Vamp: when a hero deals damage, heal its owner's leader by that much (capped at leader max).
        static void ApplyVamp(BattleSlot mine, BattleSlot theirs, Battle b, BUnit attacker, bool attackerIsLeader, int dealt)
        {
            if (attackerIsLeader || attacker == null || dealt <= 0) return;
            if ((attacker.Abilities & UnitAbility.Vamp) == 0) return;
            PlayerState ps = b.P[mine.P];
            int before = ps.LeaderLife;
            ps.LeaderLife = Math.Min(ps.LeaderMax, ps.LeaderLife + dealt);
            if (ps.LeaderLife != before)
            {
                Log("  -> VAMP: heal own leader +" + (ps.LeaderLife - before) + " (life " + ps.LeaderLife + ")");
                SendLeaderHp(mine, theirs, ps);
            }
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
            AdvanceTurn(mine, theirs, b);
        }

        // Advance to the next turn / wave / round. Called by HandleEndTurn (op14) and by the turn-
        // timeout thread when the active player exceeds TurnSeconds. Assumes mine.P == b.Active.
        static void AdvanceTurn(BattleSlot mine, BattleSlot theirs, Battle b)
        {
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
                GrantRoundOrbs(b); // +1 orb each round (capped)
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
                    bool ephemeral = (u.Abilities & UnitAbility.Ephemeral) != 0;
                    // Ephemeral heroes are defeated at wave end regardless of damage.
                    if (ephemeral)
                    {
                        Log("  EPHEMERAL: player " + pi + " unit(" + (kvp.Key / 10) + "," + (kvp.Key % 10) + ") card " + u.Card + " expires");
                        deadKeys.Add(kvp.Key); anyCasualties = true;
                        continue;
                    }
                    if (u.Damage >= u.Max)
                    {
                        // Deathproof heroes survive lethal (keep their damage, are NOT defeated).
                        if ((u.Abilities & UnitAbility.Deathproof) != 0)
                        {
                            Log("  DEATHPROOF: player " + pi + " unit(" + (kvp.Key / 10) + "," + (kvp.Key % 10) + ") card " + u.Card + " survives lethal");
                            continue;
                        }
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

            // Start the turn clock for the auto-advance timeout.
            if (act.Battle != null) act.Battle.TurnDeadline = DateTime.UtcNow.AddSeconds(TurnSeconds);

            // Re-send the current orb counts each turn to keep both clients in sync (idempotent).
            // Orbs are gained per round (GrantRoundOrbs) and spent on orders (HandleOrder).
            if (act.Battle != null) SendOrbs(p0, p1, act.Battle);

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
            RecomputeAuras(ps); // fold ally auras into each unit's effective stats before sending them
            if (PacketLog) Log("[SyncPlayerUnits] player=" + ownerP + " wave=" + b.Wave);

            // 1. Leader at (1,1) — active in all waves
            byte leaderAtk = (byte)(AtkOf(ps.LeaderCard) + ps.LeaderStrBonus); // + "Leader: Strength N" auras
            byte leaderArmor = (byte)ps.LeaderArmorBonus;                      // from "Leader: Armor N" auras
            uint leaderLife = (uint)Math.Max(0, ps.LeaderLife);
            bool leaderDead = ps.LeaderLife <= 0;
            ushort leaderMax = (ushort)ps.LeaderMax;

            // UpdateUnit to owner — raw Y
            Send(ownerSlot.Me.Ns, new PacketWriter()
                .WriteU8(1).WriteU8((byte)1).WriteU8(leaderAtk).WriteU16((ushort)leaderLife)
                .WriteU8(0).WriteU8(0).WriteU8(leaderArmor).WriteBool(true)
                .WriteBool(leaderDead).WriteU8((byte)leaderMax)
                .Frame(Op.UpdateUnit));
            // UpdateUnitGet to opponent — raw Y (container_update_unit_get: yy = 2 - read)
            Send(oppSlot.Me.Ns, new PacketWriter()
                .WriteU8(1).WriteU8(1).WriteU8(leaderAtk).WriteU16((ushort)leaderLife)
                .WriteU8(0).WriteU8(0).WriteU8(leaderArmor).WriteBool(true)
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
                // Render dead on lethal damage (or corpse) so the death animation plays during the
                // turn, not in the wave-advance burst (see SendUnitUpdateToBoth). Corpses keep 0 HP.
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
