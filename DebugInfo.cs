using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ForgeC;

// Debug information (-g).
//
// While emitting code, CodeGen records a line table (code offset -> source position, one
// row per statement) and, per function, its code range, frame shape and variables. After
// layout this model is written out as DWARF 4 inside the Linux ELF (DwarfSections, below)
// or as a PDB next to the Windows .exe (Pdb.cs).
//
// Variables: a framed function keeps every variable in a stack slot at [rbp + off], so at
// -O0 every variable can be shown. At -O2 a variable the register allocator put in a
// register shares that register with others over its life, so it is reported as
// optimized out rather than with a value that may belong to another variable.
// Compiler temporaries (names with '$') and runtime code are left out.
sealed partial class CodeGen {
    public bool DebugInfo;

    public sealed class DbgVar {
        public string Name = "", Type = "";
        public int Off;                 // rbp-relative slot
        public bool IsParam, InReg;     // InReg: no location (optimized out)
    }
    public sealed class DbgFunc {
        public string Name = "", Ret = "void";
        public int Line;                // position id of the declaration
        public int Start, BodyStart, End;
        public bool Frameless;
        public int FrameSize;           // framed: the sub rsp, N of the prologue
        public List<int> SavedRegs = new();             // x86 register numbers, in push / slot order
        public Dictionary<int, int> SaveSlot = new();   // framed: register -> rbp-relative slot
        public List<DbgVar> Vars = new();
    }
    public readonly List<DbgFunc> DbgFuncs = new();
    public readonly List<(int Pos, int Line)> DbgRows = new();   // Line = position id; 0 = no source (runtime code)
    DbgFunc? _dbgFunc;

    // a line-table row for the code about to be emitted
    void DbgRow(int line) {
        if (_dbgFunc == null || line <= 0 || Sources.FileOf(line) < 0) return;
        if (DbgRows.Count > 0) {
            var (pos, last) = DbgRows[^1];
            if (pos == _a.Pos) { DbgRows[^1] = (pos, line); return; }
            if (last > 0 && Sources.FileOf(last) == Sources.FileOf(line) && Sources.LineOf(last) == Sources.LineOf(line)) return;
        }
        DbgRows.Add((_a.Pos, line));
    }

    void DbgFuncStart(Func f) {
        _dbgFunc = null;
        if (!DebugInfo || f.IsRuntime || Sources.FileOf(f.Line) < 0) return;
        _dbgFunc = new DbgFunc { Name = f.Name, Ret = f.Ret, Line = f.Line, Start = _a.Pos, Frameless = _frameless };
        DbgRow(f.Line);
    }
    void DbgBodyStart() { if (_dbgFunc != null) _dbgFunc.BodyStart = _a.Pos; }
    void DbgFuncEnd(Func f) {
        var d = _dbgFunc;
        if (d == null) return;
        d.End = _a.Pos;
        d.SavedRegs.AddRange(_savedRegs);
        foreach (var (r, slot) in _saveSlot) d.SaveSlot[r] = slot;
        var pars = new HashSet<string>(f.Params.Select(p => p.Name));
        foreach (var (name, (off, type)) in _locals) {
            if (name.Contains('$')) continue;
            d.Vars.Add(new DbgVar { Name = name, Type = type, Off = off, IsParam = pars.Contains(name),
                                    InReg = _frameless || _regOf.ContainsKey(name) });
        }
        // parameters first, in declaration order
        d.Vars = d.Vars.OrderBy(v => v.IsParam ? f.Params.FindIndex(p => p.Name == v.Name) : int.MaxValue).ToList();
        DbgFuncs.Add(d);
        if (DbgRows.Count > 0 && DbgRows[^1].Pos == _a.Pos) DbgRows.RemoveAt(DbgRows.Count - 1);
        DbgRows.Add((_a.Pos, 0));                       // what follows has no source line
        _dbgFunc = null;
    }

