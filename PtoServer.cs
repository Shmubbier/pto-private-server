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
        public const byte HandCardRemoveGet = 13; // opponent's view of a card leaving their hand: [handsize u8][pos u8][cardid u16]
        public const byte DiscardCard    = 30; // add a card (u8) to the owner's client-side discard pile (container_discard_card)
        public const byte DiscardCardGet = 31; // opponent's view of that discard (container_discard_card_get)
        public const byte DestroyUnit  = 32;  // remove a unit from the owner's board [x u8, y u8]
        public const byte DestroyUnitGet = 33; // opponent's view of that removal (y mirrored client-side)
        public const byte WaveUpdate   = 17;
        public const byte BattleAttackPhase = 21;
        public const byte BattleCasualties = 23;
        public const byte ClearCorpse  = 24;
        public const byte ClearCorpseGet = 25;
        public const byte Move         = 26;
        public const byte MoveGet      = 27;
        public const byte Order        = 28;  // client->server: cast an order / targeted spell
        public const byte SpellCreate  = 44;  // battle: caster cast-pose + spell text; blocks queue (next_que_effect=0) [isMe u8/bool, xx u8, yy u8]
        public const byte Effect       = 41;  // battle: effect projectile/hit [isOrder,xf,yf,gridf,xt,yt,gridt,eid u16,isheal,dmg]
        public const byte SpellRemove  = 45;  // battle: end the cast window; releases queue (next_que_effect=1) [no payload]
        public const byte ScryResult   = 51;  // client->server: scry pick result [u8 handSize][handSize bools: 0=draw this, 1=shuffle back]
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
    enum UnitAbility : int
    {
        None            = 0,
        Intercept       = 1,     // blocks enemy ranged attacks in this column
        RangedAttack    = 2,     // can target any enemy regardless of column
        Counter         = 4,     // deals its attack back when hit by a melee attack
        HeroKiller      = 8,     // deals double damage to enemy heroes
        Vamp            = 16,    // when it deals attack damage, heals ITSELF by that much (removes its own damage)
        Deathproof      = 32,    // immune to INSTANT-KILL effects only (Assassinate/Execute/Takedown/Finisher Kill); ordinary damage still kills
        Ephemeral       = 64,    // on defeat its corpse clears INSTANTLY (leaves no corpse); does not change death timing
        RImmunity       = 128,   // cannot be targeted by ranged attacks
        InterceptKiller = 256,   // deals DOUBLE damage to targets that HAVE Intercept
        Swift           = 512,   // can act the turn it is recruited (no summon-sickness); the attack still costs an action
        Incorporeal     = 1024,  // does not block melee (unit behind can attack / be targeted through it)
        Mercy           = 2048,  // when it deals attack damage, heals a RANDOM damaged friendly hero by that much
        DoubleAttack    = 4096,  // makes its melee attack twice
        Reflect         = 8192,  // reflects attack damage it takes back to the attacker
        FreeAttack      = 16384, // "Free Attack" / Quick Attack: its basic attacks do NOT cost an action
                                 // (official Quick = "does not consume an action"); still exhausts (once/wave)
    }

    class Program
    {
        static int Port = 51338;
        const ushort ClientVersion = 72;
        static bool Verbose = true;
        static bool PacketLog = true;
        const int ActionsPerWave = 3;  // actions granted at the start of each wave (Haste/effects add on top)
        const int OpeningHand = 5;
        const int OrbGainPerWave = 1;   // order resource gained at the start of each wave (3 waves/round)
        const int OrbMax = 3;           // ...capped at this

        // Calibration: PTO_EFXID=<0..25> makes every resolved order also fire container_effect (op41)
        // with that effect id from the leader at the enemy leader (center lane, no Y-mirror ambiguity),
        // so we can identify which eid is a real projectile that RELEASES the client queue (only
        // obj_arrow_effect does). Default -1 = off (normal play, cast-pose only). See [[spell-order-protocol]].
        static readonly int EfxProbeId = ParseIntEnv("PTO_EFXID", -1);
        // Confirmed (user-tested 2026-08-12): eid 0 = obj_single_arrow_target, a queue-safe single-target
        // projectile (plays a projectile AND releases the queue). Used in production for single-target
        // enemy-damage orders/spells. PTO_EFXID overrides it for further calibration.
        const int EfxArrow = 0;
        // Auto-cycle sweep: PTO_EFXCYCLE=1 steps the effect id on EVERY cast (starting from PTO_EFXID),
        // skipping the already-identified projectile eids, so one launch sweeps the at-target splashes.
        static readonly bool EfxCycle = Environment.GetEnvironmentVariable("PTO_EFXCYCLE") == "1";
        static int _efxNext = int.MinValue;
        // eid (0..25) -> client object index (from the client's effects_init/add_effect), for sweep logging.
        static readonly int[] EfxObjIndex = { 76,44,43,45,46,47,48,49,50,52,51,53,54,55,56,57,71,58,60,62,61,54,63,79,79,59 };
        static int ParseIntEnv(string name, int dflt)
        { int v; return int.TryParse(Environment.GetEnvironmentVariable(name), out v) ? v : dflt; }
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

            // Build stamp — the exe's last-write time. Lets us instantly tell from server_live.log whether
            // a running server is stale (compare this to PtoServer.exe's mtime after a rebuild).
            try { Log("BUILD: PtoServer.exe last-written " + File.GetLastWriteTime(System.Reflection.Assembly.GetExecutingAssembly().Location).ToString("yyyy-MM-dd HH:mm:ss")); }
            catch { }

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
                        case Op.BattleReady:SendBattleSetup(ns); MaybeSetupBotOpponent(ns); break;
                        case Op.Mulligan:   HandleMulligan(ns, payload); break;
                        case Op.Summon:     HandleSummon(ns, payload); break;
                        case Op.Attack:     HandleAttack(ns, payload); break;
                        case Op.Move:       HandleMove(ns, payload); break;
                        case Op.Order:      HandleOrder(ns, payload); break;
                        case Op.DrawCard:   HandleDraw(ns); break;
                        case Op.TurnGet:    HandleEndTurn(ns); break;
                        case Op.ClearCorpse:HandleClearCorpse(ns, payload); break;
                        case Op.ScryResult: HandleScryResult(ns, payload); break;
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
            if (p.Intercept)       a |= UnitAbility.Intercept;
            if (p.Counter)         a |= UnitAbility.Counter;
            if (p.HeroKiller)      a |= UnitAbility.HeroKiller;
            if (p.Vamp)            a |= UnitAbility.Vamp;
            if (p.Deathproof)      a |= UnitAbility.Deathproof;
            if (p.Ephemeral)       a |= UnitAbility.Ephemeral;
            if (p.RImmunity)       a |= UnitAbility.RImmunity;
            if (p.InterceptKiller) a |= UnitAbility.InterceptKiller;
            if (p.Swift)           a |= UnitAbility.Swift;
            if (p.Incorporeal)     a |= UnitAbility.Incorporeal;
            if (p.Mercy)           a |= UnitAbility.Mercy;
            if (p.DoubleAttack)    a |= UnitAbility.DoubleAttack;
            if (p.Reflect)         a |= UnitAbility.Reflect;
            if (p.FreeAttack)      a |= UnitAbility.FreeAttack;
            if (IsRangedAtWave(card, wave)) a |= UnitAbility.RangedAttack;
            return a;
        }

        // ---- data-driven self passives (parsed from client card_init.gml power text) -----------
        // "Self" passives apply to the hero that has them (no Forerunner:/Supporter:/Unit:/Leader:/
        // Vanguard: prefix — those are auras, a later tier). Strength here folds in "Melee Strength"
        // (nearly all our melee units), so it's added to the unit's attack stat. Armor is flat damage
        // reduction. Intercept/Counter are combat flags. wave index: 2=Vanguard, 1=Flank, 0=Rear.
        struct SelfPassive { public bool Intercept; public bool Counter; public bool HeroKiller; public bool Vamp; public bool Deathproof; public bool Ephemeral; public bool RImmunity; public bool InterceptKiller; public bool Swift; public bool Incorporeal; public bool Mercy; public bool DoubleAttack; public bool Reflect; public bool FreeAttack; public CoverType Cover; public int Strength; public int Armor; public int Regen; public int Finisher; public int RevengeStrength; }
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

            // -- Vamp (heal ITSELF by attack damage dealt) --
            p[49, 2].Vamp = true; // Vampire    (Vanguard)
            p[97, 2].Vamp = true; // Dark Knight (Vanguard)

            // -- Deathproof (immune to non-attack damage; still dies to attacks) -- (plain self only; auras are a later tier)
            p[109, 2].Deathproof = true; // Druid (Vanguard)

            // -- Ephemeral (on defeat, corpse clears instantly — leaves no corpse) --
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
            p[40, 1].Cover = CoverType.Forerunner; // Paladin Flank:  Forerunner: Cover
            p[53, 1].Cover = CoverType.Forerunner; // Water Elemental Flank: Forerunner: Cover

            // -- R.Immunity (cannot be targeted by ranged) --
            p[48, 2].RImmunity = true; // Trapper Vanguard: "Intercept, R.Immunity"
            // -- Intercept Killer (double damage vs targets that HAVE Intercept) --
            p[31, 2].InterceptKiller = true; // Gunner Vanguard: "R.Attack, Intercept Killer"
            p[68, 2].InterceptKiller = true; // Sniper Vanguard
            p[82, 0].InterceptKiller = true; // Ranger Rear
            // -- Swift (no summon-sickness) --
            p[56, 2].Swift = p[56, 1].Swift = p[56, 0].Swift = true; // Lightning Elemental (all waves)
            // -- Incorporeal (transparent to melee) --
            p[55, 2].Incorporeal = true;  // Dark Elemental  Vanguard
            p[113, 2].Incorporeal = true; // Shadow Elemental Vanguard
            // -- Mercy (heals itself by attack damage dealt) --
            p[63, 1].Mercy = true; // Divinity Flank
            // -- Double Attack (attacks twice) --
            p[81, 2].DoubleAttack = true; // Twinblade Vanguard
            // -- Reflect Damage (reflects damage taken back to attacker) --
            p[87, 2].Reflect = true; // Reflector Vanguard
            // -- Free Attack (Quick Attack: basic attacks cost no action; still exhausts once/wave) --
            p[91, 1].FreeAttack = true; // Commando Flank:   "Free Attack"
            p[93, 2].FreeAttack = true; // Pistolier Vanguard: "Free Attack, R. Attack"
            // -- Regen N (heal N at end of each of your turns) --
            p[67, 2].Regen = 2; // Sage Vanguard: "Intercept, Regen 2"
            // -- Finisher N (this hero's attack deals +N to an ALREADY-damaged hero) --
            p[86, 2].Finisher = 3; // Lancer Vanguard: "Finisher 3"
            // -- Revenge: Strength N (while THIS hero is itself damaged (Damage>0), it attacks for +N) --
            p[47, 2].RevengeStrength = 4; // Templar Vanguard: "Revenge: Strength 4"
            p[58, 2].RevengeStrength = 3; // Adventure Ranger Vanguard: "Revenge: Melee Strength 3"

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
        internal enum AuraTarget : byte { Forerunner, Supporter, Vanguard, Rear, Unit, Leader }
        struct Aura { public AuraTarget Target; public int Strength; public int Armor; public UnitAbility Grant; }

        // A persistent "Ongoing: <region>: <effect>" operation (o_type 1), reserved face-up in a lane. Unlike
        // the hero auras (which come from a hero at a cell), an ongoing is board-global and lasts the game.
        internal struct OngoingOp { public AuraTarget Target; public int Strength; public int Armor; public int Regen; public UnitAbility Grant; public ushort Card; public int Lane; }

        // Map an operation card to its ongoing aura (region + bonus). Returns false if the card is not an
        // ongoing operation. Cards (o_type 1): Templar/Earth Elem Unit:Armor2, Vampire Unit:Vamp, Adventure
        // Ranger Unit:MeleeStr2, Sage Unit:Regen2, Sniper Rear:R.Attack, Defender Vanguard:Intercept, Druid
        // Unit:Deathproof. (Curse Knight's Immortality is a separate wave-limited op, handled on its own.)
        static bool TryOngoingOp(ushort card, out OngoingOp op)
        {
            op = new OngoingOp { Card = card, Lane = -1 };
            switch (card / 2)
            {
                case 47: case 57: op.Target = AuraTarget.Unit; op.Armor = 2; return true;               // Templar / Earth Elemental: Ongoing Unit: Armor 2
                case 49: op.Target = AuraTarget.Unit; op.Grant = UnitAbility.Vamp; return true;          // Vampire: Ongoing Unit: Vamp
                case 58: op.Target = AuraTarget.Unit; op.Strength = 2; return true;                      // Adventure Ranger: Ongoing Unit: Melee Strength 2
                case 67: op.Target = AuraTarget.Unit; op.Regen = 2; return true;                         // Sage: Ongoing Unit: Regen 2
                case 68: op.Target = AuraTarget.Rear; op.Grant = UnitAbility.RangedAttack; return true;  // Sniper: Ongoing Rear: R. Attack
                case 83: op.Target = AuraTarget.Vanguard; op.Grant = UnitAbility.Intercept; return true; // Defender: Ongoing Vanguard: Intercept
                case 109: op.Target = AuraTarget.Unit; op.Grant = UnitAbility.Deathproof; return true;   // Druid: Ongoing Unit: Deathproof
            }
            return false;
        }

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
                case 35: if (wave == 1) list.Add(new Aura { Target = AuraTarget.Leader, Grant = UnitAbility.RImmunity }); break;     // Knight F: Leader: R.Immunity
                case 47: if (wave == 1) list.Add(new Aura { Target = AuraTarget.Supporter, Grant = UnitAbility.RImmunity }); break;  // Templar F: Supporter: R.Immunity
                case 37: if (wave == 1) list.Add(new Aura { Target = AuraTarget.Forerunner, Strength = 2, Armor = 1 }); break; // Mystic F
                case 41: if (wave == 1) { list.Add(new Aura { Target = AuraTarget.Forerunner, Grant = UnitAbility.RangedAttack });
                                          list.Add(new Aura { Target = AuraTarget.Supporter, Grant = UnitAbility.RangedAttack }); } break; // Planestalker F
                case 43: if (wave == 1) list.Add(new Aura { Target = AuraTarget.Forerunner, Grant = UnitAbility.RangedAttack }); break; // Pyromancer F: Forerunner: R.Attack
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
                case 81: if (wave == 1) list.Add(new Aura { Target = AuraTarget.Forerunner, Grant = UnitAbility.DoubleAttack }); break; // Twinblade F: Forerunner: Double Attack
                case 83: if (wave == 0) list.Add(new Aura { Target = AuraTarget.Vanguard, Grant = UnitAbility.Intercept });
                         if (wave == 1) list.Add(new Aura { Target = AuraTarget.Leader, Armor = 2 }); break; // Defender R: Vanguard Intercept / F: Leader Armor 2
                case 86: if (wave == 1) list.Add(new Aura { Target = AuraTarget.Vanguard, Grant = UnitAbility.Deathproof }); break; // Lancer F: Vanguard: Deathproof
                case 109:if (wave == 1) list.Add(new Aura { Target = AuraTarget.Forerunner, Grant = UnitAbility.Deathproof }); break; // Druid F: Forerunner: Deathproof
                case 94: if (wave == 0) list.Add(new Aura { Target = AuraTarget.Forerunner, Strength = 2 }); break; // Magus R
                case 87: if (wave == 1) list.Add(new Aura { Target = AuraTarget.Forerunner, Grant = UnitAbility.Reflect }); break; // Reflector F: Forerunner: Reflect Damage
                case 106:if (wave == 1) list.Add(new Aura { Target = AuraTarget.Leader, Grant = UnitAbility.Reflect }); break;     // Statistician F: Leader: Reflect Damage
            }
            return list;
        }

        // Recompute every unit's effective stats = its own base passives + all ally auras. Also fills the
        // leader stat bonuses. Call after any board change (summon/move/casualty) and before combat.
        static void RecomputeAuras(PlayerState ps)
        {
            ps.LeaderArmorBonus = 0; ps.LeaderStrBonus = 0; ps.LeaderAbilities = UnitAbility.None;
            // 1. Reset each unit to its OWN base passives for its current wave.
            foreach (var kv in ps.Units)
            {
                BUnit u = kv.Value; if (u == null) continue;
                int wave = kv.Key / 10;
                u.Strength = GetUnitStrength(u.Card, wave);
                u.Atk = Math.Max(0, AtkOf(u.Card) + u.Strength + u.TempStrength - u.Enervate); // + Strength Buff, - Enervate
                u.Armor = GetUnitArmor(u.Card, wave);
                u.Finisher = GetUnitFinisher(u.Card, wave);
                u.Revenge = GetUnitRevenge(u.Card, wave);
                u.Abilities = GetUnitAbilities(u.Card, wave);
                if (u.GrantedRanged) u.Abilities |= UnitAbility.RangedAttack; // Lizaveta's persistent grant
                u.Cover = PassiveOf(u.Card, wave).Cover;
            }
            // 2. Apply each source's auras onto its recipients. Silenced units grant nothing.
            foreach (var kv in ps.Units)
            {
                BUnit src = kv.Value; if (src == null || src.IsCorpse || src.Silenced) continue;
                int sx = kv.Key / 10, sy = kv.Key % 10;
                foreach (Aura a in AurasOf(src.Card, sx))
                    foreach (int rk in AuraRecipients(a.Target, sx, sy, ps))
                    {
                        if (rk < 0) { ps.LeaderArmorBonus += a.Armor; ps.LeaderStrBonus += a.Strength; ps.LeaderAbilities |= a.Grant; continue; }
                        BUnit r; if (!ps.Units.TryGetValue(rk, out r) || r == null || r.IsCorpse) continue;
                        r.Strength += a.Strength; r.Atk += a.Strength; r.Armor += a.Armor; r.Abilities |= a.Grant;
                    }
            }
            // 2.5 Leader passive grants that apply to ALL your heroes (a leader-wide aura).
            ApplyLeaderUnitGrants(ps);
            // 2.6 Ongoing operations (persistent region auras placed in reserve). Regen is applied in
            // ApplyRegen, not here (it isn't a combat stat). Source cell is irrelevant for Unit/Vanguard/Rear.
            foreach (var op in ps.Ongoing)
                foreach (int rk in AuraRecipients(op.Target, 0, 0, ps))
                {
                    if (rk < 0) { ps.LeaderArmorBonus += op.Armor; ps.LeaderStrBonus += op.Strength; ps.LeaderAbilities |= op.Grant; continue; }
                    BUnit r; if (!ps.Units.TryGetValue(rk, out r) || r == null || r.IsCorpse) continue;
                    r.Strength += op.Strength; r.Atk += op.Strength; r.Armor += op.Armor; r.Abilities |= op.Grant;
                }
            // 3. Silence: a silenced hero loses ALL innate abilities + applied status (auras, Str buff,
            // Shield) — only its base attack remains. Applied last so nothing above leaks through.
            foreach (var kv in ps.Units)
            {
                BUnit u = kv.Value; if (u == null || !u.Silenced) continue;
                u.Abilities = UnitAbility.None; u.Cover = CoverType.None;
                u.Strength = 0; u.Armor = 0; u.Finisher = 0; u.Revenge = 0; u.TempStrength = 0; u.Shield = false; u.Enervate = 0;
                u.Atk = AtkOf(u.Card);
            }
        }

        // Leader passives that behave like a "Unit: X" aura — they grant an ability/stat to ALL of your
        // heroes (and sometimes the leader). Applied every RecomputeAuras (after ally auras, before
        // Silence). Silenced heroes are skipped. Indexed by leader REAL (LeaderCard/2).
        static void ApplyLeaderUnitGrants(PlayerState ps)
        {
            int lr = ps.LeaderCard / 2;
            foreach (var kv in ps.Units)
            {
                BUnit u = kv.Value;
                if (u == null || u.IsCorpse || u.Silenced) continue;
                switch (lr)
                {
                    case 3:  u.Armor += 1; break;                                 // Cadenza: your heroes have Armor 1
                    case 4:  if (ps.IsFirstPlayer) { u.Strength += 3; u.Atk += 3; } else u.Armor += 1; break; // Kallistar: First Player -> Strength 3, else Armor 1
                    case 6:  u.Strength += 2; u.Atk += 2; break;                  // Hikaru: Melee Attack Strength 2
                    case 12: u.Abilities |= UnitAbility.Swift; break;             // Borneo: all your heroes have Swift
                    case 17: u.Abilities |= UnitAbility.RangedAttack; break;      // Zaamassal Kett: gain Ranged Attack
                    case 20:
                    case 75: if (u.Revenge < 5) u.Revenge = 5; break;            // Eligor Larington / Eligor: Revenge: Strength 5
                    case 22: u.Abilities |= UnitAbility.Vamp; break;              // Demitras: gain Vamp
                    case 71: if (kv.Key / 10 == 2) u.Abilities |= UnitAbility.Intercept | UnitAbility.Deathproof; break; // Cague: your Vanguard heroes have Intercept + Deathproof
                }
                // Magdelina: persistent +Strength to all your heroes each round.
                if (ps.RoundStrBonus > 0) { u.Strength += ps.RoundStrBonus; u.Atk += ps.RoundStrBonus; }
                // Rexan / Hepzibah: your Zombies (card 102 = REAL 51) have +1 Melee Strength.
                if ((lr == 16 || lr == 23) && u.Card / 2 == 51) { u.Strength += 1; u.Atk += 1; }
            }
            if (lr == 6) ps.LeaderStrBonus += 2; // Hikaru: the Leader also has Melee Attack Strength 2
            if (lr == 77) // Ivo: the Leader has Strength 1 for each hero in your unit
            {
                int heroes = 0;
                foreach (var kv in ps.Units) if (kv.Value != null && !kv.Value.IsCorpse) heroes++;
                ps.LeaderStrBonus += heroes;
            }
            // Star Knight Iri (per-defeat) + Magdelina (per-round) also boost the LEADER's attack.
            ps.LeaderStrBonus += ps.LeaderPermStr + ps.RoundStrBonus;
        }

        // Leader passive that fires at the END OF the leader-owner's turn (heal / draw). ps = ending player.
        static void ApplyLeaderEndOfTurn(BattleSlot mine, BattleSlot theirs, Battle b)
        {
            PlayerState ps = b.P[mine.P];
            int lr = ps.LeaderCard / 2;
            if (lr == 8) // Kavri: heal 2 from each hero + leader in the CURRENT wave
            {
                int n = 0;
                foreach (var kv in ps.Units) { BUnit u = kv.Value; if (u != null && !u.IsCorpse && kv.Key / 10 == b.Wave && u.Damage > 0) { u.Damage = Math.Max(0, u.Damage - 2); n++; } }
                ps.LeaderLife = Math.Min(ps.LeaderMax, ps.LeaderLife + 2);
                Log("  LEADER (Kavri): healed 2 from wave " + b.Wave + " (" + n + " heroes + leader)");
            }
            else if (lr == 24 && mine.Hand.Count < 5) // Marmelee Greyheart: draw 1 if fewer than 5 cards
            {
                DrawCardsFor(mine, theirs, b, 1);
                Log("  LEADER (Marmelee): drew 1 (hand < 5)");
            }
        }

        // Fired at every own-hero death site. `ownerSlot` owns the dying hero `deadUnit`; `oppSlot` is the
        // enemy. Handles Star Knight Iri (own leader), Shekhtur (own leader), and Rexan (the ENEMY leader).
        static void OnHeroDefeated(BattleSlot ownerSlot, BattleSlot oppSlot, Battle b, BUnit deadUnit)
        {
            if (ownerSlot == null || b == null) return;
            PlayerState ps = b.P[ownerSlot.P];
            PlayerState opPs = oppSlot != null ? b.P[oppSlot.P] : null;
            int lr = ps.LeaderCard / 2;

            // Star Knight Iri: your leader gains +1 Strength (persistent) and heals 3.
            if (lr == 25)
            {
                ps.LeaderPermStr += 1;
                ps.LeaderLife = Math.Min(ps.LeaderMax, ps.LeaderLife + 3);
                Log("  LEADER (Star Knight Iri): +1 Strength & heal 3 (leader life " + ps.LeaderLife + ")");
            }
            // Shekhtur: the defeated hero makes a free melee attack at a random enemy hero (parting shot).
            if (lr == 2 && opPs != null && oppSlot != null && deadUnit != null)
            {
                var alive = new List<int>();
                foreach (var kv in opPs.Units) if (kv.Value != null && !kv.Value.IsCorpse) alive.Add(kv.Key);
                if (alive.Count > 0)
                {
                    int k; lock (_rng) k = alive[_rng.Next(alive.Count)];
                    BUnit t = opPs.Units[k];
                    int actual = Math.Max(1, Math.Max(1, deadUnit.Atk) - t.Armor);
                    t.Damage += actual;
                    Log("  LEADER (Shekhtur): defeated hero's parting shot -> " + actual + " to enemy (" + (k / 10) + "," + (k % 10) + ")");
                    SendUnitUpdateToBoth(oppSlot, ownerSlot, k / 10, k % 10, t, b);
                }
            }
            // Rexan (the ENEMY's leader): when this hero dies, the enemy summons a Zombie in a random
            // empty Vanguard slot on their board.
            if (opPs != null && oppSlot != null && opPs.LeaderCard / 2 == 16)
            {
                var empties = new List<int>();
                for (int lane = 0; lane < 3; lane++) if (!opPs.Units.ContainsKey(Key(2, lane))) empties.Add(Key(2, lane));
                if (empties.Count > 0)
                {
                    int ek; lock (_rng) ek = empties[_rng.Next(empties.Count)];
                    PlaceUnit(oppSlot, ownerSlot, b, 102, ek / 10, ek % 10, 0, true);
                    Log("  LEADER (Rexan): enemy hero died -> summoned a Zombie at (" + (ek / 10) + "," + (ek % 10) + ")");
                }
            }
        }

        // Leader passives that fire at the END OF EACH ROUND (both players). Lixis can kill, so the
        // caller must re-check for a rout afterward.
        static void ApplyLeaderEndOfRound(Battle b, BattleSlot p0, BattleSlot p1)
        {
            for (int pi = 0; pi < 2; pi++)
            {
                PlayerState ps = b.P[pi], opPs = b.P[1 - pi];
                BattleSlot own = pi == 0 ? p0 : p1, opp = pi == 0 ? p1 : p0;
                int lr = ps.LeaderCard / 2;
                if (lr == 1) // Lixis: 3 to all rival heroes + 1 to rival leader
                {
                    var keys = new List<int>(); foreach (var kv in opPs.Units) keys.Add(kv.Key);
                    foreach (int k in keys) DamageEnemyAt(opPs, k / 10, k % 10, 3);
                    opPs.LeaderLife -= 1;
                    Log("  LEADER (Lixis): 3 to all rival heroes + 1 to rival leader (rival leader life " + opPs.LeaderLife + ")");
                    ProcessImmediateDeaths(opPs, opp, own, b);
                }
                else if (lr == 18) // Magdelina: each hero + leader in your unit gains Strength 1 (persistent)
                {
                    ps.RoundStrBonus += 1;
                    Log("  LEADER (Magdelina): +1 Strength to all your heroes + leader (now +" + ps.RoundStrBonus + ")");
                }
                else if (lr == 14) // Sagas: cast Replicate free (copy a random own hero's card to hand)
                {
                    var heroes = new List<int>(); foreach (var kv in ps.Units) if (kv.Value != null && !kv.Value.IsCorpse) heroes.Add(kv.Key);
                    if (heroes.Count > 0) { int hk; lock (_rng) hk = heroes[_rng.Next(heroes.Count)]; AddCardToHand(own, opp, b, pi, ps.Units[hk].Card); Log("  LEADER (Sagas): Replicate -> copied card " + ps.Units[hk].Card + " to hand"); }
                }
                else if (lr == 10) // Mikhail: cast Resurrect (revive one own corpse at full HP)
                {
                    int ck = -1; foreach (var kv in ps.Units) if (kv.Value != null && kv.Value.IsCorpse) { ck = kv.Key; break; }
                    if (ck >= 0)
                    {
                        ushort rc = ps.Units[ck].Card; int cx = ck / 10, cy = ck % 10;
                        ps.Units.Remove(ck); SendClearCorpse(own, opp, cx, cy); PlaceUnit(own, opp, b, rc, cx, cy, 0, true);
                        Log("  LEADER (Mikhail): resurrected a corpse at (" + cx + "," + cy + ")");
                    }
                }
            }
        }

        // Leader passive that fires whenever the owner PLAYS AN ORDER (Rukyuk / Tatsumi). Called from
        // HandleOrder after the order resolves. Can damage the rival (Rukyuk) so it checks for a win.
        static void ApplyLeaderOnOrder(BattleSlot mine, BattleSlot theirs, Battle b)
        {
            PlayerState ps = b.P[mine.P], opPs = b.P[1 - mine.P];
            int lr = ps.LeaderCard / 2;
            if (lr == 7) // Rukyuk Amberdeen: Bombard 3 (3 to a random rival hero)
            {
                var alive = new List<int>();
                foreach (var kv in opPs.Units) if (kv.Value != null && !kv.Value.IsCorpse) alive.Add(kv.Key);
                if (alive.Count > 0)
                {
                    int k; lock (_rng) k = alive[_rng.Next(alive.Count)];
                    DamageEnemyAt(opPs, k / 10, k % 10, 3);
                    Log("  LEADER (Rukyuk): Bombard 3 -> rival (" + (k / 10) + "," + (k % 10) + ")");
                    if (theirs != null) ProcessImmediateDeaths(opPs, theirs, mine, b);
                }
            }
            else if (lr == 13 && !ps.OrderedThisTurn) // Tatsumi: first order each turn -> draw 1 + gain 1 action
            {
                DrawCardsFor(mine, theirs, b, 1);
                ps.ActionsRemaining += 1;
                Log("  LEADER (Tatsumi): first order this turn -> drew 1 + gained 1 action");
            }
            ps.OrderedThisTurn = true;
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
        static PacketWriter BuildBuff(int x, int y, byte atktype, bool intercept, bool counter, bool incorp, bool shield, bool silence, bool decoy, bool immort)
        {
            return new PacketWriter()
                .WriteU8((byte)x).WriteU8((byte)y).WriteU8(atktype)
                .WriteBool(intercept)     // inter
                .WriteU8(255)             // ongo (s8 -1)
                .WriteBool(false)         // covered
                .WriteU8(255).WriteU8(255)// coveredx, coveredy (s8 -1)
                .WriteBool(incorp)        // incorp (transparent to melee); 0 also avoids the return_noone_infront crash
                .WriteBool(shield)        // shield
                .WriteBool(silence)       // silence
                .WriteBool(false)         // rev
                .WriteBool(counter)       // cnter
                .WriteBool(immort)        // immort (vanguard Immortality)
                .WriteBool(false)         // deathpro
                .WriteU8((byte)x)         // adpx = grid_x (wave) -> spellid follows the unit on move
                .WriteBool(true)          // can_attack
                .WriteU8(99)              // m_actions
                .WriteU8(0)               // actions
                .WriteBool(decoy);        // dec
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
            bool incorp = (unit.Abilities & UnitAbility.Incorporeal) != 0;
            bool immort = ownerSlot.Battle != null && IsImmortalVanguard(ownerSlot.Battle.P[ownerSlot.P], x);
            Send(ownerSlot.Me.Ns, BuildBuff(x, y, atktype, intercept, counter, incorp, unit.Shield, unit.Silenced, unit.Decoy, immort).Frame(Op.UpdateBuff));
            if (oppSlot != null) Send(oppSlot.Me.Ns, BuildBuff(x, y, atktype, intercept, counter, incorp, unit.Shield, unit.Silenced, unit.Decoy, immort).Frame(Op.UpdateBuffGet));
        }

        static int GetUnitArmor(ushort card, int wave)    { return PassiveOf(card, wave).Armor; }
        static int GetUnitStrength(ushort card, int wave) { return PassiveOf(card, wave).Strength; }
        static int GetUnitFinisher(ushort card, int wave) { return PassiveOf(card, wave).Finisher; }
        static int GetUnitRevenge(ushort card, int wave)  { return PassiveOf(card, wave).RevengeStrength; }

        // ---- matchmaking / battle bootstrap -----------------------------------

        static readonly Random _rng = new Random();
        // Finished-hero card ids Polymorph can turn a unit into (safe, playable stats).
        static readonly ushort[] PolyPool = { 56, 58, 60, 62, 64, 68, 70, 72, 78, 84, 86, 88, 92, 94, 96, 98, 100 };

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
            public int BotP = -1;  // player index controlled by the AI bot, or -1 if none
            public bool BotActing; // guard so only one bot-turn thread runs at a time
        }

        internal class PlayerState
        {
            public Dictionary<int, BUnit> Units = new Dictionary<int, BUnit>();
            public int LeaderLife = 20;
            public int LeaderMax = 20;
            public ushort LeaderCard;
            public List<ushort> Deck = new List<ushort>(); // draw pile (hero cards remaining)
            public List<ushort> Discard = new List<ushort>(); // discard pile (played orders, cleared corpses, banished/entombed cards) — used by Phantom
            public List<ushort> PhantomCards = new List<ushort>(); // card ids added by Phantom this turn: printed life = 1, vanish from hand at end of turn
            public List<ushort> PendingScry = null; // cards pulled off the top of the deck for an open Scry UI, awaiting the op51 pick
            public int ActionsRemaining;
            public int Orbs;  // order resource: +1 each round, capped at OrbCap; spent on orders
            public int OrbCap = 3;  // max orbs (OrbMax default; Malandrax raises his own to 6)
            public int LeaderArmorBonus; // from "Leader: Armor N" auras (recomputed each RecomputeAuras)
            public int LeaderStrBonus;   // from "Leader: Strength N" auras (recomputed each RecomputeAuras)
            public UnitAbility LeaderAbilities; // abilities granted to the leader by "Leader: X" / "Unit: X" auras
            public ushort LastSummonedCard;     // last hero recruited this battle (for the Summon spell = copy it)
            public bool LeaderShield;           // negate the next damage to the leader, then lose it (Shield Leader)
            // Traps. The client plays a Trap card (card_init o_type==2) as a SUMMON (op10) to the reserve
            // row grid_x==3 — NOT as an order (op28). HandleSummon arms it here and renders it face-down
            // (op5/op6 istrap=true). It stays armed until the rival's next matching action (round >= 2),
            // which it negates (rival still spends the action). *Lane = the reserve grid_y (0..2) holding
            // the face-down card, so we can clear it (op32) when it triggers. -1 = not armed.
            public bool TrapCancelAttack;       // Reflector: cancels the rival's next attack, then Backlashes the attacker
            public bool TrapCancelSpell;        // Statistician: cancels the rival's next wave-spell; owner draws 1
            public bool TrapCancelOrder;        // Mastermind: cancels the rival's next order
            public int TrapAttackLane = -1;
            public int TrapSpellLane = -1;
            public int TrapOrderLane = -1;
            // Ongoing effects.
            public int ImmortalWaves;    // >0: your VANGUARD (grid_x==2) heroes can't die by any means; decrements each wave (Curse Knight order)
            public int ImmortalLane = -1; // reserve lane holding the face-up Immortality operation card (-1 = none)
            public List<OngoingOp> Ongoing = new List<OngoingOp>(); // persistent unit/region auras (o_type 1 operations), one per reserve lane
            public bool RestructureFree; // this turn, MOVE and CLEAR CORPSE cost no action (Homunculus/Bannerman); cleared at end of your turn
            public bool OrderedThisTurn; // has an order been played this turn (for Tatsumi's first-order-per-turn passive)
            public int LeaderPermStr;    // Star Knight Iri: +1 leader attack per own hero defeated (persistent)
            public int RoundStrBonus;    // Magdelina: +1 to all your heroes + leader each round (persistent, stacks)
            public bool IsFirstPlayer;   // holds the first-player token this round (for Kallistar's conditional aura)
        }

        internal class BUnit
        {
            public ushort Card;
            public int Atk;
            public int Max;           // max life (total)
            public int Damage;        // accumulated damage (for wave-end casualties)
            public int Armor;         // flat damage reduction
            public int Strength;      // bonus attack damage
            public int Finisher;      // +N attack damage vs an already-damaged hero (Finisher N passive)
            public int Revenge;       // +N attack while THIS hero is itself damaged (Revenge: Strength N)
            public bool Shield;       // negate the next damage event to this unit, then lose it
            public int TempStrength;  // Strength Buff: +N attack until the end of the wave
            public int Enervate;      // -N attack (Enervate debuff); persistent until removed/silenced
            public bool Silenced;     // loses all innate abilities + applied status (Shield/Str buff) until removed
            public bool Decoy;        // enemy attacks must target this hero while it lives, if it's a legal target
            public UnitAbility Abilities;
            public CoverType Cover;   // what this unit covers (redirects damage aimed there to itself)
            public bool IsCorpse;     // dead unit occupying space
            public bool DeathSent;    // the death update was already sent (don't re-send / re-trigger the anim)
            public bool CorpseFresh;  // died THIS turn — its death anim may still be playing client-side, so
                                      // don't re-send it (a repeat dead=1 while dead==0 restarts the anim).
                                      // Cleared at the next turn start, after which re-sends are safe and
                                      // needed (anim_wave_update un-greys corpses; a re-send re-greys them).
            public bool RecruitedThisWave;  // cannot attack on the turn recruited
            public bool HasAttackedThisWave; // can only attack once per wave
            public bool GrantedRanged;      // Lizaveta leader active: persistently granted Ranged Attack
        }

        // What a Cover:X unit protects. It takes damage that would land on the covered position.
        internal enum CoverType : byte { None = 0, Forerunner = 1, Leader = 2, Vanguard = 3 }

        static int Key(int x, int y) { return x * 10 + y; }
        static readonly object _battleLock = new object();
        static readonly Dictionary<NetworkStream, BattleSlot> _battles = new Dictionary<NetworkStream, BattleSlot>();

        // The bot needs a NON-NULL NetworkStream to be a dictionary key (Dictionary throws on null keys).
        // Make a real loopback socket pair: the bot's stream is written to normally, and a drain thread
        // reads+discards the far end so those writes never block.
        internal static NetworkStream MakeBotStream()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var cs = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            cs.Connect(IPAddress.Loopback, ((IPEndPoint)l.LocalEndpoint).Port);
            var ss = l.AcceptSocket();
            l.Stop();
            var far = new NetworkStream(ss, true);
            new Thread(() => { var buf = new byte[4096]; try { while (far.Read(buf, 0, buf.Length) > 0) { } } catch { } }) { IsBackground = true }.Start();
            return new NetworkStream(cs, true);
        }

        internal static void StartBattle(Waiting a, Waiting b, int battleId)
        {
            Log("MATCH: " + a.User + " vs " + b.User + " (battle " + battleId + ")");
            var battle = new Battle();
            if (a.IsBot) battle.BotP = 1; else if (b.IsBot) battle.BotP = 0;
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

        // ---- AI bot (single-player) -------------------------------------------
        // The bot is a virtual player with a null stream. Its moves are made by BUILDING the same
        // payloads a client would send and calling the normal handlers (HandleSummon/HandleAttack/
        // HandleEndTurn) with ns=null, so the human sees identical updates. Enabled via PTO_BOT=1.
        internal static bool BotEnabled = Environment.GetEnvironmentVariable("PTO_BOT") == "1";

        // Called after the human enters the battle: finish the bot's server-side setup (deck/hand) and
        // auto-mulligan it (keep all) so the human's mulligan starts turn 1.
        static void MaybeSetupBotOpponent(NetworkStream humanNs)
        {
            BattleSlot human, bot = null;
            lock (_battleLock)
            {
                if (!_battles.TryGetValue(humanNs, out human) || human.Opp == null || !human.Opp.IsBot) return;
                _battles.TryGetValue(human.Opp.Ns, out bot); // Opp.Ns == null key
            }
            if (bot == null) return;
            SendBattleSetup(bot.Me.Ns);                    // null ns: sets up the bot's deck/hand, no-op sends
            lock (_battleLock) bot.Mulliganed = true;
            Log("BOT: set up and auto-mulliganed (keep all)");
        }

        // Kick off the bot's turn on a background thread (so it doesn't block the caller's handler).
        static void StartBotTurn(BattleSlot bot)
        {
            Battle b = bot.Battle;
            if (b == null) return;
            lock (_battleLock) { if (b.BotActing || b.Over || b.Active != bot.P) return; b.BotActing = true; }
            var t = new Thread(() =>
            {
                try { BotTakeTurn(bot); }
                catch (Exception ex) { Log("BOT error: " + ex.Message); }
                finally { lock (_battleLock) b.BotActing = false; }
            }) { IsBackground = true };
            t.Start();
        }

        // Play out the bot's turn(s): take a few actions, end the turn, and repeat while it's still the
        // bot's turn (consecutive turns at a wave/round boundary are handled in this same loop).
        static void BotTakeTurn(BattleSlot bot)
        {
            Battle b = bot.Battle;
            while (b != null && !b.Over)
            {
                bool myTurn; lock (_battleLock) myTurn = (b.Started && b.Active == bot.P);
                if (!myTurn) break;
                Thread.Sleep(1100); // let the human's turn_get settle / pace the moves
                for (int step = 0; step < 12 && !b.Over; step++)
                {
                    int actions; lock (_battleLock) actions = (b.Active == bot.P) ? b.P[bot.P].ActionsRemaining : 0;
                    if (actions <= 0) break;
                    if (!BotDoOneAction(bot)) break;
                    Thread.Sleep(1000);
                }
                if (b.Over) break;
                Thread.Sleep(600);
                HandleEndTurn(bot.Me.Ns); // ns=null -> _battles[null] = bot; advances the turn
                Thread.Sleep(300);
            }
        }

        // The bot keeps roughly this many cards in hand — it draws (spending an ACTION, like a human)
        // when below this, so hand-effects (Banish/Wild Summon) have targets without breaking the action
        // economy (the old free turn-start refill let the bot draw AND summon 3, bypassing the rules).
        const int BotHandTarget = 3;

        // Decide and perform ONE bot action. Returns false when the bot has nothing useful to do.
        static bool BotDoOneAction(BattleSlot bot)
        {
            Battle b = bot.Battle;
            byte[] payload = null; int kind = 0; // 1=summon, 2=attack, 3=draw
            lock (_battleLock)
            {
                if (b.Over || b.Active != bot.P) return false;
                PlayerState ps = b.P[bot.P], hp = b.P[1 - bot.P];
                if (ps.ActionsRemaining <= 0) return false;

                // 0) Draw (costs an action, like a human) if the hand is thin — keeps a buffer so hand-
                //    effects have targets, without the old free-refill rule break.
                if (bot.Hand.Count < BotHandTarget && ps.Deck != null && ps.Deck.Count > 0)
                    kind = 3;

                // 1) Summon a hero into an empty slot of the current wave.
                if (kind == 0 && bot.Hand.Count > 0)
                    for (int col = 0; col < 3 && kind == 0; col++)
                    {
                        if (b.Wave == 1 && col == 1) continue;           // leader cell
                        if (ps.Units.ContainsKey(Key(b.Wave, col))) continue;
                        payload = new byte[] { 0, (byte)b.Wave, (byte)col, 0 }; kind = 1; // summon hand[0]
                    }

                // 2) Otherwise attack (round 2+) with a ready unit in the current wave.
                if (kind == 0 && b.Round >= 2)
                    foreach (var kv in ps.Units)
                    {
                        if (kv.Key / 10 != b.Wave) continue;
                        BUnit u = kv.Value;
                        if (u == null || u.IsCorpse || u.HasAttackedThisWave || u.RecruitedThisWave) continue;
                        int ay = kv.Key % 10;
                        bool ranged = (u.Abilities & UnitAbility.RangedAttack) != 0;
                        // Melee is blocked by an ally in front (same rule the server now enforces); skip it.
                        if (!ranged && !IsFrontmostAliveInColumn(kv.Key / 10, ay, ps)) continue;
                        int tx, ty;
                        // If the human has a reachable decoy, the attack MUST hit it (else the server rejects).
                        int decoy = ForcedDecoyCell(hp);
                        if (decoy >= 0)
                        {
                            int dwave = decoy / 10, dlane = decoy % 10;
                            if (!ranged && dlane != ay) continue; // melee can't reach a decoy in another lane
                            tx = dwave; ty = dlane;
                            payload = new byte[] { 0, 0, (byte)b.Wave, (byte)ay, (byte)tx, (byte)ty }; kind = 2; break;
                        }
                        if (BotPickTarget(hp, ay, ranged, out tx, out ty))
                        { payload = new byte[] { 0, 0, (byte)b.Wave, (byte)ay, (byte)tx, (byte)ty }; kind = 2; break; }
                    }
            }
            if (kind == 3) { Log("BOT: draw"); HandleDraw(bot.Me.Ns); return true; } // consumes an action + syncs the human's bot-hand count (op9)
            if (kind == 1) { Log("BOT: summon -> (" + payload[1] + "," + payload[2] + ")"); HandleSummon(bot.Me.Ns, payload); return true; }
            if (kind == 2) { Log("BOT: attack (" + payload[2] + "," + payload[3] + ") -> (" + payload[4] + "," + payload[5] + ")"); HandleAttack(bot.Me.Ns, payload); return true; }
            return false;
        }

        // Pick a valid target for a bot attacker in lane `lane`. Ranged: frontmost enemy anywhere, else
        // leader. Melee: frontmost enemy in the same lane, else the leader from the center lane.
        static bool BotPickTarget(PlayerState hp, int lane, bool ranged, out int tx, out int ty)
        {
            tx = ty = 0; BUnit u;
            if (ranged)
            {
                for (int col = 0; col < 3; col++)
                    for (int w = 2; w >= 0; w--)
                        if (hp.Units.TryGetValue(Key(w, col), out u) && u != null && !u.IsCorpse) { tx = w; ty = col; return true; }
                tx = 1; ty = 1; return true; // no units left -> shoot the leader
            }
            for (int w = 2; w >= 0; w--)
                if (hp.Units.TryGetValue(Key(w, lane), out u) && u != null && !u.IsCorpse) { tx = w; ty = lane; return true; }
            // melee can only reach the leader down the center lane when its Vanguard is clear
            if (lane == 1 && !(hp.Units.TryGetValue(Key(2, 1), out u) && u != null && !u.IsCorpse)) { tx = 1; ty = 1; return true; }
            return false;
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
                ps.OrbCap = (leader / 2 == 111) ? 6 : OrbMax; // Malandrax: "maximum of 6 Orbs"
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
                b.P[0].IsFirstPlayer = (b.First == 0); b.P[1].IsFirstPlayer = (b.First == 1); // Kallistar
                b.P[b.Active].ActionsRemaining = ActionsPerWave;
                b.Started = true;
                // Orbs start at 0. No orbs are gained during the round-1 ceasefire — they only begin
                // accruing (+1/wave) once the ceasefire ends (round >= 2). See AdvanceTurn.

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
            byte actB = (byte)Math.Max(0, Math.Min(255, actions));
            Send(slot.Me.Ns, new PacketWriter().WriteU8(actB).Frame(Op.Action));
            // Mirror the count to the opponent as the cosmetic "enemy action points" (op16,
            // global.__player_other_actions) — otherwise the enemy count always displays 0.
            if (slot.Opp != null && slot.Opp.Ns != null)
                Send(slot.Opp.Ns, new PacketWriter().WriteU8(actB).Frame(Op.ActionGet));
        }

        // Order resource. Each player gains OrbGainPerWave at the start of every wave (3 waves/round,
        // capped at OrbMax) and spends OrbCostOf(card) when an order is played.
        static void GrantWaveOrbs(Battle b)
        {
            for (int pi = 0; pi < 2; pi++)
                b.P[pi].Orbs = Math.Min(b.P[pi].OrbCap, b.P[pi].Orbs + OrbGainPerWave);
            Log("  ORBS granted (+ " + OrbGainPerWave + ", cap " + OrbMax + "): P0=" + b.P[0].Orbs + " P1=" + b.P[1].Orbs);
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

        // ---- scry pick (op 51) ------------------------------------------------
        // The player finished the Scry UI. Payload = u8 handSize, then handSize bools. The client
        // front-inserts the scry cards (reversing our send order), so packet bool[k] maps directly to
        // the k-th card we sent (PendingScry[k]). cancel==0 => draw that card; cancel==1 => shuffle back.
        static void HandleScryResult(NetworkStream ns, byte[] payload)
        {
            BattleSlot mine, theirs = null;
            lock (_battleLock)
            {
                if (!_battles.TryGetValue(ns, out mine)) return;
                if (mine.Opp != null) _battles.TryGetValue(mine.Opp.Ns, out theirs);
            }
            Battle b = mine.Battle;
            if (b == null) return;
            PlayerState ps = b.P[mine.P];
            var pend = ps.PendingScry;
            ps.PendingScry = null;
            if (pend == null || pend.Count == 0) { Log("SCRY result: no pending scry (ignored)"); return; }

            int n = payload.Length > 0 ? payload[0] : pend.Count;
            ushort drawn = 0; bool haveDraw = false;
            var back = new List<ushort>();
            for (int k = 0; k < n && k < pend.Count; k++)
            {
                bool cancel = (1 + k < payload.Length) ? (payload[1 + k] != 0) : true; // 0 = draw, 1 = shuffle back
                if (!cancel && !haveDraw) { drawn = pend[k]; haveDraw = true; }
                else back.Add(pend[k]);
            }
            for (int k = n; k < pend.Count; k++) back.Add(pend[k]); // any extras go back

            // Shuffle the un-chosen cards back into the deck.
            ps.Deck.AddRange(back);
            lock (_rng) for (int i = ps.Deck.Count - 1; i > 0; i--) { int j = _rng.Next(i + 1); var t = ps.Deck[i]; ps.Deck[i] = ps.Deck[j]; ps.Deck[j] = t; }

            if (haveDraw)
            {
                mine.Hand.Insert(0, drawn); // client draws to the front (scry=false op8 -> obj_hand)
                byte deckLeft = (byte)Math.Min(255, ps.Deck.Count);
                byte handSize = (byte)Math.Min(255, mine.Hand.Count);
                Send(mine.Me.Ns, new PacketWriter()
                    .WriteBool(false).WriteU8(deckLeft).WriteU16(drawn).WriteBool(false)
                    .WriteU8(0).WriteBool(false).WriteU8(0).WriteU8(0).Frame(Op.DrawCard));
                if (theirs != null)
                    Send(theirs.Me.Ns, new PacketWriter()
                        .WriteBool(false).WriteU8(deckLeft).WriteU8(handSize).WriteU8(0)
                        .WriteBool(false).WriteU8(0).WriteU8(0).Frame(Op.DrawCardGet));
                Log("SCRY result: " + mine.Me.User + " drew card " + drawn + ", shuffled " + back.Count + " back");
            }
            else Log("SCRY result: " + mine.Me.User + " drew nothing, shuffled " + back.Count + " back");
        }

        // ---- order / targeted spell (op 28) -----------------------------------
        // Client sends: u8 card, bool grid, u8 x, u8 y, u16 cardId (see battle_order_attack).
        // cardId == 0 is a cancel (player clicked empty). Effects are server-authoritative: we
        // mutate the data model, then SyncUnitStates pushes the new life/state to both clients
        // (no dedicated cast animation yet — the life bars just update). Orders/spells we haven't
        // implemented fall through to a graceful no-op (action refunded) so nothing freezes.
        enum OrderKind { None, DamageSingle, DamageRow, DamageColumn, DamageBlast, DamageAll, HealSingle, HealAll, HealLeader, DrawCards, KillHero, GainActions, Inspire, SummonCopy, SummonRandom, RaiseDead, Revive, Resurrect,
                         DamageRandom, DamageFrontEach, DamageLeader, WildSummon,
                         FinisherKill, Backlash, DamageIce, Banish, OrbBoost, OrbDrain, Retreat, Unsummon,
                         ShieldHero, ShieldLeader, ShieldVanguard, StrengthBuff, StrengthBuffVanguard, Silence,
                         Decoy, Swap, Takedown, Seance, Replicate, Infusion, Polymorph, Copycat,
                         TrapCancelAttack, TrapCancelSpell, TrapCancelOrder,
                         Immortality, Restructure, DamageSpread,
                         Reload, MindControl, Duplicate, Transfusion, Enervate, Entomb, ForceCube,
                         ResurrectAll, Phantom, Scry, LucHaste, GrantRanged, ShieldRear }

        // Kind2/Amount2 = an optional RIDER: a second effect the same order/spell fires on the same target
        // right after the primary (e.g. Warrior "Cure 4, Strength Buff 2"). The rider never costs its own action.
        struct OrderEffect { public OrderKind Kind; public int Amount; public bool Free; public OrderKind Kind2; public int Amount2; } // Free = no action cost

        // Effects for the tester Arena deck (card id = REAL*2). Damage orders hit the enemy grid,
        // heals hit the caster's grid. Unimplemented cards return None (graceful no-op).
        static OrderEffect OrderOf(ushort cardId)
        {
            switch (cardId / 2)
            {
                case 26: return new OrderEffect { Kind = OrderKind.DamageColumn, Amount = 5 }; // Fighter: Thunder 5 (column)
                case 28: return new OrderEffect { Kind = OrderKind.DamageRandom, Amount = 8 }; // Berserker: Bombard 8 (random hero)
                case 30: return new OrderEffect { Kind = OrderKind.DamageAll,    Amount = 3 }; // Alchemist: Poison 3 (official: X to ALL rival heroes)
                case 31: return new OrderEffect { Kind = OrderKind.DamageRow,    Amount = 5 }; // Gunner: Bombard Row 5 (a row)
                case 32: return new OrderEffect { Kind = OrderKind.HealAll,      Amount = 4 }; // Healer: Cure All 4
                case 29: return new OrderEffect { Kind = OrderKind.KillHero };                  // Assassin: Assassinate
                case 34: return new OrderEffect { Kind = OrderKind.WildSummon }; // Illusionist: Wild Summon (play a random card from the rival's hand as an order, then discard it)
                case 35: return new OrderEffect { Kind = OrderKind.DamageFrontEach, Amount = 4 }; // Knight: Blast 4 (front of each row)
                case 36: return new OrderEffect { Kind = OrderKind.Inspire, Kind2 = OrderKind.DrawCards, Amount2 = 2 }; // Mascot: Inspire + Draw 2 (Inspire targets a friendly hero, like Legionnaire)
                case 39: return new OrderEffect { Kind = OrderKind.SummonRandom, Amount = 3 }; // Overlord: Summon x3
                case 42: return new OrderEffect { Kind = OrderKind.Resurrect };                // Priestess: Resurrect
                case 44: return new OrderEffect { Kind = OrderKind.GainActions,  Amount = 2, Free = true }; // Scientist: Haste 2 (free)
                case 50: return new OrderEffect { Kind = OrderKind.RaiseDead,    Amount = 3 }; // Witch: Raise Dead x3
                case 41: return new OrderEffect { Kind = OrderKind.DamageColumn, Amount = 5 }; // Planestalker: Bombard Column 5
                case 43: return new OrderEffect { Kind = OrderKind.DamageRow,    Amount = 5 }; // Pyromancer: Fire 5
                case 52: return new OrderEffect { Kind = OrderKind.DamageRow,    Amount = 4 }; // Fire Elemental: Fire 4
                case 53: return new OrderEffect { Kind = OrderKind.HealSingle,   Amount = 5 }; // Water Elemental: Cure 5
                case 56: return new OrderEffect { Kind = OrderKind.DamageColumn, Amount = 4 }; // Lightning Elemental: Thunder 4 (column)
                case 48: return new OrderEffect { Kind = OrderKind.Banish,       Amount = 2 }; // Trapper: Banish 2 (rival discards 2 random)
                case 82: return new OrderEffect { Kind = OrderKind.DamageIce,    Amount = 4 }; // Ranger: Ice 4 (target + adjacent)
                case 86: return new OrderEffect { Kind = OrderKind.FinisherKill };             // Lancer: Finisher Kill (defeat a damaged hero)
                case 105:return new OrderEffect { Kind = OrderKind.DamageSingle, Amount = 6 }; // Scholar: Meteor 6 (single hero)
                case 55: return new OrderEffect { Kind = OrderKind.Unsummon };                 // Dark Elemental: Unsummon
                case 113:return new OrderEffect { Kind = OrderKind.Unsummon };                 // Shadow Elemental: Unsummon
                case 27: return new OrderEffect { Kind = OrderKind.StrengthBuff, Amount = 5 };  // Dragon Mage: Give Strength Buff 5
                case 84: return new OrderEffect { Kind = OrderKind.StrengthBuff, Amount = 3 };  // Fencer: Strength 3 (buff a hero)
                case 94: return new OrderEffect { Kind = OrderKind.ShieldVanguard };            // Magus: Vanguard: Shield
                case 96: return new OrderEffect { Kind = OrderKind.ShieldLeader, Kind2 = OrderKind.HealLeader, Amount2 = 2 }; // Squire: Shield Leader + Cure Leader 2
                case 110:return new OrderEffect { Kind = OrderKind.Decoy };                     // Occultist: Decoy (mark a friendly hero)
                case 69: return new OrderEffect { Kind = OrderKind.DamageSpread, Amount = 10 };  // Warmage: Meteor Storm 10 (X divided randomly)
                case 65: return new OrderEffect { Kind = OrderKind.Replicate, Free = true };    // Puppeteer: Quick Order: Replicate
                case 60: return new OrderEffect { Kind = OrderKind.HealLeader, Amount = 4 };    // Chronicler: Infuse Leader 4
                case 88: return new OrderEffect { Kind = OrderKind.Polymorph };                 // Biomancer: Polymorph (an enemy hero)
                case 38: return new OrderEffect { Kind = OrderKind.Reload };                    // Oracle: Reload (shuffle hand into deck, draw that many + 1)
                case 104:return new OrderEffect { Kind = OrderKind.Reload };                    // Magic Student: Reload
                case 64: return new OrderEffect { Kind = OrderKind.Duplicate, Free = true };    // Doppelganger: Quick Order: Duplicate (copy a card in hand)
                case 62: return new OrderEffect { Kind = OrderKind.Entomb, Amount = 2 };        // Diabloist: Entomb 2 (rival discards, placed as corpses in enemy spaces)
                case 63: return new OrderEffect { Kind = OrderKind.ResurrectAll };             // Divinity: Resurrect All (revive every corpse on BOTH boards)
                case 85: return new OrderEffect { Kind = OrderKind.Phantom, Amount = 2 };       // Wizard: Phantom x2 (2 discard copies -> hand)
                case 46: return new OrderEffect { Kind = OrderKind.Scry, Amount = 6, Free = true }; // Summoner: Quick Order: Scry 6
                case 54: return new OrderEffect { Kind = OrderKind.Scry, Amount = 4 };          // Air Elemental: Scry 4
                case 66: return new OrderEffect { Kind = OrderKind.Scry, Amount = 4, Free = true }; // Relic Hunter: Quick Order: Scry 4
                case 61: return new OrderEffect { Kind = OrderKind.Immortality };               // Curse Knight: Ongoing: Vanguard: Immortality (your vanguard heroes can't die for 3 waves)
                case 33: return new OrderEffect { Kind = OrderKind.Restructure };               // Homunculus: Restructure (move + clear-corpse are free until end of turn)
                case 95: return new OrderEffect { Kind = OrderKind.Restructure };               // Bannerman: Restructure
                case 87: return new OrderEffect { Kind = OrderKind.TrapCancelAttack };          // Reflector: Trap: Cancel Attack, Backlash
                case 106:return new OrderEffect { Kind = OrderKind.TrapCancelSpell };            // Statistician: Trap: Cancel Spell, Draw 1
                case 112:return new OrderEffect { Kind = OrderKind.TrapCancelOrder };            // Mastermind: Trap: Cancel Order
                case 37: return new OrderEffect { Kind = OrderKind.DrawCards,   Amount = 3 };    // Mystic: Draw 3
                case 40: return new OrderEffect { Kind = OrderKind.Revive, Free = true };        // Paladin: Quick Order: Revive
                case 59: return new OrderEffect { Kind = OrderKind.Resurrect };                  // Ghost (59): Resurrect
                case 80: return new OrderEffect { Kind = OrderKind.HealSingle,  Amount = 4, Kind2 = OrderKind.StrengthBuff, Amount2 = 2 }; // Warrior: Cure 4 + Strength Buff 2 (same hero)
                case 81: return new OrderEffect { Kind = OrderKind.StrengthBuff, Amount = 2, Kind2 = OrderKind.Inspire };                // Twinblade: Strength Buff 2 + Inspire (same hero)
                case 89: return new OrderEffect { Kind = OrderKind.Inspire };                    // Legionnaire: Inspire
                case 90: return new OrderEffect { Kind = OrderKind.HealLeader,  Amount = 5 };    // Nurse: Cure Leader 5
                case 91: return new OrderEffect { Kind = OrderKind.GainActions, Amount = 1, Free = true }; // Commando: Quick Order: Haste 1
                case 92: return new OrderEffect { Kind = OrderKind.DrawCards,   Amount = 2 };    // Treasure Hunter: Draw 2
                case 93: return new OrderEffect { Kind = OrderKind.DamageRandom, Amount = 6 };   // Pistolier: Bombard 6
                case 97: return new OrderEffect { Kind = OrderKind.RaiseDead,   Amount = 3 };    // Dark Knight: Raise Dead x3
                case 98: return new OrderEffect { Kind = OrderKind.Resurrect };                  // White Knight: Resurrect
                case 114:return new OrderEffect { Kind = OrderKind.Resurrect };                  // Ghost (114): Resurrect
                case 115:return new OrderEffect { Kind = OrderKind.DamageAll,   Amount = 1 };    // Plague Doctor: Poison 1 (all)
                default: return new OrderEffect { Kind = OrderKind.None };
            }
        }

        // Effects that resolve against the ENEMY board (so the order must be aimed at the enemy). Random/
        // leader/front-each targeters pick their own cells, so they don't require an enemy click.
        static bool IsEnemyTargeting(OrderKind k)
        {
            return k == OrderKind.DamageSingle || k == OrderKind.DamageRow || k == OrderKind.DamageColumn
                || k == OrderKind.DamageBlast || k == OrderKind.KillHero
                || k == OrderKind.FinisherKill || k == OrderKind.Backlash || k == OrderKind.DamageIce
                || k == OrderKind.Unsummon || k == OrderKind.Silence || k == OrderKind.Takedown
                || k == OrderKind.MindControl || k == OrderKind.Enervate || k == OrderKind.Entomb;
        }

        // Immediate death: any unit on `ps` at/over lethal damage becomes a CORPSE right now (or is
        // cleared outright if Ephemeral), so the server and client agree instantly and the corpse can be
        // cleared/revived without waiting for wave end. The client already renders it dead from the
        // damage update, so a plain corpse (dead=true, life=0) is idempotent there.
        static void ProcessImmediateDeaths(PlayerState ps, BattleSlot owner, BattleSlot opp, Battle b)
        {
            List<int> dead = null;
            foreach (var kv in ps.Units)
                if (kv.Value != null && !kv.Value.IsCorpse && kv.Value.Damage >= kv.Value.Max)
                { (dead ?? (dead = new List<int>())).Add(kv.Key); }
            if (dead == null) return;
            foreach (int key in dead)
            {
                BUnit u = ps.Units[key];
                int ux = key / 10, uy = key % 10;
                // Immortality: a vanguard hero can't die — cap its damage just below lethal and re-sync.
                if (IsImmortalVanguard(ps, ux))
                {
                    u.Damage = Math.Max(0, u.Max - 1);
                    SendUnitUpdateToBoth(owner, opp, ux, uy, u, b);
                    Log("  IMMORTAL: (" + ux + "," + uy + ") card " + u.Card + " survives lethal (vanguard immortality)");
                    continue;
                }
                if ((u.Abilities & UnitAbility.Ephemeral) != 0)
                {
                    DiscardCard(owner, opp, u.Card);
                    ps.Units.Remove(key);
                    SendClearCorpse(owner, opp, ux, uy);
                    Log("  DIED (ephemeral): (" + ux + "," + uy + ") card " + u.Card + " -> corpse cleared");
                }
                else
                {
                    u.IsCorpse = true; u.Damage = 0; u.CorpseFresh = true;
                    // Send the death transition exactly once. If an attack already sent
                    // dead=true for this unit, DeathSent is set and re-sending would
                    // restart the client's death animation ("keeps refreshing").
                    if (!u.DeathSent) SendUnitUpdateToBoth(owner, opp, ux, uy, u, b);
                    Log("  DIED: (" + ux + "," + uy + ") card " + u.Card + " -> corpse");
                }
                OnHeroDefeated(owner, opp, b, u); // Iri / Shekhtur / Rexan
            }
        }

        // ---- order/spell visuals ---------------------------------------------
        // The cast-pose "spell window": op44 makes the caster at (cx,cy) do a cast animation + shows the
        // spell name + zooms the camera, and sets next_que_effect=0 (BLOCKS the client action queue).
        // op45 ends the window (next_que_effect=1) and un-blocks. They MUST be sent as a pair around the
        // effect, or the client's queue stalls (frozen, can't even end turn). anim_spell_create looks up
        // the caster on the sender's own board (isMe=1) or the opponent's (isMe=0, Y mirrored client-side).
        static void SendSpellCast(BattleSlot mine, BattleSlot theirs, int cx, int cy)
        {
            Send(mine.Me.Ns, new PacketWriter().WriteBool(true).WriteU8((byte)cx).WriteU8((byte)cy).Frame(Op.SpellCreate));
            if (theirs != null)
                Send(theirs.Me.Ns, new PacketWriter().WriteBool(false).WriteU8((byte)cx).WriteU8((byte)cy).Frame(Op.SpellCreate));
        }

        static void SendSpellEnd(BattleSlot mine, BattleSlot theirs)
        {
            Send(mine.Me.Ns, new PacketWriter().Frame(Op.SpellRemove));
            if (theirs != null) Send(theirs.Me.Ns, new PacketWriter().Frame(Op.SpellRemove));
        }

        // Fire an effect (op41 container_effect) from `mine`'s cell (fx,fy) to a cell (tx,ty) on the
        // target board (targetIsMine=true => the effect lands on mine's own board, e.g. a heal). Sent to
        // both clients with the grid flags flipped per viewer; anim_effect mirrors Y for the opponent
        // board. isOrder=false so the client uses a real source unit (the leader) — a fromto=1 projectile
        // eid then travels from it and its arrival releases the queue. Only call with a queue-safe eid.
        static void SendEffect(BattleSlot mine, BattleSlot theirs, int fx, int fy, int tx, int ty, bool targetIsMine, int eid, bool isheal, int dmg)
        {
            byte d = (byte)Math.Max(0, Math.Min(255, dmg));
            // mine's view: source is on mine's own board (gridf=true); target on own board iff targetIsMine.
            Send(mine.Me.Ns, new PacketWriter()
                .WriteBool(false).WriteU8((byte)fx).WriteU8((byte)fy).WriteBool(true)
                .WriteU8((byte)tx).WriteU8((byte)ty).WriteBool(targetIsMine)
                .WriteU16((ushort)eid).WriteBool(isheal).WriteU8(d).Frame(Op.Effect));
            // theirs' view: mine's board is the "other player" (gridf=false); target board flips too.
            if (theirs != null)
                Send(theirs.Me.Ns, new PacketWriter()
                    .WriteBool(false).WriteU8((byte)fx).WriteU8((byte)fy).WriteBool(false)
                    .WriteU8((byte)tx).WriteU8((byte)ty).WriteBool(!targetIsMine)
                    .WriteU16((ushort)eid).WriteBool(isheal).WriteU8(d).Frame(Op.Effect));
        }

        // Effect id per effect kind. AUTHORITATIVE map from the data.win object dump (2026-08-12), NOT the
        // earlier visual guesses. eid -> object: 1 obj_quick_fire(fire), 2 obj_single_poison, 3 obj_first_ice
        // / 4 obj_second_ice (both RELEASE the queue; Ice handled specially in FireEffect), 5 obj_first_thunder,
        // 7 obj_single_heal, 8 obj_many_heal, 9 obj_single_ress, 10 obj_many_ress, 11 obj_single_revive,
        // 12 obj_single_haste, 13 obj_silence_intro, 14 obj_single_execute(defeat), 15 obj_single_backstab,
        // 17 obj_single_summon, 18 obj_str_up, 19 obj_mass_str_up, 22 obj_single_polymorph, 25 obj_wild_summon.
        // Shield/Silence/Decoy are NOT op41 effects -> they're persistent per-hero flags in the buff packet
        // (BuildBuff/anim_update_buff): the client draws spr_eff_shield/spr_eff_silenced/spr_decoy while the
        // flag is set and auto-spawns obj_shield_break (spr_eff_shield_break) when shield flips 1->0. So they
        // stay -1 here (SyncUnitStates carries them). Enervate/etc genuinely have no visual.
        static int EfxOf(OrderKind k)
        {
            switch (k)
            {
                case OrderKind.DamageAll:            return 2;   // poison
                case OrderKind.DamageColumn:         return 5;   // thunder (lightning)
                case OrderKind.DamageLeader:         return 15;  // backstab
                case OrderKind.KillHero:
                case OrderKind.FinisherKill:
                case OrderKind.Takedown:             return 14;  // execute (defeat)
                case OrderKind.Silence:              return 13;  // silence
                case OrderKind.GainActions:
                case OrderKind.LucHaste:             return 12;  // haste (spr_single_haste)
                case OrderKind.HealSingle:
                case OrderKind.HealLeader:
                case OrderKind.Infusion:             return 7;   // heal (single)
                case OrderKind.HealAll:              return 8;   // heal (many)
                case OrderKind.Revive:               return 11;  // revive
                case OrderKind.Resurrect:            return 9;   // resurrect (single)
                case OrderKind.ResurrectAll:         return 10;  // resurrect (many)
                case OrderKind.StrengthBuff:         return 18;  // strength up (single)
                case OrderKind.StrengthBuffVanguard: return 19;  // strength up (mass)
                case OrderKind.GrantRanged:          return 20;  // obj_give_ranged (Lizaveta)
                case OrderKind.DamageSingle:
                case OrderKind.DamageRow:            // Fire
                case OrderKind.DamageBlast:
                case OrderKind.DamageRandom:
                case OrderKind.DamageFrontEach:
                case OrderKind.DamageSpread:
                case OrderKind.Backlash:             return 1;   // fire/explosion (no generic damage object)
                default:                             return -1;  // no matching client effect object
            }
        }

        // Fire the themed op41 effect(s) for a resolved order/spell along the real caster->target path.
        // AoE fires one effect per affected cell (same-eid effects chain via can_unque_same_effect, so they
        // don't freeze). Own-target heals/buffs land on the caster's board (targetIsMine=true).
        static void FireEffect(OrderEffect eff, BattleSlot mine, BattleSlot theirs, Battle b, int casterX, int casterY, int gx, int enemyGy, int selfGy)
        {
            if (theirs == null) return;
            PlayerState ps = b.P[mine.P], opPs = b.P[1 - mine.P];
            int a = eff.Amount;

            // Transfusion is a two-part visual: heal glow on the ally, fire on the caster.
            if (eff.Kind == OrderKind.Transfusion)
            {
                SendEffect(mine, theirs, casterX, casterY, gx, selfGy, true, 7, true, a);
                SendEffect(mine, theirs, casterX, casterY, casterX, casterY, true, 1, false, a);
                return;
            }

            // Ice N (target + 4 orthogonal neighbours): obj_first_ice (eid 3) on the primary target,
            // obj_second_ice (eid 4) on each adjacent enemy hero. Both release the queue (first_ice calls
            // can_unque_damage unconditionally; the second_ice group chains via can_unque_same_effect).
            if (eff.Kind == OrderKind.DamageIce)
            {
                SendEffect(mine, theirs, casterX, casterY, gx, enemyGy, false, 3, false, a);
                int[] ax = { gx - 1, gx + 1, gx, gx }, ay = { enemyGy, enemyGy, enemyGy - 1, enemyGy + 1 };
                for (int i = 0; i < 4; i++)
                {
                    if (ax[i] < 0 || ax[i] > 2 || ay[i] < 0 || ay[i] > 2) continue;
                    BUnit nu; if (opPs.Units.TryGetValue(Key(ax[i], ay[i]), out nu) && nu != null && !nu.IsCorpse)
                        SendEffect(mine, theirs, casterX, casterY, ax[i], ay[i], false, 4, false, a);
                }
                return;
            }

            int eid = EfxOf(eff.Kind);
            if (eid < 0) return;

            var cells = new List<int>();
            bool targetIsMine = false, telegraph = false;
            switch (eff.Kind)
            {
                case OrderKind.DamageSingle: case OrderKind.Backlash:
                case OrderKind.KillHero: case OrderKind.FinisherKill: case OrderKind.Takedown:
                case OrderKind.Silence: case OrderKind.DamageRandom:
                    cells.Add(Key(gx, enemyGy)); break;
                case OrderKind.DamageLeader:
                    cells.Add(Key(1, 1)); break;
                case OrderKind.DamageRow:
                    for (int cx = 0; cx <= 2; cx++) cells.Add(Key(cx, enemyGy)); telegraph = true; break;
                case OrderKind.DamageColumn:
                    for (int cy = 0; cy <= 2; cy++) cells.Add(Key(gx, cy)); telegraph = true; break;
                case OrderKind.DamageAll: case OrderKind.DamageSpread:
                    foreach (var kv in opPs.Units) if (kv.Value != null && !kv.Value.IsCorpse) cells.Add(kv.Key); telegraph = true; break;
                case OrderKind.DamageBlast: case OrderKind.DamageFrontEach:
                    for (int lane = 0; lane < 3; lane++)
                        for (int w = 2; w >= 0; w--) { BUnit fu; if (opPs.Units.TryGetValue(Key(w, lane), out fu) && fu != null && !fu.IsCorpse) { cells.Add(Key(w, lane)); break; } }
                    telegraph = true; break;
                // own single cell (heal / strength buff / revive / resurrect / grant ranged)
                case OrderKind.HealSingle: case OrderKind.StrengthBuff:
                case OrderKind.Revive: case OrderKind.Resurrect: case OrderKind.GrantRanged:
                    cells.Add(Key(gx, selfGy)); targetIsMine = true; break;
                case OrderKind.HealLeader: case OrderKind.Infusion: case OrderKind.GainActions:
                case OrderKind.LucHaste:
                    cells.Add(Key(1, 1)); targetIsMine = true; break;
                // own all units
                case OrderKind.HealAll: case OrderKind.ResurrectAll:
                    foreach (var kv in ps.Units) if (kv.Value != null) cells.Add(kv.Key); targetIsMine = true; break;
                // own vanguard
                case OrderKind.StrengthBuffVanguard:
                    for (int lane = 0; lane < 3; lane++) { BUnit vu; if (ps.Units.TryGetValue(Key(2, lane), out vu) && vu != null && !vu.IsCorpse) cells.Add(Key(2, lane)); }
                    targetIsMine = true; break;
            }
            if (cells.Count == 0) return;
            bool isheal = (eid == 7 || eid == 8); // heal-number style only for actual heals
            // Telegraph: obj_single_arrow_target (eid 24, at-target reticle) marks every unit that will be hit
            // by an AoE damage effect. Queue-safe: consecutive 24s chain via can_unque_same_effect and the last
            // releases into the damage splash. Enemy board (targetIsMine=false), no damage number.
            if (telegraph)
                foreach (int c in cells) SendEffect(mine, theirs, casterX, casterY, c / 10, c % 10, false, 24, false, 0);
            foreach (int c in cells) SendEffect(mine, theirs, casterX, casterY, c / 10, c % 10, targetIsMine, eid, isheal, a);
        }

        // Remove a unit from `ownerSlot`'s board on both clients (op32 own view, op33 opponent view,
        // Y mirrored client-side). Used by Retreat (own unit) / Unsummon (enemy unit) before returning
        // the card to its owner's hand.
        static void SendDestroyUnit(BattleSlot ownerSlot, BattleSlot oppSlot, int gx, int gy)
        {
            Send(ownerSlot.Me.Ns, new PacketWriter().WriteU8((byte)gx).WriteU8((byte)gy).Frame(Op.DestroyUnit));
            if (oppSlot != null) Send(oppSlot.Me.Ns, new PacketWriter().WriteU8((byte)gx).WriteU8((byte)gy).Frame(Op.DestroyUnitGet));
        }

        // Put a SPECIFIC card into ownerSlot's hand (front) and notify both clients (op8 with the card to
        // the owner, op9 count-only to the opponent) — the same shape DrawCardsFor uses, but the card is
        // given (a returned unit) rather than drawn from the deck.
        static void AddCardToHand(BattleSlot ownerSlot, BattleSlot oppSlot, Battle b, int ownerP, ushort card)
        {
            ownerSlot.Hand.Insert(0, card);
            byte deckLeft = (byte)Math.Min(255, b.P[ownerP].Deck == null ? 0 : b.P[ownerP].Deck.Count);
            Send(ownerSlot.Me.Ns, new PacketWriter()
                .WriteBool(false).WriteU8(deckLeft).WriteU16(card).WriteBool(false)
                .WriteU8(0).WriteBool(false).WriteU8(0).WriteU8(0).Frame(Op.DrawCard));
            if (oppSlot != null)
                Send(oppSlot.Me.Ns, new PacketWriter()
                    .WriteBool(false).WriteU8(deckLeft).WriteU8((byte)Math.Min(255, ownerSlot.Hand.Count)).WriteU8(0)
                    .WriteBool(false).WriteU8(0).WriteU8(0).Frame(Op.DrawCardGet));
        }

        // Play the summon animation for `card` at (gx,gy) on `mine`'s board (visual only; no model change).
        // Used by Swap to re-place the two swapped units after destroying them client-side.
        static void SendSummonVisual(BattleSlot mine, BattleSlot theirs, ushort card, int gx, int gy)
        {
            Send(mine.Me.Ns, new PacketWriter().WriteU16(card).WriteU8((byte)gx).WriteU8((byte)gy).WriteBool(false).Frame(Op.SummonUnit));
            if (theirs != null) // container_summon_unit_get mirrors Y client-side
                Send(theirs.Me.Ns, new PacketWriter().WriteU16(card).WriteU8((byte)gx).WriteU8((byte)gy).WriteBool(false).Frame(Op.SummonUnitGet));
        }

        // Tell both clients to clear the corpse at (cx,cy) on `mine`'s board (visual removal).
        static void SendClearCorpse(BattleSlot mine, BattleSlot theirs, int cx, int cy)
        {
            Send(mine.Me.Ns, new PacketWriter().WriteU8((byte)cx).WriteU8((byte)cy).WriteBool(false).Frame(Op.ClearCorpse));
            if (theirs != null) Send(theirs.Me.Ns, new PacketWriter().WriteU8((byte)cx).WriteU8((byte)cy).WriteBool(false).Frame(Op.ClearCorpseGet));
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
            PlayerState opPs = b.P[1 - mine.P];
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
            if ((eff.Kind == OrderKind.HealSingle || eff.Kind == OrderKind.Revive || eff.Kind == OrderKind.Resurrect || eff.Kind == OrderKind.Retreat || eff.Kind == OrderKind.ShieldHero || eff.Kind == OrderKind.StrengthBuff || eff.Kind == OrderKind.Decoy || eff.Kind == OrderKind.Seance || eff.Kind == OrderKind.Transfusion || eff.Kind == OrderKind.ForceCube) && !grid)
            {
                Log("  -> ORDER rejected: heal/revive must target your OWN side (enemy board clicked)");
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
                DiscardCard(mine, theirs, cardId); // played orders go to the discard pile
                Send(mine.Me.Ns, new PacketWriter().WriteU8(card).WriteU16(cardId).WriteBool(true).Frame(Op.HandCardRemove));
                if (theirs != null)
                    Send(theirs.Me.Ns, new PacketWriter().WriteU8((byte)Math.Min(255, mine.Hand.Count)).WriteU8(card).WriteU16(cardId).Frame(Op.HandCardRemoveGet));
                Log("  -> discarded order card " + cardId + " from hand[" + card + "]");
            }

            // Visual: the leader (1,1) performs a cast pose + camera zoom while the effect resolves. The
            // op44/op45 pair brackets ResolveEffect (whose life-bar updates queue between them, so they
            // play right after the cast). Sent from here (not ResolveEffect) so Wild Summon's recursive
            // inner resolve doesn't emit a second cast pose.
            // TRAP — Cancel Order: the rival's armed trap (round >= 2) negates this order. The card is
            // already discarded and the orb already paid; the caster still spends the action. A trap-
            // arming order is itself an order and can legitimately be cancelled by an opposing trap.
            if (b.Round >= 2 && opPs.TrapCancelOrder)
            {
                opPs.TrapCancelOrder = false;
                ClearTrapVisual(theirs, mine, opPs.TrapOrderLane); opPs.TrapOrderLane = -1;
                Log("  -> TRAP: " + (theirs != null ? theirs.Me.User : "defender") + "'s Cancel Order negates " + mine.Me.User + "'s order");
                ConsumeAction(mine, theirs, b);
                BattleSlot t0 = mine.P == 0 ? mine : theirs, t1 = mine.P == 0 ? theirs : mine;
                if (t0 != null && t1 != null) SyncUnitStates(t0, t1, b);
                return;
            }

            SendSpellCast(mine, theirs, 1, 1);
            ResolveEffect(eff, mine, theirs, b, x, y, 1, 1, grid); // grid(isgrid): 1=own board clicked (for either-board effects)
            SendSpellEnd(mine, theirs);
            // Leader on-order passives (Rukyuk Bombard 3 / Tatsumi first-order draw+action).
            if (!b.Over) ApplyLeaderOnOrder(mine, theirs, b);
        }

        // Apply an order/spell effect and push results to both clients. (x,y) are the raw client
        // target coords: damage effects hit the enemy grid (gy mirrored 2-y), heals hit the caster's
        // grid (gy = y). (casterX,casterY) = the casting unit's cell (leader 1,1 for orders) — needed by
        // effects that act on the caster itself (Swap, Takedown). Shared by HandleOrder (op28) and the
        // isSpell path of HandleAttack (op22).
        static void ResolveEffect(OrderEffect eff, BattleSlot mine, BattleSlot theirs, Battle b, byte x, byte y, int casterX = 1, int casterY = 1, bool targetSelf = true)
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
                case OrderKind.DamageBlast:    // (legacy) target + its column-neighbours
                    DamageEnemyAt(opPs, gx, enemyGy, eff.Amount);
                    DamageEnemyAt(opPs, gx, enemyGy - 1, eff.Amount);
                    DamageEnemyAt(opPs, gx, enemyGy + 1, eff.Amount);
                    break;
                case OrderKind.DamageFrontEach: // Blast: the frontmost enemy hero/leader in EACH lane
                    for (int lane = 0; lane < 3; lane++)
                    {
                        int fx = -1;
                        for (int w = 2; w >= 0; w--) { BUnit fu; if (opPs.Units.TryGetValue(Key(w, lane), out fu) && fu != null && !fu.IsCorpse) { fx = w; break; } }
                        if (fx >= 0) DamageEnemyAt(opPs, fx, lane, eff.Amount);
                        else if (lane == 1) DamageEnemyAt(opPs, 1, 1, eff.Amount); // clear center -> the leader
                    }
                    break;
                case OrderKind.DamageRandom: // Bombard: a random rival hero
                    {
                        var alive = new List<int>();
                        foreach (var kv in opPs.Units) if (kv.Value != null && !kv.Value.IsCorpse) alive.Add(kv.Key);
                        if (alive.Count > 0) { int k; lock (_rng) k = alive[_rng.Next(alive.Count)]; DamageEnemyAt(opPs, k / 10, k % 10, eff.Amount); }
                        else Log("  bombard: no enemy heroes");
                    }
                    break;
                case OrderKind.DamageLeader: // Backstab: the rival leader
                    DamageEnemyAt(opPs, 1, 1, eff.Amount);
                    break;
                case OrderKind.FinisherKill: // Finisher Kill: defeat a hero ONLY if it is already damaged
                    {
                        BUnit u;
                        if (opPs.Units.TryGetValue(Key(gx, enemyGy), out u) && u != null && !u.IsCorpse && u.Damage > 0)
                            KillEnemyAt(opPs, gx, enemyGy);
                        else Log("  finisher kill: target at (" + gx + "," + enemyGy + ") is not a damaged hero");
                    }
                    break;
                case OrderKind.Backlash: // deal a hero damage equal to its OWN attack
                    {
                        BUnit u;
                        if (gx == 1 && enemyGy == 1)
                            DamageEnemyAt(opPs, 1, 1, AtkOf(opPs.LeaderCard) + opPs.LeaderStrBonus);
                        else if (opPs.Units.TryGetValue(Key(gx, enemyGy), out u) && u != null && !u.IsCorpse)
                            DamageEnemyAt(opPs, gx, enemyGy, u.Atk); // u.Atk folds in base + strength
                        else Log("  backlash: no enemy hero at (" + gx + "," + enemyGy + ")");
                    }
                    break;
                case OrderKind.DamageIce: // Ice N: N damage to the target hero AND its 4 orthogonal neighbours
                    DamageEnemyAt(opPs, gx, enemyGy, eff.Amount);
                    DamageEnemyAt(opPs, gx - 1, enemyGy, eff.Amount);
                    DamageEnemyAt(opPs, gx + 1, enemyGy, eff.Amount);
                    DamageEnemyAt(opPs, gx, enemyGy - 1, eff.Amount);
                    DamageEnemyAt(opPs, gx, enemyGy + 1, eff.Amount);
                    break;
                case OrderKind.Banish: // opponent discards N random cards from hand (removed from the game)
                    if (theirs == null || theirs.Hand == null || theirs.Hand.Count == 0) Log("  banish: rival hand empty");
                    else
                        for (int i = 0; i < eff.Amount && theirs.Hand.Count > 0; i++)
                        {
                            int bidx; lock (_rng) bidx = _rng.Next(theirs.Hand.Count);
                            ushort bc = theirs.Hand[bidx]; theirs.Hand.RemoveAt(bidx);
                            DiscardCard(theirs, mine, bc); // banished cards go to the rival's discard
                            Send(theirs.Me.Ns, new PacketWriter().WriteU8((byte)bidx).WriteU16(bc).WriteBool(true).Frame(Op.HandCardRemove));
                            Send(mine.Me.Ns, new PacketWriter().WriteU8((byte)Math.Min(255, theirs.Hand.Count)).WriteU8((byte)bidx).WriteU16(bc).Frame(Op.HandCardRemoveGet));
                            Log("  BANISH: removed card " + bc + " from " + theirs.Me.User + " hand[" + bidx + "]");
                        }
                    break;
                case OrderKind.OrbBoost: // +N of your own orbs (capped)
                    ps.Orbs = Math.Min(ps.OrbCap, ps.Orbs + Math.Max(1, eff.Amount));
                    { BattleSlot z0 = mine.P == 0 ? mine : theirs, z1 = mine.P == 0 ? theirs : mine; if (z0 != null && z1 != null) SendOrbs(z0, z1, b); }
                    Log("  ORB BOOST: " + mine.Me.User + " orbs now " + ps.Orbs);
                    break;
                case OrderKind.Retreat: // return one of YOUR heroes to your hand
                    {
                        int rk = Key(gx, selfGy);
                        BUnit u;
                        if (gx == 1 && selfGy == 1) { Log("  retreat: cannot return the leader"); break; }
                        if (ps.Units.TryGetValue(rk, out u) && u != null && !u.IsCorpse)
                        {
                            ps.Units.Remove(rk);
                            SendDestroyUnit(mine, theirs, gx, selfGy);
                            AddCardToHand(mine, theirs, b, mine.P, u.Card);
                            Log("  RETREAT: " + mine.Me.User + " returned (" + gx + "," + selfGy + ") card " + u.Card + " to hand");
                        }
                        else Log("  retreat: no friendly hero at (" + gx + "," + selfGy + ")");
                    }
                    break;
                case OrderKind.Unsummon: // return an enemy hero WITHOUT Intercept to the rival's hand
                    {
                        int uk = Key(gx, enemyGy);
                        BUnit u;
                        if (gx == 1 && enemyGy == 1) { Log("  unsummon: cannot return the leader"); break; }
                        if (theirs != null && opPs.Units.TryGetValue(uk, out u) && u != null && !u.IsCorpse)
                        {
                            // Official Unsummon: return any enemy hero to its owner's hand (no Intercept restriction).
                            opPs.Units.Remove(uk);
                            SendDestroyUnit(theirs, mine, gx, enemyGy);
                            AddCardToHand(theirs, mine, b, 1 - mine.P, u.Card);
                            Log("  UNSUMMON: " + mine.Me.User + " returned enemy (" + gx + "," + enemyGy + ") card " + u.Card + " to " + theirs.Me.User + "'s hand");
                        }
                        else Log("  unsummon: no enemy hero at (" + gx + "," + enemyGy + ")");
                    }
                    break;
                case OrderKind.OrbDrain: // remove N of the rival's orbs
                    opPs.Orbs = Math.Max(0, opPs.Orbs - Math.Max(1, eff.Amount));
                    { BattleSlot z0 = mine.P == 0 ? mine : theirs, z1 = mine.P == 0 ? theirs : mine; if (z0 != null && z1 != null) SendOrbs(z0, z1, b); }
                    Log("  ORB DRAIN: rival orbs now " + opPs.Orbs);
                    break;
                case OrderKind.ShieldHero: // give a friendly hero (or the leader) Shield
                    {
                        if (gx == 1 && selfGy == 1) { ps.LeaderShield = true; Log("  SHIELD -> leader"); break; }
                        BUnit u;
                        if (ps.Units.TryGetValue(Key(gx, selfGy), out u) && u != null && !u.IsCorpse) { u.Shield = true; Log("  SHIELD -> (" + gx + "," + selfGy + ")"); }
                        else Log("  shield: no friendly hero at (" + gx + "," + selfGy + ")");
                    }
                    break;
                case OrderKind.GrantRanged: // Lizaveta: give a friendly hero Ranged Attack (persistent)
                    {
                        if (gx == 1 && selfGy == 1) { Log("  grant ranged: choose a hero, not the leader"); break; }
                        BUnit u;
                        if (ps.Units.TryGetValue(Key(gx, selfGy), out u) && u != null && !u.IsCorpse)
                        { u.GrantedRanged = true; u.Abilities |= UnitAbility.RangedAttack; Log("  GRANT RANGED -> (" + gx + "," + selfGy + ")"); }
                        else Log("  grant ranged: no friendly hero at (" + gx + "," + selfGy + ")");
                    }
                    break;
                case OrderKind.ShieldLeader: // give your leader Shield
                    ps.LeaderShield = true; Log("  SHIELD LEADER");
                    break;
                case OrderKind.ShieldVanguard: // give all your Vanguard heroes Shield
                    { int n = 0; for (int lane = 0; lane < 3; lane++) { BUnit u; if (ps.Units.TryGetValue(Key(2, lane), out u) && u != null && !u.IsCorpse) { u.Shield = true; n++; } } Log("  SHIELD VANGUARD (" + n + " heroes)"); }
                    break;
                case OrderKind.ShieldRear: // give all your Rear heroes Shield (Scholar: Rear: Shield)
                    { int n = 0; for (int lane = 0; lane < 3; lane++) { BUnit u; if (ps.Units.TryGetValue(Key(0, lane), out u) && u != null && !u.IsCorpse) { u.Shield = true; n++; } } Log("  SHIELD REAR (" + n + " heroes)"); }
                    break;
                case OrderKind.StrengthBuff: // +N attack (until end of turn) to a friendly hero
                    {
                        BUnit u;
                        if (ps.Units.TryGetValue(Key(gx, selfGy), out u) && u != null && !u.IsCorpse) { u.TempStrength += eff.Amount; Log("  STR BUFF +" + eff.Amount + " -> (" + gx + "," + selfGy + ")"); }
                        else Log("  str buff: no friendly hero at (" + gx + "," + selfGy + ")");
                    }
                    break;
                case OrderKind.StrengthBuffVanguard: // +N attack (until end of turn) to all your Vanguard heroes
                    { int n = 0; for (int lane = 0; lane < 3; lane++) { BUnit u; if (ps.Units.TryGetValue(Key(2, lane), out u) && u != null && !u.IsCorpse) { u.TempStrength += eff.Amount; n++; } } Log("  STR BUFF VANGUARD +" + eff.Amount + " (" + n + " heroes)"); }
                    break;
                case OrderKind.Silence: // an enemy hero loses all abilities + applied status
                    {
                        BUnit u;
                        if (opPs.Units.TryGetValue(Key(gx, enemyGy), out u) && u != null && !u.IsCorpse) { u.Silenced = true; Log("  SILENCE -> enemy (" + gx + "," + enemyGy + ")"); }
                        else Log("  silence: no enemy hero at (" + gx + "," + enemyGy + ")");
                    }
                    break;
                case OrderKind.Decoy: // mark a friendly hero the enemy must attack while it lives
                    {
                        BUnit u;
                        if (ps.Units.TryGetValue(Key(gx, selfGy), out u) && u != null && !u.IsCorpse) { u.Decoy = true; Log("  DECOY -> (" + gx + "," + selfGy + ")"); }
                        else Log("  decoy: no friendly hero at (" + gx + "," + selfGy + ")");
                    }
                    break;
                case OrderKind.Swap: // switch the caster's position with a chosen friendly hero
                    {
                        int ck = Key(casterX, casterY), tk = Key(gx, selfGy);
                        BUnit cu, tu;
                        if (ck != tk && ps.Units.TryGetValue(ck, out cu) && cu != null && !cu.IsCorpse
                                     && ps.Units.TryGetValue(tk, out tu) && tu != null && !tu.IsCorpse)
                        {
                            ps.Units[ck] = tu; ps.Units[tk] = cu;   // swap in the model
                            SendDestroyUnit(mine, theirs, casterX, casterY);
                            SendDestroyUnit(mine, theirs, gx, selfGy);
                            RecomputeAuras(ps);                     // stats depend on the new waves
                            SendSummonVisual(mine, theirs, tu.Card, casterX, casterY);
                            SendSummonVisual(mine, theirs, cu.Card, gx, selfGy);
                            SendUnitUpdateToBoth(mine, theirs, casterX, casterY, tu, b);
                            SendUnitUpdateToBoth(mine, theirs, gx, selfGy, cu, b);
                            Log("  SWAP: (" + casterX + "," + casterY + ") <-> (" + gx + "," + selfGy + ")");
                        }
                        else Log("  swap: need two living friendly heroes (caster " + casterX + "," + casterY + " and target " + gx + "," + selfGy + ")");
                    }
                    break;
                case OrderKind.Takedown: // defeat a chosen enemy hero AND the caster
                    KillEnemyAt(opPs, gx, enemyGy);
                    { BUnit cu; if (ps.Units.TryGetValue(Key(casterX, casterY), out cu) && cu != null && !cu.IsCorpse) { cu.Damage = cu.Max; Log("  TAKEDOWN: caster (" + casterX + "," + casterY + ") also defeated"); } }
                    break;
                case OrderKind.Seance: // return one of YOUR corpses to your hand
                    {
                        int ck = -1; BUnit clicked;
                        if (ps.Units.TryGetValue(Key(gx, selfGy), out clicked) && clicked != null && clicked.IsCorpse) ck = Key(gx, selfGy);
                        else foreach (var kv in ps.Units) if (kv.Value != null && kv.Value.IsCorpse) { ck = kv.Key; break; }
                        if (ck < 0) { Log("  seance: no corpse to return"); break; }
                        ushort scard = ps.Units[ck].Card;
                        ps.Units.Remove(ck);
                        SendClearCorpse(mine, theirs, ck / 10, ck % 10);
                        AddCardToHand(mine, theirs, b, mine.P, scard);
                        Log("  SEANCE: corpse (" + (ck / 10) + "," + (ck % 10) + ") card " + scard + " -> hand");
                    }
                    break;
                case OrderKind.Replicate: // add a copy of a chosen hero's card (either board) to YOUR hand
                    {
                        PlayerState tb = targetSelf ? ps : opPs;
                        BUnit u;
                        if (tb.Units.TryGetValue(Key(gx, y), out u) && u != null && !u.IsCorpse)
                        { AddCardToHand(mine, theirs, b, mine.P, u.Card); Log("  REPLICATE: copy of card " + u.Card + " (" + (targetSelf ? "own" : "enemy") + ") -> hand"); }
                        else Log("  replicate: no hero at (" + gx + "," + y + ") on " + (targetSelf ? "own" : "enemy") + " board");
                    }
                    break;
                case OrderKind.Infusion: // heal N from your leader, deal N to the caster
                    {
                        int n = Math.Max(1, eff.Amount);
                        ps.LeaderLife = Math.Min(ps.LeaderMax, ps.LeaderLife + n);
                        BUnit cu;
                        if (ps.Units.TryGetValue(Key(casterX, casterY), out cu) && cu != null && !cu.IsCorpse) cu.Damage += n;
                        Log("  INFUSION: leader +" + n + " (life " + ps.LeaderLife + "), caster (" + casterX + "," + casterY + ") -" + n);
                    }
                    break;
                case OrderKind.Polymorph: // transform a chosen hero (either board) into a random hero
                    {
                        PlayerState tb = targetSelf ? ps : opPs;
                        BattleSlot tOwner = targetSelf ? mine : theirs, tOpp = targetSelf ? theirs : mine;
                        int pk = Key(gx, y); BUnit u;
                        if (tOwner != null && tb.Units.TryGetValue(pk, out u) && u != null && !u.IsCorpse)
                        {
                            ushort nc; lock (_rng) nc = PolyPool[_rng.Next(PolyPool.Length)];
                            tb.Units.Remove(pk);
                            SendEffect(mine, theirs, casterX, casterY, gx, y, targetSelf, 22, false, 0); // obj_single_polymorph
                            SendDestroyUnit(tOwner, tOpp, gx, y);
                            PlaceUnit(tOwner, tOpp, b, nc, gx, y, 0, false);
                            Log("  POLYMORPH: " + (targetSelf ? "own" : "enemy") + " (" + gx + "," + y + ") -> card " + nc);
                        }
                        else Log("  polymorph: no hero at (" + gx + "," + y + ") on " + (targetSelf ? "own" : "enemy") + " board");
                    }
                    break;
                case OrderKind.Copycat: // the caster becomes a copy of a chosen hero (either board)
                    {
                        PlayerState tb = targetSelf ? ps : opPs;
                        BUnit tu, cu;
                        if (tb.Units.TryGetValue(Key(gx, y), out tu) && tu != null && !tu.IsCorpse
                            && ps.Units.TryGetValue(Key(casterX, casterY), out cu) && cu != null && !cu.IsCorpse)
                        {
                            ushort copy = tu.Card;
                            ps.Units.Remove(Key(casterX, casterY));
                            SendDestroyUnit(mine, theirs, casterX, casterY);
                            PlaceUnit(mine, theirs, b, copy, casterX, casterY, 0, false);
                            Log("  COPYCAT: caster (" + casterX + "," + casterY + ") -> copy of card " + copy + " (" + (targetSelf ? "own" : "enemy") + ")");
                        }
                        else Log("  copycat: need a living caster and a target hero on " + (targetSelf ? "own" : "enemy") + " board");
                    }
                    break;
                case OrderKind.Immortality: // Ongoing: your VANGUARD heroes can't die by any means for 3 waves
                    ps.ImmortalWaves = 3;
                    Log("  IMMORTALITY: " + mine.Me.User + "'s vanguard heroes can't die for 3 waves");
                    break;
                case OrderKind.Restructure: // this turn, MOVE and CLEAR CORPSE are free (no action cost)
                    ps.RestructureFree = true;
                    Log("  RESTRUCTURE: " + mine.Me.User + "'s moves + corpse-clears are free until end of turn");
                    break;
                case OrderKind.ResurrectAll: // restore ALL corpses to life on BOTH boards (full HP)
                    ReviveAllCorpses(mine, theirs, b, ps);
                    if (theirs != null) ReviveAllCorpses(theirs, mine, b, opPs);
                    break;
                case OrderKind.LucHaste: // Luc Von Gott leader ability: -1 leader life, +2 actions this turn
                    ps.LeaderLife = Math.Max(0, ps.LeaderLife - 1);
                    ps.ActionsRemaining += 2;
                    SendLeaderHp(mine, theirs, ps);
                    Log("  LEADER (Luc Von Gott): -1 leader life (now " + ps.LeaderLife + "), +2 actions");
                    break;
                case OrderKind.Scry: // open the interactive Scry UI: show top N, player picks one to draw (op51)
                    OpenScry(mine, ps, eff.Amount);
                    break;
                case OrderKind.Phantom: // add N copies of random discard cards to hand; life=1, vanish at end of turn
                    {
                        if (ps.Discard.Count == 0) { Log("  phantom: discard pile empty"); break; }
                        int added = 0;
                        for (int i = 0; i < Math.Max(1, eff.Amount); i++)
                        {
                            int pi2; lock (_rng) pi2 = _rng.Next(ps.Discard.Count);
                            ushort pcard = ps.Discard[pi2]; // copy — the card stays in the discard pile
                            AddCardToHand(mine, theirs, b, mine.P, pcard);
                            ps.PhantomCards.Add(pcard);
                            added++;
                        }
                        Log("  PHANTOM: added " + added + " discard copy(ies) to hand (life 1, vanish EOT)");
                    }
                    break;
                case OrderKind.Reload: // shuffle your hand back into your deck, then draw that many + 1
                    {
                        int handSize = mine.Hand.Count;
                        for (int pos = handSize - 1; pos >= 0; pos--)
                        {
                            ushort rc = mine.Hand[pos];
                            ps.Deck.Add(rc);
                            mine.Hand.RemoveAt(pos);
                            Send(mine.Me.Ns, new PacketWriter().WriteU8((byte)pos).WriteU16(rc).WriteBool(true).Frame(Op.HandCardRemove));
                            if (theirs != null)
                                Send(theirs.Me.Ns, new PacketWriter().WriteU8((byte)Math.Min(255, mine.Hand.Count)).WriteU8((byte)pos).WriteU16(rc).Frame(Op.HandCardRemoveGet));
                        }
                        lock (_rng) for (int i = ps.Deck.Count - 1; i > 0; i--) { int j = _rng.Next(i + 1); var t = ps.Deck[i]; ps.Deck[i] = ps.Deck[j]; ps.Deck[j] = t; }
                        DrawCardsFor(mine, theirs, b, handSize + 1);
                        Log("  RELOAD: shuffled " + handSize + " card(s) back, drew " + (handSize + 1));
                    }
                    break;
                case OrderKind.Duplicate: // add a copy of a card in your hand to your hand (auto: random hand card)
                    {
                        if (mine.Hand.Count == 0) { Log("  duplicate: hand empty"); break; }
                        int di; lock (_rng) di = _rng.Next(mine.Hand.Count);
                        ushort dc = mine.Hand[di];
                        AddCardToHand(mine, theirs, b, mine.P, dc);
                        Log("  DUPLICATE: added a copy of hand card " + dc);
                    }
                    break;
                case OrderKind.Transfusion: // heal ALL damage from a target ally hero; the caster takes that much
                    {
                        BUnit tf;
                        if (ps.Units.TryGetValue(Key(gx, selfGy), out tf) && tf != null && !tf.IsCorpse)
                        {
                            int healed = tf.Damage;
                            tf.Damage = 0;
                            BUnit cf;
                            if (ps.Units.TryGetValue(Key(casterX, casterY), out cf) && cf != null && !cf.IsCorpse)
                                cf.Damage += healed;
                            Log("  TRANSFUSION: healed " + healed + " from (" + gx + "," + selfGy + "); caster (" + casterX + "," + casterY + ") takes " + healed);
                        }
                        else Log("  transfusion: no ally hero at (" + gx + "," + selfGy + ")");
                    }
                    break;
                case OrderKind.Enervate: // target enemy hero has -N attack (persistent debuff)
                    {
                        BUnit en;
                        if (opPs.Units.TryGetValue(Key(gx, enemyGy), out en) && en != null && !en.IsCorpse)
                        {
                            en.Enervate += eff.Amount;
                            Log("  ENERVATE -" + eff.Amount + " attack -> enemy (" + gx + "," + enemyGy + ")");
                        }
                        else Log("  enervate: no enemy hero at (" + gx + "," + enemyGy + ")");
                    }
                    break;
                case OrderKind.MindControl: // swap the caster with an enemy hero; control swaps (you gain the enemy hero)
                    {
                        int mk = Key(casterX, casterY), ek = Key(gx, enemyGy);
                        BUnit cu, eu;
                        if (theirs != null && !(gx == 1 && enemyGy == 1)
                            && ps.Units.TryGetValue(mk, out cu) && cu != null && !cu.IsCorpse
                            && opPs.Units.TryGetValue(ek, out eu) && eu != null && !eu.IsCorpse)
                        {
                            ushort cCard = cu.Card, eCard = eu.Card; int cDmg = cu.Damage, eDmg = eu.Damage;
                            ps.Units.Remove(mk); opPs.Units.Remove(ek);
                            SendDestroyUnit(mine, theirs, casterX, casterY);
                            SendDestroyUnit(theirs, mine, gx, enemyGy);
                            PlaceUnit(mine, theirs, b, eCard, casterX, casterY, eDmg, true); // you gain the enemy hero
                            PlaceUnit(theirs, mine, b, cCard, gx, enemyGy, cDmg, true);       // opponent gets your caster
                            Log("  MIND CONTROL: (" + casterX + "," + casterY + ") <-> enemy (" + gx + "," + enemyGy + "); control swapped");
                        }
                        else Log("  mind control: need a living caster and a non-leader enemy hero");
                    }
                    break;
                case OrderKind.ForceCube: // summon a Force Cube (card 90) into an empty space on your side
                    {
                        int fx, fy;
                        if (gx >= 0 && gx <= 2 && selfGy >= 0 && selfGy <= 2 && !(gx == 1 && selfGy == 1) && !ps.Units.ContainsKey(Key(gx, selfGy)))
                        { fx = gx; fy = selfGy; }
                        else if (!FindEmptySlot(ps, b.Wave, out fx, out fy)) { Log("  force cube: no empty slot"); break; }
                        PlaceUnit(mine, theirs, b, 90, fx, fy, 0, true);
                        Log("  FORCE CUBE: summoned at (" + fx + "," + fy + ")");
                    }
                    break;
                case OrderKind.Entomb: // rival discards N random cards; each becomes a CORPSE in an empty enemy space
                    {
                        int n = Math.Max(1, eff.Amount);
                        for (int i = 0; i < n; i++)
                        {
                            if (theirs == null || theirs.Hand.Count == 0) { Log("  entomb: rival hand empty"); break; }
                            int ex, ey;
                            if (i == 0 && gx >= 0 && gx <= 2 && enemyGy >= 0 && enemyGy <= 2 && !(gx == 1 && enemyGy == 1) && !opPs.Units.ContainsKey(Key(gx, enemyGy)))
                            { ex = gx; ey = enemyGy; }
                            else if (!FindEmptySlot(opPs, 2, out ex, out ey)) { Log("  entomb: no empty enemy slot"); break; }
                            int hi; lock (_rng) hi = _rng.Next(theirs.Hand.Count);
                            ushort ec = theirs.Hand[hi]; theirs.Hand.RemoveAt(hi);
                            Send(theirs.Me.Ns, new PacketWriter().WriteU8((byte)hi).WriteU16(ec).WriteBool(true).Frame(Op.HandCardRemove));
                            Send(mine.Me.Ns, new PacketWriter().WriteU8((byte)Math.Min(255, theirs.Hand.Count)).WriteU8((byte)hi).WriteU16(ec).Frame(Op.HandCardRemoveGet));
                            var corpse = new BUnit { Card = ec, Max = LifeOf(ec), Damage = 0, IsCorpse = true, CorpseFresh = false };
                            opPs.Units[Key(ex, ey)] = corpse;
                            // Render: summon the unit on the enemy board, then push it as a corpse (dead=1).
                            Send(theirs.Me.Ns, new PacketWriter().WriteU16(ec).WriteU8((byte)ex).WriteU8((byte)ey).WriteBool(false).Frame(Op.SummonUnit));
                            Send(mine.Me.Ns, new PacketWriter().WriteU16(ec).WriteU8((byte)ex).WriteU8((byte)ey).WriteBool(false).Frame(Op.SummonUnitGet));
                            SendUnitUpdateToBoth(theirs, mine, ex, ey, corpse, b);
                            Log("  ENTOMB: " + theirs.Me.User + " discards card " + ec + " -> corpse at enemy (" + ex + "," + ey + ")");
                        }
                    }
                    break;
                case OrderKind.TrapCancelAttack: // arm: negate the rival's next attack (+ Backlash the attacker)
                    ps.TrapCancelAttack = true; Log("  TRAP ARMED: Cancel Attack (" + mine.Me.User + ")");
                    break;
                case OrderKind.TrapCancelSpell:  // arm: negate the rival's next wave-spell (owner draws 1)
                    ps.TrapCancelSpell = true; Log("  TRAP ARMED: Cancel Spell (" + mine.Me.User + ")");
                    break;
                case OrderKind.TrapCancelOrder:  // arm: negate the rival's next order
                    ps.TrapCancelOrder = true; Log("  TRAP ARMED: Cancel Order (" + mine.Me.User + ")");
                    break;
                case OrderKind.DamageAll:   // Poison (official): X damage to ALL rival heroes
                    foreach (var kv in opPs.Units) DamageEnemyAt(opPs, kv.Key / 10, kv.Key % 10, eff.Amount);
                    break;
                case OrderKind.DamageSpread: // Meteor Storm (official): X damage DIVIDED randomly among enemy heroes (X separate 1-dmg hits)
                    for (int i = 0; i < eff.Amount; i++)
                    {
                        var alive = new List<int>();
                        foreach (var kv in opPs.Units) if (kv.Value != null && !kv.Value.IsCorpse) alive.Add(kv.Key);
                        if (alive.Count == 0) { Log("  meteor storm: no enemy heroes left"); break; }
                        int mk; lock (_rng) mk = alive[_rng.Next(alive.Count)];
                        DamageEnemyAt(opPs, mk / 10, mk % 10, 1);
                    }
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
                case OrderKind.SummonCopy:
                    // Summon a copy of the last hero you recruited into an empty slot.
                    {
                        int sx, sy;
                        if (ps.LastSummonedCard != 0 && FindEmptySlot(ps, b.Wave, out sx, out sy))
                            PlaceUnit(mine, theirs, b, ps.LastSummonedCard, sx, sy, 0, true);
                        else Log("  summon copy: none to copy / board full");
                    }
                    break;
                case OrderKind.SummonRandom:
                    // Wild Summon / Summon x3: place N random heroes from your deck into empty slots.
                    for (int i = 0; i < eff.Amount; i++)
                    {
                        int sx, sy;
                        if (ps.Deck == null || ps.Deck.Count == 0) { Log("  wild summon: deck empty"); break; }
                        if (!FindEmptySlot(ps, b.Wave, out sx, out sy)) { Log("  wild summon: board full"); break; }
                        int idx; lock (_rng) idx = _rng.Next(ps.Deck.Count);
                        ushort rc = ps.Deck[idx]; ps.Deck.RemoveAt(idx);
                        PlaceUnit(mine, theirs, b, rc, sx, sy, 0, true);
                    }
                    break;
                case OrderKind.WildSummon:
                    // Steal a RANDOM card from the rival's hand, play its order effect AGAINST them
                    // (resolves from the caster's side), then discard it from the rival's hand.
                    {
                        if (theirs == null || theirs.Hand == null || theirs.Hand.Count == 0)
                        { Log("  wild summon: rival hand empty"); break; }
                        int hidx; lock (_rng) hidx = _rng.Next(theirs.Hand.Count);
                        ushort stolen = theirs.Hand[hidx];
                        theirs.Hand.RemoveAt(hidx);
                        DiscardCard(theirs, mine, stolen); // Wild Summon plays then discards the rival's card
                        // Rival's own hand view removes the card; caster's view of the rival hand-count shrinks.
                        Send(theirs.Me.Ns, new PacketWriter().WriteU8((byte)hidx).WriteU16(stolen).WriteBool(true).Frame(Op.HandCardRemove));
                        Send(mine.Me.Ns, new PacketWriter().WriteU8((byte)Math.Min(255, theirs.Hand.Count)).WriteU8((byte)hidx).WriteU16(stolen).Frame(Op.HandCardRemoveGet));
                        Log("  WILD SUMMON: " + mine.Me.User + " steals card " + stolen + " from " + theirs.Me.User + " hand[" + hidx + "]");

                        OrderEffect inner = OrderOf(stolen);
                        if (inner.Kind == OrderKind.None || inner.Kind == OrderKind.WildSummon)
                        { Log("  wild summon: stolen card resolves to no-op (kind=" + inner.Kind + ")"); break; }
                        inner.Free = true; // the outer Wild Summon pays the action; the inner must not double-charge
                        int igx, igy; PickAutoTarget(inner.Kind, b, ps, opPs, out igx, out igy);
                        Log("  wild summon: playing " + inner.Kind + " amt=" + inner.Amount + " @ (" + igx + "," + igy + ")");
                        ResolveEffect(inner, mine, theirs, b, (byte)igx, (byte)igy);
                        if (b.Over) return; // inner effect already ended the match; don't run the outer tail
                    }
                    break;
                case OrderKind.RaiseDead:
                    // Clear up to Amount of YOUR corpses; for each cleared, summon a Zombie (card 102).
                    {
                        var corpses = new List<int>();
                        foreach (var kv in ps.Units) if (kv.Value != null && kv.Value.IsCorpse) corpses.Add(kv.Key);
                        int cleared = 0;
                        foreach (int ckey in corpses)
                        {
                            if (cleared >= eff.Amount) break;
                            DiscardCard(mine, theirs, ps.Units[ckey].Card);
                            ps.Units.Remove(ckey);
                            SendClearCorpse(mine, theirs, ckey / 10, ckey % 10);
                            int sx, sy;
                            if (FindEmptySlot(ps, b.Wave, out sx, out sy)) PlaceUnit(mine, theirs, b, 102, sx, sy, 0, true);
                            cleared++;
                        }
                        if (cleared == 0) Log("  raise dead: no corpses to clear");
                    }
                    break;
                case OrderKind.Revive:
                case OrderKind.Resurrect:
                    // Turn one of your corpses back into a live hero (Revive = half HP, Resurrect = full).
                    // Prefer the corpse the player clicked (gx,selfGy); else the first own corpse.
                    {
                        int ck = -1;
                        BUnit clicked;
                        if (ps.Units.TryGetValue(Key(gx, selfGy), out clicked) && clicked != null && clicked.IsCorpse)
                            ck = Key(gx, selfGy);
                        else foreach (var kv in ps.Units) if (kv.Value != null && kv.Value.IsCorpse) { ck = kv.Key; break; }
                        if (ck < 0) { Log("  " + eff.Kind + ": no corpse to revive"); break; }
                        ushort rcard = ps.Units[ck].Card;
                        int cx2 = ck / 10, cy2 = ck % 10;
                        ps.Units.Remove(ck); // clear the corpse (model) + tell the clients (visual)
                        SendClearCorpse(mine, theirs, cx2, cy2);
                        // Revive = comes back with 1 HP (damage = max-1); Resurrect = full HP.
                        int rdmg = (eff.Kind == OrderKind.Resurrect) ? 0 : Math.Max(0, LifeOf(rcard) - 1);
                        PlaceUnit(mine, theirs, b, rcard, cx2, cy2, rdmg, true);
                        Log("  " + eff.Kind + " -> (" + cx2 + "," + cy2 + ") card " + rcard + " dmg " + rdmg);
                    }
                    break;
                case OrderKind.Inspire:
                    // Choose a friendly hero; it makes a RANGED attack at a random rival hero (or leader).
                    {
                        if (gx == 1 && selfGy == 1) { Log("  inspire: choose a hero, not the leader"); break; }
                        BUnit hero;
                        if (!ps.Units.TryGetValue(Key(gx, selfGy), out hero) || hero == null || hero.IsCorpse)
                        { Log("  inspire: no hero at (" + gx + "," + selfGy + ")"); break; }
                        var targets = new List<int>();
                        foreach (var kv in opPs.Units) if (kv.Value != null && !kv.Value.IsCorpse) targets.Add(kv.Key);
                        int dmg = Math.Max(1, hero.Atk);
                        if (targets.Count > 0)
                        {
                            int tk; lock (_rng) tk = targets[_rng.Next(targets.Count)];
                            int tgx = tk / 10, tgy = tk % 10;
                            BUnit t = opPs.Units[tk];
                            int actual = Math.Max(1, dmg - t.Armor);
                            t.Damage += actual;
                            Log("  inspire -> (" + gx + "," + selfGy + ") ranged-attacks (" + tgx + "," + tgy + ") for " + actual);
                            SendAttackAndUpdate(mine, theirs, (byte)gx, (byte)selfGy, (byte)tgx, (byte)tgy, actual, false, t, true, false, b);
                        }
                        else
                        {
                            opPs.LeaderLife -= dmg;
                            Log("  inspire -> (" + gx + "," + selfGy + ") ranged-attacks LEADER for " + dmg);
                            SendAttackAndLeaderUpdate(mine, theirs, (byte)gx, (byte)selfGy, 1, 1, dmg, false, opPs, true, false, b);
                        }
                    }
                    break;
            }

            // EFFECT PROJECTILE (op41): fire the confirmed queue-safe arrow (eid 0) from the caster along
            // the real trajectory to the target, for single-target enemy-damage effects. The cast-pose
            // bracket (op44/op45 in HandleOrder) gates next_que_effect around it. PTO_EFXID (>=0) overrides
            // the eid AND fires for every effect (calibration mode).
            if (theirs != null)
            {
                if (EfxProbeId >= 0)
                {
                    int eid = EfxProbeId;
                    if (EfxCycle)
                    {
                        if (_efxNext == int.MinValue) _efxNext = Math.Max(0, EfxProbeId);
                        // skip the already-identified projectile eids
                        while (_efxNext == 0 || _efxNext == 12 || _efxNext == 16 || _efxNext == 21 || _efxNext == 23) _efxNext++;
                        eid = _efxNext;
                        _efxNext++;
                    }
                    if (eid >= 0 && eid <= 25)
                    {
                        int oi = EfxObjIndex[eid];
                        Log("  EFX " + (EfxCycle ? "CYCLE" : "PROBE") + ": eid=" + eid + " (objIndex " + oi + ") "
                            + "(" + casterX + "," + casterY + ") -> enemy (" + gx + "," + enemyGy + ")  [cast this at an enemy; note the visual + whether play continues]");
                        SendEffect(mine, theirs, casterX, casterY, gx, enemyGy, false, eid, false, eff.Amount);
                    }
                    else Log("  EFX CYCLE: swept past eid 25 (done)");
                }
                else
                {
                    // Production: fire the themed effect(s) per effect kind (see EfxOf/FireEffect).
                    FireEffect(eff, mine, theirs, b, casterX, casterY, gx, enemyGy, selfGy);
                }
            }

            // Turn any enemy units the effect just killed into corpses immediately (kept in sync).
            if (theirs != null) ProcessImmediateDeaths(opPs, theirs, mine, b);
            ProcessImmediateDeaths(ps, mine, theirs, b); // own deaths too (e.g. Takedown defeats the caster)

            // A single-target damage can hit the enemy leader at (1,1) — check for a win.
            if (opPs.LeaderLife <= 0)
            {
                b.Over = true;
                Log("BATTLE END: " + mine.Me.User + " wins (order/spell lethal)");
                Send(mine.Me.Ns, new PacketWriter().WriteBool(true).WriteU16(0).Frame(Op.BattleEnd));
                if (theirs != null) Send(theirs.Me.Ns, new PacketWriter().WriteBool(false).WriteU16(0).Frame(Op.BattleEnd));
                return;
            }

            // Push updated life/state to both clients, then spend the action (Free/Quick effects don't
            // cost an action — just re-grant so the client unlocks with its count unchanged).
            BattleSlot p0slot = mine.P == 0 ? mine : theirs;
            BattleSlot p1slot = mine.P == 0 ? theirs : mine;
            if (p0slot != null && p1slot != null) SyncUnitStates(p0slot, p1slot, b);
            if (eff.Free) GrantAction(mine); else ConsumeAction(mine, theirs, b);

            // RIDER: fire the order/spell's second effect on the SAME target. Marked Free so it re-grants
            // instead of spending a second action (the order already paid one above).
            if (eff.Kind2 != OrderKind.None && !b.Over)
                ResolveEffect(new OrderEffect { Kind = eff.Kind2, Amount = eff.Amount2, Free = true }, mine, theirs, b, x, y, casterX, casterY, targetSelf);
        }

        // Pick a sensible (gx,gy) for an effect that has no player-chosen target — used when Wild Summon
        // plays a stolen card whose order normally needs a click. ps = caster's board, opPs = rival's.
        static void PickAutoTarget(OrderKind k, Battle b, PlayerState ps, PlayerState opPs, out int gx, out int gy)
        {
            gx = b.Wave; gy = 1; // default: current wave, centre lane (self/random effects ignore this)
            switch (k)
            {
                case OrderKind.DamageSingle:
                case OrderKind.DamageBlast:
                case OrderKind.KillHero:
                    {   // a random living rival hero; fall back to the rival leader
                        var alive = new List<int>();
                        foreach (var kv in opPs.Units) if (kv.Value != null && !kv.Value.IsCorpse) alive.Add(kv.Key);
                        if (alive.Count > 0) { int key; lock (_rng) key = alive[_rng.Next(alive.Count)]; gx = key / 10; gy = key % 10; }
                        else { gx = 1; gy = 1; }
                    }
                    break;
                case OrderKind.DamageRow:      // Fire: a whole lane -> pick a random lane
                    lock (_rng) gy = _rng.Next(3);
                    gx = b.Wave;
                    break;
                case OrderKind.DamageColumn:   // Thunder: a whole wave -> use the current wave
                    gx = b.Wave; gy = 1;
                    break;
                case OrderKind.HealSingle:
                    {   // a random damaged friendly hero; fall back to centre
                        var dmgd = new List<int>();
                        foreach (var kv in ps.Units) if (kv.Value != null && !kv.Value.IsCorpse && kv.Value.Damage > 0) dmgd.Add(kv.Key);
                        if (dmgd.Count > 0) { int key; lock (_rng) key = dmgd[_rng.Next(dmgd.Count)]; gx = key / 10; gy = key % 10; }
                        else { gx = 1; gy = 1; }
                    }
                    break;
                case OrderKind.Inspire:
                    {   // a random friendly non-leader hero (the Inspire case no-ops if none)
                        var heroes = new List<int>();
                        foreach (var kv in ps.Units) if (kv.Value != null && !kv.Value.IsCorpse && kv.Key != Key(1, 1)) heroes.Add(kv.Key);
                        if (heroes.Count > 0) { int key; lock (_rng) key = heroes[_rng.Next(heroes.Count)]; gx = key / 10; gy = key % 10; }
                        else { gx = 0; gy = 0; }
                    }
                    break;
                case OrderKind.Revive:
                case OrderKind.Resurrect:
                    gx = 0; gy = 0; // the Revive/Resurrect case self-selects the first own corpse
                    break;
            }
        }

        // Wave-position spells (v/f/r) for the deck. wave: 2=Vanguard,1=Flank,0=Rear (=caster grid_x).
        static OrderEffect SpellOf(ushort cardId, int wave)
        {
            switch (cardId / 2)
            {
                // ---- LEADER active abilities (leader casts its Flank f_spell via the isSpell path at grid_x=1).
                //      Leader REALs (0-25) never collide with hero REALs (26+). ----
                case 19: return new OrderEffect { Kind = OrderKind.LucHaste, Free = true };      // Luc Von Gott: apply 1 dmg to leader, gain 2 actions
                case 21: return new OrderEffect { Kind = OrderKind.Banish, Amount = 1 };         // Khadath: Banish 1 (once/turn — client-gated)
                case 23: return new OrderEffect { Kind = OrderKind.RaiseDead, Amount = 3 };      // Hepzibah: cast Raise Dead
                // ---- gen-2 leader actives (same isSpell path; f_spell=4/5 makes the client always offer the
                //      cast). Mapped to existing effects; minor riders noted (leader life/str cost, discard) TODO.
                case 70: return new OrderEffect { Kind = OrderKind.ShieldHero, Free = true };    // Merjoram: Quick: give a hero Shield (rider: -2 leader life TODO)
                case 73: return new OrderEffect { Kind = OrderKind.Phantom, Amount = 1 };        // Luca: cast Phantom (return a random discard to hand)
                case 74: return new OrderEffect { Kind = OrderKind.FinisherKill, Free = true };  // Rayne: Quick: defeat a DAMAGED enemy hero (rider: discard a random card TODO)
                case 77: return new OrderEffect { Kind = OrderKind.StrengthBuff, Amount = 2 };   // Ivo: give a hero Strength 2 (+ passive +Str/hero aura, done in ApplyLeaderUnitGrants)
                case 78: return new OrderEffect { Kind = OrderKind.GrantRanged };                // Lizaveta: give a friendly hero Ranged Attack (persistent)
                case 79: return new OrderEffect { Kind = OrderKind.Polymorph };                  // Riflam: transform target hero into a random hero (new hero enters full-life)
                case 99: return new OrderEffect { Kind = OrderKind.Silence };                    // Baenvier: Silence an enemy hero (rider: +1 leader Strength TODO)
                case 111:return new OrderEffect { Kind = OrderKind.OrbBoost, Amount = 1 };        // Malandrax: gain an orb (rider: discard a random card + 6-orb cap TODO)
                case 28: if (wave == 0) return new OrderEffect { Kind = OrderKind.DamageRandom, Amount = 5 }; break; // Berserker R: Bombard 5 (random)
                case 29: if (wave == 2) return new OrderEffect { Kind = OrderKind.DamageLeader, Amount = 4 };        // Assassin V: Backstab 4 (leader)
                         if (wave == 0) return new OrderEffect { Kind = OrderKind.DamageLeader, Amount = 2 }; break; // Assassin R: Backstab 2 (leader)
                case 30: if (wave == 0) return new OrderEffect { Kind = OrderKind.DamageAll,    Amount = 1 }; break; // Alchemist R: Poison 1 (all)
                case 32: if (wave == 1) return new OrderEffect { Kind = OrderKind.HealSingle,   Amount = 4 };        // Healer F: Cure 4
                         if (wave == 0) return new OrderEffect { Kind = OrderKind.HealAll,      Amount = 2 }; break; // Healer R: Cure All 2
                case 34: if (wave == 0) return new OrderEffect { Kind = OrderKind.SummonRandom, Amount = 1 };
                         if (wave == 1) return new OrderEffect { Kind = OrderKind.Banish, Amount = 1 };
                         if (wave == 2) return new OrderEffect { Kind = OrderKind.Retreat };            break; // Illusionist R: Summon / F: Banish 1 / V: Retreat
                case 46: if (wave == 1) return new OrderEffect { Kind = OrderKind.SummonRandom, Amount = 1 };
                         if (wave == 2) return new OrderEffect { Kind = OrderKind.Unsummon };
                         if (wave == 0) return new OrderEffect { Kind = OrderKind.Scry, Amount = 3 }; break; // Summoner F: Summon / V: Unsummon / R: Scry 3
                case 38: if (wave == 2) return new OrderEffect { Kind = OrderKind.Banish, Amount = 1 };       // Oracle V: Banish 1
                         if (wave == 1) return new OrderEffect { Kind = OrderKind.Scry, Amount = 3 };        // Oracle F: Scry 3
                         if (wave == 0) return new OrderEffect { Kind = OrderKind.Inspire }; break;          // Oracle R: Inspire
                case 36: if (wave == 2) return new OrderEffect { Kind = OrderKind.HealSingle,   Amount = 4 };        // Mascot V: Cure 4
                         return new OrderEffect { Kind = OrderKind.Inspire };                                       // Mascot F/R: Inspire
                case 37: if (wave == 2) return new OrderEffect { Kind = OrderKind.HealSingle,   Amount = 4 };        // Mystic V: Cure 4
                         if (wave == 0) return new OrderEffect { Kind = OrderKind.DrawCards,    Amount = 1, Free = true }; break; // Mystic R: Quick Draw 1
                case 39: if (wave == 1 || wave == 0) return new OrderEffect { Kind = OrderKind.Inspire }; break;     // Overlord F/R: Inspire
                case 42: if (wave == 2) return new OrderEffect { Kind = OrderKind.HealSingle,   Amount = 4 };        // Priestess V: Cure 4
                         if (wave == 1) return new OrderEffect { Kind = OrderKind.HealLeader,   Amount = 2 };        // Priestess F: Cure Leader 2
                         if (wave == 0) return new OrderEffect { Kind = OrderKind.Revive };                  break; // Priestess R: Revive
                case 50: if (wave == 2) return new OrderEffect { Kind = OrderKind.Revive, Free = true };              // Witch V: QUICK Spell: Revive (free)
                         if (wave == 1) return new OrderEffect { Kind = OrderKind.RaiseDead,    Amount = 1 };
                         if (wave == 0) return new OrderEffect { Kind = OrderKind.Seance }; break;                   // Witch R: Seance (corpse -> hand)
                case 43: if (wave == 2) return new OrderEffect { Kind = OrderKind.DamageFrontEach, Amount = 3 };     // Pyromancer V: Blast 3 (front of each row)
                         if (wave == 0) return new OrderEffect { Kind = OrderKind.DamageRow,    Amount = 3 }; break; // Pyromancer R: Fire 3 (row)
                case 44: if (wave == 2) return new OrderEffect { Kind = OrderKind.ForceCube };                       // Scientist V: Force Cube (summon a Force Cube unit)
                         if (wave == 1) return new OrderEffect { Kind = OrderKind.DrawCards,    Amount = 2 };        // Scientist F: Draw 2
                         if (wave == 0) return new OrderEffect { Kind = OrderKind.Inspire };                 break; // Scientist R: Inspire
                case 52: if (wave == 0) return new OrderEffect { Kind = OrderKind.DamageRow,    Amount = 2 }; break; // Fire Elem R: Fire 2
                case 53: if (wave == 0) return new OrderEffect { Kind = OrderKind.HealSingle,   Amount = 3 }; break; // Water Elem R: Cure 3
                case 56: if (wave == 1) return new OrderEffect { Kind = OrderKind.DamageColumn, Amount = 2 }; break; // Lightning Elem F: Thunder 2 (column)
                case 48: if (wave == 0) return new OrderEffect { Kind = OrderKind.Banish, Amount = 1 }; break;      // Trapper R: Banish 1
                case 69: if (wave == 0) return new OrderEffect { Kind = OrderKind.DamageSingle, Amount = 7 };        // Warmage R: Meteor 7 (single hero)
                         if (wave == 2) return new OrderEffect { Kind = OrderKind.DamageFrontEach, Amount = 5 }; break; // Warmage V: Blast 5 (front of each row)
                case 86: if (wave == 0) return new OrderEffect { Kind = OrderKind.FinisherKill }; break;            // Lancer R: Finisher Kill
                case 87: if (wave == 0) return new OrderEffect { Kind = OrderKind.Backlash }; break;                // Reflector R: Backlash
                case 104:if (wave == 2) return new OrderEffect { Kind = OrderKind.Scry, Amount = 2, Free = true };  // Magic Student V: Quick Scry 2
                         if (wave == 1) return new OrderEffect { Kind = OrderKind.DrawCards, Amount = 1, Free = true }; // Magic Student F: Quick Draw 1
                         if (wave == 0) return new OrderEffect { Kind = OrderKind.DamageSingle, Amount = 2, Free = true }; break; // Magic Student R: Quick Meteor 2
                case 112:if (wave == 2) return new OrderEffect { Kind = OrderKind.OrbBoost, Amount = 1 };            // Mastermind V: Orb Boost 1
                         if (wave == 1) return new OrderEffect { Kind = OrderKind.Duplicate };                       // Mastermind F: Duplicate
                         if (wave == 0) return new OrderEffect { Kind = OrderKind.OrbDrain, Amount = 1 }; break;    // Mastermind R: Orb Drain 1
                case 84: if (wave == 0) return new OrderEffect { Kind = OrderKind.StrengthBuff, Amount = 1 }; break; // Fencer R: Strength 1 (buff a hero)
                case 85: if (wave == 2) return new OrderEffect { Kind = OrderKind.StrengthBuffVanguard, Amount = 1 }; // Wizard V: Vanguard: Strength 1
                         if (wave == 1) return new OrderEffect { Kind = OrderKind.Silence };                        // Wizard F: Silence (an enemy hero)
                         if (wave == 0) return new OrderEffect { Kind = OrderKind.Phantom, Amount = 1 }; break;      // Wizard R: Phantom
                case 94: if (wave == 1) return new OrderEffect { Kind = OrderKind.ShieldLeader }; break;            // Magus F: Shield Leader
                case 105:if (wave == 2) return new OrderEffect { Kind = OrderKind.ShieldHero };                     // Scholar V: Shield Hero
                         if (wave == 0) return new OrderEffect { Kind = OrderKind.ShieldRear }; break;             // Scholar R: Rear: Shield (shield all your rear heroes)
                case 54: if (wave == 0) return new OrderEffect { Kind = OrderKind.Swap, Free = true }; break;       // Air Elemental R: Quick Swap
                case 64: if (wave == 0) return new OrderEffect { Kind = OrderKind.Swap, Free = true };              // Doppelganger R: Quick Swap
                         if (wave == 1) return new OrderEffect { Kind = OrderKind.Replicate };                       // Doppelganger F: Replicate
                         if (wave == 2) return new OrderEffect { Kind = OrderKind.Copycat, Free = true }; break;     // Doppelganger V: Quick Copycat
                case 97: if (wave == 0) return new OrderEffect { Kind = OrderKind.Takedown };                       // Dark Knight R: Takedown
                         if (wave == 1) return new OrderEffect { Kind = OrderKind.DamageRow, Amount = 3 }; break;   // Dark Knight F: Fire 3
                case 27: if (wave == 2) return new OrderEffect { Kind = OrderKind.KillHero }; break;                // Dragon Mage V: Execute (defeat a hero)
                case 68: if (wave == 1) return new OrderEffect { Kind = OrderKind.KillHero };                       // Sniper F: Execute
                         if (wave == 0) return new OrderEffect { Kind = OrderKind.Banish, Amount = 1 }; break;      // Sniper R: Banish
                case 62: if (wave == 2) return new OrderEffect { Kind = OrderKind.Enervate, Amount = 3 };            // Diabloist V: Enervate 3 (-3 attack to an enemy hero)
                         if (wave == 1) return new OrderEffect { Kind = OrderKind.Seance };                          // Diabloist F: Seance
                         if (wave == 0) return new OrderEffect { Kind = OrderKind.Entomb, Amount = 1 }; break;       // Diabloist R: Entomb
                case 60: if (wave == 2) return new OrderEffect { Kind = OrderKind.Infusion, Amount = 3 };           // Chronicler V: Infusion (leader +N, this hero -N)
                         if (wave == 0) return new OrderEffect { Kind = OrderKind.HealLeader, Amount = 2 }; break;  // Chronicler R: Cure Leader 2
                case 88: if (wave == 2 || wave == 1) return new OrderEffect { Kind = OrderKind.Polymorph };         // Biomancer V/F: Polymorph
                         if (wave == 0) return new OrderEffect { Kind = OrderKind.SummonRandom, Amount = 1 }; break; // Biomancer R: Summon
                case 106:if (wave == 0) return new OrderEffect { Kind = OrderKind.DamageSpread, Amount = 7 };        // Statistician R: Meteor Storm 7 (X divided randomly)
                         if (wave == 2) return new OrderEffect { Kind = OrderKind.HealLeader, Amount = 3 }; break;  // Statistician V: Infuse Leader 3
                case 114:if (wave == 1) return new OrderEffect { Kind = OrderKind.Copycat };                        // Ghost(114) F: Copycat
                         if (wave == 2) return new OrderEffect { Kind = OrderKind.DamageLeader, Amount = 3 };        // Ghost(114) V: Backstab 3
                         if (wave == 0) return new OrderEffect { Kind = OrderKind.DrawCards, Amount = 2 }; break;    // Ghost(114) R: Draw 2
                case 33: if (wave == 0) return new OrderEffect { Kind = OrderKind.Transfusion }; break;              // Homunculus R: Transfusion (heal all dmg from an ally, caster takes it)
                case 65: if (wave == 2) return new OrderEffect { Kind = OrderKind.MindControl };                     // Puppeteer V: Mind Control (swap + steal an enemy hero)
                         if (wave == 1 || wave == 0) return new OrderEffect { Kind = OrderKind.Inspire }; break;     // Puppeteer F/R: Inspire
                case 110:if (wave == 2) return new OrderEffect { Kind = OrderKind.DamageRandom, Amount = 6 };         // Occultist V: Bombard 6
                         if (wave == 1) return new OrderEffect { Kind = OrderKind.DamageLeader, Amount = 3 };        // Occultist F: Backstab 3
                         if (wave == 0) return new OrderEffect { Kind = OrderKind.Phantom, Amount = 1 }; break;      // Occultist R: Phantom
                case 95: if (wave == 2) return new OrderEffect { Kind = OrderKind.DrawCards, Amount = 2 };           // Bannerman V: Draw 2
                         if (wave == 1) return new OrderEffect { Kind = OrderKind.Restructure };                     // Bannerman F: Restructure
                         if (wave == 0) return new OrderEffect { Kind = OrderKind.DrawCards, Amount = 1, Free = true }; break; // Bannerman R: Quick Draw 1
                case 40: if (wave == 0) return new OrderEffect { Kind = OrderKind.HealAll,     Amount = 2 }; break;   // Paladin R: Cure All 2
                case 49: if (wave == 0) return new OrderEffect { Kind = OrderKind.DamageLeader, Amount = 2, Kind2 = OrderKind.HealLeader, Amount2 = 2 }; break; // Vampire R: Backstab 2 (enemy leader) + Cure Leader 2 (own leader)
                case 55: if (wave == 0) return new OrderEffect { Kind = OrderKind.Banish,      Amount = 1 }; break;   // Dark Elemental R: Banish 1
                case 59: if (wave == 2) return new OrderEffect { Kind = OrderKind.DamageLeader, Amount = 3 };         // Ghost(59) V: Backstab 3
                         if (wave == 1) return new OrderEffect { Kind = OrderKind.DamageAll,   Amount = 2 };          // Ghost(59) F: Poison 2
                         if (wave == 0) return new OrderEffect { Kind = OrderKind.DrawCards,   Amount = 2 }; break;   // Ghost(59) R: Draw 2
                case 63: if (wave == 2) return new OrderEffect { Kind = OrderKind.HealSingle,  Amount = 4 }; break;   // Divinity V: Cure 4
                case 67: if (wave == 1) return new OrderEffect { Kind = OrderKind.Unsummon };                         // Sage F: Unsummon
                         if (wave == 0) return new OrderEffect { Kind = OrderKind.DamageRow,   Amount = 3 }; break;   // Sage R: Fire 3
                case 89: if (wave == 1) return new OrderEffect { Kind = OrderKind.DamageColumn, Amount = 2 }; break;  // Legionnaire F: Thunder 2
                case 90: if (wave == 1) return new OrderEffect { Kind = OrderKind.HealSingle,  Amount = 2, Free = true };   // Nurse F: Quick Cure 2
                         if (wave == 0) return new OrderEffect { Kind = OrderKind.HealSingle,  Amount = 4, Free = true }; break; // Nurse R: Quick Cure 4
                case 98: if (wave == 0) return new OrderEffect { Kind = OrderKind.HealSingle,  Amount = 4 }; break;   // White Knight R: Cure 4
                case 109:if (wave == 0) return new OrderEffect { Kind = OrderKind.Revive }; break;                    // Druid R: Revive
                case 113:if (wave == 0) return new OrderEffect { Kind = OrderKind.Banish,      Amount = 1 }; break;   // Shadow Elemental R: Banish 1
                case 115:if (wave == 1) return new OrderEffect { Kind = OrderKind.DamageAll,   Amount = 3 };          // Plague Doctor F: Poison 3
                         if (wave == 0) return new OrderEffect { Kind = OrderKind.HealSingle,  Amount = 3 }; break;   // Plague Doctor R: Cure 3
            }
            return new OrderEffect { Kind = OrderKind.None };
        }

        // Defeat an enemy hero outright (Assassinate / Hero Killer effects). Applies lethal damage so
        // the unit renders dead during the turn and is finalized as a corpse at wave end by
        // ProcessCasualties. Leaders are not heroes and cannot be targeted.
        // Immortality (Curse Knight ongoing): while ImmortalWaves>0, a VANGUARD (grid_x==2) hero on that
        // board cannot die by any means. True if the unit at wave gx on ps is currently protected.
        static bool IsImmortalVanguard(PlayerState ps, int gx) { return ps != null && ps.ImmortalWaves > 0 && gx == 2; }

        static void KillEnemyAt(PlayerState opPs, int gx, int gy)
        {
            if (gx < 0 || gx > 2 || gy < 0 || gy > 2) return;
            if (gx == 1 && gy == 1) { Log("    kill -> leader is not a hero, ignored"); return; }
            BUnit u;
            if (opPs.Units.TryGetValue(Key(gx, gy), out u) && u != null && !u.IsCorpse)
            {
                if (IsImmortalVanguard(opPs, gx)) { Log("    kill -> (" + gx + "," + gy + ") is an Immortal vanguard; cannot be defeated"); return; }
                if ((u.Abilities & UnitAbility.Deathproof) != 0) { Log("    kill -> (" + gx + "," + gy + ") is Deathproof; immune to instant-kill effects"); return; }
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

        // Open the interactive Scry UI: pull the top N cards off `ps`'s deck, store them as PendingScry,
        // and send op8 (scry=true) per card so the client shows them for a pick (resolved by op51).
        static void OpenScry(BattleSlot mine, PlayerState ps, int n)
        {
            if (mine == null || ps == null || ps.Deck == null || ps.Deck.Count == 0) { Log("  scry: deck empty"); return; }
            var scryCards = new List<ushort>();
            for (int i = 0; i < Math.Max(1, n) && ps.Deck.Count > 0; i++) { scryCards.Add(ps.Deck[0]); ps.Deck.RemoveAt(0); }
            ps.PendingScry = scryCards;
            byte deckLeft = (byte)Math.Min(255, ps.Deck.Count);
            foreach (ushort sc in scryCards)
                Send(mine.Me.Ns, new PacketWriter()
                    .WriteBool(true).WriteU8(deckLeft).WriteU16(sc).WriteBool(false)
                    .WriteU8(0).WriteBool(false).WriteU8(0).WriteU8(0).Frame(Op.DrawCard));
            Log("  SCRY " + scryCards.Count + ": opened scry UI (awaiting op51 pick)");
        }

        // Revive every corpse on `ps` (owner's board) to full life (Resurrect All). Fresh, summon-sick.
        static void ReviveAllCorpses(BattleSlot owner, BattleSlot opp, Battle b, PlayerState ps)
        {
            var corpses = new List<int>();
            foreach (var kv in ps.Units) if (kv.Value != null && kv.Value.IsCorpse) corpses.Add(kv.Key);
            foreach (int ck in corpses)
            {
                ushort card = ps.Units[ck].Card;
                int cx = ck / 10, cy = ck % 10;
                ps.Units.Remove(ck);
                SendClearCorpse(owner, opp, cx, cy);
                PlaceUnit(owner, opp, b, card, cx, cy, 0, true); // full HP
            }
            if (corpses.Count > 0) Log("  RESURRECT ALL: revived " + corpses.Count + " corpse(s) for " + owner.Me.User);
        }

        // Add a card to a player's discard pile and sync the client's (hidden) discard pile.
        // Sources: an order played from hand, a cleared corpse, a Banished/Wild-Summoned card.
        // The client tracks obj_deck.discard[player] via op30 (own) / op31 (opponent view); there is no
        // viewer UI yet, but we keep it accurate for when one is added. Card is sent as a u8.
        static void DiscardCard(BattleSlot ownerSlot, BattleSlot oppSlot, ushort card)
        {
            if (ownerSlot == null || ownerSlot.Battle == null || card == 0) return;
            ownerSlot.Battle.P[ownerSlot.P].Discard.Add(card);
            Send(ownerSlot.Me.Ns, new PacketWriter().WriteU8((byte)card).Frame(Op.DiscardCard));
            if (oppSlot != null) Send(oppSlot.Me.Ns, new PacketWriter().WriteU8((byte)card).Frame(Op.DiscardCardGet));
        }

        // Discard a RANDOM card from the owner's own hand (a cost paid by some leader actives:
        // Rayne, Malandrax). Mirrors the Banish hand-removal packets but for the owner's own hand.
        static void DiscardRandomFromHand(BattleSlot owner, BattleSlot opp, Battle b)
        {
            if (owner == null || owner.Hand == null || owner.Hand.Count == 0) { Log("  discard cost: hand empty"); return; }
            int idx; lock (_rng) idx = _rng.Next(owner.Hand.Count);
            ushort card = owner.Hand[idx]; owner.Hand.RemoveAt(idx);
            DiscardCard(owner, opp, card);
            Send(owner.Me.Ns, new PacketWriter().WriteU8((byte)idx).WriteU16(card).WriteBool(true).Frame(Op.HandCardRemove));
            if (opp != null) Send(opp.Me.Ns, new PacketWriter().WriteU8((byte)Math.Min(255, owner.Hand.Count)).WriteU8((byte)idx).WriteU16(card).Frame(Op.HandCardRemoveGet));
            Log("  DISCARD (cost): " + owner.Me.User + " discarded card " + card + " from hand[" + idx + "]");
        }

        // Find an empty (non-leader) slot on ps's board, preferring `preferWave`. Returns false if full.
        static bool FindEmptySlot(PlayerState ps, int preferWave, out int gx, out int gy)
        {
            int[] waves = { preferWave, 2, 1, 0 };
            var tried = new HashSet<int>();
            foreach (int w in waves)
            {
                if (w < 0 || w > 2 || !tried.Add(w)) continue;
                for (int lane = 0; lane < 3; lane++)
                {
                    if (w == 1 && lane == 1) continue;               // leader cell
                    if (!ps.Units.ContainsKey(Key(w, lane))) { gx = w; gy = lane; return true; }
                }
            }
            gx = gy = -1; return false;
        }

        // Create a hero on `owner`'s board and play the summon animation on both clients (the same op5/
        // op6 flow HandleSummon uses). Used by Summon / Wild Summon / Raise Dead / Revive effects.
        static void PlaceUnit(BattleSlot owner, BattleSlot opp, Battle b, ushort card, int gx, int gy, int damage, bool summonSick)
        {
            PlayerState ps = b.P[owner.P];
            var unit = new BUnit
            {
                Card = card,
                Atk = AtkOf(card) + GetUnitStrength(card, gx),
                Max = LifeOf(card),
                Damage = Math.Max(0, Math.Min(LifeOf(card), damage)),
                Armor = GetUnitArmor(card, gx),
                Strength = GetUnitStrength(card, gx),
                Finisher = GetUnitFinisher(card, gx),
                Revenge = GetUnitRevenge(card, gx),
                Abilities = GetUnitAbilities(card, gx),
                Cover = PassiveOf(card, gx).Cover,
                IsCorpse = false,
                RecruitedThisWave = summonSick,
                HasAttackedThisWave = false,
            };
            ps.Units[Key(gx, gy)] = unit;
            RecomputeAuras(ps);
            Send(owner.Me.Ns, new PacketWriter().WriteU16(card).WriteU8((byte)gx).WriteU8((byte)gy).WriteBool(false).Frame(Op.SummonUnit));
            if (opp != null)
            {
                SendUnitUpdateToBoth(owner, opp, gx, gy, unit, b);
                Send(opp.Me.Ns, new PacketWriter().WriteU16(card).WriteU8((byte)gx).WriteU8((byte)gy).WriteBool(false).Frame(Op.SummonUnitGet));
            }
            Log("    place unit card " + card + " -> (" + gx + "," + gy + ") dmg=" + damage);
        }

        // Apply order damage to an enemy unit (or the enemy leader at 1,1). Unit death is resolved
        // at wave end by ProcessCasualties, exactly like attack damage.
        // Order/spell/effect damage (NOT an attack). Armor and Cover apply ONLY to attacks, so they are
        // NOT used here. Deathproof units are IMMUNE to this (only melee/ranged attacks hurt them).
        static void DamageEnemyAt(PlayerState opPs, int gx, int gy, int dmg)
        {
            if (gx < 0 || gx > 2 || gy < 0 || gy > 2) return;
            if (gx == 1 && gy == 1)
            {
                if (opPs.LeaderShield) { opPs.LeaderShield = false; Log("    LEADER SHIELD absorbed the effect"); return; }
                opPs.LeaderLife -= dmg; Log("    damage -> enemy LEADER -" + dmg + " (life " + opPs.LeaderLife + ")"); return;
            }
            BUnit u;
            if (opPs.Units.TryGetValue(Key(gx, gy), out u) && u != null && !u.IsCorpse)
            {
                if (u.Shield) { u.Shield = false; Log("    SHIELD absorbed the effect at (" + gx + "," + gy + ")"); return; }
                // Deathproof (official) = immune to INSTANT-KILL effects only (blocked in KillEnemyAt).
                // Ordinary effect DAMAGE applies normally and can kill via accumulation — no special case.
                int actual = dmg; // no armor on effect damage
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

            // Reserve row (grid_x==3): the client plays Traps (o_type 2) AND Operations/Ongoing (o_type 1,
            // e.g. Immortality) here, not as a normal summon. Arm the reserve card.
            if (gx == 3) { HandleReservePlace(mine, theirs, b, ps, handIndex, gy); return; }

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
                Finisher = GetUnitFinisher(card, gx),
                Revenge = GetUnitRevenge(card, gx),
                Abilities = GetUnitAbilities(card, gx),
                Cover = PassiveOf(card, gx).Cover,
                IsCorpse = false,
                RecruitedThisWave = true,
                HasAttackedThisWave = false,
            };
            // Phantom card: its printed life becomes 1.
            if (ps.PhantomCards.Remove(card)) { unit.Max = 1; unit.Damage = 0; Log("  (phantom summon: printed life -> 1)"); }
            ps.Units[key] = unit;
            ps.LastSummonedCard = card; // for the Summon spell (copy the last recruited hero)
            RecomputeAuras(ps); // the new unit may grant/receive auras -> refresh effective stats

            // Remove card from hand
            mine.Hand.RemoveAt(handIndex);
            Send(mine.Me.Ns, new PacketWriter().WriteU8(handIndex).WriteU16(card).WriteBool(true).Frame(Op.HandCardRemove));
            // Tell the OPPONENT the summoner's hand shrank (op13), else their view of it only ever grows
            // (on draws) and never decrements on summon -> the displayed count drifts too high.
            if (theirs != null)
                Send(theirs.Me.Ns, new PacketWriter().WriteU8((byte)Math.Min(255, mine.Hand.Count)).WriteU8(handIndex).WriteU16(card).Frame(Op.HandCardRemoveGet));

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

            // Consume action — unless the leader is Lesandra (recruiting is a free action).
            if (ps.LeaderCard / 2 == 11) { Log("  LEADER (Lesandra): recruit was free"); GrantAction(mine); }
            else ConsumeAction(mine, theirs, b);
        }

        // ---- trap placement (op 10 into the reserve row grid_x==3) ------------
        // A Trap card (card_init o_type==2) is dragged onto a reserve slot; the client sends it as a
        // summon to (3, lane). We arm the trap (a hidden pending flag), render it face-down (op5/op6
        // istrap=true), and remember its reserve lane so the face-down card can be cleared when it
        // triggers. Costs 1 orb + 1 action (the client's summon_ui_script gates on orbs>=cost too).
        static void HandleReservePlace(BattleSlot mine, BattleSlot theirs, Battle b, PlayerState ps, int handIndex, int lane)
        {
            if (lane < 0 || lane > 2) { Log("RESERVE rejected: bad lane " + lane); GrantAction(mine); return; }
            ushort card = mine.Hand[handIndex];
            OrderEffect eff = OrderOf(card);
            bool isTrap = eff.Kind == OrderKind.TrapCancelAttack || eff.Kind == OrderKind.TrapCancelSpell || eff.Kind == OrderKind.TrapCancelOrder;
            bool isImmortality = eff.Kind == OrderKind.Immortality; // an Operation (o_type 1), played face-up to reserve
            OngoingOp ongoing; bool isOngoing = TryOngoingOp(card, out ongoing); // persistent region-aura operation
            if (!isTrap && !isImmortality && !isOngoing)
            { Log("RESERVE rejected: card " + card + " is not a trap or operation"); GrantAction(mine); return; }

            // One reserve card per lane.
            bool ongoingLaneTaken = false; foreach (var o in ps.Ongoing) if (o.Lane == lane) { ongoingLaneTaken = true; break; }
            if (ps.TrapAttackLane == lane || ps.TrapSpellLane == lane || ps.TrapOrderLane == lane || ps.ImmortalLane == lane || ongoingLaneTaken)
            { Log("RESERVE rejected: lane " + lane + " already occupied"); GrantAction(mine); return; }

            // Orb cost (same as the client gate).
            int cost = OrbCostOf(card);
            if (ps.Orbs < cost) { Log("RESERVE rejected: not enough orbs (" + ps.Orbs + "/" + cost + ")"); GrantAction(mine); return; }
            ps.Orbs -= cost;
            { BattleSlot q0 = mine.P == 0 ? mine : theirs, q1 = mine.P == 0 ? theirs : mine; if (q0 != null && q1 != null) SendOrbs(q0, q1, b); }

            // Arm the effect + remember its reserve lane. Traps are hidden (face-down); operations (the
            // Immortality "Ongoing" and the region auras) are visible (face-up).
            bool faceDown;
            if (isTrap)
            {
                switch (eff.Kind)
                {
                    case OrderKind.TrapCancelAttack: ps.TrapCancelAttack = true; ps.TrapAttackLane = lane; break;
                    case OrderKind.TrapCancelSpell:  ps.TrapCancelSpell  = true; ps.TrapSpellLane  = lane; break;
                    case OrderKind.TrapCancelOrder:  ps.TrapCancelOrder  = true; ps.TrapOrderLane  = lane; break;
                }
                faceDown = true;
            }
            else if (isImmortality)
            {
                ps.ImmortalWaves = 3; ps.ImmortalLane = lane; faceDown = false;
                Log("  IMMORTALITY: " + mine.Me.User + "'s vanguard heroes can't die for 3 waves");
            }
            else // ongoing region-aura operation (persistent)
            {
                ongoing.Lane = lane; ps.Ongoing.Add(ongoing); faceDown = false;
                Log("  ONGOING: " + mine.Me.User + " placed " + ongoing.Target + " aura (str+" + ongoing.Strength
                    + " armor+" + ongoing.Armor + " regen+" + ongoing.Regen + " grant=" + ongoing.Grant + ") card " + card);
            }

            // Discard the played card from hand (op12 to owner, op13 count to opponent).
            mine.Hand.RemoveAt(handIndex);
            Send(mine.Me.Ns, new PacketWriter().WriteU8((byte)handIndex).WriteU16(card).WriteBool(true).Frame(Op.HandCardRemove));
            if (theirs != null)
                Send(theirs.Me.Ns, new PacketWriter().WriteU8((byte)Math.Min(255, mine.Hand.Count)).WriteU8((byte)handIndex).WriteU16(card).Frame(Op.HandCardRemoveGet));

            // Render the card in the reserve slot (grid_x=3). istrap=true => face-down (trap); false =>
            // face-up (operation, drawn as the card's spell sprite). script_summon sets obj_unit.trap.
            Send(mine.Me.Ns, new PacketWriter().WriteU16(card).WriteU8(3).WriteU8((byte)lane).WriteBool(faceDown).Frame(Op.SummonUnit));
            if (theirs != null)
                Send(theirs.Me.Ns, new PacketWriter().WriteU16(card).WriteU8(3).WriteU8((byte)lane).WriteBool(faceDown).Frame(Op.SummonUnitGet));

            Log((isTrap ? "TRAP PLACED: " : "OPERATION PLACED: ") + mine.Me.User + " -> " + (isTrap ? eff.Kind.ToString() : "operation") + " in reserve lane " + lane + " (card " + card + ")");
            ConsumeAction(mine, theirs, b);
            // Operations take effect immediately — refresh so units show the new stats/abilities/protection.
            if (isImmortality || isOngoing)
            { BattleSlot q0 = mine.P == 0 ? mine : theirs, q1 = mine.P == 0 ? theirs : mine; if (q0 != null && q1 != null) SyncUnitStates(q0, q1, b); }
        }

        // Clear the face-down card for a triggered trap from the OWNER's reserve slot on both clients.
        static void ClearTrapVisual(BattleSlot owner, BattleSlot opp, int lane)
        {
            if (owner == null || lane < 0 || lane > 2) return;
            SendDestroyUnit(owner, opp, 3, lane);
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
                // Board validation. selectGrid==1 means the player clicked their OWN board, 0 the enemy's.
                // The client's spell targeting will highlight a unit on EITHER board (e.g. spell_unsummon
                // marks any non-Intercept living unit under the cursor), so an enemy-only spell can be
                // mis-aimed at a friendly cell — which the server would otherwise resolve against the
                // MIRRORED enemy cell (wrong unit). Enforce the correct board here (reject + refund).
                bool spellSelf = (seff.Kind == OrderKind.HealSingle || seff.Kind == OrderKind.Revive
                                  || seff.Kind == OrderKind.Resurrect || seff.Kind == OrderKind.Retreat
                                  || seff.Kind == OrderKind.ShieldHero || seff.Kind == OrderKind.StrengthBuff
                                  || seff.Kind == OrderKind.Decoy || seff.Kind == OrderKind.Swap
                                  || seff.Kind == OrderKind.Seance || seff.Kind == OrderKind.Transfusion
                                  || seff.Kind == OrderKind.ForceCube || seff.Kind == OrderKind.GrantRanged
                                  || seff.Kind == OrderKind.ShieldRear);
                if (IsEnemyTargeting(seff.Kind) && selectGrid)
                {
                    Log("  -> SPELL rejected: " + seff.Kind + " must target an ENEMY hero (own board clicked)");
                    GrantAction(mine);
                    BattleSlot q0 = mine.P == 0 ? mine : theirs, q1 = mine.P == 0 ? theirs : mine;
                    if (q0 != null && q1 != null) SyncUnitStates(q0, q1, b);
                    return;
                }
                if (spellSelf && !selectGrid)
                {
                    Log("  -> SPELL rejected: " + seff.Kind + " must target your OWN side (enemy board clicked)");
                    GrantAction(mine);
                    BattleSlot q0 = mine.P == 0 ? mine : theirs, q1 = mine.P == 0 ? theirs : mine;
                    if (q0 != null && q1 != null) SyncUnitStates(q0, q1, b);
                    return;
                }
                // TRAP — Cancel Spell: the rival's armed trap (round >= 2) negates this wave-spell; the
                // caster still spends its action and the trap owner (Statistician) draws 1.
                {
                    PlayerState defTrap = b.P[1 - mine.P];
                    if (b.Round >= 2 && defTrap.TrapCancelSpell)
                    {
                        defTrap.TrapCancelSpell = false;
                        ClearTrapVisual(theirs, mine, defTrap.TrapSpellLane); defTrap.TrapSpellLane = -1;
                        Log("  -> TRAP: " + (theirs != null ? theirs.Me.User : "defender") + "'s Cancel Spell negates " + mine.Me.User + "'s spell; owner draws 1");
                        if (theirs != null) DrawCardsFor(theirs, mine, b, 1);
                        ConsumeAction(mine, theirs, b);
                        BattleSlot t0 = mine.P == 0 ? mine : theirs, t1 = mine.P == 0 ? theirs : mine;
                        if (t0 != null && t1 != null) SyncUnitStates(t0, t1, b);
                        return;
                    }
                }
                ResolveEffect(seff, mine, theirs, b, tx, ty, ax, ay, selectGrid); // caster cell (Swap/Takedown) + clicked board (either-board effects)
                // Seth: any wave-spell also triggers Scry 3 (opens the scry UI — unless one is already open).
                if (!b.Over && ps.LeaderCard / 2 == 15 && ps.PendingScry == null) { Log("  LEADER (Seth): any wave-spell -> Scry 3"); OpenScry(mine, ps, 3); }
                // Gen-2 leader-active RIDERS: extra cost/bonus attached to a leader's own cast (ax,ay = 1,1).
                if (ax == 1 && ay == 1)
                {
                    switch (ps.LeaderCard / 2)
                    {
                        case 70: // Merjoram: "Give a HERO shield, then lose 2 life."
                            ps.LeaderLife = Math.Max(0, ps.LeaderLife - 2); SendLeaderHp(mine, theirs, ps);
                            Log("  LEADER (Merjoram): -2 leader life (now " + ps.LeaderLife + ")");
                            break;
                        case 99: // Baenvier: "cast Silence ... and give this Leader Strength 1."
                            ps.LeaderPermStr += 1;
                            Log("  LEADER (Baenvier): +1 permanent leader Strength (now +" + ps.LeaderPermStr + ")");
                            break;
                        case 74: // Rayne: "discard a random card to defeat a Hero with damage on it."
                        case 111:// Malandrax: "Discard a random card and gain an orb."
                            DiscardRandomFromHand(mine, theirs, b);
                            break;
                    }
                }
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

            // Validate attacker was not recruited this wave (Swift units ignore summon-sickness).
            if (!attackerIsLeader && attacker.RecruitedThisWave && (attacker.Abilities & UnitAbility.Swift) == 0)
            { Log("ATTACK rejected: attacker was recruited this wave"); GrantAction(mine); return; }

            // Free Attack (Quick Attack): this hero's basic attacks do NOT cost an action. The unit still
            // exhausts (HasAttackedThisWave), so it attacks only once/wave -- this just refunds the action.
            bool freeAtk = !attackerIsLeader && (attacker.Abilities & UnitAbility.FreeAttack) != 0;
            if (freeAtk) Log("  FREE ATTACK: (" + ax + "," + ay + ") attacks without spending an action");

            // Uriah: "This LEADER cannot attack." Reject a leader-initiated attack for Uriah's owner.
            if (attackerIsLeader && ps.LeaderCard / 2 == 76)
            { Log("ATTACK rejected: Uriah leader cannot attack"); GrantAction(mine); return; }

            // TRAP — Cancel Attack: if the defender armed one (round >= 2), negate this attack before any
            // targeting resolves. The attacker still spends its action. Reflector's trap also BACKLASHES
            // the attacker: it takes damage equal to its own attack. This can defeat the attacker (or the
            // attacking leader -> loss).
            {
                PlayerState defTrap = b.P[1 - mine.P];
                if (b.Round >= 2 && defTrap.TrapCancelAttack)
                {
                    defTrap.TrapCancelAttack = false;
                    int backlash = Math.Max(1, attacker.Atk);
                    Log("  -> TRAP: " + (theirs != null ? theirs.Me.User : "defender") + "'s Cancel Attack negates " + mine.Me.User + "'s attack; Backlash " + backlash);
                    // Play the attack so the block is VISIBLE: the attacker lunges at its target, which
                    // takes 0 damage (its life is unchanged). Without this the attacker just stands still
                    // and the trap silently vanishes — which reads as "nothing happened". Uses the raw
                    // clicked target cell (before any intercept/cover redirect, which we skip anyway).
                    bool animRanged = !attackerIsLeader && (attacker.Abilities & UnitAbility.RangedAttack) != 0;
                    BUnit animTgt; b.P[1 - mine.P].Units.TryGetValue(Key(tx, serverTy), out animTgt);
                    if ((tx == 1 && serverTy == 1) || animTgt == null || animTgt.IsCorpse)
                        SendAttackAndLeaderUpdate(mine, theirs, ax, ay, 1, 1, 0, attackerIsLeader, b.P[1 - mine.P], animRanged, false, b);
                    else
                        SendAttackAndUpdate(mine, theirs, ax, ay, tx, serverTy, 0, attackerIsLeader, animTgt, animRanged, false, b);
                    // Then remove the face-down trap card and Backlash the attacker.
                    ClearTrapVisual(theirs, mine, defTrap.TrapAttackLane); defTrap.TrapAttackLane = -1;
                    bool selfLeaderDied = false;
                    if (attackerIsLeader)
                    {
                        ps.LeaderLife -= backlash;
                        selfLeaderDied = ps.LeaderLife <= 0;
                        SendLeaderHp(mine, theirs, ps);
                    }
                    else
                    {
                        attacker.Damage += backlash;
                        attacker.HasAttackedThisWave = true; // used its action attacking into the trap
                        SendUnitUpdateToBoth(mine, theirs, ax, ay, attacker, b);
                        ProcessImmediateDeaths(ps, mine, theirs, b);
                    }
                    { BattleSlot t0 = mine.P == 0 ? mine : theirs, t1 = mine.P == 0 ? theirs : mine; if (t0 != null && t1 != null) SyncUnitStates(t0, t1, b); }
                    if (selfLeaderDied)
                    {
                        b.Over = true;
                        Log("BATTLE END: " + (theirs != null ? theirs.Me.User : "defender") + " wins (attacking leader Backlashed to death by trap)");
                        if (theirs != null) Send(theirs.Me.Ns, new PacketWriter().WriteBool(true).WriteU16(0).Frame(Op.BattleEnd));
                        Send(mine.Me.Ns, new PacketWriter().WriteBool(false).WriteU16(0).Frame(Op.BattleEnd));
                        return;
                    }
                    if (freeAtk) GrantAction(mine); else ConsumeAction(mine, theirs, b);
                    return;
                }
            }

            // Arec: the defender's leader makes ALL incoming attacks target a RANDOM enemy hero. Redirect
            // to a random frontmost-alive enemy (keeps it a legal melee/ranged target); leave the leader
            // as the target only if the board is empty.
            {
                PlayerState arecPs = b.P[1 - mine.P];
                if (arecPs.LeaderCard / 2 == 9)
                {
                    var fronts = new List<int>();
                    for (int lane = 0; lane < 3; lane++)
                        for (int w = 2; w >= 0; w--) { BUnit fu; if (arecPs.Units.TryGetValue(Key(w, lane), out fu) && fu != null && !fu.IsCorpse) { fronts.Add(Key(w, lane)); break; } }
                    if (fronts.Count > 0)
                    {
                        int rk; lock (_rng) rk = fronts[_rng.Next(fronts.Count)];
                        tx = (byte)(rk / 10); serverTy = (byte)(rk % 10);
                        Log("  LEADER (Arec): incoming attack retargeted at random -> (" + tx + "," + serverTy + ")");
                    }
                }
            }

            // Determine attack type: ranged if unit has RangedAttack ability
            // Ranged is decided by the same table the client's atktype (update_buff) uses, so the
            // two never disagree. Leaders melee. ax is the attacker's wave (grid_x).
            // Ranged if the attacker has the RangedAttack ability (own OR aura-granted; auras were
            // recomputed above), not just the card's own wave table.
            bool isRanged = !attackerIsLeader && (attacker.Abilities & UnitAbility.RangedAttack) != 0;

            // A MELEE unit can only attack if no ally is in front of it in its column (client rule
            // return_noone_infront; an ally at a higher wave in the same lane blocks it). The client
            // enforces this in its UI, but the bot sends attacks directly, so enforce it server-side.
            // (IsFrontmostAliveInColumn treats incorporeal/dead as non-blocking, matching the client.)
            if (!attackerIsLeader && !isRanged && !IsFrontmostAliveInColumn(ax, ay, ps))
            { Log("ATTACK rejected: melee attacker (" + ax + "," + ay + ") blocked by an ally in front"); GrantAction(mine); return; }

            // Find target (using server Y coordinate)
            PlayerState opPs = b.P[1 - mine.P];
            int targetKey = Key(tx, serverTy);

            // Check if target is the opponent's leader
            bool targetIsLeader = (tx == 1 && serverTy == 1);

            // Decoy: if the defender has a reachable decoy hero, the attack MUST land on it.
            int decoyKey = ForcedDecoyCell(opPs);
            if (decoyKey >= 0 && targetKey != decoyKey)
            { Log("ATTACK rejected: must target the decoy at (" + (decoyKey / 10) + "," + (decoyKey % 10) + ")"); GrantAction(mine); return; }

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

            // --- Ranged validation: R.Immunity + Intercept ---
            if (isRanged)
            {
                // R.Immunity: the chosen target cannot be hit by ranged at all. Reject (and refund).
                if (targetIsLeader)
                {
                    if ((opPs.LeaderAbilities & UnitAbility.RImmunity) != 0)
                    { Log("ATTACK rejected: leader has Ranged Immunity"); GrantAction(mine); return; }
                }
                else
                {
                    BUnit tImm;
                    if (opPs.Units.TryGetValue(targetKey, out tImm) && tImm != null && !tImm.IsCorpse
                        && (tImm.Abilities & UnitAbility.RImmunity) != 0)
                    { Log("ATTACK rejected: target has Ranged Immunity"); GrantAction(mine); return; }
                }
            }
            if (isRanged && !targetIsLeader)
            {
                // Intercept redirects a ranged shot to the frontmost interceptor standing IN FRONT of the target.
                int interceptWave = FindInterceptInColumn(serverTy, opPs, b.Wave);
                if (interceptWave > tx)
                {
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
            // Revenge N: this hero attacks for +N while IT is itself damaged. It's a flat attack bonus
            // (independent of the target), so fold it into rawDmg here — this covers unit AND leader hits.
            if (!attackerIsLeader && attacker.Revenge > 0 && attacker.Damage > 0)
            {
                rawDmg += attacker.Revenge;
                Log("  -> REVENGE: +" + attacker.Revenge + " (attacker is damaged), attack now " + rawDmg);
            }
            bool targetDied = false;
            bool leaderDied = false;

            if (targetIsLeader)
            {
                // Damage goes to opponent's leader (reduced by "Leader: Armor N" auras).
                int ldmg = Math.Max(1, rawDmg - opPs.LeaderArmorBonus);
                if (opPs.LeaderShield) { opPs.LeaderShield = false; ldmg = 0; Log("  -> LEADER SHIELD absorbed the hit"); }
                if (opPs.LeaderCard / 2 == 76 && ldmg > 4) { ldmg = 4; Log("  -> URIAH: single-attack damage capped at 4"); }
                opPs.LeaderLife -= ldmg;
                leaderDied = opPs.LeaderLife <= 0;
                Log("  -> LEADER takes " + ldmg + " (life " + opPs.LeaderLife + (leaderDied ? ", DEAD" : "") + ")");
                // Target the leader's real slot (1,1), NOT the empty front slot the client aimed at.
                // The client's attack animation looks up a unit at the target coords; sending the
                // empty slot makes that lookup undefined and crashes (and mis-addresses the leader's
                // HP update). The leader is always stored at (1,1) on both grids.
                SendAttackAndLeaderUpdate(mine, theirs, ax, ay, 1, 1, ldmg, attackerIsLeader, opPs, isRanged, false, b);
                ApplyVamp(mine, theirs, b, attacker, attackerIsLeader, ldmg, ax, ay);
                ApplyMercy(mine, theirs, b, attacker, attackerIsLeader, ldmg);
                ApplyLeaderReflect(mine, theirs, b, opPs, attacker, attackerIsLeader, ldmg, ax, ay);
                ApplyLeaderCounter(mine, theirs, b, opPs, attacker, attackerIsLeader, isRanged, ax, ay);
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
                    // Intercept Killer: double damage against a target that HAS Intercept.
                    if (!attackerIsLeader && (attacker.Abilities & UnitAbility.InterceptKiller) != 0
                        && (target.Abilities & UnitAbility.Intercept) != 0)
                    {
                        baseDmg *= 2;
                        Log("  -> INTERCEPT KILLER: double damage vs intercept target");
                    }
                    // Finisher N: +N attack damage when the target is ALREADY damaged (checked before this hit).
                    if (!attackerIsLeader && attacker.Finisher > 0 && target.Damage > 0)
                    {
                        baseDmg += attacker.Finisher;
                        Log("  -> FINISHER: +" + attacker.Finisher + " vs damaged hero (" + (baseDmg - attacker.Finisher) + " -> " + baseDmg + ")");
                    }
                    // Double Attack: a melee attack lands TWICE (two separate hits).
                    int hits = (!attackerIsLeader && !isRanged && (attacker.Abilities & UnitAbility.DoubleAttack) != 0) ? 2 : 1;
                    for (int h = 0; h < hits; h++)
                    {
                        int actualDmg = Math.Max(1, baseDmg - target.Armor);
                        // Shield negates one damage event entirely, then breaks.
                        if (target.Shield) { target.Shield = false; actualDmg = 0; Log("  -> SHIELD absorbed the hit"); }
                        target.Damage += actualDmg;
                        targetDied = target.Damage >= target.Max;
                        Log("  -> unit(" + tx + "," + serverTy + ") card " + target.Card + " takes " + actualDmg +
                            (hits > 1 ? " (hit " + (h + 1) + "/2)" : "") +
                            " (damage " + target.Damage + "/" + target.Max + (targetDied ? ", WILL DIE AT WAVE END" : "") + ")");
                        SendAttackAndUpdate(mine, theirs, ax, ay, tx, serverTy, actualDmg, attackerIsLeader, target, isRanged, false, b);
                        ApplyVamp(mine, theirs, b, attacker, attackerIsLeader, actualDmg, ax, ay);
                        ApplyMercy(mine, theirs, b, attacker, attackerIsLeader, actualDmg);
                        // Reflect Damage: the unit that took the hit reflects it back to the attacker.
                        if (!attackerIsLeader && (target.Abilities & UnitAbility.Reflect) != 0)
                        {
                            attacker.Damage += actualDmg;
                            Log("  -> REFLECT: " + actualDmg + " back to attacker (" + attacker.Damage + "/" + attacker.Max + ")");
                            SendUnitUpdateToBoth(mine, theirs, ax, ay, attacker, b);
                        }
                    }
                }
                else
                {
                    // No unit at target, damage goes to leader (reduced by "Leader: Armor N" auras).
                    int ldmg2 = Math.Max(1, rawDmg - opPs.LeaderArmorBonus);
                    if (opPs.LeaderShield) { opPs.LeaderShield = false; ldmg2 = 0; Log("  -> LEADER SHIELD absorbed the hit"); }
                    if (opPs.LeaderCard / 2 == 76 && ldmg2 > 4) { ldmg2 = 4; Log("  -> URIAH: single-attack damage capped at 4"); }
                    opPs.LeaderLife -= ldmg2;
                    leaderDied = opPs.LeaderLife <= 0;
                    Log("  -> LEADER (no unit at target) takes " + ldmg2 + " (life " + opPs.LeaderLife + (leaderDied ? ", DEAD" : "") + ")");
                    // Target the leader's real slot (1,1) — see note above; empty-slot coords crash the client.
                    SendAttackAndLeaderUpdate(mine, theirs, ax, ay, 1, 1, ldmg2, attackerIsLeader, opPs, isRanged, false, b);
                    ApplyVamp(mine, theirs, b, attacker, attackerIsLeader, ldmg2, ax, ay);
                    ApplyMercy(mine, theirs, b, attacker, attackerIsLeader, ldmg2);
                    ApplyLeaderReflect(mine, theirs, b, opPs, attacker, attackerIsLeader, ldmg2, ax, ay);
                    ApplyLeaderCounter(mine, theirs, b, opPs, attacker, attackerIsLeader, isRanged, ax, ay);
                }
            }

            // Andrus: the attacker's Leader casts a free Ice 2 (melee) / Fire 2 row (ranged) on the target.
            ApplyAndrus(mine, theirs, b, ps, opPs, attackerIsLeader, targetIsLeader, isRanged, tx, serverTy);

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

            // Turn any freshly-lethal units into corpses NOW (target, and the attacker if a counter
            // killed it) so they're clearable/revivable immediately and stay in sync.
            ProcessImmediateDeaths(opPs, theirs, mine, b);
            ProcessImmediateDeaths(ps, mine, theirs, b);

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

            // Swift (official) only lets a hero act the turn it's summoned (summon-sickness bypass handled
            // above) — the attack still costs an action, like any other. Free Attack (Quick Attack) is the
            // exception: the attack is refunded (no action spent); the unit still exhausts for the wave.
            if (freeAtk) GrantAction(mine); else ConsumeAction(mine, theirs, b);
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
            // An Incorporeal center Vanguard does not block the leader.
            if (opPs.Units.TryGetValue(Key(2, 1), out u) && u != null && !u.IsCorpse
                && (u.Abilities & UnitAbility.Incorporeal) == 0) return false; // center Vanguard blocks
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
                // Incorporeal units do not block melee — a unit behind one is still reachable.
                if (opPs.Units.TryGetValue(k, out u) && !u.IsCorpse && (u.Abilities & UnitAbility.Incorporeal) == 0)
                    return false;
            }
            return true;
        }

        // Decoy: if the defender has a living decoy hero that is a legal target (frontmost in its column,
        // so it's actually reachable), attacks MUST target it. Returns its cell key, or -1 if none forces.
        static int ForcedDecoyCell(PlayerState defender)
        {
            foreach (var kv in defender.Units)
            {
                BUnit u = kv.Value;
                if (u != null && !u.IsCorpse && u.Decoy && IsFrontmostAliveInColumn(kv.Key / 10, kv.Key % 10, defender))
                    return kv.Key;
            }
            return -1;
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
            // Immortality: a protected vanguard hero is never sent as dead (even at/over lethal damage),
            // so the client won't play its death animation; it shows at least 1 HP until protection ends.
            bool immVan = IsImmortalVanguard(b.P[ownerSlot.P], x);
            bool dead = !immVan && (unit.IsCorpse || unit.Damage >= unit.Max);
            if (dead) unit.DeathSent = true;  // client now knows it's dead; never re-send (would replay the death anim)
            int curLife = dead ? 0 : Math.Max(immVan ? 1 : 0, unit.Max - unit.Damage);
            // Ready = alive, hasn't acted, not summon-sick (see SyncPlayerUnits). Must use `dead` (which
            // includes lethal-but-not-yet-corpse) NOT just IsCorpse: on a killing blow IsCorpse isn't set
            // until ProcessImmediateDeaths runs a moment later, so sending active=1 with dead=1 makes the
            // client treat the corpse as a live, movable unit (move button, no clear).
            bool active = !dead && !unit.HasAttackedThisWave && !unit.RecruitedThisWave;
            if (PacketLog)
                Log("[SendUnitUpdateToBoth] -> (" + x + "," + y + ") active=" + (active ? 1 : 0)
                    + " wave=" + b.Wave + " ux=" + x + " isCorpse=" + unit.IsCorpse);

            // The client shows attack = atk + str (+ mstr for melee), so send the BASE atk here and the
            // Strength bonus in the str field — otherwise strength double-counts (unit.Atk already folds it in).
            // Include TempStrength (Strength Buff) so the displayed attack reflects the buff.
            byte baseAtk = (byte)Math.Max(0, AtkOf(unit.Card) - unit.Enervate); // Enervate lowers the shown base attack
            byte dispStr = (byte)Math.Max(0, Math.Min(255, unit.Strength + unit.TempStrength + (unit.Damage > 0 ? unit.Revenge : 0)));
            // UpdateUnit to owner — raw Y (unit stored at slot_get_id(x, y))
            Send(ownerSlot.Me.Ns, new PacketWriter()
                .WriteU8((byte)x).WriteU8((byte)y).WriteU8(baseAtk).WriteU16((ushort)curLife)
                .WriteU8(dispStr).WriteU8(0).WriteU8((byte)unit.Armor)
                .WriteBool(active).WriteBool(dead).WriteU8((byte)unit.Max)
                .Frame(Op.UpdateUnit));
            // UpdateUnitGet to opponent — raw Y (container_update_unit_get: yy = 2 - read)
            if (oppSlot != null)
                Send(oppSlot.Me.Ns, new PacketWriter()
                    .WriteU8((byte)x).WriteU8((byte)y).WriteU8(baseAtk).WriteU16((ushort)curLife)
                    .WriteU8(dispStr).WriteU8(0).WriteU8((byte)unit.Armor)
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
            unit.Finisher = GetUnitFinisher(unit.Card, x2);
            unit.Revenge = GetUnitRevenge(unit.Card, x2);
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
            // Diagnostic for the "supporter aura not applied on move" bug: after RecomputeAuras (inside
            // SyncUnitStates) the moved unit's effective abilities should include any aura granted by the
            // unit now in front of it (e.g. a Flank "Supporter: R.Attack" granting RangedAttack to the rear).
            {
                BUnit moved; ps.Units.TryGetValue(dstKey, out moved);
                if (moved != null)
                    Log("  MOVE result: (" + x2 + "," + y2 + ") card " + moved.Card
                        + " effective abilities=" + moved.Abilities
                        + " ranged=" + (((moved.Abilities & UnitAbility.RangedAttack) != 0) ? 1 : 0));
            }

            // Restructure makes moves free this turn.
            if (ps.RestructureFree) { Log("  RESTRUCTURE: move was free"); GrantAction(mine); }
            else ConsumeAction(mine, theirs, b);
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

            DiscardCard(mine, theirs, unit.Card); // a cleared corpse goes to the discard pile
            ps.Units.Remove(key);
            Log("CLEAR_CORPSE " + mine.Me.User + ": removed corpse at (" + cx + "," + cy + ")");

            // Notify both clients. Payload is x, y, AND a `goup` bool (container_clear_corpse reads
            // all three) — omitting it made the client read past the buffer end and abort the clear,
            // desyncing the boards. goup=false = the corpse fades in place (normal clear).
            Send(mine.Me.Ns, new PacketWriter().WriteU8(cx).WriteU8(cy).WriteBool(false).Frame(Op.ClearCorpse));
            if (theirs != null)
                // container_clear_corpse_get mirrors Y: yy = 2 - buffer_read
                Send(theirs.Me.Ns, new PacketWriter().WriteU8(cx).WriteU8(cy).WriteBool(false).Frame(Op.ClearCorpseGet));

            // Restructure makes corpse-clears free this turn.
            if (ps.RestructureFree) { Log("  RESTRUCTURE: clear-corpse was free"); GrantAction(mine); }
            else ConsumeAction(mine, theirs, b);
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

        // Vamp: whenever this hero deals attack damage, remove that much damage from ITSELF (self-heal).
        static void ApplyVamp(BattleSlot mine, BattleSlot theirs, Battle b, BUnit attacker, bool attackerIsLeader, int dealt, int ax, int ay)
        {
            if (attackerIsLeader || attacker == null || dealt <= 0) return;
            if ((attacker.Abilities & UnitAbility.Vamp) == 0 || attacker.Damage <= 0) return;
            attacker.Damage = Math.Max(0, attacker.Damage - dealt);
            Log("  -> VAMP: heal self -" + dealt + " (damage now " + attacker.Damage + ")");
            SendUnitUpdateToBoth(mine, theirs, ax, ay, attacker, b);
        }

        // Regen N: heal N damage from each of the player's Regen heroes (end of their turn).
        static void ApplyRegen(PlayerState ps)
        {
            foreach (var kv in ps.Units)
            {
                BUnit u = kv.Value; if (u == null || u.IsCorpse) continue;
                int regen = PassiveOf(u.Card, kv.Key / 10).Regen;
                // Ongoing "Unit/Rear: Regen N" operations add to this hero's regen (Sage: Unit: Regen 2).
                foreach (var op in ps.Ongoing) if (op.Regen > 0 && OngoingHits(op.Target, kv.Key, ps)) regen += op.Regen;
                if (regen > 0 && u.Damage > 0)
                {
                    u.Damage = Math.Max(0, u.Damage - regen);
                    Log("  REGEN: (" + (kv.Key / 10) + "," + (kv.Key % 10) + ") card " + u.Card + " heals " + regen);
                }
            }
        }

        // Does an ongoing op with region `t` cover the cell `key` on ps's board? (Source-independent for
        // Unit/Vanguard/Rear — the only regions ongoing operations use.)
        static bool OngoingHits(AuraTarget t, int key, PlayerState ps)
        {
            foreach (int rk in AuraRecipients(t, 0, 0, ps)) if (rk == key) return true;
            return false;
        }

        // Mercy: whenever this hero deals attack damage, remove `dealt` damage from a RANDOM damaged
        // hero on the attacker's side (its owner's units, itself included; leaders are not heroes).
        static void ApplyMercy(BattleSlot mine, BattleSlot theirs, Battle b, BUnit attacker, bool attackerIsLeader, int dealt)
        {
            if (attackerIsLeader || attacker == null || dealt <= 0) return;
            if ((attacker.Abilities & UnitAbility.Mercy) == 0) return;
            PlayerState ps = b.P[mine.P];
            var damaged = new List<int>();
            foreach (var kv in ps.Units) { BUnit u = kv.Value; if (u != null && !u.IsCorpse && u.Damage > 0) damaged.Add(kv.Key); }
            if (damaged.Count == 0) return;
            int key; lock (_rng) key = damaged[_rng.Next(damaged.Count)];
            BUnit tgt = ps.Units[key];
            int before = tgt.Damage;
            tgt.Damage = Math.Max(0, tgt.Damage - dealt);
            Log("  -> MERCY: heal random damaged hero (" + (key / 10) + "," + (key % 10) + ") by " + (before - tgt.Damage));
            SendUnitUpdateToBoth(mine, theirs, key / 10, key % 10, tgt, b);
        }

        // Leader: Reflect Damage (aura, e.g. Statistician Flank) — when the leader takes ATTACK damage,
        // deal that much back to the attacking unit. (Leader-vs-leader attackers are not reflected.)
        static void ApplyLeaderReflect(BattleSlot mine, BattleSlot theirs, Battle b, PlayerState opPs, BUnit attacker, bool attackerIsLeader, int ldmg, int ax, int ay)
        {
            if (attackerIsLeader || attacker == null || ldmg <= 0) return;
            if ((opPs.LeaderAbilities & UnitAbility.Reflect) == 0) return;
            attacker.Damage += ldmg;
            Log("  -> LEADER REFLECT: " + ldmg + " back to attacker (" + attacker.Damage + "/" + attacker.Max + ")");
            SendUnitUpdateToBoth(mine, theirs, ax, ay, attacker, b);
        }

        // Uriah: leader Counter — when the Uriah leader is hit in MELEE, it strikes the attacker back for
        // its own attack (minus the attacker's Armor). Mirrors ApplyLeaderReflect's damage-only update.
        static void ApplyLeaderCounter(BattleSlot mine, BattleSlot theirs, Battle b, PlayerState opPs, BUnit attacker, bool attackerIsLeader, bool isRanged, int ax, int ay)
        {
            if (attackerIsLeader || isRanged || attacker == null || attacker.IsCorpse) return;
            if (opPs.LeaderCard / 2 != 76 || attacker.Damage >= attacker.Max) return;
            int catk = Math.Max(1, AtkOf(opPs.LeaderCard) + opPs.LeaderStrBonus - attacker.Armor);
            attacker.Damage += catk;
            Log("  -> LEADER COUNTER (Uriah): " + catk + " to attacker (" + ax + "," + ay + ") (" + attacker.Damage + "/" + attacker.Max + ")");
            SendUnitUpdateToBoth(mine, theirs, ax, ay, attacker, b);
        }

        // Andrus: after a hero in your unit attacks, the Leader casts a free follow-up on the target —
        // Ice 2 (target + orthogonal neighbours) after a MELEE attack, Fire 2 (the target's whole row)
        // after a RANGED attack. Effect damage (armor/cover don't apply); pushed via SyncUnitStates.
        static void ApplyAndrus(BattleSlot mine, BattleSlot theirs, Battle b, PlayerState ps, PlayerState opPs, bool attackerIsLeader, bool targetIsLeader, bool isRanged, int tx, int ty)
        {
            if (attackerIsLeader || theirs == null || ps.LeaderCard / 2 != 72) return;
            if (isRanged)
            {
                Log("  LEADER (Andrus): ranged attack -> Fire 2 on row (lane " + ty + ")");
                for (int wx = 0; wx <= 2; wx++) DamageEnemyAt(opPs, wx, ty, 2);
            }
            else
            {
                if (targetIsLeader) return; // Ice needs a hero target
                Log("  LEADER (Andrus): melee attack -> Ice 2 on (" + tx + "," + ty + ") + adjacent");
                DamageEnemyAt(opPs, tx, ty, 2);
                DamageEnemyAt(opPs, tx - 1, ty, 2); DamageEnemyAt(opPs, tx + 1, ty, 2);
                DamageEnemyAt(opPs, tx, ty - 1, 2); DamageEnemyAt(opPs, tx, ty + 1, 2);
            }
            ProcessImmediateDeaths(opPs, theirs, mine, b);
            BattleSlot q0 = mine.P == 0 ? mine : theirs, q1 = mine.P == 0 ? theirs : mine;
            if (q0 != null && q1 != null) SyncUnitStates(q0, q1, b);
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

            // Regen N: at the end of each of your turns, heal N damage from each of your Regen heroes.
            ApplyRegen(b.P[mine.P]);
            // Leader end-of-turn passives (Kavri heal / Marmelee draw) for the player whose turn is ending.
            ApplyLeaderEndOfTurn(mine, theirs, b);

            // Restructure ("free move/clear-corpse until end of turn") expires at end of turn.
            // (Strength Buff lasts until end of WAVE — cleared in ResetWaveFlags, not here.)
            b.P[mine.P].RestructureFree = false;
            // Phantom cards vanish from the ending player's hand at end of their turn.
            {
                PlayerState eps = b.P[mine.P];
                foreach (ushort pc in eps.PhantomCards)
                {
                    int idx = mine.Hand.IndexOf(pc);
                    if (idx < 0) continue;
                    mine.Hand.RemoveAt(idx);
                    Send(mine.Me.Ns, new PacketWriter().WriteU8((byte)idx).WriteU16(pc).WriteBool(true).Frame(Op.HandCardRemove));
                    if (theirs != null) Send(theirs.Me.Ns, new PacketWriter().WriteU8((byte)Math.Min(255, mine.Hand.Count)).WriteU8((byte)idx).WriteU16(pc).Frame(Op.HandCardRemoveGet));
                }
                if (eps.PhantomCards.Count > 0) Log("  PHANTOM: expired " + eps.PhantomCards.Count + " phantom card(s) from " + mine.Me.User + "'s hand");
                eps.PhantomCards.Clear();
            }

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
                if (b.Round >= 2) GrantWaveOrbs(b); // +1 orb each wave (capped), only after the ceasefire
                ResetWaveFlags(b);
                Log("  -> wave complete -> wave " + b.Wave + " (" + WaveName(b.Wave) + ")");
                SendWave(p0, p1, b);
            }
            else
            {
                // Rear wave done -> process casualties, new round
                ProcessCasualties(b, p0, p1);
                if (b.Over) return;
                // Leader end-of-round passives (Lixis rival damage / Mikhail resurrect), then re-check rout.
                ApplyLeaderEndOfRound(b, p0, p1);
                for (int pi = 0; pi < 2 && !b.Over; pi++)
                    if (b.P[pi].LeaderLife <= 0)
                    {
                        b.Over = true;
                        BattleSlot win = pi == 0 ? p1 : p0, lose = pi == 0 ? p0 : p1;
                        Log("BATTLE END: player " + pi + " leader routed by an end-of-round leader passive");
                        Send(win.Me.Ns, new PacketWriter().WriteBool(true).WriteU16(0).Frame(Op.BattleEnd));
                        Send(lose.Me.Ns, new PacketWriter().WriteBool(false).WriteU16(0).Frame(Op.BattleEnd));
                    }
                if (b.Over) return;
                b.Round++;
                b.First = 1 - b.First;
                b.P[0].IsFirstPlayer = (b.First == 0); b.P[1].IsFirstPlayer = (b.First == 1); // Kallistar
                b.Wave = 2;
                b.Active = b.First;
                if (b.Round >= 2) GrantWaveOrbs(b); // first grant is entering round 2 (ceasefire just ended)
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

                // A unit dies when its accumulated damage reaches its life. Deathproof needs no special
                // case here: it only blocks INSTANT-KILL effects (KillEnemyAt); ordinary damage (attacks
                // AND damage effects) accumulates and can defeat it normally.
                foreach (var kvp in ps.Units)
                {
                    BUnit u = kvp.Value;
                    if (u == null || u.IsCorpse) continue;
                    if (u.Damage >= u.Max)
                    {
                        // Immortality: a vanguard hero can't die — cap just below lethal and skip.
                        if (IsImmortalVanguard(ps, kvp.Key / 10)) { u.Damage = Math.Max(0, u.Max - 1); continue; }
                        deadKeys.Add(kvp.Key); anyCasualties = true;
                    }
                }

                foreach (int key in deadKeys)
                {
                    BUnit u = ps.Units[key];
                    int ux = key / 10, uy = key % 10;
                    BattleSlot own = pi == 0 ? p0 : p1, opp = pi == 0 ? p1 : p0;
                    // Ephemeral: leaves NO corpse — clear it immediately instead of flipping to a corpse.
                    if ((u.Abilities & UnitAbility.Ephemeral) != 0)
                    {
                        Log("  EPHEMERAL: player " + pi + " unit(" + ux + "," + uy + ") card " + u.Card + " defeated -> corpse cleared");
                        DiscardCard(own, opp, u.Card);
                        ps.Units.Remove(key);
                        SendClearCorpse(own, opp, ux, uy);
                        OnHeroDefeated(own, opp, b, u); // Iri / Shekhtur / Rexan
                        continue;
                    }
                    Log("  CASUALTY: player " + pi + " unit(" + ux + "," + uy + ") card " + u.Card + " (damage " + u.Damage + " >= life " + u.Max + ")");
                    u.IsCorpse = true;
                    u.Damage = 0;
                    u.CorpseFresh = true;
                    SendUnitUpdateToBoth(own, opp, ux, uy, u, b);
                    OnHeroDefeated(own, opp, b, u); // Iri / Shekhtur / Rexan
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

            // Immortality counts down one per wave (it protected this wave's casualties above, then ticks).
            for (int pi = 0; pi < 2; pi++)
                if (b.P[pi].ImmortalWaves > 0)
                {
                    b.P[pi].ImmortalWaves--;
                    Log("  IMMORTALITY: player " + pi + " has " + b.P[pi].ImmortalWaves + " wave(s) of vanguard immortality left");
                    // Expired: clear the face-up operation card from the reserve slot.
                    if (b.P[pi].ImmortalWaves == 0 && b.P[pi].ImmortalLane >= 0)
                    {
                        BattleSlot own = pi == 0 ? p0 : p1, opp = pi == 0 ? p1 : p0;
                        SendDestroyUnit(own, opp, 3, b.P[pi].ImmortalLane);
                        b.P[pi].ImmortalLane = -1;
                        Log("  IMMORTALITY: player " + pi + " operation expired -> reserve card cleared");
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
                    // Strength Buff (official) lasts "until end of the wave" — clear it as the wave turns over.
                    if (u != null) u.TempStrength = 0;
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
            if (act.Battle != null) act.Battle.P[active].OrderedThisTurn = false; // reset Tatsumi's first-order tracker
            GrantAction(act);
            // Any corpse alive at a turn boundary has finished its death animation client-side, so it's
            // now safe (and necessary) to re-send it — mark it settled so SyncUnitStates re-greys it.
            if (act.Battle != null)
                for (int pi = 0; pi < 2; pi++)
                    foreach (var kv in act.Battle.P[pi].Units)
                        if (kv.Value != null && kv.Value.IsCorpse) kv.Value.CorpseFresh = false;
            SyncUnitStates(p0, p1, act.Battle);

            // Start the turn clock for the auto-advance timeout.
            if (act.Battle != null) act.Battle.TurnDeadline = DateTime.UtcNow.AddSeconds(TurnSeconds);

            // Re-send the current orb counts each turn to keep both clients in sync (idempotent).
            // Orbs are gained per wave (GrantWaveOrbs) and spent on orders (HandleOrder).
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

            // If it's now the bot's turn, let the AI play it.
            if (b != null && b.BotP == active) StartBotTurn(act);
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

                // Skip only FRESHLY-died corpses (death anim may still be playing client-side; a repeat
                // dead=1 would restart it). SETTLED corpses ARE re-sent (active=0) so they stay greyed —
                // anim_wave_update un-greys every unit on a wave change, and only a re-send re-greys them.
                if (u.IsCorpse && u.CorpseFresh) continue;

                int ux = kvp.Key / 10;
                int uy = kvp.Key % 10;
                // "active" (client: not greyed / can be ordered) means simply READY: alive, hasn't
                // acted this wave, and isn't summon-sick. It must NOT be tied to the current wave
                // column — the client already restricts attacks to the active wave via
                // (global.__wave == grid_x) in the unit action menu. Greying by wave here overrides
                // anim_wave_update's activate=1 refresh and paints the whole board exhausted.
                // Render dead on lethal damage (or corpse) so the death animation plays during the
                // turn, not in the wave-advance burst (see SendUnitUpdateToBoth). Corpses keep 0 HP.
                bool immVanS = IsImmortalVanguard(ps, ux); // protected vanguard: never dead
                bool unitDead = !immVanS && (u.IsCorpse || u.Damage >= u.Max);
                // active must exclude dead/dying units (see SendUnitUpdateToBoth) — else a corpse renders
                // as a live, movable unit on the client.
                bool unitActive = !unitDead && !u.HasAttackedThisWave && !u.RecruitedThisWave;
                if (unitDead) u.DeathSent = true;  // sent once; future syncs skip it (see the continue above)
                int curLife = unitDead ? 0 : Math.Max(immVanS ? 1 : 0, u.Max - u.Damage);
                if (PacketLog)
                    Log("  unit (" + ux + "," + uy + ") active=" + (unitActive ? 1 : 0)
                        + " wave=" + b.Wave + " ux=" + ux + " isCorpse=" + u.IsCorpse);

                    // Send BASE atk + Strength bonus separately (client shows atk+str+mstr).
                    // str includes TempStrength (Strength Buff) so the display reflects the buff.
                byte uBaseAtk = (byte)Math.Max(0, AtkOf(u.Card) - u.Enervate); // Enervate lowers the shown base attack
                byte uDispStr = (byte)Math.Max(0, Math.Min(255, u.Strength + u.TempStrength + (u.Damage > 0 ? u.Revenge : 0)));
                    // UpdateUnit to owner — raw Y
                Send(ownerSlot.Me.Ns, new PacketWriter()
                    .WriteU8((byte)ux).WriteU8((byte)uy).WriteU8(uBaseAtk).WriteU16((ushort)curLife)
                    .WriteU8(uDispStr).WriteU8(0).WriteU8((byte)u.Armor)
                    .WriteBool(unitActive).WriteBool(unitDead).WriteU8((byte)u.Max)
                    .Frame(Op.UpdateUnit));
                // UpdateUnitGet to opponent — raw Y
                Send(oppSlot.Me.Ns, new PacketWriter()
                    .WriteU8((byte)ux).WriteU8((byte)uy).WriteU8(uBaseAtk).WriteU16((ushort)curLife)
                    .WriteU8(uDispStr).WriteU8(0).WriteU8((byte)u.Armor)
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
            if (ns == null) return; // bot "connection" has no stream — sends are no-ops
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
        public NetworkStream Ns;   // null for the AI bot
        public string User;
        public byte DeckId;
        public bool IsBot;
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
                if (_waiting == null || ReferenceEquals(_waiting.Ns, ns))
                {
                    _waiting = me;
                    Program.Log("QUEUE: " + user + " waiting (deck " + deckId + ")");
                    // Single-player: if no human joins within a short window, pair with the AI bot.
                    if (Program.BotEnabled) ScheduleBotMatch(me);
                    return;
                }
                opp = _waiting; _waiting = null; bid = _nextBattleId++;
            }
            Program.StartBattle(opp, me, bid);
        }

        // After a short delay, if `pending` is still the lone queued player, start a match vs the bot.
        static void ScheduleBotMatch(Waiting pending)
        {
            var t = new System.Threading.Thread(() =>
            {
                System.Threading.Thread.Sleep(2500);
                Waiting human = null; int bid = 0;
                lock (_lock)
                {
                    if (!ReferenceEquals(_waiting, pending)) return; // matched a human, or cancelled
                    human = _waiting; _waiting = null; bid = _nextBattleId++;
                }
                var bot = new Waiting { Ns = Program.MakeBotStream(), User = "bot", DeckId = 0, IsBot = true };
                Program.Log("QUEUE: no human found -> matching " + human.User + " vs BOT");
                Program.StartBattle(human, bot, bid);
            }) { IsBackground = true };
            t.Start();
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
                // Anyone without a deck (new playtesters, the bot) gets a ready-to-play finished deck in
                // slot 0, so self-hosting works out of the box with any username.
                if (arr[0] == null) arr[0] = DefaultDeck();
                _mem[user] = arr;
                return arr;
            }
        }

        // Leader + 30 finished hero cards (card id = REAL * 2), repeated to fill 30 slots.
        static readonly int[] _finished = { 56, 58, 60, 62, 64, 68, 70, 72, 78, 84, 86, 88, 92, 94, 96, 98, 100 };
        static Deck DefaultDeck()
        {
            var d = new Deck { Id = 0, Flag = false, Back = 1, Land = 2, Name = "Arena Deck" };
            d.Cards[0] = 2; // leader
            for (int i = 0; i < 30; i++) d.Cards[i + 1] = (ushort)_finished[i % _finished.Length];
            return d;
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
