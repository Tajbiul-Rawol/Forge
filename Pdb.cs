using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ForgeC;

// Windows debug info (-g): a PDB next to the .exe, plus what the .exe needs to point at
// it and to be unwound by a debugger.
//
// The .exe gets, after the code in .text: UNWIND_INFO + RUNTIME_FUNCTION entries for
// every Forge function (data directory 3, so debuggers can walk the stack), and a debug
// directory with a CodeView "RSDS" record (data directory 6) naming the PDB and its GUID.
//
// The PDB is an MSF container of numbered streams:
//   1 PDB info (GUID, age, "/names")   2 TPI (types)   3 DBI (modules, sections, files)
//   4 IPI (empty)   5 /names (file names)   6 TPI hash   7 the module's symbols and lines
//   8 globals hash   9 publics hash   10 symbol records   11 section headers
// One module holds every Forge function: S_GPROC32 with S_REGREL32 locals (rbp-relative)
// and C13 line tables. The layouts follow the format notes of LLVM's PDB writer, which
// Microsoft's tools read. Builds are reproducible: the GUID is a hash of the code.
sealed partial class CodeGen {
    public string? PdbPath;              // set by the driver; the RSDS record names it
    public byte[]? PdbBytes;             // the PDB, after LowerToFile
    byte[] _pdbGuid = new byte[16];
    uint _pdbSig;

    // ---- the .exe side: unwind info and the debug directory, appended to the code
    public sealed record WinDebugLayout(int PdataOff, int PdataLen, int DebugDirOff);
    WinDebugLayout AppendWinDebug(List<byte> code, int textRva, int textFileOff) {
        var hash = SHA256.HashData(code.ToArray());
        Array.Copy(hash, _pdbGuid, 16);
        _pdbSig = BitConverter.ToUInt32(hash, 16);

        void Align4() { while (code.Count % 4 != 0) code.Add(0); }
        void U8(int v) => code.Add((byte)v);
        void U16(int v) { U8(v); U8(v >> 8); }
        void U32(long v) { for (int i = 0; i < 4; i++) U8((int)(v >> (8 * i))); }
        Align4();
        // UNWIND_INFO per function. Framed: push rbp (ends at 1), mov rbp, rsp (4),
        // sub rsp, imm32 (11) -- rbp is the frame register, so pushes in the body are fine.
        // Frameless: one push per saved register.
        var unwind = new List<int>();
        foreach (var f in DbgFuncs) {
            unwind.Add(code.Count);
            var codes = new List<(int Off, int Op, int Info, int? Extra)>();   // in prolog order
            int prolog;
            if (!f.Frameless) {
                codes.Add((1, 0, 5, null));                                   // UWOP_PUSH_NONVOL rbp
                codes.Add((4, 3, 0, null));                                   // UWOP_SET_FPREG
                prolog = 4;
                if (f.FrameSize > 0) {
                    prolog = 11;
                    if (f.FrameSize <= 128) codes.Add((11, 2, f.FrameSize / 8 - 1, null));        // UWOP_ALLOC_SMALL
                    else codes.Add((11, 1, 0, f.FrameSize / 8));                                // UWOP_ALLOC_LARGE
                }
            } else {
                int pos = 0;
                foreach (int r in f.SavedRegs) { pos += r >= 8 ? 2 : 1; codes.Add((pos, 0, r, null)); }
                prolog = pos;
            }
            int slots = codes.Sum(c => c.Extra != null ? 2 : 1);
            U8(1); U8(prolog); U8(slots); U8(f.Frameless ? 0 : 5);             // version 1, frame register rbp, offset 0
            for (int k = codes.Count - 1; k >= 0; k--) {                       // most recent first
                var c = codes[k];
                U8(c.Off); U8(c.Op | (c.Info << 4));
                if (c.Extra != null) U16(c.Extra.Value);
            }
            if (slots % 2 != 0) U16(0);
        }
        Align4();
        int pdataOff = code.Count;
        for (int k = 0; k < DbgFuncs.Count; k++) {
            var f = DbgFuncs[k];
            U32(textRva + f.Start); U32(textRva + f.End); U32(textRva + unwind[k]);
        }
        int pdataLen = code.Count - pdataOff;
        // IMAGE_DEBUG_DIRECTORY (type 2 = CodeView), then the RSDS record it points to
        int dirOff = code.Count;
        var path = Encoding.UTF8.GetBytes(PdbPath ?? "program.pdb");
        int rsdsOff = dirOff + 28, rsdsLen = 4 + 16 + 4 + path.Length + 1;
        U32(0); U32(0); U16(0); U16(0); U32(2); U32(rsdsLen); U32(textRva + rsdsOff); U32(textFileOff + rsdsOff);
        code.AddRange(Encoding.ASCII.GetBytes("RSDS")); code.AddRange(_pdbGuid); U32(1); code.AddRange(path); U8(0);
        return new WinDebugLayout(pdataOff, pdataLen, dirOff);
    }