    // ---------------------------------------------------------------- types
    // How a Forge type looks to a debugger.
    enum DbgKind { Base, Pointer, Struct }
    sealed record DbgType(DbgKind Kind, string Name, int Size, int Encoding, string? Target);
    // Encoding: DWARF DW_ATE (2 boolean, 4 float, 5 signed, 6 signed char, 8 unsigned).
    DbgType DescribeType(string t) {
        if (t == "string") return new(DbgKind.Pointer, "string", 8, 0, "char");
        if (t == "char") return new(DbgKind.Base, "char", 1, 6, null);
        if (t.EndsWith("*")) return new(DbgKind.Pointer, t, 8, 0, Pointee(t) == "void" ? null : Pointee(t));
        if (t.EndsWith("[]")) return new(DbgKind.Pointer, t, 8, 0, t[..^2]);
        if (_structs.ContainsKey(t)) return new(DbgKind.Pointer, t, 8, 0, "struct " + t);
        if (t.StartsWith("struct ")) return new(DbgKind.Struct, t[7..], Math.Max(1, _structs[t[7..]].Size), 0, null);
        if (t == "bool") return new(DbgKind.Base, "bool", 8, 2, null);
        if (t == "double") return new(DbgKind.Base, "double", 8, 4, null);
        if (IntTypes.Contains(t) || t is "i8" or "u8" or "i16" or "u16" or "i32" or "u32" or "i64" or "u64")
            return new(DbgKind.Base, t, Width(t), Signed(t) ? 5 : 8, null);
        return new(DbgKind.Base, "long", 8, 5, null);   // anything else: shown as a 64-bit number
    }
    IEnumerable<(string Name, int Off, string Type)> StructFields(string name) =>
        _structs[name].Fields.OrderBy(kv => kv.Value.Off).Select(kv => (kv.Key, kv.Value.Off, kv.Value.Type));

    // Every type the debug info mentions, reachable from variables, return types and fields.
    List<string> DbgTypeClosure() {
        var order = new List<string>();
        var seen = new HashSet<string>();
        void Add(string t) {
            if (!seen.Add(t)) return;
            order.Add(t);
            var d = DescribeType(t);
            if (d.Kind == DbgKind.Pointer && d.Target != null) Add(d.Target);
            if (d.Kind == DbgKind.Struct) foreach (var (_, _, ft) in StructFields(d.Name)) Add(ft);
        }
        foreach (var f in DbgFuncs) {
            if (f.Ret != "void") Add(f.Ret);
            foreach (var v in f.Vars) Add(v.Type);
        }
        return order;
    }

    // x86 register number -> DWARF register number
    static readonly int[] DwarfReg = { 0, 2, 1, 3, 7, 6, 4, 5, 8, 9, 10, 11, 12, 13, 14, 15 };

