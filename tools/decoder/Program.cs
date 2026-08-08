using System.Collections.Generic;
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
    bool scanMode = args.Length > 1 && (args[1] == "scan" || args[1] == "enum" || args[1] == "map" || args[1] == "mapall" || args[1] == "voices" || args[1] == "calib" || args[1] == "filt" || args[1] == "lfo" || args[1] == "song" || args[1] == "smf" || args[1] == "drum" || args[1] == "drumsong" || args[1] == "holdnote" || args[1] == "tvftrace" || args[1] == "drumnote" || args[1] == "panscan" || args[1] == "lfotrace" || args[1] == "seq" || args[1] == "revdump" || args[1] == "chodump" || args[1] == "delaytest" || args[1] == "ampramp" || args[1] == "volramp" || args[1] == "volscan" || args[1] == "panramp" || args[1] == "sendramp" || args[1] == "ccscan" || args[1] == "busscan" || args[1] == "partfind" || args[1] == "pokebyte" || args[1] == "progscan" || args[1] == "peek" || args[1] == "partdump" || args[1] == "fxmatrix" || args[1] == "xgvoices" || args[1] == "xgsweep" || args[1] == "slotscan" || args[1] == "matscan" || args[1] == "mattrace" || args[1] == "outfilt" || args[1] == "sampstate" || args[1] == "resetstate" || args[1] == "lfonodes" || args[1] == "chorusin" || args[1] == "envseg" || args[1] == "keysend" || args[1] == "predtrace" || args[1] == "dumpmem" || args[1] == "postrace" || args[1] == "drumprobe" || args[1] == "portatrace" || args[1] == "panprobe" || args[1] == "svfcoef" || args[1] == "svfmel" || args[1] == "xgdrumfilt" || args[1] == "drumnrpn" || args[1] == "gsdrumnrpn" || args[1] == "mapsysex" || args[1] == "chophase" || args[1] == "blkdiff" || args[1] == "svfslew" || args[1] == "partialmix" || args[1] == "voicesolo" || args[1] == "stagedpitch" || args[1] == "pitchword" || args[1] == "pitchmat" || args[1] == "jitterprobe" || args[1] == "svfin" || args[1] == "notebatch" || args[1] == "tvatrace" || args[1] == "onsetprobe" || args[1] == "sysexstress" || args[1] == "sysexreplay" || args[1] == "efxdump" || args[1] == "revir" || args[1] == "choir" || args[1] == "dlyir" || args[1] == "partprobe" || args[1] == "partmap" || args[1] == "efxir" || args[1] == "fxgain" || args[1] == "envtrace" || args[1] == "bulkmap" || args[1] == "drumbulk" || args[1] == "drumreplay" || args[1] == "drumreset" || args[1] == "progorder" || args[1] == "smfstate" || args[1] == "ccdiff" || args[1] == "buscap");
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
            // Optional finer grid via TS_POSTRACE_BLOCK. The core's own audio chunk is 32 samples -- events are
            // still only taken every 320 -- so stepping at 32 shows the sampler state evolve inside
            // the first control tick instead of only at its boundary.
            int blkP = int.TryParse(Environment.GetEnvironmentVariable("TS_POSTRACE_BLOCK"), out var _bp) ? _bp : 320;
            for(int sub=0; sub<320; sub+=blkP){
                fixed(float* pl=lp2,pr=rp2) process(pl,pr,(uint)blkP);
                posP+=blkP;
                if(blkP<320){
                    Console.Write($"{posP,6} smp:");
                    for(int v=0;v<64;v++){ if((*(byte*)(fbp+v*0x50)&1)==0) continue;
                        long st3=ssp+(long)v*0x50;
                        // g_voice_ramp_pitch @181a1cbf0, stride 0x18: +0 flags (bit0 = active),
                        // +4 counter, +8 current, +0xc target, +0x10 step, +0x14 cached increment.
                        // The increment scratch at DAT_181a18f30 is only four lanes and is refilled
                        // per voice group, so the persistent ramp state is what can be read here.
                        long rp=b+(0x181a1cbf0L-0x180000000L)+(long)v*0x18;
                        Console.Write($"  v{v} pos={*(int*)(st3+0x28)} ph={*(ushort*)(st3+0x46)}"
                                     +$" exact={*(int*)(st3+0x28) + *(ushort*)(st3+0x46)/65536.0:0.000000}"
                                     +$" | pitchramp flags={*(ushort*)(rp+0):x4} cur={*(int*)(rp+8)}"
                                     +$" tgt={*(int*)(rp+0xc)} step={*(int*)(rp+0x10)}"
                                     +$" inc={*(int*)(rp+0x14)} ({*(int*)(rp+0x14)/65536.0:0.000000})"); }
                    Console.WriteLine();
                }
            }
            if(blkP<320){ if(posP>=totalP) break; else continue; }
            Console.Write($"{posP*1000.0/SRp,6:0}ms:");
            for(int v=0;v<64;v++){ if((*(byte*)(fbp+v*0x50)&1)==0) continue;
                long st=ssp+(long)v*0x50;
                // +0x46 is the sampler's 16-bit phase fraction (the interpolator row is its top
                // seven bits), so pos + phase/65536 is the full fixed-point read position.
                Console.Write($"  v{v} pos={*(int*)(st+0x28)}/{*(int*)(st+0x2c)}"
                             +$" ph={*(ushort*)(st+0x46)}"
                             +$" exact={*(int*)(st+0x28) + *(ushort*)(st+0x46)/65536.0:0.000000}"); }
            Console.WriteLine();
            if(posP==320){   // first tick: dump the whole 0x50-byte sampler state per active voice
                for(int v=0;v<64;v++){ if((*(byte*)(fbp+v*0x50)&1)==0) continue;
                    long st2=ssp+(long)v*0x50;
                    Console.Write($"    v{v} sampler_state:");
                    for(int i=0;i<0x50;i++) Console.Write($" {*(byte*)(st2+i):x2}");
                    Console.WriteLine(); }
            }
        }
        return;
    }
    // svfcoef mode: read the coefficients the SVF is actually handed, rather than the voice fields
    //   they are derived from. g_svf_f_coef (181a1cb70) and g_svf_q_coef (181a1d1f0) are the
    //   per-voice float scratch the filter loop reads; the voice's own +0xcc / +0xdc are only the
    //   ramp *targets*, and the conversion between them is exactly what a reimplementation has to
    //   guess. Strikes one drum note and prints both, per control tick, beside the raw fields.
    //   args: dll svfcoef <prog> <note> <vel> <sec> [cc74]
    if (args.Length > 1 && args[1] == "svfcoef")
    {
        int SRs=32000; int pgs=int.Parse(args[2]); int nts=int.Parse(args[3]);
        int vls=int.Parse(args[4]);
        double secs=double.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture);
        int cut74=args.Length>6?int.Parse(args[6]):-1;
        setSR((float)SRs); setBS(512); activate((float)SRs,512); setThr();
        long fbs=b+0x1a1b5b8;
        var getVCs=(delegate* unmanaged[Cdecl]<int,long>)(b+0x5c360);
        long vcs=getVCs(0);
        float* fcoef=(float*)(b+(0x181a1cb70L-0x180000000L));
        float* qcoef=(float*)(b+(0x181a1d1f0L-0x180000000L));
        var ls=new float[512]; var rs=new float[512];
        GsReset(); flush(); fixed(float* pl=ls,pr=rs) for(int i=0;i<8;i++) process(pl,pr,512);
        void CCs(int c,int v)=>shortIn((uint)((0xB0|9)|(c<<8)|(v<<16)),0);
        CCs(7,127); CCs(10,64); CCs(91,0); CCs(93,0);
        if(cut74>=0) CCs(74,cut74);
        shortIn((uint)(0xC9|(pgs<<8)),0); flush();
        Console.WriteLine("t_ms,voice,f_coef,q_coef,cc_cutoff,dc_qraw,ee_resobyte,f5_type");
        shortIn((uint)((0x90|9)|(nts<<8)|(vls<<16)),0); flush();
        int ntk=(int)(secs*100);
        for(int t=0;t<ntk;t++){
            fixed(float* pl=ls,pr=rs) process(pl,pr,320);
            for(int v=0;v<64;v++){
                if((*(byte*)(fbs+v*0x50)&1)==0) continue;
                long pv=vcs+(long)v*0x220;
                // SoA layout: four lanes per group of four voices, groups stride 0x10 floats.
                int lane=v&3, grp=v>>2;
                Console.WriteLine($"{t*10},{v},{fcoef[grp*16+lane]:0.000000},{qcoef[grp*16+lane]:0.000000},"
                                 +$"{*(int*)(pv+0xcc)},{*(int*)(pv+0xdc)},{*(byte*)(pv+0xee)},{*(byte*)(pv+0x1f5)}");
                if(t>6) break;
            }
            if(t>6) break;
        }
        shortIn((uint)((0x80|9)|(nts<<8)),0); flush();
        return;
    }
    // svfin mode: dump the buffer the SVF reads its input from, so a reimplementation can compare
    //   its *pre-filter* signal against the engine's instead of inferring the filter from the mix.
    //   svf_render_* takes input from DAT_181a1c970 as SoA [sample][lane] -- sample stride 0x10
    //   bytes, lane at +lane*4 -- and writes 32 samples per call to DAT_181a1d230 + voice*0x80,
    //   contiguous. The engine's internal audio chunk is therefore 32 samples, ten to a 320-sample
    //   control tick, so reading the buffers after a 320-frame Process yields the LAST chunk of that
    //   tick: absolute samples [tick*320 + 288, tick*320 + 320).
    //   Writes a CSV of tick,index,abs_sample,in,out. args: dll svfin <prog> <note> <vel> <sec> <out.csv>
    if (args.Length > 1 && args[1] == "svfin")
    {
        int SRi=32000; int pgi=int.Parse(args[2]); int nti=int.Parse(args[3]);
        int vli=int.Parse(args[4]);
        double seci=double.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture);
        string outi=args.Length>6?args[6]:"svfin.csv";
        setSR((float)SRi); setBS(512); activate((float)SRi,512); setThr();
        long fbi=b+0x1a1b5b8;
        byte* inBuf =(byte*)(b+(0x181a1c970L-0x180000000L));
        byte* outBuf=(byte*)(b+(0x181a1d230L-0x180000000L));
        var li=new float[512]; var ri=new float[512];
        GsReset(); flush(); fixed(float* pl=li,pr=ri) for(int i=0;i<8;i++) process(pl,pr,512);
        void CCi(int c,int v)=>shortIn((uint)((0xB0|9)|(c<<8)|(v<<16)),0);
        CCi(7,127); CCi(10,64); CCi(91,0); CCi(93,0);
        shortIn((uint)(0xC9|(pgi<<8)),0); flush();
        var sb=new System.Text.StringBuilder("tick,index,abs_sample,in,out\n");
        shortIn((uint)((0x90|9)|(nti<<8)|(vli<<16)),0); flush();
        int tk=(int)(seci*100);
        int restrike=args.Length>7?int.Parse(args[7]):0;   // re-strike every N ticks, 0 = once
        for(int t=0;t<tk;t++){
            if(restrike>0 && t>0 && t%restrike==0){
                shortIn((uint)((0x80|9)|(nti<<8)),0);
                shortIn((uint)((0x90|9)|(nti<<8)|(vli<<16)),0); flush();
            }
            int blk=args.Length>8?int.Parse(args[8]):320;   // 32 = the core's own chunk: full coverage
            for(int sub=0; sub<320; sub+=blk){
                fixed(float* pl=li,pr=ri) process(pl,pr,(uint)blk);
                bool any=false;
                for(int v=0;v<64;v++) if((*(byte*)(fbi+v*0x50)&1)!=0){ any=true; break; }
                if(!any) continue;
                int shown=Math.Min(32,blk);
                for(int n=0;n<shown;n++){
                    float fin =*(float*)(inBuf + n*0x10);      // group 0, lane 0
                    float fout=*(float*)(outBuf+ n*4);         // voice 0, contiguous
                    int abs = blk>=320 ? t*320+288+n : t*320+sub+n;
                    sb.Append($"{t},{n},{abs},{fin:0.00000000},{fout:0.00000000}\n");
                }
            }
        }
        shortIn((uint)((0x80|9)|(nti<<8)),0); flush();
        File.WriteAllText(outi, sb.ToString());
        Console.WriteLine($"svfin done: {outi}");
        return;
    }
    // notebatch mode: render many single notes through the ORACLE in one process, so a fixture can
    //   be generated from the DLL rather than from any reimplementation. Reads a case file of
    //   "program note velocity hold map [channel]" lines and writes one raw interleaved float32
    //   stereo file per case into <outdir>, named case<NNNN>.f32. Raw float rather than WAV because
    //   the digest the port checks is taken over interleaved float32 pairs, so this is the exact
    //   byte sequence with nothing to round.
    //
    //   The channel field is optional and defaults to 0, so a case file written before it existed
    //   still means what it did. Its reason for existing is channel 9: a program change there
    //   selects a drum kit rather than a tone, and the melodic sweep could not reach the kits at
    //   all -- which left the bass drum, the one thing in a GS arrangement with real energy below
    //   90 Hz, outside everything the note gate could see.
    //
    //   Frames are (hold + tail) * 32000 to match render_note's own accounting, and the render is
    //   driven in 320-sample control ticks: the core renders audio in 32-sample units but only
    //   takes events every 10 ms, so 320 is the grid on which a note-off means what it says.
    //   args: dll notebatch <cases.txt> <outdir> [tailSeconds]
    //
    // tvatrace mode: play one note and read the amplitude envelope the module built for it, straight
    //   out of the voice, before any of it has run. `tva_compute_env_rates @ 180060ca0` writes the
    //   four segment durations to voice+0x12/0x1c6/0x1c8/0x1ca and the release to +0x32, and
    //   `tva_compute_env_levels @ 180060b40` writes the four targets to +0x16/0x1d2/0x1d4/0x1d6.
    //   Reading them is the difference between inferring a port's envelope error from the audio and
    //   being told what the answer should have been.
    //
    //   Segments 0 and the release store a per-tick *step*, 0xa0000/duration, rather than the
    //   duration -- segments 1 to 3 store the duration and their loaders divide it later. Both are
    //   printed so the two are never confused.
    // onsetprobe mode: how long after a note-on the module's first sample departs from idle.
    //   Every case in the note fixture answers 128 samples exactly, and that number needs pinning
    //   before it can be modelled: 128 samples is both "4 ms at 32 kHz" and "four of the core's
    //   32-sample render blocks", and those two predict different things anywhere else. So the
    //   probe sweeps the sample rate and the size of the chunk the note is rendered in.
    //   args: dll onsetprobe <prog> <note> <rate> <chunk> [prerender]
    if (args.Length > 1 && args[1] == "onsetprobe")
    {
        int pgo = int.Parse(args[2]), nto = int.Parse(args[3]);
        int rateo = args.Length > 4 ? int.Parse(args[4]) : 32000;
        int chunko = args.Length > 5 ? int.Parse(args[5]) : 320;
        // Frames rendered between the *program change* and the note-on. The first run of this probe
        // rendered 32 here without meaning anything by it and got an onset 32 samples earlier than
        // the note fixture's, which is the whole question: whether the delay belongs to the note-on
        // or to the program change in front of it.
        int preo = args.Length > 6 ? int.Parse(args[6]) : 0;
        setSR((float)rateo); setBS(512); activate((float)rateo, 512); setThr();
        var lo2 = new float[4096]; var ro2 = new float[4096];
        void CCo(int c, int v) => shortIn((uint)(0xB0 | (c << 8) | (v << 16)), 0);
        GsReset(); flush();
        fixed (float* pl = lo2, pr = ro2) for (int i = 0; i < 8; i++) process(pl, pr, 512);
        // The idle level the core sits at with nothing sounding; the onset is the first departure
        // from it, which is independent of how fast the note's attack happens to be.
        float idle = lo2[511];
        CCo(0, 0); CCo(32, 0); CCo(7, 127); CCo(10, 64); CCo(91, 0); CCo(93, 0);
        shortIn((uint)(0xC0 | (pgo << 8)), 0); flush();
        if (preo > 0) { fixed (float* pl = lo2, pr = ro2) process(pl, pr, (uint)preo); }
        shortIn((uint)(0x90 | (nto << 8) | (100 << 16)), 0); flush();
        int seen = -1, at = 0;
        while (seen < 0 && at < rateo)
        {
            fixed (float* pl = lo2, pr = ro2) process(pl, pr, (uint)chunko);
            for (int i = 0; i < chunko; i++)
                if (Math.Abs(lo2[i] - idle) > 1e-7f) { seen = at + i; break; }
            at += chunko;
        }
        Console.WriteLine($"rate={rateo} chunk={chunko} pre={preo} idle={idle:G6} onset={seen}");
        return;
    }
    //   args: dll tvatrace <prog> <note> <vel> <map> [channel]
    if (args.Length > 1 && args[1] == "tvatrace")
    {
        int pgv = int.Parse(args[2]), ntv = int.Parse(args[3]);
        int vlv = args.Length > 4 ? int.Parse(args[4]) : 100;
        int mpv = args.Length > 5 ? int.Parse(args[5]) : 0;
        int chv = args.Length > 6 ? int.Parse(args[6]) & 15 : 0;
        setSR(32000f); setBS(512); activate(32000f, 512); setThr();
        long fbv2 = b + 0x1a1b5b8;
        var getVCv = (delegate* unmanaged[Cdecl]<int, long>)(b + 0x5c360);
        long vcv = getVCv(0);
        var lv2 = new float[512]; var rv2 = new float[512];
        void CCv2(int c, int v) => shortIn((uint)((0xB0 | chv) | (c << 8) | (v << 16)), 0);
        GsReset();
        if (mpv >= 1 && mpv <= 4) for (int c = 0; c < 16; c++) ToneMap0(c, mpv);
        flush();
        fixed (float* pl = lv2, pr = rv2) for (int i = 0; i < 8; i++) process(pl, pr, 512);
        CCv2(0, 0); CCv2(32, 0); CCv2(7, 127); CCv2(10, 64); CCv2(91, 0); CCv2(93, 0);
        shortIn((uint)((0xC0 | chv) | (pgv << 8)), 0); flush();
        shortIn((uint)((0x90 | chv) | (ntv << 8) | (vlv << 16)), 0); flush();
        // One control tick only. The envelope is built at note-on; rendering longer would let the
        // first segment run and tell us nothing more.
        fixed (float* pl = lv2, pr = rv2) process(pl, pr, 320);
        for (int v = 0; v < 64; v++)
        {
            if ((*(byte*)(fbv2 + v * 0x50) & 1) == 0) continue;
            long p = vcv + (long)v * 0x220;
            ushort step0 = *(ushort*)(p + 0x12), d1 = *(ushort*)(p + 0x1c6);
            ushort d2 = *(ushort*)(p + 0x1c8), d3 = *(ushort*)(p + 0x1ca);
            ushort rstep = *(ushort*)(p + 0x26), rdur = *(ushort*)(p + 0x32);
            Console.WriteLine(
                $"voice{v} targets={*(ushort*)(p + 0x16)},{*(ushort*)(p + 0x1d2)}," +
                $"{*(ushort*)(p + 0x1d4)},{*(ushort*)(p + 0x1d6)}" +
                $" dur={(step0 == 0xffff ? 0 : 0xa0000 / step0)},{d1},{d2},{d3}" +
                $" rel={(rstep == 0xffff ? 0 : 0xa0000 / rstep)}/{rdur}" +
                $" step0={step0} relstep={rstep}" +
                $" curve={*(ushort*)(p + 0x10)},{*(ushort*)(p + 0x1cc)},{*(ushort*)(p + 0x1ce)}," +
                $"{*(ushort*)(p + 0x1d0)},{*(ushort*)(p + 0x24)}" +
                $" zeroflag={*(byte*)(p + 0x188)}");
        }
        return;
    }
    // envtrace mode: the TVA envelope's registers AND the gain word they produce, across a
    //   note-off, for a tone selected by bank as well as program. `tvatrace` reads the registers but
    //   cannot reach a variation tone, and `ampramp` traces the gain but only through the attack and
    //   only for the first voice -- neither can answer what a two-partial tone does when one
    //   partial's release duration computes to zero.
    //
    //   The registers printed include the two the others omit: voice+0x0e and voice+0x22. Ghidra
    //   calls the table behind them `g_env_startphase_b` and it is not a start phase. Every stage
    //   loader writes `g_env_startphase_b[min(duration,10)]` there, and `voice_block_process` hands
    //   it on as `value + 0x4000` to the per-voice amplitude ramp's rate word -- so it is how fast
    //   the anti-zipper ramp chases the envelope, chosen per segment. The table is 512/n.
    //   **Render in 32-sample chunks, not 16.** The gain buffer holds 16 entries and the engine
    //   refills it once per 32-sample block, so a 16-sample call reads the same 16 values twice and
    //   the trace comes out looking like a staircase of 32-sample treads that does not exist. One
    //   entry is two output samples. Measured by rendering the same note both ways.
    //   args: dll envtrace <msb> <lsb> <prog> <note> <vel> <holdSamples> <traceSamples> [map] [ch]
    //         [chunk]
    if (args.Length > 1 && args[1] == "envtrace")
    {
        int emsb = int.Parse(args[2]), elsb = int.Parse(args[3]), eprog = int.Parse(args[4]);
        int enote = int.Parse(args[5]), evel = int.Parse(args[6]);
        int ehold = int.Parse(args[7]), etrace = int.Parse(args[8]);
        int emap = args.Length > 9 ? int.Parse(args[9]) : 2;
        int ech = args.Length > 10 ? int.Parse(args[10]) & 15 : 0;
        int echunk = args.Length > 11 ? int.Parse(args[11]) : 16;
        setSR(32000f); setBS(512); activate(32000f, 512); setThr();
        long efb = b + 0x1a1b5b8;
        var getVCe = (delegate* unmanaged[Cdecl]<int, long>)(b + 0x5c360);
        long evc = getVCe(0);
        var el = new float[512]; var er = new float[512];
        void CCe(int c, int v) => shortIn((uint)((0xB0 | ech) | (c << 8) | (v << 16)), 0);
        GsReset();
        if (emap >= 1 && emap <= 4) for (int c = 0; c < 16; c++) ToneMap0(c, emap);
        flush();
        fixed (float* pl = el, pr = er) for (int i = 0; i < 8; i++) process(pl, pr, 512);
        CCe(0, emsb); CCe(32, elsb); CCe(7, 127); CCe(10, 64); CCe(91, 0); CCe(93, 0);
        shortIn((uint)((0xC0 | ech) | (eprog << 8)), 0); flush();
        shortIn((uint)((0x90 | ech) | (enote << 8) | (evel << 16)), 0); flush();
        fixed (float* pl = el, pr = er) process(pl, pr, 320);

        var voices = new System.Collections.Generic.List<int>();
        for (int v = 0; v < 64; v++)
        {
            if ((*(byte*)(efb + v * 0x50) & 1) == 0) continue;
            voices.Add(v);
            long p = evc + (long)v * 0x220;
            ushort step0 = *(ushort*)(p + 0x12), rstep = *(ushort*)(p + 0x26);
            Console.WriteLine(
                $"voice{v} dur={(step0 == 0xffff ? 0 : 0xa0000 / step0)},{*(ushort*)(p + 0x1c6)}," +
                $"{*(ushort*)(p + 0x1c8)},{*(ushort*)(p + 0x1ca)}" +
                $" reldur={*(ushort*)(p + 0x32)} step0={step0} relstep={rstep}" +
                $" rate0(+0x0e)={*(ushort*)(p + 0x0e)} relrate(+0x22)={*(ushort*)(p + 0x22)}" +
                $" targets={*(ushort*)(p + 0x16)},{*(ushort*)(p + 0x1d2)}," +
                $"{*(ushort*)(p + 0x1d4)},{*(ushort*)(p + 0x1d6)}");
        }

        // Hold, then note-off, tracing the gain word of every voice the note started at one-sample
        // resolution throughout. The buffer holds 16 floats and is rewritten each call, so the
        // render is chunked to 16 to read it without gaps.
        Console.WriteLine("i," + string.Join(",", voices.ConvertAll(v => $"v{v}")) + ",rate0");
        int done = 320;
        bool released = false;
        for (int i = 0; done < ehold + etrace; i++)
        {
            if (!released && done >= ehold)
            {
                shortIn((uint)((0x80 | ech) | (enote << 8) | (64 << 16)), 0); flush();
                released = true;
            }
            fixed (float* pl = el, pr = er) process(pl, pr, (uint)echunk);
            long p0 = evc + (long)voices[0] * 0x220;
            for (int k = 0; k < 16; k++)
            {
                var cols = new System.Collections.Generic.List<string>();
                foreach (int v in voices)
                {
                    long gb = b + 0x1a1d830 + (v & 3) * 0x40 + (v >> 2) * 4;
                    cols.Add($"{*(float*)(gb + k * 4):0.000000}");
                }
                Console.WriteLine($"{done + k}," + string.Join(",", cols)
                                  + $",{*(ushort*)(p0 + 0x0e)}");
            }
            done += echunk;
        }
        return;
    }
    if (args.Length > 1 && args[1] == "notebatch")
    {
        const int SRn = 32000;
        string casePath = args[2];
        string outDir = args[3];
        double tailN = args.Length > 4
            ? double.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture) : 1.8;
        Directory.CreateDirectory(outDir);
        var lines = File.ReadAllLines(casePath);
        setSR((float)SRn); setBS(512); activate((float)SRn, 512); setThr();
        var ln = new float[512]; var rn = new float[512];
        int index = 0, rendered = 0;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            index++;
            if (line.Length == 0 || line[0] == '#') continue;
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 5) { Console.Error.WriteLine($"line {index}: need 5 fields"); Environment.Exit(2); }
            int pg = int.Parse(f[0]), nt = int.Parse(f[1]), vl = int.Parse(f[2]);
            double hs = double.Parse(f[3], System.Globalization.CultureInfo.InvariantCulture);
            int mp = int.Parse(f[4]);
            int ch = f.Length > 5 ? int.Parse(f[5]) & 15 : 0;

            // A full reset between cases: a fixture case must not depend on what preceded it.
            GsReset();
            if (mp >= 1 && mp <= 4) for (int c = 0; c < 16; c++) ToneMap0(c, mp);
            flush();
            fixed (float* pl = ln, pr = rn) for (int i = 0; i < 8; i++) process(pl, pr, 512);
            void CCn(int c, int v) => shortIn((uint)((0xB0 | ch) | (c << 8) | (v << 16)), 0);
            CCn(0, 0); CCn(32, 0); CCn(7, 127); CCn(10, 64); CCn(91, 0); CCn(93, 0);
            shortIn((uint)((0xC0 | ch) | (pg << 8)), 0); flush();

            int total = (int)((hs + tailN) * SRn);
            int offAt = (int)(hs * SRn);
            var interleaved = new float[total * 2];
            shortIn((uint)((0x90 | ch) | (nt << 8) | (vl << 16)), 0); flush();
            int pos = 0; bool sent = false;
            while (pos < total)
            {
                if (!sent && pos >= offAt) { shortIn((uint)((0x80 | ch) | (nt << 8)), 0); flush(); sent = true; }
                int nf = Math.Min(320, total - pos);
                fixed (float* pl = ln, pr = rn) process(pl, pr, (uint)nf);
                for (int i = 0; i < nf; i++) { interleaved[(pos + i) * 2] = ln[i]; interleaved[(pos + i) * 2 + 1] = rn[i]; }
                pos += nf;
            }
            shortIn((uint)((0x80 | ch) | (nt << 8)), 0); flush();

            var bytes = new byte[interleaved.Length * 4];
            Buffer.BlockCopy(interleaved, 0, bytes, 0, bytes.Length);
            File.WriteAllBytes(Path.Combine(outDir, $"case{rendered:D4}.f32"), bytes);
            rendered++;
        }
        Console.WriteLine($"notebatch done: {rendered} cases -> {outDir}");
        return;
    }
    // sysexstress mode: send N SysEx messages of S data bytes back to back WITHOUT rendering
    //   between them, then render, and report survival. Separates the three candidate causes of the
    //   fault th07_19_user_gm.mid triggers: too many messages queued before the core drains them
    //   (a FIFO entry overflow), one message too long (a byte-buffer overflow), or something
    //   specific to the manufacturer. `vendor` picks 0x41 Roland or 0x43 Yamaha so the last can be
    //   ruled in or out against the other two.
    //   args: dll sysexstress <count> <dataBytes> <vendorHex> [flushEvery]
    if (args.Length > 1 && args[1] == "sysexstress")
    {
        int count = int.Parse(args[2]);
        int size = int.Parse(args[3]);
        byte vendor = Convert.ToByte(args[4], 16);
        int flushEvery = args.Length > 5 ? int.Parse(args[5]) : 0;
        setSR(32000f); setBS(512); activate(32000f, 512); setThr();
        var ls = new float[512]; var rs = new float[512];
        GsReset(); flush();
        fixed (float* pl = ls, pr = rs) for (int i = 0; i < 8; i++) process(pl, pr, 512);

        var msg = new byte[size + 3];
        msg[0] = 0xF0; msg[1] = vendor; msg[2] = 0x10;
        for (int i = 3; i < size + 2; i++) msg[i] = 0x00;
        msg[size + 2] = 0xF7;

        for (int n = 0; n < count; n++)
        {
            fixed (byte* mp = msg) longIn(mp, 0);
            if (flushEvery > 0 && (n + 1) % flushEvery == 0)
            {
                flush();
                fixed (float* pl = ls, pr = rs) process(pl, pr, 320);
            }
        }
        flush();
        fixed (float* pl = ls, pr = rs) for (int i = 0; i < 4; i++) process(pl, pr, 320);
        Console.WriteLine($"survived: {count} x {size}B vendor 0x{vendor:x2} flushEvery={flushEvery}");
        return;
    }
    // sysexreplay mode: send real SysEx messages, one per line of hex, flushing and rendering after
    //   each, printing the index first. Whatever index is last on stdout when the process dies is
    //   the message that killed it. Synthetic payloads do not reproduce the th07_19_user_gm.mid
    //   fault at any count or length, so the trigger is content, and this is what finds it.
    //   args: dll sysexreplay <hexfile>
    if (args.Length > 1 && args[1] == "sysexreplay")
    {
        var lines = File.ReadAllLines(args[2]);
        setSR(32000f); setBS(512); activate(32000f, 512); setThr();
        var lr = new float[512]; var rr = new float[512];
        GsReset(); flush();
        fixed (float* pl = lr, pr = rr) for (int i = 0; i < 8; i++) process(pl, pr, 512);
        int index = 0;
        foreach (var line in lines)
        {
            var text = line.Trim();
            if (text.Length == 0) continue;
            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var msg = new byte[parts.Length];
            for (int i = 0; i < parts.Length; i++) msg[i] = Convert.ToByte(parts[i], 16);
            Console.WriteLine($"{index}: {msg.Length}B {text[..Math.Min(36, text.Length)]}");
            Console.Out.Flush();
            fixed (byte* mp = msg) longIn(mp, 0);
            flush();
            fixed (float* pl = lr, pr = rr) process(pl, pr, 320);
            index++;
        }
        Console.WriteLine($"survived all {index} messages");
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
    // revir mode: the reverb network's IMPULSE RESPONSE, taken from the live engine by calling its
    //   own `fx_reverb_process` (0x180086140) with a controlled input instead of driving it with a
    //   note. That removes the voice path from the comparison entirely: a reimplementation feeding
    //   the same impulse must produce the same samples, so a difference is the network's and
    //   nothing else. Reading the wet out of a rendered note cannot do this -- the dry signal
    //   feeding the reverb differs slightly between engines, which moves the wet level by ~10% and
    //   varies with the patch, and that masks (or invents) network differences.
    //
    //   The function takes 32 samples per call from `input`, adds the two chorus-return buffers
    //   (zeroed here), and writes 32 floats of L at `out` and 32 of R at `out+0x80`. The ring
    //   cursor `DAT_181a62a34` is advanced by its CALLER, so this does that too.
    //   args: dll revir <out.f32> <samples> [revType]
    if (args.Length > 1 && args[1] == "revir")
    {
        string irOut = args[2];
        int irCount = int.Parse(args[3]);
        int irType = args.Length > 4 ? int.Parse(args[4]) : -1;
        setSR(32000f); setBS(512); activate(32000f, 512); setThr();
        GsReset();
        if (irType >= 0) SendSysEx(Dt1(0x40, 0x01, 0x30, (byte)irType));
        // The network's own gain ramps -- input, injection, feedback, output -- are parked at zero
        // while nothing is feeding the reverb bus, so the network is muted at idle and an impulse
        // into it produces silence. They only reach their preset values once a part is actually
        // sending, which is why this drives a note first, exactly as `revdump` does.
        void CCir(int c, int v) => shortIn((uint)((0xB0 | 0) | (c << 8) | (v << 16)), 0);
        CCir(0, 0); CCir(32, 0); CCir(7, 127); CCir(10, 64); CCir(91, 127); CCir(93, 0);
        shortIn((uint)(0xC0 | (12 << 8)), 0);   // marimba, a short one
        flush();
        var wl = new float[512]; var wr = new float[512];
        fixed (float* pl = wl, pr = wr) for (int i = 0; i < 8; i++) process(pl, pr, 512);
        shortIn((uint)(0x90 | (60 << 8) | (100 << 16)), 0); flush();
        fixed (float* pl = wl, pr = wr) for (int i = 0; i < 8; i++) process(pl, pr, 512);
        shortIn((uint)(0x80 | (60 << 8) | (64 << 16)), 0); flush();
        // Let that note's own tail fall ~90 dB before the impulse goes in, so what is captured is
        // the impulse and not the marimba.
        fixed (float* pl = wl, pr = wr) for (int i = 0; i < 250; i++) process(pl, pr, 512);

        var revProc = (delegate* unmanaged[Cdecl]<long,float*,long,long,float*,void>)(b + 0x86140);
        long V(long va) => b + (va - 0x180000000L);
        Console.WriteLine($"gain in[0..3]={*(float*)V(0x181a6ed70)},{*(float*)V(0x181a6ed74)},{*(float*)V(0x181a6ed78)},{*(float*)V(0x181a6ed7c)}"
            + $" inj={*(float*)V(0x181a6ee70)} fb={*(float*)V(0x181a6eef0)} out={*(float*)V(0x181a6edf0)} cursor={*(uint*)V(0x181a62a34):X}");
        // Silence the chorus returns the network sums in, and let the tank settle from the notes
        // the reset may have left ringing.
        for (int i = 0; i < 32; i++) { *(float*)(V(0x181a22860) + i * 4) = 0f; *(float*)(V(0x181a228e0) + i * 4) = 0f; }
        var inBuf = new float[32]; var outBuf = new float[64];
        fixed (float* ip = inBuf, op = outBuf)
        {
            for (int i = 0; i < 64; i++) { for (int k = 0; k < 32; k++) ip[k] = 0f;
                revProc(0, ip, 0, 0, op); *(uint*)V(0x181a62a34) = (*(uint*)V(0x181a62a34) - 0x20) & 0xFFFF; }
            var samples = new float[irCount * 2];
            int written = 0;
            for (int block = 0; written < irCount; block++)
            {
                for (int k = 0; k < 32; k++) ip[k] = 0f;
                if (block == 0) ip[0] = 1f;                      // the impulse
                for (int i = 0; i < 32; i++) { *(float*)(V(0x181a22860) + i * 4) = 0f; *(float*)(V(0x181a228e0) + i * 4) = 0f; }
                revProc(0, ip, 0, 0, op);
                *(uint*)V(0x181a62a34) = (*(uint*)V(0x181a62a34) - 0x20) & 0xFFFF;
                for (int k = 0; k < 32 && written < irCount; k++, written++)
                { samples[written * 2] = op[k]; samples[written * 2 + 1] = op[32 + k]; }
            }
            var bytes = new byte[samples.Length * 4];
            Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
            File.WriteAllBytes(irOut, bytes);
        }
        Console.WriteLine($"revir type={irType} -> {irOut} ({irCount} frames, interleaved f32)");
        return;
    }
    // choir mode: the chorus network's IMPULSE RESPONSE, the same way `revir` takes the reverb's --
    //   by calling the module's own `fx_chorus_stage_l` (0x1800851c0) with a controlled input. Same
    //   contract: 32 samples per call from `input`, 32 floats of L at `out` and 32 of R at
    //   `out+0x80`, and the ring cursor advanced by the caller.
    //
    //   The chorus is swept by a free-running LFO, so its response is only reproducible from a
    //   known phase: this prints the phase accumulator (0x181a62af8) and its increment beside the
    //   capture, and a reimplementation has to be started from the same phase for a sample-exact
    //   comparison to mean anything.
    //   args: dll choir <out.f32> <samples> [choType]
    if (args.Length > 1 && args[1] == "choir")
    {
        string irOut = args[2];
        int irCount = int.Parse(args[3]);
        int irType = args.Length > 4 ? int.Parse(args[4]) : -1;
        setSR(32000f); setBS(512); activate(32000f, 512); setThr();
        GsReset();
        if (irType >= 0) SendSysEx(Dt1(0x40, 0x01, 0x38, (byte)irType));
        void CCch(int c, int v) => shortIn((uint)((0xB0 | 0) | (c << 8) | (v << 16)), 0);
        CCch(0, 0); CCch(32, 0); CCch(7, 127); CCch(10, 64); CCch(91, 0); CCch(93, 127);
        shortIn((uint)(0xC0 | (12 << 8)), 0);
        flush();
        var cw = new float[512]; var cwr = new float[512];
        fixed (float* pl = cw, pr = cwr) for (int i = 0; i < 8; i++) process(pl, pr, 512);
        shortIn((uint)(0x90 | (60 << 8) | (100 << 16)), 0); flush();
        fixed (float* pl = cw, pr = cwr) for (int i = 0; i < 8; i++) process(pl, pr, 512);
        shortIn((uint)(0x80 | (60 << 8) | (64 << 16)), 0); flush();
        fixed (float* pl = cw, pr = cwr) for (int i = 0; i < 250; i++) process(pl, pr, 512);

        var choProc = (delegate* unmanaged[Cdecl]<long,float*,long,void>)(b + 0x851c0);
        long Vc(long va) => b + (va - 0x180000000L);
        // The sweep LFO free-runs, so a capture is only comparable against a reimplementation
        // started from the same phase. Rather than ask the other side to adopt this one's phase,
        // pin the engine to zero -- where a reimplementation's accumulator naturally starts.
        *(int*)Vc(0x181a62af8) = 0;
        Console.WriteLine($"lfoPhase={*(int*)Vc(0x181a62af8)} (pinned) lfoInc={*(int*)Vc(0x181a62afc)}"
            + $" gainIn={*(float*)Vc(0x181a6ef70)} gainOut={*(float*)Vc(0x181a6f0f0)} cursor={*(uint*)Vc(0x181a62a34):X}");
        var cin = new float[32]; var cout = new float[64];
        fixed (float* ip = cin, op = cout)
        {
            var samples = new float[irCount * 2];
            int written = 0;
            for (int block = 0; written < irCount; block++)
            {
                for (int k = 0; k < 32; k++) ip[k] = 0f;
                if (block == 0) ip[0] = 1f;
                choProc(0, ip, (long)op);
                *(uint*)Vc(0x181a62a34) = (*(uint*)Vc(0x181a62a34) - 0x20) & 0xFFFF;
                for (int k = 0; k < 32 && written < irCount; k++, written++)
                { samples[written * 2] = op[k]; samples[written * 2 + 1] = op[32 + k]; }
            }
            var bytes = new byte[samples.Length * 4];
            Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
            File.WriteAllBytes(irOut, bytes);
        }
        Console.WriteLine($"choir type={irType} -> {irOut} ({irCount} frames, interleaved f32)");
        return;
    }
    // dlyir mode: the GS system delay's IMPULSE RESPONSE, taken from the live engine the same way
    //   `revir` and `choir` take theirs. The delay's processor is `fx_chorus_stage_r`
    //   (0x180085460), whose Ghidra name is misleading: it is not the chorus's right channel, it is
    //   the delay -- three taps (0x181a629f4/f8/fc) with their own gains, a feedback path, a
    //   pre-LPF, and a send into the reverb, working a region of the shared delay memory 0x8000
    //   below the chorus's. It reads the chorus's send-to-delay output (0x181a22960) as well as its
    //   own bus, so that is zeroed here.
    //
    //   The delay is OFF until a macro selects it -- the power-on delay level is zero -- which is
    //   why a part's delay send appears to do nothing on a bare GS reset. The macro goes first.
    //   args: dll dlyir <out.f32> <samples> [dlyType]
    if (args.Length > 1 && args[1] == "dlyir")
    {
        string irOut = args[2];
        int irCount = int.Parse(args[3]);
        int irType = args.Length > 4 ? int.Parse(args[4]) : 0;
        setSR(32000f); setBS(512); activate(32000f, 512); setThr();
        GsReset();
        SendSysEx(Dt1(0x40, 0x01, 0x50, (byte)irType));       // delay macro
        SendSysEx(Dt1(0x40, 0x11, 0x2C, 0x7F));               // part 1 delay send
        void CCd2(int c, int v) => shortIn((uint)((0xB0 | 0) | (c << 8) | (v << 16)), 0);
        CCd2(0, 0); CCd2(32, 0); CCd2(7, 127); CCd2(10, 64); CCd2(91, 0); CCd2(93, 0);
        shortIn((uint)(0xC0 | (12 << 8)), 0);
        flush();
        var dw = new float[512]; var dwr = new float[512];
        fixed (float* pl = dw, pr = dwr) for (int i = 0; i < 8; i++) process(pl, pr, 512);
        shortIn((uint)(0x90 | (60 << 8) | (100 << 16)), 0); flush();
        fixed (float* pl = dw, pr = dwr) for (int i = 0; i < 8; i++) process(pl, pr, 512);
        shortIn((uint)(0x80 | (60 << 8) | (64 << 16)), 0); flush();
        fixed (float* pl = dw, pr = dwr) for (int i = 0; i < 400; i++) process(pl, pr, 512);

        var dlyProc = (delegate* unmanaged[Cdecl]<long,float*,long,float*,void>)(b + 0x85460);
        long Vd(long va) => b + (va - 0x180000000L);
        Console.WriteLine($"taps c={*(short*)Vd(0x181a629f4)} l={*(short*)Vd(0x181a629f8)} r={*(short*)Vd(0x181a629fc)}"
            + $" gC={*(float*)Vd(0x181a62a2c)} gL={*(float*)Vd(0x181a62a30)} gR={*(float*)Vd(0x181a62a28)}"
            + $" fb={*(float*)Vd(0x181a62a24)} lpfIn={*(float*)Vd(0x181a629ec)} lpfFb={*(float*)Vd(0x181a629e8)}"
            + $" gainIn={*(float*)Vd(0x181a6f1f0)} gainOut={*(float*)Vd(0x181a6f170)}");
        var din = new float[32]; var dout = new float[64];
        fixed (float* ip = din, op = dout)
        {
            var samples = new float[irCount * 2];
            int written = 0;
            for (int block = 0; written < irCount; block++)
            {
                for (int k = 0; k < 32; k++) ip[k] = 0f;
                if (block == 0) ip[0] = 1f;
                for (int i = 0; i < 32; i++) *(float*)(Vd(0x181a22960) + i * 4) = 0f;
                dlyProc(0, ip, 0, op);
                *(uint*)Vd(0x181a62a34) = (*(uint*)Vd(0x181a62a34) - 0x20) & 0xFFFF;
                for (int k = 0; k < 32 && written < irCount; k++, written++)
                { samples[written * 2] = op[k]; samples[written * 2 + 1] = op[32 + k]; }
            }
            var bytes = new byte[samples.Length * 4];
            Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
            File.WriteAllBytes(irOut, bytes);
        }
        Console.WriteLine($"dlyir type={irType} -> {irOut} ({irCount} frames, interleaved f32)");
        return;
    }
    // partprobe mode: read a live part's send bytes after each of the controls that claim to set
    //   them, so a reimplementation can see which byte a control really writes rather than trusting
    //   an address list. The bus-assign reads reverb from part+0x3e3, and the part's SECOND send is
    //   either chorus (part+0x3e2, bus 0x3d) or delay (part+0x44a, bus 0x30) -- selected by
    //   part+0x45c, not sent alongside. That selector is why a part can look like it has a delay
    //   send set and still put nothing on the delay bus.
    //   args: dll partprobe [prog] [ch]
    if (args.Length > 1 && args[1] == "partprobe")
    {
        int ppProg = args.Length > 2 ? int.Parse(args[2]) : 115;
        int ppCh = args.Length > 3 ? int.Parse(args[3]) : 0;
        setSR(32000f); setBS(512); activate(32000f, 512); setThr();
        long fbP = b + 0x1a1b5b8;
        var getVCp = (delegate* unmanaged[Cdecl]<int, long>)(b + 0x5c360);
        long vcp = getVCp(0);
        var lp = new float[512]; var rp = new float[512];
        void CCp(int c, int v) => shortIn((uint)((0xB0 | ppCh) | (c << 8) | (v << 16)), 0);
        GsReset(); flush();
        fixed (float* pl = lp, pr = rp) for (int i = 0; i < 8; i++) process(pl, pr, 512);
        shortIn((uint)((0xC0 | ppCh) | (ppProg << 8)), 0); flush();
        fixed (float* pl = lp, pr = rp) process(pl, pr, 512);

        long PartOf()
        {
            for (int v = 0; v < 64; v++)
                if ((*(byte*)(fbP + v * 0x50) & 1) != 0) return *(long*)(vcp + (long)v * 0x220 + 0x128);
            return 0;
        }
        void Strike()
        {
            shortIn((uint)((0x90 | ppCh) | (60 << 8) | (110 << 16)), 0); flush();
            fixed (float* pl = lp, pr = rp) for (int i = 0; i < 3; i++) process(pl, pr, 256);
        }
        void Dump(string tag)
        {
            long part = PartOf();
            if (part == 0) { Console.WriteLine($"{tag}: no sounding voice"); return; }
            Console.WriteLine($"{tag,-26} 0x13={*(byte*)(part + 0x13):X2}"
                + $" rev(3e3)={*(byte*)(part + 0x3e3),3} cho(3e2)={*(byte*)(part + 0x3e2),3}"
                + $" dly(44a)={*(byte*)(part + 0x44a),3} sel(45c)={*(byte*)(part + 0x45c),3}"
                + $" rx(3d6)=0x{*(ushort*)(part + 0x3d6):X4}");
        }
        Strike(); Dump("after GS reset");
        // The part's tone map: an SC-55-era part may simply not have a delay send, where an
        // SC-88/8820 one does.
        ToneMap0(ppCh, 4); flush(); Strike(); Dump("after tone map 4 (SC-8820)");
        CCp(94, 127); flush(); Strike(); Dump("+ CC#94=127 on map 4");
        SendSysEx(Dt1(0x40, 0x00, 0x7F, 0x01)); flush();   // System Mode Set 1
        shortIn((uint)((0xC0 | ppCh) | (ppProg << 8)), 0); flush();
        CCp(94, 127); flush(); Strike(); Dump("+ mode set 1, CC#94");
        // Delay send first, with chorus still at zero: the engine's second send is a selector
        // between chorus and delay rather than two independent sends, so the order may matter.
        CCp(94, 127); flush(); Strike(); Dump("CC#94=127 (chorus still 0)");
        SendSysEx(Dt1(0x40, 0x01, 0x50, 0x00)); flush(); Strike(); Dump("+ delay macro");
        byte[] preset = { 0x00, 0x61, 0x01, 0x01, 0x7F, 0x00, 0x00, 0x40, 0x50, 0x00 };
        for (int i = 0; i < preset.Length; i++)
            SendSysEx(Dt1(0x40, 0x01, (byte)(0x51 + i), preset[i]));
        flush(); Strike(); Dump("+ delay params 51-5A");
        CCp(94, 127); flush(); Strike(); Dump("+ CC#94 again");
        CCp(93, 90); flush(); Strike(); Dump("+ CC#93=90 (chorus on)");
        CCp(93, 0); flush(); CCp(94, 127); flush(); Strike(); Dump("+ chorus 0, CC#94=127");
        return;
    }
    // partmap mode: write every GS part-parameter address in turn and report which byte of the
    //   live part struct each one moved. An address list copied from documentation -- or worse,
    //   from the XG block, which is laid out differently -- cannot be checked any other way, and a
    //   wrong entry is silent: the message is accepted and lands somewhere harmless.
    //   args: dll partmap [prog] [ch] [firstAddr] [lastAddr] [blockBase: 10 or 40]
    if (args.Length > 1 && args[1] == "partmap")
    {
        int pmProg = args.Length > 2 ? int.Parse(args[2]) : 115;
        int pmCh = args.Length > 3 ? int.Parse(args[3]) : 0;
        int pmFirst = args.Length > 4 ? Convert.ToInt32(args[4], 16) : 0x00;
        int pmLast = args.Length > 5 ? Convert.ToInt32(args[5], 16) : 0x5F;
        // Which part block to sweep. `10` is the ordinary one; `40` is the **extended** block, and
        // it is where most of a part's record actually lives -- about 58 of the bytes a patch bulk
        // dump carries have no `40 1x` address at all and are reached only from here. Sweeping just
        // `10` leaves those looking undecodable when they are merely addressed elsewhere.
        int pmBase = args.Length > 6 ? Convert.ToInt32(args[6], 16) : 0x10;
        const int WINDOW = 0x480;
        setSR(32000f); setBS(512); activate(32000f, 512); setThr();
        long fbM = b + 0x1a1b5b8;
        var getVCm = (delegate* unmanaged[Cdecl]<int, long>)(b + 0x5c360);
        long vcm = getVCm(0);
        var lm = new float[512]; var rm = new float[512];
        GsReset(); flush();
        fixed (float* pl = lm, pr = rm) for (int i = 0; i < 8; i++) process(pl, pr, 512);
        shortIn((uint)((0xC0 | pmCh) | (pmProg << 8)), 0); flush();
        fixed (float* pl = lm, pr = rm) process(pl, pr, 512);
        long PartOfM()
        {
            for (int v = 0; v < 64; v++)
                if ((*(byte*)(fbM + v * 0x50) & 1) != 0) return *(long*)(vcm + (long)v * 0x220 + 0x128);
            return 0;
        }
        void StrikeM()
        {
            shortIn((uint)((0x90 | pmCh) | (60 << 8) | (110 << 16)), 0); flush();
            fixed (float* pl = lm, pr = rm) for (int i = 0; i < 3; i++) process(pl, pr, 256);
        }
        StrikeM();
        long partM = PartOfM();
        if (partM == 0) { Console.WriteLine("no sounding voice"); return; }
        var before = new byte[WINDOW];
        var after = new byte[WINDOW];
        System.Runtime.InteropServices.Marshal.Copy((nint)partM, before, 0, WINDOW);
        for (int addr = pmFirst; addr <= pmLast; addr++)
        {
            // A value no default is likely to already hold, so a write that lands shows up.
            SendSysEx(Dt1(0x40, (byte)(pmBase | BlockNum(pmCh)), (byte)addr, 0x33));
            flush();
            fixed (float* pl = lm, pr = rm) process(pl, pr, 512);
            // The part pointer is cached rather than re-resolved from a sounding voice: some of
            // these addresses (Rx switches, key range, Rx channel) stop the part from sounding,
            // and a probe that needs a live voice to find the part loses it exactly where the
            // interesting writes are.
            System.Runtime.InteropServices.Marshal.Copy((nint)partM, after, 0, WINDOW);
            var moved = new System.Collections.Generic.List<string>();
            for (int off = 0; off < WINDOW; off++)
                if (before[off] != after[off]) moved.Add($"+{off:X3}:{before[off]:X2}->{after[off]:X2}");
            Console.WriteLine($"40 {pmBase:X}x {addr:X2}  {(moved.Count == 0 ? "(no change)" : string.Join(" ", moved))}");
            Array.Copy(after, before, WINDOW);
        }
        return;
    }
    // efxir mode: an insertion-EFX algorithm's response to a controlled signal, taken by driving
    //   the module's own DSP function directly -- the counterpart of `revir`/`choir`/`dlyir` for
    //   the EFX block, and the only way to compare a modulated algorithm sample for sample.
    //
    //   The problem it solves is phase. Rotary's two rotors, and every other modulated type, run
    //   their accumulators out of the shared state buffer, free-running from whatever the engine
    //   was doing beforehand. Comparing a render against a reimplementation whose accumulators
    //   start at zero then compares two different points of a sweep, and the difference looks like
    //   a transcription fault when it is only a phase offset. So this resets **both** delay buffers
    //   to the anti-denormal seed the allocator fills them with -- which is where a fresh
    //   reimplementation starts -- and only then begins.
    //
    //   The loop is `fx_process_block`'s inner one: wrap both delay lines, call the algorithm with
    //   the input doubled, halve what comes back. Coefficients come from the type select alone, so
    //   unlike the send networks no note has to be played first.
    //   args: dll efxir <typeMsbHex> <typeLsbHex> <out.f32> <samples> [impulse|sine] [addr val ...]
    if (args.Length > 1 && args[1] == "efxir")
    {
        int msb = Convert.ToInt32(args[2], 16), lsb = Convert.ToInt32(args[3], 16);
        string irOut = args[4];
        int irCount = int.Parse(args[5]);
        string shape = args.Length > 6 ? args[6] : "impulse";
        setSR(32000f); setBS(512); activate(32000f, 512); setThr();
        var el = new float[512]; var er = new float[512];
        GsReset(); flush();
        fixed (float* pl = el, pr = er) for (int i = 0; i < 8; i++) process(pl, pr, 512);
        SendSysEx(Dt1(0x40, 0x03, 0x00, (byte)msb, (byte)lsb)); flush();
        fixed (float* pl = el, pr = er) for (int i = 0; i < 8; i++) process(pl, pr, 512);
        for (int p = 7; p + 1 < args.Length; p += 2)
        {
            SendSysEx(Dt1(0x40, 0x03, (byte)Convert.ToInt32(args[p], 16),
                          (byte)Convert.ToInt32(args[p + 1], 16)));
            flush();
            fixed (float* pl = el, pr = er) for (int i = 0; i < 8; i++) process(pl, pr, 512);
        }

        long Ve(long va) => b + (va - 0x180000000L);
        var wrap = (delegate* unmanaged[Cdecl]<long, void>)(b + 0x89830);
        // The algorithms take their two input samples in INTEGER registers carrying the float's
        // bit pattern, not in XMM -- which is what the decompiler is saying when it types them
        // `undefined4` and only ever stores them. Declaring them as floats here compiles and runs,
        // and produces garbage from the second sample on: the first output depends only on state,
        // so it looks correct and hides the mistake.
        var algoTable = Ve(0x181895190);
        int algoIndex = *(int*)Ve(0x181a63460);
        var algo = (delegate* unmanaged[Cdecl]<int, int, float*, float*, long, long, long, void>)
            (*(long*)(algoTable + (long)algoIndex * 8));
        long bufA = *(long*)Ve(0x181a62cd8);
        long bufB = *(long*)Ve(0x181a63468);

        // Both buffers back to the seed the allocator wrote, so every accumulator in the algorithm
        // -- rotor phases included -- starts where a reimplementation's does.
        void Seed(long s)
        {
            long baseptr = *(long*)s;
            int size = *(int*)(s + 0xC);
            for (int i = 0; i < size; i++) *(float*)(baseptr + i * 4) = 1e-05f;
            *(int*)(s + 0x10) = size - *(int*)(s + 8);
        }
        Seed(bufA); Seed(bufB);
        Console.WriteLine($"efxir type={msb:X2} {lsb:X2} algo={algoIndex} shape={shape}");
        Console.WriteLine($"  bufA struct=0x{bufA:X} base=0x{*(long*)bufA:X} window={*(int*)(bufA+8)} size={*(int*)(bufA+0xC)} cursor={*(int*)(bufA+0x10)}");
        Console.WriteLine($"  bufB struct=0x{bufB:X} base=0x{*(long*)bufB:X} window={*(int*)(bufB+8)} size={*(int*)(bufB+0xC)} cursor={*(int*)(bufB+0x10)}");
        Console.WriteLine($"  algo ptr=0x{(long)algo:X} coef[0]={*(float*)Ve(0x181a1af70)} coef[6]={*(float*)(Ve(0x181a1af70)+24)}");

        // The output pair is two pointers 0x20 floats apart inside one buffer, exactly as
        // `fx_process_block` hands them over -- the algorithms are written against that spacing.
        var samples = new float[irCount * 2];
        var pair = new float[0x40];
        fixed (float* pp = pair)
        {
            for (int n = 0; n < irCount; n++)
            {
                float x = shape == "sine"
                    ? (float)(0.25 * Math.Sin(2.0 * Math.PI * 440.0 * n / 32000.0))
                    : (n == 0 ? 1f : 0f);
                wrap(bufA); wrap(bufB);
                long aBase = *(long*)bufA + (long)(*(int*)(bufA + 0x10)) * 4;
                long bBase = *(long*)bufB + (long)(*(int*)(bufB + 0x10)) * 4;
                float doubled = x + x;
                int bits = *(int*)&doubled;
                algo(bits, bits, pp, pp + 0x20, aBase, bBase, Ve(0x181a1af70));
                samples[n * 2] = pp[0] * 0.5f;
                samples[n * 2 + 1] = pp[0x20] * 0.5f;
            }
        }
        var bytes = new byte[samples.Length * 4];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        File.WriteAllBytes(irOut, bytes);
        Console.WriteLine($"efxir -> {irOut} ({irCount} frames, interleaved f32)");
        return;
    }
    // efxdump mode: select an insertion-EFX type over SysEx in the LIVE engine and dump the block's
    //   programmed state -- the float coefficient file `g_fx_coef_f32` (0x181a1af70, 0x180 floats),
    //   the reverb tap program (0x181a0f108, 34 ints) and the register mirror (0x181a73cc0 + 0x200,
    //   0x180 words). This is the ground truth a reimplementation's register machine diffs against:
    //   a wrong curve, width kind or preset slice shows up as the exact register that differs.
    //   args: dll efxdump <typeMsbHex> <typeLsbHex> <out.bin> [p1Hex p2Hex ...]
    if (args.Length > 1 && args[1] == "efxdump")
    {
        int msb = Convert.ToInt32(args[2], 16), lsb = Convert.ToInt32(args[3], 16);
        string outp = args[4];
        setSR(32000f); setBS(512); activate(32000f, 512); setThr();
        var el = new float[512]; var er = new float[512];
        GsReset(); flush();
        fixed (float* pl = el, pr = er) for (int i = 0; i < 8; i++) process(pl, pr, 512);
        SendSysEx(Dt1(0x40, 0x03, 0x00, (byte)msb, (byte)lsb)); flush();
        fixed (float* pl = el, pr = er) for (int i = 0; i < 8; i++) process(pl, pr, 512);
        for (int p = 5; p + 1 < args.Length; p += 2)
        {
            SendSysEx(Dt1(0x40, 0x03, (byte)Convert.ToInt32(args[p], 16),
                          (byte)Convert.ToInt32(args[p + 1], 16)));
            flush();
            fixed (float* pl = el, pr = er) for (int i = 0; i < 8; i++) process(pl, pr, 512);
        }
        var dump = new byte[0x180 * 4 + 34 * 4 + 0x180 * 4];
        System.Runtime.InteropServices.Marshal.Copy((nint)(b + 0x1a1af70), dump, 0, 0x180 * 4);
        System.Runtime.InteropServices.Marshal.Copy((nint)(b + 0x1a0f108), dump, 0x180 * 4, 34 * 4);
        System.Runtime.InteropServices.Marshal.Copy((nint)(b + 0x1a73cc0 + 0x200), dump, 0x180 * 4 + 34 * 4, 0x180 * 4);
        File.WriteAllBytes(outp, dump);
        Console.WriteLine($"efxdump {msb:X2} {lsb:X2} -> {outp}");
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
    // volramp mode: trace the per-voice PART-VOLUME gain -- voice_ctrl_ramp_b's output, found by
    //   volscan at DAT_181a1cbb0 and only four floats wide -- across a single CC7 step. The buffer
    //   is a zero-order hold rewritten each call, so one read per rendered chunk is one point on
    //   the glide. args: dll volramp <prog> <note> <vel> <chunk> <npoints> [map] [cc7after]
    if (args.Length > 1 && args[1] == "volramp")
    {
        int pg=args.Length>2?int.Parse(args[2]):19, nt=args.Length>3?int.Parse(args[3]):96, vel=args.Length>4?int.Parse(args[4]):110;
        int chunk=args.Length>5?int.Parse(args[5]):16; int npts=args.Length>6?int.Parse(args[6]):200;
        int map=args.Length>7?int.Parse(args[7]):4; int after=args.Length>8?int.Parse(args[8]):0;
        // Which controller to step. 7 is volume, the case this was written for; 11 is expression,
        // which reaches the same part-volume gain and needed checking for whether it slews at all.
        int ccN=args.Length>9?int.Parse(args[9]):7;
        // Base CC#7, so the OTHER controller can be held off full. Volume and expression are
        // documented as entering symmetrically and being squared together; that is only actually
        // exercised when both are away from 127, which is the case a real file presents.
        int volBase=args.Length>10?int.Parse(args[10]):127;
        setSR(32000f); setBS(512); activate(32000f,512); setThr();
        void CCr(int c,int v)=>shortIn((uint)((0xB0|0)|(c<<8)|(v<<16)),0);
        if(map>=1&&map<=4){ GsReset(); for(int c=0;c<16;c++) ToneMap0(c,map); } else Gm1On();
        CCr(7,volBase);CCr(11,127);CCr(10,64);CCr(91,0);CCr(93,0);
        shortIn((uint)(0xC0|(pg<<8)),0);
        var l4=new float[512]; var r4=new float[512];
        flush();
        fixed(float* pl=l4,pr=r4) for(int i=0;i<8;i++) process(pl,pr,512);
        shortIn((uint)(0x90|(nt<<8)|(vel<<16)),0); flush();
        fixed(float* pl=l4,pr=r4) for(int i=0;i<40;i++) process(pl,pr,512);   // settle fully
        long gb=b+0x1a1cbb0;
        Console.WriteLine($"volramp prog={pg} note={nt} chunk={chunk} cc{ccN} 127 -> {after}");
        Console.WriteLine("sample,gain");
        int t=0;
        // a few points at rest first, then the step
        for(int i=0;i<4;i++){ Console.WriteLine($"{t},{*(float*)gb:0.00000000}");
            fixed(float* pl=l4,pr=r4) process(pl,pr,(uint)chunk); t+=chunk; }
        CCr(ccN,after); flush();
        for(int i=0;i<npts;i++){ Console.WriteLine($"{t},{*(float*)gb:0.00000000}");
            fixed(float* pl=l4,pr=r4) process(pl,pr,(uint)chunk); t+=chunk; }
        return;
    }
    // volscan mode: find the per-voice PART-VOLUME gain buffer by observation rather than by
    //   address. Hold a note, snapshot a memory window, move CC7, snapshot again, and report every
    //   float that sat near 1.0 and then moved by the ratio the squared volume law predicts.
    //   args: dll volscan <prog> <note> <vel> [map] [cc7after]
    if (args.Length > 1 && args[1] == "volscan")
    {
        int pg=args.Length>2?int.Parse(args[2]):19, nt=args.Length>3?int.Parse(args[3]):96, vel=args.Length>4?int.Parse(args[4]):110;
        int map=args.Length>5?int.Parse(args[5]):4; int after=args.Length>6?int.Parse(args[6]):64;
        setSR(32000f); setBS(512); activate(32000f,512); setThr();
        void CCs(int c,int v)=>shortIn((uint)((0xB0|0)|(c<<8)|(v<<16)),0);
        if(map>=1&&map<=4){ GsReset(); for(int c=0;c<16;c++) ToneMap0(c,map); } else Gm1On();
        CCs(7,127);CCs(10,64);CCs(91,0);CCs(93,0);
        shortIn((uint)(0xC0|(pg<<8)),0);
        var l3=new float[512]; var r3=new float[512];
        flush();
        fixed(float* pl=l3,pr=r3) for(int i=0;i<8;i++) process(pl,pr,512);
        shortIn((uint)(0x90|(nt<<8)|(vel<<16)),0); flush();
        fixed(float* pl=l3,pr=r3) for(int i=0;i<40;i++) process(pl,pr,512);   // settle fully

        long lo=b+0x1a10000, hi=b+0x1a20000; int n=(int)((hi-lo)/4);
        var before=new float[n];
        for(int i=0;i<n;i++) before[i]=*(float*)(lo+i*4L);
        CCs(7,after); flush();
        fixed(float* pl=l3,pr=r3) for(int i=0;i<8;i++) process(pl,pr,512);    // well past any glide
        double want = (double)after*after/(127.0*127.0);
        Console.WriteLine($"volscan prog={pg} note={nt} cc7 127 -> {after}, expected ratio {want:0.0000}");
        int hits=0;
        for(int i=0;i<n;i++){
            float a=before[i], c=*(float*)(lo+i*4L);
            if(a>0.90f && a<1.01f && c>0f){
                double ratio=c/a;
                if(Math.Abs(ratio-want)/want < 0.03){
                    Console.WriteLine($"  +0x{(i*4):x6}  (VA 0x1{(0x81a10000+i*4L):x})  {a:0.000000} -> {c:0.000000}  ratio {ratio:0.0000}");
                    if(++hits>=40) break;
                }
            }
        }
        Console.WriteLine($"{hits} candidates");
        return;
    }
    // panramp mode: trace the per-voice PAN gain pair across a CC10 jump. Unlike the volume fader,
    //   pan does not go through voice_ctrl_ramp_b at all -- voice_pan_slew slews the 0..127
    //   *position* and the L/R gains fall out of a table pair, landing as two per-voice scalars at
    //   DAT_181a1d930 / DAT_181a1da30. args: dll panramp <prog> <note> <vel> <chunk> <npoints> [map] [cc10after]
    if (args.Length > 1 && args[1] == "panramp")
    {
        int pg=args.Length>2?int.Parse(args[2]):19, nt=args.Length>3?int.Parse(args[3]):96, vel=args.Length>4?int.Parse(args[4]):110;
        int chunk=args.Length>5?int.Parse(args[5]):32; int npts=args.Length>6?int.Parse(args[6]):400;
        int map=args.Length>7?int.Parse(args[7]):4; int after=args.Length>8?int.Parse(args[8]):127;
        setSR(32000f); setBS(512); activate(32000f,512); setThr();
        void CCp(int c,int v)=>shortIn((uint)((0xB0|0)|(c<<8)|(v<<16)),0);
        if(map>=1&&map<=4){ GsReset(); for(int c=0;c<16;c++) ToneMap0(c,map); } else Gm1On();
        CCp(7,127);CCp(10,64);CCp(91,0);CCp(93,0);
        shortIn((uint)(0xC0|(pg<<8)),0);
        var l5=new float[512]; var r5=new float[512];
        flush();
        fixed(float* pl=l5,pr=r5) for(int i=0;i<8;i++) process(pl,pr,512);
        shortIn((uint)(0x90|(nt<<8)|(vel<<16)),0); flush();
        fixed(float* pl=l5,pr=r5) for(int i=0;i<40;i++) process(pl,pr,512);
        long gl=b+0x1a1d930, gr=b+0x1a1da30;
        Console.WriteLine($"panramp prog={pg} note={nt} chunk={chunk} cc10 64 -> {after}");
        Console.WriteLine("sample,left,right");
        int t=0;
        for(int i=0;i<3;i++){ Console.WriteLine($"{t},{*(float*)gl:0.00000000},{*(float*)gr:0.00000000}");
            fixed(float* pl=l5,pr=r5) process(pl,pr,(uint)chunk); t+=chunk; }
        CCp(10,after); flush();
        for(int i=0;i<npts;i++){ Console.WriteLine($"{t},{*(float*)gl:0.00000000},{*(float*)gr:0.00000000}");
            fixed(float* pl=l5,pr=r5) process(pl,pr,(uint)chunk); t+=chunk; }
        return;
    }
    // sendramp mode: trace all four per-voice mix scalars -- the pan pair at DAT_181a1d930 /
    //   DAT_181a1da30 and the two send slots at DAT_181a1db30 / DAT_181a1dc30 -- across a jump on
    //   one controller, to see which move and how fast. Each word also carries a 6-bit bus number
    //   in its low bits, mirrored at DAT_181a6e4b0 / DAT_181a6e7b0; those are printed too.
    //   args: dll sendramp <prog> <note> <vel> <cc> <before> <after> <chunk> <npoints> [map]
    if (args.Length > 1 && args[1] == "sendramp")
    {
        int pg=args.Length>2?int.Parse(args[2]):19, nt=args.Length>3?int.Parse(args[3]):96, vel=args.Length>4?int.Parse(args[4]):110;
        int cc=args.Length>5?int.Parse(args[5]):91; int before=args.Length>6?int.Parse(args[6]):0;
        int after=args.Length>7?int.Parse(args[7]):127;
        int chunk=args.Length>8?int.Parse(args[8]):32; int npts=args.Length>9?int.Parse(args[9]):400;
        int map=args.Length>10?int.Parse(args[10]):4;
        // Optional "a2,val" part-parameter poke at 40 41 a2, to steer the bus-assign branch.
        string poke=args.Length>11?args[11]:null;
        setSR(32000f); setBS(512); activate(32000f,512); setThr();
        void CCs2(int c,int v)=>shortIn((uint)((0xB0|0)|(c<<8)|(v<<16)),0);
        if(map>=1&&map<=4){ GsReset(); for(int c=0;c<16;c++) ToneMap0(c,map); } else Gm1On();
        if(poke!=null){ var pp=poke.Split(','); 
            SendSysEx(Dt1(0x40,0x41,Convert.ToByte(pp[0],16),Convert.ToByte(pp[1],16)));
            Console.WriteLine($"poked 40 41 {pp[0]} {pp[1]}"); }
        CCs2(7,127);CCs2(10,64);CCs2(91,0);CCs2(93,0);CCs2(cc,before);
        shortIn((uint)(0xC0|(pg<<8)),0);
        var l6=new float[512]; var r6=new float[512];
        flush();
        fixed(float* pl=l6,pr=r6) for(int i=0;i<8;i++) process(pl,pr,512);
        shortIn((uint)(0x90|(nt<<8)|(vel<<16)),0); flush();
        fixed(float* pl=l6,pr=r6) for(int i=0;i<40;i++) process(pl,pr,512);
        // The gain and bus arrays are both on a 0x100 stride; walk further than the four the
        // decode function was seen to touch, in case a voice carries more sends than that.
        const int slots = 8;
        Console.WriteLine($"sendramp prog={pg} note={nt} cc{cc} {before} -> {after} chunk={chunk}");
        Console.Write("sample");
        for(int k=0;k<slots;k++) Console.Write($",g{k}");
        for(int k=0;k<slots;k++) Console.Write($",bus{k}");
        Console.WriteLine();
        int t=0;
        void Row(){
            Console.Write(t);
            for(int k=0;k<slots;k++) Console.Write($",{*(float*)(b+0x1a1d930+k*0x100L):0.000000}");
            for(int k=0;k<slots;k++) Console.Write($",{*(uint*)(b+0x1a6e4b0+k*0x100L)}");
            Console.WriteLine();
        }
        for(int i=0;i<3;i++){ Row(); fixed(float* pl=l6,pr=r6) process(pl,pr,(uint)chunk); t+=chunk; }
        CCs2(cc,after); flush();
        for(int i=0;i<npts;i++){ Row(); fixed(float* pl=l6,pr=r6) process(pl,pr,(uint)chunk); t+=chunk; }
        return;
    }
    // ccscan mode: which floats in the mix scratch move when a controller does? Snapshot, send the
    //   controller, settle well past any slew, snapshot again, and list everything that changed.
    //   The generic form of volscan, for controllers whose gain law is not known in advance.
    //   args: dll ccscan <cc> <before> <after> [prog] [note] [vel] [map]
    if (args.Length > 1 && args[1] == "ccscan")
    {
        // "sx:a1,a2,a3" addresses a GS system parameter instead of a Control Change.
        string sx=args.Length>2 && args[2].StartsWith("sx:") ? args[2].Substring(3) : null;
        int cc=sx!=null?0:int.Parse(args[2]); int before=args.Length>3?int.Parse(args[3]):0;
        int after=args.Length>4?int.Parse(args[4]):127;
        int pg=args.Length>5?int.Parse(args[5]):19, nt=args.Length>6?int.Parse(args[6]):96;
        int vel=args.Length>7?int.Parse(args[7]):110; int map=args.Length>8?int.Parse(args[8]):4;
        setSR(32000f); setBS(512); activate(32000f,512); setThr();
        void CCc(int c,int v)=>shortIn((uint)((0xB0|0)|(c<<8)|(v<<16)),0);
        if(map>=1&&map<=4){ GsReset(); for(int c=0;c<16;c++) ToneMap0(c,map); } else Gm1On();
        CCc(7,127);CCc(10,64);CCc(91,0);CCc(93,0);CCc(cc,before);
        shortIn((uint)(0xC0|(pg<<8)),0);
        var l7=new float[512]; var r7=new float[512];
        flush();
        fixed(float* pl=l7,pr=r7) for(int i=0;i<8;i++) process(pl,pr,512);
        shortIn((uint)(0x90|(nt<<8)|(vel<<16)),0); flush();
        fixed(float* pl=l7,pr=r7) for(int i=0;i<40;i++) process(pl,pr,512);
        long lo=b+0x1a10000, hi=b+0x1a70000; int n=(int)((hi-lo)/4);
        var snap=new uint[n];
        for(int i=0;i<n;i++) snap[i]=*(uint*)(lo+i*4L);
        // A control render at the SAME setting, to learn which floats are just audio. Anything that
        // moves without a controller moving cannot be the controller's scalar.
        fixed(float* pl=l7,pr=r7) for(int i=0;i<60;i++) process(pl,pr,512);
        var noisy=new bool[n];
        for(int i=0;i<n;i++) noisy[i] = snap[i]!=*(uint*)(lo+i*4L);
        for(int i=0;i<n;i++) snap[i]=*(uint*)(lo+i*4L);
        CCc(cc,after); flush();
        fixed(float* pl=l7,pr=r7) for(int i=0;i<60;i++) process(pl,pr,512);
        Console.WriteLine($"ccscan cc{cc} {before} -> {after}");
        int hits=0;
        for(int i=0;i<n;i++){
            if(noisy[i]) continue;
            uint a=snap[i], c=*(uint*)(lo+i*4L);
            if(a!=c){
                Console.WriteLine($"  VA 0x1{(0x81a10000+i*4L):x}  u32 {a} -> {c}" +
                    $"   f32 {*(float*)&a:0.000000} -> {*(float*)&c:0.000000}");
                if(++hits>=40) break;
            }
        }
        Console.WriteLine($"{hits} stable floats moved");
        return;
    }
    // busscan mode: which part parameter opens the chorus/delay send route? The voice's fourth
    //   (gain, bus) slot is a direct bus route while part[+0x13] <= 0x1f and becomes a chorus send
    //   (bus 0x3d) or a delay send (bus 0x30) above it. Sweep every 40 1x NN, start a note, and
    //   report the ones that move that slot's bus off its default.
    //   args: dll busscan [value] [prog] [note] [map]
    if (args.Length > 1 && args[1] == "busscan")
    {
        int val=args.Length>2?int.Parse(args[2]):0x40;
        // The middle SysEx byte: 0x11 is the 40 1x part block (where the chorus and delay sends
        // live, at 21 and 2C), 0x41 the 40 4x one.
        int a2=args.Length>3?Convert.ToInt32(args[3],16):0x11;
        int pg=args.Length>4?int.Parse(args[4]):19, nt=args.Length>5?int.Parse(args[5]):96;
        int map=args.Length>6?int.Parse(args[6]):4;
        setSR(32000f); setBS(512); activate(32000f,512); setThr();
        void CCb(int c,int v)=>shortIn((uint)((0xB0|0)|(c<<8)|(v<<16)),0);
        var l8=new float[512]; var r8=new float[512];
        Console.WriteLine($"busscan: 40 {a2:X2} NN = 0x{val:X2}, watching slot 3's bus");
        uint baseline=0;
        for(int nn=0; nn<0x80; nn++){
            GsReset(); for(int c=0;c<16;c++) ToneMap0(c,map);
            CCb(7,127);CCb(10,64);CCb(91,64);CCb(93,64);
            shortIn((uint)(0xC0|(pg<<8)),0);
            flush();
            fixed(float* pl=l8,pr=r8) for(int i=0;i<4;i++) process(pl,pr,512);
            if(nn>=0) SendSysEx(Dt1(0x40,(byte)a2,(byte)nn,(byte)val));
            flush();
            fixed(float* pl=l8,pr=r8) process(pl,pr,512);
            shortIn((uint)(0x90|(nt<<8)|(110<<16)),0); flush();
            fixed(float* pl=l8,pr=r8) for(int i=0;i<4;i++) process(pl,pr,512);
            uint b2=*(uint*)(b+0x1a6e6b0), b3=*(uint*)(b+0x1a6e7b0);
            float g3=*(float*)(b+0x1a1dc30);
            if(nn==0) baseline=b3;
            if(b3!=baseline || b3==0x3d || b3==0x30)
                Console.WriteLine($"  40 {a2:X2} {nn:X2} -> slot2 bus {b2}, slot3 bus {b3} (0x{b3:X}), g3 {g3:0.000000}");
            shortIn((uint)(0x80|(nt<<8)|(64<<16)),0); flush();
            fixed(float* pl=l8,pr=r8) for(int i=0;i<2;i++) process(pl,pr,512);
        }
        Console.WriteLine($"baseline slot3 bus = {baseline}");
        return;
    }
    // partfind mode: locate the part struct by watching a byte follow CC#91, then read the whole
    //   struct around it. part[+0x3e3] is the reverb send, so an address whose byte tracks CC#91
    //   is that field and the part base is 0x3e3 below it. args: dll partfind [lo] [hi] (hex offsets)
    if (args.Length > 1 && args[1] == "partfind")
    {
        long lo=b+(args.Length>2?Convert.ToInt64(args[2],16):0x1a00000);
        long hi=b+(args.Length>3?Convert.ToInt64(args[3],16):0x1a70000);
        setSR(32000f); setBS(512); activate(32000f,512); setThr();
        void CCf(int c,int v)=>shortIn((uint)((0xB0|0)|(c<<8)|(v<<16)),0);
        GsReset(); for(int c=0;c<16;c++) ToneMap0(c,4);
        var l9=new float[512]; var r9=new float[512];
        int n=(int)(hi-lo);
        var seen=new byte[n][];
        // Three distinct values, all of which the field must follow. Two is not enough: a SysEx
        // echo buffer holding a run of ascending bytes matches a single transition by accident.
        byte[] probe={0x11,0x5a,0x2c};
        for(int k=0;k<probe.Length;k++){
            CCf(91,probe[k]); flush();
            fixed(float* pl=l9,pr=r9) for(int i=0;i<4;i++) process(pl,pr,512);
            var snapk=new byte[n];
            for(int i=0;i<n;i++) snapk[i]=*(byte*)(lo+i);
            for(int i=0;i<n;i++){ if(seen[i]==null) seen[i]=new byte[probe.Length]; seen[i][k]=snapk[i]; }
        }
        Console.WriteLine("partfind: bytes that followed CC#91 through 0x11, 0x5a, 0x2c");
        int hits=0;
        for(int i=0;i<n;i++){
            bool all=true;
            for(int k=0;k<probe.Length;k++) if(seen[i][k]!=probe[k]) { all=false; break; }
            if(all){
                long fld=lo+i, bse=fld-0x3e3;
                Console.WriteLine($"  +0x{(fld-b):x}  part base +0x{(bse-b):x}" +
                    $"   [+0x13]={*(byte*)(bse+0x13)}  [+0x45c]={*(byte*)(bse+0x45c)}" +
                    $"   [+0x3e2]={*(byte*)(bse+0x3e2)} [+0x44a]={*(byte*)(bse+0x44a)}");
                if(++hits>=12) break;
            }
        }
        Console.WriteLine($"{hits} candidates");
        return;
    }
    // pokebyte mode: write one byte of the part struct directly and see what the voice's slots do.
    //   Confirms both that a candidate part base is real (its neighbours a 0x488 stride away should
    //   hold the same field) and what part[+0x13] actually gates.
    //   args: dll pokebyte <baseOff hex> <fieldOff hex> <value> [prog] [note]
    if (args.Length > 1 && args[1] == "pokebyte")
    {
        // "auto" reads g_part_array_base @181a222a0 and takes part 0 (stride 0x488).
        long bse = args[2]=="auto" ? b+0x1a222a0 : b+Convert.ToInt64(args[2],16);
        long fld=Convert.ToInt64(args[3],16);
        int val=Convert.ToInt32(args[4]);
        int pg=args.Length>5?int.Parse(args[5]):19, nt=args.Length>6?int.Parse(args[6]):96;
        setSR(32000f); setBS(512); activate(32000f,512); setThr();
        void CCk(int c,int v)=>shortIn((uint)((0xB0|0)|(c<<8)|(v<<16)),0);
        GsReset(); for(int c=0;c<16;c++) ToneMap0(c,4);
        CCk(7,127);CCk(10,64);CCk(91,64);CCk(93,64);
        shortIn((uint)(0xC0|(pg<<8)),0);
        var la=new float[512]; var ra=new float[512];
        flush();
        fixed(float* pl=la,pr=ra) for(int i=0;i<4;i++) process(pl,pr,512);
        Console.WriteLine($"part base +0x{(bse-b):x} (g_part_array_base): [+0x13]={*(byte*)(bse+0x13)} " +
            $"[+0x3e2]={*(byte*)(bse+0x3e2)} [+0x3e3]={*(byte*)(bse+0x3e3)} " +
            $"[+0x44a]={*(byte*)(bse+0x44a)} [+0x45c]={*(byte*)(bse+0x45c)}");
        Console.WriteLine($"  stride check: part-1 [+0x3e3]={*(byte*)(bse-0x488+0x3e3)}, " +
            $"part+1 [+0x3e3]={*(byte*)(bse+0x488+0x3e3)}");
        *(byte*)(bse+fld) = (byte)val;
        Console.WriteLine($"  wrote [+0x{fld:x}] = {val}");
        shortIn((uint)(0x90|(nt<<8)|(110<<16)),0); flush();
        fixed(float* pl=la,pr=ra) for(int i=0;i<4;i++) process(pl,pr,512);
        for(int k=0;k<4;k++)
            Console.WriteLine($"  slot{k}: gain {*(float*)(b+0x1a1d930+k*0x100L):0.000000}" +
                $"  bus {*(uint*)(b+0x1a6e4b0+k*0x100L)} (0x{*(uint*)(b+0x1a6e4b0+k*0x100L):X})");
        return;
    }
    // progscan mode: does the chorus/delay send route depend on the PROGRAM rather than the part?
    //   The gate the bus-assign code reads is at +0x13 off the pointer at voice+0x128; if that
    //   pointer is the tone rather than the part, the route is a property of the instrument and no
    //   part parameter can move it. Sweep every program and report each one's slot buses.
    //   args: dll progscan [note] [map]
    if (args.Length > 1 && args[1] == "progscan")
    {
        int nt=args.Length>2?int.Parse(args[2]):60; int map=args.Length>3?int.Parse(args[3]):4;
        setSR(32000f); setBS(512); activate(32000f,512); setThr();
        void CCg(int c,int v)=>shortIn((uint)((0xB0|0)|(c<<8)|(v<<16)),0);
        var lb=new float[512]; var rb=new float[512];
        Console.WriteLine("progscan: slot2/slot3 bus per program");
        var tally=new Dictionary<string,List<int>>();
        for(int pg=0; pg<128; pg++){
            GsReset(); for(int c=0;c<16;c++) ToneMap0(c,map);
            CCg(7,127);CCg(10,64);CCg(91,64);CCg(93,64);
            shortIn((uint)(0xC0|(pg<<8)),0); flush();
            fixed(float* pl=lb,pr=rb) for(int i=0;i<4;i++) process(pl,pr,512);
            shortIn((uint)(0x90|(nt<<8)|(110<<16)),0); flush();
            fixed(float* pl=lb,pr=rb) for(int i=0;i<4;i++) process(pl,pr,512);
            uint b2=*(uint*)(b+0x1a6e6b0), b3=*(uint*)(b+0x1a6e7b0);
            string key=$"slot2 bus {b2} (0x{b2:X}), slot3 bus {b3} (0x{b3:X})";
            if(!tally.ContainsKey(key)) tally[key]=new List<int>();
            tally[key].Add(pg);
            shortIn((uint)(0x80|(nt<<8)|(64<<16)),0); flush();
            fixed(float* pl=lb,pr=rb) for(int i=0;i<2;i++) process(pl,pr,512);
        }
        foreach(var kv in tally){
            var progs=kv.Value;
            string list = progs.Count>12 ? $"{progs.Count} programs" : string.Join(",",progs);
            Console.WriteLine($"  {kv.Key}  <- {list}");
        }
        return;
    }
    // fxgain mode: read the send-effect gain ramp bank live, as floats. The bank is ten 32-float
    //   blocks the block loop multiplies its buffers by; six of them belong to the chorus, and two
    //   of those are the routes OUT of the chorus that no reimplementation here has ever carried:
    //   `fx_chorus_stage_l` writes (tap1+tap2) scaled by 0x181a6eff0 into the reverb's input buffer
    //   and by 0x181a6f070 into the delay's, while 0x181a6f0f0 is the plain return level. The
    //   function named `fx_chorus_stage_r` is the SYSTEM DELAY, not a right-hand chorus: it reads
    //   the 0x181a6f070 buffer and adds its own 0x181a6f270-scaled feed to the reverb, which is the
    //   delay's "send level to reverb" byte -- nonzero (36) in exactly one stored preset.
    //   The macro table's "chorus send to reverb" byte is 0 for all eight types, so the question a
    //   static read cannot answer is whether the route is genuinely silent by default or whether
    //   something else programs that block -- hence reading it out of the running engine instead.
    //   Each block is a RAMP: [0] and [31] differ while a parameter is moving, so both are printed.
    //   args: dll fxgain [cc91] [cc93] [choToRev] [choLevel]
    if (args.Length > 1 && args[1] == "fxgain")
    {
        int rev  = args.Length>2 ? int.Parse(args[2]) : 100;
        int cho  = args.Length>3 ? int.Parse(args[3]) : 127;
        int c2r  = args.Length>4 ? int.Parse(args[4]) : -1;
        int clvl = args.Length>5 ? int.Parse(args[5]) : -1;
        int dlvl = args.Length>6 ? int.Parse(args[6]) : -1;
        int d2r  = args.Length>7 ? int.Parse(args[7]) : -1;
        int c2d  = args.Length>8 ? int.Parse(args[8]) : -1;
        setSR(32000f); setBS(512); activate(32000f,512); setThr();
        void CCf(int c,int v)=>shortIn((uint)((0xB0|0)|(c<<8)|(v<<16)),0);
        GsReset(); for(int c=0;c<16;c++) ToneMap0(c,4);
        CCf(7,127); CCf(10,64); CCf(91,rev); CCf(93,cho);
        if (clvl >= 0) SendSysEx(Dt1(0x40,0x01,0x3A,(byte)clvl));
        if (c2r  >= 0) SendSysEx(Dt1(0x40,0x01,0x3F,(byte)c2r));
        if (c2d  >= 0) SendSysEx(Dt1(0x40,0x01,0x40,(byte)c2d));
        if (dlvl >= 0) SendSysEx(Dt1(0x40,0x01,0x58,(byte)dlvl));
        if (d2r  >= 0) SendSysEx(Dt1(0x40,0x01,0x5A,(byte)d2r));
        shortIn((uint)(0xC0|(48<<8)),0); flush();
        var lf=new float[512]; var rf=new float[512];
        fixed(float* pl=lf,pr=rf) for(int i=0;i<8;i++) process(pl,pr,512);
        shortIn((uint)(0x90|(60<<8)|(110<<16)),0); flush();
        fixed(float* pl=lf,pr=rf) for(int i=0;i<64;i++) process(pl,pr,512);
        // A second round of writes, then only ONE 32-sample block, so a block that RAMPS is caught
        // mid-move: [0] and [31] straddle the step and neither equals the target yet. A block that
        // steps outright reads the target in [0]. args 9..12 are the same four parameters again.
        if (args.Length > 9)
        {
            int c2r2  = args.Length>9  ? int.Parse(args[9])  : -1;
            int clvl2 = args.Length>10 ? int.Parse(args[10]) : -1;
            int dlvl2 = args.Length>11 ? int.Parse(args[11]) : -1;
            int d2r2  = args.Length>12 ? int.Parse(args[12]) : -1;
            if (clvl2 >= 0) SendSysEx(Dt1(0x40,0x01,0x3A,(byte)clvl2));
            if (c2r2  >= 0) SendSysEx(Dt1(0x40,0x01,0x3F,(byte)c2r2));
            if (dlvl2 >= 0) SendSysEx(Dt1(0x40,0x01,0x58,(byte)dlvl2));
            if (d2r2  >= 0) SendSysEx(Dt1(0x40,0x01,0x5A,(byte)d2r2));
            flush();
            Console.WriteLine("  blocks  choRet    choToRev  dlyRet    dlyToRev");
            int done = 0;
            foreach (int upto in new[]{0,1,2,4,8,16,32,64,256}) {
                fixed(float* pl=lf,pr=rf) for(; done<upto; done++) process(pl,pr,32);
                float G(long va)=>*(float*)(b + (va - 0x180000000L));
                Console.WriteLine($"  {upto,6}  {G(0x181a6f0f0),-9:G6} {G(0x181a6eff0),-9:G6} "
                                  + $"{G(0x181a6f170),-9:G6} {G(0x181a6f270),-9:G6}");
            }
            return;
        }
        var bank = new (string name, long va)[]{
            ("reverb   input",      0x181a6ed70),
            ("chorus L write",      0x181a6ef70),
            ("chorus L -> reverb",  0x181a6eff0),
            ("chorus -> delay",     0x181a6f070),
            ("chorus return",       0x181a6f0f0),
            ("delay  return",       0x181a6f170),
            ("delay  write",        0x181a6f1f0),
            ("delay  -> reverb",    0x181a6f270),
        };
        Console.WriteLine($"fxgain cc91={rev} cc93={cho} choToRev={(c2r<0?"default":c2r.ToString())} "
                          + $"choLevel={(clvl<0?"default":clvl.ToString())}");
        foreach (var (name, va) in bank) {
            float* p = (float*)(b + (va - 0x180000000L));
            Console.WriteLine($"  {name,-20} [0]={p[0]:G9}  [31]={p[31]:G9}");
        }
        return;
    }
    // peek mode: dump raw bytes at a module offset, after a GS reset and a note. For inspecting a
    //   symbol before trusting it -- a "base" global may be the array itself or a pointer to it,
    //   and dereferencing the wrong one just crashes. args: dll peek <off hex> [count] [cc91]
    if (args.Length > 1 && args[1] == "peek")
    {
        long off=Convert.ToInt64(args[2],16);
        int count=args.Length>3?int.Parse(args[3]):64;
        int rev=args.Length>4?int.Parse(args[4]):64;
        setSR(32000f); setBS(512); activate(32000f,512); setThr();
        void CCp2(int c,int v)=>shortIn((uint)((0xB0|0)|(c<<8)|(v<<16)),0);
        GsReset(); for(int c=0;c<16;c++) ToneMap0(c,4);
        CCp2(7,127);CCp2(10,64);CCp2(91,rev);
        shortIn((uint)(0xC0|(19<<8)),0); flush();
        var lc=new float[512]; var rc=new float[512];
        fixed(float* pl=lc,pr=rc) for(int i=0;i<4;i++) process(pl,pr,512);
        shortIn((uint)(0x90|(96<<8)|(110<<16)),0); flush();
        fixed(float* pl=lc,pr=rc) for(int i=0;i<4;i++) process(pl,pr,512);
        Console.WriteLine($"peek +0x{off:x} (cc91={rev}):");
        for(int i=0;i<count;i+=16){
            Console.Write($"  +0x{(off+i):x}  ");
            for(int k=0;k<16 && i+k<count;k++) Console.Write($"{*(byte*)(b+off+i+k):x2} ");
            Console.WriteLine();
        }
        // and the same bytes read as 64-bit words, in case this is a pointer table
        for(int i=0;i<Math.Min(count,32);i+=8){
            ulong v=*(ulong*)(b+off+i);
            Console.WriteLine($"  as u64 +0x{(off+i):x} = 0x{v:x}   (module-relative 0x{(v>=(ulong)b ? (long)(v-(ulong)b) : -1):x})");
        }
        return;
    }
    // partdump mode: follow g_part_array_base @181a222a0 -- which holds a HEAP pointer, the structs
    //   being malloc'd by engine_alloc_init_voices -- and print the fields the bus-assign code reads
    //   for each part slot. CC#91 is set to a distinctive value so the right slot identifies itself
    //   by its reverb-send byte. args: dll partdump [cc91] [slots]
    // blkdiff mode: send one DT1 and report every byte of the **part array** that moved.
    //   Built for the `48`/`49` address families, which real files send, which demonstrably change
    //   the render, and which nothing had located. The part array is heap, reached through the
    //   pointer at 0x1a222a0, and is safe to read whole -- a blind slab of the module's data
    //   section is not, since the range the globals live in has unmapped pages in it.
    //   args: dll blkdiff <a1 hex> <a2 hex> <a3 hex> <databyte hex> [count]
    if (args.Length > 1 && args[1] == "blkdiff")
    {
        int d1 = Convert.ToInt32(args[2], 16);
        int d2 = Convert.ToInt32(args[3], 16);
        int d3 = Convert.ToInt32(args[4], 16);
        int dv = args.Length > 5 ? Convert.ToInt32(args[5], 16) : 0x0F;
        int dn = args.Length > 6 ? int.Parse(args[6]) : 1;
        setSR(32000f); setBS(512); activate(32000f, 512); setThr();
        GsReset(); flush();
        var lb = new float[512]; var rb = new float[512];
        fixed (float* pl = lb, pr = rb) for (int i = 0; i < 8; i++) process(pl, pr, 512);

        long arr = *(long*)(b + 0x1a222a0);
        int span = 32 * 0x488;
        var before = new byte[span];
        for (int i = 0; i < span; i++) before[i] = *(byte*)(arr + i);

        var payload = new byte[3 + dn];
        payload[0] = (byte)d1; payload[1] = (byte)d2; payload[2] = (byte)d3;
        for (int i = 0; i < dn; i++) payload[i + 3] = (byte)dv;
        int sum = 0; foreach (var x in payload) sum += x;
        var msg = new byte[5 + payload.Length + 2];
        msg[0] = 0xF0; msg[1] = 0x41; msg[2] = 0x10; msg[3] = 0x42; msg[4] = 0x12;
        payload.CopyTo(msg, 5);
        msg[5 + payload.Length] = (byte)((128 - (sum & 0x7F)) & 0x7F);
        msg[6 + payload.Length] = 0xF7;
        fixed (byte* mp = msg) longIn(mp, 0);
        flush();
        fixed (float* pl = lb, pr = rb) process(pl, pr, 512);

        Console.WriteLine($"{d1:x2} {d2:x2} {d3:x2} <- {dn} x 0x{dv:x2}");
        int runs = 0, changed = 0, i2 = 0;
        while (i2 < span)
        {
            if (*(byte*)(arr + i2) == before[i2]) { i2++; continue; }
            int st = i2;
            while (i2 < span && *(byte*)(arr + i2) != before[i2]) i2++;
            changed += i2 - st;
            if (runs++ < 20)
                Console.WriteLine($"  part[{st / 0x488}] + 0x{st % 0x488:x3} .. {i2 - st} bytes"
                    + $"   {before[st]:x2} -> {*(byte*)(arr + st):x2}");
        }
        Console.WriteLine($"  {runs} runs, {changed} bytes changed across 32 parts");
        return;
    }

    // bulkmap mode: send one page of the `48` patch bulk dump with a **distinct value in every
    //   payload position**, and report where each one landed in the part array. `blkdiff` sends the
    //   same byte `count` times, which finds the extent of a write and cannot say which payload
    //   position produced which struct byte -- that is the whole question for a bulk dump, and it
    //   takes one message to answer if the payload carries its own index.
    //
    //   Position i is sent as i+1, so 1..64 for a 64-byte page: a struct byte that comes back
    //   holding v was written by position v-1, read straight off the diff. Zero is avoided because
    //   a byte written with the value it already had is invisible to a diff.
    //
    //   `nib` sends the same 64 values Roland-packed, high nibble first, as 128 bytes -- which is
    //   the shape real files use. Running both settles whether the module unpacks or takes the
    //   payload raw: if it unpacks, both runs land the same values; if it does not, `nib` lands
    //   nibbles.
    //   args: dll bulkmap <a1 hex> <a2 hex> <a3 hex> [raw|nib] [count]
    if (args.Length > 1 && args[1] == "bulkmap")
    {
        int m1 = Convert.ToInt32(args[2], 16);
        int m2 = Convert.ToInt32(args[3], 16);
        int m3 = Convert.ToInt32(args[4], 16);
        bool nib = args.Length > 5 && args[5] == "nib";
        int mn = args.Length > 6 ? int.Parse(args[6]) : 64;
        bool mrender = !(args.Length > 7 && args[7] == "noproc");
        // An optional anchor sent first. Most `48` addresses do not carry a part: the dispatcher
        // re-anchors `g_cur_part_base` only at the addresses that begin a region, and every other
        // one continues from wherever the last left it. Sent alone after a reset, a continuation
        // address walks from a stale base -- which is why this probe used to fault, and it is not
        // a range problem. `anchor=<a2hex>:<a3hex>` sends that address first, with a payload of
        // 0x40, so the continuation has somewhere legitimate to continue from.
        string manchor = null;
        foreach (var a in args) if (a.StartsWith("anchor=")) manchor = a.Substring(7);
        setSR(32000f); setBS(512); activate(32000f, 512); setThr();
        GsReset(); flush();
        var lm = new float[512]; var rm = new float[512];
        fixed (float* pl = lm, pr = rm) for (int i = 0; i < 8; i++) process(pl, pr, 512);

        long marr = *(long*)(b + 0x1a222a0);
        int mspan = 32 * 0x488;
        var mbefore = new byte[mspan];
        for (int i = 0; i < mspan; i++) mbefore[i] = *(byte*)(marr + i);

        if (manchor != null)
        {
            var ap = manchor.Split(':');
            var abody = new System.Collections.Generic.List<byte>();
            for (int i = 0; i < mn; i++) { abody.Add(0x04); abody.Add(0x00); }
            var apay = new byte[3 + abody.Count];
            apay[0] = (byte)m1;
            apay[1] = Convert.ToByte(ap[0], 16);
            apay[2] = Convert.ToByte(ap[1], 16);
            abody.CopyTo(apay, 3);
            int asum = 0; foreach (var x in apay) asum += x;
            var amsg = new byte[5 + apay.Length + 2];
            amsg[0] = 0xF0; amsg[1] = 0x41; amsg[2] = 0x10; amsg[3] = 0x42; amsg[4] = 0x12;
            apay.CopyTo(amsg, 5);
            amsg[5 + apay.Length] = (byte)((128 - (asum & 0x7F)) & 0x7F);
            amsg[6 + apay.Length] = 0xF7;
            fixed (byte* ap2 = amsg) longIn(ap2, 0);
            flush();
            fixed (float* pl = lm, pr = rm) process(pl, pr, 512);
            for (int i = 0; i < mspan; i++) mbefore[i] = *(byte*)(marr + i);
            Console.WriteLine($"  (anchored with {m1:x2} {ap[0]} {ap[1]}, payload 0x40)");
        }

        var body = new System.Collections.Generic.List<byte>();
        for (int i = 0; i < mn; i++)
        {
            int v = (i + 1) & 0x7f;
            if (nib) { body.Add((byte)((v >> 4) & 0x0f)); body.Add((byte)(v & 0x0f)); }
            else body.Add((byte)v);
        }
        var mpay = new byte[3 + body.Count];
        mpay[0] = (byte)m1; mpay[1] = (byte)m2; mpay[2] = (byte)m3;
        body.CopyTo(mpay, 3);
        int msum = 0; foreach (var x in mpay) msum += x;
        var mmsg = new byte[5 + mpay.Length + 2];
        mmsg[0] = 0xF0; mmsg[1] = 0x41; mmsg[2] = 0x10; mmsg[3] = 0x42; mmsg[4] = 0x12;
        mpay.CopyTo(mmsg, 5);
        mmsg[5 + mpay.Length] = (byte)((128 - (msum & 0x7F)) & 0x7F);
        mmsg[6 + mpay.Length] = 0xF7;
        fixed (byte* mp = mmsg) longIn(mp, 0);
        flush();
        // Rendering after the write is optional, and on some pages it is fatal: this payload sends
        // a position index into every field, which puts values in range-checked ones that no real
        // dump would, and the module faults on the next block. The write itself lands at the flush,
        // so the map can be read without ever asking the engine to sound the result.
        if (mrender) { fixed (float* pl = lm, pr = rm) process(pl, pr, 512); }

        Console.WriteLine($"{m1:x2} {m2:x2} {m3:x2} <- {(nib ? "nibbles" : "raw")}, "
                          + $"{mn} values 1..{mn}, {body.Count} payload bytes");
        int mchanged = 0;
        for (int i = 0; i < mspan; i++)
        {
            byte now = *(byte*)(marr + i);
            if (now == mbefore[i]) continue;
            mchanged++;
            Console.WriteLine($"  part[{i / 0x488,2}] +0x{i % 0x488:x3}  {mbefore[i]:x2} -> {now:x2}"
                              + $"   (payload position {(now >= 1 && now <= mn ? now - 1 : -1)})");
        }
        Console.WriteLine($"  {mchanged} bytes changed");
        return;
    }

    // drumbulk mode: where the `49` family writes. `blkdiff` reports it touching nothing, and that
    //   is true of the *part array* -- which is the only thing `blkdiff` watches. The address table
    //   in `sysex_select_param_map` hands `a1 = 0x49` to `sysex_drumset_dump_dispatch @ 1800782b0`,
    //   the same handler `0x41` takes, and the writers behind it (`FUN_18007a680` and its
    //   twenty-two siblings) store into the buffer at `DAT_181a222d0` rather than into a part.
    //
    //   So this diffs that buffer instead. Payload position i carries i+1, the same trick `bulkmap`
    //   uses, so the map falls out of one message.
    //   args: dll drumbulk <a1 hex> <a2 hex> <a3 hex> [count] [span]
    if (args.Length > 1 && args[1] == "drumbulk")
    {
        int k1 = Convert.ToInt32(args[2], 16);
        int k2 = Convert.ToInt32(args[3], 16);
        int k3 = Convert.ToInt32(args[4], 16);
        int kn = args.Length > 5 ? int.Parse(args[5]) : 64;
        int kspan = args.Length > 6 ? int.Parse(args[6]) : 0x600;
        // `49` is nibble-packed; `41` -- the per-parameter form of the same data -- is not. Sweeping
        // `41` against these buffers is how a plane offset gets a GS parameter number put to it.
        bool kraw = args.Length > 7 && args[7] == "raw";
        // A fixed value rather than a position index. The index form puts a large number into every
        // field, which is fine for locating a run of plain bytes and fatal for a range-checked one --
        // it is what faulted the module during the `48` work. `0x30` is inside every drum-setup
        // parameter's range and is not the default of any of them, so a write with it is both safe
        // and visible.
        int kval = args.Length > 8 ? Convert.ToInt32(args[8], 16) : 0x30;
        setSR(32000f); setBS(512); activate(32000f, 512); setThr();
        GsReset(); flush();
        var kl = new float[512]; var kr = new float[512];
        fixed (float* pl = kl, pr = kr) for (int i = 0; i < 8; i++) process(pl, pr, 512);

        // `DAT_181a222d0` is null until a drum-set message has selected a buffer, so prime it with
        // a harmless one of the same family before snapshotting.
        {
            var prime = new byte[] { (byte)k1, (byte)k2, (byte)k3, 0x04, 0x00 };
            int psum = 0; foreach (var x in prime) psum += x;
            var pmsg = new byte[5 + prime.Length + 2];
            pmsg[0] = 0xF0; pmsg[1] = 0x41; pmsg[2] = 0x10; pmsg[3] = 0x42; pmsg[4] = 0x12;
            prime.CopyTo(pmsg, 5);
            pmsg[5 + prime.Length] = (byte)((128 - (psum & 0x7F)) & 0x7F);
            pmsg[6 + prime.Length] = 0xF7;
            fixed (byte* mp = pmsg) longIn(mp, 0);
            flush();
            fixed (float* pl = kl, pr = kr) process(pl, pr, 512);
        }

        // Reading `DAT_181a222f8` after the fact gives nothing: the selection is per-message and
        // the pointer does not survive it. Resolve the eight buffers directly instead, through the
        // same accessor `sysex_drumset_dump_dispatch` calls -- `DAT_181a749f8(map * 2 + slot)`,
        // four drum maps by two kit slots -- and diff all of them.
        var kget = (delegate* unmanaged[Cdecl]<int, long>)(*(long*)(b + 0x1a749f8));
        var kbufs = new long[8];
        for (int i = 0; i < 8; i++) kbufs[i] = kget(i);
        Console.WriteLine("drum-set buffers: "
            + string.Join(" ", System.Array.ConvertAll(kbufs, x => $"{(x > b ? x - b : x):x}")));
        long kbuf = kbufs[0];
        if (kbuf == 0) { Console.WriteLine("  null, nothing to diff"); return; }
        var kall = new byte[8][];
        for (int j = 0; j < 8; j++)
        {
            kall[j] = new byte[kspan];
            if (kbufs[j] == 0) continue;
            for (int i = 0; i < kspan; i++) kall[j][i] = *(byte*)(kbufs[j] + i);
        }
        var kbefore = kall[0];

        var body = new System.Collections.Generic.List<byte>();
        for (int i = 0; i < kn; i++)
        {
            int v = kraw ? kval : ((i + 1) & 0x7f);
            if (kraw) body.Add((byte)v);
            else { body.Add((byte)((v >> 4) & 0x0f)); body.Add((byte)(v & 0x0f)); }
        }
        var kpay = new byte[3 + body.Count];
        kpay[0] = (byte)k1; kpay[1] = (byte)k2; kpay[2] = (byte)k3;
        body.CopyTo(kpay, 3);
        int ksum = 0; foreach (var x in kpay) ksum += x;
        var kmsg = new byte[5 + kpay.Length + 2];
        kmsg[0] = 0xF0; kmsg[1] = 0x41; kmsg[2] = 0x10; kmsg[3] = 0x42; kmsg[4] = 0x12;
        kpay.CopyTo(kmsg, 5);
        kmsg[5 + kpay.Length] = (byte)((128 - (ksum & 0x7F)) & 0x7F);
        kmsg[6 + kpay.Length] = 0xF7;
        fixed (byte* mp = kmsg) longIn(mp, 0);
        flush();
        fixed (float* pl = kl, pr = kr) process(pl, pr, 512);

        Console.WriteLine($"{k1:x2} {k2:x2} {k3:x2} <- {kn} nibble-packed values 1..{kn}");
        int kchanged = 0;
        for (int j = 0; j < 8; j++)
        {
            if (kbufs[j] == 0) continue;
            int shown = 0;
            for (int i = 0; i < kspan; i++)
            {
                byte now = *(byte*)(kbufs[j] + i);
                if (now == kall[j][i]) continue;
                kchanged++;
                if (shown++ < 200)
                    Console.WriteLine($"  buf{j} +0x{i:x3}  {kall[j][i]:x2} -> {now:x2}"
                                      + $"   (position {(now >= 1 && now <= kn ? now - 1 : -1)})");
            }
        }
        Console.WriteLine($"  {kchanged} bytes changed across eight buffers of {kspan:x}");
        return;
    }

    // drumreplay mode: send a real file's `49` messages **in order**, diffing the eight drum-set
    //   buffers after each one. `drumbulk` probes a single message after a GS reset, and for the
    //   `48` family that turned out to be a different thing entirely from a run of them -- the walk
    //   is stateful and a message that re-anchors nothing continues from wherever the last left off.
    //   This is the same question asked of `49`: replayed in sequence, does an odd-numbered message
    //   land where it does in isolation, or half a plane further on?
    //   args: dll drumreplay <hexfile, one message per line> [span]
    if (args.Length > 1 && args[1] == "drumreplay")
    {
        string rpath = args[2];
        int rspan = args.Length > 3 ? int.Parse(args[3]) : 0x520;
        setSR(32000f); setBS(512); activate(32000f, 512); setThr();
        GsReset(); flush();
        var rl = new float[512]; var rr = new float[512];
        fixed (float* pl = rl, pr = rr) for (int i = 0; i < 8; i++) process(pl, pr, 512);

        var rget = (delegate* unmanaged[Cdecl]<int, long>)(*(long*)(b + 0x1a749f8));
        var rbufs = new long[8];
        for (int i = 0; i < 8; i++) rbufs[i] = rget(i);
        var rsnap = new byte[8][];
        void Snap()
        {
            for (int j = 0; j < 8; j++)
            {
                rsnap[j] = new byte[rspan];
                if (rbufs[j] == 0) continue;
                for (int i = 0; i < rspan; i++) rsnap[j][i] = *(byte*)(rbufs[j] + i);
            }
        }
        Snap();

        foreach (string line in File.ReadAllLines(rpath))
        {
            string hex = line.Trim();
            if (hex.Length < 4) continue;
            var msg = new byte[hex.Length / 2];
            for (int i = 0; i < msg.Length; i++)
                msg[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            fixed (byte* mp = msg) longIn(mp, 0);
            flush();
            fixed (float* pl = rl, pr = rr) process(pl, pr, 512);

            var parts = new System.Collections.Generic.List<string>();
            for (int j = 0; j < 8; j++)
            {
                if (rbufs[j] == 0) continue;
                int lo = -1, hi = -1, n = 0;
                for (int i = 0; i < rspan; i++)
                {
                    if (*(byte*)(rbufs[j] + i) == rsnap[j][i]) continue;
                    if (lo < 0) lo = i;
                    hi = i; n++;
                }
                if (n > 0) parts.Add($"buf{j} +0x{lo:x3}..+0x{hi:x3} ({n})");
            }
            Console.WriteLine($"  49 {msg[5]:x2} {msg[6]:x2}  "
                              + (parts.Count == 0 ? "(nothing)" : string.Join("  ", parts)));
            Snap();
        }
        return;
    }

    // drumreset mode: does a drum program change throw away the per-key setup?
    //
    //   This port resets `Part::drum_keys` whenever a drum program change resolves a kit, which
    //   means a file that dumps its drum setup and then sets programs -- the normal shape for a file
    //   built by dumping a configured module -- loses the dump. The module holds the same data in a
    //   buffer per *map* rather than per part, and reseeds it from the kit record when a kit loads.
    //   The question is whether selecting the kit that is **already loaded** reseeds it too.
    //
    //   Measured against the buffers rather than against audio: write a distinctive value through
    //   `41 <param> <key>`, send a program change, and see whether the value survives.
    //   args: dll drumreset [param hex] [key hex] [value hex] [otherKit]
    if (args.Length > 1 && args[1] == "drumreset")
    {
        int zp = args.Length > 2 ? Convert.ToInt32(args[2], 16) : 0x02;   // level
        int zk = args.Length > 3 ? Convert.ToInt32(args[3], 16) : 0x3c;
        int zv = args.Length > 4 ? Convert.ToInt32(args[4], 16) : 0x30;
        int zother = args.Length > 5 ? int.Parse(args[5]) : 24;           // a different kit
        // Which kit slot to write and watch. `a2` bit 4 picks it, and the buffer is
        // `drum_map * 2 + slot` -- so slot 1 with the default map is buffer 1. The question this
        // answers: does a program change reseed *every* slot, or only the one the part reads?
        int zslot = args.Length > 6 ? int.Parse(args[6]) : 0;
        setSR(32000f); setBS(512); activate(32000f, 512); setThr();
        var zl = new float[512]; var zr = new float[512];
        var zget = (delegate* unmanaged[Cdecl]<int, long>)(*(long*)(b + 0x1a749f8));

        void Settle(int n) { fixed (float* pl = zl, pr = zr) for (int i = 0; i < n; i++) process(pl, pr, 512); }
        void Prog(int kit) { shortIn((uint)(0xC9 | (kit << 8)), 0); flush(); Settle(2); }
        void Write() { SendSysEx(Dt1(0x41, (byte)((zslot << 4) | zp), (byte)zk, (byte)zv)); flush(); Settle(2); }
        long Buf() => zget(zslot);
        int Read() { long p2 = Buf(); return p2 == 0 ? -1 : *(byte*)(p2 + zp switch {
            0x01 => 0x180, 0x02 => 0x100, 0x03 => 0x200, 0x04 => 0x280,
            0x05 => 0x300, 0x06 => 0x380, 0x09 => 0x400, _ => 0x480 } + zk); }

        GsReset(); flush(); Settle(8);
        Console.WriteLine($"param {zp:x2} key {zk:x2}, writing {zv:x2}, slot {zslot} (buffer {zslot})");
        Console.WriteLine($"  after GS reset, before any write : {Read():x2}");
        Write();
        Console.WriteLine($"  after the 41 write               : {Read():x2}");
        Prog(0);
        Console.WriteLine($"  after program change to kit 0    : {Read():x2}   <- the kit already in force");
        Write();
        Console.WriteLine($"  written again                    : {Read():x2}");
        Prog(zother);
        Console.WriteLine($"  after program change to kit {zother,-4} : {Read():x2}   <- a different kit");
        return;
    }

    // progorder mode: when a bulk dump and a plain program change both name a part's patch, which
    //   wins? `darkness3.mid` sets block 9 to program 48 in its `48` dump and also sends
    //   `PROG ch9 = 0`, and the module plays the dump's choice -- removing the program change from
    //   the file leaves its render byte-identical, so the plain one has no effect at all. Delivery
    //   order says it should: both sequencers put track 0's SysEx before track 6's program change.
    //
    //   This reads the part's own bank and program bytes rather than inferring from audio, and can
    //   send the two in either order.
    //   args: dll progorder <hexfile> <block> <program> [progfirst]
    if (args.Length > 1 && args[1] == "progorder")
    {
        string gpath = args[2];
        int gblock = int.Parse(args[3]);
        int gprog = int.Parse(args[4]);
        bool gfirst = args.Length > 5 && args[5] == "progfirst";
        // Whether to render a block between the two. A file does not: every event at tick 0 is
        // enqueued before a single `process` call, so if the module resolves a whole flush in an
        // order of its own rather than in arrival order, that only shows without the gap.
        bool ggap = !(args.Length > 6 && args[6] == "nogap");
        int gchan = gblock == 0 ? 9 : (gblock < 10 ? gblock - 1 : gblock);
        setSR(32000f); setBS(512); activate(32000f, 512); setThr();
        GsReset(); flush();
        var gl = new float[512]; var gr = new float[512];
        fixed (float* pl = gl, pr = gr) for (int i = 0; i < 8; i++) process(pl, pr, 512);
        long garr = *(long*)(b + 0x1a222a0);
        long gpart = garr + (long)gblock * 0x488;
        void Show(string when) =>
            Console.WriteLine($"  {when,-32} bank {*(byte*)(gpart + 0x3d4):x2}  "
                              + $"program {*(byte*)(gpart + 0x3d5):x2} ({*(byte*)(gpart + 0x3d5)})");
        void SendProg() { shortIn((uint)((0xC0 | gchan) | (gprog << 8)), 0);
                          if (ggap) { flush(); fixed (float* pl = gl, pr = gr) process(pl, pr, 512); } }
        void SendDump()
        {
            foreach (string line in File.ReadAllLines(gpath))
            {
                string hex = line.Trim();
                if (hex.Length < 4) continue;
                var msg = new byte[hex.Length / 2];
                for (int i = 0; i < msg.Length; i++)
                    msg[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
                // A channel message rather than SysEx: the file's own burst mixes both, and the
                // whole point is to feed them in exactly the order the sequencer would.
                if (msg[0] < 0xF0)
                    shortIn((uint)(msg[0] | (msg.Length > 1 ? msg[1] << 8 : 0)
                                   | (msg.Length > 2 ? msg[2] << 16 : 0)), 0);
                else { fixed (byte* mp = msg) longIn(mp, 0); }
            }
            if (ggap) { flush(); fixed (float* pl = gl, pr = gr) process(pl, pr, 512); }
        }

        Console.WriteLine($"block {gblock} (channel {gchan + 1}), plain program {gprog}, "
                          + (gfirst ? "program change FIRST" : "dump FIRST"));
        Show("after GS reset");
        if (gfirst) { SendProg(); if (ggap) Show("after the program change"); SendDump(); if (ggap) Show("after the dump"); }
        else { SendDump(); if (ggap) Show("after the dump"); SendProg(); if (ggap) Show("after the program change"); }
        if (!ggap)
        {
            flush();
            fixed (float* pl = gl, pr = gr) process(pl, pr, 512);
            Show("after one flush, both queued");
        }
        return;
    }

    // chophase mode: read the chorus LFO phase after N processed samples, so its origin and rate
    //   can be solved rather than guessed. The register at 0x181a62af8 advances by the increment at
    //   0x181a62afc per sample into 24 bits. Every `smf` render prints this value once; this mode
    //   varies the warm-up so two readings pin both unknowns.
    //   args: dll chophase <blocks> [blockFrames]
    if (args.Length > 1 && args[1] == "chophase")
    {
        int nblk = int.Parse(args[2]);
        int bfrm = args.Length > 3 ? int.Parse(args[3]) : 512;
        setSR(32000f); setBS(512); activate(32000f, 512); setThr();
        long PVc(long va) => b + (va - 0x180000000L);
        Console.WriteLine($"  at rest, before any process(): phase={*(int*)PVc(0x181a62af8L)}"
                        + $" inc={*(int*)PVc(0x181a62afcL)}");
        GsReset(); flush();
        Console.WriteLine($"  after GsReset, still no process(): phase={*(int*)PVc(0x181a62af8L)}"
                        + $" inc={*(int*)PVc(0x181a62afcL)}");
        var lc2 = new float[512]; var rc2 = new float[512];
        fixed (float* pl = lc2, pr = rc2) for (int i = 0; i < nblk; i++) process(pl, pr, (uint)bfrm);
        // Optionally send a second GS reset *after* the warm-up: does a reset zero the accumulator?
        if (args.Length > 4 && args[4] == "reset")
        {
            int before = *(int*)PVc(0x181a62af8L);
            GsReset(); flush();
            fixed (float* pl = lc2, pr = rc2) process(pl, pr, 32);
            Console.WriteLine($"  GS reset after the warm-up: phase {before} -> "
                            + $"{*(int*)PVc(0x181a62af8L)}");
        }
        int ph = *(int*)PVc(0x181a62af8L);
        int inc = *(int*)PVc(0x181a62afcL);
        long samples = (long)nblk * bfrm;
        Console.WriteLine($"  after {nblk} x {bfrm} = {samples} samples: phase={ph} inc={inc}");
        if (inc > 0)
            Console.WriteLine($"  implied origin = {ph - samples * inc} (0x{(ph - samples * inc):X})");
        return;
    }

    // mapsysex mode: what does a `40 4x pp` write? Sends one DT1 into the extended part block and
    //   reports the four part bytes the bank/map handlers touch, before and after. Built to settle
    //   which of `40 4x 00` and `40 4x 01` is the tone map, since the two handlers
    //   (`sysex_part_bank_msb` -> part+0x44d, `sysex_part_bank_lsb` -> part+0x44e clamped 1..4) are
    //   adjacent in the chain and the chain's own next-address bytes do not read off cleanly.
    //   args: dll mapsysex <pp> <value> [channel]
    if (args.Length > 1 && args[1] == "mapsysex")
    {
        int ppm = args[2] == "sweep" ? -1 : Convert.ToInt32(args[2], 16);
        int vvm = int.Parse(args[3]);
        int chm = args.Length > 4 ? int.Parse(args[4]) : 0;
        setSR(32000f); setBS(512); activate(32000f, 512); setThr();
        GsReset(); flush();
        var lm = new float[512]; var rm = new float[512];
        fixed (float* pl = lm, pr = rm) for (int i = 0; i < 8; i++) process(pl, pr, 512);
        long arrm = *(long*)(b + 0x1a222a0);
        // The part array is indexed by **block**, not by channel: channel 10 is block 0 and
        // channels 1-9 are blocks 1-9. Reading slot `chm` dumps somebody else's part.
        long qm = arrm + (long)BlockNum(chm) * 0x488;
        void Show(string when) =>
            Console.WriteLine($"  {when,-7} +0x444={*(byte*)(qm+0x444),3} +0x445={*(byte*)(qm+0x445),3}"
                            + $" +0x44d={*(byte*)(qm+0x44d),3} +0x44e={*(byte*)(qm+0x44e),3}");
        if (ppm < 0)
        {
            // Sweep every address in both the `40 1x` and `40 4x` blocks, reporting any that moves
            // one of the four bytes. A whole-block sweep beats guessing which one the manual means.
            Console.WriteLine($"sweeping, channel {chm}, value {vvm}");
            foreach (int hi in new[] { 0x10, 0x40 })
            {
                for (int pp = 0; pp <= 0x7f; pp++)
                {
                    byte a444 = *(byte*)(qm + 0x444), a445 = *(byte*)(qm + 0x445);
                    byte a44d = *(byte*)(qm + 0x44d), a44e = *(byte*)(qm + 0x44e);
                    SendSysEx(Dt1(0x40, (byte)(hi | BlockNum(chm)), (byte)pp, (byte)vvm));
                    flush();
                    fixed (float* pl = lm, pr = rm) process(pl, pr, 512);
                    byte b444 = *(byte*)(qm + 0x444), b445 = *(byte*)(qm + 0x445);
                    byte b44d = *(byte*)(qm + 0x44d), b44e = *(byte*)(qm + 0x44e);
                    if (a444 != b444 || a445 != b445 || a44d != b44d || a44e != b44e)
                        Console.WriteLine($"  40 {hi | BlockNum(chm):x2} {pp:x2} <- {vvm}:"
                            + $" +0x444 {a444}->{b444}  +0x445 {a445}->{b445}"
                            + $"  +0x44d {a44d}->{b44d}  +0x44e {a44e}->{b44e}");
                }
            }
            return;
        }

        Console.WriteLine($"40 4{BlockNum(chm):x} {ppm:x2} <- {vvm}   (channel {chm})");
        Show("before");
        SendSysEx(Dt1(0x40, (byte)(0x40 | BlockNum(chm)), (byte)ppm, (byte)vvm));
        flush();
        fixed (float* pl = lm, pr = rm) process(pl, pr, 512);
        Show("after");
        return;
    }

    if (args.Length > 1 && args[1] == "partdump")
    {
        int rev=args.Length>2?int.Parse(args[2]):90;
        int slots=args.Length>3?int.Parse(args[3]):20;
        // "xg" resets with XG System On instead of a GS reset, to read XG's own part defaults.
        string mode=args.Length>4?args[4]:"gs";
        setSR(32000f); setBS(512); activate(32000f,512); setThr();
        void CCd(int c,int v)=>shortIn((uint)((0xB0|0)|(c<<8)|(v<<16)),0);
        if(mode=="xg"){ SendSysEx(new byte[]{0xF0,0x43,0x10,0x4C,0x00,0x00,0x7E,0x00,0xF7}); }
        else { GsReset(); for(int c=0;c<16;c++) ToneMap0(c,4); }
        flush();
        var lw=new float[512]; var rw=new float[512];
        fixed(float* pl=lw,pr=rw) for(int i=0;i<8;i++) process(pl,pr,512);
        if(rev>=0){ CCd(7,127);CCd(10,64);CCd(91,rev);CCd(93,77); }
        shortIn((uint)(0xC0|(19<<8)),0); flush();
        var ld=new float[512]; var rd=new float[512];
        fixed(float* pl=ld,pr=rd) for(int i=0;i<4;i++) process(pl,pr,512);
        shortIn((uint)(0x90|(96<<8)|(110<<16)),0); flush();
        fixed(float* pl=ld,pr=rd) for(int i=0;i<4;i++) process(pl,pr,512);
        long arr=*(long*)(b+0x1a222a0);
        Console.WriteLine($"g_part_array_base -> 0x{arr:x}  (cc91={rev}, cc93=77)");
        Console.WriteLine("slot  vol(3dc) exp(464) pan(3dd) cho(3e2) rev(3e3) dly(44a)  rx(3d6) nrpn");
        for(int i=0;i<slots;i++){
            long q=arr+(long)i*0x488;
            ushort rxw=*(ushort*)(q+0x3d6);
            Console.WriteLine($"{i,4}  {*(byte*)(q+0x3dc),7} {*(byte*)(q+0x464),7}" +
                $" {*(byte*)(q+0x3dd),8} {*(byte*)(q+0x3e2),8} {*(byte*)(q+0x3e3),8}" +
                $" {*(byte*)(q+0x44a),8}   0x{rxw:X4} {((rxw&0x8000)!=0?"on":"OFF"),4}");
        }
        return;
    }
    // fxmatrix mode: fx_process_block opens by interpolating 16 send-matrix coefficients, each a
    //   current at DAT_181a6ead0[i] chasing a target at DAT_181a6f2f0[i], stepped 16 times a block
    //   by (target - current) * 0x300 >> 16. Trace both arrays across a controller jump to see
    //   which coefficient a controller drives and how fast it moves.
    //   args: dll fxmatrix <cc> <before> <after> [chunk] [npoints]
    if (args.Length > 1 && args[1] == "fxmatrix")
    {
        // "sx:a1,a2,a3" addresses a GS system parameter instead of a Control Change.
        string sx=args.Length>2 && args[2].StartsWith("sx:") ? args[2].Substring(3) : null;
        int cc=sx!=null?0:int.Parse(args[2]); int before=args.Length>3?int.Parse(args[3]):0;
        int after=args.Length>4?int.Parse(args[4]):127;
        int chunk=args.Length>5?int.Parse(args[5]):32; int npts=args.Length>6?int.Parse(args[6]):120;
        setSR(32000f); setBS(512); activate(32000f,512); setThr();
        void CCm(int c,int v){
            if(sx!=null){ var a=sx.Split(',');
                SendSysEx(Dt1(Convert.ToByte(a[0],16),Convert.ToByte(a[1],16),
                              Convert.ToByte(a[2],16),(byte)v)); }
            else shortIn((uint)((0xB0|0)|(c<<8)|(v<<16)),0);
        }
        void RawCC(int c,int v)=>shortIn((uint)((0xB0|0)|(c<<8)|(v<<16)),0);
        GsReset(); for(int c=0;c<16;c++) ToneMap0(c,4);
        RawCC(7,127);RawCC(10,64);RawCC(91,64);RawCC(93,64);CCm(cc,before);
        shortIn((uint)(0xC0|(19<<8)),0); flush();
        var le=new float[512]; var re=new float[512];
        fixed(float* pl=le,pr=re) for(int i=0;i<8;i++) process(pl,pr,512);
        shortIn((uint)(0x90|(96<<8)|(110<<16)),0); flush();
        fixed(float* pl=le,pr=re) for(int i=0;i<40;i++) process(pl,pr,512);
        long cur=b+0x1a6ead0, tgt=b+0x1a6f2f0;
        Console.WriteLine($"fxmatrix {(sx!=null?"sysex 40 "+sx:"cc"+cc)} {before} -> {after}");
        Console.Write("sample");
        for(int k=0;k<16;k++) Console.Write($",cur{k}");
        for(int k=0;k<16;k++) Console.Write($",tgt{k}");
        Console.WriteLine();
        int t=0;
        void Row(){
            Console.Write(t);
            for(int k=0;k<16;k++) Console.Write($",{*(short*)(cur+k*2)}");
            for(int k=0;k<16;k++) Console.Write($",{*(short*)(tgt+k*2)}");
            Console.WriteLine();
        }
        for(int i=0;i<3;i++){ Row(); fixed(float* pl=le,pr=re) process(pl,pr,(uint)chunk); t+=chunk; }
        CCm(cc,after); flush();
        for(int i=0;i<npts;i++){ Row(); fixed(float* pl=le,pr=re) process(pl,pr,(uint)chunk); t+=chunk; }
        return;
    }
    // slotscan mode: the four (gain, bus) slot arrays are indexed by VOICE -- DAT_181a1d930 + v*4
    //   and so on, four arrays 0x100 apart. Earlier passes read voice 0 only. Sweep all 64 voices
    //   and report every slot whose gain or bus moves when a controller does.
    //   args: dll slotscan <cc> <before> <after> [prog] [note]
    if (args.Length > 1 && args[1] == "slotscan")
    {
        int cc=args.Length>2?int.Parse(args[2]):93; int before=args.Length>3?int.Parse(args[3]):0;
        int after=args.Length>4?int.Parse(args[4]):127;
        int pg=args.Length>5?int.Parse(args[5]):19, nt=args.Length>6?int.Parse(args[6]):72;
        setSR(32000f); setBS(512); activate(32000f,512); setThr();
        void CCs3(int c,int v)=>shortIn((uint)((0xB0|0)|(c<<8)|(v<<16)),0);
        GsReset(); for(int c=0;c<16;c++) ToneMap0(c,4);
        CCs3(7,127);CCs3(10,64);CCs3(91,0);CCs3(93,0);CCs3(94,0);CCs3(cc,before);
        shortIn((uint)(0xC0|(pg<<8)),0); flush();
        var lf=new float[512]; var rf=new float[512];
        fixed(float* pl=lf,pr=rf) for(int i=0;i<8;i++) process(pl,pr,512);
        shortIn((uint)(0x90|(nt<<8)|(110<<16)),0); flush();
        fixed(float* pl=lf,pr=rf) for(int i=0;i<40;i++) process(pl,pr,512);
        var g0=new float[4*64]; var u0=new uint[4*64];
        for(int k=0;k<4;k++) for(int v=0;v<64;v++){
            g0[k*64+v]=*(float*)(b+0x1a1d930+k*0x100L+v*4L);
            u0[k*64+v]=*(uint*)(b+0x1a6e4b0+k*0x100L+v*4L);
        }
        CCs3(cc,after); flush();
        fixed(float* pl=lf,pr=rf) for(int i=0;i<80;i++) process(pl,pr,512);
        Console.WriteLine($"slotscan cc{cc} {before} -> {after} (prog {pg}, note {nt})");
        int hits=0;
        for(int k=0;k<4;k++) for(int v=0;v<64;v++){
            float g=*(float*)(b+0x1a1d930+k*0x100L+v*4L);
            uint u=*(uint*)(b+0x1a6e4b0+k*0x100L+v*4L);
            if(g!=g0[k*64+v] || u!=u0[k*64+v]){
                Console.WriteLine($"  voice {v,2} slot {k}: gain {g0[k*64+v]:0.000000} -> {g:0.000000}" +
                    $"   bus {u0[k*64+v]} -> {u} (0x{u:X})");
                hits++;
            }
        }
        Console.WriteLine($"{hits} slots moved");
        return;
    }
    // matscan mode: the 33-bus send matrix. fx_process_block runs two 33-tap dot products, one per
    //   coefficient array -- DAT_181a6e8c0 and DAT_181a6f310, each 33 x 4 floats -- summing the bus
    //   accumulators into the effect inputs. Report every tap that moves with a controller.
    //   args: dll matscan <cc> <before> <after> [prog] [note]
    if (args.Length > 1 && args[1] == "matscan")
    {
        int cc=args.Length>2?int.Parse(args[2]):93; int before=args.Length>3?int.Parse(args[3]):0;
        int after=args.Length>4?int.Parse(args[4]):127;
        int pg=args.Length>5?int.Parse(args[5]):19, nt=args.Length>6?int.Parse(args[6]):72;
        setSR(32000f); setBS(512); activate(32000f,512); setThr();
        void CCx(int c,int v)=>shortIn((uint)((0xB0|0)|(c<<8)|(v<<16)),0);
        GsReset(); for(int c=0;c<16;c++) ToneMap0(c,4);
        CCx(7,127);CCx(10,64);CCx(91,0);CCx(93,0);CCx(94,0);CCx(cc,before);
        shortIn((uint)(0xC0|(pg<<8)),0); flush();
        var lg=new float[512]; var rg=new float[512];
        fixed(float* pl=lg,pr=rg) for(int i=0;i<8;i++) process(pl,pr,512);
        shortIn((uint)(0x90|(nt<<8)|(110<<16)),0); flush();
        fixed(float* pl=lg,pr=rg) for(int i=0;i<40;i++) process(pl,pr,512);
        long[] bases={b+0x1a6e8c0, b+0x1a6f310};
        const int taps=33*4;
        var snap=new float[2][];
        for(int m=0;m<2;m++){ snap[m]=new float[taps];
            for(int i=0;i<taps;i++) snap[m][i]=*(float*)(bases[m]+i*4L); }
        CCx(cc,after); flush();
        fixed(float* pl=lg,pr=rg) for(int i=0;i<80;i++) process(pl,pr,512);
        Console.WriteLine($"matscan cc{cc} {before} -> {after}");
        int hits=0;
        for(int m=0;m<2;m++) for(int i=0;i<taps;i++){
            float v=*(float*)(bases[m]+i*4L);
            if(v!=snap[m][i]){
                Console.WriteLine($"  matrix {m} (0x{(bases[m]-b):x}) tap {i/4,2} lane {i%4}: " +
                    $"{snap[m][i]:0.000000} -> {v:0.000000}");
                hits++;
            }
        }
        Console.WriteLine($"{hits} taps moved");
        return;
    }
    // mattrace mode: trace one 33-bus matrix tap across a controller jump, a block at a time, to
    //   see whether the send coefficient steps or slews. args: dll mattrace <cc> <matrix 0|1> <tap>
    //   <before> <after> [chunk] [npoints]
    if (args.Length > 1 && args[1] == "mattrace")
    {
        int cc=int.Parse(args[2]); int mat=int.Parse(args[3]); int tap=int.Parse(args[4]);
        int before=int.Parse(args[5]); int after=int.Parse(args[6]);
        int chunk=args.Length>7?int.Parse(args[7]):32; int npts=args.Length>8?int.Parse(args[8]):200;
        setSR(32000f); setBS(512); activate(32000f,512); setThr();
        void CCt(int c,int v)=>shortIn((uint)((0xB0|0)|(c<<8)|(v<<16)),0);
        GsReset(); for(int c=0;c<16;c++) ToneMap0(c,4);
        CCt(7,127);CCt(10,64);CCt(91,0);CCt(93,0);CCt(94,0);CCt(cc,before);
        shortIn((uint)(0xC0|(19<<8)),0); flush();
        var lh=new float[512]; var rh=new float[512];
        fixed(float* pl=lh,pr=rh) for(int i=0;i<8;i++) process(pl,pr,512);
        shortIn((uint)(0x90|(72<<8)|(110<<16)),0); flush();
        fixed(float* pl=lh,pr=rh) for(int i=0;i<40;i++) process(pl,pr,512);
        long addr=(mat==0 ? b+0x1a6e8c0 : b+0x1a6f310) + tap*4L*4L;
        Console.WriteLine($"mattrace cc{cc} matrix {mat} tap {tap}: {before} -> {after}");
        Console.WriteLine("sample,lane0");
        int t=0;
        for(int i=0;i<3;i++){ Console.WriteLine($"{t},{*(float*)addr:0.00000000}");
            fixed(float* pl=lh,pr=rh) process(pl,pr,(uint)chunk); t+=chunk; }
        CCt(cc,after); flush();
        for(int i=0;i<npts;i++){ Console.WriteLine($"{t},{*(float*)addr:0.00000000}");
            fixed(float* pl=lh,pr=rh) process(pl,pr,(uint)chunk); t+=chunk; }
        return;
    }
    // xgvoices mode: which tone does XG resolve a (program, bank LSB) pair to? Same idea as
    //   "voices", but in XG mode and with the LSB set, since XG's variations hang off the bank LSB
    //   rather than the MSB. Prints each sounding voice's wave, which is what identifies the tone.
    //   args: dll xgvoices <prog> <lsb> [note] [vel]
    if (args.Length > 1 && args[1] == "xgvoices")
    {
        int pg=int.Parse(args[2]), lsb=int.Parse(args[3]);
        int nt=args.Length>4?int.Parse(args[4]):60, vel=args.Length>5?int.Parse(args[5]):100;
        long fbX=b+0x1a1b5b8; var lx=new float[512]; var rx=new float[512];
        void CCx2(int c,int v)=>shortIn((uint)(0xB0|(c<<8)|(v<<16)),0);
        SendSysEx(new byte[]{0xF0,0x43,0x10,0x4C,0x00,0x00,0x7E,0x00,0xF7});
        flush(); fixed(float* pl=lx,pr=rx) for(int i=0;i<6;i++) process(pl,pr,512);
        CCx2(120,0); flush(); fixed(float* pl=lx,pr=rx) process(pl,pr,512);
        CCx2(0,0); CCx2(32,lsb); CCx2(7,127); CCx2(10,64); CCx2(91,0); CCx2(93,0);
        shortIn((uint)(0xC0|(pg<<8)),0);
        shortIn((uint)(0x90|(nt<<8)|(vel<<16)),0); flush();
        fixed(float* pl=lx,pr=rx) for(int i=0;i<4;i++) process(pl,pr,512);
        Console.Write($"prog={pg} lsb={lsb}:");
        int cnt=0;
        for(int v=0;v<64;v++){ byte fl=*(byte*)(fbX+v*0x50); if((fl&1)==0) continue;
            uint wc=*(uint*)(b+0x1a6fb60+v*4);
            Console.Write($"  wave={wc:X4}"); cnt++; }
        Console.WriteLine($"   ({cnt} voices)");
        return;
    }
    // xgsweep mode: REMOVED, and deliberately left as a note rather than a working mode.
    //
    //   The obvious way to sweep XG's (program, bank LSB) resolution is to loop in one process,
    //   playing a note per cell and counting the voices flagged active at DAT_181a1b5b8 + v*0x50.
    //   That does not work: bit 0 of that byte is *allocated*, not *sounding*. It is clear on the
    //   first note after init and never returns to zero afterwards, so from the second cell on the
    //   count is the running total and every one-partial tone reads as two. All-sound-off does not
    //   clear it and neither does waiting -- a guard loop that waits for zero simply spins out.
    //
    //   A sweep built that way reported 31.5% of pairs disagreeing with a port's own resolution.
    //   All of it was the artefact. Use xgvoices, one process per query, which starts from a clean
    //   allocator every time and agrees with itself.
    // svfmel mode: svfcoef for a MELODIC part. Same fields -- the engine's own summed cutoff at
    //   voice+0xcc, the resonance byte at +0xee, and the f/q coefficients the filter runs on -- but
    //   on channel 0 with a bank LSB, so CC#74's effect on a tone-map voice can be read directly
    //   rather than inferred from level. args: dll svfmel <prog> <lsb> <note> <vel> <cc74> [cc71] [xg]
    if (args.Length > 1 && args[1] == "svfmel")
    {
        int pgm=int.Parse(args[2]), lsb=int.Parse(args[3]), nt=int.Parse(args[4]);
        int vel=int.Parse(args[5]), c74=int.Parse(args[6]);
        int c71=args.Length>7?int.Parse(args[7]):64;
        bool xg=args.Length<=8 || args[8]!="gs";
        setSR(32000f); setBS(512); activate(32000f,512); setThr();
        long fbm=b+0x1a1b5b8;
        var getVCm=(delegate* unmanaged[Cdecl]<int,long>)(b+0x5c360);
        long vcm=getVCm(0);
        float* fc=(float*)(b+(0x181a1cb70L-0x180000000L));
        float* qc=(float*)(b+(0x181a1d1f0L-0x180000000L));
        var lm=new float[512]; var rm=new float[512];
        if(xg) SendSysEx(new byte[]{0xF0,0x43,0x10,0x4C,0x00,0x00,0x7E,0x00,0xF7});
        else GsReset();
        flush(); fixed(float* pl=lm,pr=rm) for(int i=0;i<8;i++) process(pl,pr,512);
        void CCm(int c,int v)=>shortIn((uint)(0xB0|(c<<8)|(v<<16)),0);
        CCm(0,0); CCm(32,lsb); CCm(7,127); CCm(10,64); CCm(91,0); CCm(93,0);
        CCm(74,c74); CCm(71,c71);
        shortIn((uint)(0xC0|(pgm<<8)),0); flush();
        Console.WriteLine("cc74,voice,f_coef,q_coef,cutoff_cc,qraw_dc,resobyte_ee,type_f5");
        shortIn((uint)(0x90|(nt<<8)|(vel<<16)),0); flush();
        for(int t=0;t<3;t++) fixed(float* pl=lm,pr=rm) process(pl,pr,320);
        for(int v=0;v<64;v++){
            if((*(byte*)(fbm+v*0x50)&1)==0) continue;
            long pv=vcm+(long)v*0x220;
            int lane=v&3, grp=v>>2;
            Console.WriteLine($"{c74},{v},{fc[grp*16+lane]:0.000000},{qc[grp*16+lane]:0.000000},"
                             +$"{*(int*)(pv+0xcc)},{*(int*)(pv+0xdc)},{*(byte*)(pv+0xee)},{*(byte*)(pv+0x1f5)}");
        }
        return;
    }
    // jitterprobe mode: does the module draw a random pitch offset for this tone, and which partial
    //   byte switches it on? `partial_compute_pitch` @`18005fc20` reads the depth from the partial
    //   block's **+0x12**, and only when that byte is non-zero calls `prng_lfsr` and folds the draw
    //   into the base pitch it writes to voice+0x1f8 and voice+0x218. A port reading a different
    //   byte disagrees on 220 of the 4,726 partial blocks, so a tone from that disagreeing set
    //   answers the question directly.
    //
    //   The measurement is note-to-note *variance*, not a value: the LFSR is not reset between
    //   notes, so a jittered partial writes a different base pitch every time and an unjittered one
    //   repeats its base pitch exactly. That makes the result independent of the generator's seed
    //   and of any port's idea of it. It also puts one voice per partial on the generator instead
    //   of a whole song's worth, which is the draw-order confound this exists to avoid.
    //
    //   Reads 0x1fc/0x200 alongside as the control: those come from the wave descriptor before the
    //   jitter branch, so they must stay constant across repeats even when 0x1f8/0x218 do not.
    //   args: dll jitterprobe <prog> <note> <vel> <map> [bankMsb] [repeats]
    if (args.Length > 1 && args[1] == "jitterprobe")
    {
        int pgj=int.Parse(args[2]), ntj=int.Parse(args[3]), vlj=int.Parse(args[4]);
        int mpj=int.Parse(args[5]);
        int bkj=args.Length>6?int.Parse(args[6]):0;
        int repj=args.Length>7?int.Parse(args[7]):12;
        setSR(32000f); setBS(512); activate(32000f,512); setThr();
        long fbj=b+0x1a1b5b8;
        var getVCj=(delegate* unmanaged[Cdecl]<int,long>)(b+0x5c360);
        long vcj=getVCj(0);
        var lj=new float[512]; var rj=new float[512];
        GsReset(); for(int c=0;c<16;c++) ToneMap0(c,mpj); flush();
        fixed(float* pl=lj,pr=rj) for(int i=0;i<8;i++) process(pl,pr,512);
        void CCj(int c,int v)=>shortIn((uint)(0xB0|(c<<8)|(v<<16)),0);
        CCj(0,bkj); CCj(32,0); CCj(7,127); CCj(10,64); CCj(91,0); CCj(93,0);
        shortIn((uint)(0xC0|(pgj<<8)),0); flush();
        fixed(float* pl=lj,pr=rj) for(int i=0;i<2;i++) process(pl,pr,512);
        Console.WriteLine($"jitterprobe prog={pgj} note={ntj} vel={vlj} map={mpj} bankMsb={bkj}"
            + $" repeats={repj}");
        // Keyed by *allocation order within the repeat*, not by voice index and not by wave. The
        // allocator hands a note's partials out in slot order, so the k-th active voice is the
        // k-th partial on every repeat, while the absolute voice index walks forward as notes are
        // reused. Keying on the wave number instead looks safe and is not: a unison patch gives
        // both its partials the same wave (TB Lead and MG unison both do), and the two are then
        // pooled, so the static detune between them reads as a spread and the tone is called
        // jittered when neither partial moved.
        var seen=new SortedDictionary<int,List<(int rep,uint wave,int w1f8,int w218,int w1fc)>>();
        for(int rep=0; rep<repj; ++rep){
            shortIn((uint)(0x90|(ntj<<8)|(vlj<<16)),0); flush();
            fixed(float* pl=lj,pr=rj) for(int t=0;t<2;t++) process(pl,pr,320);
            int ord=0;
            for(int v=0;v<64;v++){
                if((*(byte*)(fbj+v*0x50)&1)==0) continue;
                long pv=vcj+(long)v*0x220;
                uint wave=*(uint*)(b+0x1a6fb60+v*4);
                if(!seen.TryGetValue(ord, out var list)){ list=new(); seen[ord]=list; }
                list.Add((rep,wave,*(int*)(pv+0x1f8),*(int*)(pv+0x218),*(int*)(pv+0x1fc)));
                ++ord;
            }
            // Release, then All Sound Off: a release tail alone outlives any plausible gap on a pad
            // or a bell, and a voice still sounding on the next repeat is read twice and reported
            // as a repeat that jittered when it did not.
            shortIn((uint)(0x80|(ntj<<8)|(64<<16)),0);
            CCj(120,0); flush();
            fixed(float* pl=lj,pr=rj) for(int t=0;t<8;t++) process(pl,pr,512);
        }
        foreach(var kv in seen){
            var vals=kv.Value;
            var d218=new SortedSet<int>(); foreach(var e in vals) d218.Add(e.w218);
            var d1f8=new SortedSet<int>(); foreach(var e in vals) d1f8.Add(e.w1f8);
            var d1fc=new SortedSet<int>(); foreach(var e in vals) d1fc.Add(e.w1fc);
            var waves=new SortedSet<uint>(); foreach(var e in vals) waves.Add(e.wave);
            int lo=int.MaxValue, hi=int.MinValue;
            foreach(var x in d218){ if(x<lo) lo=x; if(x>hi) hi=x; }
            // A clean run is exactly one reading per repeat. More means a voice outlived its
            // All Sound Off and shifted every later ordinal, which would pool two partials again;
            // say so rather than let it pass as a spread.
            string health = vals.Count==repj ? "" : $"  [!! {vals.Count} readings for {repj} repeats,"
                + " ordinals are not aligned -- do not read the spread]";
            var wl=new List<string>(); foreach(var w in waves) wl.Add($"0x{w:X4}");
            Console.WriteLine($"  partial#{kv.Key} (wave {string.Join("/", wl)}):"
                + $" {vals.Count} readings, {d218.Count} distinct base pitches (voice+0x218),"
                + $" {d1f8.Count} distinct voice+0x1f8, {d1fc.Count} distinct voice+0x1fc{health}");
            Console.WriteLine($"    spread {hi-lo} mst  [{lo} .. {hi}]"
                + $" -> {(d218.Count>1 ? "JITTERED" : "constant, no draw")}");
            var parts=new List<string>();
            foreach(var e in vals) parts.Add($"{e.rep}:{e.w218}");
            Console.WriteLine($"    per repeat  {string.Join(" ", parts)}");
        }
        return;
    }
    // pitchword mode: did the engine adopt each voice's second-fine-tune pitch, or not?
    //   partial_compute_pitch writes voice+0x1fc (root*1000 - fine + 0x400) and
    //   voice+0x200 (that, minus desc[0x0e], plus 0x400). voices_control_update then copies
    //   0x200 over 0x1fc on every control tick while voice+0x16c is 1 -- effectively once, since
    //   the words are equal after the first -- but only when the one-shot flag at voice+4 is set
    //   and the retrigger flag at voice+0x1b0 is clear. So reading the two words after a few ticks
    //   says directly whether the term was adopted for that voice: equal means adopted, different
    //   means not. That gate resolves per voice, not per patch, and the same wave splits both ways
    //   across its notes -- stagedpitch below drives the split rather than observing it.
    //   args: dll pitchword <prog> <note> <vel> <map> [ticks]
    if (args.Length > 1 && args[1] == "pitchword")
    {
        int pgw=int.Parse(args[2]), ntw=int.Parse(args[3]), vlw=int.Parse(args[4]);
        int mpw=int.Parse(args[5]);
        int ticks=args.Length>6?int.Parse(args[6]):4;
        // Bank MSB, so the SFX variations are reachable. `Stream` and `Bubble` -- the two tones
        // whose key follow this was added to measure -- sit at bank 4 and 5 of program 122.
        int bkw=args.Length>7?int.Parse(args[7]):0;
        setSR(32000f); setBS(512); activate(32000f,512); setThr();
        long fbw=b+0x1a1b5b8;
        var getVCw=(delegate* unmanaged[Cdecl]<int,long>)(b+0x5c360);
        long vcw=getVCw(0);
        var lw2=new float[512]; var rw2=new float[512];
        GsReset(); for(int c=0;c<16;c++) ToneMap0(c,mpw); flush();
        fixed(float* pl=lw2,pr=rw2) for(int i=0;i<8;i++) process(pl,pr,512);
        void CCw(int c,int v)=>shortIn((uint)(0xB0|(c<<8)|(v<<16)),0);
        CCw(0,bkw); CCw(32,0); CCw(7,127); CCw(10,64); CCw(91,0); CCw(93,0);
        shortIn((uint)(0xC0|(pgw<<8)),0); flush();
        fixed(float* pl=lw2,pr=rw2) process(pl,pr,512);
        shortIn((uint)(0x90|(ntw<<8)|(vlw<<16)),0); flush();
        for(int t=0;t<ticks;t++) fixed(float* pl=lw2,pr=rw2) process(pl,pr,320);
        Console.WriteLine($"prog {pgw} note {ntw} vel {vlw} map {mpw} after {ticks} ticks");
        for(int v=0;v<64;v++){
            if((*(byte*)(fbw+v*0x50)&1)==0) continue;
            long pv=vcw+(long)v*0x220;
            int w1fc=*(int*)(pv+0x1fc), w200=*(int*)(pv+0x200);
            // +0xb8 is the increment `voice_pitch_block_init` finally writes -- what the resampler
            // is actually driven by, and the one number directly comparable with this port's
            // `pitch_word`. +0x1f8 is the absolute pitch it was derived from.
            Console.WriteLine($"  voice{v}: ramp+0xb8={*(uint*)(pv+0xb8)} abs+0x1f8={*(int*)(pv+0x1f8)}"
                            + $" env+0x64={*(int*)(pv+0x64)}");
            Console.WriteLine($"  voice{v}: 0x1fc={w1fc} 0x200={w200} delta={w200-w1fc}"
                + $" state16c={*(byte*)(pv+0x16c)} flag4={*(byte*)(pv+4)} retrig1b0={*(byte*)(pv+0x1b0)}"
                + $" -> {(w1fc==w200 ? "ADOPTED" : "not adopted")}");
        }
        return;
    }
    // stagedpitch mode: which voices does the second-fine-tune copy actually fire for?
    //
    //   Whether the staged word reaches the sampler at all is settled and this mode is NOT asking
    //   it: voices_control_update @1800849a0 walks the voice array with its pointer at voice+4, so
    //   `voice+0x1fc = voice+0x200` spells as displacement 0x1fc and every search for the literal
    //   offset missed it. It runs on every control tick while voice+0x16c is 1 -- idempotent after
    //   the first, since the words are equal from then on -- and a sounding note is tuned with both
    //   fine tunes. See pitchword above, which reads the two words statically.
    //
    //   What is open is the *gate*: the copy needs voice+4 set with voice+0x1b0 clear, and that
    //   resolves per voice rather than per patch. Wave 2883 takes the term on eight notes and
    //   ignores it on seven, and twelve more waves split the same way, which is why no descriptor
    //   field ever explained "this instrument wants it, that one does not". Nothing static can see
    //   the split, so this drives it: bend the pitch once *before* the crossing and once *after*,
    //   reading the increment either side of each bend. On a voice the gate refused, both bends
    //   multiply the increment by the same factor; on one it allowed, the later bend carries the
    //   term as well and its factor is larger by 2^(delta/12000). Sweep the notes of one wave to
    //   find what the two flags track.
    //   args: dll stagedpitch <prog> <note> <vel> <map> <ticksBeforeBend> [ticksAfter]
    if (args.Length > 1 && args[1] == "stagedpitch")
    {
        int pgs2=int.Parse(args[2]), nts2=int.Parse(args[3]), vls2=int.Parse(args[4]);
        int mps2=int.Parse(args[5]), waitTicks=int.Parse(args[6]);
        int after=args.Length>7?int.Parse(args[7]):6;
        setSR(32000f); setBS(512); activate(32000f,512); setThr();
        long fbs2=b+0x1a1b5b8, sss2=b+0x1a1b570;
        var getVCs2=(delegate* unmanaged[Cdecl]<int,long>)(b+0x5c360);
        long vcs2=getVCs2(0);
        var ls3=new float[512]; var rs3=new float[512];
        GsReset(); for(int c=0;c<16;c++) ToneMap0(c,mps2); flush();
        fixed(float* pl=ls3,pr=rs3) for(int i=0;i<8;i++) process(pl,pr,512);
        void CCs2(int c,int v)=>shortIn((uint)(0xB0|(c<<8)|(v<<16)),0);
        CCs2(0,0); CCs2(32,0); CCs2(7,127); CCs2(10,64); CCs2(91,0); CCs2(93,0);
        shortIn((uint)(0xC0|(pgs2<<8)),0); flush();
        fixed(float* pl=ls3,pr=rs3) process(pl,pr,512);
        shortIn((uint)(0x90|(nts2<<8)|(vls2<<16)),0); flush();
        for(int t=0;t<waitTicks;t++) fixed(float* pl=ls3,pr=rs3) process(pl,pr,320);

        var before=new System.Collections.Generic.Dictionary<int,int>();
        var words=new System.Collections.Generic.Dictionary<int,(int,int)>();
        for(int v=0;v<64;v++){
            if((*(byte*)(fbs2+v*0x50)&1)==0) continue;
            long pv=vcs2+(long)v*0x220;
            before[v]=*(int*)(b+(0x181a1cbf0L-0x180000000L)+(long)v*0x18+0x14);
            words[v]=(*(int*)(pv+0x1fc), *(int*)(pv+0x200));
        }
        // Full bend up on the default two-semitone range, then watched tick by tick: the increment
        // lives in the pitch ramp's slot (g_voice_ramp_pitch @181a1cbf0, stride 0x18, +0x14), not
        // in the voice's own accumulator.
        shortIn((uint)(0xE0|(0x7F<<8)|(0x7F<<16)),0); flush();
        for(int t=0;t<after;t++){
            fixed(float* pl=ls3,pr=rs3) process(pl,pr,320);
            Console.Write($"  t+{t+1}:");
            foreach(var k in before.Keys){
                long rp2=b+(0x181a1cbf0L-0x180000000L)+(long)k*0x18;
                long pv3=vcs2+(long)k*0x220;
                Console.Write($" v{k} inc={*(int*)(rp2+0x14)} pitch6c={*(int*)(pv3+0x6c)}");
            }
            Console.WriteLine();
        }

        Console.WriteLine($"prog {pgs2} note {nts2} map {mps2}, bend sent after {waitTicks} ticks");
        foreach(var kv in before){
            int v=kv.Key;
            int inc0=kv.Value;
            long pv2=vcs2+(long)v*0x220;
            int inc1=*(int*)(b+(0x181a1cbf0L-0x180000000L)+(long)v*0x18+0x14);
            int pitchNow=*(int*)(pv2+0x6c);
            var (w1fc,w200)=words[v];
            double factor = inc0!=0 ? (double)inc1/inc0 : 0.0;
            double cents = inc0!=0 ? 1200.0*System.Math.Log2((double)inc1/inc0) : 0.0;
            Console.WriteLine($"  voice{v}: inc {inc0} -> {inc1}  factor {factor:0.000000} ({cents:+0.00;-0.00} cents)"
                + $"   pitch6c={pitchNow} 0x1fc={w1fc} 0x200={w200}"
                + $" staged={(w1fc==w200?"adopted":"pending")}");
        }
        return;
    }
    // pitchmat mode: what pitch does the module compute for a note once a file's own control-matrix
    //   SysEx has been replayed into it? `part_mod_depth_recalc` sums the five matrix sources into
    //   part+0x3a2 and writes the scaled milli-semitone result to part+0x3ba, so those two words are
    //   the module's own answer rather than an inference from the audio. Reads the sounding voice's
    //   pitch words beside them.
    //   args: dll pitchmat <hexfile> <channel> <note> <vel> <map> [cc1] [ticks]
    if (args.Length > 1 && args[1] == "pitchmat")
    {
        var linesPm = File.ReadAllLines(args[2]);
        int chPm=int.Parse(args[3]), ntPm=int.Parse(args[4]), vlPm=int.Parse(args[5]);
        int mpPm=int.Parse(args[6]);
        int cc1Pm=args.Length>7?int.Parse(args[7]):-1;
        int tkPm=args.Length>8?int.Parse(args[8]):8;
        setSR(32000f); setBS(512); activate(32000f,512); setThr();
        long fbPm=b+0x1a1b5b8;
        var getVCPm=(delegate* unmanaged[Cdecl]<int,long>)(b+0x5c360);
        long vcPm=getVCPm(0);
        var lPm=new float[512]; var rPm=new float[512];
        GsReset(); for(int c=0;c<16;c++) ToneMap0(c,mpPm); flush();
        fixed(float* pl=lPm,pr=rPm) for(int i=0;i<8;i++) process(pl,pr,512);

        int sent=0;
        foreach(var line in linesPm){
            var s=line.Trim(); if(s.Length==0||s.StartsWith("#")) continue;
            var parts=s.Split(new[]{' ','\t'}, StringSplitOptions.RemoveEmptyEntries);
            var msg=new byte[parts.Length];
            for(int i=0;i<parts.Length;i++) msg[i]=Convert.ToByte(parts[i],16);
            SendSysEx(msg); ++sent;
        }
        flush();
        fixed(float* pl=lPm,pr=rPm) for(int i=0;i<4;i++) process(pl,pr,512);
        if(cc1Pm>=0){ shortIn((uint)(0xB0|chPm|(1<<8)|(cc1Pm<<16)),0); flush(); }
        fixed(float* pl=lPm,pr=rPm) for(int i=0;i<4;i++) process(pl,pr,512);

        // The part array lives on the heap and the base is re-read every time rather than cached:
        // a reset or a reallocation between renders would leave a stale pointer reading nothing.
        long PartPm(int ch)=>(*(long*)(b+0x1a222a0))+(long)ch*0x488;
        Console.WriteLine($"replayed {sent} sysex; channel {chPm} note {ntPm} vel {vlPm} map {mpPm} cc1 {cc1Pm}");
        Console.WriteLine($"  g_part_array_base    = 0x{*(long*)(b+0x1a222a0):x}");
        // The pitch key-follow gate, across every part: partial_compute_pitch falls back to curve
        // row 2 when the voice's +0x169 is zero, and +0x169 is copied from this byte.
        Console.Write("  part+0x10 gate:");
        for(int ch=0; ch<16; ch++) Console.Write($" {ch}={*(byte*)(PartPm(ch)+0x10)}");
        Console.WriteLine();
        for(int ch=0; ch<4; ch++)
            Console.WriteLine($"  ch{ch}: 0x3a2 raw={*(short*)(PartPm(ch)+0x3a2)}"
                + $" 0x3ba scaled={*(short*)(PartPm(ch)+0x3ba)}"
                + $" 0x448 tune={*(ushort*)(PartPm(ch)+0x448)}"
                + $" -> {((int)(*(ushort*)(PartPm(ch)+0x448)) * 1000) >> 13} mst"
                + $" (with -0x7e8: {(((int)(*(ushort*)(PartPm(ch)+0x448)) * 1000) >> 13) - 0x7e8})"
                // voice_pitch_keyfollow gates its whole branch on this byte: 0x80 is neutral and
                // takes the early return, anything else adds a further term scaled by the
                // partial's +0x161 row of DAT_1819a7900.
                + $" 0x3db keyfollow={*(byte*)(PartPm(ch)+0x3db)}"
                + $" ({(*(byte*)(PartPm(ch)+0x3db) == 0x80 ? "neutral" : "ACTIVE")})"
                // The gate on the pitch key-follow curve row: partial_compute_pitch takes row 2
                // only when the voice's +0x169 is zero, and +0x169 is copied from this byte.
                + $" | 0x10 gate={*(byte*)(PartPm(ch)+0x10)}");
        shortIn((uint)(0x90|chPm|(ntPm<<8)|(vlPm<<16)),0); flush();
        for(int tk=0;tk<tkPm;tk++) fixed(float* pl=lPm,pr=rPm) process(pl,pr,320);
        Console.WriteLine($"  after note: ch{chPm} 0x3a2 raw={*(short*)(PartPm(chPm)+0x3a2)}"
            + $" 0x3ba scaled={*(short*)(PartPm(chPm)+0x3ba)}");
        // The live pitch, which is what the resampler is finally driven by: g_voice_ramp_pitch
        // carries the base plus every modulation term -- matrix, LFO, envelope -- so comparing it
        // against a port's own ratio tests the whole chain rather than one term of it.
        // 1 unit = 375/512 milli-semitones.
        for(int v=0;v<64;v++){
            if((*(byte*)(fbPm+v*0x50)&1)==0) continue;
            long pv=vcPm+(long)v*0x220;
            long rp=b+(0x181a1cbf0L-0x180000000L)+(long)v*0x18;
            int cur=*(int*)(rp+8), tgt=*(int*)(rp+0xc);
            long blk=*(long*)(pv+0x150);
            Console.WriteLine($"  voice{v}: base 0x1fc={*(int*)(pv+0x1fc)} 0x200={*(int*)(pv+0x200)}"
                + $" | voice 0x168={*(byte*)(pv+0x168)} 0x169={*(byte*)(pv+0x169)}");
            // The whole partial parameter block, so a port's own view can be aligned against it
            // byte for byte rather than one index at a time.
            Console.Write("  block:");
            for(int i=0;i<0x40;i++){
                if(i%16==0) Console.Write($"\n    {i:X2}: ");
                Console.Write($"{*(byte*)(blk+i):X2} ");
            }
            Console.WriteLine();
            Console.WriteLine($"           pitchramp cur={cur} tgt={tgt}"
                + $"  = {cur*375.0/512.0:0.0} / {tgt*375.0/512.0:0.0} mst");
        }
        return;
    }
    // voicesolo mode: capture one voice's contribution to the output on its own.
    //   Every input to the mix is verifiable through other modes; the *output* of a single voice is
    //   not, because the engine sums them. This holds every other sounding voice's four mix-slot
    //   gains at zero, re-zeroing before each chunk because the engine rewrites them every control
    //   tick, and writes the result as raw interleaved float32 -- the same format render-note emits,
    //   so a port's per-partial render can be compared against the engine's directly.
    //   args: dll voicesolo <prog> <note> <vel> <map> <voiceIndex> <seconds> <out.f32>
    if (args.Length > 1 && args[1] == "voicesolo")
    {
        int pgv=int.Parse(args[2]), ntv=int.Parse(args[3]), vlv=int.Parse(args[4]);
        int mpv=int.Parse(args[5]), wantv=int.Parse(args[6]);
        double secv=double.Parse(args[7], System.Globalization.CultureInfo.InvariantCulture);
        string outv=args[8];
        setSR(32000f); setBS(512); activate(32000f,512); setThr();
        long fbv=b+0x1a1b5b8;
        float* slotv=(float*)(b+(0x181a1d930L-0x180000000L));
        var lv2=new float[512]; var rv2=new float[512];
        GsReset(); for(int c=0;c<16;c++) ToneMap0(c,mpv); flush();
        fixed(float* pl=lv2,pr=rv2) for(int i=0;i<8;i++) process(pl,pr,512);
        void CCv2(int c,int v)=>shortIn((uint)(0xB0|(c<<8)|(v<<16)),0);
        CCv2(0,0); CCv2(32,0); CCv2(7,127); CCv2(10,64); CCv2(91,0); CCv2(93,0);
        shortIn((uint)(0xC0|(pgv<<8)),0); flush();
        fixed(float* pl=lv2,pr=rv2) process(pl,pr,512);
        shortIn((uint)(0x90|(ntv<<8)|(vlv<<16)),0); flush();

        const int chunk=32;
        int total=(int)(secv*32000);
        var outBytes=new System.IO.BinaryWriter(System.IO.File.Create(outv));
        // The voice indices are decided on the first chunk and then held: a voice that stops
        // sounding must stay muted rather than silently rejoining the mix.
        int[] keep=null;
        for(int done=0; done<total; done+=chunk){
            if(keep==null){
                var live=new System.Collections.Generic.List<int>();
                for(int v=0;v<64;v++) if((*(byte*)(fbv+v*0x50)&1)!=0) live.Add(v);
                if(live.Count>0) keep=live.ToArray();
            }
            if(keep!=null){
                for(int i=0;i<keep.Length;i++){
                    if(i==wantv) continue;
                    for(int k=0;k<4;k++) slotv[k*0x40+keep[i]]=0f;
                }
            }
            fixed(float* pl=lv2,pr=rv2) process(pl,pr,chunk);
            for(int i=0;i<chunk;i++){ outBytes.Write(lv2[i]); outBytes.Write(rv2[i]); }
        }
        outBytes.Close();
        // Peak and where it lands. A per-voice phase or timing error shows up here as a shifted
        // offset long before it is visible in a spectrum, and the offset is directly comparable
        // against the same figure from a port's own per-partial render.
        {
            var raw = System.IO.File.ReadAllBytes(outv);
            float peak = 0f; int at = 0;
            for (int i = 0; i + 3 < raw.Length; i += 8) {
                float v = System.Math.Abs(BitConverter.ToSingle(raw, i));
                if (v > peak) { peak = v; at = i / 8; }
            }
            Console.WriteLine($"voicesolo prog={pgv} note={ntv} voice={wantv} of {(keep==null?0:keep.Length)}"
                + $" -> {outv} ({total} frames)  peak {peak:0.000000} at sample {at}");
        }
        return;
    }
    // partialmix mode: how loud is each of a tone's partials, relative to the others? Plays one
    //   note and reads every sounding voice's four mix-slot gains, which is where the TVA envelope
    //   and every per-voice level have already been folded in -- so the ratio between two voices is
    //   the partial balance the tone actually sounds with.
    //   args: dll partialmix <prog> <note> <vel> [map] [blocks]
    if (args.Length > 1 && args[1] == "partialmix")
    {
        int pgm=int.Parse(args[2]), nt=int.Parse(args[3]), vel=int.Parse(args[4]);
        int mp=args.Length>5?int.Parse(args[5]):1;
        int blocks=args.Length>6?int.Parse(args[6]):3;
        setSR(32000f); setBS(512); activate(32000f,512); setThr();
        long fbp=b+0x1a1b5b8;
        float* slotGain=(float*)(b+(0x181a1d930L-0x180000000L));
        var lp2=new float[512]; var rp2=new float[512];
        GsReset(); for(int c=0;c<16;c++) ToneMap0(c,mp); flush();
        fixed(float* pl=lp2,pr=rp2) for(int i=0;i<8;i++) process(pl,pr,512);
        void CCp(int c,int v)=>shortIn((uint)(0xB0|(c<<8)|(v<<16)),0);
        CCp(7,127); CCp(10,64); CCp(91,0); CCp(93,0); CCp(11,127);
        shortIn((uint)(0xC0|(pgm<<8)),0); flush();
        fixed(float* pl=lp2,pr=rp2) process(pl,pr,512);
        shortIn((uint)(0x90|(nt<<8)|(vel<<16)),0); flush();
        for(int t=0;t<blocks;t++) fixed(float* pl=lp2,pr=rp2) process(pl,pr,320);
        Console.WriteLine($"prog {pgm} note {nt} vel {vel} map {mp}, after {blocks} control blocks");
        for(int v=0;v<64;v++){
            if((*(byte*)(fbp+v*0x50)&1)==0) continue;
            var g=new double[4];
            for(int k=0;k<4;k++) g[k]=slotGain[k*0x40+v];
            Console.WriteLine($"  voice{v}: slots {g[0]:0.000000} {g[1]:0.000000} {g[2]:0.000000} {g[3]:0.000000}"
                + $"  sum {g[0]+g[1]+g[2]+g[3]:0.000000}");
        }
        return;
    }
    // svfslew mode: does the engine slew its filter coefficients, or step them once a control tick?
    //   Holds a note, steps CC#74 once, and reads g_svf_f_coef / g_svf_q_coef every `chunk` samples
    //   across the step. A coefficient that jumps in one reading is a step; one that walks over many
    //   is the anti-zipper ramp (voice_ctrl_ramp_c/_d), which matters most at high resonance where
    //   a step re-excites the filter on every tick.
    //   args: dll svfslew <prog> <lsb> <note> <vel> <cc74from> <cc74to> <cc71> [chunk] [reads] [gs|xg]
    if (args.Length > 1 && args[1] == "svfslew")
    {
        int pgs=int.Parse(args[2]), lsbs=int.Parse(args[3]), nts=int.Parse(args[4]);
        int vels=int.Parse(args[5]), c74a=int.Parse(args[6]), c74b=int.Parse(args[7]);
        int c71s=int.Parse(args[8]);
        int chunk=args.Length>9?int.Parse(args[9]):32;
        int reads=args.Length>10?int.Parse(args[10]):48;
        bool xgs=args.Length<=11 || args[11]!="gs";
        int c71b=args.Length>12?int.Parse(args[12]):-1;
        setSR(32000f); setBS(512); activate(32000f,512); setThr();
        long fbs=b+0x1a1b5b8;
        var getVCs=(delegate* unmanaged[Cdecl]<int,long>)(b+0x5c360);
        long vcs=getVCs(0);
        float* fcs=(float*)(b+(0x181a1cb70L-0x180000000L));
        float* qcs=(float*)(b+(0x181a1d1f0L-0x180000000L));
        var ls=new float[512]; var rs=new float[512];
        if(xgs) SendSysEx(new byte[]{0xF0,0x43,0x10,0x4C,0x00,0x00,0x7E,0x00,0xF7}); else GsReset();
        flush(); fixed(float* pl=ls,pr=rs) for(int i=0;i<8;i++) process(pl,pr,512);
        void CCs(int c,int v)=>shortIn((uint)(0xB0|(c<<8)|(v<<16)),0);
        CCs(0,0); CCs(32,lsbs); CCs(7,127); CCs(10,64); CCs(91,0); CCs(93,0);
        CCs(74,c74a); CCs(71,c71s);
        shortIn((uint)(0xC0|(pgs<<8)),0); flush();
        shortIn((uint)(0x90|(nts<<8)|(vels<<16)),0); flush();
        fixed(float* pl=ls,pr=rs) for(int i=0;i<6;i++) process(pl,pr,320);
        // Find the sounding voice's lane in the coefficient arrays.
        int lane=-1, grp=-1;
        for(int v=0;v<64;v++){ if((*(byte*)(fbs+v*0x50)&1)==0) continue; lane=v&3; grp=v>>2; break; }
        if(lane<0){ Console.WriteLine("no sounding voice"); return; }
        CCs(74,c74b); if(c71b>=0) CCs(71,c71b); flush();
        // Far enough past the control tick that the retarget has happened and the ramp is still
        // running, so target and step can be read rather than inferred from the decoded trace.
        fixed(float* pl=ls,pr=rs) process(pl,pr,448);
        // The cutoff ramp's own state -- the thing the revert of the ported ramps was blocked on.
        // The flag word carries the divider index in bits 3-4 and the rate sits at +0x2 of the
        // per-voice slot, neither tied back to any tone-table byte. Reading them off a live voice
        // pins them per tone instead of standing one guessed index in for all of them.
        {
            long slot = b + (0x181a10740L - 0x180000000L) + (long)(grp * 4 + lane) * 0x18;
            ushort flag = *(ushort*)slot;
            Console.WriteLine($"rampC(f): flag=0x{flag:X4} divider_index={(flag >> 3) & 3} active={flag & 1}"
                + $" rate={*(short*)(slot + 2)} current={*(int*)(slot + 8)}"
                + $" target={*(int*)(slot + 12)} step={*(int*)(slot + 16)}");
            // voice_ctrl_ramp_d's own slot, the damping side. Same layout, different law.
            long slotd = b + (0x181a0fb40L - 0x180000000L) + (long)(grp * 4 + lane) * 0x18;
            ushort flagd = *(ushort*)slotd;
            Console.WriteLine($"rampD(q): flag=0x{flagd:X4} divider_index={(flagd >> 3) & 3} active={flagd & 1}"
                + $" rate={*(short*)(slotd + 2)} current={*(int*)(slotd + 8)}"
                + $" target={*(int*)(slotd + 12)} accum={*(int*)(slotd + 16)}");
        }
        Console.WriteLine($"sample,f,q  (cc74 {c74a} -> {c74b} at sample 0, chunk {chunk})");
        for(int i=0;i<reads;i++){
            fixed(float* pl=ls,pr=rs) process(pl,pr,(uint)chunk);
            Console.WriteLine($"{(i+1)*chunk},{fcs[grp*16+lane]:0.000000},{qcs[grp*16+lane]:0.000000}");
        }
        return;
    }
    // drumnrpn mode: which NRPN MSBs actually write a drum part's per-key record? Sweeps every MSB
    //   from 0 to 0x3f, snapshotting the *whole* 0x50c-byte record either side of one NRPN and
    //   reporting the byte offsets that moved -- so a plane nobody has named yet still shows up.
    //   The record hangs off part+0x18, which is heap, so this reads it through a sounding voice
    //   rather than off any static address.
    //   args: dll drumnrpn <note> [value] [gs|xg] [prog]
    if (args.Length > 1 && (args[1] == "drumnrpn" || args[1] == "svfslew"))
    {
        int ntn=int.Parse(args[2]);
        int valn=args.Length>3?int.Parse(args[3]):0x50;
        bool xgn=args.Length>4 && args[4]=="xg";
        int pgn=args.Length>5?int.Parse(args[5]):0;
        setSR(32000f); setBS(512); activate(32000f,512); setThr();
        long fbn=b+0x1a1b5b8;
        var getVCn=(delegate* unmanaged[Cdecl]<int,long>)(b+0x5c360);
        long vcn=getVCn(0);
        var ln=new float[512]; var rn=new float[512];
        void CCn(int c,int v)=>shortIn((uint)((0xB0|9)|(c<<8)|(v<<16)),0);
        if(xgn) SendSysEx(new byte[]{0xF0,0x43,0x10,0x4C,0x00,0x00,0x7E,0x00,0xF7}); else GsReset();
        flush(); fixed(float* pl=ln,pr=rn) for(int i=0;i<8;i++) process(pl,pr,512);
        CCn(7,127); CCn(10,64); CCn(91,0); CCn(93,0);
        shortIn((uint)((0xC0|9)|(pgn<<8)),0); flush();
        fixed(float* pl=ln,pr=rn) process(pl,pr,512);
        // Strike once so a voice exists to reach the part -- and so the record is the live one.
        shortIn((uint)((0x90|9)|(ntn<<8)|(110<<16)),0); flush();
        fixed(float* pl=ln,pr=rn) for(int i=0;i<3;i++) process(pl,pr,320);
        long mapn=0, partn=0;
        for(int v=0;v<64;v++){ if((*(byte*)(fbn+v*0x50)&1)==0) continue;
            partn=*(long*)(vcn+(long)v*0x220+0x128); mapn=*(long*)(partn+0x18); break; }
        if(mapn==0){ Console.WriteLine("no sounding voice -- cannot reach the record"); return; }
        const int RecLen=0x50c;
        var snap=new byte[RecLen];
        void Snap(){ System.Runtime.InteropServices.Marshal.Copy((nint)mapn,snap,0,RecLen); }
        // The gates nrpn_apply tests before writing: Rx NRPN is bit 15 of the Rx word at part+0x3d6,
        // Rx CC is bit 11, and the drum test is part+0x12 bit 5 set with the low five bits not all
        // set. A sweep that writes nothing means one of these, not a missing handler.
        ushort rxn=*(ushort*)(partn+0x3d6);
        Console.WriteLine($"mode={(xgn?"xg":"gs")} note={ntn} value=0x{valn:X2} record=0x{mapn:X}"
            +$" rx=0x{rxn:X4} rxNRPN={(rxn&0x8000)!=0} rxCC={(rxn&0x800)!=0}"
            +$" flags=0x{*(byte*)(partn+0x12):X2}");
        for(int msb=0;msb<=0x3f;msb++){
            Snap();
            var before=(byte[])snap.Clone();
            CCn(99,msb); CCn(98,ntn); CCn(6,valn); flush();
            fixed(float* pl=ln,pr=rn) process(pl,pr,512);
            Snap();
            var moved=new System.Collections.Generic.List<string>();
            for(int i=0;i<RecLen;i++)
                if(before[i]!=snap[i]) moved.Add($"+0x{i:X3}({before[i]}->{snap[i]})");
            if(moved.Count>0) Console.WriteLine($"  MSB 0x{msb:X2}: {string.Join(" ",moved)}");
        }
        Console.WriteLine("done");
        return;
    }
    // xgdrumfilt mode: does an XG Drum Setup message reach a drum voice's filter? Strikes a drum
    //   key in XG mode and reads the voice's resonance byte (+0xee) and both SVF coefficients, then
    //   sends one XG Drum Setup parameter (3n rr pp) and strikes again. A parameter the module
    //   stores moves the readback; one it parses and drops leaves every field identical.
    //   Read off the *voice*, through its heap part pointer, rather than off any static: the drum
    //   setup records live behind a pointer the module allocates, so a static dump cannot see them.
    //   args: dll xgdrumfilt <note> <param> <value> [prog]
    //   param is the XG Drum Setup parameter: 0b filter cutoff, 0c filter resonance, 02 level.
    if (args.Length > 1 && (args[1] == "xgdrumfilt" || args[1] == "drumnrpn" || args[1] == "svfslew"))
    {
        int ntx=int.Parse(args[2]);
        int prmx=Convert.ToInt32(args[3],16);
        int valx=int.Parse(args[4]);
        int pgx=args.Length>5?int.Parse(args[5]):0;
        setSR(32000f); setBS(512); activate(32000f,512); setThr();
        long fbx=b+0x1a1b5b8;
        var getVCx=(delegate* unmanaged[Cdecl]<int,long>)(b+0x5c360);
        long vcx=getVCx(0);
        float* fcx=(float*)(b+(0x181a1cb70L-0x180000000L));
        float* qcx=(float*)(b+(0x181a1d1f0L-0x180000000L));
        var lx=new float[512]; var rx=new float[512];
        void CCx(int c,int v)=>shortIn((uint)((0xB0|9)|(c<<8)|(v<<16)),0);
        SendSysEx(new byte[]{0xF0,0x43,0x10,0x4C,0x00,0x00,0x7E,0x00,0xF7});
        flush(); fixed(float* pl=lx,pr=rx) for(int i=0;i<8;i++) process(pl,pr,512);
        CCx(7,127); CCx(10,64); CCx(91,0); CCx(93,0);
        shortIn((uint)((0xC0|9)|(pgx<<8)),0); flush();
        fixed(float* pl=lx,pr=rx) process(pl,pr,512);
        void Strikex(){
            shortIn((uint)((0x90|9)|(ntx<<8)|(110<<16)),0); flush();
            fixed(float* pl=lx,pr=rx) for(int i=0;i<3;i++) process(pl,pr,320);
        }
        void Showx(string tag){
            bool any=false;
            for(int v=0;v<64;v++){
                if((*(byte*)(fbx+v*0x50)&1)==0) continue;
                long pv=vcx+(long)v*0x220;
                int lane=v&3, grp=v>>2;
                Console.WriteLine($"{tag} voice{v}: reso_ee={*(byte*)(pv+0xee)} f={fcx[grp*16+lane]:0.000000}"
                    +$" q={qcx[grp*16+lane]:0.000000} cutoff_cc={*(int*)(pv+0xcc)} type_f5={*(byte*)(pv+0x1f5)}");
                if(!any){
                    // The positive control. The per-key planes hang off the part, which hangs off
                    // the voice -- all heap. A parameter the module stores moves one of these, so a
                    // run where every plane is also unchanged means the message never landed at all
                    // rather than that this particular parameter is dropped.
                    long part=*(long*)(pv+0x128);
                    long map=*(long*)(part+0x18);
                    Console.WriteLine($"{tag}  planes[{ntx}]: level={*(byte*)(map+0x100+ntx)}"
                        +$" pitch={*(sbyte*)(map+0x180+ntx)} group={*(byte*)(map+0x200+ntx)}"
                        +$" pan={*(byte*)(map+0x280+ntx)} rev={*(byte*)(map+0x300+ntx)}"
                        +$" cho={*(byte*)(map+0x380+ntx)} flags=0x{*(byte*)(map+0x480+ntx):X2}");
                }
                any=true;
            }
            if(!any) Console.WriteLine($"{tag}: no sounding voice");
        }
        Strikex(); Showx("before");
        // 3n rr pp vv -- setup 0, key rr, parameter pp.
        SendSysEx(new byte[]{0xF0,0x43,0x10,0x4C,0x30,(byte)ntx,(byte)prmx,(byte)valx,0xF7});
        flush(); fixed(float* pl=lx,pr=rx) process(pl,pr,512);
        // Note off and re-strike, so a parameter that is only read at note-on still shows up.
        shortIn((uint)((0x80|9)|(ntx<<8)|(64<<16)),0); flush();
        fixed(float* pl=lx,pr=rx) for(int i=0;i<12;i++) process(pl,pr,512);
        Strikex(); Showx("after ");
        return;
    }
    // gsdrumnrpn mode: the GS counterpart of drumnrpn above. That one drives the drum setup over XG
    //   SysEx; this one drives it over the GS NRPN a plain MIDI file actually sends -- CC#99 the
    //   parameter, CC#98 the key, CC#6 the value -- and dumps the same per-key planes before and
    //   after, so a write that lands can be told apart from one that does not.
    //   A trailing program number sends a program change AFTER the write and dumps a third time,
    //   which is how the lifetime of an override across a kit reload was settled: the reload
    //   overwrites every plane, and only when the program names a kit. On the SC-55 drum row 0, 1
    //   and 8 are Standard 1, Standard 2 and Room and all three clear; 7 and 63 name nothing and
    //   the override survives.
    //   args: dll gsdrumnrpn <note> <nrpnMsbDec> <valueDec> [prog] [rxNrpn] [progAfterWrite]
    if (args.Length > 1 && args[1] == "gsdrumnrpn")
    {
        int ntg=int.Parse(args[2]);
        int prmg=int.Parse(args[3]);
        int valg=int.Parse(args[4]);
        int pgg=args.Length>5?int.Parse(args[5]):0;
        bool rxg=args.Length>6 && args[6]=="1";
        // A program change sent AFTER the write, to ask which planes a kit reload clears. -1 skips.
        int pcAfter=args.Length>7?int.Parse(args[7]):-1;
        setSR(32000f); setBS(512); activate(32000f,512); setThr();
        long fbg=b+0x1a1b5b8;
        var getVCg=(delegate* unmanaged[Cdecl]<int,long>)(b+0x5c360);
        long vcg=getVCg(0);
        var lg=new float[512]; var rg=new float[512];
        void CCg(int c,int v)=>shortIn((uint)((0xB0|9)|(c<<8)|(v<<16)),0);
        GsReset();
        flush(); fixed(float* pl=lg,pr=rg) for(int i=0;i<8;i++) process(pl,pr,512);
        if(rxg) SendSysEx(Dt1(0x40,(byte)(0x10|BlockNum(9)),0x0A,0x01));
        CCg(7,127); CCg(10,64); CCg(91,0); CCg(93,0);
        shortIn((uint)((0xC0|9)|(pgg<<8)),0); flush();
        fixed(float* pl=lg,pr=rg) process(pl,pr,512);
        void Strikeg(){
            shortIn((uint)((0x90|9)|(ntg<<8)|(110<<16)),0); flush();
            fixed(float* pl=lg,pr=rg) for(int i=0;i<3;i++) process(pl,pr,320);
        }
        void Showg(string tag){
            for(int v=0;v<64;v++){
                if((*(byte*)(fbg+v*0x50)&1)==0) continue;
                long pv=vcg+(long)v*0x220;
                long part=*(long*)(pv+0x128);
                long map=*(long*)(part+0x18);
                Console.WriteLine($"{tag} voice{v} planes[{ntg}]: level={*(byte*)(map+0x100+ntg)}"
                    +$" pitch={*(sbyte*)(map+0x180+ntg)} group={*(byte*)(map+0x200+ntg)}"
                    +$" pan={*(byte*)(map+0x280+ntg)} rev={*(byte*)(map+0x300+ntg)}"
                    +$" cho={*(byte*)(map+0x380+ntg)} dly={*(byte*)(map+0x400+ntg)}"
                    +$" flags=0x{*(byte*)(map+0x480+ntg):X2}");
                // The pan the voice actually resolved, and the part panpot it started from. If the
                // plane moves and this does not, the write lands and the read side is at fault.
                Console.WriteLine($"{tag}   voice pan f8={*(short*)(pv+0xf8)}"
                    +$" gainL={*(ushort*)(pv+0xf4)} gainR={*(ushort*)(pv+0xf6)}"
                    +$" partPanpot={*(byte*)(part+0x3dd)} rxFlags=0x{*(ushort*)(part+0x3d6):X4}"
                    +$" part12=0x{*(byte*)(part+0x12):X2}");
                return;
            }
            Console.WriteLine($"{tag}: no sounding voice");
        }
        Strikeg(); Showg("before");
        CCg(99,prmg); CCg(98,ntg); CCg(6,valg); flush();
        fixed(float* pl=lg,pr=rg) process(pl,pr,512);
        shortIn((uint)((0x80|9)|(ntg<<8)|(64<<16)),0); flush();
        fixed(float* pl=lg,pr=rg) for(int i=0;i<12;i++) process(pl,pr,512);
        Strikeg(); Showg("after ");
        if(pcAfter>=0){
            shortIn((uint)((0x80|9)|(ntg<<8)|(64<<16)),0); flush();
            fixed(float* pl=lg,pr=rg) for(int i=0;i<12;i++) process(pl,pr,512);
            shortIn((uint)((0xC0|9)|(pcAfter<<8)),0); flush();
            fixed(float* pl=lg,pr=rg) for(int i=0;i<4;i++) process(pl,pr,512);
            Strikeg(); Showg($"pc{pcAfter,-3}");
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
    // keysend mode: ONE cell of the per-key-send matrix, in its own process.
    //
    //   The method is Matt Phelps's, from NativeTS PR #3. He found the per-key reverb plane
    //   unwired BY EAR on an SC-88Pro file -- in STANDARD 1 the kick reads 0 where the snare and
    //   crash read 127, so any kit played with the send open had a room on the one drum the module
    //   keeps dry -- and pinned it with a 3x3 of the key's own send against the part's, showing
    //   that the two multiply rather than one overriding the other. The chorus plane at +0x380 and
    //   the delay plane at +0x400 were left latched there because that measurement had not been
    //   done for them. This mode is his 3x3, run for those two.
    //
    //   One cell per run, deliberately. Nine cells in one process is not nine measurements: a GS
    //   reset does not restore the module -- `resetstate` puts the residue at about -51 dB and the
    //   generator is never reseeded -- so cell n carries cell n-1. A first attempt at this ran the
    //   whole matrix in-process and its strikes varied by 30x on identical settings.
    //
    //   Parameter numbering per the SC-8850 manual, cross-checked against SpessaSynth's GS handler:
    //   the address is 41 <(map<<4)|param> <note>, and param is 1 pitch, 2 level, 3 assign group,
    //   4 PAN, 5 REVERB, 6 CHORUS, 7/8 rx note off/on, 9 DELAY. Reverb is 5, not 4 -- writing 4
    //   pans the key hard right, which reads as silence on the left channel and nothing else.
    //   args: dll keysend <kit> <note> <gsParam> <cc> <keyVal> <ccVal> [map]
    if (args.Length > 1 && args[1] == "keysend")
    {
        int kitK=int.Parse(args[2]), ntK=int.Parse(args[3]);
        int prmK=int.Parse(args[4]), ccK=int.Parse(args[5]);
        int keyV=int.Parse(args[6]), ccV=int.Parse(args[7]);
        int mpK=args.Length>8?int.Parse(args[8]):4;
        setSR(32000f); setBS(512); activate(32000f,512); setThr();
        var lK=new float[512]; var rK=new float[512];
        void CCk(int ch,int c,int v)=>shortIn((uint)((0xB0|ch)|(c<<8)|(v<<16)),0);

        GsReset(); if(mpK>=1&&mpK<=4) for(int c=0;c<16;c++) ToneMap0(c,mpK); flush();
        fixed(float* pl=lK,pr=rK) for(int i=0;i<8;i++) process(pl,pr,512);
        CCk(9,7,127); CCk(9,10,64);
        CCk(9,91,0); CCk(9,93,0); CCk(9,94,0);      // all three off, then only the one under test
        shortIn((uint)(0xC0|9|(kitK<<8)),0);
        SendSysEx(Dt1((byte)0x41,(byte)prmK,(byte)ntK,(byte)keyV));
        CCk(9,ccK,ccV);
        flush();
        fixed(float* pl=lK,pr=rK) for(int i=0;i<4;i++) process(pl,pr,512);
        shortIn((uint)(0x99|(ntK<<8)|(110<<16)),0); flush();

        // 0.5 s of hit discarded, then 1.0 s of tail. Both channels, since a per-key pan would
        // otherwise masquerade as a level change.
        // TWO windows, because the three networks do not decay alike. Reverb and delay leave a
        // tail half a second on; a chorus is a twenty to thirty millisecond modulated delay and
        // has contributed everything it will while the hit is still sounding. Measuring only the
        // tail reads the floor for chorus and says nothing about it.
        double early=0.0; int ne=0, hitPk=0;
        double hit=0.0;
        fixed(float* pl=lK,pr=rK) for(int i=0;i<31;i++){ process(pl,pr,512);
            for(int j=0;j<512;j++){
                hit=System.Math.Max(hit,System.Math.Max(System.Math.Abs((double)pl[j]),System.Math.Abs((double)pr[j])));
                early+=(double)pl[j]*pl[j]+(double)pr[j]*pr[j]; ne+=2; } }
        double sum=0.0; int n=0;
        fixed(float* pl=lK,pr=rK) for(int i=0;i<62;i++){ process(pl,pr,512);
            for(int j=0;j<512;j++){ sum+=(double)pl[j]*pl[j]+(double)pr[j]*pr[j]; n+=2; } }
        Console.WriteLine($"{System.Math.Sqrt(sum/n):0.00000000} early={System.Math.Sqrt(early/ne):0.00000000} hit={hit:0.000000}");
        return;
    }
    // envseg mode: the TVA envelope segment's own state, tick by tick, plus the curve it rides.
    //
    //   env_ramp_segment @180083a70 works on a block based at voice+0xc:
    //     +0x0c stage (4 = done)   +0x0d rate modifier   +0x10 CURVE   +0x12 rate
    //     +0x14 start   +0x16 target   +0x18 out   +0x1a phase   +0x1c carry
    //   Per tick phase += rate * (g_env_block_speed + carry); on wrap past 0xffff the segment ends
    //   and out := target, otherwise out interpolates start -> target by phase. The curve word
    //   selects how: 0x4000 is linear, 0 rides a 256-entry table at DAT_1819a7a90 indexed by the
    //   INVERTED phase high byte, anything else forces out to zero.
    //
    //   What partial_load_params hands the amplitude ramp is +0x18, the interpolated OUT, not the
    //   segment target -- so the ramp chases the envelope and the attack's PEAK is a property of
    //   the curve rather than of any single stored level. This prints the three that decide it.
    //   args: dll envseg <prog> <note> <vel> <map> [ticks]
    if (args.Length > 1 && args[1] == "envseg")
    {
        int pgE=int.Parse(args[2]), ntE=int.Parse(args[3]), vlE=int.Parse(args[4]), mpE=int.Parse(args[5]);
        int tkE=args.Length>6?int.Parse(args[6]):24;
        // CC#11. If expression reaches the per-voice TVA level, voice+0xac moves with it; if it is
        // only a part-level multiplier applied after the envelope, +0xac is unchanged and the
        // level lands somewhere else. The two are indistinguishable at 127, which is why every
        // clean-part measurement so far agreed.
        int exprE=args.Length>7?int.Parse(args[7]):127;
        setSR(32000f); setBS(512); activate(32000f,512); setThr();
        long fbE=b+0x1a1b5b8;
        var getVCe=(delegate* unmanaged[Cdecl]<int,long>)(b+0x5c360);
        long vcE=getVCe(0);
        var lE=new float[512]; var rE=new float[512];
        void CCe(int c,int v)=>shortIn((uint)(0xB0|(c<<8)|(v<<16)),0);
        GsReset(); for(int c=0;c<16;c++) ToneMap0(c,mpE); flush();
        fixed(float* pl=lE,pr=rE) for(int i=0;i<8;i++) process(pl,pr,512);
        CCe(7,127); CCe(11,exprE); CCe(10,64); CCe(91,0); CCe(93,0);
        shortIn((uint)(0xC0|(pgE<<8)),0); flush();
        fixed(float* pl=lE,pr=rE) process(pl,pr,512);
        shortIn((uint)(0x90|(ntE<<8)|(vlE<<16)),0); flush();

        Console.WriteLine($"envseg prog={pgE} note={ntE} vel={vlE} map={mpE} cc11={exprE}");
        // The sampler's sub-sample phase alongside the envelope. sampler_pcm reads
        // *(ushort*)(param_1[4] + 6) -- state +0x46 -- and selects an interpolator phase with
        // `frac >> 9`, so the kernel has 128 positions. A transient read on an exact sample
        // boundary keeps its peak; read between samples it is spread across the four taps and
        // flattened, which is the shape of the attack difference this is chasing.
        long ssE=b+0x1a1b570;
        Console.WriteLine("  tick  stage curve   rate  start target    out    phase  carry   amp+0xac   sampPos  frac16  ph/128");
        for(int t=0;t<tkE;t++){
            fixed(float* pl=lE,pr=rE) process(pl,pr,320);
            for(int v=0;v<64;v++){
                if((*(byte*)(fbE+v*0x50)&1)==0) continue;
                long pv=vcE+(long)v*0x220;
                Console.WriteLine($"  {t,4}  {*(byte*)(pv+0x0c),5} 0x{*(ushort*)(pv+0x10):X4} {*(ushort*)(pv+0x12),6}"
                    + $" {*(ushort*)(pv+0x14),6} {*(ushort*)(pv+0x16),6} {*(ushort*)(pv+0x18),6}"
                    + $" {*(ushort*)(pv+0x1a),8} {*(ushort*)(pv+0x1c),6}   {*(int*)(pv+0xac),8}"
                    + $"  {*(int*)(ssE+(long)v*0x50+0x28),8}  {*(ushort*)(ssE+(long)v*0x50+0x46),6}"
                    + $"  {*(ushort*)(ssE+(long)v*0x50+0x46)>>9,6}");
                break;   // one voice is enough for a single-partial probe
            }
        }
        // The curve itself. Pairs on a 2-byte stride, so the table proper is 256 entries.
        Console.WriteLine("\n  DAT_1819a7a90 curve table, 256 entries of u16:");
        long tbl=b+(0x1819a7a90L-0x180000000L);
        for(int i=0;i<256;i+=16){
            Console.Write($"    [{i,3}]");
            for(int k=0;k<16;k++) Console.Write($" {*(ushort*)(tbl+(i+k)*2),5}");
            Console.WriteLine();
        }
        return;
    }
    // chorusin mode: read the chorus input bus, by stopping the thing that consumes it.
    //
    //   panwet.mid's chorus return is short by a pure gain of 5.79. Every coefficient on the path
    //   has been read out of the module and matches this port -- the part send, the three system
    //   sends, the return level, the write gain, the whole send-mix gain bank, and the network's
    //   impulse response -- so the one stage left unexamined is the input bus at 0x181a190f0, and
    //   it has never been readable: `buscap` reads 2.0003e-05 whatever is played.
    //
    //   That figure is the answer to why. fx_process_block @18008c2c0 seeds the send-mix
    //   accumulator at 1e-05 per lane before summing anything into it, so 2.0003e-05 is what an
    //   accumulation that summed to nothing leaves behind -- the bus had already been consumed and
    //   rewritten by the time anything outside could look.
    //
    //   So rather than trying to catch it mid-block, this stops the consumer. fx_chorus_stage_r
    //   @180085460 is patched to a single `ret` for one block, which leaves the bus holding what
    //   the send mix wrote. The byte is restored immediately after. A control pass with the module
    //   untouched runs first, so the difference between the two is the evidence rather than the
    //   absolute number.
    //   args: dll chorusin <prog> <note> <vel> <map> [cc93]
    if (args.Length > 1 && args[1] == "chorusin")
    {
        int pgC=int.Parse(args[2]), ntC=int.Parse(args[3]), vlC=int.Parse(args[4]), mpC=int.Parse(args[5]);
        int sndC=args.Length>6?int.Parse(args[6]):127;
        int dlyC=args.Length>7?int.Parse(args[7]):0;
        var k32=NativeLibrary.Load("kernel32.dll");
        var VirtualProtect=(delegate* unmanaged[Stdcall]<void*,nuint,uint,uint*,int>)
            NativeLibrary.GetExport(k32,"VirtualProtect");
        setSR(32000f); setBS(512); activate(32000f,512); setThr();
        // Both inputs, read off the disassembly rather than inferred. At 18008c960 the call to
        // fx_chorus_stage_l is preceded by `lea rdx,[0x181a19070]`, and at 18008c973 the call to
        // fx_chorus_stage_r by `lea rdx,[0x181a190f0]` -- so 0x19070 is the LEFT input and 0x190f0
        // the right. 2c7190c named only the second of the pair.
        long busC=b+(0x181a19070L-0x180000000L);
        long busR=b+(0x181a190f0L-0x180000000L);
        long stageR=b+0x85460;
        var lC=new float[512]; var rC=new float[512];
        void CCc(int c,int v)=>shortIn((uint)(0xB0|(c<<8)|(v<<16)),0);

        void Arm(){
            GsReset(); for(int c=0;c<16;c++) ToneMap0(c,mpC); flush();
            fixed(float* pl=lC,pr=rC) for(int i=0;i<8;i++) process(pl,pr,512);
            CCc(7,127); CCc(10,64); CCc(91,0); CCc(93,sndC); CCc(94,dlyC);
            shortIn((uint)(0xC0|(pgC<<8)),0); flush();
            fixed(float* pl=lC,pr=rC) process(pl,pr,512);
            shortIn((uint)(0x90|(ntC<<8)|(vlC<<16)),0); flush();
            fixed(float* pl=lC,pr=rC) for(int i=0;i<8;i++) process(pl,pr,512);
        }
        void ShowOne(string tag,long at){
            double peak=0.0, sum=0.0;
            for(int i=0;i<32;i++){ double v=*(float*)(at+i*4); peak=System.Math.Max(peak,System.Math.Abs(v)); sum+=v*v; }
            Console.WriteLine($"    {tag,-26} peak={peak:0.#########}  rms={System.Math.Sqrt(sum/32):0.#########}");
        }
        // Every buffer the tail of fx_process_block hands to a stage, in call order. Read off the
        // disassembly at 18008c952..18008c99e rather than from the decompile, which dropped the
        // arguments entirely.
        var busses=new (string,long)[]{
            ("19070 stage_l IN",  0x181a19070L), ("190f0 stage_r IN",  0x181a190f0L),
            ("19170 chorus OUT L", 0x181a19170L), ("191f0 chorus OUT R", 0x181a191f0L),
            ("19270 delay OUT L",  0x181a19270L), ("192f0 delay OUT R",  0x181a192f0L),
            ("19370 reverb arg",  0x181a19370L), ("19470 biquad out",  0x181a19470L),
            ("1a8f0",             0x181a1a8f0L), ("1ac70 out mix",     0x181a1ac70L),
            ("1ad70 reverb in",   0x181a1ad70L),
        };
        void ShowBus(string tag){
            Console.WriteLine($"  {tag}");
            foreach(var (n,a) in busses) ShowOne(n, b+(a-0x180000000L));
        }

        Console.WriteLine($"chorusin prog={pgC} note={ntC} vel={vlC} map={mpC} cc93={sndC} cc94={dlyC}");
        Arm();
        double dryPeak=0.0;
        fixed(float* pl=lC,pr=rC){ process(pl,pr,32);
            for(int i=0;i<32;i++) dryPeak=System.Math.Max(dryPeak,System.Math.Abs(pl[i])); }
        ShowBus("control, module untouched:");

        // One block with the consumer neutered. Restored before anything else runs, so the module
        // is only ever inconsistent for the duration of that single 32-sample call.
        uint old=0; byte save=*(byte*)stageR;
        if(VirtualProtect((void*)stageR,16,0x40,&old)==0){ Console.WriteLine("  VirtualProtect failed"); return; }
        *(byte*)stageR=0xC3;
        fixed(float* pl=lC,pr=rC) process(pl,pr,32);
        *(byte*)stageR=save;
        uint back=0; VirtualProtect((void*)stageR,16,old,&back);
        ShowBus("with fx_chorus_stage_r as ret:");

        // The MAC that 2c7190c named as feeding this bus:
        //   bus[0x181a190f0] = src[0x181a19570] * gain[0x181a6ecf0] + bus
        // Both operands sit in banks that survive a block, so unlike the bus itself they can be
        // read from outside. If the gain does not move with CC#93 then the address is not on the
        // part's chorus send path at all, and no amount of trapping the bus will help.
        long srcC=b+(0x181a19570L-0x180000000L), gainC=b+(0x181a6ecf0L-0x180000000L);
        Console.Write("  MAC gain[181a6ecf0] x8:");
        for(int i=0;i<8;i++) Console.Write($" {*(float*)(gainC+i*4):0.######}");
        Console.WriteLine();
        Console.Write("  MAC src [181a19570] x8:");
        for(int i=0;i<8;i++) Console.Write($" {*(float*)(srcC+i*4):0.######}");
        Console.WriteLine();

        // The chorus's own coefficients, from fx_chorus_stage_l's body:
        //   delay_mem[c] = (prev_wet1 * fb[181a62b10] + lowpass) * write_gain[181a6ef70 + i]
        //   out_L        = wet1 * tap_gain[181a6f0f0 + i]
        // The two gains are per-sample arrays rather than scalars, which is worth knowing before
        // comparing them against a port that holds one number for each.
        Console.Write("  chorus write gain [181a6ef70] x8:");
        for(int i=0;i<8;i++) Console.Write($" {*(float*)(b+(0x181a6ef70L-0x180000000L)+i*4):0.######}");
        Console.WriteLine();
        Console.Write("  chorus tap gain   [181a6f0f0] x8:");
        for(int i=0;i<8;i++) Console.Write($" {*(float*)(b+(0x181a6f0f0L-0x180000000L)+i*4):0.######}");
        Console.WriteLine();
        Console.WriteLine($"  chorus feedback   [181a62b10] = {*(float*)(b+(0x181a62b10L-0x180000000L)):0.########}");
        Console.WriteLine($"  chorus lpf a      [181a62af0] = {*(float*)(b+(0x181a62af0L-0x180000000L)):0.########}"
            + $"   lpf b [181a62af4] = {*(float*)(b+(0x181a62af4L-0x180000000L)):0.########}");

        // The two output filters. fx_biquad_process @180086690 is not a direct-form biquad: it is
        // two cascaded FIRST-ORDER sections sharing one 9-float block, so its two poles are real
        // and it cannot resonate.
        //   y1[n] = c0*x[n]  + c1*x[n-1] + c2*y1[n-1]
        //   y2[n] = c3*y1[n] + c4*y1[n-1] + c5*y2[n-1]
        // c[6..8] are x[n-1], y1[n-1], y2[n-1]. Called twice, on 0x181a62a40 for the left pair
        // (in 0x181a1a8f0, out 0x181a19470) and 0x181a62a70 for the right (0x181a1a970 ->
        // 0x181a194f0), so unlike the chorus and delay stages these two really are a stereo pair.
        foreach(var (nm,at) in new (string,long)[]{("L 181a62a40",0x181a62a40L),("R 181a62a70",0x181a62a70L)}){
            long p0=b+(at-0x180000000L);
            Console.Write($"  biquad {nm}: c0..c5");
            for(int i=0;i<6;i++) Console.Write($" {*(float*)(p0+i*4):0.######}");
            Console.Write("   state x1,y1,y2");
            for(int i=6;i<9;i++) Console.Write($" {*(float*)(p0+i*4):0.######}");
            Console.WriteLine();
        }
        Console.WriteLine($"  dry peak this block = {dryPeak:0.#########}");
        Console.WriteLine($"  prediction: the bus should be 5.79x larger than dry x 0.515625");
        Console.WriteLine($"  dry x 0.515625      = {dryPeak*0.515625:0.#########}");
        Console.WriteLine($"  that x 5.79         = {dryPeak*0.515625*5.79:0.#########}");
        return;
    }
    // lfonodes mode: when does an LFO node inherit from a standing one, and what does it cost?
    //
    //   The parentage rules were read out of the decompile and never measured. partial_alloc_node
    //   @1800029e0 writes node+0xa0 -- zero for a parentless node, otherwise the node it inherits
    //   from -- gated on bit 5 of the waveform byte (tone header 0x0E for LFO1, partial block 0x06
    //   for LFO2). note_on_voice_setup @18005f5c0 then branches on +0xa0: parentless initialises
    //   and takes a prng_lfsr draw, inheriting copies +0x72 phase, +0x70 out, +0x7a held and
    //   +0x78 slewed across and takes none.
    //
    //   Two claims follow and neither has been checked. That a node with the bit set inherits at
    //   all, and that it can only inherit while the note it belongs to is still SOUNDING --
    //   partial_shared_node_free clears the list head when it frees the last node, so the slot
    //   should empty once the previous note ends. This plays a run of notes at a chosen overlap
    //   and reads both: the node table after each note-on, and the generator either side of it,
    //   so the draw count is counted rather than inferred from audio.
    //
    //   args: dll lfonodes <prog> <note> <vel> <map> <count> <gapTicks> <holdTicks> [bank]
    if (args.Length > 1 && args[1] == "lfonodes")
    {
        int pgN=int.Parse(args[2]), ntN=int.Parse(args[3]), vlN=int.Parse(args[4]), mpN=int.Parse(args[5]);
        int cntN=int.Parse(args[6]), gapN=int.Parse(args[7]), holdN=int.Parse(args[8]);
        int bkN=args.Length>9?int.Parse(args[9]):0;
        setSR(32000f); setBS(512); activate(32000f,512); setThr();
        var getLFOn=(delegate* unmanaged[Cdecl]<int,long>)(b+0x5c340);
        ushort* prng0=(ushort*)(b+(0x181a6f630L-0x180000000L));
        ushort* prng1=(ushort*)(b+(0x181a6f634L-0x180000000L));
        var lN=new float[512]; var rN=new float[512];
        void CCn(int c,int v)=>shortIn((uint)(0xB0|(c<<8)|(v<<16)),0);
        GsReset(); if(mpN>=1&&mpN<=4) for(int c=0;c<16;c++) ToneMap0(c,mpN);
        flush(); fixed(float* pl=lN,pr=rN) for(int i=0;i<8;i++) process(pl,pr,512);
        CCn(0,bkN); CCn(32,0); CCn(7,127); CCn(10,64); CCn(91,0); CCn(93,0);
        shortIn((uint)(0xC0|(pgN<<8)),0); flush();
        fixed(float* pl=lN,pr=rN) process(pl,pr,512);

        // Counting draws by stepping the LFSR forward from its own state: the generator is a pure
        // function of itself, so replaying it is exact and needs no instrumentation in the module.
        ushort DrawsBetween(ushort a0,ushort a1,ushort b0,ushort b1){
            ushort s=a0, w=a1;
            for(int i=0;i<4096;i++){
                if(s==b0 && w==b1) return (ushort)i;
                ushort st=(ushort)(s>>1);
                if(((s&0x20)!=0)!=((s&0x8000)!=0)) st=(ushort)(st|0x8000);
                s=st;
                bool one=(w&4)!=0 ? (w&0x2000)==0 : (w&0x200)!=0;
                w=(ushort)((w<<1)|(one?1:0));
            }
            return 0xFFFF;
        }

        Console.WriteLine($"lfonodes prog={pgN} bank={bkN} note={ntN} vel={vlN} map={mpN}: {cntN} notes, gap {gapN} ticks, hold {holdN} ticks");
        Console.WriteLine($"  overlap: {(holdN>gapN ? "YES -- a previous note is still sounding at each note-on" : "no -- each note ends before the next begins")}");
        var live=new System.Collections.Generic.List<int>();
        for(int k=0;k<cntN;k++){
            ushort s0=*prng0, w0=*prng1;
            shortIn((uint)(0x90|((ntN+k)<<8)|(vlN<<16)),0); flush();
            fixed(float* pl=lN,pr=rN) process(pl,pr,320);
            ushort draws=DrawsBetween(s0,w0,*prng0,*prng1);
            Console.WriteLine($"  note {k} (key {ntN+k}): draws={draws}");
            for(int i=0;i<128;i++){
                long nd=getLFOn(i);
                if(nd==0) continue;
                if(*(byte*)(nd+0)==0 && *(byte*)(nd+2)==0 && *(ushort*)(nd+0x72)==0
                   && *(long*)(nd+0xa0)==0) continue;      // never claimed
                long parent=*(long*)(nd+0xa0);
                Console.WriteLine($"      node{i,3} inuse={*(byte*)(nd+0)} type={*(byte*)(nd+2)} parent={(parent==0?"0 (parentless -> drew)":"0x"+(parent-b).ToString("X")+" (INHERITED)")}"
                    + $"  phase={*(ushort*)(nd+0x72)} out={*(short*)(nd+0x70)} held={*(short*)(nd+0x7a)} wave={*(byte*)(nd+0x38)}");
            }
            for(int t=0;t<gapN;t++) fixed(float* pl=lN,pr=rN) process(pl,pr,320);
            if(holdN<=gapN*(k+1)) { shortIn((uint)(0x80|((ntN+k)<<8)),0); flush(); }
        }
        for(int k=0;k<cntN;k++) shortIn((uint)(0x80|((ntN+k)<<8)),0);
        flush();
        return;
    }
    // resetstate mode: does a GS Reset actually return the module to where it started?
    //
    //   Renders one note twice in the *same* DLL instance. The first time from a fresh reset; then
    //   unrelated material is played with the reverb and chorus sends wide open and left to ring;
    //   then a GS Reset, and the same note again. If a reset cleared everything the two renders
    //   would be identical sample for sample. Any difference is state that survived, and the test
    //   does not have to name the carrier to prove one exists.
    //
    //   The decompile predicts a difference: reverb_state_reset @1800043c0 and chorus_state_reset
    //   @180004010 are never called from anywhere, so the tanks and delay lines are never cleared.
    //   args: dll resetstate <prog> <note> <vel> <map> [seconds] [contamSeconds]
    if (args.Length > 1 && args[1] == "resetstate")
    {
        int pgR=int.Parse(args[2]), ntR=int.Parse(args[3]), vlR=int.Parse(args[4]), mpR=int.Parse(args[5]);
        double secR=args.Length>6?double.Parse(args[6],System.Globalization.CultureInfo.InvariantCulture):1.0;
        double conR=args.Length>7?double.Parse(args[7],System.Globalization.CultureInfo.InvariantCulture):2.0;
        setSR(32000f); setBS(512); activate(32000f,512); setThr();
        var lR=new float[512]; var rR=new float[512];
        int blocksR=(int)(secR*32000.0/512.0), conBlocks=(int)(conR*32000.0/512.0);
        void CCr(int c,int v)=>shortIn((uint)(0xB0|(c<<8)|(v<<16)),0);
        void CCch(int ch,int c,int v)=>shortIn((uint)((0xB0|ch)|(c<<8)|(v<<16)),0);

        // The Galois LFSR pair prng_lfsr @18008fbb0 walks. Seeded to 0xefa6/0x9c23 exactly once,
        // in engine_init_tasks_ports @180084c60 (reached only from TG_initialize @1800888a0), so
        // the reset path cannot restore it. Read either side of the reset to show that directly.
        ushort* prngA=(ushort*)(b+(0x181a6f630L-0x180000000L));
        ushort* prngB=(ushort*)(b+(0x181a6f634L-0x180000000L));
        void ShowPrng(string when)=>Console.WriteLine($"  prng {when}: 181a6f630=0x{*prngA:X4} 181a6f634=0x{*prngB:X4}");

        float[] Probe(){
            GsReset(); for(int c=0;c<16;c++) ToneMap0(c,mpR); flush();
            fixed(float* pl=lR,pr=rR) for(int i=0;i<8;i++) process(pl,pr,512);
            CCr(7,127); CCr(10,64); CCr(91,0); CCr(93,0);
            shortIn((uint)(0xC0|(pgR<<8)),0); flush();
            fixed(float* pl=lR,pr=rR) process(pl,pr,512);
            shortIn((uint)(0x90|(ntR<<8)|(vlR<<16)),0); flush();
            var buf=new float[blocksR*512*2];
            for(int i=0;i<blocksR;i++){
                fixed(float* pl=lR,pr=rR) process(pl,pr,512);
                for(int j=0;j<512;j++){ buf[(i*512+j)*2]=lR[j]; buf[(i*512+j)*2+1]=rR[j]; }
            }
            shortIn((uint)(0x80|(ntR<<8)),0); flush();
            return buf;
        }

        ShowPrng("at start (fresh instance)");
        var A=Probe();
        ShowPrng("after render A");
        // The contamination: a different patch on other channels, sends wide open so the shared
        // reverb and chorus networks are driven hard, then held silent long enough that only the
        // tails are still sounding when the reset arrives.
        for(int ch=1;ch<6;ch++){ CCch(ch,7,127); CCch(ch,91,127); CCch(ch,93,127);
            shortIn((uint)((0xC0|ch)|(48<<8)),0); }
        flush();
        for(int ch=1;ch<6;ch++) shortIn((uint)((0x90|ch)|((36+ch*7)<<8)|(120<<16)),0);
        flush();
        for(int i=0;i<conBlocks;i++) fixed(float* pl=lR,pr=rR) process(pl,pr,512);
        for(int ch=1;ch<6;ch++) shortIn((uint)((0x80|ch)|((36+ch*7)<<8)),0);
        flush();
        for(int i=0;i<conBlocks;i++) fixed(float* pl=lR,pr=rR) process(pl,pr,512);
        ShowPrng("after contamination, before reset");
        var B=Probe();
        ShowPrng("after render B");

        double maxd=0.0, sa=0.0, sb=0.0, sd=0.0; int firstDiff=-1;
        for(int i=0;i<A.Length;i++){
            double d=System.Math.Abs((double)A[i]-(double)B[i]);
            if(d>maxd) maxd=d;
            if(firstDiff<0 && A[i]!=B[i]) firstDiff=i;
            sa+=(double)A[i]*A[i]; sb+=(double)B[i]*B[i]; sd+=d*d;
        }
        double rmsA=System.Math.Sqrt(sa/A.Length), rmsB=System.Math.Sqrt(sb/B.Length), rmsD=System.Math.Sqrt(sd/A.Length);
        Console.WriteLine($"resetstate prog={pgR} note={ntR} vel={vlR} map={mpR} {secR}s probe, {conR}s contamination x2");
        Console.WriteLine($"  samples compared: {A.Length}  (interleaved stereo)");
        Console.WriteLine($"  first differing sample: {(firstDiff<0?"none -- IDENTICAL":firstDiff.ToString())}");
        Console.WriteLine($"  max abs diff: {maxd:0.#########}");
        Console.WriteLine($"  rms A={rmsA:0.#########}  rms B={rmsB:0.#########}  rms diff={rmsD:0.#########}");
        if(rmsA>0.0) Console.WriteLine($"  diff is {20.0*System.Math.Log10(rmsD/rmsA):0.00} dB relative to the first render");
        Console.WriteLine(firstDiff<0
            ? "  => a GS Reset returned the module to the same state."
            : "  => state SURVIVED the GS Reset: the same note renders differently.");
        return;
    }
    // sampstate mode: play a melodic note and dump the sampler state (DAT_181a1b570 + v*0x50) of
    //   *every* sounding voice: +0x20 delta-stream ptr, +0x38 scale-stream ptr, +0x28 pos,
    //   +0x2c loopStart, +0x30 loopEnd, +0x48 run flags, +0x49 scale, plus the first 16 bytes the
    //   delta and scale pointers point at.
    //
    //   Both of those matter for pairing a voice against a port's partials. A tone can put more
    //   than one partial on a note -- velocity layers, release layers -- and reporting only the
    //   first sounding voice compares whichever one the module happened to allocate first against
    //   whichever one the port lists first, which is not a comparison at all. Velocity is an
    //   argument for the same reason: it selects the layer, and a fixed 110 cannot reach a case
    //   the caller is asking about at 127.
    //
    //   +0x2c is the field the samplers compare the cursor against (`param_1[2]+0xc`, param_1[2]
    //   being state +0x20) -- it is the loop *start*, matching a port's own loop_start; +0x30 is
    //   the far end.
    //   args: dll sampstate <prog> <note> [map] [vel]
    if (args.Length > 1 && args[1] == "sampstate")
    {
        int pg=args.Length>2?int.Parse(args[2]):12, nt=args.Length>3?int.Parse(args[3]):60, map=args.Length>4?int.Parse(args[4]):4;
        int vlS=args.Length>5?int.Parse(args[5]):110;
        setSR(32000f); setBS(512); activate(32000f,512); setThr();
        long fbS=b+0x1a1b5b8, ss=b+0x1a1b570;
        void CCs(int c,int v)=>shortIn((uint)((0xB0|0)|(c<<8)|(v<<16)),0);
        if(map>=1&&map<=4){ GsReset(); for(int c=0;c<16;c++) ToneMap0(c,map); } else Gm1On();
        CCs(7,127);CCs(10,64);CCs(91,0);CCs(93,0);
        shortIn((uint)(0xC0|(pg<<8)),0);
        var l=new float[512]; var r=new float[512]; flush();
        fixed(float* pl=l,pr=r) for(int i=0;i<8;i++) process(pl,pr,512);
        shortIn((uint)(0x90|(nt<<8)|(vlS<<16)),0); flush();
        // Stepped rather than taken at one instant: a tone's partials do not all become active on
        // the same tick, so a single look can miss the later ones. Keeps stepping until the set
        // stops growing rather than until it is non-empty.
        // A fixed window rather than "stop once it stops growing": a release or second velocity
        // layer can start well after the first partial, and an early-out sized to a few hundred
        // samples reports one voice for a tone that has two. 256 x 64 is ~0.5 s at 32 kHz, and
        // every voice ever seen active in it is kept, not just the set live at the end.
        var soundingS=new System.Collections.Generic.List<int>();
        for(int tries=0; tries<256; tries++){
            fixed(float* pl=l,pr=r) process(pl,pr,64);
            for(int v=0;v<64;v++) if((*(byte*)(fbS+v*0x50)&1)!=0 && !soundingS.Contains(v)) soundingS.Add(v);
        }
        if(soundingS.Count==0){ Console.WriteLine("no active voice"); return; }
        Console.WriteLine($"sampstate prog={pg} note={nt} vel={vlS} map={map}: {soundingS.Count} sounding voice(s)");
        Console.WriteLine($"  moduleBase=0x{b:X}");
        foreach(int v0 in soundingS){
            long st=ss+(long)v0*0x50;
            long dptr=*(long*)(st+0x20), sptr=*(long*)(st+0x38);
            Console.WriteLine($"  voice{v0}: +0x28 pos={*(int*)(st+0x28)}  +0x2c loopStart={*(int*)(st+0x2c)}"
                + $"  +0x30 loopEnd={*(int*)(st+0x30)}  +0x48 flags=0x{*(byte*)(st+0x48):X2}  +0x49 scale={*(byte*)(st+0x49)}");
            Console.WriteLine($"    +0x20 deltaPtr-base=0x{dptr-b:X}  +0x38 scalePtr-base=0x{sptr-b:X}");
            Console.Write("    delta[0:16]:"); for(int i=0;i<16;i++) Console.Write($" {*(sbyte*)(dptr+i)}"); Console.WriteLine();
            Console.Write("    scale[0:16]:"); for(int i=0;i<16;i++) Console.Write($" {*(byte*)(sptr+i)}"); Console.WriteLine();
        }
        return;
    }
    // predtrace mode: capture the engine's ADPCM predictor accumulator (voice sampler state +0x40, int)
    //   and pos (+0x28) sample-by-sample, to compare against our cumsum(delta<<(scale+10)) decode and
    //   find any bit-width/rounding difference. args: dll predtrace <prog> <note> <nsamp> [map]
    if (args.Length > 1 && args[1] == "predtrace")
    {
        int pg=args.Length>2?int.Parse(args[2]):12, nt=args.Length>3?int.Parse(args[3]):60, nsamp=args.Length>4?int.Parse(args[4]):700, map=args.Length>5?int.Parse(args[5]):4;
        // Which of a multi-partial tone's voices to follow. Tracing only the first cannot see a
        // *relative* decode error between two partials, which is the case this was extended for.
        int want=args.Length>6?int.Parse(args[6]):0;
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
            int seen=0;
            for(int v=0;v<64;v++){ if((*(byte*)(fbP+v*0x50)&1)!=0){ if(seen++==want){ v0=v; break; } } } }
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
    if (args.Length > 1 && (args[1] == "drumnote" || args[1] == "panscan"))
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
    //   args: dll tvftrace <prog> <note> <holdSec> <out.csv> [vel] [bank] [bend] [bendRange] [map]
    //         [noteOffPermille] [cc71 resonance] [cc74 cutoff]
    if (args.Length > 1 && args[1] == "tvftrace")
    {
        int SR4=32000; int pg4=int.Parse(args[2]); int nt4=int.Parse(args[3]);
        double hs4=double.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture);
        string csv4=args.Length>5?args[5]:"tvftrace.csv"; int vl4=args.Length>6?int.Parse(args[6]):100;
        int bk4=args.Length>7?int.Parse(args[7]):0;   // CC0 bank MSB
        int bend4=args.Length>8?int.Parse(args[8]):8192;  // 14-bit pitch bend (8192 = center)
        int brange4=args.Length>9?int.Parse(args[9]):-1;  // RPN 00/00 bend range in semitones (-1 = don't set)
        int map4=args.Length>10?int.Parse(args[10]):0;    // tone map 1=SC55..4=SC8820; 0=default GS
        // args 12/13 (after the note-off fraction) drive the resonance byte and the cutoff off
        // neutral: CC#71 is the only way to reach resonance bytes outside a partial's own value, and
        // CC#74 opens the filter, so the pair reaches the corners of the (f, q) space.
        int cc71=args.Length>12?int.Parse(args[12]):-1;   // -1 = don't send
        int cc74=args.Length>13?int.Parse(args[13]):-1;
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
        if(cc71>=0) CC4(71,cc71); if(cc74>=0) CC4(74,cc74); if(cc71>=0||cc74>=0) flush();
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
        int mpl=args.Length>8?int.Parse(args[8]):0;  // tone map 1-4; 0 leaves the GS reset default
        // Optional second channel struck on the SAME tick, to settle what a batch of simultaneous
        // note-ons costs the shared generator. Each LFO node initialised takes one draw at +0x7a,
        // so reading both notes' nodes back tells you the count without inferring it from audio.
        int ch2l=args.Length>9?int.Parse(args[9]):-1;
        // Optional GS part panpot, sent as SysEx rather than CC#10. A literal zero is what selects
        // RND pan, and CC#10 cannot deliver one -- its handler stores `value == 0 ? 1 : value`, so
        // the wheel's zero lands as hard left. -1 leaves the panpot alone.
        int panl=args.Length>10?int.Parse(args[10]):-1;
        // Second note number for the extra voice. With ch2 set to the same channel this makes a
        // CHORD on one part rather than two parts sounding together, which is the case that
        // separates "the setup pass batches a whole part" from "it runs per note".
        int nt2l=args.Length>11?int.Parse(args[11]):-1;
        setSR((float)SRl); setBS(512); activate((float)SRl,512); setThr();
        var getLFO=(delegate* unmanaged[Cdecl]<int,long>)(b+0x5c340);
        var ll=new float[512]; var rl=new float[512];
        GsReset();
        if(mpl>=1&&mpl<=4) for(int c=0;c<16;c++) ToneMap0(c,mpl);
        flush(); fixed(float* pl=ll,pr=rl) for(int i=0;i<8;i++) process(pl,pr,512);
        void CCl(int c,int v)=>shortIn((uint)((0xB0|0)|(c<<8)|(v<<16)),0);
        void CCl2(int ch,int c,int v)=>shortIn((uint)((0xB0|ch)|(c<<8)|(v<<16)),0);
        CCl(0,bkl);CCl(32,0);CCl(7,127);CCl(10,64);CCl(91,0);CCl(93,0);
        shortIn((uint)(0xC0|(pgl<<8)),0);
        if(ch2l>=0){ CCl2(ch2l,0,bkl);CCl2(ch2l,32,0);CCl2(ch2l,7,127);CCl2(ch2l,10,64);
                     CCl2(ch2l,91,0);CCl2(ch2l,93,0);
                     shortIn((uint)((0xC0|ch2l)|(pgl<<8)),0); }
        if(panl>=0){ SendSysEx(Dt1(0x40,(byte)(0x10|BlockNum(0)),0x1C,(byte)panl));
                     if(ch2l>=0) SendSysEx(Dt1(0x40,(byte)(0x10|BlockNum(ch2l)),0x1C,(byte)panl)); }
        flush();
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
        shortIn((uint)(0x90|(ntl<<8)|(vll<<16)),0);
        if(ch2l>=0) shortIn((uint)((0x90|ch2l)|((nt2l>=0?nt2l:ntl)<<8)|(vll<<16)),0);
        flush();
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

    // smfstate mode: play a file for a while, then read every part's identity straight out of the
    // part array. The question it answers is which of a bulk dump's writes actually reached a part
    // -- a dump and a program change can name the same field, and the input queue can drop either.
    //   args: dll smfstate <midi> [ms] [map]
    if (args.Length > 1 && args[1] == "smfstate")
    {
        string midiPath = args.Length > 2 ? args[2] : "song.mid";
        int ms          = args.Length > 3 ? int.Parse(args[3]) : 500;
        int map         = args.Length > 4 ? int.Parse(args[4]) : 4;
        const int SR = 32000, BlockFrames = 320;

        byte[] smf; System.Collections.Generic.List<SmfEvent> events; double songSeconds;
        try { smf = File.ReadAllBytes(midiPath); events = Smf.Parse(smf, SR, out songSeconds); }
        catch (Exception ex) { Console.Error.WriteLine($"cannot read {midiPath}: {ex.Message}"); Environment.Exit(2); return; }

        setSR((float)SR); setBS(512); activate((float)SR, 512); setThr();
        GsReset();
        if (map >= 1 && map <= 4) { for (int c = 0; c < 16; c++) ToneMap0(c, map); }
        flush();
        { var wl = new float[512]; var wr = new float[512];
          fixed (float* pl = wl, pr = wr) for (int i = 0; i < 6; i++) process(pl, pr, 512); }

        int total = ms * SR / 1000;
        var sL = new float[BlockFrames]; var sR = new float[BlockFrames];
        int ei = 0, pos = 0, fed = 0;
        while (pos < total)
        {
            while (ei < events.Count && events[ei].At < pos + BlockFrames)
            {
                var e = events[ei++];
                if (e.Bytes != null) SendSysEx(e.Bytes);
                else shortIn((uint)(e.Status | (e.D1 << 8) | (e.D2 << 16)), 0);
                fed++;
            }
            flush();
            fixed (float* pl = sL, pr = sR) process(pl, pr, BlockFrames);
            pos += BlockFrames;
        }

        long arr = *(long*)(b + 0x1a222a0);
        Console.WriteLine($"{Path.GetFileName(midiPath)}: fed {fed} events over {ms} ms, map {map}");
        Console.WriteLine("blk  prog(3d5) bankM(3d4) rx(3d8) flags(3d9) sel(0x12) vol(3dc)");
        for (int i = 0; i < 16; i++)
        {
            long q = arr + (long)i * 0x488;
            Console.WriteLine($"{i,3}  {*(byte*)(q + 0x3d5),9} {*(byte*)(q + 0x3d4),10} " +
                              $"{*(byte*)(q + 0x3d8),6} 0x{*(byte*)(q + 0x3d9),-8:x2} " +
                              $"0x{*(byte*)(q + 0x12),-6:x2} {*(byte*)(q + 0x3dc),7}");
        }
        return;
    }

    // ccdiff mode: find the memory a Control Change actually moves, with the engine's own churn
    // subtracted out.
    //
    // A naive before/after diff is useless here -- a sounding voice rewrites LFO phases, ring
    // cursors, envelope counters and sample positions every block, so thousands of words differ for
    // reasons that have nothing to do with the controller. The fix is a control capture: settle at
    // the starting value and dump twice with nothing changed in between. Anything that moved on its
    // own is churn, and is excluded. What survives is what the CC did.
    //
    // Settling matters as much as the control does. A send coefficient is slewed -- MatrixRamp
    // walks it sixteen times a block and takes about 25 ms to arrive -- so a dump taken a few
    // milliseconds after the write reads a value in transit, or nothing at all.
    //
    //   args: dll ccdiff <cc> <before> <after> [va] [bytes] [prog] [note] [settleBlocks] [map]
    if (args.Length > 1 && args[1] == "ccdiff")
    {
        // "sx:a1,a2,a3" addresses a GS parameter instead of a Control Change, which is how the
        // system sends (40 01 3F chorus-to-reverb, 40 01 40 chorus-to-delay, 40 01 5A
        // delay-to-reverb) are reached -- none of them has a controller.
        string sxAddr = args[2].StartsWith("sx:") ? args[2].Substring(3) : null;
        int cc      = sxAddr != null ? 0 : int.Parse(args[2]);
        int before  = int.Parse(args[3]);
        int after   = int.Parse(args[4]);
        long va     = args.Length > 5 ? Convert.ToInt64(args[5], 16) : 0x181a00000L;
        int count   = args.Length > 6
                        ? (args[6].StartsWith("0x") ? Convert.ToInt32(args[6].Substring(2), 16)
                                                    : int.Parse(args[6]))
                        : 0x20000;
        int pg      = args.Length > 7 ? int.Parse(args[7]) : 38;
        int nt      = args.Length > 8 ? int.Parse(args[8]) : 60;
        int settle  = args.Length > 9 ? int.Parse(args[9]) : 40;
        int map     = args.Length > 10 ? int.Parse(args[10]) : 1;

        setSR(32000f); setBS(512); activate(32000f, 512); setThr();
        byte[] sxBytes = null;
        if (sxAddr != null)
        {
            var pp = sxAddr.Split(',');
            sxBytes = new byte[] { Convert.ToByte(pp[0], 16), Convert.ToByte(pp[1], 16),
                                   Convert.ToByte(pp[2], 16) };
        }
        void Set(int v)
        {
            if (sxBytes != null) SendSysEx(Dt1(sxBytes[0], sxBytes[1], sxBytes[2], (byte)v));
            else shortIn((uint)((0xB0 | 0) | (cc << 8) | (v << 16)), 0);
        }
        void CCd2(int c, int v) => shortIn((uint)((0xB0 | 0) | (c << 8) | (v << 16)), 0);
        GsReset();
        if (map >= 1 && map <= 4) { for (int c = 0; c < 16; c++) ToneMap0(c, map); }
        CCd2(7, 110); CCd2(10, 94); CCd2(91, 0);
        // The chorus runs whenever a SysEx address is being swept: the system sends are all
        // downstream of it, and a coefficient on a path carrying no signal may never be written.
        CCd2(93, sxBytes != null ? 100 : 0);
        shortIn((uint)(0xC0 | (pg << 8)), 0);
        Set(before);
        flush();

        var lb = new float[512]; var rb = new float[512];
        void Run(int blocks) { fixed (float* pl = lb, pr = rb) for (int i = 0; i < blocks; i++) process(pl, pr, 512); }

        // The note sounds throughout. A send coefficient that nothing is being sent through may not
        // be written at all, so measuring it with the part silent can read a stale value.
        shortIn((uint)(0x90 | (nt << 8) | (100 << 16)), 0); flush();
        Run(settle);

        long addr = b + (va - 0x180000000L);
        var A = new byte[count]; var B = new byte[count]; var C = new byte[count];
        System.Runtime.InteropServices.Marshal.Copy((nint)addr, A, 0, count);

        // The control leg: same amount of time, and **the same MIDI traffic**. Re-sending the value
        // it already has costs nothing musically and keeps the input ring, its cursors and its
        // counters advancing exactly as the measured leg will. Without it those pointers move only
        // on the measured side and are reported as hits -- which they are, just not interesting
        // ones.
        Set(before); flush();
        Run(settle);
        System.Runtime.InteropServices.Marshal.Copy((nint)addr, B, 0, count);

        // The measured leg.
        Set(after); flush();
        Run(settle);
        System.Runtime.InteropServices.Marshal.Copy((nint)addr, C, 0, count);

        Console.WriteLine($"ccdiff {(sxAddr != null ? "sysex " + sxAddr : "cc" + cc)} "
                          + $"{before} -> {after}, prog={pg} note={nt} map={map}");
        Console.WriteLine($"  region VA 0x{va:X} .. 0x{va + count:X}, {settle} blocks of 512 between captures");

        int churn = 0, moved = 0;
        var hits = new System.Collections.Generic.List<int>();
        for (int i = 0; i < count; i++)
        {
            bool selfMoved = A[i] != B[i];
            if (selfMoved) { churn++; continue; }
            if (A[i] != C[i]) { moved++; hits.Add(i); }
        }
        Console.WriteLine($"  {churn} bytes move on their own and are excluded; {moved} move only with the CC");

        // Grouped into runs, and read back as the widths a coefficient is plausibly stored in.
        int r = 0;
        while (r < hits.Count)
        {
            int start = hits[r], end = start;
            while (r + 1 < hits.Count && hits[r + 1] <= end + 3) { end = hits[++r]; }
            ++r;
            int len = end - start + 1;
            int at = start & ~3;
            Console.Write($"  VA 0x{va + start:X} +{len}  before");
            for (int k = at; k < Math.Min(at + 8, count); k++) Console.Write($" {A[k]:x2}");
            Console.Write("  after");
            for (int k = at; k < Math.Min(at + 8, count); k++) Console.Write($" {C[k]:x2}");
            if (at + 4 <= count)
            {
                short a16 = BitConverter.ToInt16(A, at), c16 = BitConverter.ToInt16(C, at);
                float af = BitConverter.ToSingle(A, at), cf = BitConverter.ToSingle(C, at);
                Console.Write($"   | i16 {a16} -> {c16}");
                if (!float.IsNaN(af) && !float.IsNaN(cf) && Math.Abs(af) < 1e6 && Math.Abs(cf) < 1e6)
                    Console.Write($"   f32 {af:G6} -> {cf:G6}");
            }
            Console.WriteLine();
        }
        return;
    }

    // buscap mode: read memory between 32-sample blocks, while a note sounds.
    //
    // **It does not work for the send buses it was written for, and that is the finding.** A bus is
    // filled by the send mix and consumed by its effect stage inside one block, and it is then
    // *cleared*: stepping the engine a block at a time and reading `0x181a190f0` immediately after
    // returns 2.0003e-05 every block with a note sounding, which is the module's not-quite-zero
    // idiom rather than audio. Its source at `0x181a195f0` reads 9.7647e-06, the same. So a bus
    // cannot be observed from outside `process` at all -- reading one means interrupting the module
    // mid-call, which is patching rather than driving.
    //
    // What it is still good for is anything that persists across a block: rings, cursors, and
    // coefficients caught part-way through a ramp.
    //
    // 32 is the engine's own block. `process(pl, pr, 32)` advances exactly one, which is what makes
    // "immediately after" mean anything; asking for 512 runs sixteen of them and shows only the
    // last. Two addresses are read each step so a source and its destination can be compared in the
    // same pass -- `0x181a195f0` feeds the mix and `0x181a190f0` is the chorus's input.
    //
    //   args: dll buscap <vaA> <vaB> <floats> <blocks> [prog] [note] [cc] [ccval] [map]
    if (args.Length > 1 && args[1] == "buscap")
    {
        long vaA   = Convert.ToInt64(args[2], 16);
        long vaB   = Convert.ToInt64(args[3], 16);
        int floats = args.Length > 4 ? int.Parse(args[4]) : 32;
        int blocks = args.Length > 5 ? int.Parse(args[5]) : 24;
        int pg     = args.Length > 6 ? int.Parse(args[6]) : 38;
        int nt     = args.Length > 7 ? int.Parse(args[7]) : 60;
        int cc     = args.Length > 8 ? int.Parse(args[8]) : 93;
        int ccval  = args.Length > 9 ? int.Parse(args[9]) : 127;
        int map    = args.Length > 10 ? int.Parse(args[10]) : 1;

        setSR(32000f); setBS(512); activate(32000f, 512); setThr();
        void CCb(int c, int v) => shortIn((uint)((0xB0 | 0) | (c << 8) | (v << 16)), 0);
        GsReset();
        if (map >= 1 && map <= 4) { for (int c = 0; c < 16; c++) ToneMap0(c, map); }
        CCb(7, 110); CCb(10, 94); CCb(91, 0); CCb(93, 0); CCb(cc, ccval);
        shortIn((uint)(0xC0 | (pg << 8)), 0);
        flush();

        var lb = new float[512]; var rb = new float[512];
        // Settle before the note, so the send coefficient has finished its ramp.
        fixed (float* pl = lb, pr = rb) for (int i = 0; i < 40; i++) process(pl, pr, 512);

        shortIn((uint)(0x90 | (nt << 8) | (100 << 16)), 0); flush();

        // Establish the note with full blocks before stepping. A 32-sample `process` does not
        // appear to walk the MIDI ring -- stepping straight from the note-on leaves the part
        // silent however long you step for, which reads as an empty bus rather than as a note that
        // never started. `fxgain` does the same thing for the same reason.
        fixed (float* pl = lb, pr = rb) for (int i = 0; i < 8; i++) process(pl, pr, 512);

        float* A = (float*)(b + (vaA - 0x180000000L));
        float* B = (float*)(b + (vaB - 0x180000000L));
        Console.WriteLine($"buscap cc{cc}={ccval} prog={pg} note={nt} map={map}, {floats} floats");
        Console.WriteLine($"  A = 0x{vaA:X}   B = 0x{vaB:X}");
        Console.WriteLine("  block   A rms      A peak     B rms      B peak");
        var step = new float[32]; var stepR = new float[32];
        for (int blk = 0; blk < blocks; blk++)
        {
            fixed (float* pl = step, pr = stepR) process(pl, pr, 32);
            double ra = 0, rb2 = 0, pa = 0, pb = 0;
            for (int i = 0; i < floats; i++)
            {
                double a = A[i], bb = B[i];
                ra += a * a; rb2 += bb * bb;
                if (Math.Abs(a) > pa) pa = Math.Abs(a);
                if (Math.Abs(bb) > pb) pb = Math.Abs(bb);
            }
            Console.WriteLine($"  {blk,5}   {Math.Sqrt(ra / floats),-10:G5} {pa,-10:G5} "
                              + $"{Math.Sqrt(rb2 / floats),-10:G5} {pb,-10:G5}");
        }

        // The values themselves, from the last block. A coefficient bank is constant across the
        // buffer and reads the same in every slot; a signal buffer does not.
        Console.WriteLine($"\n  A = 0x{vaA:X}, final block:");
        for (int i = 0; i < floats; i += 8)
        {
            Console.Write($"    +{i * 4:X3} ");
            for (int k = i; k < Math.Min(i + 8, floats); k++) Console.Write($" {A[k],-12:G6}");
            Console.WriteLine();
        }
        Console.WriteLine($"  B = 0x{vaB:X}, final block:");
        for (int i = 0; i < floats; i += 8)
        {
            Console.Write($"    +{i * 4:X3} ");
            for (int k = i; k < Math.Min(i + 8, floats); k++) Console.Write($" {B[k],-12:G6}");
            Console.WriteLine();
        }
        return;
    }

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
        // Warm-up blocks before the song. Six is the fixture default; the argument exists to move
        // the free-running effect LFOs to a different phase at song start, which is how you ask
        // whether the phase matters at all without touching the engine under test.
        int warmBlocks = args.Length > 7 ? int.Parse(args[7]) : 6;
        { var wl = new float[512]; var wr = new float[512];
          fixed (float* pl = wl, pr = wr) for (int i = 0; i < warmBlocks; i++) process(pl, pr, 512); }

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
                // Drop XG messages whose address would index past the end of an array the core
                // does not bounds-check. SCCore.dll does implement XG: `F0 43 10 4C 00 00 7E 00 F7`
                // (XG System On) arms it, and the parameter blocks are then honoured.
                //
                // Two such indexes are known, both read straight out of the address:
                //
                //  * Multi Part, `08 <part> <param>`. The part goes through a 64-entry remap table
                //    while the part array holds 32, so 0x00..0x1f are accepted and 0x20 upward kill
                //    the process with 0xC0000005. Thirty-two is exactly what this synth has, so the
                //    range is right and the message is not -- part 0x20 is a thirty-third part that
                //    does not exist, and th07_19_user_gm.mid genuinely asks for one.
                //
                //  * Drum Setup, `3n <note> <param>`. The setup index is `addrH & 0x0F`, which
                //    yields 0..15, but only **eight** setup buffers are allocated: the count global
                //    is 8 and the block is `malloc(0x2860)` at a stride of 0x50C, which is 8 exactly.
                //    So `38`..`3F` index buffers 8..15 and run off the end.
                //
                //    Measured with `sysexreplay`, one index per process, after XG System On:
                //    `30`..`37` (0-7) all survive, as they should. Of the rest, `3B` and `3F` die
                //    with 0xC0000005 while `38`, `39` and `3D` *survive* -- they land in mapped
                //    heap and corrupt whatever is there instead of faulting. The erratic half is
                //    the reason to filter the whole range rather than only what crashes today:
                //    a silent write into the neighbouring allocation is worse than the crash, and
                //    which indices do which is an accident of the heap. XG itself only defines
                //    setups 1-4 (`30`..`33`), so nothing well-formed is lost.
                //
                // The model byte is deliberately **not** checked. Once its XG parser is armed the
                // core stops checking it too, so `F0 43 1n <anything> 08 20 ...` reaches the same
                // unguarded index that `4C` does; a filter that insisted on `4C` would let it past.
                //
                // Still narrower than SCWrap's hook, which drops Yamaha SysEx wholesale. Keeping
                // the rest preserves whatever XG behaviour the core really has.
                bool xgUncheckedIndex = false;
                if (e.Bytes != null && e.Bytes.Length >= 6 && e.Bytes[1] == 0x43
                    && (e.Bytes[2] & 0xF0) == 0x10)
                {
                    int addrH = e.Bytes[4];
                    int addrM = e.Bytes[5];
                    if (addrH == 0x08 && addrM >= 0x20) xgUncheckedIndex = true;
                    if (addrH >= 0x30 && addrH <= 0x3F && (addrH & 0x0F) >= 8) xgUncheckedIndex = true;
                }
                if (e.Bytes != null && !xgUncheckedIndex)
                {
                    fixed (byte* mp = e.Bytes) longIn(mp, 0);
                }
                else shortIn((uint)(e.Status | (e.D1 << 8) | (e.D2 << 16)), 0);
            }
            flush();
            int nf = Math.Min(blk, total - pos);
            fixed (float* pl = sL, pr = sR) process(pl, pr, (uint)nf);
            // TS_VOICE_COUNT: how many voices the module has sounding, per rendered chunk. A
            // detuned unison that loses one of its two partials is about 3 dB down, constant, with
            // its envelope shape intact -- which is the signature NativeTS #8's channel 15 shows,
            // and nothing about the expression path itself accounts for.
            if (Environment.GetEnvironmentVariable("TS_VOICE_COUNT") != null) {
                int live = 0;
                for (int v = 0; v < 64; v++) if ((*(byte*)(b + 0x1a1b5b8 + v * 0x50) & 1) != 0) live++;
                Console.Error.WriteLine($"voices: {pos},{live}");
            }
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
            // Round, do not truncate. A cast to int truncates toward zero, which biases every
            // event early by up to a sample and -- because the dispatch loop below advances an
            // event to the start of whichever 320-sample block holds it -- occasionally by a whole
            // block, when truncation drops a position back across a boundary that rounding would
            // have kept it above. `Math.Round`'s default is half-to-even, matching the C++ side's
            // `std::nearbyint` under FE_TONEAREST, so both harnesses now land on the same integer
            // for the same input instead of agreeing only most of the time.
            copy.At = (int)Math.Round(when * sampleRate, MidpointRounding.ToEven);
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