    // ---- the PDB itself. sectionHeaders: the .exe's IMAGE_SECTION_HEADERs (40 bytes each).
    byte[] BuildPdb(byte[] sectionHeaders, int textLen) {
        var files = Sources.Files.Select(f => Path.GetFullPath(f)).ToList();
        var names = new PdbStrings();
        var fileNameOff = files.Select(f => names.Add(f)).ToList();

        // ---- types (TPI)
        var tpi = new TypeTable();
        var fwd = new Dictionary<string, int>();
        var ptrTi = new Dictionary<string, int>();
        var types = DbgTypeClosure();
        foreach (var t in types)
            if (DescribeType(t) is { Kind: DbgKind.Struct } d) {
                var r = new ByteWriter();
                r.U16(0); r.U16(0x80); r.U32(0); r.U32(0); r.U32(0); TypeTable.Numeric(r, 0); r.Str(d.Name);   // forward reference
                fwd[d.Name] = tpi.Add(0x1505, r, null);
            }
        int Ti(string t) {
            var d = DescribeType(t);
            switch (d.Kind) {
                case DbgKind.Struct: return fwd[d.Name];
                case DbgKind.Base:
                    return d.Encoding switch {
                        2 => 0x0033, 4 => 0x0041, 6 => 0x0070,                       // T_BOOL64, T_REAL64, T_RCHAR
                        _ => (d.Size, d.Encoding == 5) switch {
                            (1, true) => 0x0068, (1, false) => 0x0069, (2, true) => 0x0072, (2, false) => 0x0073,
                            (4, true) => 0x0074, (4, false) => 0x0075, (_, true) => 0x0076, _ => 0x0077,
                        },
                    };
                default:                                                             // pointer
                    if (d.Target == null) return 0x0603;                             // T_64PVOID
                    int to = Ti(d.Target);
                    if (to < 0x1000 && (to & 0x0f00) == 0) return 0x0600 | to;       // 64-bit pointer to a primitive
                    if (ptrTi.TryGetValue(t, out int have)) return have;
                    var r = new ByteWriter(); r.U32(to); r.U32(0x0c | (8 << 13));    // LF_POINTER: 64-bit, size 8
                    return ptrTi[t] = tpi.Add(0x1002, r, null);
            }
        }
        foreach (var t in types) Ti(t);
        foreach (var t in types)
            if (DescribeType(t) is { Kind: DbgKind.Struct } d) {
                var fl = new ByteWriter();
                int n = 0;
                foreach (var (fn, off, ft) in StructFields(d.Name)) {
                    fl.U16(0x150d); fl.U16(3); fl.U32(Ti(ft)); TypeTable.Numeric(fl, off); fl.Str(fn);   // LF_MEMBER, public
                    while (fl.Count % 4 != 0) fl.U8(0xf0 | (4 - fl.Count % 4));                        // LF_PAD
                    n++;
                }
                int list = tpi.Add(0x1203, fl, null);
                var r = new ByteWriter();
                r.U16(n); r.U16(0); r.U32(list); r.U32(0); r.U32(0); TypeTable.Numeric(r, d.Size); r.Str(d.Name);
                tpi.Add(0x1505, r, d.Name);
            }
        var procTi = new Dictionary<DbgFunc, int>();
        foreach (var f in DbgFuncs) {
            var pars = f.Vars.Where(v => v.IsParam).ToList();
            var al = new ByteWriter(); al.U32(pars.Count); foreach (var p in pars) al.U32(Ti(p.Type));
            int args = tpi.Add(0x1201, al, null);                                   // LF_ARGLIST
            var pr = new ByteWriter();
            pr.U32(f.Ret == "void" ? 0x0003 : Ti(f.Ret)); pr.U8(0); pr.U8(0); pr.U16(pars.Count); pr.U32(args);
            procTi[f] = tpi.Add(0x1008, pr, null);                                  // LF_PROCEDURE
        }

        // ---- the module: symbols, then C13 line tables
        var mod = new ByteWriter();
        mod.U32(4);                                                                 // CV_SIGNATURE_C13
        void Sym(int kind, ByteWriter body) {
            int len = 2 + body.Count; int pad = (4 - (2 + len) % 4) % 4;
            mod.U16(len + pad); mod.U16(kind); mod.Bytes(body.ToArray()); for (int i = 0; i < pad; i++) mod.U8(0);
        }
        var b = new ByteWriter(); b.U32(0); b.Str(files.Count > 0 ? files[0] : "program.fg"); Sym(0x1101, b);   // S_OBJNAME
        b = new ByteWriter(); b.U32(0); b.U16(0xd0); for (int i = 0; i < 8; i++) b.U16(i == 0 || i == 4 ? 1 : 0); b.Str("forgec");
        Sym(0x113c, b);                                                             // S_COMPILE3 (C, x64)
        var procOff = new Dictionary<DbgFunc, int>();
        foreach (var f in DbgFuncs) {
            procOff[f] = mod.Count;
            b = new ByteWriter();
            b.U32(0); int endAt = mod.Count + 4 + 4; b.U32(0); b.U32(0);            // parent, end (patched), next
            b.U32(f.End - f.Start); b.U32(Math.Max(0, f.BodyStart - f.Start)); b.U32(Math.Max(0, f.End - f.Start - 1));
            b.U32(procTi[f]); b.U32(f.Start); b.U16(1); b.U8(0); b.Str(f.Name);
            Sym(0x1110, b);                                                         // S_GPROC32
            b = new ByteWriter();
            int enc = f.Frameless ? 1 : 2;                                          // locals / params relative to rsp or rbp
            b.U32(f.FrameSize); b.U32(0); b.U32(0); b.U32(8 * f.SavedRegs.Count); b.U32(0); b.U16(0); b.U32((enc << 14) | (enc << 16));
            Sym(0x1012, b);                                                         // S_FRAMEPROC
            foreach (var v in f.Vars) {
                b = new ByteWriter();
                if (v.InReg) { b.U32(Ti(v.Type)); b.U16((v.IsParam ? 1 : 0) | 0x100); b.Str(v.Name); Sym(0x113e, b); }   // S_LOCAL, optimized out
                else { b.U32(v.Off); b.U32(Ti(v.Type)); b.U16(334); b.Str(v.Name); Sym(0x1111, b); }                     // S_REGREL32 [rbp+off]
            }
            mod.PatchU32(endAt, mod.Count);
            Sym(0x0006, new ByteWriter());                                          // S_END
        }
        int symBytes = mod.Count;
        // DEBUG_S_FILECHKSMS: one 8-byte entry per file (no checksum)
        var chk = new ByteWriter();
        foreach (int off in fileNameOff) { chk.U32(off); chk.U8(0); chk.U8(0); chk.U16(0); }
        void Subsection(int kind, ByteWriter body) { mod.U32(kind); mod.U32(body.Count); mod.Bytes(body.ToArray()); while (mod.Count % 4 != 0) mod.U8(0); }
        Subsection(0xf4, chk);
        foreach (var f in DbgFuncs) {
            var rows = DbgRows.Where(r => r.Pos >= f.Start && r.Pos < f.End && r.Line > 0).ToList();
            if (rows.Count == 0) continue;
            var ln = new ByteWriter();
            ln.U32(f.Start); ln.U16(1); ln.U16(0); ln.U32(f.End - f.Start);         // DEBUG_S_LINES header
            int k = 0;
            while (k < rows.Count) {                                                // one block per run of rows in one file
                int file = Sources.FileOf(rows[k].Line), e = k;
                while (e < rows.Count && Sources.FileOf(rows[e].Line) == file) e++;
                ln.U32(file * 8); ln.U32(e - k); ln.U32(12 + 8 * (e - k));
                for (int i = k; i < e; i++) { ln.U32(rows[i].Pos - f.Start); ln.U32((uint)Sources.LineOf(rows[i].Line) | 0x80000000u); }
                k = e;
            }
            Subsection(0xf2, ln);
        }
        int c13Bytes = mod.Count - symBytes;
        mod.U32(0);                                                                 // global refs: none

        // ---- symbol records: publics for every routine, procrefs for Forge functions
        var symrec = new ByteWriter();
        var pubs = new List<(string Name, int Off, int Addr)>();
        var globals = new List<(string Name, int Off)>();
        void Rec(int kind, ByteWriter body) {
            int len = 2 + body.Count; int pad = (4 - (2 + len) % 4) % 4;
            symrec.U16(len + pad); symrec.U16(kind); symrec.Bytes(body.ToArray()); for (int i = 0; i < pad; i++) symrec.U8(0);
        }
        var user = DbgFuncs.ToDictionary(f => f.Name);
        foreach (var (name, pos) in _a.Labels.OrderBy(kv => kv.Value)) {
            if (name.StartsWith(".") || name.Contains('$') && !user.ContainsKey(name)) continue;
            if (pos >= textLen) continue;
            pubs.Add((name, symrec.Count, pos));
            b = new ByteWriter(); b.U32(user.ContainsKey(name) ? 2 : 0); b.U32(pos); b.U16(1); b.Str(name);
            Rec(0x110e, b);                                                         // S_PUB32 (function)
        }
        foreach (var f in DbgFuncs) {
            globals.Add((f.Name, symrec.Count));
            b = new ByteWriter(); b.U32(0); b.U32(procOff[f]); b.U16(1); b.Str(f.Name);
            Rec(0x1125, b);                                                         // S_PROCREF -> module 1
        }
        byte[] gsi = GsiHash(globals, symrec);
        var psi = new ByteWriter();
        byte[] pubHash = GsiHash(pubs.Select(p => (p.Name, p.Off)).ToList(), symrec);
        psi.U32(pubHash.Length); psi.U32(4 * pubs.Count); psi.U32(0); psi.U32(0); psi.U16(0); psi.U16(0); psi.U32(0); psi.U32(0);
        psi.Bytes(pubHash);
        foreach (var p in pubs.OrderBy(p => p.Addr)) psi.U32(p.Off);               // address map

        // ---- DBI
        var dbi = new ByteWriter();
        var modi = new ByteWriter();
        modi.U32(0);
        void SectionContrib(ByteWriter w) { w.U16(1); w.U16(0); w.U32(0); w.U32(textLen); w.U32(0x60000020); w.U16(0); w.U16(0); w.U32(0); w.U32(0); }
        SectionContrib(modi);
        modi.U16(0); modi.U16(7); modi.U32(symBytes); modi.U32(0); modi.U32(c13Bytes); modi.U16(files.Count); modi.U16(0);
        modi.U32(0); modi.U32(0); modi.U32(0);
        string modName = files.Count > 0 ? files[0] : "program.fg";
        modi.Str(modName); modi.Str(modName); modi.Align(4);
        var sc = new ByteWriter(); sc.U32(0xeffe0000 + 19970605); SectionContrib(sc);
        int nsec = sectionHeaders.Length / 40;
        var sm = new ByteWriter(); sm.U16(nsec + 1); sm.U16(nsec + 1);
        for (int i = 0; i < nsec; i++) {
            uint ch = BitConverter.ToUInt32(sectionHeaders, i * 40 + 36);
            int flags = 0x108 | ((ch & 0x40000000) != 0 ? 1 : 0) | ((ch & 0x80000000) != 0 ? 2 : 0) | ((ch & 0x20000000) != 0 ? 4 : 0);
            sm.U16(flags); sm.U16(0); sm.U16(0); sm.U16(i + 1); sm.U16(0xffff); sm.U16(0xffff); sm.U32(0); sm.U32(BitConverter.ToUInt32(sectionHeaders, i * 40 + 8));
        }
        sm.U16(0x208); sm.U16(0); sm.U16(0); sm.U16(nsec + 1); sm.U16(0xffff); sm.U16(0xffff); sm.U32(0); sm.U32(0xffffffff);
        var fi = new ByteWriter();
        fi.U16(1); fi.U16(files.Count); fi.U16(0); fi.U16(files.Count);
        var fnames = new ByteWriter();
        foreach (var f in files) { fi.U32(fnames.Count); fnames.Str(f); }
        fi.Bytes(fnames.ToArray()); fi.Align(4);
        var ec = new PdbStrings().ToArray();
        var dbg = new ByteWriter(); for (int i = 0; i < 11; i++) dbg.U16(i == 5 ? 11 : 0xffff);   // only the section headers stream
        dbi.U32(0xffffffff); dbi.U32(19990903); dbi.U32(1);
        dbi.U16(8); dbi.U16(0x8e0b); dbi.U16(9); dbi.U16(0); dbi.U16(10); dbi.U16(0);
        dbi.U32(modi.Count); dbi.U32(sc.Count); dbi.U32(sm.Count); dbi.U32(fi.Count); dbi.U32(0); dbi.U32(0);
        dbi.U32(dbg.Count); dbi.U32(ec.Length); dbi.U16(0); dbi.U16(0x8664); dbi.U32(0);
        dbi.Bytes(modi.ToArray()); dbi.Bytes(sc.ToArray()); dbi.Bytes(sm.ToArray()); dbi.Bytes(fi.ToArray()); dbi.Bytes(ec); dbi.Bytes(dbg.ToArray());

        // ---- PDB info stream
        var info = new ByteWriter();
        info.U32(20000404); info.U32(_pdbSig); info.U32(1); info.Bytes(_pdbGuid);
        info.U32(7); info.Str("/names");                                           // named stream map: "/names" -> 5
        info.U32(1); info.U32(1); info.U32(1); info.U32(1); info.U32(0); info.U32(0); info.U32(5);
        info.U32(0);                                                                // niMac
        info.U32(20140508);                                                         // feature: VC140 (has an IPI stream)

        var streams = new List<byte[]> {
            Array.Empty<byte>(), info.ToArray(), tpi.Stream(6), dbi.ToArray(), new TypeTable().Stream(0xffff),
            names.ToArray(), tpi.HashStream(), mod.ToArray(), gsi, psi.ToArray(), symrec.ToArray(), sectionHeaders,
        };
        return Msf(streams);
    }