    // ---------------------------------------------------------------- DWARF
    // .debug_abbrev, .debug_info, .debug_line, .debug_frame for code loaded at codeBase.
    public List<(string Name, byte[] Bytes)> DwarfSections(long codeBase, int codeLen) {
        var types = DbgTypeClosure();

        // ---- abbreviations
        var ab = new ByteWriter();
        void Abbrev(int code, int tag, bool children, params (int At, int Form)[] attrs) {
            ab.Uleb(code); ab.Uleb(tag); ab.U8(children ? 1 : 0);
            foreach (var (at, form) in attrs) { ab.Uleb(at); ab.Uleb(form); }
            ab.U8(0); ab.U8(0);
        }
        const int STRING = 0x08, DATA1 = 0x0b, DATA2 = 0x05, DATA4 = 0x06, DATA8 = 0x07, ADDR = 0x01, REF4 = 0x13,
                  UDATA = 0x0f, SECOFF = 0x17, EXPRLOC = 0x18, FLAG = 0x19;
        const int NAME = 0x03, BYTESIZE = 0x0b, STMTLIST = 0x10, LOWPC = 0x11, HIGHPC = 0x12, LANG = 0x13, COMPDIR = 0x1b,
                  PRODUCER = 0x25, DECLFILE = 0x3a, DECLLINE = 0x3b, ENCODING = 0x3e, EXTERNAL = 0x3f, FRAMEBASE = 0x40,
                  LOCATION = 0x02, TYPE = 0x49, MEMBERLOC = 0x38;
        Abbrev(1, 0x11, true, (NAME, STRING), (PRODUCER, STRING), (LANG, DATA2), (COMPDIR, STRING), (LOWPC, ADDR), (HIGHPC, DATA8), (STMTLIST, SECOFF));
        Abbrev(2, 0x24, false, (NAME, STRING), (ENCODING, DATA1), (BYTESIZE, DATA1));               // base_type
        Abbrev(3, 0x0f, false, (BYTESIZE, DATA1), (TYPE, REF4));                                      // pointer_type
        Abbrev(4, 0x0f, false, (BYTESIZE, DATA1));                                                    // void*
        Abbrev(5, 0x13, true, (NAME, STRING), (BYTESIZE, DATA4));                                     // structure_type
        Abbrev(6, 0x0d, false, (NAME, STRING), (TYPE, REF4), (MEMBERLOC, DATA4));                    // member
        Abbrev(7, 0x2e, true, (NAME, STRING), (DECLFILE, UDATA), (DECLLINE, UDATA), (LOWPC, ADDR), (HIGHPC, DATA8), (FRAMEBASE, EXPRLOC), (EXTERNAL, FLAG));
        Abbrev(8, 0x2e, true, (NAME, STRING), (DECLFILE, UDATA), (DECLLINE, UDATA), (LOWPC, ADDR), (HIGHPC, DATA8), (FRAMEBASE, EXPRLOC), (EXTERNAL, FLAG), (TYPE, REF4));
        Abbrev(9, 0x05, false, (NAME, STRING), (TYPE, REF4), (LOCATION, EXPRLOC));                   // formal_parameter
        Abbrev(10, 0x34, false, (NAME, STRING), (TYPE, REF4), (LOCATION, EXPRLOC));                  // variable
        Abbrev(11, 0x05, false, (NAME, STRING), (TYPE, REF4));                                        // parameter, optimized out
        Abbrev(12, 0x34, false, (NAME, STRING), (TYPE, REF4));                                        // variable, optimized out
        ab.U8(0);

        // ---- compile unit
        var info = new ByteWriter();
        info.U32(0); info.U16(4); info.U32(0); info.U8(8);       // length (patched), version 4, abbrev offset, address size
        info.Uleb(1);
        info.Str(Sources.Files.Count > 0 ? Sources.Files[0] : "program.fg");
        info.Str("forgec");
        info.U16(0x0c);                                           // DW_LANG_C99: gdb evaluates expressions C-style
        info.Str(Directory.GetCurrentDirectory());
        info.U64(codeBase); info.U64(codeLen);
        info.U32(0);                                              // .debug_line offset

        var typeOff = new Dictionary<string, int>();
        var fixups = new List<(int At, string Type)>();
        void TypeRef(string t) { fixups.Add((info.Count, t)); info.U32(0); }
        foreach (var t in types) {
            typeOff[t] = info.Count;
            var d = DescribeType(t);
            switch (d.Kind) {
                case DbgKind.Base: info.Uleb(2); info.Str(d.Name); info.U8(d.Encoding); info.U8(d.Size); break;
                case DbgKind.Pointer when d.Target == null: info.Uleb(4); info.U8(8); break;
                case DbgKind.Pointer: info.Uleb(3); info.U8(8); TypeRef(d.Target); break;
                case DbgKind.Struct:
                    info.Uleb(5); info.Str(d.Name); info.U32(d.Size);
                    foreach (var (fn, off, ft) in StructFields(d.Name)) { info.Uleb(6); info.Str(fn); TypeRef(ft); info.U32(off); }
                    info.U8(0);
                    break;
            }
        }
        foreach (var f in DbgFuncs) {
            info.Uleb(f.Ret == "void" ? 7 : 8);
            info.Str(f.Name);
            info.Uleb(Sources.FileOf(f.Line) + 1); info.Uleb(Sources.LineOf(f.Line));
            info.U64(codeBase + f.Start); info.U64(f.End - f.Start);
            if (f.Frameless) { info.Uleb(1); info.U8(0x9c); }    // DW_OP_call_frame_cfa
            else { info.Uleb(1); info.U8(0x56); }                // DW_OP_reg6 (rbp)
            // DW_AT_external is DW_FORM_flag_present: it takes no bytes
            if (f.Ret != "void") TypeRef(f.Ret);
            foreach (var v in f.Vars) {
                if (v.InReg) { info.Uleb(v.IsParam ? 11 : 12); info.Str(v.Name); TypeRef(v.Type); continue; }
                info.Uleb(v.IsParam ? 9 : 10); info.Str(v.Name); TypeRef(v.Type);
                var loc = new ByteWriter(); loc.U8(0x91); loc.Sleb(v.Off);        // DW_OP_fbreg off
                info.Uleb(loc.Count); info.Bytes(loc.ToArray());
            }
            info.U8(0);
        }
        info.U8(0);
        foreach (var (at, t) in fixups) info.PatchU32(at, typeOff[t]);
        info.PatchU32(0, info.Count - 4);

        return new() {
            (".debug_abbrev", ab.ToArray()),
            (".debug_info", info.ToArray()),
            (".debug_line", DwarfLine(codeBase, codeLen)),
            (".debug_frame", DwarfFrame(codeBase)),
        };
    }

