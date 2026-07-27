// ---------------------------------------------------------------------------
//  PtoHarness — headless two-client test harness for the PTO_C private server.
//
//  Spins up two scripted protocol clients, runs them through a full battle
//  scenario, and prints a decoded transcript of everything the server sends.
//  Lets us iterate on server-side battle logic without driving the real GUI.
//
//  Usage: PtoHarness.exe [host] [port]      (default 127.0.0.1 51400)
//  Run an isolated server first:  PtoServer.exe --port 51400 --quiet
// ---------------------------------------------------------------------------
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace PtoHarness
{
    class Frame { public byte Op; public ushort Magic; public byte[] Payload; }

    class PW // packet writer (little-endian, matches the client)
    {
        readonly MemoryStream _ms = new MemoryStream();
        public PW U8(byte v){ _ms.WriteByte(v); return this; }
        public PW Bool(bool v){ _ms.WriteByte((byte)(v?1:0)); return this; }
        public PW U16(ushort v){ _ms.Write(BitConverter.GetBytes(v),0,2); return this; }
        public PW Str(string s){ var b=Encoding.UTF8.GetBytes(s??""); _ms.Write(b,0,b.Length); _ms.WriteByte(0); return this; }
        public byte[] Frame(byte op){ var p=_ms.ToArray(); uint tot=(uint)(7+p.Length); var pkt=new byte[tot];
            pkt[0]=op; BitConverter.GetBytes((ushort)1374).CopyTo(pkt,1); BitConverter.GetBytes(tot).CopyTo(pkt,3);
            Buffer.BlockCopy(p,0,pkt,7,p.Length); return pkt; }
    }

    class HClient
    {
        public readonly string Name;
        readonly TcpClient _tcp; readonly NetworkStream _ns;
        public readonly ConcurrentQueue<Frame> Rx = new ConcurrentQueue<Frame>();
        public volatile bool Running = true;

        public HClient(string host, int port, string name)
        {
            Name = name;
            _tcp = new TcpClient(); _tcp.Connect(host, port); _tcp.NoDelay = true;
            _ns = _tcp.GetStream();
            var t = new Thread(Reader){ IsBackground = true }; t.Start();
        }

        void Reader()
        {
            var hdr = new byte[7];
            try {
                while (Running) {
                    if (!ReadExact(hdr,7)) break;
                    uint len = BitConverter.ToUInt32(hdr,3);
                    if (len < 7 || len > (1<<20)) break;
                    var pl = new byte[len-7];
                    if (!ReadExact(pl, pl.Length)) break;
                    Rx.Enqueue(new Frame{ Op=hdr[0], Magic=BitConverter.ToUInt16(hdr,1), Payload=pl });
                }
            } catch {}
        }
        bool ReadExact(byte[] buf,int n){ int got=0; while(got<n){ int r=_ns.Read(buf,got,n-got); if(r<=0) return false; got+=r; } return true; }

        public void Send(byte[] pkt){ _ns.Write(pkt,0,pkt.Length); _ns.Flush(); }
        public void Close(){ Running=false; try{_tcp.Close();}catch{} }

        // actions
        public void Login(string u){ Send(new PW().Bool(false).Str(u).Str("pw").U16(72).Frame(46)); }
        public void SaveDeck(){ var pw=new PW().Bool(false).Str("Deck").U8(0).U16(1).U16(2);
            pw.U16(2); for(int i=0;i<30;i++) pw.U16((ushort)(26+i)); Send(pw.Frame(47)); }
        public void Queue(){ Send(new PW().U8(0).Frame(0)); }
        public void Ready(){ Send(new PW().Frame(20)); }
        public void Mulligan(){ Send(new PW().Bool(false).Bool(false).Bool(false).Bool(false).Frame(37)); }
        public void Summon(byte gx,byte gy,byte handIdx){ Send(new PW().U8(0).U8(gx).U8(gy).U8(handIdx).Frame(10)); }
        public void Attack(bool isSpell,bool selfGrid,byte ax,byte ay,byte tx,byte ty){
            Send(new PW().Bool(isSpell).Bool(selfGrid).U8(ax).U8(ay).U8(tx).U8(ty).Frame(22)); }
        public void EndTurn(){ Send(new PW().Frame(14)); }

        // collect frames arriving within window ms
        public System.Collections.Generic.List<Frame> Drain(int ms){
            var outl=new System.Collections.Generic.List<Frame>(); int w=0;
            while(w<ms){ Frame f; while(Rx.TryDequeue(out f)) outl.Add(f); Thread.Sleep(50); w+=50;
                // one more sweep
            }
            Frame g; while(Rx.TryDequeue(out g)) outl.Add(g);
            return outl;
        }
    }

    static class Decode
    {
        public static string Line(Frame f)
        {
            var b=f.Payload; var p=0;
            Func<byte> u8 = () => b[p++];
            Func<ushort> u16 = () => { var v=BitConverter.ToUInt16(b,p); p+=2; return v; };
            Func<bool> bl = () => b[p++]!=0;
            Func<string> str = () => { int s=p; while(p<b.Length&&b[p]!=0)p++; var v=Encoding.UTF8.GetString(b,s,p-s); if(p<b.Length)p++; return v; };
            try {
                switch(f.Op){
                    case 46: { byte st=u8(); string extra=st==3?(" user='"+str()+"'"):""; return "login_result="+st+extra; }
                    case 48: return "loaded(door->lobby) legend="+bl()+" rank="+u16();
                    case 49: return null; // add_card (noisy) - suppress
                    case 60: return "stages("+b.Length+"B)";
                    case 47: { byte id=u8(); string nm=str(); return "add_deck id="+id+" '"+nm+"'"; }
                    case 2:  return "battle_start other="+u16()+" battleId="+u16();
                    case 50: { bool me=bl(); ushort bk=u16(); ushort ld=u16(); string un=str(); return "battle_details me="+me+" back="+bk+" land="+ld+" user='"+un+"'"; }
                    case 4:  { ushort wp=u16(); byte hs=u8(); var sb=new StringBuilder(); for(int i=0;i<hs;i++){ if(i>0)sb.Append(","); sb.Append(u16()); } return "battle_data wavePlayer="+wp+" hand=["+sb+"]"; }
                    case 14: return "turn_get player="+u16()+" show="+bl();
                    case 5:  return "summon_unit card="+u16()+" ("+u8()+","+u8()+") trap="+bl();
                    case 6:  return "summon_unit_get card="+u16()+" ("+u8()+","+u8()+") trap="+bl();
                    case 35: return "ATTACK a=("+u8()+","+u8()+") t=("+u8()+","+u8()+") dmg="+u16()+" atype="+u8()+" activ="+bl()+" counter="+bl();
                    case 23: return "casualties("+b.Length+"B)";
                    case 18: return "update_unit("+b.Length+"B)";
                    case 3:  return "battle_end won="+bl()+" newrank="+u16();
                    case 52: return null; // ping
                    default: return "op"+f.Op+" ("+b.Length+"B)";
                }
            } catch { return "op"+f.Op+" (decode-error, "+b.Length+"B)"; }
        }
    }

    class Program
    {
        static string H; static int P;
        static void Main(string[] args)
        {
            H = args.Length>0?args[0]:"127.0.0.1";
            P = args.Length>1?int.Parse(args[1]):51400;
            Console.WriteLine("[harness] connecting to "+H+":"+P);

            var a = new HClient(H,P,"A"); var b = new HClient(H,P,"B");
            Step("login+deck", () => { a.Login("tester"); b.Login("rival"); Sleep(500);
                a.SaveDeck(); b.SaveDeck(); Sleep(300); });
            Dump(a,300); Dump(b,300);

            Step("queue (A then B -> match; B is first player)", () => { a.Queue(); Sleep(300); b.Queue(); Sleep(600); });
            Dump(a,400); Dump(b,400);

            Step("both enter battle room (op20)", () => { a.Ready(); b.Ready(); Sleep(500); });
            Dump(a,500); Dump(b,500);

            Step("both mulligan -> turn 1", () => { a.Mulligan(); Sleep(200); b.Mulligan(); Sleep(500); });
            Dump(a,500); Dump(b,500);

            // B is first player (later joiner). B summons a hero then attacks then ends turn.
            Step("B summon hand[0] at (0,0)", () => { b.Summon(0,0,0); Sleep(400); });
            Dump(a,400); Dump(b,400);

            Console.WriteLine("\n=== B attacks the enemy leader until it dies ===");
            bool ended = false;
            for (int i = 0; i < 30 && !ended; i++)
            {
                b.Attack(false,false,0,0,0,0); Sleep(200);
                foreach (var f in a.Drain(120)) { var l=Decode.Line(f); if(l!=null) Console.WriteLine("  [A] "+l); if(f.Op==3) ended=true; }
                foreach (var f in b.Drain(120)) { var l=Decode.Line(f); if(l!=null) Console.WriteLine("  [B] "+l); if(f.Op==3) ended=true; }
            }
            Console.WriteLine(ended ? "\n*** MATCH COMPLETED (battle_end received) ***" : "\n(no battle_end after 30 attacks)");

            a.Close(); b.Close();
            Console.WriteLine("[harness] done");
        }

        static void Step(string name, Action act){ Console.WriteLine("\n=== "+name+" ==="); act(); }
        static void Sleep(int ms){ Thread.Sleep(ms); }
        static void Dump(HClient c,int ms){
            foreach(var f in c.Drain(ms)){ var l=Decode.Line(f); if(l!=null) Console.WriteLine("  ["+c.Name+"] "+l); }
        }
    }
}