    // A GSI hash table over symbol records (globals, or the hash inside the publics stream).
    static byte[] GsiHash(List<(string Name, int Off)> recs, ByteWriter symrec) {
        const int Buckets = 4096;
        var sorted = recs.Select(r => (r.Name, r.Off, Bucket: (int)(PdbHash.V1(r.Name) % Buckets)))
                         .OrderBy(r => r.Bucket).ThenBy(r => r.Name, StringComparer.Ordinal).ToList();
        var w = new ByteWriter();
        var bitmap = new uint[(Buckets + 32) / 32];
        var starts = new List<int>();
        for (int i = 0; i < sorted.Count; i++)
            if (i == 0 || sorted[i].Bucket != sorted[i - 1].Bucket) { bitmap[sorted[i].Bucket / 32] |= 1u << (sorted[i].Bucket % 32); starts.Add(i); }
        w.U32(0xffffffff); w.U32(0xeffe0000 + 19990810); w.U32(8 * sorted.Count); w.U32(4 * bitmap.Length + 4 * starts.Count);
        foreach (var r in sorted) { w.U32(r.Off + 1); w.U32(1); }
        foreach (var x in bitmap) w.U32(x);
        foreach (int s in starts) w.U32(12 * s);
        return w.ToArray();
    }

    // The MSF container: superblock, two free block maps, the streams, the directory.
    static byte[] Msf(List<byte[]> streams) {
        const int B = 4096;
        int next = 3;
        var blocks = new List<int[]>();
        foreach (var s in streams) {
            int n = (s.Length + B - 1) / B;
            blocks.Add(Enumerable.Range(next, n).ToArray());
            next += n;
        }
        var dir = new ByteWriter();
        dir.U32(streams.Count);
        foreach (var s in streams) dir.U32(s.Length);
        foreach (var bl in blocks) foreach (int x in bl) dir.U32(x);
        int dirBlocks = (dir.Count + B - 1) / B;
        int dirStart = next; next += dirBlocks;
        int mapBlock = next++;
        int numBlocks = next;
        if (numBlocks > B * 8 - 1) throw new ForgeException("the program is too large for its PDB (more than 128 MB)", 0);

        var file = new byte[numBlocks * B];
        var magic = Encoding.ASCII.GetBytes("Microsoft C/C++ MSF 7.00\r\n\x1a" + "DS\0\0\0");
        Array.Copy(magic, file, magic.Length);
        void Put(int at, int v) => BitConverter.GetBytes(v).CopyTo(file, at);
        Put(32, B); Put(36, 1); Put(40, numBlocks); Put(44, dir.Count); Put(48, 0); Put(52, mapBlock);
        for (int fpm = 1; fpm <= 2; fpm++)                                         // free block maps: 1 = free
            for (int bit = 0; bit < B * 8; bit++)
                if (bit >= numBlocks) file[fpm * B + bit / 8] |= (byte)(1 << (bit % 8));
        for (int i = 0; i < streams.Count; i++)
            for (int k = 0; k < blocks[i].Length; k++)
                Array.Copy(streams[i], k * B, file, blocks[i][k] * B, Math.Min(B, streams[i].Length - k * B));
        var d = dir.ToArray();
        for (int k = 0; k < dirBlocks; k++) {
            Array.Copy(d, k * B, file, (dirStart + k) * B, Math.Min(B, d.Length - k * B));
            Put(mapBlock * B + 4 * k, dirStart + k);
        }
        return file;
    }
}