    byte[] DwarfLine(long codeBase, int codeLen) {
        var w = new ByteWriter();
        w.U32(0); w.U16(4);                                       // unit length (patched), version 4
        int hdrLenAt = w.Count; w.U32(0);
        int hdrStart = w.Count;
        w.U8(1); w.U8(1); w.U8(1);                                // min instruction length, max ops, default_is_stmt
        w.U8(unchecked((byte)-5)); w.U8(14); w.U8(13);            // line_base, line_range, opcode_base
        foreach (int n in new[] { 0, 1, 1, 1, 1, 0, 0, 0, 1, 0, 0, 1 }) w.U8(n);
        w.U8(0);                                                  // no include directories
        foreach (var f in Sources.Files) { w.Str(f); w.Uleb(0); w.Uleb(0); w.Uleb(0); }
        if (Sources.Files.Count == 0) { w.Str("program.fg"); w.Uleb(0); w.Uleb(0); w.Uleb(0); }
        w.U8(0);
        w.PatchU32(hdrLenAt, w.Count - hdrStart);

        w.U8(0); w.Uleb(9); w.U8(2); w.U64(codeBase);             // DW_LNE_set_address
        int file = 1, line = 1, col = 0, addr = 0;
        foreach (var (pos, id) in DbgRows) {
            int nf = id == 0 ? file : Sources.FileOf(id) + 1, nl = id == 0 ? 0 : Sources.LineOf(id), nc = id == 0 ? 0 : Sources.ColOf(id);
            if (nf != file) { w.U8(4); w.Uleb(nf); file = nf; }                  // DW_LNS_set_file
            if (nl != line) { w.U8(3); w.Sleb(nl - line); line = nl; }          // DW_LNS_advance_line
            if (nc != col) { w.U8(5); w.Uleb(nc); col = nc; }                    // DW_LNS_set_column
            if (pos != addr) { w.U8(2); w.Uleb(pos - addr); addr = pos; }       // DW_LNS_advance_pc
            w.U8(1);                                                             // DW_LNS_copy
        }
        if (codeLen > addr) { w.U8(2); w.Uleb(codeLen - addr); }
        w.U8(0); w.Uleb(1); w.U8(1);                              // DW_LNE_end_sequence
        w.PatchU32(0, w.Count - 4);
        return w.ToArray();
    }

