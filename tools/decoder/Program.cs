using System;
using System.IO;
using System.Runtime.InteropServices;

// Rigorous from-scratch decode:
//  1. Load real SCCore.dll, play one note.
//  2. Read the live voice's sampler-state struct (DAT_181a1b570 + v*0x50) from process memory:
//       +0x20 = delta-stream ptr, +0x38 = scale-stream ptr, +0x2c = len, +0x28 = pos, +0x49 = scale
//  3. Decode those exact ROM bytes with OUR reimplementation of the block-FP DPCM codec.
//  4. Write a WAV. If it's a coherent waveform (not noise), the codec is proven.

unsafe
{
    string dll = args.Length > 0 ? args[0] : @"C:\Program Files\Roland VS\SOUND Canvas VA\SCCore.dll";
    bool scanMode = args.Length > 1 && (args[1] == "scan" || args[1] == "enum" || args[1] == "map" || args[1] == "mapall" || args[1] == "voices" || args[1] == "calib" || args[1] == "filt" || args[1] == "lfo" || args[1] == "song" || args[1] == "smf" || args[1] == "drum" || args[1] == "drumsong" || args[1] == "holdnote" || args[1] == "tvftrace" || args[1] == "drumnote" || args[1] == "panscan" || args[1] == "lfotrace" || args[1] == "seq" || args[1] == "revdump" || args[1] == "chodump" || args[1] == "delaytest" || args[1] == "ampramp" || args[1] == "outfilt" || args[1] == "sampstate" || args[1] == "predtrace" || args[1] == "dumpmem" || args[1] == "postrace" || args[1] == "drumprobe" || args[1] == "portatrace" || args[1] == "panprobe");
    int program = (args.Length > 1 && !scanMode) ? int.Parse(args[1]) : 73; // flute
    int note    = (args.Length > 2 && !scanMode) ? int.Parse(args[2]) : 72;
    string outWav = args.Length > 3 ? args[3] : "sample_decoded.wav";

    nint h = NativeLibrary.Load(dll);
    long b = (long)h;
    Console.WriteLine($"module base = 0x{b:X}");

    var init     = (delegate* unmanaged[Cdecl]<int,int>)  NativeLibrary.GetExport(h, "TG_initialize");
    var setSR    = (delegate* unmanaged[Cdecl]<float,void>)NativeLibrary.GetExport(h, "TG_setSampleRate");
    var setBS    = (delegate* unmanaged[Cdecl]<uint,void>) NativeLibrary.GetExport(h, "TG_setMaxBlockSize");
    var activate = (delegate* unmanaged[Cdecl]<float,int,void>)NativeLibrary.GetExport(h, "TG_activate");
    var setThr   = (delegate* unmanaged[Cdecl]<void>)     NativeLibrary.GetExport(h, "TG_setInterruptThreadIdAtThisTime");
    var shortIn  = (delegate* unmanaged[Cdecl]<uint,uint,void>)NativeLibrary.GetExport(h, "TG_ShortMidiIn");
    var flush    = (delegate* unmanaged[Cdecl]<void>)     NativeLibrary.GetExport(h, "TG_flushMidi");
    var process  = (delegate* unmanaged[Cdecl]<float*,float*,uint,void>)NativeLibrary.GetExport(h, "TG_Process");
    var longIn   = (delegate* unmanaged[Cdecl]<byte*,uint,void>)NativeLibrary.GetExport(h, "TG_LongMidiIn");

    init(0); setSR(44100f); setBS(512); activate(44100f, 512); setThr();

    // --- Roland GS SysEx helpers (see sibling SauceForYourEars/RolandSysEx.cs) ---
    void SendSysEx(byte[] msg){ fixed(byte* mp=msg) longIn(mp,0); }
    byte RolandCksum(byte a1,byte a2,byte a3,byte[] d){ int s=a1+a2+a3; foreach(var x in d) s+=x; return (byte)((128-(s&0x7F))&0x7F); }
    byte[] Dt1(byte a1,byte a2,byte a3,params byte[] d){ var m=new byte[10+d.Length];
        m[0]=0xF0;m[1]=0x41;m[2]=0x10;m[3]=0x42;m[4]=0x12;m[5]=a1;m[6]=a2;m[7]=a3; d.CopyTo(m,8);
        m[^2]=RolandCksum(a1,a2,a3,d); m[^1]=0xF7; return m; }
    byte BlockNum(int ch)=> ch==9?(byte)0 : ch<9?(byte)(ch+1):(byte)ch;
    void GsReset(){ SendSysEx(Dt1(0x40,0x00,0x7F,0x00)); }
    void Gm1On(){ SendSysEx(new byte[]{0xF0,0x7E,0x7F,0x09,0x01,0xF7}); }
    void Gm2On(){ SendSysEx(new byte[]{0xF0,0x7E,0x7F,0x09,0x03,0xF7}); }
    void ToneMap0(int ch,int map){ SendSysEx(Dt1(0x40,(byte)(0x40|BlockNum(ch)),0x01,(byte)map)); }

    // voices mode: play one note, list ALL active voices + their waves. args: dll voices <prog> <note> <vel> <msb>
    if (args.Length > 1 && args[1] == "voices")
    {
        int pg=int.Parse(args[2]), nt=int.Parse(args[3]), vel=args.Length>4?int.Parse(args[4]):100;
        string module=args.Length>5?args[5]:"GM"; int page=args.Length>6?int.Parse(args[6]):0;
        long fbV=b+0x1a1b5b8; var lv=new float[512]; var rv=new float[512];
        void CCv(int c,int v)=>shortIn((uint)(0xB0|(c<<8)|(v<<16)),0);
        // proper mode+map setup, then settle
        if (module=="GM") Gm1On(); else if (module=="GM2") Gm2On();
        else { GsReset(); if(page>=1&&page<=4) for(int c=0;c<16;c++) ToneMap0(c,page); }
        flush(); fixed(float* pl=lv,pr=rv) for(int i=0;i<6;i++) process(pl,pr,512);
        CCv(120,0); flush(); fixed(float* pl=lv,pr=rv) process(pl,pr,512);
        CCv(0, module=="GM2"?page:0);CCv(32,0);CCv(7,127);CCv(10,64);CCv(91,0);CCv(93,0);
        shortIn((uint)(0xC0|(pg<<8)),0);
        shortIn((uint)(0x90|(nt<<8)|(vel<<16)),0); flush();
        fixed(float* pl=lv,pr=rv) for(int i=0;i<4;i++) process(pl,pr,512);
        Console.WriteLine($"active voices for {module} page{page} prog={pg} note={nt} vel={vel}:");
        int cnt=0;
        for(int v=0;v<64;v++){ byte fl=*(byte*)(fbV+v*0x50); if((fl&1)==0) continue;
            uint wc=*(uint*)(b+0x1a6fb60+v*4); int lp=*(int*)(b+0x1a6fc60+v*4);
            Console.WriteLine($"  voice{v} flag=0x{fl:X} wave={wc:X4}:{lp}"); cnt++; }
        Console.WriteLine($"total {cnt} voices"); return;
    }
    // dumpmem mode: write `count` bytes from a VA (hex, e.g. 0x181a03620) of the LOADED image to a
    //   file. Tables must be read from the loaded image (runtime VAs), not the raw file on disk, since
    //   section alignment differs. args: dll dumpmem <VAhex> <count> <out.bin>
    if (args.Length > 1 && args[1] == "dumpmem")
    {
        long va = Convert.ToInt64(args[2], 16);
        int count = int.Parse(args[3]);
        string outp = args[4];
        long addr = b + (va - 0x180000000L);
        var buf = new byte[count];
        System.Runtime.InteropServices.Marshal.Copy((nint)addr, buf, 0, count);
        File.WriteAllBytes(outp, buf);
        Console.WriteLine($"dumpmem VA=0x{va:X} -> {count} bytes @ 0x{addr:X} -> {outp}");
        return;
    }
    // postrace mode: per-CONTROL-TICK sampler read position (+0x28 of the sampler-state struct) for
    //   every active voice, with an optional mid-note note-off. Built to answer whether a voice whose
    //   envelope hold clock is armed (partial block byte 0x00 - the delayed-layer / key-off-layer
    //   mechanism) advances its wave while it waits, or starts the sample fresh when the clock fires.
    //   args: dll postrace <prog> <note> <holdSec> [vel] [bank] [map] [offFrac*1000] [ch] [nrpnMsb nrpnLsb nrpnVal]
    //   The optional channel selects the drum part; the optional NRPN triple is sent before the
    //   note (e.g. 24 <note> 76 = drum pitch coarse +12), so per-map parameter scaling can be
    //   measured straight off the sampler position rate.
    if (args.Length > 1 && args[1] == "postrace")
    {
        int SRp=32000; int pgp=int.Parse(args[2]); int ntp=int.Parse(args[3]);
        double hsp=double.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture);
        int vlp=args.Length>5?int.Parse(args[5]):100;
        int bkp=args.Length>6?int.Parse(args[6]):0;
        int mpp=args.Length>7?int.Parse(args[7]):0;
        int offFracP=args.Length>8?int.Parse(args[8]):int.MaxValue;
        int chp=args.Length>9?int.Parse(args[9]):0;
        setSR((float)SRp); setBS(512); activate((float)SRp,512); setThr();
        long fbp=b+0x1a1b5b8, ssp=b+0x1a1b570;
        var lp2=new float[512]; var rp2=new float[512];
        GsReset(); if(mpp>=1&&mpp<=4) for(int c=0;c<16;c++) ToneMap0(c,mpp); flush();
        fixed(float* pl=lp2,pr=rp2) for(int i=0;i<8;i++) process(pl,pr,512);
        void CCq(int c,int v)=>shortIn((uint)((0xB0|chp)|(c<<8)|(v<<16)),0);
        CCq(0,bkp);CCq(32,0);CCq(7,127);CCq(10,64);CCq(91,0);CCq(93,0);
        shortIn((uint)((0xC0|chp)|(pgp<<8)),0); flush();
        if(args.Length>12){
            CCq(99,int.Parse(args[10])); CCq(98,int.Parse(args[11])); CCq(6,int.Parse(args[12]));
            flush(); fixed(float* pl=lp2,pr=rp2) process(pl,pr,512);
        }
        shortIn((uint)((0x90|chp)|(ntp<<8)|(vlp<<16)),0); flush();
        int totalP=(int)(hsp*SRp), posP=0;
        int offAtP = offFracP==int.MaxValue?int.MaxValue:(int)(offFracP/1000.0*totalP);
        bool offSentP=false;
        while(posP<totalP){
            if(!offSentP && posP>=offAtP){ shortIn((uint)((0x80|chp)|(ntp<<8)),0); flush(); offSentP=true;
                Console.WriteLine($"--- note-off @ {posP*1000.0/SRp:0}ms"); }
            fixed(float* pl=lp2,pr=rp2) process(pl,pr,320); posP+=320;
            Console.Write($"{posP*1000.0/SRp,6:0}ms:");
            for(int v=0;v<64;v++){ if((*(byte*)(fbp+v*0x50)&1)==0) continue;
                long st=ssp+(long)v*0x50;
                Console.Write($"  v{v} pos={*(int*)(st+0x28)}/{*(int*)(st+0x2c)}"); }
            Console.WriteLine();
        }
        return;
    }
    // drumprobe mode: read a drum part's live per-note parameter planes before and after an NRPN, to
    //   see what the handler actually stores and whether the tone map changes the scaling. The part
    //   is reached from a sounding voice (voice+0x128), its per-note map from part+0x18; plane 0x180
    //   is drum pitch coarse, 0x100 level, 0x280 pan. Also prints the Rx word (part+0x3d6) and the
    //   part flags (part+0x12) that gate the NRPN handler.
    //   args: dll drumprobe <note> <map> <nrpnMsb> <nrpnVal> [prog]
    if (args.Length > 1 && args[1] == "drumprobe")
    {
        int ntd=int.Parse(args[2]); int mpd=int.Parse(args[3]);
        int msbd=int.Parse(args[4]); int vald=int.Parse(args[5]);
        int pgd=args.Length>6?int.Parse(args[6]):0;
        setSR(32000f); setBS(512); activate(32000f,512); setThr();
        long fbd=b+0x1a1b5b8;
        var getVCd=(delegate* unmanaged[Cdecl]<int,long>)(b+0x5c360);
        long vcd=getVCd(0);
        var ld=new float[512]; var rd=new float[512];
        void CCd(int c,int v)=>shortIn((uint)((0xB0|9)|(c<<8)|(v<<16)),0);
        GsReset(); if(mpd>=1&&mpd<=4) for(int c=0;c<16;c++) ToneMap0(c,mpd); flush();
        fixed(float* pl=ld,pr=rd) for(int i=0;i<8;i++) process(pl,pr,512);
        CCd(7,127);CCd(10,64);CCd(91,0);CCd(93,0);
        shortIn((uint)((0xC0|9)|(pgd<<8)),0); flush();
        fixed(float* pl=ld,pr=rd) process(pl,pr,512);

        long PartOf(){ for(int v=0;v<64;v++){ if((*(byte*)(fbd+v*0x50)&1)!=0) return *(long*)(vcd+(long)v*0x220+0x128); } return 0; }
        void Strike(){ shortIn((uint)((0x90|9)|(ntd<<8)|(110<<16)),0); flush();
                       fixed(float* pl=ld,pr=rd) for(int i=0;i<3;i++) process(pl,pr,256); }
        void Dump(string tag){
            long part=PartOf();
            if(part==0){ Console.WriteLine($"{tag}: no sounding voice"); return; }
            long map=*(long*)(part+0x18);
            Console.WriteLine($"{tag}: part=0x{part:X} rx=0x{*(ushort*)(part+0x3d6):X4} flags=0x{*(byte*)(part+0x12):X2} hdr0d=0x{*(byte*)(part+0x24c):X2}"
                +$" bank={*(byte*)(part+0x44d)}/{*(byte*)(part+0x44e)} prog={*(byte*)(part+0x3d5)}"
                +$" | pitch[{ntd}]={*(sbyte*)(map+0x180+ntd)} level={*(byte*)(map+0x100+ntd)} pan={*(byte*)(map+0x280+ntd)}");
        }

        Strike(); Dump("before");
        CCd(99,msbd); CCd(98,ntd); CCd(6,vald); flush();
        fixed(float* pl=ld,pr=rd) process(pl,pr,512);
        Dump("after ");
        Strike();
        // The voice's own absolute pitch after the NRPN, which is what the ratio is built from.
        for(int v=0;v<64;v++){ if((*(byte*)(fbd+v*0x50)&1)==0) continue;
            long p=vcd+(long)v*0x220;
            Console.WriteLine($"  voice{v}: pitch64={*(int*)(p+0x64)} pitch6c={*(int*)(p+0x6c)} inc=0x{*(uint*)(p+0xb8):X}"); }
        return;
    }
    // portatrace mode: per-control-tick absolute voice pitch (voice+0x6c) across a portamento glide,
    //   to measure the step-per-tick the engine applies for a CC5 time byte. Plays note A, then note
    //   B with portamento on, and prints the pitch each tick.
    //   args: dll portatrace <prog> <noteA> <noteB> <time> <ticks> [cc84from]
    if (args.Length > 1 && args[1] == "portatrace")
    {
        int SRt=32000; int pgt=int.Parse(args[2]); int nA=int.Parse(args[3]); int nB=int.Parse(args[4]);
        int timet=int.Parse(args[5]); int nticks=int.Parse(args[6]);
        int cc84=args.Length>7?int.Parse(args[7]):-1;
        setSR((float)SRt); setBS(512); activate((float)SRt,512); setThr();
        long fbt=b+0x1a1b5b8;
        var getVCt=(delegate* unmanaged[Cdecl]<int,long>)(b+0x5c360);
        long vct=getVCt(0);
        var lt=new float[512]; var rt=new float[512];
        void CCt(int c,int v)=>shortIn((uint)(0xB0|(c<<8)|(v<<16)),0);
        GsReset(); flush(); fixed(float* pl=lt,pr=rt) for(int i=0;i<8;i++) process(pl,pr,512);
        CCt(7,127);CCt(10,64);CCt(91,0);CCt(93,0);
        shortIn((uint)(0xC0|(pgt<<8)),0); flush();
        CCt(5,timet);
        if(args.Length>8 && int.Parse(args[8])!=0) CCt(126,1);   // mono mode
        if(cc84<0) CCt(65,127);
        shortIn((uint)(0x90|(nA<<8)|(100<<16)),0); flush();
        fixed(float* pl=lt,pr=rt) for(int i=0;i<25;i++) process(pl,pr,320);
        shortIn((uint)(0x80|(nA<<8)),0); flush();
        if(cc84>=0) CCt(84,cc84);
        shortIn((uint)(0x90|(nB<<8)|(100<<16)),0); flush();
        for(int t=0;t<nticks;t++){
            fixed(float* pl=lt,pr=rt) process(pl,pr,320);
            Console.Write($"{t,3}:");
            for(int v=0;v<64;v++){ if((*(byte*)(fbt+v*0x50)&1)==0) continue;
                long p=vct+(long)v*0x220;
                Console.Write($" v{v} pitch={*(int*)(p+0x6c)} glide={*(int*)(p+0x8c)} inc=0x{*(uint*)(p+0xb8):X}"); }
            Console.WriteLine();
        }
        return;
    }
    // panprobe mode: strike a note several times at a given CC10 and print each voice's resolved pan
    //   position (voice+0xf8) with the packed L/R bus sends (voice+0xf4/0xf6). GS panpot 0 is RND,
    //   so this shows whether the engine redraws per note.
    //   args: dll panprobe <prog> <note> <cc10> <strikes>
    if (args.Length > 1 && args[1] == "panprobe")
    {
        int pgn=int.Parse(args[2]); int ntn=int.Parse(args[3]);
        int pan=int.Parse(args[4]); int strikes=args.Length>5?int.Parse(args[5]):6;
        setSR(32000f); setBS(512); activate(32000f,512); setThr();
        long fbn=b+0x1a1b5b8;
        var getVCn=(delegate* unmanaged[Cdecl]<int,long>)(b+0x5c360);
        long vcn=getVCn(0);
        var ln=new float[512]; var rn=new float[512];
        void CCn(int c,int v)=>shortIn((uint)(0xB0|(c<<8)|(v<<16)),0);
        GsReset(); flush(); fixed(float* pl=ln,pr=rn) for(int i=0;i<8;i++) process(pl,pr,512);
        CCn(7,127);CCn(10,pan);CCn(91,0);CCn(93,0);
        shortIn((uint)(0xC0|(pgn<<8)),0); flush();
        for(int k=0;k<strikes;k++){
            shortIn((uint)(0x90|(ntn<<8)|(100<<16)),0); flush();
            fixed(float* pl=ln,pr=rn) for(int i=0;i<2;i++) process(pl,pr,320);
            Console.Write($"strike {k}:");
            for(int v=0;v<64;v++){ if((*(byte*)(fbn+v*0x50)&1)==0) continue;
                long p=vcn+(long)v*0x220;
                Console.Write($" v{v} pos={*(short*)(p+0xf8)} L=0x{*(ushort*)(p+0xf4):X4} R=0x{*(ushort*)(p+0xf6):X4}"); }
            Console.WriteLine();
            shortIn((uint)(0x80|(ntn<<8)),0); flush();
            fixed(float* pl=ln,pr=rn) for(int i=0;i<40;i++) process(pl,pr,320);
        }
        return;
    }
    // ampramp mode: read the per-voice GAIN WORD (the amp value voice_ctrl_ramp_a hands the sampler,
    //   at DAT_181a1d830 + (v&3)*0x40 + (v>>2)*4) at 1-sample resolution during the attack, to reveal
    //   the anti-zipper ZOH staircase (held 1/8/32/128 samples per ramp_divider). args: dll ampramp
    //   <prog> <note> <vel> <nsamp> [map]
    if (args.Length > 1 && args[1] == "ampramp")
    {
        int pg=args.Length>2?int.Parse(args[2]):12, nt=args.Length>3?int.Parse(args[3]):60, vel=args.Length>4?int.Parse(args[4]):110;
        int nsamp=args.Length>5?int.Parse(args[5]):900; int map=args.Length>6?int.Parse(args[6]):4;
        setSR(32000f); setBS(512); activate(32000f,512); setThr();
        long fbA=b+0x1a1b5b8;
        void CCa(int c,int v)=>shortIn((uint)((0xB0|0)|(c<<8)|(v<<16)),0);
        if(map>=1&&map<=4){ GsReset(); for(int c=0;c<16;c++) ToneMap0(c,map); } else Gm1On();
        CCa(7,127);CCa(10,64);CCa(91,0);CCa(93,0);
        shortIn((uint)(0xC0|(pg<<8)),0);
        var l=new float[512]; var r=new float[512];
        flush();
        fixed(float* pl=l,pr=r) for(int i=0;i<8;i++) process(pl,pr,512);   // settle
        shortIn((uint)(0x90|(nt<<8)|(vel<<16)),0); flush();
        Console.WriteLine($"ampramp prog={pg} note={nt} vel={vel}");
        Console.WriteLine("i,gain,voice");
        int v0=-1, rec=0;
        for(int blk=0; rec<nsamp; blk++){
            if(v0<0){ for(int v=0;v<64;v++){ if((*(byte*)(fbA+v*0x50)&1)!=0){ v0=v; break; } } }
            if(v0>=0){ long gb=b+0x1a1d830+(v0&3)*0x40+(v0>>2)*4;
                for(int k=0;k<16 && rec<nsamp;k++){ Console.WriteLine($"{rec},{*(float*)(gb+k*4):0.00000000},{v0}"); rec++; } }
            fixed(float* pl=l,pr=r) process(pl,pr,16);
        }
        return;
    }
    // outfilt mode: dump the tg_output_filter (SRC) state -- ratio@+0xc, allpass coef@+0x18 -- at a
    //   given host rate, to see if the engine resamples (and filters) even at 32000. args: dll outfilt [hostRate]
    if (args.Length > 1 && args[1] == "outfilt")
    {
        float hr = args.Length>2 ? float.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 32000f;
        setSR(hr); setBS(512); activate(hr,512); setThr();
        var l=new float[512]; var r=new float[512];
        GsReset(); flush(); fixed(float* pl=l,pr=r) for(int i=0;i<4;i++) process(pl,pr,512);
        long ptr = *(long*)(b+0x1a6e4a8);
        Console.WriteLine($"outfilt host={hr} state@0x{ptr:X}");
        Console.WriteLine($"  +0x0c rate/ratio = {*(float*)(ptr+0x0c):R}");
        Console.WriteLine($"  +0x18 coef k     = {*(float*)(ptr+0x18):R}");
        Console.WriteLine($"  +0x10 counter    = {*(int*)(ptr+0x10)}");
        Console.WriteLine($"  +0x14 frac acc   = {*(float*)(ptr+0x14):R}");
        Console.WriteLine($"  +0x30..0x44 state= {*(float*)(ptr+0x30):R} {*(float*)(ptr+0x34):R} {*(float*)(ptr+0x38):R} {*(float*)(ptr+0x3c):R} {*(float*)(ptr+0x40):R} {*(float*)(ptr+0x44):R}");
        return;
    }
    // sampstate mode: play a melodic note, find the voice, dump its sampler state (DAT_181a1b570 +
    //   v*0x50): +0x20 delta-stream ptr, +0x38 scale-stream ptr, +0x28 pos, +0x2c len, +0x49 scale,
    //   plus the first 16 bytes the delta/scale pointers point at. args: dll sampstate <prog> <note> [map]
    if (args.Length > 1 && args[1] == "sampstate")
    {
        int pg=args.Length>2?int.Parse(args[2]):12, nt=args.Length>3?int.Parse(args[3]):60, map=args.Length>4?int.Parse(args[4]):4;
        setSR(32000f); setBS(512); activate(32000f,512); setThr();
        long fbS=b+0x1a1b5b8, ss=b+0x1a1b570;
        void CCs(int c,int v)=>shortIn((uint)((0xB0|0)|(c<<8)|(v<<16)),0);
        if(map>=1&&map<=4){ GsReset(); for(int c=0;c<16;c++) ToneMap0(c,map); } else Gm1On();
        CCs(7,127);CCs(10,64);CCs(91,0);CCs(93,0);
        shortIn((uint)(0xC0|(pg<<8)),0);
        var l=new float[512]; var r=new float[512]; flush();
        fixed(float* pl=l,pr=r) for(int i=0;i<8;i++) process(pl,pr,512);
        shortIn((uint)(0x90|(nt<<8)|(110<<16)),0); flush();
        int v0=-1; for(int tries=0; tries<8 && v0<0; tries++){ fixed(float* pl=l,pr=r) process(pl,pr,16);
            for(int v=0;v<64;v++){ if((*(byte*)(fbS+v*0x50)&1)!=0){ v0=v; break; } } }
        if(v0<0){ Console.WriteLine("no active voice"); return; }
        long st=ss+(long)v0*0x50;
        long dptr=*(long*)(st+0x20), sptr=*(long*)(st+0x38);
        Console.WriteLine($"sampstate prog={pg} note={nt} voice={v0}");
        Console.WriteLine($"  +0x20 deltaPtr=0x{dptr:X}  +0x38 scalePtr=0x{sptr:X}  +0x28 pos={*(int*)(st+0x28)}  +0x2c len={*(int*)(st+0x2c)}  +0x49 scale={*(byte*)(st+0x49)}");
        Console.WriteLine($"  moduleBase=0x{b:X}  deltaPtr-base=0x{dptr-b:X}  scalePtr-base=0x{sptr-b:X}");
        Console.Write("  delta[0:16]:"); for(int i=0;i<16;i++) Console.Write($" {*(sbyte*)(dptr+i)}"); Console.WriteLine();
        Console.Write("  scale[0:16]:"); for(int i=0;i<16;i++) Console.Write($" {*(byte*)(sptr+i)}"); Console.WriteLine();
        return;
    }
    // predtrace mode: capture the engine's ADPCM predictor accumulator (voice sampler state +0x40, int)
    //   and pos (+0x28) sample-by-sample, to compare against our cumsum(delta<<(scale+10)) decode and
    //   find any bit-width/rounding difference. args: dll predtrace <prog> <note> <nsamp> [map]
    if (args.Length > 1 && args[1] == "predtrace")
    {
        int pg=args.Length>2?int.Parse(args[2]):12, nt=args.Length>3?int.Parse(args[3]):60, nsamp=args.Length>4?int.Parse(args[4]):700, map=args.Length>5?int.Parse(args[5]):4;
        setSR(32000f); setBS(512); activate(32000f,512); setThr();
        long fbP=b+0x1a1b5b8, ssP=b+0x1a1b570;
        void CCp(int c,int v)=>shortIn((uint)((0xB0|0)|(c<<8)|(v<<16)),0);
        if(map>=1&&map<=4){ GsReset(); for(int c=0;c<16;c++) ToneMap0(c,map); } else Gm1On();
        CCp(7,127);CCp(10,64);CCp(91,0);CCp(93,0);
        shortIn((uint)(0xC0|(pg<<8)),0);
        var l=new float[512]; var r=new float[512]; flush();
        fixed(float* pl=l,pr=r) for(int i=0;i<8;i++) process(pl,pr,512);
        shortIn((uint)(0x90|(nt<<8)|(110<<16)),0); flush();
        int v0=-1; for(int tr=0;tr<16 && v0<0;tr++){ fixed(float* pl=l,pr=r) process(pl,pr,16);
            for(int v=0;v<64;v++){ if((*(byte*)(fbP+v*0x50)&1)!=0){ v0=v; break; } } }
        if(v0<0){ Console.WriteLine("no voice"); return; }
        long st=ssP+(long)v0*0x50;
        Console.WriteLine($"predtrace prog={pg} note={nt} voice={v0}");
        Console.WriteLine("blk,pos,predictor,phase,win3,scale");
        for(int i=0;i<nsamp;i++){
            int pos=*(int*)(st+0x28), pred=*(int*)(st+0x40); byte sc=*(byte*)(st+0x49);
            ushort ph=*(ushort*)(st+0x46); float w3=*(float*)(st+0x0c);
            Console.WriteLine($"{i},{pos},{pred},{ph},{w3:R},{sc}");
            fixed(float* pl=l,pr=r) process(pl,pr,4);   // advance 4 samples/step (process(1) freezes the voice)
        }
        return;
    }
    // calib mode: measure control-tick period. Play a note; read env-state (rate@+0x12, phase@+0x1a)
    //   from the voice-control struct across small render chunks. control_block_samples = frames*rate*speed/dphase.
    //   args: dll calib <prog> <note> <vel> <framesPerStep> <steps>
    if (args.Length > 1 && args[1] == "calib")
    {
        int pg=args.Length>2?int.Parse(args[2]):48, nt=args.Length>3?int.Parse(args[3]):60, vel=args.Length>4?int.Parse(args[4]):100;
        uint fps=args.Length>5?(uint)int.Parse(args[5]):32; int steps=args.Length>6?int.Parse(args[6]):160;
        int cmap=args.Length>7?int.Parse(args[7]):0;   // tone map 1=SC55..4=SC8820; 0=default
        // re-init so host rate == internal 32000 (1 host frame == 1 internal sample)
        setSR(32000f); setBS(512); activate(32000f,512); setThr();
        var getVC=(delegate* unmanaged[Cdecl]<int,long>)(b+0x5c360);   // DAT_181a749e0(0) -> voice-control base
        var l=new float[512]; var r=new float[512];
        void CCc(int c,int v)=>shortIn((uint)((0xB0|0)|(c<<8)|(v<<16)),0);
        if (cmap>=1 && cmap<=4){ GsReset(); for(int c=0;c<16;c++) ToneMap0(c,cmap);
            flush(); fixed(float* pl=l,pr=r) for(int i=0;i<6;i++) process(pl,pr,512); }
        CCc(0,0);CCc(32,0);CCc(7,127);CCc(10,64);CCc(91,0);CCc(93,0);
        shortIn((uint)(0xC0|(pg<<8)),0);
        shortIn((uint)(0x90|(nt<<8)|(vel<<16)),0); flush();
        long vc=getVC(0);
        Console.WriteLine($"voice-control base=0x{vc:X}; Fs=32000 framesPerStep={fps}");
        // pick the control voice to track = first slot with a running envelope after a few blocks
        int track=-1;
        for(int i=0;i<1;i++){ fixed(float* pl=l,pr=r) process(pl,pr,fps); }
        for(int v=0;v<64;v++){ long ps=vc+(long)v*0x220; byte st=*(byte*)(ps+0xc); ushort rate=*(ushort*)(ps+0x12);
            if(st!=4 && (rate!=0 || *(ushort*)(ps+0x1a)!=0 || *(ushort*)(ps+0x18)!=0)){ track=v; break; } }
        Console.WriteLine($"tracking control voice {track}");
        Console.WriteLine("step,frames,v,state,rate,start,tgt,cur,phase,speed");
        long cum=0;
        for(int s=0;s<steps && track>=0;s++){
            fixed(float* pl=l,pr=r) process(pl,pr,fps);
            cum+=fps;
            ushort speed=*(ushort*)(b+0x1a2283c);
            long ps=vc+(long)track*0x220;
            byte st=*(byte*)(ps+0xc); ushort rate=*(ushort*)(ps+0x12); ushort start=*(ushort*)(ps+0x14);
            ushort tgt=*(ushort*)(ps+0x16); ushort cur=*(ushort*)(ps+0x18); ushort ph=*(ushort*)(ps+0x1a);
            uint cut=*(uint*)(ps+0xc8);   // runtime TVF cutoff (Fc = 17640*2^((cut-245760)/14273))
            Console.WriteLine($"{s},{cum},{track},{st},{rate},{start},{tgt},{cur},{ph},{speed},{cut}");
        }
        return;
    }
    // drum mode: select a drum kit on ch10 and dump the runtime drum map (11 byte-planes x 0x80).
    //   plane0=map, plane0x80=bank, plane0x100=program (-> the same 3-level LUT), 0x180.. = per-key params.
    //   args: dll drum <kit> <out.bin> [dmapIdx]
    if (args.Length > 1 && args[1] == "drum")
    {
        int kit=args.Length>2?int.Parse(args[2]):0;
        string outp=args.Length>3?args[3]:"drummap.bin";
        int dmi=args.Length>4?int.Parse(args[4]):0;
        setSR(32000f); setBS(512); activate(32000f,512); setThr();
        var dl=new float[512]; var dr=new float[512];
        GsReset(); flush(); fixed(float* pl=dl,pr=dr) for(int i=0;i<8;i++) process(pl,pr,512);
        shortIn((uint)(0xC9|(kit<<8)),0); flush();          // program change on ch10 = drum kit
        fixed(float* pl=dl,pr=dr) for(int i=0;i<8;i++) process(pl,pr,512);
        var getDM=(delegate* unmanaged[Cdecl]<int,long>)(b+0x5c460);   // DAT_181a74930(idx) -> drum map
        long dm=getDM(dmi);
        Console.WriteLine($"drum map[{dmi}] base=0x{dm:X} kit={kit}");
        var bytes=new byte[0x580];
        for(int i=0;i<0x580;i++) bytes[i]=*(byte*)(dm+i);
        File.WriteAllBytes(outp,bytes);
        Console.WriteLine("note: map bank prog | p3 p4 p5 p6 p7 p8 p9 p10");
        foreach(int nt in new[]{35,36,38,40,42,46,49,51,56,60}){
            Console.Write($"  {nt}: {bytes[nt]} {bytes[0x80+nt]} {bytes[0x100+nt]} |");
            for(int p=3;p<11;p++) Console.Write($" {bytes[p*0x80+nt]}");
            Console.WriteLine();
        }
        Console.WriteLine($"wrote {outp}");
        // LIVE LUT chain for the drum map planes: lut1[map] -> lut2[..bank] -> lut3[..note]
        byte* lut1=(byte*)(b+0x19f2e30); byte* lut2=(byte*)(b+0x19f28b0); short* lut3=(short*)(b+0x19f32b0);
        Console.WriteLine("live LUT chain (map,bank,prog=note) -> tone#:");
        foreach(int nt in new[]{35,36,38,40,42,46,49,51}){
            int mp=bytes[nt], bk=bytes[0x80+nt], pr=bytes[0x100+nt];
            int g1=lut1[mp]; int g2 = g1==0xff?-1:lut2[g1*0x80+bk];
            int tone = (g1==0xff||g2==0xff||g2<0)?-1:lut3[g2*0x80+pr];
            Console.WriteLine($"  note{nt}: map={mp} bank={bk} prog={pr} | lut1={g1} lut2={g2} -> tone#{tone}");
        }
        // dump the live lut3 row used by drums so we can diff vs the static export
        {   int mp=bytes[36], bk=bytes[0x80+36]; int g1=lut1[mp]; int g2=lut2[g1*0x80+bk];
            var row=new byte[0x100]; for(int i=0;i<0x80;i++){ short v=lut3[g2*0x80+i]; row[i*2]=(byte)(v&0xff); row[i*2+1]=(byte)((v>>8)&0xff); }
            File.WriteAllBytes(outp+".lut3row", row);
            Console.WriteLine($"wrote live lut3 row {g2} -> {outp}.lut3row"); }
        // GROUND TRUTH: play each drum note on ch10 and report the sounding wave
        long fbD=b+0x1a1b5b8;
        Console.WriteLine("drum note -> sounding wave (ground truth):");
        foreach(int nt in new[]{36,41,43,45,47,48,50,42,51}){
            shortIn((uint)((0xB0|9)|(120<<8)|(0<<16)),0); flush();
            fixed(float* pl=dl,pr=dr) for(int i=0;i<20;i++) process(pl,pr,512);
            shortIn((uint)((0x90|9)|(nt<<8)|(110<<16)),0); flush();
            fixed(float* pl=dl,pr=dr) for(int i=0;i<3;i++) process(pl,pr,512);
            var dfound=new System.Collections.Generic.List<string>();
            for(int v=0;v<64;v++){ if((*(byte*)(fbD+v*0x50)&1)==0) continue;
                uint wc=*(uint*)(b+0x1a6fb60+v*4); int lp=*(int*)(b+0x1a6fc60+v*4);
                int en=*(int*)(b+0x1a6fd60+v*4), st=*(int*)(b+0x1a6fe60+v*4);
                dfound.Add($"wc={wc:X4} r{wc&0x7f} loop={lp} end={en} start={st}"); }
            shortIn((uint)((0x80|9)|(nt<<8)),0); flush();
            Console.WriteLine($"  note{nt}: "+(dfound.Count==0?"(silent)":string.Join(" | ",dfound)));
        }
        return;
    }
    // drumsong mode: render the same drum pattern as scvx_engine.render_drums through the real DLL.
    //   args: dll drumsong <kit> <out.wav>
    if (args.Length > 1 && args[1] == "drumsong")
    {
        int SR2=32000; int kit=args.Length>2?int.Parse(args[2]):0;
        string dsWav=args.Length>3?args[3]:"real_engine_drums.wav";
        setSR((float)SR2); setBS(512); activate((float)SR2,512); setThr();
        var l2=new float[512]; var r2=new float[512];
        GsReset(); flush(); fixed(float* pl=l2,pr=r2) for(int i=0;i<8;i++) process(pl,pr,512);
        void CCd(int c,int v)=>shortIn((uint)((0xB0|9)|(c<<8)|(v<<16)),0);
        CCd(7,127); CCd(10,64); CCd(91,0); CCd(93,0);
        shortIn((uint)(0xC9|(kit<<8)),0); flush();
        // build identical pattern (100bpm)
        double bp=60.0/100.0;
        var evs=new System.Collections.Generic.List<(int t,int note,int vel)>();
        for(int bar=0;bar<2;bar++){ double t0=bar*4*bp;
            for(int i=0;i<8;i++) evs.Add(((int)((t0+i*bp/2)*SR2),42,90));
            evs.Add(((int)((t0+0*bp)*SR2),36,110)); evs.Add(((int)((t0+1*bp)*SR2),38,105));
            evs.Add(((int)((t0+2*bp)*SR2),36,100)); evs.Add(((int)((t0+2.5*bp)*SR2),36,90));
            evs.Add(((int)((t0+3*bp)*SR2),38,105)); }
        evs.Add((0,49,100));
        double tf=2*4*bp; int[] fn={48,47,45,43,41}; int[] fv={105,105,100,100,110};
        for(int i=0;i<5;i++) evs.Add(((int)((tf+i*bp/2)*SR2),fn[i],fv[i]));
        evs.Add(((int)((tf+2.5*bp)*SR2),49,110)); evs.Add(((int)((tf+2.5*bp)*SR2),36,110));
        evs.Sort((a,c)=>a.t-c.t);
        int total=(int)(8.5*SR2); var outD=new float[total];
        int blk=64, ei=0, pos=0;
        while(pos<total){
            while(ei<evs.Count && evs[ei].t < pos+blk){ var e=evs[ei++];
                shortIn((uint)((0x90|9)|(e.note<<8)|(e.vel<<16)),0); }
            flush();
            int nf=Math.Min(blk,total-pos);
            fixed(float* pl=l2,pr=r2) process(pl,pr,(uint)nf);
            for(int i=0;i<nf;i++) outD[pos+i]=l2[i];
            pos+=nf;
        }
        float pk=1e-9f; foreach(var v in outD) pk=Math.Max(pk,Math.Abs(v));
        var pcmD=new short[total]; float gD=0.92f/pk;
        for(int i=0;i<total;i++) pcmD[i]=(short)Math.Clamp(outD[i]*gD*32767f,-32768f,32767f);
        WriteWav(dsWav,pcmD,SR2);
        Console.WriteLine($"drumsong done: {dsWav} ({total/(double)SR2:0.0}s, {evs.Count} hits, peak={pk:0.000})");
        return;
    }
    // panscan mode: sweep CC10 (part pan) 0..127 on a centre-panned patch and report L/R rms for
    //   each, to recover the engine's pan law by measurement instead of fitting it to a few points.
    //   args: dll panscan <prog> <note> <out.csv>
    if (args.Length > 1 && args[1] == "panscan")
    {
        int SR8=32000; int pg8=int.Parse(args[2]); int nt8=int.Parse(args[3]);
        string csv8=args.Length>4?args[4]:"panscan.csv";
        setSR((float)SR8); setBS(512); activate((float)SR8,512); setThr();
        var l8=new float[512]; var r8=new float[512];
        void CC8(int c,int v)=>shortIn((uint)((0xB0|0)|(c<<8)|(v<<16)),0);
        var sb8=new System.Text.StringBuilder("cc10,rmsL,rmsR\n");
        for(int pan=0; pan<128; pan++){
            CC8(120,0); CC8(123,0); flush();
            fixed(float* pl=l8,pr=r8) for(int i=0;i<20;i++) process(pl,pr,512);
            GsReset(); flush();
            fixed(float* pl=l8,pr=r8) for(int i=0;i<6;i++) process(pl,pr,512);
            CC8(0,0);CC8(32,0);CC8(7,127);CC8(91,0);CC8(93,0);CC8(10,pan);
            shortIn((uint)(0xC0|(pg8<<8)),0); flush();
            shortIn((uint)(0x90|(nt8<<8)|(100<<16)),0); flush();
            double sl=0,sr=0; int nn=0;
            for(int i=0;i<40;i++){
                fixed(float* pl=l8,pr=r8) process(pl,pr,512);
                for(int k=0;k<512;k++){ sl+=l8[k]*l8[k]; sr+=r8[k]*r8[k]; nn++; }
            }
            shortIn((uint)(0x80|(nt8<<8)),0); flush();
            sb8.Append($"{pan},{Math.Sqrt(sl/nn):0.000000},{Math.Sqrt(sr/nn):0.000000}\n");
        }
        File.WriteAllText(csv8, sb8.ToString());
        Console.WriteLine($"panscan done: {csv8}");
        return;
    }

    // drumnote mode: strike ONE drum note on ch10 and render it alone, for per-instrument A/B.
    //   (a lone hit cannot be compared against drumsong's opening -- a crash and hat fire with it)
    //   args: dll drumnote <kit> <note> <vel> <sec> <out.wav>
    if (args.Length > 1 && args[1] == "drumnote" || args[1] == "panscan")
    {
        // (arg 7, if present, is a CSV path: also dump the full voice struct per control tick)
        int SR6=32000; int kit6=int.Parse(args[2]); int nt6=int.Parse(args[3]);
        int vl6=int.Parse(args[4]);
        double sec6=double.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture);
        string w6=args.Length>6?args[6]:"real_drumnote.wav";
        setSR((float)SR6); setBS(512); activate((float)SR6,512); setThr();
        var l6=new float[512]; var r6=new float[512];
        GsReset(); flush(); fixed(float* pl=l6,pr=r6) for(int i=0;i<8;i++) process(pl,pr,512);
        void CC6(int c,int v)=>shortIn((uint)((0xB0|9)|(c<<8)|(v<<16)),0);
        CC6(7,127); CC6(10,64); CC6(91,0); CC6(93,0);
        shortIn((uint)(0xC9|(kit6<<8)),0); flush();
        // STEREO capture -- the kit pans per note (+0x280), so a mono/left-only capture folds a pan
        // law into the level and makes any A/B comparison meaningless.
        int total6=(int)(sec6*SR6); var outL=new float[total6]; var outR=new float[total6];
        shortIn((uint)((0x90|9)|(nt6<<8)|(vl6<<16)),0); flush();
        int blk6=64,pos6=0;
        while(pos6<total6){
            int nf=Math.Min(blk6,total6-pos6);
            fixed(float* pl=l6,pr=r6) process(pl,pr,(uint)nf);
            for(int i=0;i<nf;i++){ outL[pos6+i]=l6[i]; outR[pos6+i]=r6[i]; }
            pos6+=nf;
        }
        float pk6=1e-9f;
        for(int i=0;i<total6;i++){ pk6=Math.Max(pk6,Math.Abs(outL[i])); pk6=Math.Max(pk6,Math.Abs(outR[i])); }
        var pcm6=new short[total6*2];
        for(int i=0;i<total6;i++){
            pcm6[i*2]  =(short)Math.Clamp(outL[i]/pk6*0.92f*32767f,-32768f,32767f);
            pcm6[i*2+1]=(short)Math.Clamp(outR[i]/pk6*0.92f*32767f,-32768f,32767f);
        }
        WriteWavStereo(w6,pcm6,SR6);
        double sL=0,sR=0; for(int i=0;i<total6;i++){ sL+=outL[i]*outL[i]; sR+=outR[i]*outR[i]; }
        Console.WriteLine($"  L/R rms = {Math.Sqrt(sL/total6):0.00000} / {Math.Sqrt(sR/total6):0.00000}"
                         +$"  (R/L = {Math.Sqrt(sR/Math.Max(1e-30,sL)):0.0000})");
        Console.WriteLine($"drumnote done: {w6} kit={kit6} note={nt6} vel={vl6} peak={pk6:0.0000}");
        if (args.Length > 7)
        {
            // replay, dumping the voice struct + the per-voice AMPLITUDE the sampler is handed
            // (DAT_181a1d830 group scratch, written by voice_ctrl_ramp_a in render_block)
            long fb7=b+0x1a1b5b8;
            var getVC7=(delegate* unmanaged[Cdecl]<int,long>)(b+0x5c360);
            long vc7=getVC7(0);
            setSR((float)SR6); setBS(512); activate((float)SR6,512); setThr();
            GsReset(); flush(); fixed(float* pl=l6,pr=r6) for(int i=0;i<8;i++) process(pl,pr,512);
            CC6(7,127); CC6(10,64); CC6(91,0); CC6(93,0);
            shortIn((uint)(0xC9|(kit6<<8)),0); flush();
            var sb7=new System.Text.StringBuilder("t_ms,voice,amp,cc_cutoff,ee_reso,f5_type,tva_lvl\n");
            shortIn((uint)((0x90|9)|(nt6<<8)|(vl6<<16)),0); flush();
            int nt7=(int)(Math.Min(sec6,2.0)*100);
            var vs=new byte[nt7*4*0x220]; int nrec=0;
            for(int t=0;t<nt7;t++){
                fixed(float* pl=l6,pr=r6) process(pl,pr,320);
                for(int v=0;v<64 && nrec<nt7*4;v++){
                    if((*(byte*)(fb7+v*0x50)&1)==0) continue;
                    long p7=vc7+(long)v*0x220;
                    float amp=*(float*)(b+0x1a1d830+(v&3)*0x40+(v>>2)*0x4);
                    sb7.Append($"{t*10},{v},{amp:0.000000},{*(int*)(p7+0xcc)},{*(byte*)(p7+0xee)},"
                              +$"{*(byte*)(p7+0x1f5)},{*(ushort*)(p7+0x40)}\n");
                    for(int i=0;i<0x220;i++) vs[nrec*0x220+i]=*(byte*)(p7+i);
                    nrec++;
                }
            }
            shortIn((uint)((0x80|9)|(nt6<<8)),0); flush();
            File.WriteAllText(args[7], sb7.ToString());
            File.WriteAllBytes(args[7]+".voice.bin", vs[..(nrec*0x220)]);
            Console.WriteLine($"  voice dump: {args[7]} ({nrec} records)");
        }
        return;
    }

    // tvftrace mode: hold one note and dump the live per-voice TVF fields every control tick.
    //   Ground truth for the TVF envelope: +0xcc runtime cutoff, +0xdc resonance (q raw),
    //   +0xec running env level, +0x1f0 base cutoff, +0x1f5 filter type, +0xee resonance byte.
    //   args: dll tvftrace <prog> <note> <holdSec> <out.csv> [vel]
    if (args.Length > 1 && args[1] == "tvftrace")
    {
        int SR4=32000; int pg4=int.Parse(args[2]); int nt4=int.Parse(args[3]);
        double hs4=double.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture);
        string csv4=args.Length>5?args[5]:"tvftrace.csv"; int vl4=args.Length>6?int.Parse(args[6]):100;
        int bk4=args.Length>7?int.Parse(args[7]):0;   // CC0 bank MSB
        int bend4=args.Length>8?int.Parse(args[8]):8192;  // 14-bit pitch bend (8192 = center)
        int brange4=args.Length>9?int.Parse(args[9]):-1;  // RPN 00/00 bend range in semitones (-1 = don't set)
        int map4=args.Length>10?int.Parse(args[10]):0;    // tone map 1=SC55..4=SC8820; 0=default GS
        setSR((float)SR4); setBS(512); activate((float)SR4,512); setThr();
        long fb4=b+0x1a1b5b8;
        var getVC4=(delegate* unmanaged[Cdecl]<int,long>)(b+0x5c360);
        long vc4=getVC4(0);
        var l4=new float[512]; var r4=new float[512];
        GsReset(); if(map4>=1&&map4<=4) for(int c=0;c<16;c++) ToneMap0(c,map4); flush();
        fixed(float* pl=l4,pr=r4) for(int i=0;i<8;i++) process(pl,pr,512);
        void CC4(int c,int v)=>shortIn((uint)((0xB0|0)|(c<<8)|(v<<16)),0);
        CC4(0,bk4);CC4(32,0);CC4(7,127);CC4(10,64);CC4(91,0);CC4(93,0);
        if(brange4>=0){ CC4(101,0);CC4(100,0);CC4(6,brange4);CC4(38,0);CC4(101,127);CC4(100,127); }  // RPN bend range
        shortIn((uint)(0xC0|(pg4<<8)),0); flush();
        if(bend4!=8192){ shortIn((uint)((0xE0|0)|((bend4&0x7f)<<8)|(((bend4>>7)&0x7f)<<16)),0); flush(); }  // 0xEn LSB MSB
        var sb4=new System.Text.StringBuilder("t_ms,voice,cc_cutoff,dc_resoraw,ec_envlev,f0_base,f5_type,ee_resobyte,pitch64,pitch6c,phase_bc,amp\n");
        shortIn((uint)(0x90|(nt4<<8)|(vl4<<16)),0); flush();
        int total4=(int)(hs4*SR4), pos4=0, step4=320;   // 320 samples = one 100 Hz control tick
        // optional note-off partway (arg 11, fraction*1000 of hs4) so the RELEASE segment is traced too
        int offAt = args.Length>11 ? (int)(int.Parse(args[11])/1000.0*total4) : int.MaxValue;
        bool offSent=false;
        while(pos4<total4){
            if(!offSent && pos4>=offAt){ shortIn((uint)(0x80|(nt4<<8)),0); flush(); offSent=true; }
            fixed(float* pl=l4,pr=r4) process(pl,pr,(uint)step4);
            pos4+=step4;
            for(int v=0;v<64;v++){
                if((*(byte*)(fb4+v*0x50)&1)==0) continue;
                long p4=vc4+(long)v*0x220;
                sb4.Append($"{pos4*1000.0/SR4:0.0},{v},{*(int*)(p4+0xcc)},{*(int*)(p4+0xdc)},"
                          +$"{*(short*)(p4+0xec)},{*(ushort*)(p4+0x1f0)},{*(byte*)(p4+0x1f5)},{*(byte*)(p4+0xee)},{*(int*)(p4+0x64)},{*(int*)(p4+0x6c)},{*(int*)(p4+0xbc)},{*(float*)(b+0x1a1d830+(v&3)*0x40+(v>>2)*4):0.000000}\n");
            }
        }
        shortIn((uint)(0x80|(nt4<<8)),0); flush();
        // Per-PROGRAM filter/env defaults live in the PART struct at +0x453..+0x45b (loaded by the
        // program-change handler from the preset table). Read them via the active voice's part
        // pointer (voice+0x128) and print, to see which programs are non-neutral (0x40).
        for(int v=0;v<64;v++){ long pv=vc4+(long)v*0x220; if((*(byte*)(fb4+v*0x50)&1)==0) continue;
            long part=*(long*)(pv+0x128);
            Console.Write("  part_defaults +0x453..45b:");
            for(int o=0x453;o<=0x45b;o++) Console.Write($" {*(byte*)(part+o)}");
            Console.WriteLine($"   (part+0x3e6 cut={*(byte*)(part+0x3e6)} 0x3e7 reso={*(byte*)(part+0x3e7)})");
            break; }
        File.WriteAllText(csv4, sb4.ToString());
        // also dump the full voice struct per tick for voices 0/1 so unknown fields can be mined
        {
            setSR((float)SR4); setBS(512); activate((float)SR4,512); setThr();
            GsReset(); if(map4>=1&&map4<=4) for(int c=0;c<16;c++) ToneMap0(c,map4); flush();
            fixed(float* pl=l4,pr=r4) for(int i=0;i<8;i++) process(pl,pr,512);
            CC4(0,bk4);CC4(32,0);CC4(7,127);CC4(10,64);CC4(91,0);CC4(93,0);
            shortIn((uint)(0xC0|(pg4<<8)),0); flush();
            shortIn((uint)(0x90|(nt4<<8)|(vl4<<16)),0); flush();
            int nt5=(int)(hs4*100), stride5=0x220;
            var dump=new byte[nt5*2*stride5]; var aud=new float[nt5*320];
            for(int t=0;t<nt5;t++){
                fixed(float* pl=l4,pr=r4) process(pl,pr,320);
                for(int i=0;i<320;i++) aud[t*320+i]=l4[i];
                for(int v=0;v<2;v++) for(int i=0;i<stride5;i++)
                    dump[(t*2+v)*stride5+i]=*(byte*)(vc4+(long)v*stride5+i);
            }
            shortIn((uint)(0x80|(nt4<<8)),0); flush();
            File.WriteAllBytes(csv4+".voice.bin", dump);
            var ab=new byte[aud.Length*4]; Buffer.BlockCopy(aud,0,ab,0,ab.Length);
            File.WriteAllBytes(csv4+".audio.f32", ab);
            Console.WriteLine($"  voice-struct dump: {nt5} ticks x 2 voices x 0x{stride5:X}");
        }
        Console.WriteLine($"tvftrace done: {csv4} prog={pg4} note={nt4} vel={vl4} ({hs4:0.0}s)");
        return;
    }

    // holdnote mode: play one note on one program and hold it, for TVF-sweep A/B.
    //   args: dll holdnote <prog> <note> <holdSec> <out.wav> [vel]
    if (args.Length > 1 && args[1] == "holdnote")
    {
        int SR3=32000; int pg=int.Parse(args[2]); int nt=int.Parse(args[3]);
        double hs=double.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture);
        string hw=args.Length>5?args[5]:"real_hold.wav"; int vl=args.Length>6?int.Parse(args[6]):100;
        int bkh=args.Length>7?int.Parse(args[7]):0;   // CC0 bank MSB
        int cc7=args.Length>8?int.Parse(args[8]):127; int cc11=args.Length>9?int.Parse(args[9]):127;
        setSR((float)SR3); setBS(512); activate((float)SR3,512); setThr();
        var l3=new float[512]; var r3=new float[512];
        GsReset(); flush(); fixed(float* pl=l3,pr=r3) for(int i=0;i<8;i++) process(pl,pr,512);
        void CCh(int c,int v)=>shortIn((uint)((0xB0|0)|(c<<8)|(v<<16)),0);
        CCh(0,bkh);CCh(32,0);CCh(7,cc7);CCh(10,64);CCh(11,cc11);CCh(91,0);CCh(93,0);
        shortIn((uint)(0xC0|(pg<<8)),0); flush();
        int total=(int)((hs+2.0)*SR3), offAt=(int)(hs*SR3);
        var outH=new float[total]; var outR3=new float[total];   // STEREO: partials can be panned
        shortIn((uint)(0x90|(nt<<8)|(vl<<16)),0); flush();
        int blk=64,pos=0; bool sent=false;
        while(pos<total){
            if(!sent && pos>=offAt){ shortIn((uint)(0x80|(nt<<8)),0); flush(); sent=true; }
            int nf=Math.Min(blk,total-pos);
            fixed(float* pl=l3,pr=r3) process(pl,pr,(uint)nf);
            for(int i=0;i<nf;i++){ outH[pos+i]=l3[i]; outR3[pos+i]=r3[i]; }
            pos+=nf;
        }
        float pkH=1e-9f;
        for(int i=0;i<total;i++){ pkH=Math.Max(pkH,Math.Abs(outH[i])); pkH=Math.Max(pkH,Math.Abs(outR3[i])); }
        { double sl=0,sr=0; for(int i=0;i<total;i++){ sl+=outH[i]*outH[i]; sr+=outR3[i]*outR3[i]; }
          Console.WriteLine($"  L/R rms = {Math.Sqrt(sl/total):0.00000} / {Math.Sqrt(sr/total):0.00000}"
                           +$"  identical={outH.AsSpan().SequenceEqual(outR3)}"); }
        var pcmH=new short[total]; float gH=0.92f/pkH;
        for(int i=0;i<total;i++) pcmH[i]=(short)Math.Clamp(outH[i]*gH*32767f,-32768f,32767f);
        { var st=new short[total*2]; for(int i=0;i<total;i++){ st[i*2]=pcmH[i]; st[i*2+1]=(short)Math.Clamp(outR3[i]*gH*32767f,-32768f,32767f); } WriteWavStereo(hw,st,SR3); }
        Console.WriteLine($"holdnote done: {hw} prog={pg} note={nt} ({total/(double)SR3:0.0}s, peak={pkH:0.000})");
        return;
    }
    // lfotrace mode: hold one note and dump the live LFO OBJECT state every control tick.
    //   The LFO objects are a pool of 128 x 0xa8 bytes reached through the accessor at
    //   module+0x5c340 (DAT_181a749d0 = &LAB_18005c340, line 36005 of the decompile).
    //   voices_control_update copies each object into a global scratch, runs lfo_update
    //   (type 1) or FUN_1800823b0 (type 2), then copies it back -- so reading the object
    //   between process() calls gives the settled per-tick state.
    //   Object layout (from partial_alloc_node + the scratch address map):
    //     +0x00 in-use  +0x02 type(1=common,2=per-partial)  +0x38 waveform sel
    //     +0x3a delay rate  +0x3c fade rate  +0x3e rate  +0x70 out  +0x72 PHASE
    //     +0x74 delay accum  +0x76 fade accum  +0x7c/+0x7e/+0x80 depths (TVA/TVF/pitch)
    //     +0x40 mod out TVA  +0x42 TVF  +0x44 pitch  +0x48 smoothed pitch
    //   This measures the LFO's phase increment as an INTEGER, with no pitch tracking.
    //   args: dll lfotrace <prog> <note> <holdSec> <out.csv> [vel]
    if (args.Length > 1 && args[1] == "lfotrace")
    {
        int SRl=32000; int pgl=int.Parse(args[2]); int ntl=int.Parse(args[3]);
        double hsl=double.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture);
        string csvl=args.Length>5?args[5]:"lfotrace.csv"; int vll=args.Length>6?int.Parse(args[6]):100;
        int bkl=args.Length>7?int.Parse(args[7]):0;   // CC0 bank MSB (the SFX "variations")
        setSR((float)SRl); setBS(512); activate((float)SRl,512); setThr();
        var getLFO=(delegate* unmanaged[Cdecl]<int,long>)(b+0x5c340);
        var ll=new float[512]; var rl=new float[512];
        GsReset(); flush(); fixed(float* pl=ll,pr=rl) for(int i=0;i<8;i++) process(pl,pr,512);
        void CCl(int c,int v)=>shortIn((uint)((0xB0|0)|(c<<8)|(v<<16)),0);
        CCl(0,bkl);CCl(32,0);CCl(7,127);CCl(10,64);CCl(91,0);CCl(93,0);
        shortIn((uint)(0xC0|(pgl<<8)),0); flush();
        long pool=getLFO(0);
        Console.WriteLine($"  LFO pool @ 0x{pool:X}");
        // voices_control_update (line 73547) does pcVar11 = pool+1 then tests pcVar11[1], i.e.
        // object+0x02 (the TYPE byte) -- that, not +0x00, is the "this object is live" test.
        {   int n0=0,n2=0;
            for(int o=0;o<128;o++){ long q=pool+(long)o*0xa8;
                if(*(byte*)(q+0x00)!=0) n0++; if(*(byte*)(q+0x02)!=0) n2++; }
            Console.WriteLine($"  before note-on: {n0} objects with +0x00 set, {n2} with type +0x02 set");
        }
        Console.WriteLine($"  prng_lfsr seeds at note-on: A=0x{*(ushort*)(b+0x1a6f630):X4} B=0x{*(ushort*)(b+0x1a6f634):X4}");
        var sbl=new System.Text.StringBuilder(
            "t_ms,obj,type,wavesel,rate,phase,out,delay_rate,delay_acc,fade_rate,fade_acc,"
           +"dep_tva,dep_tvf,dep_pitch,mod_tva,mod_tvf,mod_pitch,mod_pitch_sm\n");
        shortIn((uint)(0x90|(ntl<<8)|(vll<<16)),0); flush();
        int ticks=(int)(hsl*100);
        for(int t=0;t<ticks;t++){
            fixed(float* pl=ll,pr=rl) process(pl,pr,320);      // exactly one 100 Hz control tick
            for(int o=0;o<128;o++){
                long q=pool+(long)o*0xa8;
                if(*(byte*)(q+0x02)==0) continue;              // type 0 = not live
                sbl.Append($"{t*10},{o},{*(byte*)(q+0x02)},{*(byte*)(q+0x38)},"
                          +$"{*(ushort*)(q+0x3e)},{*(ushort*)(q+0x72)},{*(short*)(q+0x70)},"
                          +$"{*(ushort*)(q+0x3a)},{*(ushort*)(q+0x74)},"
                          +$"{*(ushort*)(q+0x3c)},{*(ushort*)(q+0x76)},"
                          +$"{*(short*)(q+0x7c)},{*(short*)(q+0x7e)},{*(short*)(q+0x80)},"
                          +$"{*(short*)(q+0x40)},{*(short*)(q+0x42)},{*(short*)(q+0x44)},"
                          +$"{*(short*)(q+0x48)}\n");
            }
        }
        shortIn((uint)(0x80|(ntl<<8)),0); flush();
        File.WriteAllText(csvl, sbl.ToString());
        Console.WriteLine($"lfotrace done: {csvl} prog={pgl} note={ntl} vel={vll} ({hsl:0.0}s, {ticks} ticks)");
        return;
    }

    // song mode: render a fixed note sequence (same as scvx_engine.py demo) through the real DLL,
    //   DRY (reverb/chorus off, vol 127, pan center), 32000 Hz mono, for A/B vs our engine.
    //   args: dll song <prog> <out.wav>
    if (args.Length > 1 && args[1] == "song")
    {
        int SR=32000; int pg=args.Length>2?int.Parse(args[2]):0;
        string songWav=args.Length>3?args[3]:"real_engine.wav";
        int map=args.Length>4?int.Parse(args[4]):0;   // 1=SC55 2=SC88 3=SC88Pro 4=SC8820; 0=default
        setSR((float)SR); setBS(512); activate((float)SR,512); setThr();
        if (map>=1 && map<=4){ GsReset(); for(int c=0;c<16;c++) ToneMap0(c,map);
            flush(); { var wl=new float[512]; var wr=new float[512]; fixed(float* pl=wl,pr=wr) for(int i=0;i<6;i++) process(pl,pr,512); } }
        // build the identical event list
        double[] onT={0,.34,.68,1.02,1.36,1.70,2.04,2.38}; int[] scale={60,62,64,65,67,69,71,72};
        var evs=new System.Collections.Generic.List<(int t,int on,int note,int vel)>();
        for(int i=0;i<8;i++){ int on=(int)(onT[i]*SR); evs.Add((on,1,scale[i],100)); evs.Add((on+(int)(0.30*SR),0,scale[i],0)); }
        int con=(int)((8*0.34+0.1)*SR), coff=con+(int)(1.6*SR);
        foreach(int n in new[]{60,64,67,72}){ evs.Add((con,1,n,96)); evs.Add((coff,0,n,0)); }
        evs.Sort((a,b)=>a.t-b.t);
        void CCs(int c,int v)=>shortIn((uint)((0xB0|0)|(c<<8)|(v<<16)),0);
        CCs(0,0);CCs(32,0);CCs(7,127);CCs(10,64);CCs(91,0);CCs(93,0);   // dry: reverb/chorus off
        shortIn((uint)(0xC0|(pg<<8)),0); flush();
        int total=(int)(5.9*SR); var outL=new float[total];
        var sL=new float[512]; var sR=new float[512]; int blk=64, ei=0, pos=0;
        while(pos<total){
            while(ei<evs.Count && evs[ei].t < pos+blk){ var e=evs[ei++];
                if(e.on==1) shortIn((uint)((0x90)|(e.note<<8)|(e.vel<<16)),0);
                else        shortIn((uint)((0x80)|(e.note<<8)),0); }
            flush();
            int nf=Math.Min(blk,total-pos);
            fixed(float* pl=sL,pr=sR) process(pl,pr,(uint)nf);
            for(int i=0;i<nf;i++) outL[pos+i]=sL[i];
            pos+=nf;
        }
        float peak=1e-9f; foreach(var v in outL) peak=Math.Max(peak,Math.Abs(v));
        float g=0.92f/peak; var pcm=new short[total];
        for(int i=0;i<total;i++) pcm[i]=(short)Math.Clamp(outL[i]*g*32767f,-32768f,32767f);
        WriteWav(songWav,pcm,SR);
        Console.WriteLine($"song done: {songWav} ({total/(double)SR:0.0}s, peak={peak:0.000})");
        return;
    }

    // ---------------------------------------------------------------------------------------
    // smf: render an arbitrary Standard MIDI File through the real engine.
    //
    // STATUS: working, and measured. Against a reimplementation's 64-voice render of canyon.mid:
    //
    //     envelope correlation   4 ms 0.775   20 ms 0.900   250 ms 0.919   1 s 0.938
    //     level                  -0.30 dB
    //     sample correlation     0.047
    //
    // The last of those is the one to ignore. Dense passages decorrelate sample-by-sample through
    // beating between simultaneous notes while measuring correct on an envelope, which is why the
    // verification notes quote a passage judged *correct* at 0.72 on a 4 ms envelope rising to
    // 0.91 at 250 ms, with level within 0.5 dB. These figures are better than that on every count,
    // so the harness and the engine it was measured against agree about as well as this comparison
    // can show.
    //
    // Feed the DLL on its own 320-sample block (see BlockFrames) or none of this holds.

    if (args.Length > 1 && args[1] == "smf")
    {
        string midiPath = args.Length > 2 ? args[2] : "song.mid";
        string wavPath  = args.Length > 3 ? args[3] : "real_engine_song.wav";
        int map         = args.Length > 4 ? int.Parse(args[4]) : 4;
        double tailSec  = args.Length > 5 ? double.Parse(args[5]) : 2.2;
        bool pinPhase   = args.Length > 6 && args[6] == "pin";
        const int SR = 32000;

        // The core renders in 320-sample blocks -- 10 ms at 32 kHz, its 100 Hz control tick -- and
        // asked for any other count it chunks internally, taking pending events only at the start
        // of each of its own blocks. Feeding on a finer grid therefore does not place events more
        // precisely; it places them at the same moments while making this harness *believe* they
        // landed elsewhere. Matching the core's block is what makes an event's position mean the
        // same thing on both sides.
        const int BlockFrames = 320;

        byte[] smf;
        System.Collections.Generic.List<SmfEvent> events;
        double songSeconds;
        try
        {
            smf = File.ReadAllBytes(midiPath);
            events = Smf.Parse(smf, SR, out songSeconds);
        }
        catch (Exception ex)
        {
            // Report and exit rather than throwing. An unhandled exception here pops a crash
            // dialog under wine, which is a poor way to learn that a generated probe is malformed
            // -- and generated probes are how this harness is mostly driven.
            Console.Error.WriteLine($"cannot read {midiPath}: {ex.Message}");
            Environment.Exit(2);
            return;
        }
        Console.WriteLine($"{Path.GetFileName(midiPath)}: {events.Count} events, {songSeconds:F2} s");

        setSR((float)SR); setBS(512); activate((float)SR, 512); setThr();
        GsReset();
        if (map >= 1 && map <= 4) { for (int c = 0; c < 16; c++) ToneMap0(c, map); }
        flush();
        { var wl = new float[512]; var wr = new float[512];
          fixed (float* pl = wl, pr = wr) for (int i = 0; i < 6; i++) process(pl, pr, 512); }

        // Phase pinning -- EXPERIMENTAL, and its validation failed: pre-rolling this register to
        // ~0 against a reimplementation whose accumulator starts at 0 still leaves a ~2.7 dB wet
        // difference at r 0.73, so register-zero is NOT the same origin as accumulator-zero and
        // the convention is unresolved. The deterministic phase READ is trustworthy (identical
        // across runs); the pre-roll target is not. Kept because the read is the prerequisite for
        // anyone resolving the convention; do not treat pinned output as phase-matched.
        //
        // Background: the chorus LFO free-runs from an engine-internal origin, and its phase at
        // song start is what makes two engines' wets non-comparable -- the level of a windowed wet
        // measurement is a function of the phase offset (see FINDINGS: the "1.17 dB chorus
        // deficit" was entirely this). A reimplementation whose chorus starts at phase 0 with the
        // stream is matched by pre-rolling this engine until its phase wraps to ~0 before the song.
        //
        // Phase advances 192 per sample into 24 bits, so only the residue mod 64 is unreachable:
        // the pre-roll lands within +-32 phase units, 0.0002 samples of tap. The read is the same
        // process-memory address `chodump` reports as `L lfoPhase`.
        {
            long PV(long va) => b + (va - 0x180000000L);
            int phase = *(int*)PV(0x181a62af8L);
            int lfoInc = *(int*)PV(0x181a62afcL);
            Console.WriteLine($"chorus lfoPhase at song start = {phase} (inc {lfoInc})");
            if (pinPhase && lfoInc > 0)
            {
                const int WRAP = 1 << 24;
                long n0 = ((long)WRAP - phase) / lfoInc;
                long best = n0; long bestAbs = long.MaxValue;
                for (long n = Math.Max(0, n0 - 2); n <= n0 + 2; n++)
                {
                    long residue = (phase + n * lfoInc) % WRAP;
                    long dist = Math.Min(residue, WRAP - residue);
                    if (dist < bestAbs) { bestAbs = dist; best = n; }
                }
                var zl = new float[512]; var zr = new float[512];
                long left = best;
                fixed (float* pl = zl, pr = zr)
                    while (left > 0) { uint c = (uint)Math.Min(512, left); process(pl, pr, c); left -= c; }
                int after = *(int*)PV(0x181a62af8L);
                Console.WriteLine($"pinned: pre-rolled {best} samples, lfoPhase now {after}");
            }
            else if (pinPhase)
            {
                Console.WriteLine("pin requested but the chorus LFO is not running; skipped");
            }
        }

        int total = (int)((songSeconds + tailSec) * SR);
        var outL = new float[total]; var outR = new float[total];
        var sL = new float[BlockFrames]; var sR = new float[BlockFrames];
        int blk = BlockFrames, ei = 0, pos = 0;

        while (pos < total)
        {
            while (ei < events.Count && events[ei].At < pos + blk)
            {
                var e = events[ei++];
                if (e.Bytes != null) { fixed (byte* mp = e.Bytes) longIn(mp, 0); }
                else shortIn((uint)(e.Status | (e.D1 << 8) | (e.D2 << 16)), 0);
            }
            flush();
            int nf = Math.Min(blk, total - pos);
            fixed (float* pl = sL, pr = sR) process(pl, pr, (uint)nf);
            for (int i = 0; i < nf; i++) { outL[pos + i] = sL[i]; outR[pos + i] = sR[i]; }
            pos += nf;
        }

        // Fixed gain rather than per-file normalisation: an oracle whose level depends on its own
        // peak cannot be compared across files, or against anything else.
        var pcm = new short[total * 2];
        for (int i = 0; i < total; i++)
        {
            pcm[i * 2]     = (short)Math.Clamp(outL[i] * 32767f, -32768f, 32767f);
            pcm[i * 2 + 1] = (short)Math.Clamp(outR[i] * 32767f, -32768f, 32767f);
        }
        WriteWavStereo(wavPath, pcm, SR);
        Console.WriteLine($"wrote {wavPath}");
        return;
    }
    // seq mode: play a TIMED event script through the DLL -> STEREO dry WAV (fixed gain, no per-file
    //   normalize, so absolute level is preserved). This is the ground truth for scvx_sequencer.py.
    //   args: dll seq <script.txt> <out.wav> [map] [tailSec] [wet]
    //   script lines (decimal ints unless noted; '#' comment; blank ignored):
    //       <samplePos> <status> [data1] [data2]      short MIDI message (status incl. channel nibble)
    //       <samplePos> sx <hex> <hex> ...             sysex bytes in hex (include F0..F7)
    //   Events are sent at 32-sample block boundaries (>= their samplePos); scvx_sequencer.py quantizes
    //   to the same grid so both sides align. Reverb/chorus forced OFF on all 16 parts (engine is dry)
    //   UNLESS wet=1, in which case CC91/CC93 are left to the script (for reverb/chorus A/B).
    if (args.Length > 1 && args[1] == "seq")
    {
        int SR=32000;
        string scriptPath = args.Length>2 ? args[2] : "seq.txt";
        string seqOut     = args.Length>3 ? args[3] : "real_seq.wav";
        int map           = args.Length>4 ? int.Parse(args[4]) : 4;   // default SC-8820 (matches engine)
        double tailSec    = args.Length>5 ? double.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture) : 2.2;
        bool wet          = args.Length>6 && (args[6]=="1"||args[6]=="wet");
        setSR((float)SR); setBS(512); activate((float)SR,512); setThr();
        // GS reset + explicit tone map on every part, then settle.
        GsReset(); if(map>=1 && map<=4){ for(int c=0;c<16;c++) ToneMap0(c,map); }
        if(!wet) for(int c=0;c<16;c++){ shortIn((uint)((0xB0|c)|(91<<8)|(0<<16)),0); shortIn((uint)((0xB0|c)|(93<<8)|(0<<16)),0); }
        flush();
        { var wl=new float[512]; var wr=new float[512]; fixed(float* pl=wl,pr=wr) for(int i=0;i<6;i++) process(pl,pr,512); }
        // parse the script
        var shortEvs=new System.Collections.Generic.List<(int t,uint msg)>();
        var sysExEvs=new System.Collections.Generic.List<(int t,byte[] data)>();
        int maxPos=0;
        foreach(var rawLine in File.ReadAllLines(scriptPath)){
            var line=rawLine.Trim(); if(line.Length==0 || line[0]=='#') continue;
            var tok=line.Split(new[]{' ','\t'}, StringSplitOptions.RemoveEmptyEntries);
            int pos=int.Parse(tok[0]); if(pos>maxPos) maxPos=pos;
            if(tok[1]=="sx"||tok[1]=="SX"){
                var bytes=new byte[tok.Length-2];
                for(int i=2;i<tok.Length;i++) bytes[i-2]=Convert.ToByte(tok[i],16);
                sysExEvs.Add((pos,bytes));
            } else {
                uint status=(uint)int.Parse(tok[1]);
                uint d1=tok.Length>2?(uint)int.Parse(tok[2]):0;
                uint d2=tok.Length>3?(uint)int.Parse(tok[3]):0;
                shortEvs.Add((pos, status | (d1<<8) | (d2<<16)));
            }
        }
        shortEvs.Sort((a,b)=>a.t-b.t); sysExEvs.Sort((a,b)=>a.t-b.t);
        int total=maxPos+(int)(tailSec*SR);
        var outL=new float[total]; var outR=new float[total];
        var sL=new float[512]; var sR=new float[512];
        int blk=32, si=0, xi=0, pos2=0;
        while(pos2<total){
            while(si<shortEvs.Count && shortEvs[si].t < pos2+blk) shortIn(shortEvs[si++].msg,0);
            while(xi<sysExEvs.Count && sysExEvs[xi].t < pos2+blk){ var d=sysExEvs[xi++].data; fixed(byte* dp=d) longIn(dp,0); }
            flush();
            int nf=Math.Min(blk,total-pos2);
            fixed(float* pl=sL,pr=sR) process(pl,pr,(uint)nf);
            for(int i=0;i<nf;i++){ outL[pos2+i]=sL[i]; outR[pos2+i]=sR[i]; }
            pos2+=nf;
        }
        float peak2=1e-9f; for(int i=0;i<total;i++){ peak2=Math.Max(peak2,Math.Abs(outL[i])); peak2=Math.Max(peak2,Math.Abs(outR[i])); }
        var pcm2=new short[total*2];
        for(int i=0;i<total;i++){
            pcm2[2*i]  =(short)Math.Clamp(outL[i]*32767f,-32768f,32767f);
            pcm2[2*i+1]=(short)Math.Clamp(outR[i]*32767f,-32768f,32767f);
        }
        WriteWavStereo(seqOut,pcm2,SR);
        Console.WriteLine($"seq done: {seqOut} ({total/(double)SR:0.0}s stereo, {shortEvs.Count} short + {sysExEvs.Count} sysex, peak={peak2:0.0000})");
        return;
    }
    // revdump mode: after the default GS reverb is running, dump every reverb-network coefficient/tap
    //   (node structs + damping coefs + per-sample gains) so scvx can run the exact allpass/comb net.
    //   args: dll revdump <out.txt> [revType]
    if (args.Length > 1 && args[1] == "revdump")
    {
        string rdOut = args.Length>2 ? args[2] : "revdump.txt";
        int revType = args.Length>3 ? int.Parse(args[3]) : -1;   // GS reverb macro 0..7 (Room1..PanDelay); -1 = GsReset default (Hall2)
        setSR(32000f); setBS(512); activate(32000f,512); setThr();
        GsReset();
        if(revType>=0) SendSysEx(Dt1(0x40,0x01,0x30,(byte)revType));   // REVERB MACRO -> presets character/pre-lpf/level/time/fb/predelay
        void CCr(int c,int v)=>shortIn((uint)((0xB0|0)|(c<<8)|(v<<16)),0);
        CCr(0,0);CCr(32,0);CCr(7,127);CCr(10,64);CCr(91,127);CCr(93,0);
        shortIn((uint)(0xC0|(12<<8)),0);   // marimba
        flush();
        var lrd=new float[512]; var rrd=new float[512];
        fixed(float* pl=lrd,pr=rrd) for(int i=0;i<8;i++) process(pl,pr,512);
        shortIn((uint)(0x90|(60<<8)|(100<<16)),0); flush();
        fixed(float* pl=lrd,pr=rrd) for(int i=0;i<16;i++) process(pl,pr,512);   // let reverb settle
        // helpers to read absolute VAs (module base b + (VA-0x180000000))
        long V(long va)=>b+(va-0x180000000L);
        float F(long va)=>*(float*)V(va);
        int I(long va)=>*(int*)V(va);
        long P(long va)=>*(long*)V(va);
        var sb=new System.Text.StringBuilder();
        void small(string name, long ptrVa){ long p=P(ptrVa);
            sb.AppendLine($"{name} writeTap={*(int*)(p+0):X} readTap={*(int*)(p+4):X} coefA={*(float*)(p+8):R} coefB={*(float*)(p+0xc):R}"); }
        small("ap0", 0x181a62ab0); small("ap1", 0x181a62ab8); small("ap2", 0x181a62ac0); small("ap3", 0x181a62ac8);
        void large(string name, long ptrVa){ long p=P(ptrVa);
            long sA0=*(long*)(p+0), sA1=*(long*)(p+8);
            sb.Append($"{name} tap10={*(int*)(p+0x10):X} tap14={*(int*)(p+0x14):X} tap18={*(int*)(p+0x18):X} tap1C={*(int*)(p+0x1c):X} ");
            sb.Append($"tap20={*(int*)(p+0x20):X} tap24={*(int*)(p+0x24):X} tap28={*(int*)(p+0x28):X} tap2C={*(int*)(p+0x2c):X} ");
            sb.AppendLine($"cA={*(float*)(p+0x30):R} cB={*(float*)(p+0x34):R}");
            sb.AppendLine($"{name}.sA0 writeTap={*(int*)(sA0+0):X} readTap={*(int*)(sA0+4):X} coefA={*(float*)(sA0+8):R} coefB={*(float*)(sA0+0xc):R}");
            sb.AppendLine($"{name}.sA1 writeTap={*(int*)(sA1+0):X} readTap={*(int*)(sA1+4):X} coefA={*(float*)(sA1+8):R} coefB={*(float*)(sA1+0xc):R}"); }
        large("LA", 0x181a62ad0); large("LB", 0x181a62ad8);
        sb.AppendLine($"injTap={I(0x181a62ae0):X}");
        sb.AppendLine($"damp aa8_fb={F(0x181a62aa8):R} aac_in={F(0x181a62aac):R}");
        sb.AppendLine($"gain ed70_in={F(0x181a6ed70):R} ee70_inj={F(0x181a6ee70):R} eef0_fb={F(0x181a6eef0):R} edf0_out={F(0x181a6edf0):R}");
        File.WriteAllText(rdOut, sb.ToString());
        Console.WriteLine(sb.ToString());
        Console.WriteLine($"revdump -> {rdOut}");
        return;
    }
    // chodump mode: after the default GS chorus is running (CC93), dump the chorus L+R stage state
    //   (LFO rate/phase, tap depths/bases, LPF/feedback coefs, gains) so scvx can run the swept delay.
    //   Dumps TWICE (a few blocks apart) so the LFO increment and any R-tap modulation are visible.
    //   args: dll chodump <out.txt>
    if (args.Length > 1 && args[1] == "chodump")
    {
        string cdOut = args.Length>2 ? args[2] : "chodump.txt";
        int choType = args.Length>3 ? int.Parse(args[3]) : -1;   // GS chorus macro 0..7 (Chorus1..ShortDelayFB); -1 = GsReset default (Chorus3)
        setSR(32000f); setBS(512); activate(32000f,512); setThr();
        GsReset();
        if(choType>=0) SendSysEx(Dt1(0x40,0x01,0x38,(byte)choType));   // CHORUS MACRO -> presets pre-lpf/level/fb/delay/rate/depth
        void CCc(int c,int v)=>shortIn((uint)((0xB0|0)|(c<<8)|(v<<16)),0);
        CCc(0,0);CCc(32,0);CCc(7,127);CCc(10,64);CCc(91,0);CCc(93,127);
        shortIn((uint)(0xC0|(48<<8)),0);   // strings
        flush();
        var lcd=new float[512]; var rcd=new float[512];
        fixed(float* pl=lcd,pr=rcd) for(int i=0;i<8;i++) process(pl,pr,512);
        shortIn((uint)(0x90|(60<<8)|(100<<16)),0); flush();
        fixed(float* pl=lcd,pr=rcd) for(int i=0;i<8;i++) process(pl,pr,512);
        long V(long va)=>b+(va-0x180000000L);
        float F(long va)=>*(float*)V(va);
        int I(long va)=>*(int*)V(va);
        short S(long va)=>*(short*)V(va);
        var sb=new System.Text.StringBuilder();
        void snap(string tag){
            sb.AppendLine($"# snapshot {tag}");
            sb.AppendLine($"L lfoPhase={I(0x181a62af8)} lfoInc={I(0x181a62afc)} lpfA={F(0x181a62af0):R} lpfB={F(0x181a62af4):R}");
            sb.AppendLine($"L tap1 depth={S(0x181a62b00)} base={I(0x181a62b04)}  tap2 depth={S(0x181a62b02)} base={I(0x181a62b08)}  fbCoef={F(0x181a62b10):R}");
            sb.AppendLine($"L gains writeIn={F(0x181a6ef70):R} revSend={F(0x181a6eff0):R} toR={F(0x181a6f070):R} tapOut={F(0x181a6f0f0):R}");
            sb.AppendLine($"R lpfPole={F(0x181a629e8):R} lpfGain={F(0x181a629ec):R} fbCoef={F(0x181a62a24):R}");
            sb.AppendLine($"R taps t0={I(0x181a629f4):X} t1={I(0x181a629f8):X} t2={I(0x181a629fc):X}  c0={F(0x181a62a2c):R} c1={F(0x181a62a28):R} c2={F(0x181a62a30):R}");
            sb.AppendLine($"R gains tapOut={F(0x181a6f170):R} writeIn={F(0x181a6f1f0):R} revSend={F(0x181a6f270):R}");
        }
        snap("A");
        fixed(float* pl=lcd,pr=rcd) for(int i=0;i<40;i++) process(pl,pr,512);   // ~0.64s later
        snap("B");
        File.WriteAllText(cdOut, sb.ToString());
        Console.WriteLine(sb.ToString());
        Console.WriteLine($"chodump -> {cdOut}");
        return;
    }
    // delaytest mode: GO/NO-GO -- is the GS *system* Delay (macro 40 01 50, distinct from the reverb
    //   Delay/PanDelay types) actually processed in GS mode? Set delay macro + delay return level +
    //   part delay-send (SysEx 40 11 2C, no CC exists), play a marimba stab, render a dry-ish WAV and
    //   look for repeats. args: dll delaytest <out.wav> [type]
    if (args.Length > 1 && args[1] == "delaytest")
    {
        int SR=32000;
        string dtOut = args.Length>2 ? args[2] : "delaytest.wav";
        int dType = args.Length>3 ? int.Parse(args[3]) : 0;   // GS delay macro 0..9 (Delay1..PanRepeat)
        int dSend = args.Length>4 ? int.Parse(args[4]) : 127; // ch0 part DELAY SEND LEVEL (0 = dry ref)
        setSR((float)SR); setBS(512); activate((float)SR,512); setThr();
        // dump the 10 macro presets (10 raw GS params each: pre-lpf,timeC,ratioL,ratioR,lvlC,lvlL,lvlR,level,fb,sendRev)
        { long pt=b+(0x181893930L-0x180000000L); var sbP=new System.Text.StringBuilder("g_delay_preset_tbl (10 types x 10 raw bytes):\n");
          for(int t=0;t<10;t++){ sbP.Append($"  type{t}: "); for(int k=0;k<10;k++) sbP.Append($"{*(byte*)(pt+t*10+k):D3} "); sbP.Append('\n'); }
          Console.Write(sbP.ToString()); }
        GsReset(); for(int c=0;c<16;c++) ToneMap0(c,4);
        SendSysEx(Dt1(0x40,0x01,0x50,(byte)dType));   // DELAY MACRO -> presets time/ratios/levels/fb/return(64)
        SendSysEx(Dt1(0x40,0x11,0x2C,(byte)dSend));   // ch0 part DELAY SEND LEVEL (return level left at macro default 64)
        void CCd(int c,int v)=>shortIn((uint)((0xB0|0)|(c<<8)|(v<<16)),0);
        CCd(7,127);CCd(10,64);CCd(91,0);CCd(93,0);
        shortIn((uint)(0xC0|(12<<8)),0);              // marimba (percussive -> repeats obvious)
        flush();
        var ldt=new float[512]; var rdt=new float[512];
        fixed(float* pl=ldt,pr=rdt) for(int i=0;i<8;i++) process(pl,pr,512);
        shortIn((uint)(0x90|(60<<8)|(110<<16)),0); flush();
        shortIn((uint)(0x80|(60<<8)),0);
        int total=SR*3; var oL=new float[total]; var oR=new float[total];
        int done=0;
        while(done<total){ int nf=Math.Min(512,total-done);
            fixed(float* pl=ldt,pr=rdt) process(pl,pr,(uint)nf);
            for(int i=0;i<nf;i++){ oL[done+i]=ldt[i]; oR[done+i]=rdt[i]; } done+=nf; }
        // per-100ms RMS envelope -> repeats show as secondary humps after the initial decay
        Console.WriteLine($"delaytest type={dType}: per-100ms RMS (x1000):");
        var sbE=new System.Text.StringBuilder();
        for(int s=0;s*SR/10<total;s++){ int a=s*SR/10, b2=Math.Min(a+SR/10,total); double e=0;
            for(int i=a;i<b2;i++){ e+=(double)oL[i]*oL[i]+(double)oR[i]*oR[i]; }
            e=Math.Sqrt(e/(b2-a)/2)*1000; sbE.Append($"{e:0.0} "); }
        Console.WriteLine(sbE.ToString());
        var pcmD=new short[total*2];
        for(int i=0;i<total;i++){ pcmD[2*i]=(short)Math.Clamp(oL[i]*32767f,-32768f,32767f); pcmD[2*i+1]=(short)Math.Clamp(oR[i]*32767f,-32768f,32767f); }
        WriteWavStereo(dtOut,pcmD,SR);
        Console.WriteLine($"delaytest -> {dtOut}");
        return;
    }
    // lfo mode: read the per-part LFO the voice points at, over time, with vibrato on (CC1).
    //   LFO1 = *(short*)(*(long*)(voice+0x170)); LFO2 = *(short*)(*(long*)(voice+0x180)+0x40);
    //   LFOc = *(char*)(*(long*)(voice+0x198)).  args: dll lfo <prog> <note> <mod> <framesPerStep> <steps>
    if (args.Length > 1 && args[1] == "lfo")
    {
        int pg=args.Length>2?int.Parse(args[2]):73, nt=args.Length>3?int.Parse(args[3]):72;
        int mod=args.Length>4?int.Parse(args[4]):127; uint fps=args.Length>5?(uint)int.Parse(args[5]):16;
        int steps=args.Length>6?int.Parse(args[6]):400;
        setSR(32000f); setBS(512); activate(32000f,512); setThr();
        long fb=b+0x1a1b5b8;
        var getVC=(delegate* unmanaged[Cdecl]<int,long>)(b+0x5c360); long vc=getVC(0);
        var l=new float[512]; var r=new float[512];
        void CCc(int c,int v)=>shortIn((uint)((0xB0|0)|(c<<8)|(v<<16)),0);
        CCc(0,0);CCc(32,0);CCc(7,127);CCc(10,64);CCc(91,0);CCc(93,0);CCc(1,mod);
        shortIn((uint)(0xC0|(pg<<8)),0);
        shortIn((uint)(0x90|(nt<<8)|(100<<16)),0); flush();
        for(int i=0;i<8;i++){ fixed(float* pl=l,pr=r) process(pl,pr,fps); }
        // pick active CONTROL voice (running env), like calib mode
        int tv=-1; for(int v=0;v<64;v++){ long ps0=vc+(long)v*0x220; byte st=*(byte*)(ps0+0xc);
            if(st!=4 && (*(ushort*)(ps0+0x12)!=0 || *(ushort*)(ps0+0x18)!=0)){ tv=v; break; } }
        if(tv>=0){ long ps0=vc+(long)tv*0x220;
            Console.WriteLine($"tracking control voice {tv}; mod(CC1)={mod}; ptrs 0x170=0x{*(long*)(ps0+0x170):X} 0x180=0x{*(long*)(ps0+0x180):X} 0x198=0x{*(long*)(ps0+0x198):X}"); }
        else Console.WriteLine("no active control voice");
        Console.WriteLine("step,frames,lfo1,lfo2,lfoc,pinc");
        long cum=0;
        for(int s=0;s<steps && tv>=0;s++){
            fixed(float* pl=l,pr=r) process(pl,pr,fps); cum+=fps;
            long ps=vc+(long)tv*0x220;
            long p1=*(long*)(ps+0x170), p2=*(long*)(ps+0x180), pcp=*(long*)(ps+0x198);
            int lfo1 = p1!=0 ? *(short*)p1 : 0;
            int lfo2 = p2!=0 ? *(short*)(p2+0x40) : 0;
            int lfoc = pcp!=0 ? *(sbyte*)pcp : 0;
            int pinc = *(int*)(ps+0x8c);   // pitch offset (vibrato target)
            Console.WriteLine($"{s},{cum},{lfo1},{lfo2},{lfoc},{pinc}");
        }
        return;
    }
    // filt mode: cutoff->Hz calibration. Play a bright saw note; sweep CC74 (brightness->TVF cutoff);
    //   for each: render steady-state PCM to <outdir>/filt_<cc>.f32, read live cutoff (voice+0x1f0).
    //   args: dll filt <prog> <note> <outdir>
    if (args.Length > 1 && args[1] == "filt")
    {
        int pg=args.Length>2?int.Parse(args[2]):81, nt=args.Length>3?int.Parse(args[3]):60;
        string outdir=args.Length>4?args[4]:".";
        Directory.CreateDirectory(outdir);
        setSR(32000f); setBS(512); activate(32000f,512); setThr();
        long fb=b+0x1a1b5b8;
        var getVC=(delegate* unmanaged[Cdecl]<int,long>)(b+0x5c360);
        long vc=getVC(0);
        var l=new float[512]; var r=new float[512];
        void CCc(int c,int v)=>shortIn((uint)((0xB0|0)|(c<<8)|(v<<16)),0);
        int[] ccs={127,112,96,80,72,64,56,48,40,32,24};
        var man=new System.Text.StringBuilder("cc74,cutoff,type,cutStart,cutEnd,file\n");
        int warm=8192, cap=32768;
        foreach(int cc in ccs){
            // silence everything, settle
            CCc(120,0); CCc(123,0); flush();
            for(int i=0;i<40;i++){ fixed(float* pl=l,pr=r) process(pl,pr,512); }
            CCc(0,0);CCc(32,0);CCc(7,127);CCc(10,64);CCc(91,0);CCc(93,0);CCc(74,cc);
            shortIn((uint)(0xC0|(pg<<8)),0);
            shortIn((uint)(0x90|(nt<<8)|(100<<16)),0); flush();
            // warmup past attack
            for(int i=0;i<warm/512;i++){ fixed(float* pl=l,pr=r) process(pl,pr,512); }
            // find active voice + read cutoff
            int tv=-1; for(int v=0;v<64;v++){ if((*(byte*)(fb+v*0x50)&1)!=0){ tv=v; break; } }
            int cutStart = tv>=0 ? *(ushort*)(vc+(long)tv*0x220+0x1f0) : -1;
            int ftype    = tv>=0 ? *(byte*)(vc+(long)tv*0x220+0x1f5) : -1;
            // capture
            var buf=new float[cap]; int got=0;
            while(got<cap){ fixed(float* pl=l,pr=r) process(pl,pr,512);
                for(int i=0;i<512 && got<cap;i++) buf[got++]=l[i]; }
            int cutEnd = tv>=0 ? *(ushort*)(vc+(long)tv*0x220+0x1f0) : -1;
            if(tv>=0){ var vbytes=new byte[0x220]; for(int i=0;i<0x220;i++) vbytes[i]=*(byte*)(vc+(long)tv*0x220+i);
                File.WriteAllBytes(Path.Combine(outdir,$"filt_{cc}_voice.bin"), vbytes); }
            string fn=Path.Combine(outdir,$"filt_{cc}.f32");
            var bytes=new byte[cap*4]; Buffer.BlockCopy(buf,0,bytes,0,bytes.Length); File.WriteAllBytes(fn,bytes);
            shortIn((uint)(0x80|(nt<<8)),0); flush();
            man.Append($"{cc},{cutStart},{ftype},{cutStart},{cutEnd},{Path.GetFileName(fn)}\n");
            Console.WriteLine($"cc74={cc} voice={tv} cutoff={cutStart}(->{cutEnd}) type={ftype}");
        }
        File.WriteAllText(Path.Combine(outdir,"filt_manifest.csv"), man.ToString());
        Console.WriteLine("filt done"); return;
    }
    // mapall mode: read tones.txt (lines "bankCC page prog"), emit zone CSV for every tone.
    //   Per tone: find velocity-layer boundaries (sweep vel at a live ref note), then key zones per layer.
    //   args: dll mapall <tones.txt> <out.csv>
    if (args.Length > 1 && args[1] == "mapall")
    {
        var lines = File.ReadAllLines(args[2]);
        long fbA = b + 0x1a1b5b8;
        var l5=new float[512]; var r5=new float[512];
        var sb = new System.Text.StringBuilder("module,page,prog,velLo,velHi,keyLo,keyHi,layer,wave_ctrl,region,bank,reverse,loop,end,start\n");
        // Read ALL active voices for a note; return a canonical set-key + the per-voice records.
        var buf = new System.Collections.Generic.List<(uint wc,int lp,int en,int st)>();
        int ActiveCount(){ int c=0; for(int v=0;v<64;v++) if((*(byte*)(fbA+v*0x50)&1)!=0) c++; return c; }
        var bgw = new System.Collections.Generic.HashSet<(uint,int)>();
        string VoiceSet(int ch,int nt,int vel){
            // clear + wait, then snapshot the BACKGROUND wave-set (any phantom/stuck voices still sounding)
            shortIn((uint)((0xB0|ch)|(120<<8)|(0<<16)),0); shortIn((uint)((0xB0|ch)|(123<<8)|(0<<16)),0);
            flush();
            for(int i=0;i<60;i++){ fixed(float* pl=l5,pr=r5) process(pl,pr,512); if(ActiveCount()==0) break; }
            bgw.Clear();
            for(int v=0;v<64;v++) if((*(byte*)(fbA+v*0x50)&1)!=0) bgw.Add((*(uint*)(b+0x1a6fb60+v*4),*(int*)(b+0x1a6fc60+v*4)));
            // play note; keep only voices whose wave was NOT already sounding (excludes stuck phantoms)
            shortIn((uint)((0x90|ch)|(nt<<8)|(vel<<16)),0); flush();
            fixed(float* pl=l5,pr=r5) for(int i=0;i<3;i++) process(pl,pr,512);
            buf.Clear();
            for(int v=0;v<64;v++){ if((*(byte*)(fbA+v*0x50)&1)==0) continue;
                uint wc=*(uint*)(b+0x1a6fb60+v*4); int lp=*(int*)(b+0x1a6fc60+v*4);
                if(bgw.Contains((wc,lp))) continue;
                buf.Add((wc,lp,*(int*)(b+0x1a6fd60+v*4),*(int*)(b+0x1a6fe60+v*4))); }
            shortIn((uint)((0x80|ch)|(nt<<8)),0);
            buf.Sort((x,y)=> x.wc!=y.wc ? x.wc.CompareTo(y.wc) : x.lp.CompareTo(y.lp));
            if (buf.Count==0) return "-";
            var s=new System.Text.StringBuilder(); foreach(var e in buf) s.Append($"{e.wc:X4}:{e.lp};");
            return s.ToString();
        }
        void Emit(string module,int page,int prog,int vlo,int vhi,int klo,int khi){
            for(int i=0;i<buf.Count;i++){ var e=buf[i];
                sb.Append($"{module},{page},{prog},{vlo},{vhi},{klo},{khi},{i},{e.wc:X4},{e.wc&0x7f},{(e.wc>>4)&1},{((e.wc&0x800)!=0?1:0)},{e.lp},{e.en},{e.st}\n"); }
        }
        int done=0; string curGroup=null;
        foreach (var line in lines){
            var p=line.Split(' '); if(p.Length<3) continue;
            // line format: "module page prog"  (module = GM | GM2 | SC)
            string module=p[0]; int page=int.Parse(p[1]), prog=int.Parse(p[2]);
            int ch=0;
            string group=module+":"+page;
            if (group!=curGroup){   // mode + map setup once per (module,page) group, then settle >=50ms
                curGroup=group;
                if (module=="GM") Gm1On();
                else if (module=="GM2") Gm2On();
                else { GsReset(); if (page>=1 && page<=4) for(int c=0;c<16;c++) ToneMap0(c,page); }
                flush(); fixed(float* pl=l5,pr=r5) for(int i=0;i<6;i++) process(pl,pr,512);
            }
            int cc0 = (module=="GM2") ? page : 0;   // GM2 melodic bank via CC0; GM/SC use CC0=0
            shortIn((uint)((0xB0|ch)|(0<<8)|(cc0<<16)),0); shortIn((uint)((0xB0|ch)|(32<<8)|(0<<16)),0);
            shortIn((uint)((0xB0|ch)|(7<<8)|(127<<16)),0); shortIn((uint)((0xB0|ch)|(10<<8)|(64<<16)),0);
            shortIn((uint)((0xB0|ch)|(91<<8)|(0<<16)),0); shortIn((uint)((0xB0|ch)|(93<<8)|(0<<16)),0);
            shortIn((uint)((0xC0|ch)|(prog<<8)),0); flush();
            int refn=-1;
            foreach(int cand in new[]{60,55,67,48,72,43,79,36,84}){ if(VoiceSet(ch,cand,110)!="-"){ refn=cand; break; } }
            if (refn<0){ done++; continue; }
            var vbounds=new System.Collections.Generic.List<(int lo,int hi)>();
            string prev=null; int vstart=1;
            for(int vel=1;vel<=127;vel++){ string k=VoiceSet(ch,refn,vel);
                if(k!=prev){ if(prev!=null) vbounds.Add((vstart,vel-1)); prev=k; vstart=vel; } }
            vbounds.Add((vstart,127));
            foreach(var vb in vbounds){
                int rv=(vb.lo+vb.hi)/2; string pw=null; int ks=0;
                var zoneVoices=new System.Collections.Generic.List<(uint wc,int lp,int en,int st)>();
                for(int nt=0;nt<128;nt++){
                    string k=VoiceSet(ch,nt,rv);
                    var cur=new System.Collections.Generic.List<(uint wc,int lp,int en,int st)>(buf); // snapshot THIS note
                    if(k!=pw){
                        if(pw!=null && pw!="-"){ buf.Clear(); buf.AddRange(zoneVoices); Emit(module,page,prog,vb.lo,vb.hi,ks,nt-1); }
                        pw=k; ks=nt; zoneVoices=cur;
                    }
                }
                if(pw!=null && pw!="-"){ buf.Clear(); buf.AddRange(zoneVoices); Emit(module,page,prog,vb.lo,vb.hi,ks,127); }
            }
            done++;
            if(done%25==0) Console.WriteLine($"...{done}/{lines.Length}");
        }
        File.WriteAllText(args[3], sb.ToString());
        Console.WriteLine($"mapall done: {done} tones");
        return;
    }
    // map mode: for one program, sweep all notes x velocities -> wave per cell (multisample structure)
    //   args: dll map <program> <bankMsb> <ch>
    if (args.Length > 1 && args[1] == "map")
    {
        int pg = int.Parse(args[2]); int msb = args.Length>3?int.Parse(args[3]):0; int ch = args.Length>4?int.Parse(args[4]):0;
        long fbM = b + 0x1a1b5b8;
        var l4=new float[512]; var r4=new float[512];
        int[] vels = {8,32,64,96,127};
        Console.WriteLine("note," + string.Join(",", System.Linq.Enumerable.Select(vels, v=>"v"+v)));
        void CCm(int c,int v)=>shortIn((uint)((0xB0|ch)|(c<<8)|(v<<16)),0);
        CCm(0,msb); CCm(32,0); CCm(7,127); CCm(10,64); CCm(91,0); CCm(93,0);
        shortIn((uint)((0xC0|ch)|(pg<<8)),0);
        for (int nt=0; nt<128; nt++)
        {
            var cells=new System.Collections.Generic.List<string>();
            foreach(int vel in vels){
                shortIn((uint)((0x90|ch)|(nt<<8)|(vel<<16)),0); flush();
                fixed(float* pl=l4,pr=r4) for(int i=0;i<2;i++) process(pl,pr,512);
                string cell="-";
                for(int v=0;v<64;v++){ if((*(byte*)(fbM+v*0x50)&1)==0) continue;
                    uint wc=*(uint*)(b+0x1a6fb60+v*4); int lp=*(int*)(b+0x1a6fc60+v*4);
                    cell=$"{wc:X4}:{lp}"; break; }
                cells.Add(cell);
                shortIn((uint)((0x80|ch)|(nt<<8)),0); flush();
                fixed(float* pl=l4,pr=r4) process(pl,pr,512);
            }
            Console.WriteLine($"{nt}," + string.Join(",", cells));
        }
        Console.WriteLine("map done"); return;
    }
    // enum mode: enumerate every wave (bank/prog/note) -> unique {wave_ctrl,loop,end,start} directory
    if (args.Length > 1 && args[1] == "enum")
    {
        long fbE = b + 0x1a1b5b8;
        var seen = new System.Collections.Generic.HashSet<string>();
        var rows = new System.Collections.Generic.List<string>();
        var l3=new float[512]; var r3=new float[512];
        (int ch,int msb)[] banks = { (0,0),(0,1),(0,8),(9,0) };
        foreach (var bk in banks)
        for (int pg=0; pg<128; pg++)
        {
            void CCx(int c,int v)=>shortIn((uint)((0xB0|bk.ch)|(c<<8)|(v<<16)),0);
            CCx(0,bk.msb); CCx(32,0); CCx(7,127); CCx(10,64); CCx(91,0); CCx(93,0);
            shortIn((uint)((0xC0|bk.ch)|(pg<<8)),0);
            for (int nt=24; nt<=96; nt+=3)
            foreach (int vel in new[]{12,48,96,127})
            {
                shortIn((uint)((0x90|bk.ch)|(nt<<8)|(vel<<16)),0); flush();
                fixed(float* pl=l3,pr=r3) for(int i=0;i<2;i++) process(pl,pr,512);
                for (int v=0; v<64; v++){
                    if((*(byte*)(fbE+v*0x50)&1)==0) continue;
                    uint wc=*(uint*)(b+0x1a6fb60+v*4);
                    int lp=*(int*)(b+0x1a6fc60+v*4), en=*(int*)(b+0x1a6fd60+v*4), st=*(int*)(b+0x1a6fe60+v*4);
                    string key=$"{wc:X8}:{lp}:{en}:{st}";
                    if (seen.Add(key)) rows.Add($"{wc:X8},{wc&0x7f},{(wc>>4)&1},{((wc&0x800)!=0?1:0)},{lp},{en},{st}");
                }
                shortIn((uint)((0x80|bk.ch)|(nt<<8)),0); flush();
                fixed(float* pl=l3,pr=r3) process(pl,pr,512);
            }
        }
        var outp=new System.Text.StringBuilder("wave_ctrl,region,bank,reverse,loop_start,end,start\n");
        foreach(var r in rows) outp.AppendLine(r);
        File.WriteAllText(args.Length>2?args[2]:"wave_directory.csv", outp.ToString());
        Console.WriteLine($"enum done: {rows.Count} unique waves");
        return;
    }
    // scan mode: find which programs/notes use the reverse codec (runflag 0x22/0x24)
    if (args.Length > 1 && args[1] == "scan")
    {
        long fb = b + 0x1a1b5b8;
        var l2=new float[512]; var r2=new float[512];
        int hits=0;
        // ch, bankMSB, bankLSB, program-range, note-range
        (int ch,int msb,int lsb,int pglo,int pghi,int nlo,int nhi,int nstep)[] passes = {
            (0, 0, 0, 0,128, 24,96, 6),     // GM melodic, bank 0
            (0, 1, 0, 0,128, 36,84, 12),    // SC-88 native (bank MSB=1)
            (0, 8, 0, 0,128, 36,84, 12),    // variation bank
            (9, 0, 0, 0,  1, 0,128, 1),     // drum channel, all keys
            (9, 0,16, 0,  8, 24,84, 4),     // drum kits via bank LSB
        };
        foreach (var p in passes)
        for (int pg = p.pglo; pg < p.pghi; pg++)
        {
            void CC2(int c,int v)=>shortIn((uint)((0xB0|p.ch)|(c<<8)|(v<<16)),0);
            CC2(0,p.msb); CC2(32,p.lsb); CC2(7,127); CC2(10,64); CC2(91,0); CC2(93,0);
            shortIn((uint)((0xC0|p.ch)|(pg<<8)), 0);
            for (int nt=p.nlo; nt<p.nhi; nt+=p.nstep)
            {
                shortIn((uint)((0x90|p.ch)|(nt<<8)|(110<<16)), 0); flush();
                fixed(float* pl=l2,pr=r2) for(int i=0;i<2;i++) process(pl,pr,512);
                for (int v=0; v<64; v++){
                    byte fl=*(byte*)(fb+v*0x50); if((fl&1)==0) continue;
                    uint wc=*(uint*)(b+0x1a6fb60+v*4);
                    if ((wc & 0x800)!=0 || (fl&0x20)!=0){
                        Console.WriteLine($"ch={p.ch} msb={p.msb} lsb={p.lsb} prog={pg} note={nt} runflag=0x{fl:X} wave_ctrl=0x{wc:X}");
                        if (++hits>40) { Console.WriteLine("(40+ hits, stopping)"); return; }
                    }
                }
                shortIn((uint)((0x80|p.ch)|(nt<<8)), 0); flush();
                fixed(float* pl=l2,pr=r2) for(int i=0;i<2;i++) process(pl,pr,512);
            }
        }
        Console.WriteLine($"scan done, {hits} reverse-codec hits"); return;
    }

    int bankMsb = args.Length > 4 ? int.Parse(args[4]) : 0;
    void CC(int ch,int c,int v) => shortIn((uint)((0xB0|ch)|(c<<8)|(v<<16)), 0);
    CC(0,0,bankMsb); CC(0,32,0); CC(0,7,127); CC(0,10,64); CC(0,91,0); CC(0,93,0);
    shortIn((uint)((0xC0|0)|(program<<8)), 0);              // program change
    shortIn((uint)((0x90|0)|(note<<8)|(127<<16)), 0);       // note on
    flush();

    var L = new float[512]; var R = new float[512];
    fixed (float* pL = L, pR = R)
        for (int i = 0; i < 3; i++) process(pL, pR, 512);    // let the voice start

    long vbase = b + 0x1a1b570;   // sampler state
    long fbase = b + 0x1a1b5b8;   // run flags
    int found = -1;
    for (int v = 0; v < 64; v++)
    {
        byte flag = *(byte*)(fbase + v*0x50);
        if ((flag & 1) == 0) continue;
        long vs = vbase + v*0x50;
        long D   = *(long*)(vs + 0x20);
        int  pos = *(int*) (vs + 0x28);
        int  len = *(int*) (vs + 0x2c);
        int  end = *(int*) (vs + 0x30);
        long S   = *(long*)(vs + 0x38);
        int  pred= *(int*) (vs + 0x40);
        byte sc  = *(byte*)(vs + 0x49);
        Console.WriteLine($"voice {v} flag=0x{flag:X} D=0x{D:X} S=0x{S:X} pos={pos} len={len} end={end} pred={pred} scale0={sc}");
        if (found < 0 && D != 0 && S != 0) found = v;
    }
    if (found < 0) { Console.WriteLine("no active voice with streams"); return; }

    // raw per-voice arrays (position domain): loop_start / end / start / wave-ctrl
    long baseA = *(long*)(b + 0x1a18ef0);
    long baseB = *(long*)(b + 0x1a11a68);
    uint waveCtrl = *(uint*)(b + 0x1a6fb60 + found*4);
    int loopStart = *(int*) (b + 0x1a6fc60 + found*4);
    int endPos    = *(int*) (b + 0x1a6fd60 + found*4);
    int startPos  = *(int*) (b + 0x1a6fe60 + found*4);
    Console.WriteLine($"raw: wave_ctrl=0x{waveCtrl:X} start={startPos} loop_start={loopStart} end={endPos}  baseA=0x{baseA:X} baseB=0x{baseB:X}");

    {
        long vs = vbase + found*0x50;
        long D = *(long*)(vs + 0x20);
        long S = *(long*)(vs + 0x38);
        byte flag = *(byte*)(fbase + found*0x50);
        bool reverse = (waveCtrl & 0x800) != 0;         // bit 11 => reverse-playback codec
        int aligned = startPos & ~0x1f;
        int fwdLen = *(int*)(vs + 0x30);                 // start - aligned (forward total)
        int revLen = loopStart - aligned;                // reverse total
        int nSamp = Math.Clamp(reverse ? revLen : fwdLen, 1, 400000);
        Console.WriteLine($"decoding voice {found}: {nSamp} samples {(reverse?"REVERSE":"forward")} from D=0x{D:X} (runflag 0x{flag:X})");

        var delta = new byte[nSamp + 1];
        var scale = new byte[(nSamp >> 5) + 4];
        for (int i = 0; i < delta.Length; i++) delta[i] = *(byte*)(D + i);
        for (int i = 0; i < scale.Length; i++) scale[i] = *(byte*)(S + i);
        string dir = Path.GetDirectoryName(Path.GetFullPath(outWav));
        File.WriteAllBytes(Path.Combine(dir, "delta_stream.bin"), delta);
        File.WriteAllBytes(Path.Combine(dir, "scale_stream.bin"), scale);

        int Sc(int pos){ int sb = scale[pos >> 5]; return ((pos >> 4) & 1) == 0 ? (sb & 0xf) : ((sb >> 4) & 0xf); }
        // OUR codec. Forward: pos 0..n. Reverse (FUN_18003ff90): pos n..0. Same accumulation.
        var pcm = new short[nSamp];
        int predictor = 0;
        for (int k = 0; k < nSamp; k++)
        {
            int pos = reverse ? (nSamp - 1 - k) : k;
            predictor += (sbyte)delta[pos] << (Sc(pos) + 10);
            pcm[k] = (short)Math.Clamp((int)Math.Round(predictor * 7.450580596923828e-09 * 32767.0), -32768, 32767);
        }
        WriteWav(outWav, pcm, 32000);
        long peak = 0; double sq = 0; int zc = 0;
        for (int i=0;i<nSamp;i++){ peak=Math.Max(peak,Math.Abs((long)pcm[i])); sq+=(double)pcm[i]*pcm[i]; if(i>0 && (pcm[i]>=0)!=(pcm[i-1]>=0)) zc++; }
        Console.WriteLine($"decoded {nSamp} -> {outWav}  peak={peak} rms={Math.Sqrt(sq/nSamp):0} zerocross%={100.0*zc/nSamp:0.0}");
    }

    // Stereo writer: the engine pans per note (drum kit +0x280, CC10 for melodic parts), so any
    // mono capture silently mixes a pan law into the level and makes A/B comparison meaningless.
    static void WriteWavStereo(string path, short[] interleaved, int rate)
    {
        using var fs = new FileStream(path, FileMode.Create);
        using var w = new BinaryWriter(fs);
        int dataBytes = interleaved.Length * 2;
        w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + dataBytes);
        w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        w.Write(System.Text.Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)2);
        w.Write(rate); w.Write(rate * 4); w.Write((short)4); w.Write((short)16);
        w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write(dataBytes);
        foreach (var s in interleaved) w.Write(s);
    }

    static void WriteWav(string path, short[] mono, int rate)
    {
        using var fs = new FileStream(path, FileMode.Create);
        using var w = new BinaryWriter(fs);
        int dataBytes = mono.Length * 2;
        w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + dataBytes);
        w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        w.Write(System.Text.Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write(dataBytes);
        foreach (var s in mono) w.Write(s);
    }
}