// The hash functions of the PDB format.
static class PdbHash {
    // hashStringV1: xor of little-endian 32-bit words, then a 16-bit and an 8-bit tail
    public static uint V1(string s) {
        var b = Encoding.UTF8.GetBytes(s);
        uint r = 0;
        int i = 0;
        for (; i + 4 <= b.Length; i += 4) r ^= BitConverter.ToUInt32(b, i);
        if (b.Length - i >= 2) { r ^= BitConverter.ToUInt16(b, i); i += 2; }
        if (b.Length - i == 1) r ^= b[i];
        r |= 0x20202020;
        r ^= r >> 11;
        return r ^ (r >> 16);
    }
    // hashBufferV8: CRC-32 (reflected 0xEDB88320) with initial value 0 and no final xor
    static readonly uint[] Table = Enumerable.Range(0, 256).Select(n => {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();
    public static uint V8(byte[] b) {
        uint c = 0;
        foreach (byte x in b) c = Table[(c ^ x) & 0xff] ^ (c >> 8);
        return c;
    }
}

// A TPI/IPI type stream under construction.
sealed class TypeTable {
    const uint HashBuckets = 0x3ffff;
    readonly ByteWriter _records = new();
    readonly List<uint> _hashes = new();
    int _next = 0x1000;

    // Adds a record (kind + body, padded to 4 with LF_PAD bytes) and returns its type index.
    // udtName: hash by name (a full struct definition); otherwise by the record's bytes.
    public int Add(int kind, ByteWriter body, string? udtName) {
        var rec = new ByteWriter();
        int len = 2 + body.Count; int pad = (4 - (2 + len) % 4) % 4;
        rec.U16(len + pad); rec.U16(kind); rec.Bytes(body.ToArray());
        for (int i = pad; i > 0; i--) rec.U8(0xf0 | i);
        var bytes = rec.ToArray();
        _records.Bytes(bytes);
        _hashes.Add((udtName != null ? PdbHash.V1(udtName) : PdbHash.V8(bytes)) % HashBuckets);
        return _next++;
    }
    // a numeric leaf: values below 0x8000 are stored directly, larger ones as LF_ULONG
    public static void Numeric(ByteWriter w, int v) { if (v < 0x8000) w.U16(v); else { w.U16(0x8004); w.U32(v); } }

    public byte[] Stream(int hashStream) {
        var w = new ByteWriter();
        int n = _hashes.Count;
        w.U32(20040203); w.U32(56); w.U32(0x1000); w.U32(_next); w.U32(_records.Count);
        w.U16(n > 0 ? hashStream : 0xffff); w.U16(0xffff); w.U32(4); w.U32(HashBuckets);
        w.U32(0); w.U32(4 * n); w.U32(4 * n); w.U32(n > 0 ? 8 : 0); w.U32(4 * n + (n > 0 ? 8 : 0)); w.U32(0);
        w.Bytes(_records.ToArray());
        return w.ToArray();
    }
    public byte[] HashStream() {
        var w = new ByteWriter();
        foreach (var h in _hashes) w.U32(h);
        if (_hashes.Count > 0) { w.U32(0x1000); w.U32(0); }                       // index offsets: the first record
        return w.ToArray();
    }
}

// A PDB string table ("/names"): offsets into a buffer, plus a hash table of them.
sealed class PdbStrings {
    readonly ByteWriter _buf = new();
    readonly Dictionary<string, int> _at = new();
    public PdbStrings() { _buf.U8(0); }
    public int Add(string s) {
        if (_at.TryGetValue(s, out int at)) return at;
        at = _buf.Count; _buf.Str(s); _at[s] = at;
        return at;
    }
    public byte[] ToArray() {
        var w = new ByteWriter();
        w.U32(0xeffeeffe); w.U32(1); w.U32(_buf.Count); w.Bytes(_buf.ToArray());
        int buckets = Math.Max(1, _at.Count * 2);
        var table = new uint[buckets];
        foreach (var (s, at) in _at) {
            uint k = PdbHash.V1(s) % (uint)buckets;
            while (table[k] != 0) k = (k + 1) % (uint)buckets;
            table[k] = (uint)at;
        }
        w.U32(buckets); foreach (var x in table) w.U32(x);
        w.U32(_at.Count);
        return w.ToArray();
    }
}