    // Call frame information, so a debugger can unwind (backtrace, finish, step over).
    byte[] DwarfFrame(long codeBase) {
        var w = new ByteWriter();
        // CIE: CFA = rsp + 8 and the return address at CFA - 8, on entry
        w.U32(0); int cieStart = w.Count;
        w.U32(0xffffffff); w.U8(1); w.Str("");                   // CIE id, version 1, no augmentation
        w.Uleb(1); w.Sleb(-8); w.U8(16);                          // code align, data align, return address = rip
        w.U8(0x0c); w.Uleb(7); w.Uleb(8);                         // DW_CFA_def_cfa rsp, 8
        w.U8(0x80 | 16); w.Uleb(1);                               // DW_CFA_offset rip, cfa-8
        while ((w.Count - cieStart) % 8 != 4) w.U8(0);            // DW_CFA_nop padding (length + body = 8k)
        w.PatchU32(0, w.Count - 4);

        foreach (var f in DbgFuncs) {
            int start = w.Count; w.U32(0);
            w.U32(0);                                             // the CIE at offset 0
            w.U64(codeBase + f.Start); w.U64(f.End - f.Start);
            int at = f.Start;
            void Advance(int to) {
                int d = to - at; at = to;
                if (d <= 0) return;
                if (d < 64) w.U8(0x40 | d);                                   // DW_CFA_advance_loc
                else if (d < 256) { w.U8(0x02); w.U8(d); }                    // DW_CFA_advance_loc1
                else if (d < 65536) { w.U8(0x03); w.U16(d); }                 // DW_CFA_advance_loc2
                else { w.U8(0x04); w.U32(d); }                                // DW_CFA_advance_loc4
            }
            if (!f.Frameless) {
                Advance(f.Start + 1); w.U8(0x0e); w.Uleb(16);     // push rbp: CFA = rsp + 16
                w.U8(0x80 | 6); w.Uleb(2);                        // rbp saved at CFA - 16
                Advance(f.Start + 4); w.U8(0x0d); w.Uleb(6);      // mov rbp, rsp: CFA = rbp + 16
                if (f.SaveSlot.Count > 0) {
                    Advance(f.BodyStart);
                    foreach (var (r, slot) in f.SaveSlot) { w.U8(0x80 | DwarfReg[r]); w.Uleb((16 - slot) / 8); }
                }
            } else {
                int pos = f.Start, k = 0;
                foreach (int r in f.SavedRegs) {
                    pos += r >= 8 ? 2 : 1; k++;
                    Advance(pos); w.U8(0x0e); w.Uleb(8 + 8 * k);
                    w.U8(0x80 | DwarfReg[r]); w.Uleb(1 + k);
                }
            }
            while ((w.Count - start) % 8 != 0) w.U8(0);
            w.PatchU32(start, w.Count - start - 4);
        }
        return w.ToArray();
    }

    // ---- ELF symbols: every function and runtime routine, for backtraces without DWARF too
    public List<(string Name, long Addr, long Size)> ElfSymbols(long codeBase) {
        var syms = new List<(string, long, long)>();
        var user = DbgFuncs.ToDictionary(f => f.Name);
        foreach (var (name, pos) in _a.Labels) {
            if (name.StartsWith(".") || name.Contains('$') && !user.ContainsKey(name)) continue;
            long size = user.TryGetValue(name, out var f) ? f.End - f.Start : 0;
            syms.Add((name, codeBase + pos, size));
        }
        return syms.OrderBy(s => s.Item2).ToList();
    }
}

// Little-endian byte buffer with LEB128.
sealed class ByteWriter {
    readonly List<byte> _b = new();
    public int Count => _b.Count;
    public void U8(int v) => _b.Add((byte)v);
    public void U16(int v) { U8(v); U8(v >> 8); }
    public void U32(long v) { for (int i = 0; i < 4; i++) U8((int)(v >> (8 * i))); }
    public void U64(long v) { for (int i = 0; i < 8; i++) U8((int)(v >> (8 * i))); }
    public void Uleb(long v) { do { byte b = (byte)(v & 0x7f); v = (long)((ulong)v >> 7); if (v != 0) b |= 0x80; _b.Add(b); } while (v != 0); }
    public void Sleb(long v) {
        while (true) {
            byte b = (byte)(v & 0x7f); v >>= 7;
            bool done = (v == 0 && (b & 0x40) == 0) || (v == -1 && (b & 0x40) != 0);
            _b.Add(done ? b : (byte)(b | 0x80));
            if (done) return;
        }
    }
    public void Str(string s) { _b.AddRange(Encoding.UTF8.GetBytes(s)); _b.Add(0); }
    public void Bytes(byte[] b) => _b.AddRange(b);
    public void Align(int a) { while (_b.Count % a != 0) _b.Add(0); }
    public void PatchU16(int at, int v) { _b[at] = (byte)v; _b[at + 1] = (byte)(v >> 8); }
    public void PatchU32(int at, long v) { for (int i = 0; i < 4; i++) _b[at + i] = (byte)(v >> (8 * i)); }
    public byte[] ToArray() => _b.ToArray();
}