// ---------------------------------------------------------------------------------------------
// Standard MIDI File parsing, kept deliberately small.
//
// Only what a render needs: absolute sample positions for channel messages and system exclusives,
// with the tempo map applied. Meta events other than tempo are read past, not interpreted -- the
// engine has no use for a track name.
struct SmfEvent
{
    public int At;        // sample position
    public int Status;    // channel message status byte, or 0 when Bytes is set
    public int D1, D2;
    public byte[] Bytes;  // a system exclusive, including F0 and F7
}

static class Smf
{
    public static System.Collections.Generic.List<SmfEvent> Parse(byte[] d, int sampleRate,
                                                                 out double seconds)
    {
        if (d.Length < 14 || d[0] != 'M' || d[1] != 'T' || d[2] != 'h' || d[3] != 'd')
            throw new InvalidDataException("not a Standard MIDI File");

        int tracks = (d[10] << 8) | d[11];
        int division = (d[12] << 8) | d[13];
        if ((division & 0x8000) != 0)
            throw new InvalidDataException("SMPTE division is not supported");

        // Every track's events at absolute ticks, merged, then converted once the tempo map is
        // known -- a tempo change in track 0 has to move events in track 5 too.
        var raw = new System.Collections.Generic.List<(long Tick, int Order, SmfEvent Ev)>();
        var tempos = new System.Collections.Generic.List<(long Tick, int UsPerQn)>();
        int order = 0, at = 14;

        for (int t = 0; t < tracks && at + 8 <= d.Length; t++)
        {
            if (d[at] != 'M' || d[at + 1] != 'T' || d[at + 2] != 'r' || d[at + 3] != 'k') break;
            int len = (d[at + 4] << 24) | (d[at + 5] << 16) | (d[at + 6] << 8) | d[at + 7];
            int p = at + 8, end = Math.Min(at + 8 + len, d.Length);
            long tick = 0; int status = 0;
            at = at + 8 + len;

            while (p < end)
            {
                tick += ReadVar(d, ref p);
                if (p >= end) break;

                if (d[p] >= 0x80) status = d[p++];
                if (status == 0 || p > end)
                    throw new InvalidDataException(
                        $"track {t} is malformed at byte {p}: running status with none set, or a "
                        + "length running past the track end");
                if (status == 0xFF)
                {
                    int type = d[p++];
                    int mlen = ReadVar(d, ref p);
                    if (type == 0x51 && mlen == 3)
                        tempos.Add((tick, (d[p] << 16) | (d[p + 1] << 8) | d[p + 2]));
                    p += mlen;
                }
                else if (status == 0xF0 || status == 0xF7)
                {
                    int mlen = ReadVar(d, ref p);
                    // A stored F0 event omits its own leading F0; the engine wants it back.
                    var msg = new byte[status == 0xF0 ? mlen + 1 : mlen];
                    int o = 0;
                    if (status == 0xF0) msg[o++] = 0xF0;
                    Array.Copy(d, p, msg, o, mlen);
                    p += mlen;
                    raw.Add((tick, order++, new SmfEvent { Bytes = msg }));
                }
                else
                {
                    int hi = status & 0xF0;
                    int d1 = d[p++];
                    int d2 = (hi == 0xC0 || hi == 0xD0) ? 0 : d[p++];
                    raw.Add((tick, order++, new SmfEvent { Status = status, D1 = d1, D2 = d2 }));
                }
            }
        }

        raw.Sort((a, b) => a.Tick != b.Tick ? a.Tick.CompareTo(b.Tick) : a.Order.CompareTo(b.Order));
        tempos.Sort((a, b) => a.Tick.CompareTo(b.Tick));

        var outEvents = new System.Collections.Generic.List<SmfEvent>(raw.Count);
        long lastTick = 0; double lastSeconds = 0; int us = 500000, ti = 0;

        foreach (var (tick, _, ev) in raw)
        {
            while (ti < tempos.Count && tempos[ti].Tick <= tick)
            {
                lastSeconds += (tempos[ti].Tick - lastTick) * (us / 1e6) / division;
                lastTick = tempos[ti].Tick;
                us = tempos[ti].UsPerQn;
                ti++;
            }
            double when = lastSeconds + (tick - lastTick) * (us / 1e6) / division;
            var copy = ev;
            copy.At = (int)(when * sampleRate);
            outEvents.Add(copy);
        }

        seconds = outEvents.Count == 0 ? 0.0 : outEvents[outEvents.Count - 1].At / (double)sampleRate;
        return outEvents;
    }

    static int ReadVar(byte[] d, ref int p)
    {
        int v = 0, guard = 0;
        while (p < d.Length)
        {
            int b = d[p++];
            v = (v << 7) | (b & 0x7F);
            if ((b & 0x80) == 0) break;
            if (++guard > 4)
                throw new InvalidDataException($"variable-length quantity at byte {p} never ends");
        }
        return v;
    }
}
