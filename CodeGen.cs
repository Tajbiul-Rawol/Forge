using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ForgeC;

enum Target { Windows, Linux, Kernel }

// Walks the AST, type-checks it, and emits x86-64 into an Asm buffer.
//
// Conventions:
//   * Integer-typed expressions evaluate into rax.
//   * Locals live at negative offsets from rbp; each is 8 bytes.
//   * Calling convention follows System V: args in rdi,rsi,rdx,rcx,r8,r9.
//     We only need up to 4 here so rdi/rsi/rdx/rcx are enough, but to keep the
//     evaluator simple we compute args onto the machine stack then pop them.
//   * A `throws` function returns its value in rax and an ok-flag in rdx
//     (rdx=1 ok, rdx=0 error). The error message pointer rides in rsi on failure.
//
// Supported value types are all 64-bit integers at the machine level
// (int/uint/ushort/long/bool). double and string are supported only as
// Console.WriteLine arguments and string literals; arithmetic is integer.
sealed partial class CodeGen {
    readonly ModuleNode _mod;
    readonly Dictionary<string, Func> _funcs = new();
    readonly Asm _a = new();
    readonly Target _target;
    public readonly List<Diagnostic> Warnings = new();
    // Type errors collected by Analyze (it keeps going after an error, up to MaxErrors).
    public readonly List<Diagnostic> Errors = new();
    public const int MaxErrors = 20;
    // Locals whose declaration failed to type-check: later errors that mention them are
    // follow-on noise and are not reported.
    readonly HashSet<string> _poisoned = new();
    readonly HashSet<Stmt> _poisonSources = new(ReferenceEqualityComparer.Instance);   // the declarations that failed: never suppressed

    // relocations into the IAT: (codePosition, importIndex) for `mov rax,[iatSlot]`
    readonly List<(int CodePos, int ImportIdx)> _iatRelocs = new();
    // relocations for the two mutable data slots (handle, scratch)
    readonly List<(int CodePos, int Which)> _slotRelocs = new(); // Which: 0=handle 1=scratch

    // data section: collected string literals -> assigned virtual addresses later
    readonly List<byte[]> _data = new();
    readonly List<int> _dataOffsets = new();
    int _dataLen;

    // per-function state
    Func _cur = null!;
    Dictionary<string, (int Off, string Type)> _locals = null!;
    int _frame;
    int _labelId;
    const int HeapReserved = 8192;   // bytes at the heap base never handed out by malloc (see Threads.cs for 4096..8192)
    const byte HeapUserOff = 64;     // heapbase() = heapBase + 64 (past print scratch)
    readonly bool _usesAsync;
    int _schedOff = -1;            // data offset of the async scheduler block

    public const long VADDR = 0x400000;
    public const int HEADER_BYTES = 64 + 56 * 4;       // ehdr + 4 phdrs (see Elf.Build)
    public const long CODE_VADDR = VADDR + HEADER_BYTES; // code begins right after headers
    long _dataVaddr;                            // filled during layout

    public CodeGen(ModuleNode mod, Target target) {
        _mod = mod;
        _target = target;
        _usesAsync = AsyncRuntime.Uses(mod);
        if (_usesAsync) AsyncRuntime.Inject(mod);
        StdLib.Inject(mod);                      // the Math functions the program calls (StdLib.cs)
        int doubleLine = DoubleRuntime.FirstUse(mod);
        if (doubleLine > 0) {
            if (target == Target.Kernel)
                throw new ForgeException("double needs --target win or linux (SSE is not enabled on bare metal yet)", doubleLine);
            DoubleRuntime.Inject(mod);           // __dtoa / __dtoaf for printing
        }
        ExtractGenerics(mod);                    // templates out; generic struct types used by the program in (Generics.cs)
        foreach (var f in mod.Funcs) {
            if (!_funcs.TryAdd(f.Name, f))
                throw new ForgeException($"duplicate function '{f.Name}'" + FirstAt(_funcs[f.Name].Line, f.Line), f.Line);
        }
        foreach (var sd in mod.Structs)
            if (!_structs.TryAdd(sd.Name, new StructInfo(sd)))
                throw new ForgeException($"duplicate struct '{sd.Name}'" + FirstAt(_structs[sd.Name].Decl.Line, sd.Line), sd.Line);
        foreach (var sd in mod.Structs) LayoutStruct(_structs[sd.Name]);
        SetupExterns();
        _registered = true;                      // generic instances made from now on register themselves
    }

    // " (first defined at geometry.fg:4)" when a duplicate comes from another file
    static string FirstAt(int first, int dup) =>
        Sources.FileOf(first) != Sources.FileOf(dup) ? $" (first defined at {Sources.Loc(first)})" : "";

    // ---- externs: functions in DLLs / shared libraries ------------------------------
    readonly Dictionary<string, ExternDecl> _externs = new();
    readonly Dictionary<string, int> _externSlot = new();    // Windows: flat IAT index; Linux: GOT index
    readonly List<(int Pos, int Slot)> _gotRelocs = new();   // Linux: code positions holding a GOT slot address
    public string LinuxInterp = "/lib64/ld-linux-x86-64.so.2";
    void SetupExterns() {
        Pe.ExternGroups = new();
        foreach (var ex in _mod.Externs) {
            if (_target == Target.Kernel) throw new ForgeException($"extern '{ex.Name}': there are no libraries on bare metal", ex.Line);
            if (_funcs.ContainsKey(ex.Name) || !_externs.TryAdd(ex.Name, ex))
                throw new ForgeException($"duplicate function '{ex.Name}'"
                    + FirstAt(_funcs.TryGetValue(ex.Name, out var df) ? df.Line : _externs[ex.Name].Line, ex.Line), ex.Line);
            if (ex.Params.Count > 12) throw new ForgeException($"extern '{ex.Name}': at most 12 parameters", ex.Line);
        }
        if (_target == Target.Windows) {
            var groups = new List<(string Dll, List<string> Funcs)>();
            foreach (var ex in _mod.Externs) {
                int g = groups.FindIndex(x => string.Equals(x.Dll, ex.Lib, StringComparison.OrdinalIgnoreCase));
                if (g < 0) { groups.Add((ex.Lib, new List<string>())); g = groups.Count - 1; }
                groups[g].Funcs.Add(ex.Name);
            }
            int idx = Pe.ExternBase;
            foreach (var (dll, fs) in groups) { foreach (var fn in fs) _externSlot[fn] = idx++; Pe.ExternGroups.Add((dll, fs.ToArray())); }
        } else {
            int k = 0;
            foreach (var ex in _mod.Externs) _externSlot[ex.Name] = k++;
        }
        foreach (var f in _mod.Funcs)
            if (f.IsCallback && (f.IsAsync || f.Throws != null || f.Params.Count > 4))
                throw new ForgeException($"extern function '{f.Name}': at most 4 parameters, not async, no throws", f.Line);
    }
    static bool FfiType(string t) => t is not ("void" or "double") && !IsTask(t);

    // Call a C function: arguments on the stack first (any expression may be a call),
    // then the platform convention - Win64: rcx rdx r8 r9 + 32 bytes shadow space;
    // System V: rdi rsi rdx rcx r8 r9 (al = 0 for varargs) - with rsp 16-byte aligned.
    void EmitExternCall(CallExpr c, ExternDecl ex) {
        bool win = _target == Target.Windows;
        int[] regs = win ? new[] { RCX, RDX, R8, R9 } : new[] { RDI, RSI, RDX, RCX, R8, R9 };
        int n = c.Args.Count;
        foreach (var a in c.Args) { EmitExpr(a); _a.PushRax(); }
        _a.MovRR(R11, RSP);                                   // arg i at [r11 + 8*(n-1-i)]
        _a.AndRspImm8(-16);
        _a.PushR(R11);                                        // rsp = 8 mod 16
        int stackArgs = Math.Max(0, n - regs.Length), home = win ? 32 : 0;
        int frame = ((home + 8 * stackArgs + 15) & ~15) + 8;  // -> rsp = 0 mod 16 at the call
        _a.AluRI(5, RSP, frame);
        for (int i = regs.Length; i < n; i++) { _a.Ld(RAX, R11, 8 * (n - 1 - i)); _a.St(RSP, home + 8 * (i - regs.Length), RAX); }
        for (int i = 0; i < n && i < regs.Length; i++) _a.Ld(regs[i], R11, 8 * (n - 1 - i));
        if (win) EmitIatIndex(_externSlot[ex.Name]);
        else {
            _a.Db(0x49, 0xba); _gotRelocs.Add((_a.Pos, _externSlot[ex.Name])); for (int i = 0; i < 8; i++) _a.Db(0);  // mov r10, &got[k]
            _a.Ld(R10, R10, 0);
            _a.Db(0x31, 0xc0);                                // xor eax, eax (no vector args)
            _a.Db(0x41, 0xff, 0xd2);                          // call r10
        }
        _a.Ld(RSP, RSP, frame);                               // back to the pushed arguments
        if (n > 0) _a.AluRI(0, RSP, 8 * n);
        switch (ex.Ret) {                                     // C returns narrow values in the low bits only
            case "bool" or "u8": _a.Db(0x48, 0x0f, 0xb6, 0xc0); break;
            case "i8": _a.Db(0x48, 0x0f, 0xbe, 0xc0); break;
            case "u16": _a.Db(0x48, 0x0f, 0xb7, 0xc0); break;
            case "i16": _a.Db(0x48, 0x0f, 0xbf, 0xc0); break;
            case "i32": _a.Db(0x48, 0x63, 0xc0); break;
            case "u32": _a.Db(0x89, 0xc0); break;
        }
    }
    // ---- bare metal -----------------------------------------------------------------
    // Interrupt entry: the CPU pushed [error code,] rip, cs, rflags, rsp, ss. Save every
    // register a Forge function may clobber, call the handler (error code in rdi),
    // restore, drop the error code, iretq.
    void EmitInterruptStubs() {
        foreach (var f in _mod.Funcs) {
            if (!f.IsInterrupt) continue;
            bool err = f.Params.Count == 1;
            int[] saved = { RAX, RCX, RDX, RSI, RDI, R8, R9, R10, R11 };
            _a.Label(f.Name + "$isr");
            foreach (var r in saved) _a.PushR(r);
            _a.PushR(RBP); _a.MovRR(RBP, RSP);
            if (err) _a.Ld(RDI, RBP, 8 + 8 * saved.Length);     // the CPU's error code
            _a.AndRspImm8(-16);
            _a.Db(0xfc);                                         // cld
            _a.CallRel(f.Name);
            _a.MovRR(RSP, RBP); _a.PopR(RBP);
            for (int i = saved.Length - 1; i >= 0; i--) _a.PopR(saved[i]);
            if (err) _a.AluRI(0, RSP, 8);
            _a.Db(0x48, 0xcf);                                   // iretq
        }
    }

    bool EmitBareMetalBuiltin(CallExpr c) {
        if (_funcs.ContainsKey(c.Name) || _externs.ContainsKey(c.Name)) return false;
        switch (c.Name) {
            case "outb": case "outw": case "outl":
                EmitArgsTo(c, "rdx", "rcx"); _a.MovRR(RAX, RCX);        // dx = port, eax = value
                if (c.Name == "outw") _a.Db(0x66);
                _a.Db(c.Name == "outb" ? (byte)0xee : (byte)0xef);      // out dx, al / ax / eax
                return true;
            case "inb": case "inw": case "inl":
                EmitExpr(c.Args[0]); _a.MovRR(RDX, RAX); _a.Db(0x31, 0xc0);   // xor eax, eax
                if (c.Name == "inw") _a.Db(0x66);
                _a.Db(c.Name == "inb" ? (byte)0xec : (byte)0xed);       // in al / ax / eax, dx
                return true;
            case "cli": _a.Db(0xfa); return true;
            case "sti": _a.Db(0xfb); return true;
            case "hlt": _a.Db(0xf4); return true;
            case "pause": _a.Db(0xf3, 0x90); return true;
            case "read_cr0": _a.Db(0x0f, 0x20, 0xc0); return true;     // mov rax, cr0
            case "read_cr2": _a.Db(0x0f, 0x20, 0xd0); return true;
            case "read_cr3": _a.Db(0x0f, 0x20, 0xd8); return true;
            case "read_cr4": _a.Db(0x0f, 0x20, 0xe0); return true;
            case "write_cr0": EmitExpr(c.Args[0]); _a.Db(0x0f, 0x22, 0xc0); return true;   // mov cr0, rax
            case "write_cr3": EmitExpr(c.Args[0]); _a.Db(0x0f, 0x22, 0xd8); return true;
            case "write_cr4": EmitExpr(c.Args[0]); _a.Db(0x0f, 0x22, 0xe0); return true;
            case "invlpg": EmitExpr(c.Args[0]); _a.Db(0x0f, 0x01, 0x38); return true;     // invlpg [rax]
            case "rdtsc": _a.Db(0x0f, 0x31); _a.Db(0x48, 0xc1, 0xe2, 0x20); _a.Db(0x48, 0x09, 0xd0); return true;  // rax = edx:eax
            case "rdmsr":
                EmitExpr(c.Args[0]); _a.MovRR(RCX, RAX); _a.Db(0x0f, 0x32);
                _a.Db(0x48, 0xc1, 0xe2, 0x20); _a.Db(0x89, 0xc0); _a.Db(0x48, 0x09, 0xd0);   // rax = edx:eax
                return true;
            case "wrmsr":
                EmitArgsTo(c, "rcx", "rdx"); _a.MovRR(RAX, RDX); _a.Db(0x48, 0xc1, 0xea, 0x20);  // eax = low, edx = high
                _a.Db(0x0f, 0x30);
                return true;
            case "lidt": case "lgdt":
                // 10-byte descriptor on the stack: limit (16 bits), base (64 bits)
                EmitArgsTo(c, "rcx", "rdx");                            // rcx = base, rdx = limit
                _a.AluRI(5, RSP, 16);
                _a.Db(0x66, 0x89, 0x14, 0x24);                          // mov [rsp], dx
                _a.Db(0x48, 0x89, 0x4c, 0x24, 0x02);                    // mov [rsp+2], rcx
                _a.Db(0x0f, 0x01, c.Name == "lidt" ? (byte)0x1c : (byte)0x14, 0x24);   // lidt/lgdt [rsp]
                _a.AluRI(0, RSP, 16);
                return true;
            case "getc": _a.CallRel("rt_getc"); return true;
        }
        return false;
    }

    // getc() -> next input byte, or -1 at end of input. Kernel: polls COM1 (what you
    // type into QEMU -nographic arrives there). Windows/Linux: one byte of stdin.
    void EmitGetc() {
        _a.Label("rt_getc");
        if (_target == Target.Kernel) {
            string wait = NewLabel("gc_wait");
            _a.Label(wait);
            _a.MovRI(RDX, 0x3fd); _a.Db(0xec);                         // in al, dx (line status)
            _a.Db(0xa8, 0x01); _a.Jcc(0x4, wait);                      // test al, 1 (data ready)
            _a.MovRI(RDX, 0x3f8); _a.Db(0x31, 0xc0); _a.Db(0xec);      // in al, dx (the byte)
            _a.Ret();
            return;
        }
        string eof = NewLabel("gc_eof"), done = NewLabel("gc_done");
        _a.PushR(RBP); _a.MovRR(RBP, RSP); _a.AndRspImm8(-16); _a.AluRI(5, RSP, 64);
        if (_target == Target.Windows) {
            _a.MovRI(RCX, -10); EmitIatCall("GetStdHandle");           // STD_INPUT_HANDLE
            _a.MovRR(RCX, RAX); _a.Lea(RDX, RSP, 56); _a.MovRI(R8, 1); _a.Lea(R9, RSP, 48);
            _a.StImm(RSP, 32, 0); _a.StImm(RSP, 48, 0);
            EmitIatCall("ReadFile");
            _a.Db(0x85, 0xc0); _a.Jcc(0x4, eof);
            _a.Ld(RAX, RSP, 48); _a.TestRR(RAX, RAX); _a.Jcc(0x4, eof);
        } else {
            _a.MovRI(RDI, 0); _a.Lea(RSI, RSP, 56); _a.MovRI(RDX, 1); _a.MovRI(RAX, 0); _a.Syscall();   // read(0, buf, 1)
            _a.AluRI(7, RAX, 1); _a.Jcc(0x5, eof);
        }
        _a.Db(0x0f, 0xb6, 0x44, 0x24, 0x38);                            // movzx eax, byte [rsp+56]
        _a.JmpRel(done);
        _a.Label(eof); _a.MovRI(RAX, -1);
        _a.Label(done);
        _a.MovRR(RSP, RBP); _a.PopR(RBP);
        _a.Ret();
    }

    void EmitIatIndex(int idx) {
        _a.Db(0x48, 0xa1); _iatRelocs.Add((_a.Pos, idx)); for (int i = 0; i < 8; i++) _a.Db(0);
        _a.CallRax();
    }
    // Win64 -> Forge adapter for a callback: Win64 passes rcx rdx r8 r9 and expects rsi/rdi
    // preserved; Forge takes rdi rsi rdx rcx and may use rsi/rdi. (System V needs none.)
    void EmitCallbackThunks() {
        if (_target != Target.Windows) return;
        foreach (var f in _mod.Funcs) {
            if (!f.IsCallback) continue;
            _a.Label(f.Name + "$c");
            _a.PushR(RSI); _a.PushR(RDI); _a.AluRI(5, RSP, 8);
            _a.MovRR(RDI, RCX); _a.MovRR(RSI, RDX); _a.MovRR(RDX, R8); _a.MovRR(RCX, R9);
            _a.CallRel(f.Name);
            _a.AluRI(0, RSP, 8); _a.PopR(RDI); _a.PopR(RSI);
            _a.Ret();
        }
    }

    // ---- structs ------------------------------------------------------------------
    // A struct value is a reference (8 bytes) to its fields in memory, laid out in
    // order with natural alignment (or none, for 'packed'), like a C struct.
    sealed class StructInfo {
        public readonly StructDecl Decl;
        public readonly Dictionary<string, (int Off, string Type)> Fields = new();
        public int Size, Align = 1;
        public StructInfo(StructDecl d) { Decl = d; }
    }
    readonly Dictionary<string, StructInfo> _structs = new();
    bool IsStruct(string t) => _structs.ContainsKey(t);
    bool KnownType(string t) {
        while (t.EndsWith("*")) t = t[..^1];
        if (t.EndsWith("[]")) t = t[..^2];
        return IntTypes.Contains(t) || t is "string" or "double" || IsStruct(t);
    }
    void LayoutStruct(StructInfo si) {
        int off = 0;
        foreach (var (ty, name) in si.Decl.Fields) {
            if (!KnownType(ty)) throw new ForgeException($"unknown type '{ty}' for field '{name}'", si.Decl.Line);
            if (si.Fields.ContainsKey(name)) throw new ForgeException($"duplicate field '{name}' in struct {si.Decl.Name}", si.Decl.Line);
            int w = Width(ty);
            if (!si.Decl.Packed) { off = (off + w - 1) / w * w; si.Align = Math.Max(si.Align, w); }
            si.Fields[name] = (off, ty);
            off += w;
        }
        si.Size = si.Decl.Packed ? off : (off + si.Align - 1) / si.Align * si.Align;
    }
    (int Off, string Type) FieldOf(string st, string name, int line) {
        var si = _structs[st];
        if (si.Fields.TryGetValue(name, out var fi)) return fi;
        throw new ForgeException($"struct {st} has no field '{name}'" + Suggest.Hint(name, si.Fields.Keys), line);
    }

    // the # keeps prefixes ending in a digit apart: "or1"+86 and "or"+186 were both .or186
    string NewLabel(string p) => $".{p}#{_labelId++}";

    // ----------------------------------------------------------------- type check
    static readonly HashSet<string> IntTypes = new() {
        "int","uint","ushort","long","bool",
        "i8","u8","i16","u16","i32","u32","i64","u64"
    };
    static bool IsInt(string t) => IntTypes.Contains(t) && t != "bool";
    static bool IsArray(string t) => t.EndsWith("[]");
    static string ElemType(string t) => t.Substring(0, t.Length - 2);
    static bool IsPtr(string t) => t.EndsWith("*");
    static bool IsStr(string t) => t == "string";
    static bool IsTask(string t) => t == "Task" || t.StartsWith("Task<");
    static string TaskResult(string t) => t == "Task" ? "void" : t.Substring(5, t.Length - 6);
    static string TaskTypeOf(Func f) => f.Ret == "void" ? "Task" : "Task<" + f.Ret + ">";
    bool CanAwaitHere => _cur.IsAsync || _cur.IsRuntime || _cur.Name == "Main";
    static void Need(CallExpr c, int n) { if (c.Args.Count != n) throw new ForgeException($"{c.Name}() expects {n} argument(s)", c.Line); }
    static void Err(CallExpr c, string m) => throw new ForgeException(m, c.Line);
    void NeedOs(CallExpr c) { if (_target == Target.Kernel) throw new ForgeException($"{c.Name}() needs an OS: use --target win or --target linux", c.Line); }
    static string Pointee(string t) => t.Substring(0, t.Length - 1);
    // byte width of a scalar type for memory access / pointer scaling
    static int Width(string t) => t switch {
        "i8" or "u8" => 1,
        "i16" or "u16" => 2,
        "i32" or "u32" => 4,
        _ => 8,                       // i64/u64/int/uint/long/ushort/bool/pointers
    };
    static bool Signed(string t) => t is "i8" or "i16" or "i32" or "i64" or "int" or "long";
    static int Log2(int n) => n == 1 ? 0 : n == 2 ? 1 : n == 4 ? 2 : 3;

    // TypeOf is called on the same nodes many times (at every level of a nested
    // expression), so results are cached per node. The cache is cleared whenever
    // _locals is rebuilt for another function (SetupLocalsForCheck, EmitFunc).
    readonly Dictionary<Expr, string> _typeCache = new(ReferenceEqualityComparer.Instance);

    // Builtins that report failure through ioerr(); `try` works on these like on a
    // `throws` function (the error is called IOError).
    static readonly HashSet<string> FallibleBuiltins = new() {
        "readfile", "writefile", "tcp_connect", "tcp_listen", "accept", "send", "recv", "closefd",
    };

    // Builtins type-checked in TypeOf's CallExpr case (async-runtime intrinsics are handled separately).
    static readonly HashSet<string> CheckedBuiltins = new() {
        "accept","atomic_add","atomic_cas","atomic_load","atomic_store","atomic_xchg","cli","closefd","cpu_count",
        "exit","getc","heapbase","heapmark","heapreset","hlt","inb","inl","invlpg","inw","join","lgdt","lidt",
        "lock","malloc","now_ns","outb","outl","outw","pause","putc","rdmsr","rdtsc","read_cr0","read_cr2",
        "read_cr3","read_cr4","readfile","recv","send","sleep_ms","sti","str","strlen","substr","tcp_connect",
        "tcp_listen","toint","unlock","write_cr0","write_cr3","write_cr4","writefile","wrmsr","ioerr","__sqrt",
    };
    string TypeOf(Expr e) {
        if (_typeCache.TryGetValue(e, out var cached)) return cached;
        var t = TypeOfUncached(e);
        _typeCache[e] = t;
        return t;
    }

    string TypeOfUncached(Expr e) {
        switch (e) {
            case IncDecExpr id:
                throw new ForgeException($"'{id.Op}' can only be used as a whole statement (x{id.Op};) or as a for-loop step, not inside an expression", id.Line);
            case IntLit:  return "int";
            case FloatLit:return "double";
            case StrLit:  return "string";
            case BoolLit: return "bool";
            case VarRef v:
                if (_locals.TryGetValue(v.Name, out var info)) return info.Type;
                throw new ForgeException($"use of undeclared variable '{v.Name}'"
                    + Suggest.Hint(v.Name, _locals.Keys), v.Line);
            case Unary u: {
                var t = TypeOf(u.E);
                if (u.Op == "!") { if (t != "bool") throw new ForgeException("'!' needs a bool", u.Line); return "bool"; }
                if (!IsInt(t) && t != "double") throw new ForgeException("unary '-' needs a number", u.Line);
                return t;
            }
            case NullLit: return "null";
            case SizeOfExpr so:
                if (!KnownType(so.Type)) throw new ForgeException($"unknown type '{so.Type}' in sizeof", so.Line);
                return "int";
            case NewStruct ns: {
                if (!IsStruct(ns.Type)) throw new ForgeException($"unknown struct '{ns.Type}'", ns.Line);
                for (int k = 0; k < ns.Inits.Count; k++) {
                    var (fn, fe) = ns.Inits[k];
                    var (_, ft) = FieldOf(ns.Type, fn, ns.Line);
                    var et = TypeOf(fe);
                    if (!Assignable(ft, et)) throw new ForgeException($"field '{fn}' is {ft}, got {et}", ns.Line);
                    ns.Inits[k] = (fn, Coerce(fe, ft));
                }
                return ns.Type;
            }
            case MemberExpr me: {
                var ot = TypeOf(me.Obj);
                if (me.Name is "length" or "Length" && (IsArray(ot) || IsStr(ot))) return "int";   // C# spells it Length
                if (!IsStruct(ot)) throw new ForgeException($"'.{me.Name}' needs a struct, got {ot}", me.Line);
                return FieldOf(ot, me.Name, me.Line).Type;
            }
            case Binary b: {
                var lt = TypeOf(b.L); var rt = TypeOf(b.R);
                if (b.Op is "==" or "!=" && (lt == "null" && Assignable(rt, "null") || rt == "null" && Assignable(lt, "null")))
                    return "bool";                      // reference compared with null
                if (b.Op is "&&" or "||") {
                    if (lt != "bool" || rt != "bool") throw new ForgeException($"'{b.Op}' needs bool operands", b.Line);
                    return "bool";
                }
                if (lt == "double" || rt == "double") {
                    // double arithmetic and comparisons; an int operand is converted
                    if (!IsNum(lt) || !IsNum(rt)) {
                        if (b.Op is "==" or "!=") throw new ForgeException($"cannot compare {lt} with {rt}", b.Line);
                        throw new ForgeException($"'{b.Op}' needs numbers, got {lt} and {rt}", b.Line);
                    }
                    if (lt != "double") b.L = ToDouble(b.L);
                    if (rt != "double") b.R = ToDouble(b.R);
                    if (b.Op is "==" or "!=" or "<" or ">" or "<=" or ">=") return "bool";
                    if (b.Op is "+" or "-" or "*" or "/") return "double";
                    throw new ForgeException($"'{b.Op}' is not defined for double (only + - * / and comparisons)", b.Line);
                }
                if (b.Op is "==" or "!=") {
                    if (lt == rt || (IsInt(lt) && IsInt(rt))) return "bool";
                    throw new ForgeException($"cannot compare {lt} with {rt}", b.Line);
                }
                if (b.Op is "<" or ">" or "<=" or ">=") {
                    if (IsInt(lt) && IsInt(rt)) return "bool";
                    throw new ForgeException($"'{b.Op}' needs numeric operands", b.Line);
                }
                // string concatenation:  string + string -> string
                if (b.Op == "+" && IsStr(lt) && IsStr(rt)) return "string";
                // pointer arithmetic:  ptr + int  /  ptr - int  -> ptr (scaled by pointee)
                if (b.Op is "+" or "-" && IsPtr(lt) && IsInt(rt)) return lt;
                if (b.Op == "+" && IsInt(lt) && IsPtr(rt)) return rt;
                if (!IsInt(lt) || !IsInt(rt)) throw new ForgeException($"'{b.Op}' needs integers", b.Line);
                return "int";
            }
            case CallExpr c: {
                if (_externs.TryGetValue(c.Name, out var exd)) {
                    if (c.Args.Count != exd.Params.Count)
                        throw new ForgeException($"'{c.Name}' expects {exd.Params.Count} argument(s), got {c.Args.Count}", c.Line);
                    for (int k = 0; k < c.Args.Count; k++) {
                        var at = TypeOf(c.Args[k]);
                        if (!FfiType(at)) throw new ForgeException($"argument {k + 1} of '{c.Name}': cannot pass {at} to C", c.Line);
                    }
                    return exd.Ret;
                }
                if (c.Name.StartsWith("Math.")) ResolveMathCall(c);   // Math.Sqrt(x) -> __sqrt, Math.Sin(x) -> __math_sin, ...
                if (c.Name is "__hash" or "__less") ResolveLibDispatch(c);   // by argument type (StdLib.cs)
                // a generic function: instantiate it (Generics.cs) and call the instance
                if (c.TypeArgs != null || (!_funcs.ContainsKey(c.Name) && _genericFuncs.ContainsKey(c.Name))) ResolveGenericCall(c);
                if (c.Name.StartsWith(".") && IsStr(TypeOf(c.Args[0]))) ResolveStringMethod(c);   // s.Contains(x) -> __str_contains(s, x)
                if (c.Name.StartsWith(".")) {            // obj.Method(...) -> Struct.Method(obj, ...)
                    var ot = TypeOf(c.Args[0]);
                    if (!IsStruct(ot)) throw new ForgeException($"'{c.Name[1..]}()' needs a struct, got {ot}", c.Line);
                    string mname = ot + c.Name;
                    // List<T>.Sort compares elements with <: say so here rather than inside the library
                    if (c.Name == ".Sort" && _structInstances.TryGetValue(ot, out var lsi) && lsi.Template.Name == "List"
                        && lsi.Map.Count == 1 && !IsNum(lsi.Map.Values.First()) && !IsStr(lsi.Map.Values.First()))
                        throw new ForgeException($"{ot}.Sort() needs elements that can be compared (numbers or strings), not {lsi.Map.Values.First()}", c.Line);
                    if (!_funcs.ContainsKey(mname) && !InstantiateMethod(ot, c.Name[1..]))
                        throw new ForgeException($"struct {ot} has no method '{c.Name[1..]}'" + Suggest.Hint(c.Name[1..], MethodsOf(ot)), c.Line);
                    c.Name = mname;
                }
                if (_funcs.ContainsKey(c.Name)) goto userFunc;   // a program's own function shadows a builtin of the same name
                // Each builtin below checks the arguments it cares about; make sure every
                // argument type-checks regardless, so none reaches codegen unchecked.
                if (CheckedBuiltins.Contains(c.Name)) foreach (var a in c.Args) TypeOf(a);
                if (c.Name == "malloc") {
                    if (c.Args.Count != 1 || !IsInt(TypeOf(c.Args[0])))
                        throw new ForgeException("malloc(n) takes one integer (byte count)", c.Line);
                    return "u8*";
                }
                if (c.Name == "heapbase") {
                    if (c.Args.Count != 0) throw new ForgeException("heapbase() takes no arguments", c.Line);
                    return "u8*";
                }
                if (c.Name == "heapreset") {
                    if (c.Args.Count > 1 || (c.Args.Count == 1 && !IsInt(TypeOf(c.Args[0]))))
                        throw new ForgeException("heapreset() or heapreset(mark): mark comes from heapmark()", c.Line);
                    return "void";
                }
                if (c.Name == "heapmark") { Need(c, 0); return "int"; }
                if (c.Name == "__sqrt") {                 // Math.Sqrt: the sqrtsd instruction
                    Need(c, 1); c.Args[0] = Coerce(c.Args[0], "double");
                    if (TypeOf(c.Args[0]) != "double") Err(c, "Math.Sqrt(x) takes a number");
                    return "double";
                }
                // ---- bare metal: ports, interrupts, control registers, MSRs, descriptor tables
                if (c.Name is "outb" or "outw" or "outl") { Need(c, 2); if (!IsInt(TypeOf(c.Args[0])) || !IsInt(TypeOf(c.Args[1]))) Err(c, $"{c.Name}(port, value)"); return "void"; }
                if (c.Name is "inb" or "inw" or "inl") { Need(c, 1); if (!IsInt(TypeOf(c.Args[0]))) Err(c, $"{c.Name}(port)"); return "int"; }
                if (c.Name is "cli" or "sti" or "hlt" or "pause") { Need(c, 0); return "void"; }
                if (c.Name is "read_cr0" or "read_cr2" or "read_cr3" or "read_cr4" or "rdtsc") { Need(c, 0); return "int"; }
                if (c.Name is "write_cr0" or "write_cr3" or "write_cr4") { Need(c, 1); if (!IsInt(TypeOf(c.Args[0]))) Err(c, $"{c.Name}(value)"); return "void"; }
                if (c.Name == "invlpg") { Need(c, 1); var t = TypeOf(c.Args[0]); if (!IsInt(t) && !IsPtr(t)) Err(c, "invlpg(address)"); return "void"; }
                if (c.Name == "rdmsr") { Need(c, 1); if (!IsInt(TypeOf(c.Args[0]))) Err(c, "rdmsr(msr)"); return "int"; }
                if (c.Name == "wrmsr") { Need(c, 2); if (!IsInt(TypeOf(c.Args[0])) || !IsInt(TypeOf(c.Args[1]))) Err(c, "wrmsr(msr, value)"); return "void"; }
                if (c.Name is "lidt" or "lgdt") { Need(c, 2); var t = TypeOf(c.Args[0]); if ((!IsInt(t) && !IsPtr(t) && !IsStruct(t)) || !IsInt(TypeOf(c.Args[1]))) Err(c, $"{c.Name}(base, limit)"); return "void"; }
                if (c.Name == "getc") { Need(c, 0); return "int"; }
                // ---- string / IO / socket builtins ----
                if (c.Name == "str")     {
                    Need(c, 1); var st0 = TypeOf(c.Args[0]);
                    if (st0 == "double") { c.Name = "__dtoa"; return "string"; }   // the double runtime formats it
                    if (!IsInt(st0) && st0 != "bool" && !IsPtr(st0)) Err(c, "str(n) takes a number");
                    return "string";
                }
                if (c.Name == "toint")   { Need(c, 1); if (!IsStr(TypeOf(c.Args[0]))) Err(c, "toint(s) takes a string"); return "int"; }
                if (c.Name == "strlen")  { Need(c, 1); if (!IsStr(TypeOf(c.Args[0]))) Err(c, "strlen(s) takes a string"); return "int"; }
                if (c.Name == "substr")  { Need(c, 3); if (!IsStr(TypeOf(c.Args[0])) || !IsInt(TypeOf(c.Args[1])) || !IsInt(TypeOf(c.Args[2]))) Err(c, "substr(s,start,len)"); return "string"; }
                if (c.Name == "readfile"){ Need(c, 1); NeedOs(c); if (!IsStr(TypeOf(c.Args[0]))) Err(c, "readfile(path) takes a string"); return "string"; }
                if (c.Name == "writefile"){ Need(c, 2); NeedOs(c); if (!IsStr(TypeOf(c.Args[0])) || !IsStr(TypeOf(c.Args[1]))) Err(c, "writefile(path, contents)"); return "int"; }
                if (c.Name == "tcp_connect"){ Need(c, 2); NeedOs(c); if (!IsStr(TypeOf(c.Args[0])) || !IsInt(TypeOf(c.Args[1]))) Err(c, "tcp_connect(host, port)"); return "int"; }
                if (c.Name == "tcp_listen"){ Need(c, 1); NeedOs(c); if (!IsInt(TypeOf(c.Args[0]))) Err(c, "tcp_listen(port)"); return "int"; }
                if (c.Name == "accept")  { Need(c, 1); NeedOs(c); if (!IsInt(TypeOf(c.Args[0]))) Err(c, "accept(serverfd)"); return "int"; }
                if (c.Name == "send")    { Need(c, 2); NeedOs(c); if (!IsInt(TypeOf(c.Args[0])) || !IsStr(TypeOf(c.Args[1]))) Err(c, "send(fd, string)"); return "int"; }
                if (c.Name == "recv")    { Need(c, 2); NeedOs(c); if (!IsInt(TypeOf(c.Args[0])) || !IsInt(TypeOf(c.Args[1]))) Err(c, "recv(fd, maxbytes)"); return "string"; }
                if (c.Name == "closefd") { Need(c, 1); NeedOs(c); if (!IsInt(TypeOf(c.Args[0]))) Err(c, "closefd(fd)"); return "int"; }
                if (c.Name == "join") {
                    if (c.Args.Count != 1 || !IsInt(TypeOf(c.Args[0])))
                        throw new ForgeException("join(handle) takes one thread handle", c.Line);
                    NeedOs(c);
                    return "int";    // the thread function's return value (0 for void)
                }
                // ---- threads: atomics, locks, cpu count, sleep ----
                if (c.Name is "atomic_add" or "atomic_xchg" or "atomic_store") {
                    Need(c, 2); if (!IsPtr(TypeOf(c.Args[0])) || !IsInt(TypeOf(c.Args[1]))) Err(c, $"{c.Name}(int* p, int v)");
                    return c.Name == "atomic_store" ? "void" : "int";
                }
                if (c.Name == "atomic_cas") {
                    Need(c, 3); if (!IsPtr(TypeOf(c.Args[0])) || !IsInt(TypeOf(c.Args[1])) || !IsInt(TypeOf(c.Args[2]))) Err(c, "atomic_cas(int* p, int expected, int desired)");
                    return "bool";
                }
                if (c.Name is "atomic_load" or "lock" or "unlock") {
                    Need(c, 1); if (!IsPtr(TypeOf(c.Args[0]))) Err(c, $"{c.Name}(int* p)");
                    if (c.Name is "lock" or "unlock") NeedOs(c);
                    return c.Name == "atomic_load" ? "int" : "void";
                }
                if (c.Name == "cpu_count") { Need(c, 0); NeedOs(c); return "int"; }
                if (c.Name == "now_ns")    { Need(c, 0); NeedOs(c); return "int"; }   // monotonic clock, nanoseconds
                if (c.Name == "ioerr")     { Need(c, 0); NeedOs(c); return "int"; }   // error code of the last I/O builtin, 0 = ok
                if (c.Name == "sleep_ms")  { Need(c, 1); NeedOs(c); if (!IsInt(TypeOf(c.Args[0]))) Err(c, "sleep_ms(milliseconds)"); return "void"; }
                if (c.Name == "exit") {
                    if (c.Args.Count != 1 || !IsInt(TypeOf(c.Args[0])))
                        throw new ForgeException("exit(code) takes one integer", c.Line);
                    return "void";
                }
                if (AsyncRuntime.Intrinsics.Contains(c.Name)) {
                    if (!_cur.IsRuntime) throw new ForgeException($"'{c.Name}' is internal to the async runtime", c.Line);
                    foreach (var a in c.Args) TypeOf(a);
                    return c.Name switch { "__sched" => "i64*", "__ctx_init" => "i64", _ => "void" };
                }
                if (c.Name == "putc") {
                    if (c.Args.Count != 1 || !IsInt(TypeOf(c.Args[0])))
                        throw new ForgeException("putc(ch) takes one integer (byte to emit)", c.Line);
                    return "void";
                }
                userFunc:
                if (!_funcs.TryGetValue(c.Name, out var f)) throw new ForgeException($"unknown function '{c.Name}'" + Suggest.Hint(c.Name, _funcs.Keys), c.Line);
                if (f.Throws != null) throw new ForgeException($"'{c.Name}' can throw - call it with 'try'", c.Line);
                CheckArgs(f, c);
                if (f.IsAsync) return TaskTypeOf(f);   // calling an async function yields a Task
                return f.Ret;
            }
            case TryExpr t: {
                if (!_funcs.ContainsKey(t.Call.Name) && CheckedBuiltins.Contains(t.Call.Name)) {
                    // try on an I/O builtin: it fails when it sets ioerr (see FallibleBuiltins)
                    if (!FallibleBuiltins.Contains(t.Call.Name))
                        throw new ForgeException($"'{t.Call.Name}' cannot fail - remove 'try'", t.Line);
                    var bt = TypeOf(t.Call);
                    if (t.Else == null && _cur.Throws == null)
                        throw new ForgeException("a bare 'try' propagates - the enclosing function must declare 'throws'", t.Line);
                    if (t.Else != null) { var et = TypeOf(t.Else); if (!Assignable(bt, et)) throw new ForgeException($"'else' fallback must be {bt}", t.Line); }
                    return bt;
                }
                if (!_funcs.TryGetValue(t.Call.Name, out var f)) throw new ForgeException($"unknown function '{t.Call.Name}'", t.Line);
                if (f.Throws == null) throw new ForgeException($"'{t.Call.Name}' does not throw - remove 'try'", t.Line);
                CheckArgs(f, t.Call);
                if (t.Else == null && _cur.Throws == null)
                    throw new ForgeException("a bare 'try' propagates - the enclosing function must declare 'throws'", t.Line);
                if (t.Else != null) { var et = TypeOf(t.Else); if (!Assignable(f.Ret, et)) throw new ForgeException($"'else' fallback must be {f.Ret}", t.Line); t.Else = Coerce(t.Else, f.Ret); }
                return f.Ret;
            }
            case Interp itp: {
                for (int k = 0; k < itp.Parts.Count; k++) {
                    if (itp.Parts[k] is not ValueTuple<Expr, string?> hole) continue;
                    // holes must type-check too; a double hole becomes a string from the runtime
                    if (TypeOf(hole.Item1) == "double") itp.Parts[k] = (DoubleText(hole.Item1, hole.Item2, itp.Line), (string?)null);
                }
                return "string";
            }
            case NewArray na: {
                var st = TypeOf(na.Size);
                if (!IsInt(st)) throw new ForgeException("array size must be an integer", na.Line);
                if (!IntTypes.Contains(na.ElemType) && !IsStruct(na.ElemType) && na.ElemType is not ("string" or "double")) throw new ForgeException($"cannot make an array of {na.ElemType}", na.Line);
                return na.ElemType + "[]";
            }
            case IndexExpr ix: {
                var at = TypeOf(ix.Arr);
                if (!IsInt(TypeOf(ix.Index))) throw new ForgeException("index must be an integer", ix.Line);
                if (IsStr(at)) return "int";        // s[i] -> byte value
                if (IsArray(at)) return ElemType(at);
                if (IsPtr(at)) return Pointee(at);
                throw new ForgeException("indexing a non-array, non-pointer value", ix.Line);
            }
            case LengthExpr le: {
                var at = TypeOf(le.Arr);
                if (IsStr(at)) return "int";        // strlen
                if (!IsArray(at)) throw new ForgeException("'.length' on a non-array value", le.Line);
                return "int";
            }
            case AddrOf ao when !_locals.ContainsKey(ao.Name) && _funcs.TryGetValue(ao.Name, out var cbf): {
                if (cbf.IsInterrupt) return "u8*";
                if (!cbf.IsCallback) throw new ForgeException($"'&{ao.Name}': only extern functions (callable from C) have an address; declare it 'extern {cbf.Ret} {ao.Name}(...) {{ }}'", ao.Line);
                return "u8*";
            }
            case AddrOf ao: {
                if (!_locals.TryGetValue(ao.Name, out var li))
                    throw new ForgeException($"'&' on undeclared variable '{ao.Name}'"
                        + Suggest.Hint(ao.Name, _locals.Keys), ao.Line);
                return li.Type + "*";
            }
            case Deref de: {
                var pt = TypeOf(de.Ptr);
                if (!IsPtr(pt)) throw new ForgeException("dereferencing a non-pointer", de.Line);
                return Pointee(pt);
            }
            case CastExpr ce: {
                var st = TypeOf(ce.E);
                if (st == "double" || ce.TargetType == "double") {
                    // a real conversion: (double)i, (int)d (truncates toward zero, like C#)
                    if (!IsNum(st) || !(IsInt(ce.TargetType) || ce.TargetType == "double"))
                        throw new ForgeException($"cannot cast {st} to {ce.TargetType}", ce.Line);
                    return ce.TargetType;
                }
                if (!IsInt(st) && !IsPtr(st) && !IsTask(st) && !IsStruct(st) && !IsArray(st) && !IsStr(st) && st != "null")
                    throw new ForgeException("can only cast integers, pointers, arrays, strings, struct references and tasks", ce.Line);
                if (!KnownType(ce.TargetType) && !IsTask(ce.TargetType)) throw new ForgeException($"unknown type '{ce.TargetType}'", ce.Line);
                return ce.TargetType;     // reinterpret
            }
            case AwaitExpr aw: {
                RequireAwaitContext(aw.Line);
                var tt = TypeOf(aw.Task);
                if (!IsTask(tt)) throw new ForgeException($"'await' needs a Task, got {tt}", aw.Line);
                return TaskResult(tt);
            }
            case YieldExpr y: {
                RequireAwaitContext(y.Line);
                return "void";
            }
            case WhenAllExpr wa: {
                RequireAwaitContext(wa.Line);
                foreach (var t in wa.Tasks) {
                    var tt = TypeOf(t);
                    if (!IsTask(tt)) throw new ForgeException($"Task.WhenAll expects tasks, got {tt}", wa.Line);
                }
                return "void";
            }
            case InlineExpr ie: return ie.ResultType;
            case SpawnExpr sp: {
                if (_target == Target.Kernel)
                    throw new ForgeException("spawn (threads) needs an OS: use --target win or --target linux", sp.Line);
                if (!_funcs.TryGetValue(sp.FuncName, out var tf))
                    throw new ForgeException($"spawn of unknown function '{sp.FuncName}'" + Suggest.Hint(sp.FuncName, _funcs.Keys), sp.Line);
                if (tf.IsAsync || tf.Throws != null)
                    throw new ForgeException($"spawned function '{sp.FuncName}' cannot be async or declare 'throws'", sp.Line);
                if (tf.Params.Count > 1)
                    throw new ForgeException($"spawned function '{sp.FuncName}' can take at most one parameter (pass a pointer or array for more)", sp.Line);
                if (tf.Params.Count == 1 && sp.Arg == null)
                    throw new ForgeException($"'{sp.FuncName}' takes a parameter: spawn {sp.FuncName}(value)", sp.Line);
                if (tf.Params.Count == 0 && sp.Arg != null)
                    throw new ForgeException($"'{sp.FuncName}' takes no parameters", sp.Line);
                if (sp.Arg != null) {
                    var at = TypeOf(sp.Arg); var pt = tf.Params[0].Type;
                    if (pt == "double" && IsInt(at)) { sp.Arg = ToDouble(sp.Arg); at = "double"; }
                    bool fits = at == pt || (IsInt(at) && IsInt(pt)) || (IsPtr(pt) && IsPtr(at)) || (IsPtr(pt) && IsInt(at));
                    if (!fits) throw new ForgeException($"spawn {sp.FuncName}: argument is {at}, parameter is {pt}", sp.Line);
                }
                return "long";   // thread handle
            }
        }
        throw new ForgeException("unsupported expression", e.Line);
    }

    static string AwaitHint(string want, string got) =>
        IsTask(got) && !IsTask(want) && Assignable(want, TaskResult(got)) ? $", got {got} (did you forget 'await'?)"
        : IsInt(want) && got == "double" ? $", got double (convert with ({want})x, which truncates toward zero)" : "";

    void RequireAwaitContext(int line) {
        if (!CanAwaitHere)
            throw new ForgeException($"'await' can only be used inside an 'async' function (or Main); mark '{_cur.Name}' as async", line);
    }

    void CheckArgs(Func f, CallExpr c) {
        if (c.Args.Count != f.Params.Count)
            throw new ForgeException($"'{f.Name}' expects {f.Params.Count} arg(s), got {c.Args.Count}", c.Line);
        for (int i = 0; i < c.Args.Count; i++) {
            var at = TypeOf(c.Args[i]);
            if (!Assignable(f.Params[i].Type, at))
                throw new ForgeException($"argument '{f.Params[i].Name}': expected {f.Params[i].Type}, got {at}", c.Line);
            c.Args[i] = Coerce(c.Args[i], f.Params[i].Type);
        }
    }
    static bool IsNum(string t) => IsInt(t) || t == "double";

    // An int used where a double is expected: a literal becomes a double literal,
    // anything else gets an explicit conversion (cvtsi2sd).
    static Expr ToDouble(Expr e) => e is IntLit il
        ? new FloatLit { Line = il.Line, Val = il.Val }
        : new CastExpr { Line = e.Line, TargetType = "double", E = e };
    Expr Coerce(Expr e, string want) => want == "double" && IsInt(TypeOf(e)) ? ToDouble(e) : e;

    // the text of a double: $"{x}" / str(x) / Console.WriteLine(x) use 15 significant
    // digits; a hole like {x:F2} gets exactly that many decimals.
    Expr DoubleText(Expr e, string? fmt, int line) {
        if (fmt != null && fmt.Length >= 2 && (fmt[0] is 'F' or 'f') && int.TryParse(fmt[1..], out int places) && places is >= 0 and <= 15)
            return new CallExpr { Line = line, Name = "__dtoaf", Args = { e, new IntLit { Line = line, Val = places } } };
        if (fmt != null && fmt.Length > 0)
            throw new ForgeException($"unknown format '{fmt}' for a double (use F0..F15, e.g. {{x:F2}})", line);
        return new CallExpr { Line = line, Name = "__dtoa", Args = { e } };
    }

    static bool Assignable(string want, string got) =>
        want == got || (IsInt(want) && IsInt(got)) || (want == "double" && IsInt(got))
        || (got == "null" && !IntTypes.Contains(want) && want is not ("void" or "double"))
        || (want == "Task" && IsTask(got));     // Task<T> is a Task (result ignored)

    // ----------------------------------------------------------------- string data
    int InternString(string s) {
        var raw = Encoding.UTF8.GetBytes(s);
        var bytes = new byte[raw.Length + 1];      // null-terminated for runtime strings
        Array.Copy(raw, bytes, raw.Length);
        int off = _dataLen;
        _data.Add(bytes); _dataOffsets.Add(off);
        _dataLen += bytes.Length;
        return off;
    }
    long StrAddr(int off) => _dataVaddr + off;
    int InternZeros(int n) {
        int off = _dataLen;
        _data.Add(new byte[n]); _dataOffsets.Add(off);
        _dataLen += n;
        return off;
    }

    // ----------------------------------------------------------------- entry
    // Type-check and run static analyses (populates Warnings). No code emission.
    public void Analyze() {
        try {
            if (!_funcs.ContainsKey("Main")) Report(new ForgeException("no Main function found", 1), null);
            else {
                var main = _funcs["Main"];
                if (main.Ret != "void" || main.Params.Count != 0 || main.Throws != null)
                    Report(new ForgeException("Main must be: void Main()", main.Line), null);
            }

            for (int fi = 0; fi < _mod.Funcs.Count; fi++) {     // by index: checking a call can add a generic instance
                var f = _mod.Funcs[fi];
                int before = Errors.Count;
                _poisoned.Clear(); _poisonSources.Clear();
                Recover(null, () => {
                    if (f.IsAsync && f.Throws != null)
                        throw new ForgeException($"async function '{f.Name}' cannot declare 'throws' (not supported yet)", f.Line);
                    if (f.IsAsync && f.Params.Count > 4)
                        throw new ForgeException($"async function '{f.Name}' can take at most 4 parameters", f.Line);
                });
                _cur = f;
                Recover(null, () => SetupLocalsForCheck(f));
                f.Body = LowerBody(f.Body);
                CheckBody(f.Body);
                if (Errors.Count > before) continue;    // return-path check and lints only on clean functions
                Recover(null, () => CheckAllPathsReturn(f));
                if (f.IsRuntime) continue;          // compiler-supplied: no lint warnings
                CheckUnreachable(f.Body);
                CheckUnusedLocals(f);
            }
        } catch (TooManyErrors) { }
        // generic instances share their lines with the template: report each warning once
        var seenW = new HashSet<(int, string)>();
        Warnings.RemoveAll(w => !seenW.Add((w.Line, w.Message)));
        if (Errors.Count > 0) throw new ForgeErrors(Errors.OrderBy(e => Sources.Order(e.Line)).ToList());
    }

    // Run one check; a ForgeException becomes a collected error instead of aborting.
    void Recover(Stmt? at, Action check) {
        try { check(); }
        catch (ForgeException fe) { Report(fe, at); }
    }
    void Report(ForgeException fe, Stmt? at) {
        // A failed declaration ignores its own name here: `var t = f(t)` must still
        // report, but `var b = a + 1` after a failed `a` is follow-on noise.
        string? own = at == null || !_poisonSources.Contains(at) ? null : at is VarDecl vd ? vd.Name : at is ForeachStmt fo ? fo.Name : null;
        if (at != null && _poisoned.Count > 0 && MentionsPoisoned(at, own)) return;
        string msg = _cur?.InstanceOf != null && !fe.Message.Contains(" (in ") ? $"{fe.Message} (in {_cur.Name})" : fe.Message;
        if (Errors.Any(e => e.Line == fe.Line && e.Message == msg)) return;
        Errors.Add(new Diagnostic(Severity.Error, fe.Line, msg));
        if (Errors.Count >= MaxErrors) throw new TooManyErrors();
    }
    bool MentionsPoisoned(Stmt s, string? except) {
        bool P(string n) => n != except && _poisoned.Contains(n);
        bool hit = false;
        Walker.WalkStmt(s, e => {
            if (e is VarRef vr && P(vr.Name)) hit = true;
            else if (e is AddrOf ao && P(ao.Name)) hit = true;
        }, st => { if (st is Assign a && P(a.Name)) hit = true; });
        return hit;
    }
    sealed class TooManyErrors : Exception { }

    public byte[] Compile() {
        Analyze();
        if (OptimizeEnabled) Optimize();   // inlining, tail recursion, folding (Optimizer.cs)

        // ---- emit: _start trampoline calls Main then exits ----
        _a.Label("_start");
        if (_target == Target.Windows) {
            _a.AndRspImm8(-16);        // align stack for the Win64 world
            _a.CallRel("win_init");    // fetch + store the stdout handle
        }
        if (_target == Target.Kernel) {
            _a.Db(0xfa);               // cli  -- no IDT yet; keep IRQs masked
            _a.CallRel("serial_init"); // bring up COM1
        }
        // initialize the heap pointer. The heap's first 4 KB are reserved:
        //   [heapBase + 0 .. 64)    print_int scratch buffer
        //   [heapBase + 64 .. 4096) the user's heapbase() shared region
        // so neither can overwrite (or be overwritten by) malloc'd memory.
        EmitSlotAddr(0xb8, 3);         // mov rax, imm64(heap_base)
        _a.Db(0x48, 0x05); EmitImm32(HeapReserved); // add rax, imm32
        EmitSlotAddr(0xa3, 2);         // mov [heapPtr], rax
        if (Threaded) _a.CallRel("rt_init");   // main thread's arena + TLS (Threads.cs)
        _a.CallRel("Main");
        if (_usesAsync) _a.CallRel("__rt_drain");   // finish fire-and-forget tasks
        if (_target == Target.Linux) {
            _a.MovRaxImm(231);         // exit_group: ends every thread
            _a.MovRdiImm(0);
            _a.Syscall();
        } else if (_target == Target.Kernel) {
            // nothing to return to: mask interrupts and spin forever.
            // A 2-byte self-jump can never fault (unlike hlt with no IDT).
            _a.Db(0xfa);               // cli
            _a.Db(0xeb, 0xfe);         // jmp $  (infinite self-loop)
        } else {
            // ExitProcess(0)
            _a.AndRspImm8(-16);
            _a.SubRspImm8(32);         // shadow space
            _a.MovRcxImm(0);
            EmitIatCall("ExitProcess");            // ExitProcess
        }

        foreach (var f in _mod.Funcs) EmitFunc(f);
        EmitCallbackThunks();          // Win64 entry points for extern (C-callable) functions
        EmitInterruptStubs();          // CPU entry points for interrupt handlers
        EmitGetc();
        if (_usesAsync) EmitAsyncAsm(); // context switch + fiber entry
        if (Threaded) { EmitAllocThreaded(); EmitThreadRuntime(); }   // per-thread arenas (Threads.cs)
        else EmitAlloc();          // heap bump allocator (kernel)
        EmitRuntime();             // string / file / socket runtime (Linux syscalls)
        EmitWriteOut();            // unified stdout writer (rsi=ptr, rdx=len)
        if (_target == Target.Windows) EmitWinInit();
        if (_target == Target.Kernel) EmitSerial();
        EmitBoundsStubs();         // any left over from code emitted outside a function
        EmitBoundsFail();          // the shared out-of-range report (only if an array is indexed)
        EmitPrintInt();            // helper: prints rdi as signed decimal
        EmitPrintStr();
        _a.Resolve();
        return _a.Code.ToArray();
    }

    // pre-scan to assign local slots (so type checking can see declared types)
    void SetupLocalsForCheck(Func f) {
        _locals = new(); _frame = 0; _typeCache.Clear();
        foreach (var (t, n) in f.Params) AddLocal(n, t);
        PrescanDecls(f.Body);
    }
    void PrescanDecls(List<Stmt> body) {
        foreach (var s in body) {
            switch (s) {
                case VarDecl v: {
                    string t;
                    try { t = v.DeclType ?? TypeOf(v.E); }
                    catch (ForgeException) { _poisoned.Add(v.Name); _poisonSources.Add(v); break; }   // reported when the statement is checked
                    AddLocal(v.Name, t);
                    break;
                }
                case IfStmt i: PrescanDecls(i.Then); if (i.Else != null) PrescanDecls(i.Else); break;
                case WhileStmt w: PrescanDecls(w.Body); break;
                case ForeachStmt fo: {
                    string? et = fo.DeclType;
                    if (et == null) { try { et = ForeachElemType(TypeOf(fo.Coll)); } catch (ForgeException) { } }
                    if (et != null) AddLocal(fo.Name, et); else { _poisoned.Add(fo.Name); _poisonSources.Add(fo); }   // reported when the loop is lowered
                    PrescanDecls(fo.Body);
                    break;
                }
                case ForStmt f: {
                    if (f.Init is VarDecl fv) AddLocal(fv.Name, fv.DeclType ?? TypeOf(fv.E));
                    PrescanDecls(f.Body);
                    break;
                }
            }
        }
    }
    // ---- lowering of struct syntax to plain memory access (runs in Analyze, after the
    // locals' types are known):  obj.f  ->  *(T*)((u8*)obj + offset),  obj.f = v  ->
    // a store through that pointer,  sizeof(T) -> constant,  arr.length -> LengthExpr.
    // Lowering errors are collected per statement (see Recover), so nested blocks keep going too.
    // ---- foreach: element type of what can be iterated (array, string, List<T>)
    string ForeachElemType(string ct) {
        if (IsArray(ct)) return ElemType(ct);
        if (IsStr(ct)) return "int";                                       // the bytes of a string
        if (_structInstances.TryGetValue(ct, out var si) && si.Template.Name == "List" && si.Map.Count == 1)
            return si.Map.Values.First();
        throw new ForgeException($"foreach needs an array, a string or a List<T>, got {ct}", 0);
    }

    // foreach (T x in coll) body   ->   var c = coll;
    //                                   for (long i = 0; i < c.length (or c.Count); i++) { T x = c[i] (or c.Get(i)); body }
    // break/continue labels were placed by the parser (continue at the end of body, so i++ still runs).
    int _foreachSeq;
    Stmt LowerForeach(ForeachStmt fe) {
        string ct = TypeOf(fe.Coll);
        string elem;
        try { elem = ForeachElemType(ct); }
        catch (ForgeException ex) { throw new ForgeException(ex.Message, fe.Line); }
        if (fe.DeclType != null && !Assignable(fe.DeclType, elem))
            throw new ForgeException($"foreach variable '{fe.Name}' is {fe.DeclType}, but the elements are {elem}", fe.Line);
        string vt = fe.DeclType ?? elem;
        int id = ++_foreachSeq;
        string cn = $"fe$c{id}", ix = $"fe$i{id}";
        AddLocal(cn, ct); AddLocal(ix, "long"); AddLocal(fe.Name, vt);
        int ln = fe.Line;
        VarRef C() => new VarRef { Line = ln, Name = cn };
        VarRef I() => new VarRef { Line = ln, Name = ix };
        bool list = !IsArray(ct) && !IsStr(ct);
        Expr len = new MemberExpr { Line = ln, Obj = C(), Name = list ? "Count" : "length" };
        Expr item = list ? new CallExpr { Line = ln, Name = ".Get", Args = { C(), I() } } : new IndexExpr { Line = ln, Arr = C(), Index = I() };
        var body = new List<Stmt> { new VarDecl { Line = ln, DeclType = vt, Name = fe.Name, E = item } };
        body.AddRange(fe.Body);
        var loop = new ForStmt {
            Line = ln,
            Init = new VarDecl { Line = ln, DeclType = "long", Name = ix, E = new IntLit { Line = ln, Val = 0 } },
            Cond = new Binary { Line = ln, Op = "<", L = I(), R = len },
            Step = new Assign { Line = ln, Name = ix, E = new Binary { Line = ln, Op = "+", L = I(), R = new IntLit { Line = ln, Val = 1 } } },
            Body = body,
        };
        var stmts = new List<Stmt> { new VarDecl { Line = ln, DeclType = ct, Name = cn, E = fe.Coll }, loop };
        return new BlockStmt { Line = ln, Body = LowerBody(stmts) };
    }

    // (a statement may lower to several: a BlockStmt is flattened into the body)
    List<Stmt> LowerBody(List<Stmt> b) => b.SelectMany(s => { Stmt r = s; Recover(s, () => r = LowerStmt(s)); return r is BlockStmt bl ? bl.Body : new List<Stmt> { r }; }).ToList();
    Stmt LowerStmt(Stmt st) {
        switch (st) {
            case ForeachStmt fe: return LowerForeach(fe);
            case MemberAssign ma: {
                var ot = TypeOf(ma.Obj);
                if (!IsStruct(ot)) throw new ForgeException($"'.{ma.Name}' needs a struct, got {ot}", ma.Line);
                var (off, ft) = FieldOf(ot, ma.Name, ma.Line);
                return new DerefAssign { Line = ma.Line, Ptr = FieldPtr(LowerExpr(ma.Obj), off, ft, ma.Line), E = LowerExpr(ma.E) };
            }
            case VarDecl v: v.E = LowerExpr(v.E); return v;
            case Assign a: a.E = LowerExpr(a.E); return a;
            case IndexAssign ia: ia.Arr = LowerExpr(ia.Arr); ia.Index = LowerExpr(ia.Index); ia.E = LowerExpr(ia.E); return ia;
            case DerefAssign da: da.Ptr = LowerExpr(da.Ptr); da.E = LowerExpr(da.E); return da;
            case IfStmt i: i.Cond = LowerExpr(i.Cond); i.Then = LowerBody(i.Then); if (i.Else != null) i.Else = LowerBody(i.Else); return i;
            case WhileStmt w: w.Cond = LowerExpr(w.Cond); w.Body = LowerBody(w.Body); return w;
            case ForStmt fs:
                if (fs.Init != null) fs.Init = LowerStmt(fs.Init);
                if (fs.Cond != null) fs.Cond = LowerExpr(fs.Cond);
                if (fs.Step != null) fs.Step = LowerStmt(fs.Step);
                fs.Body = LowerBody(fs.Body); return fs;
            case RetStmt r: if (r.E != null) r.E = LowerExpr(r.E); return r;
            case ExprStmt es: es.E = LowerExpr(es.E); return es;
            case PrintStmt pr:
                if (pr.Arg is Interp pi) LowerInterpParts(pi);       // printed directly, no string built
                else pr.Arg = LowerExpr(pr.Arg);
                return pr;
            default: return st;
        }
    }
    Expr FieldPtr(Expr obj, int off, string ft, int line) =>
        new CastExpr { Line = line, TargetType = ft + "*",
            E = new Binary { Line = line, Op = "+", L = new CastExpr { Line = line, TargetType = "u8*", E = obj }, R = new IntLit { Line = line, Val = off } } };
    // matches FieldPtr's shape: (T*)((u8*)obj + off)
    static bool IsFieldPtr(Expr p, out Expr obj, out int off) {
        obj = null!; off = 0;
        if (p is CastExpr { E: Binary { Op: "+", L: CastExpr { TargetType: "u8*" } inner, R: IntLit k } } && Asm.Fits32(k.Val)) {
            obj = inner.E; off = (int)k.Val; return true;
        }
        return false;
    }
    Expr LowerExpr(Expr e) {
        switch (e) {
            case MemberExpr me: {
                var ot = TypeOf(me.Obj);
                var obj = LowerExpr(me.Obj);
                if (me.Name is "length" or "Length" && (IsArray(ot) || IsStr(ot))) return new LengthExpr { Line = me.Line, Arr = obj };
                if (!IsStruct(ot)) throw new ForgeException($"'.{me.Name}' needs a struct, got {ot}", me.Line);
                var (off, ft) = FieldOf(ot, me.Name, me.Line);
                return new Deref { Line = me.Line, Ptr = FieldPtr(obj, off, ft, me.Line) };
            }
            case SizeOfExpr so: TypeOf(so); return new IntLit { Line = so.Line, Val = SizeOf(so.Type) };
            case Unary u: u.E = LowerExpr(u.E); return u;
            case Binary b: b.L = LowerExpr(b.L); b.R = LowerExpr(b.R); return b;
            case CallExpr c: TypeOfCallName(c); for (int k = 0; k < c.Args.Count; k++) c.Args[k] = LowerExpr(c.Args[k]); return c;
            case TryExpr t: TypeOfCallName(t.Call); for (int k = 0; k < t.Call.Args.Count; k++) t.Call.Args[k] = LowerExpr(t.Call.Args[k]); if (t.Else != null) t.Else = LowerExpr(t.Else); return t;
            case NewArray na: na.Size = LowerExpr(na.Size); return na;
            case NewStruct ns: for (int k = 0; k < ns.Inits.Count; k++) ns.Inits[k] = (ns.Inits[k].Name, LowerExpr(ns.Inits[k].E)); return ns;
            case IndexExpr ix: ix.Arr = LowerExpr(ix.Arr); ix.Index = LowerExpr(ix.Index); return ix;
            case LengthExpr le: le.Arr = LowerExpr(le.Arr); return le;
            case Deref de: de.Ptr = LowerExpr(de.Ptr); return de;
            case CastExpr ce: ce.E = LowerExpr(ce.E); return ce;
            case SpawnExpr sp: if (sp.Arg != null) sp.Arg = LowerExpr(sp.Arg); return sp;
            case AwaitExpr aw: aw.Task = LowerExpr(aw.Task); return aw;
            case WhenAllExpr wa: for (int k = 0; k < wa.Tasks.Count; k++) wa.Tasks[k] = LowerExpr(wa.Tasks[k]); return wa;
            case Interp itp: {
                // $"a{x}b" used as a value -> "a" + str(x) + "b"
                LowerInterpParts(itp);
                return InterpConcat(itp);
            }
            default: return e;
        }
    }

    // $"a{x}b" -> "a" + str(x) + "b" (string holes are used as they are)
    Expr InterpConcat(Interp itp) {
        Expr? acc = null;
        foreach (var part in itp.Parts) {
            Expr piece = part is string lit ? new StrLit { Line = itp.Line, Val = lit }
                : ((ValueTuple<Expr, string?>)part).Item1 is var pe && IsStr(TypeOf(pe)) ? pe
                : new CallExpr { Line = itp.Line, Name = "str", Args = { ((ValueTuple<Expr, string?>)part).Item1 } };
            acc = acc == null ? piece : new Binary { Line = itp.Line, Op = "+", L = acc, R = piece };
        }
        if (acc == null) return new StrLit { Line = itp.Line, Val = "" };
        if (acc is not Binary && acc is not StrLit) acc = new Binary { Line = itp.Line, Op = "+", L = new StrLit { Line = itp.Line, Val = "" }, R = acc };
        return acc;
    }

    // Does a hole run code that could print or otherwise be seen (a Forge function,
    // an extern, await, spawn)? Then Console.WriteLine must build the whole string
    // before printing any of it, as C# does.
    bool InterpRunsUserCode(Interp itp) {
        bool hit = false;
        foreach (var part in itp.Parts) {
            if (part is not ValueTuple<Expr, string?> hole) continue;
            Walker.WalkExpr(hole.Item1, e => {
                if (e is CallExpr c && ((_funcs.TryGetValue(c.Name, out var f) && !f.IsRuntime) || _externs.ContainsKey(c.Name) || c.Name == "join")) hit = true;   // join waits for a thread running user code
                else if (e is TryExpr or AwaitExpr or SpawnExpr or WhenAllExpr or YieldExpr or InlineExpr) hit = true;
            }, null);
        }
        return hit;
    }
    void LowerInterpParts(Interp itp) {
        for (int k = 0; k < itp.Parts.Count; k++)
            if (itp.Parts[k] is ValueTuple<Expr, string?> tup) itp.Parts[k] = (LowerExpr(tup.Item1), tup.Item2);
    }
    // resolve obj.Method(...) to its function name before the arguments are rewritten
    void TypeOfCallName(CallExpr c) { if (c.Name.StartsWith(".")) TypeOf(c); }
    int SizeOf(string t) => IsStruct(t) ? _structs[t].Size : Width(t);

    void AddLocal(string name, string type) {
        if (!_locals.ContainsKey(name)) { _frame += 8; _locals[name] = (-_frame, type); }
    }
    void CheckBody(List<Stmt> body) {
        foreach (var s in body) Recover(s, () => CheckStmt(s));
    }

    // --- analysis: does control definitely return/throw before falling off? ---
    static bool StmtReturns(Stmt s) => s switch {
        RetStmt => true,
        ThrowStmt => true,
        IfStmt i => i.Else != null && BlockReturns(i.Then) && BlockReturns(i.Else),
        _ => false,
    };
    static bool BlockReturns(List<Stmt> body) => body.Any(StmtReturns);

    void CheckAllPathsReturn(Func f) {
        if (f.Ret == "void") return;       // void may fall off the end
        if (!BlockReturns(f.Body))
            throw new ForgeException(
                $"'{f.Name}' must return a {f.Ret} on every path (some path reaches the end without 'return'"
                + (f.Throws != null ? " or 'throw')" : ")"), f.Line);
    }

    // --- analysis: code after a definite return/throw is unreachable (warning) ---
    void CheckUnreachable(List<Stmt> body) {
        for (int i = 0; i < body.Count; i++) {
            var s = body[i];
            if (s is IfStmt ifs) { CheckUnreachable(ifs.Then); if (ifs.Else != null) CheckUnreachable(ifs.Else); }
            else if (s is WhileStmt w) CheckUnreachable(w.Body);
            else if (s is ForStmt f) CheckUnreachable(f.Body);
            if (StmtReturns(s) && i + 1 < body.Count)
                Warnings.Add(new Diagnostic(Severity.Warning, NodeLine(body[i + 1]),
                    "unreachable code after a 'return' or 'throw'"));
        }
    }

    // --- analysis: locals that are declared but never read (warning) ---
    void CheckUnusedLocals(Func f) {
        var declared = new List<(string Name, int Line)>();
        var read = new HashSet<string>();
        void Walk(List<Stmt> body) {
            foreach (var s in body) WalkStmt(s);
        }
        void WalkStmt(Stmt s) {
            switch (s) {
                case VarDecl v: if (!v.Name.Contains('$')) declared.Add((v.Name, v.Line)); WalkExpr(v.E); break;   // $-names are compiler temps
                case Assign a: WalkExpr(a.E); break;                 // target write is not a read
                case AsmStmt asm:
                    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(asm.Text, @"\{\s*(\w+)\s*\}")) read.Add(m.Groups[1].Value);
                    break;
                case IndexAssign ia: WalkExpr(ia.Arr); WalkExpr(ia.Index); WalkExpr(ia.E); break;
                case DerefAssign da: WalkExpr(da.Ptr); WalkExpr(da.E); break;
                case IfStmt i: WalkExpr(i.Cond); Walk(i.Then); if (i.Else != null) Walk(i.Else); break;
                case WhileStmt w: WalkExpr(w.Cond); Walk(w.Body); break;
                case ForStmt f: if (f.Init != null) WalkStmt(f.Init); WalkExpr(f.Cond); if (f.Step != null) WalkStmt(f.Step); Walk(f.Body); break;
                case RetStmt r: if (r.E != null) WalkExpr(r.E); break;
                case PrintStmt p: WalkExpr(p.Arg); break;
                case ExprStmt e: WalkExpr(e.E); break;
            }
        }
        void WalkExpr(Expr? e) {
            switch (e) {
                case null: break;
                case VarRef v: read.Add(v.Name); break;
                case Unary u: WalkExpr(u.E); break;
                case Binary b: WalkExpr(b.L); WalkExpr(b.R); break;
                case CallExpr c: foreach (var a in c.Args) WalkExpr(a); break;
                case TryExpr t: foreach (var a in t.Call.Args) WalkExpr(a); WalkExpr(t.Else); break;
                case NewArray na: WalkExpr(na.Size); break;
                case NewStruct ns: foreach (var (_, ie) in ns.Inits) WalkExpr(ie); break;
                case IndexExpr ix: WalkExpr(ix.Arr); WalkExpr(ix.Index); break;
                case LengthExpr le: WalkExpr(le.Arr); break;
                case AddrOf ao: read.Add(ao.Name); break;
                case Deref de: WalkExpr(de.Ptr); break;
                case CastExpr ce: WalkExpr(ce.E); break;
                case SpawnExpr sp: if (sp.Arg != null) WalkExpr(sp.Arg); break;
                case AwaitExpr aw: WalkExpr(aw.Task); break;
                case WhenAllExpr wa: foreach (var t in wa.Tasks) WalkExpr(t); break;
                case Interp itp: foreach (var part in itp.Parts) if (part is ValueTuple<Expr,string?> tup) WalkExpr(tup.Item1); break;
            }
        }
        Walk(f.Body);
        foreach (var (name, line) in declared)
            if (!read.Contains(name))
                Warnings.Add(new Diagnostic(Severity.Warning, line, $"local variable '{name}' is declared but never used"));
    }

    static int NodeLine(Stmt s) => s.Line;

    void CheckStmt(Stmt s) {
        switch (s) {
            case VarDecl v: { var t = TypeOf(v.E); if (t == "void") throw new ForgeException($"'{v.Name}' cannot hold the result of an expression with no value (void)", v.Line); if (v.DeclType != null && !Assignable(v.DeclType, t)) throw new ForgeException($"initializer of '{v.Name}' must be {v.DeclType}" + AwaitHint(v.DeclType, t), v.Line); if (v.DeclType != null) v.E = Coerce(v.E, v.DeclType); break; }
            case Assign a: { if (!_locals.TryGetValue(a.Name, out var li)) throw new ForgeException($"assignment to undeclared '{a.Name}'", a.Line); var at0 = TypeOf(a.E); if (!Assignable(li.Type, at0)) throw new ForgeException($"assignment to '{a.Name}' type mismatch" + AwaitHint(li.Type, at0), a.Line); a.E = Coerce(a.E, li.Type); break; }
            case AsmStmt asm:
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(asm.Text, @"\{\s*(\w+)\s*\}"))
                    if (!_locals.ContainsKey(m.Groups[1].Value))
                        throw new ForgeException($"asm: unknown variable '{m.Groups[1].Value}'" + Suggest.Hint(m.Groups[1].Value, _locals.Keys), asm.Line);
                break;
            case IndexAssign ia: {
                var at = TypeOf(ia.Arr);
                if (!IsInt(TypeOf(ia.Index))) throw new ForgeException("array index must be an integer", ia.Line);
                string elem = IsArray(at) ? ElemType(at) : IsPtr(at) ? Pointee(at)
                    : throw new ForgeException("indexed assignment to a non-array, non-pointer", ia.Line);
                if (!Assignable(elem, TypeOf(ia.E))) throw new ForgeException("element type mismatch", ia.Line);
                ia.E = Coerce(ia.E, elem);
                break;
            }
            case DerefAssign da: {
                var pt = TypeOf(da.Ptr);
                if (!IsPtr(pt)) throw new ForgeException("assigning through a non-pointer", da.Line);
                if (!Assignable(Pointee(pt), TypeOf(da.E))) throw new ForgeException("pointer target type mismatch", da.Line);
                da.E = Coerce(da.E, Pointee(pt));
                break;
            }
            case IfStmt i:
                Recover(i, () => { if (TypeOf(i.Cond) != "bool") throw new ForgeException("if condition must be bool", i.Line); });
                CheckBody(i.Then); if (i.Else != null) CheckBody(i.Else); break;
            case WhileStmt w:
                Recover(w, () => { if (TypeOf(w.Cond) != "bool") throw new ForgeException("while condition must be bool", w.Line); });
                CheckBody(w.Body); break;
            case ForStmt f: {
                if (f.Init != null) CheckStmt(f.Init);
                if (f.Cond != null && TypeOf(f.Cond) != "bool") throw new ForgeException("for condition must be bool", f.Line);
                if (f.Step != null) CheckStmt(f.Step);
                CheckBody(f.Body);
                break;
            }
            case RetStmt r: {
                if (_cur.Ret == "void") { if (r.E != null) throw new ForgeException($"'{_cur.Name}' returns void", r.Line); }
                else { if (r.E == null) throw new ForgeException($"'{_cur.Name}' must return {_cur.Ret}", r.Line); var rt0 = TypeOf(r.E); if (!Assignable(_cur.Ret, rt0)) throw new ForgeException("return type mismatch" + AwaitHint(_cur.Ret, rt0), r.Line); r.E = Coerce(r.E, _cur.Ret); }
                break;
            }
            case ThrowStmt th: if (_cur.Throws == null) throw new ForgeException($"'{_cur.Name}' does not declare 'throws'", th.Line); if (th.Err != _cur.Throws) throw new ForgeException($"'{_cur.Name}' throws {_cur.Throws}, not {th.Err}", th.Line); break;
            case PrintStmt p: {
                var pt0 = TypeOf(p.Arg);      // also turns double holes into strings
                if (pt0 == "double") p.Arg = DoubleText(p.Arg, null, p.Line);
                else if (p.Arg is Interp itp && InterpRunsUserCode(itp)) { p.Arg = InterpConcat(itp); TypeOf(p.Arg); }
                break;
            }
            case ExprStmt e: TypeOf(e.E); break;
        }
    }

    // ----------------------------------------------------------------- emission
    // ---- register allocation ----------------------------------------------------
    // Variables of optimizable functions get the callee-saved registers rbx, r12..r15
    // (most-used first, uses inside loops weigh more). Every routine in the runtime
    // preserves these, and __ctx_switch saves them, so they survive calls and awaits.
    static readonly int[] CalleeSaved = { 3, 12, 13, 14, 15 };
    static readonly int[] ArgRegs = { 7, 6, 2, 1 };          // rdi, rsi, rdx, rcx
    readonly Dictionary<string, int> _regOf = new();
    bool _usesNet;                           // the program calls a socket builtin (Windows: import ws2_32)
    readonly HashSet<string> _addrTaken = new();
    readonly List<int> _savedRegs = new();
    readonly Dictionary<int, int> _saveSlot = new();
    bool _frameless;

    void AllocateRegisters(Func f) {
        var weight = new Dictionary<string, double>();
        foreach (var n in _locals.Keys) weight[n] = 0;
        Walker.Walk(f.Body, e => { if (e is AddrOf a) _addrTaken.Add(a.Name); },
            st => { if (st is AsmStmt asm) foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(asm.Text, @"\{\s*(\w+)\s*\}")) _addrTaken.Add(m.Groups[1].Value); });
        bool loopsBack = false;
        Walker.Walk(f.Body, null, s => { if (s is GotoStmt) loopsBack = true; });
        void Bump(string n, int depth) { if (weight.ContainsKey(n)) weight[n] += Math.Pow(8, Math.Min(depth, 6)); }
        void WS(List<Stmt> body, int d) { foreach (var s in body) WSt(s, d); }
        void WSt(Stmt s, int d) {
            switch (s) {
                case VarDecl v: Bump(v.Name, d); WE(v.E, d); break;
                case Assign a: Bump(a.Name, d); WE(a.E, d); break;
                case IndexAssign ia: WE(ia.Arr, d); WE(ia.Index, d); WE(ia.E, d); break;
                case DerefAssign da: WE(da.Ptr, d); WE(da.E, d); break;
                case IfStmt i: WE(i.Cond, d); WS(i.Then, d); if (i.Else != null) WS(i.Else, d); break;
                case WhileStmt w: WE(w.Cond, d + 1); WS(w.Body, d + 1); break;
                case ForStmt fs:
                    if (fs.Init != null) WSt(fs.Init, d);
                    if (fs.Cond != null) WE(fs.Cond, d + 1);
                    if (fs.Step != null) WSt(fs.Step, d + 1);
                    WS(fs.Body, d + 1); break;
                case RetStmt r: if (r.E != null) WE(r.E, d); break;
                case InlineRet ir: if (ir.E != null) WE(ir.E, d); break;
                case ExprStmt es: WE(es.E, d); break;
                case PrintStmt p: WE(p.Arg, d); break;
            }
        }
        void WE(Expr? e, int d) {
            switch (e) {
                case null: return;
                case VarRef v: Bump(v.Name, d); return;
                case InlineExpr ie: WS(ie.Body, d); return;        // inlined body: its loops count as loops
                default: foreach (var c in Children(e)) WE(c, d); return;
            }
        }
        WS(f.Body, loopsBack ? 1 : 0);
        foreach (var (_, n) in f.Params) if (weight.ContainsKey(n)) weight[n] += 0.5;   // saves the spill
        // A function that calls nothing can also keep variables in the scratch registers
        // (nothing it runs clobbers them, and they need no saving): those go first.

        // Linear scan over live ranges: variables whose ranges do not overlap share a
        // register (inlined copies of a function each bring their own locals, which
        // are short-lived). When registers run out the lowest-weight variable spills.
        // A variable whose live range spans no call (or anything else that clobbers the
        // scratch registers) may also use rsi, rdi, r8..r11; others need callee-saved ones.
        var clobbers = new List<(int S, int E)>();
        var live = LiveRanges(f, clobbers);
        bool callerOk = f.Throws == null;                  // rdx/rsi carry the error protocol
        int[] RegsFor(string n) {
            var (vs, ve) = live[n];
            return callerOk && !clobbers.Exists(c => c.S <= ve && c.E >= vs) ? LeafRegs : CalleeSaved;
        }
        var order = weight.Where(kv => kv.Value > 0 && !_addrTaken.Contains(kv.Key) && live.ContainsKey(kv.Key))
                          .Select(kv => kv.Key)
                          .OrderBy(n => live[n].S).ThenByDescending(n => weight[n]).ThenBy(n => n, StringComparer.Ordinal).ToList();
        var active = new List<(string Name, int Reg)>();
        var free = new List<int>(LeafRegs);
        foreach (var n in order) {
            int start = live[n].S;
            for (int i = active.Count - 1; i >= 0; i--)
                if (live[active[i].Name].E < start) { free.Add(active[i].Reg); active.RemoveAt(i); }
            int[] regs = RegsFor(n);
            int pickReg = -1;
            foreach (var r in regs) if (free.Contains(r)) { pickReg = r; break; }
            if (pickReg >= 0) { _regOf[n] = pickReg; active.Add((n, pickReg)); free.Remove(pickReg); continue; }
            int vi = -1;                                   // no register free: spill the cheapest that would fit
            for (int i = 0; i < active.Count; i++)
                if (Array.IndexOf(regs, active[i].Reg) >= 0 && (vi < 0 || weight[active[i].Name] < weight[active[vi].Name])) vi = i;
            if (vi >= 0 && weight[active[vi].Name] < weight[n]) {
                var (victim, reg) = active[vi];
                _regOf.Remove(victim); active.RemoveAt(vi);
                _regOf[n] = reg; active.Add((n, reg));
            }
        }
        foreach (var r in _regOf.Values.Distinct().OrderBy(r => Array.IndexOf(CalleeSaved, r)))
            if (Array.IndexOf(CalleeSaved, r) >= 0) _savedRegs.Add(r);
    }

    // Live range of every variable as [first, last] positions in a numbering of the
    // function's statements. Conservative: every variable an ordinary statement
    // mentions counts as live across the whole statement (including inlined bodies
    // nested in its expressions, which the code generator may evaluate first), and a
    // variable that is live around a loop's back edge is live for the whole loop.
    Dictionary<string, (int S, int E)> LiveRanges(Func f, List<(int S, int E)> clobbers) {
        var iv = new Dictionary<string, (int S, int E)>();
        var firstWrite = new Dictionary<string, bool>();
        var loops = new List<(int S, int E)>();
        var labels = new Dictionary<string, int>();
        int pos = 0;
        void Occ(string n, int p) => iv[n] = iv.TryGetValue(n, out var r) ? (Math.Min(r.S, p), Math.Max(r.E, p)) : (p, p);
        foreach (var (_, pn) in f.Params) { Occ(pn, 0); firstWrite[pn] = false; }
        pos = 1;

        void Mentions(HashSet<string> names, Expr x) {
            if (x is VarRef vr) names.Add(vr.Name); else if (x is AddrOf ao) names.Add(ao.Name);
        }
        void Assigns(HashSet<string> names, Stmt s2) {
            if (s2 is Assign asg) names.Add(asg.Name); else if (s2 is VarDecl vd) names.Add(vd.Name);
        }
        void Ex(Expr? e, HashSet<string> names) {
            switch (e) {
                case null: return;
                case VarRef v: names.Add(v.Name); return;
                case AddrOf a: names.Add(a.Name); return;
                case InlineExpr ie: Body(ie.Body); return;            // numbered in place, inside the statement
                case Unary u: Ex(u.E, names); return;
                case Binary b: Ex(b.L, names); Ex(b.R, names); return;
                case CallExpr c: foreach (var a in c.Args) Ex(a, names); return;
                case TryExpr t: Ex(t.Call, names); Ex(t.Else, names); return;
                case NewArray na: Ex(na.Size, names); return;
                case IndexExpr ix: Ex(ix.Arr, names); Ex(ix.Index, names); return;
                case LengthExpr le: Ex(le.Arr, names); return;
                case Deref de: Ex(de.Ptr, names); return;
                case CastExpr ce: Ex(ce.E, names); return;
                case SpawnExpr sp: Ex(sp.Arg, names); return;
                case AwaitExpr aw: Ex(aw.Task, names); return;
                case WhenAllExpr wa: foreach (var t in wa.Tasks) Ex(t, names); return;
                case Interp itp: foreach (var part in itp.Parts) if (part is ValueTuple<Expr, string?> tup) Ex(tup.Item1, names); return;
                default: Walker.WalkExpr(e, x => Mentions(names, x), s2 => Assigns(names, s2)); return;
            }
        }
        void Simple(string? target, params Expr?[] es) {
            int a = pos++;
            var names = new HashSet<string>();
            bool seen = target != null && iv.ContainsKey(target);
            foreach (var e in es) Ex(e, names);
            int b = pos++;
            if (es.Any(Clobbers)) clobbers.Add((a, b));
            foreach (var n in names) { if (!iv.ContainsKey(n)) firstWrite[n] = false; Occ(n, a); Occ(n, b); }
            if (target != null) {
                if (!seen && !iv.ContainsKey(target)) firstWrite[target] = true;
                Occ(target, a); Occ(target, b);
            }
        }
        void St(Stmt st) {
            switch (st) {
                case VarDecl v: Simple(v.Name, v.E); break;
                case Assign a: Simple(a.Name, a.E); break;
                case IndexAssign ia: Simple(null, ia.Arr, ia.Index, ia.E); break;
                case DerefAssign da: Simple(null, da.Ptr, da.E); break;
                case ExprStmt es: Simple(null, es.E); break;
                case RetStmt r: Simple(null, r.E); break;
                case InlineRet ir: Simple(null, ir.E); break;
                case PrintStmt pr: Simple(null, pr.Arg); clobbers.Add((pos - 2, pos - 1)); break;
                case IfStmt i: Simple(null, i.Cond); Body(i.Then); if (i.Else != null) Body(i.Else); break;
                case WhileStmt w: { int ls = pos; Simple(null, w.Cond); Body(w.Body); loops.Add((ls, pos - 1)); break; }
                case ForStmt fs: {
                    if (fs.Init != null) St(fs.Init);
                    int ls = pos; Simple(null, fs.Cond); Body(fs.Body); if (fs.Step != null) St(fs.Step);
                    loops.Add((ls, pos - 1)); break;
                }
                case LabelStmt l: labels[l.Name] = pos++; break;
                case GotoStmt g: { int gp = pos++; if (labels.TryGetValue(g.Name, out int lp)) loops.Add((lp, gp)); break; }
                default: {                                            // anything else: all it mentions, whole statement
                    int a = pos++;
                    var names = new HashSet<string>();
                    Walker.WalkStmt(st, x => Mentions(names, x), s2 => Assigns(names, s2));
                    int b = pos++;
                    clobbers.Add((a, b));
                    foreach (var n in names) { firstWrite.TryAdd(n, false); Occ(n, a); Occ(n, b); }
                    break;
                }
            }
        }
        void Body(List<Stmt> body) { foreach (var st in body) St(st); }
        Body(f.Body);

        // Around loops: a variable is dead at the back edge only if it lives entirely
        // inside the loop and starts with a plain write (a fresh value each iteration).
        bool changed = true;
        while (changed) {
            changed = false;
            foreach (var (ls, le) in loops)
                foreach (var n in iv.Keys.ToList()) {
                    var (vs, ve) = iv[n];
                    if (vs > le || ve < ls) continue;
                    bool fresh = vs >= ls && ve <= le && firstWrite.GetValueOrDefault(n);
                    if (fresh || (vs <= ls && ve >= le)) continue;
                    iv[n] = (Math.Min(vs, ls), Math.Max(ve, le)); changed = true;
                }
        }
        return iv;
    }

    // direct sub-expressions of e (InlineExpr bodies are statements: not included)
    static IEnumerable<Expr?> Children(Expr e) {
        switch (e) {
            case Unary u: yield return u.E; break;
            case Binary b: yield return b.L; yield return b.R; break;
            case CallExpr c: foreach (var a in c.Args) yield return a; break;
            case TryExpr t: yield return t.Call; yield return t.Else; break;
            case NewArray na: yield return na.Size; break;
            case NewStruct ns: foreach (var (_, ie) in ns.Inits) yield return ie; break;
            case IndexExpr ix: yield return ix.Arr; yield return ix.Index; break;
            case LengthExpr le: yield return le.Arr; break;
            case Deref de: yield return de.Ptr; break;
            case CastExpr ce: yield return ce.E; break;
            case SpawnExpr sp: yield return sp.Arg; break;
            case AwaitExpr aw: yield return aw.Task; break;
            case WhenAllExpr wa: foreach (var t in wa.Tasks) yield return t; break;
            case Interp itp: foreach (var part in itp.Parts) if (part is ValueTuple<Expr, string?> tup) yield return tup.Item1; break;
        }
    }

    // rsi, rdi, r8..r11 first (free to use in a leaf), then the callee-saved ones.
    // Expression code itself only uses rax, rcx and rdx as scratch.
    static readonly int[] LeafRegs = { 6, 7, 8, 9, 10, 11, 3, 12, 13, 14, 15 };

    // No calls of any kind (user functions, runtime routines, OS) and nothing that
    // uses registers beyond rax/rcx/rdx: integer/pointer arithmetic, loads and stores.
    bool IsLeaf(Func f) {
        if (f.Throws != null || f.IsAsync) return false;
        bool ok = true;
        Walker.Walk(f.Body, e => { if (!NodeIsLeaf(e)) ok = false; }, st => { if (st is PrintStmt or ThrowStmt or AsmStmt) ok = false; });
        return ok;
    }
    // this node alone (not its children) runs no call and uses no register beyond rax/rcx/rdx
    bool NodeIsLeaf(Expr e) {
        bool NotFloat(Expr x) { var t = TypeOf(x); return t != "double" && t != "float" && !IsStr(t); }
        return e switch {
            IntLit or BoolLit or StrLit or NullLit or VarRef or InlineExpr or Deref or IndexExpr => true,   // a string literal is a mov rax, imm64
            Unary u => NotFloat(u.E),
            Binary b => NotFloat(b.L) && NotFloat(b.R),
            CastExpr ce => NotFloat(ce.E) && ce.TargetType is not ("double" or "float"),
            LengthExpr le => !IsStr(TypeOf(le.Arr)),                // strlen is a call
            _ => false,
        };
    }
    // e (not counting inlined bodies, numbered separately) may clobber the scratch registers
    bool Clobbers(Expr? e) => e != null && e is not InlineExpr && (!NodeIsLeaf(e) || Children(e).Any(Clobbers));

    // A function can drop the rbp frame when every variable is in a register and it
    // contains nothing that realigns rsp or returns from the middle of an expression.
    bool FramelessSafe(Func f) {
        if (_regOf.Count != _locals.Count) return false;
        bool ok = true;
        Walker.Walk(f.Body, e => {
            switch (e) {
                case IntLit or BoolLit or NullLit or VarRef or Unary or CastExpr or InlineExpr or IndexExpr or Deref: break;
                case LengthExpr le: if (IsStr(TypeOf(le.Arr))) ok = false; break;
                case Binary b: if (IsStr(TypeOf(b.L))) ok = false; break;
                case CallExpr c: if (!_funcs.TryGetValue(c.Name, out var g) || g.IsAsync || g.Throws != null) ok = false; break;
                default: ok = false; break;
            }
        }, s => { if (s is PrintStmt or ThrowStmt or AsmStmt) ok = false; });
        return ok;
    }

    void EmitFunc(Func f) {
        _cur = f;
        _locals = new(); _frame = 0; _typeCache.Clear();
        _regOf.Clear(); _addrTaken.Clear(); _savedRegs.Clear(); _saveSlot.Clear(); _frameless = false;
        foreach (var (t, n) in f.Params) AddLocal(n, t);
        foreach (var (t, n) in f.ExtraLocals) AddLocal(n, t);
        PrescanDecls(f.Body);
        bool opt = OptimizeEnabled && Optimizable(f);
        if (opt) { AllocateRegisters(f); _frameless = FramelessSafe(f); }

        _a.Label(f.Name);
        DbgFuncStart(f);               // -g: function range and declaration line (DebugInfo.cs)
        if (_frameless) {
            foreach (var r in _savedRegs) _a.PushR(r);
        } else {
            foreach (var r in _savedRegs) { _frame += 8; _saveSlot[r] = -_frame; }
            int frame = (_frame + 15) & ~15;   // 16-byte align
            _a.Prologue(frame);
            if (_dbgFunc != null) _dbgFunc.FrameSize = frame;
            foreach (var r in _savedRegs) _a.StoreSlot(r, _saveSlot[r]);
        }
        // incoming register args (rdi,rsi,rdx,rcx) -> their register or stack slot
        // (a parallel move: a parameter's register may be another parameter's source)
        var moves = new List<(int dst, int src)>();
        for (int i = 0; i < f.Params.Count && i < 4; i++) {
            var pn = f.Params[i].Name;
            if (_regOf.TryGetValue(pn, out int pr)) { if (pr != ArgRegs[i]) moves.Add((pr, ArgRegs[i])); }
            else _a.StoreSlot(ArgRegs[i], _locals[pn].Off);
        }
        while (moves.Count > 0) {
            int k = moves.FindIndex(m => !moves.Exists(o => o.src == m.dst));
            if (k < 0) {                                   // a cycle: park one source in rax
                int src = moves[0].src;
                _a.MovRR(RAX, src);
                for (int j = 0; j < moves.Count; j++) if (moves[j].src == src) moves[j] = (moves[j].dst, RAX);
                continue;
            }
            _a.MovRR(moves[k].dst, moves[k].src);
            moves.RemoveAt(k);
        }
        DbgBodyStart();
        EmitBody(f.Body);
        // implicit return for void
        if (f.Ret == "void") {
            if (f.IsAsync) _a.MovRaxImm(0);        // task result for async Task
            if (f.Throws != null) _a.MovRdxImm(1); // ok
            EmitFuncEpilogue();
        } else {
            // safety net: return 0
            _a.MovRaxImm(0); if (f.Throws != null) _a.MovRdxImm(1); EmitFuncEpilogue();
        }
        EmitBoundsStubs();             // cold out-of-range paths (BoundsCheck.cs)
        DbgFuncEnd(f);
    }

    // Restores the callee-saved registers this function used, then returns.
    // Leaves rax, rdx and rsi (return value / error protocol) untouched.
    void EmitFuncEpilogue() {
        if (_frameless) {
            for (int i = _savedRegs.Count - 1; i >= 0; i--) _a.PopR(_savedRegs[i]);
            _a.Ret();
            return;
        }
        foreach (var r in _savedRegs) _a.LoadSlot(r, _saveSlot[r]);
        _a.Epilogue();
    }

    // ---- variable access ----------------------------------------------------------
    void LoadVar(int reg, string name) {
        if (_regOf.TryGetValue(name, out int r)) _a.MovRR(reg, r);
        else _a.LoadSlot(reg, _locals[name].Off);
    }
    void StoreVar(string name) {             // name = rax
        if (_regOf.TryGetValue(name, out int r)) _a.MovRR(r, 0);
        else _a.StoreLocal(_locals[name].Off);
    }
    bool InReg(Expr e, out int reg) {
        reg = -1;
        return e is VarRef v && _regOf.TryGetValue(v.Name, out reg);
    }
    // literal that fits an imm32 operand
    static bool IsImm(Expr e, out int v) {
        v = 0;
        if (e is IntLit i && Asm.Fits32(i.Val)) { v = (int)i.Val; return true; }
        if (e is BoolLit b) { v = b.Val ? 1 : 0; return true; }
        if (e is NullLit) return true;
        return false;
    }
    // an operand usable directly in an instruction: imm32, register variable or stack slot
    bool IsOperand(Expr e) => IsImm(e, out _) || e is VarRef;
    // safe to evaluate out of order (reads nothing a call could change behind our back)
    bool Reorderable(Expr e) => IsImm(e, out _) || (e is VarRef v && !_addrTaken.Contains(v.Name) && _regOf.ContainsKey(v.Name));
    // mov reg, operand
    void LoadOperand(int reg, Expr e) {
        if (IsImm(e, out int v)) _a.MovRI(reg, v);
        else LoadVar(reg, ((VarRef)e).Name);
    }
    // <alu> reg, operand   (op: the "r/m, reg" opcode; ext: the /digit for the imm form)
    void AluOperand(byte op, int ext, int reg, Expr e) {
        if (IsImm(e, out int v)) _a.AluRI(ext, reg, v);
        else if (InReg(e, out int r)) _a.AluRR(op, reg, r);
        else _a.AluRM((byte)(op + 2), reg, _locals[((VarRef)e).Name].Off);
    }


    void EmitImm32(int v) { _a.Db(v & 0xff); _a.Db((v>>8)&0xff); _a.Db((v>>16)&0xff); _a.Db((v>>24)&0xff); }

    void EmitBody(List<Stmt> body) { foreach (var s in body) EmitStmt(s); }

    void EmitStmt(Stmt s) {
        DbgRow(s.Line);
        switch (s) {
            case VarDecl v: EmitAssign(v.Name, v.E); break;
            case Assign a:  EmitAssign(a.Name, a.E); break;
            case LabelStmt l: _a.Label(l.Name); break;
            case GotoStmt g: _a.JmpRel(g.Name); break;
            case InlineRet ir: if (ir.E != null) EmitExpr(ir.E); _a.JmpRel(ir.End); break;
            case IndexAssign ia: {
                var at = TypeOf(ia.Arr);
                EmitElemStore(ia.Arr, ia.Index, ia.E, IsPtr(at) ? Width(Pointee(at)) : 8, IsArray(at) ? ia.Line : -1);
                break;
            }
            case AsmStmt asm:
                new InlineAsm(_a, n => _locals[n].Off, NewLabel("asm") + "_", asm.Line).Assemble(asm.Text);
                break;
            case DerefAssign da when IsFieldPtr(da.Ptr, out var sobj, out int soff): {
                int w = Width(Pointee(TypeOf(da.Ptr)));
                string? ov = sobj is VarRef svr ? svr.Name : null;
                if (IsPlain(sobj) && (ov == null || !Writes(da.E, ov))) {     // value first, then the object
                    EmitExpr(da.E); _a.MovRR(RDX, RAX);
                    int bas = InReg(sobj, out int sr) ? sr : RAX;
                    if (bas == RAX) EmitExpr(sobj);
                    EmitStoreAt(new MemRef(bas, -1, 1, soff), w);
                } else if (IsPlain(da.E) && !(da.E is VarRef evr && Writes(sobj, evr.Name))) {
                    EmitExpr(sobj); LoadOperand(RDX, da.E);
                    EmitStoreAt(new MemRef(RAX, -1, 1, soff), w);
                } else {
                    EmitExpr(sobj); _a.PushRax();
                    EmitExpr(da.E); _a.MovRR(RDX, RAX);
                    _a.PopRax();
                    EmitStoreAt(new MemRef(RAX, -1, 1, soff), w);
                }
                break;
            }
            case DerefAssign da: {
                EmitExpr(da.Ptr); _a.PushRax();           // pointer
                EmitExpr(da.E); _a.Db(0x48,0x89,0xc2);    // mov rdx, rax (value)
                _a.PopRax();                               // rax = pointer
                EmitStore(Pointee(TypeOf(da.Ptr)));        // *(rax) = rdx, sized
                break;
            }
            case ExprStmt e: EmitExpr(e.E); break;
            case RetStmt r: EmitReturn(r); break;
            case ThrowStmt th: EmitThrow(th); break;
            case PrintStmt p: EmitPrint(p.Arg); break;
            case IfStmt i: {
                string els = NewLabel("else"), end = NewLabel("endif");
                EmitJump(i.Cond, false, i.Else != null ? els : end);
                EmitBody(i.Then);
                if (i.Else != null) { _a.JmpRel(end); _a.Label(els); EmitBody(i.Else); }
                _a.Label(end);
                break;
            }
            // Loops are rotated: the condition is tested at the bottom, so each
            // iteration takes one (taken) branch instead of a test plus a jump back.
            case WhileStmt w: {
                string top = NewLabel("while"), test = NewLabel("wtest");
                bool forever = w.Cond is BoolLit { Val: true };
                if (!forever) _a.JmpRel(test);
                _a.Label(top);
                EmitBody(w.Body);
                if (forever) _a.JmpRel(top);
                else { _a.Label(test); DbgRow(w.Line); EmitJump(w.Cond, true, top); }
                break;
            }
            case ForStmt f: {
                string top = NewLabel("for"), test = NewLabel("ftest");
                if (f.Init != null) EmitStmt(f.Init);
                if (f.Cond != null) _a.JmpRel(test);
                _a.Label(top);
                EmitBody(f.Body);
                if (f.Step != null) EmitStmt(f.Step);
                if (f.Cond == null) _a.JmpRel(top);
                else { _a.Label(test); DbgRow(f.Line); EmitJump(f.Cond, true, top); }
                break;
            }
        }
    }

    // name = e, with in-place forms for register variables:  x = x + 1  ->  add rbx, 1
    void EmitAssign(string name, Expr e) {
        if (_regOf.TryGetValue(name, out int r)) {
            if (e is Binary { Op: "+" or "-" or "&" or "|" or "^" } b && b.L is VarRef lv && lv.Name == name && IsIntOp(b)) {
                var (op, ext) = AluOf(b.Op);
                if (IsOperand(b.R)) { AluOperand(op, ext, r, b.R); return; }
                EmitExpr(b.R); _a.AluRR(op, r, 0); return;          // x = x op <complex>
            }
            if (e is Binary { Op: "+" } b2 && b2.R is VarRef rv && rv.Name == name && IsIntOp(b2) && !IsOperand(b2.L)) {
                EmitExpr(b2.L); _a.AluRR(0x01, r, 0); return;       // x = <complex> + x
            }
            if (IsOperand(e)) { LoadOperand(r, e); return; }
            if (e is Binary { Op: "+" or "-" } lb && InReg(lb.L, out int br) && IsImm(lb.R, out int k) && IsIntOp(lb)) {
                _a.Lea(r, br, lb.Op == "+" ? k : -k); return;       // x = y + k
            }
        }
        EmitExpr(e); StoreVar(name);
    }
    bool IsIntOp(Binary b) { var lt = TypeOf(b.L); var rt = TypeOf(b.R); return (IsInt(lt) || lt == "bool") && (IsInt(rt) || rt == "bool"); }
    static (byte Op, int Ext) AluOf(string op) => op switch {
        "+" => (0x01, 0), "-" => (0x29, 5), "&" => (0x21, 4), "|" => (0x09, 1), "^" => (0x31, 6), _ => (0x39, 7),
    };
    // condition-code nibble for a comparison operator (signed)
    static int CcOf(string op) => op switch { "==" => 4, "!=" => 5, "<" => 0xc, ">=" => 0xd, "<=" => 0xe, ">" => 0xf, _ => -1 };
    static string Swapped(string op) => op switch { "<" => ">", ">" => "<", "<=" => ">=", ">=" => "<=", _ => op };

    // Jump to label when cond evaluates to `when`. Comparisons become cmp + jcc,
    // && / || short-circuit without materializing 0/1.
    void EmitJump(Expr c, bool when, string label) {
        switch (c) {
            case BoolLit bl: if (bl.Val == when) _a.JmpRel(label); return;
            case Unary { Op: "!" } u: EmitJump(u.E, !when, label); return;
            case Binary { Op: "&&" } a:
                if (!when) { EmitJump(a.L, false, label); EmitJump(a.R, false, label); }
                else { var skip = NewLabel("and"); EmitJump(a.L, false, skip); EmitJump(a.R, true, label); _a.Label(skip); }
                return;
            case Binary { Op: "||" } o:
                if (when) { EmitJump(o.L, true, label); EmitJump(o.R, true, label); }
                else { var skip = NewLabel("or"); EmitJump(o.L, true, skip); EmitJump(o.R, false, label); _a.Label(skip); }
                return;
            case Binary b when CcOf(b.Op) >= 0 && TypeOf(b.L) == "double":
                EmitDoubleJump(b, when, label);
                return;
            case Binary b when CcOf(b.Op) >= 0 && !(IsStr(TypeOf(b.L)) && IsStr(TypeOf(b.R))): {
                string op = EmitCompare(b);
                int cc = CcOf(op);
                _a.Jcc(when ? cc : cc ^ 1, label);           // x86 cc pairs differ only in the low bit
                return;
            }
        }
        EmitExpr(c); _a.TestRaxRax();
        if (when) _a.JnzRel(label); else _a.JzRel(label);
    }

    // Emits the flags for a comparison; returns the operator to test (may be swapped).
    string EmitCompare(Binary b) {
        if (InReg(b.L, out int lr) && IsOperand(b.R)) { AluOperand(0x39, 7, lr, b.R); return b.Op; }
        if (IsOperand(b.R)) { EmitExpr(b.L); AluOperand(0x39, 7, 0, b.R); return b.Op; }
        if (Reorderable(b.L)) { EmitExpr(b.R); AluOperand(0x39, 7, 0, b.L); return Swapped(b.Op); }
        EmitExpr(b.L); _a.PushRax();
        EmitExpr(b.R); _a.MovRcxRax(); _a.PopRax();
        _a.CmpRaxRcx();
        return b.Op;
    }

    void EmitReturn(RetStmt r) {
        if (r.E != null) EmitExpr(r.E); else _a.MovRaxImm(0);
        if (_cur.Throws != null) _a.MovRdxImm(1);   // ok flag
        EmitFuncEpilogue();
    }

    void EmitThrow(ThrowStmt th) {
        int off = InternString($"{th.Err}: {th.Msg}\n");
        // value irrelevant; rdx=0 (error), rsi=msg ptr (patched at layout via reloc list)
        _a.MovRaxImm(0);
        AddStrReloc(off);   // emits: mov rsi, imm64(addr)
        _a.MovRdxImm(0);
        EmitFuncEpilogue();
    }

    // string address relocations: we emit mov rsi, imm64 with a placeholder, then
    // record the code position + data offset; patched in Layout once addresses known.
    readonly List<(int CodePos, int DataOff)> _strRelocs = new();
    void AddStrReloc(int dataOff) {
        _a.Db(0x48, 0xbe);                 // mov rsi, imm64
        _strRelocs.Add((_a.Pos, dataOff));
        for (int i = 0; i < 8; i++) _a.Db(0);
    }
    void AddStrRelocReg(int dataOff, byte regOpcode) {
        _a.Db(0x48, regOpcode);
        _strRelocs.Add((_a.Pos, dataOff));
        for (int i = 0; i < 8; i++) _a.Db(0);
    }

    void EmitExpr(Expr e) {
        switch (e) {
            case IntLit i:  _a.MovRI(0, i.Val); break;
            case FloatLit fl: _a.MovRI(0, BitConverter.DoubleToInt64Bits(fl.Val)); break;   // doubles live in GPRs as raw bits
            case BoolLit b: _a.MovRI(0, b.Val ? 1 : 0); break;
            case StrLit sl: {                  // value = address of null-terminated bytes
                int off = InternString(sl.Val);
                AddStrRelocReg(off, 0xb8);     // mov rax, imm64(addr)
                break;
            }
            case VarRef v:  LoadVar(0, v.Name); break;
            case NullLit: _a.MovRaxImm(0); break;
            case NewStruct ns: EmitNewStruct(ns); break;
            case InlineExpr ie: {
                for (int k = 0; k < ie.Body.Count; k++) {
                    if (k == ie.Body.Count - 1 && ie.Body[k] is InlineRet last) { if (last.E != null) EmitExpr(last.E); }
                    else EmitStmt(ie.Body[k]);
                }
                _a.Label(ie.End);
                break;
            }
            case Unary u:
                EmitExpr(u.E);
                if (u.Op == "-" && TypeOf(u.E) == "double") _a.Db(0x48, 0x0f, 0xba, 0xf8, 0x3f);   // btc rax, 63: flip the sign bit
                else if (u.Op == "-") _a.NegRax();
                else { _a.TestRaxRax(); _a.SetccRax(0x94); } // sete: !x
                break;
            case Binary b: EmitBinary(b); break;
            case CallExpr c: EmitCall(c); break;
            case TryExpr t: EmitTry(t); break;
            case NewArray na: {
                EmitExpr(na.Size);                 // rax = n
                _a.PushRax();                       // save n
                _a.MovRdiRax();                     // rdi = n
                _a.LeaRdiArrayBytes();              // rdi = n*8 + 8 (header + elements)
                _a.CallRel("alloc");                // rax = base
                _a.PopRcx();                        // rcx = n
                _a.MovQwordPtrRaxRcx();             // [base] = n   (length header)
                // zero the elements: memory can be reused after heapreset()
                _a.PushRax();
                _a.Lea(7, 0, 8);                    // rdi = base + 8
                _a.Db(0x31, 0xc0);                  // xor eax, eax
                _a.Db(0xf3, 0x48, 0xab);            // rep stosq  (rcx = n qwords)
                _a.PopRax();
                _a.AddRaxImm8(8);                   // rax = base + 8 (element pointer)
                break;
            }
            case IndexExpr ix: {
                // one load with an x86 address: [base + index*size + disp]
                var at = TypeOf(ix.Arr);
                if (IsStr(at)) EmitLoadAt(EmitElemAddr(ix.Arr, ix.Index, 1), 1, false);     // s[i] -> byte
                else if (IsPtr(at)) {
                    string pe = Pointee(at);
                    EmitLoadAt(EmitElemAddr(ix.Arr, ix.Index, Width(pe)), Width(pe), Signed(pe));
                } else {                                                                     // array: 8-byte elements
                    var m = EmitElemAddr(ix.Arr, ix.Index, 8);
                    EmitBoundsCheck(m, ix.Line);
                    EmitLoadAt(m, 8, true);
                }
                break;
            }
            case LengthExpr le: {
                if (IsStr(TypeOf(le.Arr))) {
                    EmitExpr(le.Arr); _a.Db(0x48,0x89,0xc7); // mov rdi, rax
                    _a.CallRel("strlen");                     // rax = length
                } else {
                    EmitExpr(le.Arr);                    // rax = ptr
                    _a.LoadLenRax();                     // rax = [ptr - 8]
                }
                break;
            }
            case AddrOf ao when !_locals.ContainsKey(ao.Name):   // &Callback / &Handler: what C or the CPU should call
                _a.MovRegLabelAddr(RAX, _funcs[ao.Name].IsInterrupt ? ao.Name + "$isr" : _target == Target.Windows ? ao.Name + "$c" : ao.Name);
                break;
            case AddrOf ao:
                _a.LeaLocal(_locals[ao.Name].Off);   // rax = &local
                break;
            case Deref de: {
                string pt = Pointee(TypeOf(de.Ptr));
                if (IsFieldPtr(de.Ptr, out var fobj, out int foff)) {     // struct field: one load at [obj + off]
                    int bas = InReg(fobj, out int fr) ? fr : RAX;
                    if (bas == RAX) EmitExpr(fobj);
                    EmitLoadAt(new MemRef(bas, -1, 1, foff), Width(pt), Signed(pt));
                    break;
                }
                EmitExpr(de.Ptr);                    // rax = pointer
                EmitLoad(pt);                        // rax = *pointer (sized)
                break;
            }
            case CastExpr ce: {
                EmitExpr(ce.E);                       // reinterpret: value unchanged in rax
                string from = TypeOf(ce.E);
                if (ce.TargetType == "double" && IsInt(from)) {
                    _a.Db(0xf2, 0x48, 0x0f, 0x2a, 0xc0);   // cvtsi2sd xmm0, rax
                    _a.Db(0x66, 0x48, 0x0f, 0x7e, 0xc0);   // movq rax, xmm0
                } else if (from == "double" && IsInt(ce.TargetType)) {
                    _a.Db(0x66, 0x48, 0x0f, 0x6e, 0xc0);   // movq xmm0, rax
                    _a.Db(0xf2, 0x48, 0x0f, 0x2c, 0xc0);   // cvttsd2si rax, xmm0 (truncates toward zero)
                }
                break;
            }
            case AwaitExpr aw:
                EmitExpr(aw.Task);                    // rax = task record
                _a.MovRdiRax();
                _a.CallRel("__rt_await");             // rax = result (suspends if pending)
                break;
            case YieldExpr:
                _a.CallRel("__rt_yield");
                _a.MovRaxImm(0);
                break;
            case WhenAllExpr wa:
                foreach (var t in wa.Tasks) {         // waiting for each in turn == waiting for all
                    EmitExpr(t); _a.MovRdiRax(); _a.CallRel("__rt_await");
                }
                _a.MovRaxImm(0);
                break;
            case SpawnExpr sp: {
                // thr_spawn(rdi = &Func, rsi = argument) -> rax = handle (Threads.cs)
                if (sp.Arg != null) { EmitExpr(sp.Arg); _a.MovRR(6, 0); } else _a.MovRI(6, 0);
                _a.MovRegLabelAddr(7, sp.FuncName);
                _a.CallRel("thr_spawn");
                break;
            }
            default: throw new ForgeException("cannot emit this expression here", e.Line);
        }
    }

    // double op double. Values are raw bits in rax; the operation itself runs in
    // xmm0 (left) / xmm1 (right). Uses only rax, rcx and xmm0/xmm1.
    void EmitDoubleBinary(Binary b) {
        LoadDoubleOperands(b);
        byte arith = b.Op switch { "+" => 0x58, "*" => 0x59, "-" => 0x5c, "/" => 0x5e, _ => 0 };
        if (arith != 0) {
            _a.Db(0xf2, 0x0f, arith, 0xc1);              // addsd/mulsd/subsd/divsd xmm0, xmm1
            _a.Db(0x66, 0x48, 0x0f, 0x7e, 0xc0);        // movq rax, xmm0
            return;
        }
        // Comparisons. ucomisd sets ZF=PF=CF=1 when either side is NaN, so every
        // comparison with NaN must come out false except !=. a < b is tested as b > a,
        // because "above" (CF=0, ZF=0) is false for NaN.
        switch (b.Op) {
            case ">":  _a.Db(0x66, 0x0f, 0x2e, 0xc1); _a.Db(0x0f, 0x97, 0xc0); break;   // ucomisd xmm0,xmm1; seta al
            case ">=": _a.Db(0x66, 0x0f, 0x2e, 0xc1); _a.Db(0x0f, 0x93, 0xc0); break;   // setae al
            case "<":  _a.Db(0x66, 0x0f, 0x2e, 0xc8); _a.Db(0x0f, 0x97, 0xc0); break;   // ucomisd xmm1,xmm0; seta al
            case "<=": _a.Db(0x66, 0x0f, 0x2e, 0xc8); _a.Db(0x0f, 0x93, 0xc0); break;   // setae al
            case "==":
                _a.Db(0x66, 0x0f, 0x2e, 0xc1);
                _a.Db(0x0f, 0x94, 0xc0); _a.Db(0x0f, 0x9b, 0xc1); _a.Db(0x20, 0xc8);   // sete al; setnp cl; and al, cl
                break;
            case "!=":
                _a.Db(0x66, 0x0f, 0x2e, 0xc1);
                _a.Db(0x0f, 0x95, 0xc0); _a.Db(0x0f, 0x9a, 0xc1); _a.Db(0x08, 0xc8);   // setne al; setp cl; or al, cl
                break;
            default: throw new ForgeException($"'{b.Op}' is not defined for double", b.Line);
        }
        _a.Db(0x0f, 0xb6, 0xc0);                        // movzx eax, al
    }

    // A double operand whose evaluation touches only rax and has no side effects:
    // a literal, or a local that nothing can change behind our back.
    bool DoublePlain(Expr e) => e is FloatLit || (e is VarRef v && _locals.ContainsKey(v.Name) && !_addrTaken.Contains(v.Name));

    // left -> xmm0, right -> xmm1. A plain operand is loaded last, straight into its
    // register, so no push/pop is needed (evaluation order can't be observed).
    void LoadDoubleOperands(Binary b) {
        if (DoublePlain(b.R)) {
            EmitExpr(b.L); _a.Db(0x66, 0x48, 0x0f, 0x6e, 0xc0);   // movq xmm0, rax
            EmitExpr(b.R); _a.Db(0x66, 0x48, 0x0f, 0x6e, 0xc8);   // movq xmm1, rax
        } else if (DoublePlain(b.L)) {
            EmitExpr(b.R); _a.Db(0x66, 0x48, 0x0f, 0x6e, 0xc8);   // movq xmm1, rax
            EmitExpr(b.L); _a.Db(0x66, 0x48, 0x0f, 0x6e, 0xc0);   // movq xmm0, rax
        } else {
            EmitExpr(b.L); _a.PushRax();
            EmitExpr(b.R); _a.Db(0x66, 0x48, 0x0f, 0x6e, 0xc8);   // movq xmm1, rax
            _a.PopRax();   _a.Db(0x66, 0x48, 0x0f, 0x6e, 0xc0);   // movq xmm0, rax
        }
    }

    // if/while on a double comparison: branch on the ucomisd flags directly.
    // NaN (ZF=PF=CF=1) makes every comparison false except !=.
    void EmitDoubleJump(Binary b, bool when, string label) {
        LoadDoubleOperands(b);
        string op = b.Op;
        if (op is "<" or "<=") { op = op == "<" ? ">" : ">="; _a.Db(0x66, 0x0f, 0x2e, 0xc8); }   // ucomisd xmm1, xmm0
        else _a.Db(0x66, 0x0f, 0x2e, 0xc1);                                                     // ucomisd xmm0, xmm1
        switch (op) {
            case ">":  _a.Jcc(when ? 0x7 : 0x6, label); break;    // ja / jbe
            case ">=": _a.Jcc(when ? 0x3 : 0x2, label); break;    // jae / jb
            case "==":
                if (when) { string skip = NewLabel("dq"); _a.Jcc(0xa, skip); _a.Jcc(0x4, label); _a.Label(skip); }   // jp skip; je
                else { _a.Jcc(0xa, label); _a.Jcc(0x5, label); }                                                  // jp; jne
                break;
            case "!=":
                if (when) { _a.Jcc(0xa, label); _a.Jcc(0x5, label); }                                             // jp; jne
                else { string skip = NewLabel("dn"); _a.Jcc(0xa, skip); _a.Jcc(0x4, label); _a.Label(skip); }     // jp skip; je
                break;
        }
    }

    // ptr (rax) +/- (index * size) where index is the integer operand
    void EmitPtrArith(Expr ptr, Expr idx, int size, string op) {
        EmitExpr(ptr); _a.PushRax();
        EmitExpr(idx); _a.MovRcxRax();        // rcx = index
        if (size > 1) _a.ShlRcxK((byte)Log2(size));
        _a.PopRax();                           // rax = pointer
        if (op == "+") _a.AddRaxRcx2();        // rax += scaled index
        else _a.Db(0x48, 0x29, 0xc8);          // sub rax, rcx
    }

    void EmitBinary(Binary b) {
        if (b.Op is "&&" or "||") { EmitLogical(b); return; }
        if (TypeOf(b.L) == "double") { EmitDoubleBinary(b); return; }   // both sides are double after type checking

        // string operations
        var blt = TypeOf(b.L); var brt = TypeOf(b.R);
        if (IsStr(blt) && IsStr(brt)) {
            if (b.Op == "+") {                       // concat(a,b)
                EmitExpr(b.L); _a.PushRax();
                EmitExpr(b.R); _a.Db(0x48,0x89,0xc6); // mov rsi, rax (b)
                _a.PopRax(); _a.Db(0x48,0x89,0xc7);   // mov rdi, rax (a)
                _a.CallRel("str_concat");
                return;
            }
            if (b.Op is "==" or "!=") {              // streq(a,b)
                EmitExpr(b.L); _a.PushRax();
                EmitExpr(b.R); _a.Db(0x48,0x89,0xc6); // mov rsi, rax
                _a.PopRax(); _a.Db(0x48,0x89,0xc7);   // mov rdi, rax
                _a.CallRel("str_eq");                 // rax = 1 if equal
                if (b.Op == "!=") { _a.Db(0x48,0x83,0xf0,0x01); } // xor rax, 1
                return;
            }
        }

        // pointer arithmetic: scale the integer side by the pointee width
        if (b.Op is "+" or "-") {
            var lt = TypeOf(b.L); var rt = TypeOf(b.R);
            if (IsPtr(lt) && IsInt(rt)) { EmitPtrArith(b.L, b.R, Width(Pointee(lt)), b.Op); return; }
            if (b.Op == "+" && IsInt(lt) && IsPtr(rt)) { EmitPtrArith(b.R, b.L, Width(Pointee(rt)), "+"); return; }
        }

        if (IsIntOp(b) && EmitBinaryFast(b)) return;

        if (IsIntOp(b) && b.Op is not ("/" or "%") && OnlyRaxRcx(b.R)) {
            EmitExpr(b.L); _a.MovRR(RDX, RAX);   // left waits in rdx: the right side never touches it
            EmitExpr(b.R); _a.MovRcxRax();
            _a.MovRR(RAX, RDX);
        } else {
            EmitExpr(b.L); _a.PushRax();
            EmitExpr(b.R); _a.MovRcxRax();   // rcx = right
            _a.PopRax();                      // rax = left
        }
        // now compute on rax (left) and rcx (right)
        switch (b.Op) {
            case "+": _a.Db(0x48,0x01,0xc8); break;                 // add rax, rcx
            case "-": _a.Db(0x48,0x29,0xc8); break;                 // sub rax, rcx
            case "*": _a.Db(0x48,0x0f,0xaf,0xc1); break;            // imul rax, rcx
            case "/": _a.Cqo(); _a.IdivRcx(); break;                // rax = rax/rcx
            case "%": _a.Cqo(); _a.IdivRcx(); _a.Db(0x48,0x89,0xd0); break; // mov rax, rdx
            case "&": _a.Db(0x48,0x21,0xc8); break;                 // and rax, rcx
            case "|": _a.Db(0x48,0x09,0xc8); break;                 // or  rax, rcx
            case "^": _a.Db(0x48,0x31,0xc8); break;                 // xor rax, rcx
            case "<<": _a.Db(0x48,0xd3,0xe0); break;                // shl rax, cl
            case ">>": _a.Db(0x48,0xd3,0xe8); break;                // shr rax, cl
            case "==": _a.CmpRaxRcx(); _a.SetccRax(0x94); break;    // sete
            case "!=": _a.CmpRaxRcx(); _a.SetccRax(0x95); break;    // setne
            case "<":  _a.CmpRaxRcx(); _a.SetccRax(0x9c); break;    // setl
            case ">":  _a.CmpRaxRcx(); _a.SetccRax(0x9f); break;    // setg
            case "<=": _a.CmpRaxRcx(); _a.SetccRax(0x9e); break;    // setle
            case ">=": _a.CmpRaxRcx(); _a.SetccRax(0x9d); break;    // setge
        }
    }

    // Integer ops where one side is an immediate or a variable: no push/pop, the
    // operand goes straight into the instruction (add rax, rbx / cmp rax, 2 / lea).
    bool EmitBinaryFast(Binary b) {
        string op = b.Op;
        if (op is "+" or "-" && InReg(b.L, out int lr) && IsImm(b.R, out int k)) { _a.Lea(0, lr, op == "+" ? k : -k); return true; }
        if (op == "+" && IsImm(b.L, out int k2) && InReg(b.R, out int rr)) { _a.Lea(0, rr, k2); return true; }
        bool alu = op is "+" or "-" or "&" or "|" or "^";
        bool cmp = CcOf(op) >= 0;
        bool commut = op is "+" or "&" or "|" or "^" or "*" or "==" or "!=";
        if (!(alu || cmp || op is "*" or "<<" or ">>" or "/" or "%")) return false;
        Expr left, right; bool swapped = false;
        if (IsOperand(b.R)) { left = b.L; right = b.R; }
        else if (Reorderable(b.L) && (commut || cmp || op == "-")) { left = b.R; right = b.L; swapped = true; }
        else return false;
        if (op is "<<" or ">>" && !IsImm(right, out _)) return false;
        if (op is "/" or "%" && swapped) return false;
        EmitExpr(left);
        switch (op) {
            case "-" when swapped: _a.NegRax(); AluOperand(0x01, 0, 0, right); break;   // L - R = -R + L
            case "+" or "-" or "&" or "|" or "^": { var (o, e) = AluOf(op); AluOperand(o, e, 0, right); break; }
            case "*":
                if (IsImm(right, out int m)) _a.ImulRRI(0, 0, m);
                else if (InReg(right, out int mr)) _a.ImulRR(0, mr);
                else _a.ImulRM(0, _locals[((VarRef)right).Name].Off);
                break;
            case "<<": IsImm(right, out int sl); _a.ShiftRI(4, 0, (byte)(sl & 63)); break;
            case ">>": IsImm(right, out int sr); _a.ShiftRI(5, 0, (byte)(sr & 63)); break;
            case "/" or "%":
                if (IsImm(right, out int dv) && dv >= 2) { EmitDivConst(dv, op == "%"); break; }
                LoadOperand(1, right); _a.Cqo(); _a.IdivRcx();
                if (op == "%") _a.Db(0x48, 0x89, 0xd0);             // mov rax, rdx
                break;
            default:
                AluOperand(0x39, 7, 0, right);
                _a.SetccRax((byte)(0x90 | CcOf(swapped ? Swapped(op) : op)));
                break;
        }
        return true;
    }

    // rax = rax / d  (or rax % d), signed, rounding toward zero like idiv, for a
    // constant d >= 2 - without the ~40-cycle idiv: a shift for powers of two, else
    // a multiply by a "magic" reciprocal (Hacker's Delight 10-1). Uses rcx, rdx.
    void EmitDivConst(int d, bool rem) {
        if ((d & (d - 1)) == 0) {
            int k = Log2Big(d);
            _a.MovRR(RCX, RAX); _a.Db(0x48, 0xc1, 0xf9, 63);        // sar rcx, 63   (all ones if negative)
            _a.ShiftRI(5, RCX, (byte)(64 - k));                     // shr rcx, 64-k (d-1 if negative)
            _a.AluRR(0x01, RCX, RAX);                               // rcx = x + bias
            _a.Db(0x48, 0xc1, 0xf9, (byte)k);                       // sar rcx, k    = quotient
            if (rem) { _a.ShiftRI(4, RCX, (byte)k); _a.AluRR(0x29, RAX, RCX); }   // x - q*d
            else _a.MovRR(RAX, RCX);
            return;
        }
        var (m, sh) = SignedMagic(d);
        _a.MovRR(RCX, RAX);                                         // rcx = x
        _a.MovRI(RAX, m);
        _a.Db(0x48, 0xf7, 0xe9);                                    // imul rcx: rdx:rax = m * x
        if (m < 0) _a.AluRR(0x01, RDX, RCX);                        // add rdx, x
        if (sh > 0) _a.Db(0x48, 0xc1, 0xfa, (byte)sh);              // sar rdx, sh
        _a.MovRR(RAX, RCX); _a.ShiftRI(5, RAX, 63);                 // rax = 1 if x < 0
        _a.AluRR(0x01, RDX, RAX);                                   // rdx = quotient
        if (rem) { _a.ImulRRI(RDX, RDX, d); _a.MovRR(RAX, RCX); _a.AluRR(0x29, RAX, RDX); }
        else _a.MovRR(RAX, RDX);
    }
    static int Log2Big(int d) { int k = 0; while ((1 << k) != d) k++; return k; }
    // magic multiplier and shift for signed 64-bit division by d >= 2
    static (long M, int Shift) SignedMagic(long d) {
        const ulong two63 = 1UL << 63;
        ulong ad = (ulong)d;
        ulong anc = two63 - 1 - two63 % ad;
        int p = 63;
        ulong q1 = two63 / anc, r1 = two63 - q1 * anc;
        ulong q2 = two63 / ad, r2 = two63 - q2 * ad;
        ulong delta;
        do {
            p++;
            q1 *= 2; r1 *= 2; if (r1 >= anc) { q1++; r1 -= anc; }
            q2 *= 2; r2 *= 2; if (r2 >= ad) { q2++; r2 -= ad; }
            delta = ad - r2;
        } while (q1 < delta || (q1 == delta && r1 == 0));
        return ((long)(q2 + 1), p - 64);
    }

    // Small shapes whose code uses only rax and rcx (no rdx, no stack, no calls):
    // a[i] / p[i+k] with plain parts, and  x op y  on plain operands (one fast-path op).
    bool OnlyRaxRcx(Expr e) => e switch {
        IndexExpr ix => PlainAddr(ix.Arr, ix.Index),
        Binary b => b.Op is "+" or "-" or "&" or "|" or "^" or "*" && IsIntOp(b) && IsOperand(b.L) && IsOperand(b.R),
        _ => false,
    };

    // Evaluating a "light" expression touches only rax and rcx, so call arguments
    // made of them can be computed straight into rdi/rsi/rdx/rcx (rcx last).
    bool IsLight(Expr e) => e switch {
        IntLit or BoolLit or NullLit or VarRef => true,
        Unary u => IsLight(u.E),
        CastExpr ce => IsLight(ce.E),
        Binary b => b.Op is "+" or "-" or "&" or "|" or "^" or "*" or "<" or ">" or "<=" or ">=" or "==" or "!="
                    && IsIntOp(b) && IsLight(b.L) && IsLight(b.R)
                    && (IsOperand(b.R) || Reorderable(b.L)),        // the fast path: never parks a value in rdx
        _ => false,
    };

    void EmitLogical(Binary b) {
        string end = NewLabel("logic");
        EmitExpr(b.L);
        _a.TestRaxRax();
        if (b.Op == "&&") {
            // if left==0 -> result 0
            string isZero = NewLabel("and0");
            _a.JzRel(isZero);
            EmitExpr(b.R); _a.TestRaxRax(); _a.SetccRax(0x95); // setne -> normalize to 0/1
            _a.JmpRel(end);
            _a.Label(isZero); _a.MovRaxImm(0);
        } else {
            string isOne = NewLabel("or1");
            _a.JnzRel(isOne);
            EmitExpr(b.R); _a.TestRaxRax(); _a.SetccRax(0x95);
            _a.JmpRel(end);
            _a.Label(isOne); _a.MovRaxImm(1);
        }
        _a.Label(end);
    }

    // load *(rax) -> rax, sized + sign-correct by pointee type
    void EmitLoad(string pointee) {
        switch (Width(pointee)) {
            case 1: if (Signed(pointee)) _a.LoadI8(); else _a.LoadU8(); break;
            case 2: if (Signed(pointee)) _a.LoadI16(); else _a.LoadU16(); break;
            case 4: if (Signed(pointee)) _a.LoadI32(); else _a.LoadU32(); break;
            default: _a.Load64(); break;
        }
    }
    // new T { f = v, ... }: zeroed memory from this thread's arena, then the initializers
    void EmitNewStruct(NewStruct ns) {
        int size = Math.Max(8, (_structs[ns.Type].Size + 7) & ~7);
        _a.MovRI(RDI, size); _a.CallRel("alloc");          // rax = rdi = memory
        _a.PushRax();
        _a.MovRI(RCX, size / 8); _a.Db(0x31, 0xc0); _a.Db(0xf3, 0x48, 0xab);   // xor eax,eax; rep stosq
        foreach (var (fn, fe) in ns.Inits) {
            var (off, ft) = FieldOf(ns.Type, fn, ns.Line);
            EmitExpr(fe); _a.MovRR(RDX, RAX);
            _a.Ld(RAX, RSP, 0);                                 // the object
            EmitStoreAt(new MemRef(RAX, -1, 1, off), Width(ft));
        }
        _a.PopRax();
    }

    // ---- element addressing: arr[idx] as one x86 memory operand [base + index*scale + disp]
    readonly record struct MemRef(int Base, int Index, int Scale, int Disp);

    // e reads a variable and nothing else (no code beyond one mov)
    bool IsVarLoad(Expr e) => e is VarRef v && _locals.ContainsKey(v.Name);
    // evaluating e may assign the variable 'name' (only possible through inlined bodies)
    static bool Writes(Expr e, string name) {
        bool w = false;
        Walker.WalkExpr(e, null, s => { if (s is Assign a && a.Name == name || s is VarDecl d && d.Name == name) w = true; });
        return w;
    }
    // can be loaded with plain movs into rax/rcx without touching rdx or the stack
    bool IsPlain(Expr e) => IsImm(e, out _) || IsVarLoad(e);

    // Emits whatever is needed to address element idx of arr (elements w bytes wide)
    // and returns the operand. Uses only rax and rcx (plus the stack in the general case).
    MemRef EmitElemAddr(Expr arr, Expr idx, int w) {
        Expr? core = idx; long k = 0;
        if (idx is Binary { Op: "+" or "-" } bb && IsImm(bb.R, out int kv) && IsInt(TypeOf(bb.L))) {
            core = bb.L; k = bb.Op == "+" ? kv : -(long)kv;              // a[i + 1] -> disp folds the 1
        }
        if (core != null && IsImm(core, out int cv)) { k += cv; core = null; }
        if (!Asm.Fits32(k * w)) { core = idx; k = 0; }
        int disp = (int)(k * w);
        string? arrVar = arr is VarRef av ? av.Name : null, coreVar = core is VarRef cvr ? cvr.Name : null;

        if (core == null) {
            if (InReg(arr, out int rb)) return new(rb, -1, 1, disp);
            EmitExpr(arr); return new(RAX, -1, 1, disp);
        }
        if (InReg(core, out int ri) && !Writes(arr, coreVar!)) {
            if (InReg(arr, out int rb)) return new(rb, ri, w, disp);
            EmitExpr(arr); return new(RAX, ri, w, disp);
        }
        if (InReg(arr, out int rb2) && !Writes(core, arrVar!)) { EmitExpr(core); return new(rb2, RAX, w, disp); }
        if (IsVarLoad(core) && !Writes(arr, coreVar!)) { EmitExpr(arr); LoadVar(RCX, coreVar!); return new(RAX, RCX, w, disp); }
        if (IsVarLoad(arr) && !Writes(core, arrVar!)) { EmitExpr(core); _a.MovRR(RCX, RAX); LoadVar(RAX, arrVar!); return new(RAX, RCX, w, disp); }
        EmitExpr(arr); _a.PushRax(); EmitExpr(core); _a.MovRR(RCX, RAX); _a.PopRax();
        return new(RAX, RCX, w, disp);
    }
    // the address computation above emits nothing but movs (safe to do after evaluating a value in rdx)
    bool PlainAddr(Expr arr, Expr idx) {
        Expr core = idx;
        if (idx is Binary { Op: "+" or "-" } bb && IsImm(bb.R, out _)) core = bb.L;
        return IsPlain(arr) && IsPlain(core);
    }

    // rax = element at m, width/sign from the element type
    void EmitLoadAt(MemRef m, int width, bool signed) {
        var (b, i, s, d) = (m.Base, m.Index, m.Scale, m.Disp);
        switch (width) {
            case 1: _a.MemSib(0x0f, signed ? 0xbe : 0xb6, true, false, RAX, b, i, s, d); break;  // movzx/movsx rax, byte
            case 2: _a.MemSib(0x0f, signed ? 0xbf : 0xb7, true, false, RAX, b, i, s, d); break;  // movzx/movsx rax, word
            case 4: if (signed) _a.MemSib(0x63, 0, true, false, RAX, b, i, s, d);                // movsxd rax, dword
                    else _a.MemSib(0x8b, 0, false, false, RAX, b, i, s, d); break;              // mov eax, dword
            default: _a.MemSib(0x8b, 0, true, false, RAX, b, i, s, d); break;                    // mov rax, qword
        }
    }
    // element at m = rdx
    void EmitStoreAt(MemRef m, int width) {
        var (b, i, s, d) = (m.Base, m.Index, m.Scale, m.Disp);
        switch (width) {
            case 1: _a.MemSib(0x88, 0, false, false, RDX, b, i, s, d); break;
            case 2: _a.MemSib(0x89, 0, false, true, RDX, b, i, s, d); break;
            case 4: _a.MemSib(0x89, 0, false, false, RDX, b, i, s, d); break;
            default: _a.MemSib(0x89, 0, true, false, RDX, b, i, s, d); break;
        }
    }
    // arr[idx] = e  for an element of the given width
    // checkLine >= 0: arr is an array, bounds-checked (the check reports that line)
    void EmitElemStore(Expr arr, Expr idx, Expr e, int width, int checkLine = -1) {
        void Store(MemRef m) { if (checkLine >= 0) EmitBoundsCheck(m, checkLine); EmitStoreAt(m, width); }
        string? arrVar = arr is VarRef av ? av.Name : null;
        Expr core = idx is Binary { Op: "+" or "-" } bb && IsImm(bb.R, out _) ? bb.L : idx;
        string? coreVar = core is VarRef cv ? cv.Name : null;
        bool eSafe = (arrVar == null || !Writes(e, arrVar)) && (coreVar == null || !Writes(e, coreVar));
        if (PlainAddr(arr, idx) && eSafe) {                     // value first, then movs for the address
            EmitExpr(e); _a.MovRR(RDX, RAX);
            Store(EmitElemAddr(arr, idx, width));
            return;
        }
        if (IsPlain(e) && !(e is VarRef ev && (Writes(arr, ev.Name) || Writes(idx, ev.Name)))) {
            var m = EmitElemAddr(arr, idx, width);             // address first, then the value
            LoadOperand(RDX, e);
            Store(m);
            return;
        }
        EmitExpr(arr); _a.PushRax();                           // general case: arr, idx, value in source order
        EmitExpr(idx); _a.PushRax();
        EmitExpr(e); _a.MovRR(RDX, RAX);
        _a.PopRcx(); _a.PopRax();
        Store(new MemRef(RAX, RCX, width, 0));
    }

    // store rdx -> *(rax), sized by pointee type
    void EmitStore(string pointee) {
        switch (Width(pointee)) {
            case 1: _a.Store8(); break;
            case 2: _a.Store16(); break;
            case 4: _a.Store32(); break;
            default: _a.Store64(); break;
        }
    }

    // Evaluate each argument and place it in the named register (rdi,rsi,rdx).
    // Evaluates left-to-right onto the stack, then pops into the targets.
    void EmitArgsTo(CallExpr c, params string[] regs) {
        static int RegNo(string r) => r switch { "rdi" => RDI, "rsi" => RSI, "rdx" => RDX, _ => RCX };
        // Literals and variables nothing else can change are loaded straight into
        // their register at the end; everything else goes through the stack.
        bool Direct(Expr e) => IsImm(e, out _) || (e is VarRef v && !_addrTaken.Contains(v.Name));
        var complex = new List<int>();
        for (int i = 0; i < c.Args.Count; i++) if (!Direct(c.Args[i])) complex.Add(i);
        if (complex.Count == 1) {
            EmitExpr(c.Args[complex[0]]); _a.MovRR(RegNo(regs[complex[0]]), RAX);
        } else {
            foreach (int i in complex) { EmitExpr(c.Args[i]); _a.PushRax(); }
            for (int k = complex.Count - 1; k >= 0; k--) { _a.PopRax(); _a.MovRR(RegNo(regs[complex[k]]), RAX); }
        }
        for (int i = 0; i < c.Args.Count; i++) if (Direct(c.Args[i])) LoadOperand(RegNo(regs[i]), c.Args[i]);
    }

    void EmitCall(CallExpr c) {
        if (_externs.TryGetValue(c.Name, out var exd)) { EmitExternCall(c, exd); return; }
        if (_funcs.ContainsKey(c.Name)) goto userFunc;   // the program's own function shadows a builtin
        if (c.Name == "malloc") {                 // builtin: raw bytes from the heap
            EmitExpr(c.Args[0]); _a.MovRdiRax();  // rdi = byte count
            _a.CallRel("alloc");                  // rax = pointer
            return;
        }
        if (c.Name == "heapbase") {               // stable shared anchor: the heap base VA
            EmitSlotAddr(0xb8, 3);                // mov rax, imm64(heapBase)
            _a.Db(0x48, 0x83, 0xc0, HeapUserOff); // add rax, 64 (skip the print scratch)
            return;
        }
        if (EmitBareMetalBuiltin(c)) return;
        if (c.Name == "heapmark" && Threaded) {   // a point in this thread's arena to free back to
            EmitLoadBlockR10(); _a.Ld(RAX, R10, 0);
            return;
        }
        if (c.Name == "heapmark") {               // kernel: the bump pointer
            EmitSlotAddr(0xb9, 2); _a.Ld(RAX, RCX, 0);
            return;
        }
        if (c.Name == "heapreset" && c.Args.Count == 1) {   // free everything allocated after the mark
            EmitExpr(c.Args[0]);
            if (Threaded) { _a.MovRR(RSI, RAX); EmitLoadBlockR10(); _a.CallRel("arena_reset_to"); }
            else { EmitSlotAddr(0xb9, 2); _a.St(RCX, 0, RAX); }
            return;
        }
        if (c.Name == "heapreset" && Threaded) {  // free this thread's allocations
            EmitLoadBlockR10(); _a.CallRel("arena_reset");
            return;
        }
        if (c.Name == "heapreset") {              // free everything malloc'd so far (bump back to start)
            EmitSlotAddr(0xb8, 3);                // mov rax, imm64(heapBase)
            _a.Db(0x48, 0x05); EmitImm32(HeapReserved); // add rax, imm32
            EmitSlotAddr(0xb9, 2);                // mov rcx, imm64(&heapPtr)
            _a.Db(0x48, 0x89, 0x01);              // mov [rcx], rax
            return;
        }
        // ---- string / IO / socket builtins: marshal args then call the runtime ----
        switch (c.Name) {
            case "str":      EmitExpr(c.Args[0]); _a.Db(0x48,0x89,0xc7); _a.CallRel("itoa"); return;       // rdi=n
            case "toint":    EmitExpr(c.Args[0]); _a.Db(0x48,0x89,0xc7); _a.CallRel("atoi"); return;       // rdi=s
            case "strlen":   EmitExpr(c.Args[0]); _a.Db(0x48,0x89,0xc7); _a.CallRel("strlen"); return;
            case "substr":   EmitArgsTo(c, "rdi","rsi","rdx"); _a.CallRel("substr"); return;                // s,start,len
            case "__sqrt":   EmitExpr(c.Args[0]); _a.Db(0x66,0x48,0x0f,0x6e,0xc0); _a.Db(0xf2,0x0f,0x51,0xc0); _a.Db(0x66,0x48,0x0f,0x7e,0xc0); return;   // movq xmm0,rax; sqrtsd xmm0,xmm0; movq rax,xmm0
            case "readfile": EmitExpr(c.Args[0]); _a.Db(0x48,0x89,0xc7); _a.CallRel("readfile"); return;   // rdi=path
            case "writefile":EmitArgsTo(c, "rdi","rsi"); _a.CallRel("writefile"); return;                  // path, data
            case "tcp_connect": _usesNet = true; EmitArgsTo(c, "rdi","rsi"); _a.CallRel("tcp_connect"); return;             // host, port
            case "tcp_listen":  _usesNet = true; EmitExpr(c.Args[0]); _a.Db(0x48,0x89,0xc7); _a.CallRel("tcp_listen"); return; // port
            case "accept":   _usesNet = true; EmitExpr(c.Args[0]); _a.Db(0x48,0x89,0xc7); _a.CallRel("sock_accept"); return;
            case "send":     _usesNet = true; EmitArgsTo(c, "rdi","rsi"); _a.CallRel("sock_send"); return;                  // fd, str
            case "recv":     _usesNet = true; EmitArgsTo(c, "rdi","rsi"); _a.CallRel("sock_recv"); return;                  // fd, max
            case "closefd":  _usesNet = true; EmitExpr(c.Args[0]); _a.Db(0x48,0x89,0xc7); _a.CallRel("sock_close"); return;
        }
        if (EmitThreadBuiltin(c)) return;         // join, atomics, lock, cpu_count, sleep_ms
        if (c.Name == "exit") { EmitExpr(c.Args[0]); EmitExit(); return; }
        if (c.Name == "__sched") {                // address of the scheduler block (.data)
            if (_schedOff < 0) _schedOff = InternZeros(AsyncRuntime.SchedBytes);
            AddStrRelocReg(_schedOff, 0xb8);      // mov rax, imm64
            return;
        }
        if (c.Name == "__ctx_switch") { EmitArgsTo(c, "rdi", "rsi"); _a.CallRel("__ctx_switch"); return; }
        if (c.Name == "__ctx_init")   { EmitArgsTo(c, "rdi", "rsi"); _a.CallRel("__ctx_init"); return; }
        userFunc:
        if (_funcs.TryGetValue(c.Name, out var af) && af.IsAsync) { EmitAsyncCall(c); return; }
        if (c.Name == "putc") {                   // builtin: emit one byte to stdout
            EmitExpr(c.Args[0]);                  // rax = value (low byte is the char)
            _a.PushRax();                         // byte now at [rsp] (little-endian low byte)
            _a.Db(0x48, 0x89, 0xe6);              // mov rsi, rsp
            _a.MovRdxImm(1);                      // length 1
            _a.CallRel("write_out");
            _a.Db(0x48, 0x83, 0xc4, 0x08);        // add rsp, 8 (pop)
            return;
        }
        var f = _funcs[c.Name];
        if (c.Args.All(IsLight)) {
            for (int i = 0; i < c.Args.Count; i++) {
                var a = c.Args[i]; int dst = ArgRegs[i];
                if (IsOperand(a)) LoadOperand(dst, a);
                else if (a is Binary { Op: "+" or "-" } ab && InReg(ab.L, out int br) && IsImm(ab.R, out int k))
                    _a.Lea(dst, br, ab.Op == "+" ? k : -k);
                else { EmitExpr(a); _a.MovRR(dst, 0); }
            }
            _a.CallRel(c.Name);
            return;
        }
        // evaluate args left to right onto the stack, then pop into registers
        foreach (var arg in c.Args) { EmitExpr(arg); _a.PushRax(); }
        // pop in reverse into rdi, rsi, rdx, rcx
        int n = c.Args.Count;
        byte[][] pops = {
            new byte[]{0x5f}, // pop rdi
            new byte[]{0x5e}, // pop rsi
            new byte[]{0x5a}, // pop rdx
            new byte[]{0x59}, // pop rcx
        };
        for (int i = n - 1; i >= 0; i--) _a.Db(pops[i]);
        _a.CallRel(c.Name);
        // result already in rax
    }

    // Calling an async function: evaluate the arguments, create a task whose entry is
    // the function body, store the arguments into the task, start it. rax = task.
    void EmitAsyncCall(CallExpr c) {
        foreach (var arg in c.Args) { EmitExpr(arg); _a.PushRax(); }
        _a.MovRegLabelAddr(7, c.Name);            // mov rdi, &F
        _a.CallRel("__rt_new");                   // rax = task
        for (int i = c.Args.Count - 1; i >= 0; i--) {
            _a.PopRcx();
            _a.Db(0x48, 0x89, 0x48, (byte)(32 + 8 * i)); // mov [rax+32+8i], rcx
        }
        _a.PushRax();
        _a.MovRdiRax();
        _a.CallRel("__rt_start");                 // runs until the task first suspends
        _a.PopRax();
    }

    // exit(rax): terminate the program with the given status.
    void EmitExit() {
        if (_target == Target.Linux) {
            _a.MovRdiRax(); _a.MovRaxImm(231); _a.Syscall();   // exit_group
        } else if (_target == Target.Kernel) {
            _a.Db(0xfa); _a.Db(0xeb, 0xfe);       // cli; jmp $
        } else {
            _a.Db(0x48, 0x89, 0xc1);              // mov rcx, rax
            _a.AndRspImm8(-16);
            _a.SubRspImm8(32);
            EmitIatCall("ExitProcess");                       // ExitProcess
        }
    }

    // Hand-written machine code for the coroutine runtime (see AsyncRuntime.cs).
    void EmitAsyncAsm() {
        bool win = _target == Target.Windows;

        // __ctx_switch(rdi = &saveSp, rsi = newSp)
        _a.Label("__ctx_switch");
        _a.Db(0x55, 0x53, 0x41, 0x54, 0x41, 0x55, 0x41, 0x56, 0x41, 0x57); // push rbp,rbx,r12..r15
        if (win) {
            _a.Db(0x65, 0xff, 0x34, 0x25, 0x08, 0, 0, 0);   // push qword gs:[0x08]  (TEB StackBase)
            _a.Db(0x65, 0xff, 0x34, 0x25, 0x10, 0, 0, 0);   // push qword gs:[0x10]  (TEB StackLimit)
        }
        _a.Db(0x48, 0x89, 0x27);                            // mov [rdi], rsp
        _a.Db(0x48, 0x89, 0xf4);                            // mov rsp, rsi
        if (win) {
            _a.Db(0x65, 0x8f, 0x04, 0x25, 0x10, 0, 0, 0);   // pop qword gs:[0x10]
            _a.Db(0x65, 0x8f, 0x04, 0x25, 0x08, 0, 0, 0);   // pop qword gs:[0x08]
        }
        _a.Db(0x41, 0x5f, 0x41, 0x5e, 0x41, 0x5d, 0x41, 0x5c, 0x5b, 0x5d); // pop r15..r12,rbx,rbp
        _a.Ret();

        // __ctx_init(rdi = stack top, rsi = stack limit) -> rax = initial saved sp.
        // Builds the frame __ctx_switch expects; its 'ret' lands in __fiber_entry with
        // rsp 16-byte aligned.
        _a.Label("__ctx_init");
        _a.Db(0x48, 0x89, 0xf8);                            // mov rax, rdi
        _a.Db(0x48, 0x83, 0xe0, 0xf0);                      // and rax, -16
        _a.Db(0x48, 0x83, 0xe8, 0x10);                      // sub rax, 16   (padding)
        _a.Db(0x48, 0x83, 0xe8, 0x08);                      // sub rax, 8
        _a.MovRegLabelAddr(1, "__fiber_entry");             // mov rcx, &__fiber_entry
        _a.Db(0x48, 0x89, 0x08);                            // mov [rax], rcx   (return address)
        for (int i = 0; i < 6; i++) {                       // rbp, rbx, r12..r15 = 0
            _a.Db(0x48, 0x83, 0xe8, 0x08);                  // sub rax, 8
            _a.Db(0x48, 0xc7, 0x00, 0, 0, 0, 0);            // mov qword [rax], 0
        }
        if (win) {
            _a.Db(0x48, 0x83, 0xe8, 0x08); _a.Db(0x48, 0x89, 0x38); // StackBase  = top
            _a.Db(0x48, 0x83, 0xe8, 0x08); _a.Db(0x48, 0x89, 0x30); // StackLimit = limit
        }
        _a.Ret();

        // __fiber_entry: first code a new task runs.
        _a.Label("__fiber_entry");
        _a.CallRel("__rt_cur");                             // rax = this task
        _a.Db(0x4c, 0x8b, 0x50, 0x18);                      // mov r10, [rax+24]  (function)
        _a.Db(0x48, 0x8b, 0x78, 0x20);                      // mov rdi, [rax+32]
        _a.Db(0x48, 0x8b, 0x70, 0x28);                      // mov rsi, [rax+40]
        _a.Db(0x48, 0x8b, 0x50, 0x30);                      // mov rdx, [rax+48]
        _a.Db(0x48, 0x8b, 0x48, 0x38);                      // mov rcx, [rax+56]
        _a.Db(0x41, 0xff, 0xd2);                            // call r10
        _a.MovRdiRax();
        _a.CallRel("__rt_finish");                          // never returns
        _a.Db(0x0f, 0x0b);                                  // ud2
    }

    void EmitTry(TryExpr t) {
        if (!_funcs.ContainsKey(t.Call.Name)) { EmitTryBuiltin(t); return; }
        EmitCall(t.Call);                  // value->rax, ok-flag->rdx, err ptr->rsi
        string ok = NewLabel("tryok");
        _a.Db(0x48,0x85,0xd2);             // test rdx, rdx
        _a.JnzRel(ok);
        // error path
        if (t.Else != null) {
            EmitExpr(t.Else);              // fallback value -> rax
        } else {
            // propagate: print nothing, just bubble rdx=0/rsi up and return
            if (_cur.Throws != null) { _a.MovRdxImm(0); /* rsi already holds err */ EmitFuncEpilogue(); }
        }
        _a.Label(ok);
    }

    // try on an I/O builtin: call it, then a nonzero ioerr takes the error path
    // (the fallback value, or propagate an IOError like a bare try on a throws function).
    void EmitTryBuiltin(TryExpr t) {
        EmitCall(t.Call);                  // value -> rax
        string ok = NewLabel("tryok");
        EmitLoadBlockR10();
        _a.Mem(0x83, 7, R10, BlkIoErr); _a.Db(0);   // cmp qword [r10+ioerr], 0
        _a.Jcc(0x4, ok);                   // je: it worked
        if (t.Else != null) {
            EmitExpr(t.Else);
        } else if (_cur.Throws != null) {
            int off = InternString($"IOError: {t.Call.Name} failed\n");
            AddStrReloc(off);              // mov rsi, message
            _a.MovRdxImm(0);
            EmitFuncEpilogue();
        }
        _a.Label(ok);
    }

    // ----------------------------------------------------------------- printing
    void EmitPrint(Expr arg) {
        if (arg is StrLit s) { EmitPrintLiteral(s.Val + "\n"); return; }
        if (arg is Interp itp) { EmitPrintInterp(itp); return; }
        var t = TypeOf(arg);
        if (t == "string") {
            // print a runtime string: ptr in rax; save it, strlen, then write_out(ptr,len)
            EmitExpr(arg);
            _a.PushRax();                            // save ptr
            _a.Db(0x48,0x89,0xc7);                   // mov rdi, rax
            _a.CallRel("strlen");                    // rax = len (clobbers rdi/rcx)
            _a.Db(0x48,0x89,0xc2);                   // mov rdx, rax (len)
            _a.PopRax(); _a.Db(0x48,0x89,0xc6);      // pop ptr -> rsi
            _a.CallRel("write_out");
            EmitPrintLiteral("\n");
            return;
        }
        // integer/bool: value in rdi -> call print_int, then newline
        EmitExpr(arg); _a.MovRdiRax(); _a.CallRel("print_int");
        EmitPrintLiteral("\n");
    }

    void EmitPrintLiteral(string text) {
        int off = InternString(text);
        int len = Encoding.UTF8.GetByteCount(text);
        AddStrRelocReg(off, 0xbe);          // mov rsi, imm64 (ptr)
        _a.MovRdxImm(len);                   // rdx = length
        _a.CallRel("write_out");             // unified stdout writer
    }

    void EmitPrintInterp(Interp itp) {
        foreach (var part in itp.Parts) {
            if (part is string str) {
                if (str.Length > 0) {
                    int off = InternString(str);
                    int len = Encoding.UTF8.GetByteCount(str);
                    AddStrRelocReg(off, 0xbe);       // mov rsi, imm64 (ptr)
                    _a.MovRdxImm(len);
                    _a.CallRel("write_out");         // works on every target
                }
            } else {
                var (expr, _) = ((Expr, string?))part;
                var t = TypeOf(expr);
                EmitExpr(expr);
                if (t == "string") {                 // runtime string hole
                    _a.PushRax();
                    _a.MovRdiRax();
                    _a.CallRel("strlen");
                    _a.Db(0x48,0x89,0xc2);           // mov rdx, rax
                    _a.PopRax(); _a.MovRsiRax();
                    _a.CallRel("write_out");
                } else { _a.MovRdiRax(); _a.CallRel("print_int"); }
            }
        }
        EmitPrintLiteral("\n");
    }

    // alloc(rdi = bytes) -> rax = pointer. Bumps the heap pointer slot.
    // Clobbers rax, r10 (both volatile). No bounds check: a fixed heap region.
    void EmitAlloc() {
        _a.Label("alloc");
        // atomic bump: rax = &heapPtr; lock xadd [rax], rdi  (rdi gets the OLD top,
        // [heapPtr] += size). Thread-safe under CLONE_VM concurrency.
        EmitSlotAddr(0xb8, 2);             // mov rax, imm64(&heapPtr)  (slot address)
        _a.Db(0xf0, 0x48, 0x0f, 0xc1, 0x38); // lock xadd [rax], rdi
        _a.Db(0x48, 0x89, 0xf8);           // mov rax, rdi   (old top = our allocation)
        _a.Ret();
    }

    // ===================== runtime library (Linux syscalls) =====================
    // Strings are pointers to null-terminated heap bytes. fd-based IO uses raw
    // Linux syscalls. These routines only run on the Linux target.
    void EmitRuntime() {
        // strlen(rdi=s) -> rax = length (scan to NUL)
        _a.Label("strlen");
        _a.Db(0x48,0x31,0xc0);                       // xor rax, rax
        string sl = NewLabel("sl"), sld = NewLabel("sld");
        _a.Label(sl);
        _a.Db(0x80,0x3c,0x07,0x00);                  // cmp byte [rdi+rax], 0
        _a.JzRel(sld);
        _a.Db(0x48,0xff,0xc0);                       // inc rax
        _a.JmpRel(sl);
        _a.Label(sld);
        _a.Ret();

        // str_concat(rdi=a, rsi=b) -> rax = new null-terminated string
        _a.Label("str_concat");
        _a.Db(0x53,0x55,0x41,0x54,0x41,0x55,0x41,0x56); // push rbx,rbp,r12,r13,r14 (align-ish)
        _a.Db(0x49,0x89,0xfc);                       // mov r12, rdi (a)
        _a.Db(0x49,0x89,0xf5);                       // mov r13, rsi (b)
        _a.Db(0x4c,0x89,0xe7);                       // mov rdi, r12
        _a.CallRel("strlen"); _a.Db(0x49,0x89,0xc6); // mov r14, rax (la)
        _a.Db(0x4c,0x89,0xef);                       // mov rdi, r13
        _a.CallRel("strlen"); _a.Db(0x48,0x89,0xc3); // mov rbx, rax (lb)
        // total = la + lb + 1
        _a.Db(0x4c,0x01,0xf3);                       // add rbx, r14   (rbx = lb+la)
        _a.Db(0x48,0x8d,0x7b,0x01);                  // lea rdi, [rbx+1]
        _a.CallRel("alloc");                          // rax = dest
        _a.Db(0x48,0x89,0xc5);                       // mov rbp, rax (dest, saved)
        // copy a (r14 bytes) from r12 to rbp
        _a.Db(0x48,0x89,0xc1);                       // mov rcx, rax (dest cursor)
        _a.Db(0x4c,0x89,0xe6);                       // mov rsi, r12 (src a)
        EmitCopyLoop("ca");                           // copy r14 bytes rsi->rcx
        // now rcx points past a; copy b (rbx-r14? we lost lb). Recompute lb via strlen(r13)
        _a.Db(0x4c,0x89,0xef);                       // mov rdi, r13
        _a.Db(0x51);                                  // push rcx (dest cursor)
        _a.CallRel("strlen"); _a.Db(0x49,0x89,0xc6); // mov r14, rax (lb)  reuse r14
        _a.Db(0x59);                                  // pop rcx
        _a.Db(0x4c,0x89,0xee);                       // mov rsi, r13 (src b)
        EmitCopyLoop("cb");                           // copy r14 bytes
        // null-terminate at rcx
        _a.Db(0xc6,0x01,0x00);                       // mov byte [rcx], 0
        _a.Db(0x48,0x89,0xe8);                       // mov rax, rbp (result)
        _a.Db(0x41,0x5e,0x41,0x5d,0x41,0x5c,0x5d,0x5b); // pop r14,r13,r12,rbp,rbx
        _a.Ret();

        // str_eq(rdi=a, rsi=b) -> rax = 1 if equal else 0
        _a.Label("str_eq");
        string se = NewLabel("se"), seq = NewLabel("seq"), sne = NewLabel("sne");
        _a.Label(se);
        _a.Db(0x8a,0x07);                            // mov al, [rdi]
        _a.Db(0x3a,0x06);                            // cmp al, [rsi]
        _a.JnzRel(sne);
        _a.Db(0x84,0xc0);                            // test al, al  (both NUL?)
        _a.JzRel(seq);
        _a.Db(0x48,0xff,0xc7);                       // inc rdi
        _a.Db(0x48,0xff,0xc6);                       // inc rsi
        _a.JmpRel(se);
        _a.Label(seq); _a.MovRaxImm(1); _a.Ret();
        _a.Label(sne); _a.MovRaxImm(0); _a.Ret();

        // itoa(rdi=n) -> rax = new string (decimal). Uses a 24-byte heap buffer.
        _a.Label("itoa");
        _a.Db(0x53,0x55,0x41,0x54);                  // push rbx, rbp, r12
        _a.Db(0x49,0x89,0xfc);                       // mov r12, rdi (value)
        _a.Db(0xbf,24,0,0,0);                         // mov edi, 24
        _a.CallRel("alloc"); _a.Db(0x48,0x89,0xc5);  // mov rbp, rax (buf)
        _a.Db(0x4c,0x89,0xe0);                       // mov rax, r12 (value)
        // handle zero
        string iz=NewLabel("iz"), inn=NewLabel("inn"), idg=NewLabel("idg"), irev=NewLabel("irev");
        _a.Db(0x48,0x85,0xc0);                       // test rax, rax
        _a.JnzRel(inn);
        _a.Db(0xc6,0x45,0x00,0x30);                  // mov byte [rbp], '0'
        _a.Db(0xc6,0x45,0x01,0x00);                  // mov byte [rbp+1], 0
        _a.Db(0x48,0x89,0xe8);                       // mov rax, rbp
        _a.Db(0x41,0x5c,0x5d,0x5b); _a.Ret();        // pop r12,rbp,rbx; ret
        _a.Label(inn);
        // negative?
        string ipos=NewLabel("ipos");
        _a.Db(0x48,0x85,0xc0);                       // test rax,rax
        _a.Db(0x0f,0x89); _a.Rel32To(ipos);          // jns ipos
        _a.Db(0xc6,0x45,0x00,0x2d);                  // mov byte [rbp], '-'  (we'll shift; simpler: emit then digits) -- handled below
        _a.Db(0x48,0xf7,0xd8);                       // neg rax
        _a.Label(ipos);
        // write digits backward into a temp on stack region [rbp+1..], then reverse.
        // simpler: build into buffer end. rbx = cursor at rbp+22 (leave room), write back-to-front.
        _a.Db(0x48,0x8d,0x5d,0x16);                  // lea rbx, [rbp+22]
        _a.Db(0xc6,0x03,0x00);                       // mov byte [rbx], 0  (terminator)
        _a.Label(idg);
        _a.Db(0x48,0x31,0xd2);                       // xor rdx, rdx
        _a.Db(0x49,0xc7,0xc1,10,0,0,0);              // mov r9, 10
        _a.Db(0x49,0xf7,0xf1);                       // div r9
        _a.Db(0x48,0xff,0xcb);                       // dec rbx
        _a.Db(0x48,0x83,0xc2,0x30);                  // add rdx, '0'
        _a.Db(0x88,0x13);                            // mov [rbx], dl
        _a.Db(0x48,0x85,0xc0);                       // test rax, rax
        _a.JnzRel(idg);
        // rbx points at the first digit; a negative value gets its '-' in front.
        // (This used to drop the sign: str(-1) gave "1". The digit loop divides the
        // negated value unsigned, so long.MinValue comes out right as well.)
        string inosign = NewLabel("inosign");
        _a.TestRR(R12, R12); _a.Jcc(0x9, inosign);   // jns: not negative
        _a.Db(0x48,0xff,0xcb);                       // dec rbx
        _a.Db(0xc6,0x03,0x2d);                       // mov byte [rbx], '-'
        _a.Label(inosign);
        _a.Db(0x48,0x89,0xd8);                       // mov rax, rbx
        _a.Db(0x41,0x5c,0x5d,0x5b);                  // pop r12,rbp,rbx
        _a.Ret();

        // atoi(rdi=s) -> rax = integer
        _a.Label("atoi");
        _a.Db(0x48,0x31,0xc0);                       // xor rax, rax
        string aL=NewLabel("aL"), aD=NewLabel("aD"), aPos=NewLabel("aPos"), aDigits=NewLabel("aDigits");
        // an optional sign: r8 = 1 for '-' (toint("-42") used to give 0)
        _a.MovRI(R8, 0);
        _a.Db(0x80,0x3f,0x2d); _a.Jcc(0x5, aPos);    // cmp byte [rdi], '-'; jne
        _a.MovRI(R8, 1); _a.Db(0x48,0xff,0xc7);      // inc rdi
        _a.JmpRel(aDigits);
        _a.Label(aPos);
        _a.Db(0x80,0x3f,0x2b); _a.Jcc(0x5, aDigits); // cmp byte [rdi], '+'; jne
        _a.Db(0x48,0xff,0xc7);                       // inc rdi
        _a.Label(aDigits);
        _a.Label(aL);
        _a.Db(0x0f,0xb6,0x0f);                       // movzx ecx, byte [rdi]
        _a.Db(0x80,0xf9,0x30);                       // cmp cl, '0'
        _a.Db(0x0f,0x8c); _a.Rel32To(aD);            // jl done
        _a.Db(0x80,0xf9,0x39);                       // cmp cl, '9'
        _a.Db(0x0f,0x8f); _a.Rel32To(aD);            // jg done
        _a.Db(0x48,0x6b,0xc0,10);                    // imul rax, rax, 10
        _a.Db(0x48,0x83,0xe9,0x30);                  // sub rcx, '0'
        _a.Db(0x48,0x01,0xc8);                       // add rax, rcx
        _a.Db(0x48,0xff,0xc7);                       // inc rdi
        _a.JmpRel(aL);
        _a.Label(aD);
        string aRet = NewLabel("aRet");
        _a.TestRR(R8, R8); _a.Jcc(0x4, aRet);
        _a.Db(0x48,0xf7,0xd8);                       // neg rax
        _a.Label(aRet);
        _a.Ret();

        // substr(rdi=s, rsi=start, rdx=len) -> rax = new string
        _a.Label("substr");
        _a.Db(0x53,0x55,0x41,0x54,0x41,0x55,0x41,0x56); // push rbx,rbp,r12,r13,r14
        _a.Db(0x49,0x89,0xfc);                       // mov r12, rdi (s)
        _a.Db(0x49,0x89,0xf5);                       // mov r13, rsi (start)
        _a.Db(0x48,0x89,0xd3);                       // mov rbx, rdx (len)
        _a.Db(0x48,0x8d,0x7b,0x01);                  // lea rdi, [rbx+1]
        _a.CallRel("alloc"); _a.Db(0x48,0x89,0xc5);  // mov rbp, rax (dest)
        _a.Db(0x4c,0x89,0xe6);                       // mov rsi, r12  (src)
        _a.Db(0x4c,0x01,0xee);                       // add rsi, r13  (src + start)
        _a.Db(0x48,0x89,0xe9);                       // mov rcx, rbp (dest cursor)
        _a.Db(0x49,0x89,0xde);                       // mov r14, rbx (count)
        EmitCopyLoop("cs");
        _a.Db(0xc6,0x01,0x00);                       // mov byte [rcx], 0
        _a.Db(0x48,0x89,0xe8);                       // mov rax, rbp
        _a.Db(0x41,0x5e,0x41,0x5d,0x41,0x5c,0x5d,0x5b); // pop r14,r13,r12,rbp,rbx
        _a.Ret();

        // is_ip(rdi=str) -> rax = 1 if the string is a non-empty run of digits and dots
        _a.Label("is_ip");
        {
            string sc = NewLabel("isip_sc"), nx = NewLabel("isip_nx"), end = NewLabel("isip_end"), no = NewLabel("isip_no");
            _a.Db(0x48,0x89,0xf8);                   // mov rax, rdi
            _a.Label(sc);
            _a.Db(0x0f,0xb6,0x08);                   // movzx ecx, byte [rax]
            _a.Db(0x84,0xc9);                        // test cl, cl
            _a.JzRel(end);
            _a.Db(0x80,0xf9,0x2e); _a.JzRel(nx);     // cmp cl,'.' ; je next
            _a.Db(0x80,0xf9,0x30); _a.Db(0x0f,0x82); _a.Rel32To(no); // cmp cl,'0' ; jb no
            _a.Db(0x80,0xf9,0x39); _a.Db(0x0f,0x87); _a.Rel32To(no); // cmp cl,'9' ; ja no
            _a.Label(nx);
            _a.Db(0x48,0xff,0xc0);                   // inc rax
            _a.JmpRel(sc);
            _a.Label(end);
            _a.Db(0x48,0x39,0xf8); _a.JzRel(no);     // cmp rax, rdi ; empty -> no
            _a.MovRaxImm(1); _a.Ret();
            _a.Label(no);
            _a.Db(0x48,0x31,0xc0); _a.Ret();         // xor rax, rax
        }

        // parse_ip(rdi=str "a.b.c.d", rsi=dest 4 bytes) -- shared by both targets
        _a.Label("parse_ip");
        string pi=NewLabel("pi"), pidot=NewLabel("pidot"), pidone=NewLabel("pidone");
        _a.Db(0x48,0x31,0xc9);                       // xor rcx, rcx (octet)
        _a.Label(pi);
        _a.Db(0x0f,0xb6,0x07);                       // movzx eax, byte [rdi]
        _a.Db(0x84,0xc0);                            // test al, al
        _a.JzRel(pidone);
        _a.Db(0x3c,0x2e);                            // cmp al, '.'
        _a.Db(0x0f,0x84); _a.Rel32To(pidot);         // je pidot
        _a.Db(0x6b,0xc9,10);                         // imul ecx, ecx, 10
        _a.Db(0x2c,0x30);                            // sub al, '0'
        _a.Db(0x0f,0xb6,0xc0);                       // movzx eax, al
        _a.Db(0x01,0xc1);                            // add ecx, eax
        _a.Db(0x48,0xff,0xc7);                       // inc rdi
        _a.JmpRel(pi);
        _a.Label(pidot);
        _a.Db(0x88,0x0e);                            // mov [rsi], cl
        _a.Db(0x48,0xff,0xc6);                       // inc rsi
        _a.Db(0x48,0x31,0xc9);                       // xor rcx, rcx
        _a.Db(0x48,0xff,0xc7);                       // inc rdi
        _a.JmpRel(pi);
        _a.Label(pidone);
        _a.Db(0x88,0x0e);                            // mov [rsi], cl (last octet)
        _a.Ret();

        EmitIoStatus();
        if (_target == Target.Windows) EmitWinRuntime();
        else EmitSyscallRuntime();
    }

    // ---- I/O status (ioerr). Every I/O builtin stores its outcome in the calling
    // thread's arena block: 0 on success, else the OS error code (GetLastError /
    // WSAGetLastError on Windows, errno on Linux). These helpers preserve every
    // register except r10 and the flags.
    void EmitIoStatus() {
        if (_target == Target.Kernel) {          // no I/O builtins on bare metal (NeedOs); keep the labels
            _a.Label("io_ok"); _a.Ret();
            _a.Label("io_err_win"); _a.Ret();
            _a.Label("io_err_neg"); _a.Ret();
            return;
        }
        _a.Label("io_ok");                       // ioerr = 0
        EmitLoadBlockR10(); _a.StImm(R10, BlkIoErr, 0); _a.Ret();

        _a.Label("io_err_win");                  // ioerr = GetLastError() (TEB.LastErrorValue, gs:[0x68] on x64;
        EmitLoadBlockR10(); _a.PushR(RAX);       //  WSAGetLastError returns the same field)
        _a.Db(0x65, 0x8b, 0x04, 0x25, 0x68, 0, 0, 0);   // mov eax, gs:[0x68]
        string have = NewLabel("iow_have");
        _a.TestRR(RAX, RAX); _a.Jcc(0x5, have);           // a failure must never read as 0:
        _a.MovRI(RAX, 31);                                //   ERROR_GEN_FAILURE if the API left no code
        _a.Label(have);
        _a.St(R10, BlkIoErr, RAX); _a.PopR(RAX); _a.Ret();

        _a.Label("io_err_neg");                  // ioerr = -rax (a syscall's -errno)
        _a.PushR(RAX); _a.Db(0x48, 0xf7, 0xd8);  // neg rax
        EmitLoadBlockR10(); _a.St(R10, BlkIoErr, RAX); _a.PopR(RAX); _a.Ret();
    }

    // rax = result of a Windows call that reports failure as a negative value
    // (SOCKET_ERROR / INVALID_SOCKET): record the outcome, keep rax.
    void IoCheckWin() {
        string err = NewLabel("iow_err"), done = NewLabel("iow_done");
        _a.TestRR(RAX, RAX); _a.Jcc(0x8, err);   // js
        _a.CallRel("io_ok"); _a.JmpRel(done);
        _a.Label(err); _a.CallRel("io_err_win");
        _a.Label(done);
    }

    // rax = raw syscall result (-errno on failure): record the outcome; failure becomes -1.
    void IoCheckSys() {
        string err = NewLabel("ios_err"), done = NewLabel("ios_done");
        _a.TestRR(RAX, RAX); _a.Jcc(0x8, err);   // js
        _a.CallRel("io_ok"); _a.JmpRel(done);
        _a.Label(err); _a.CallRel("io_err_neg"); _a.MovRI(RAX, -1);
        _a.Label(done);
    }

    void Movsxd() => _a.Db(0x48, 0x63, 0xc0);    // movsxd rax, eax  (Win32 int results: -1 must stay negative)

    // copy r14 bytes from [rsi] to [rcx], advancing both; ends with rcx past the copy
    void EmitCopyLoop(string tag) {
        string lp = NewLabel(tag+"_lp"), dn = NewLabel(tag+"_dn");
        _a.Label(lp);
        _a.Db(0x4d,0x85,0xf6);                       // test r14, r14
        _a.JzRel(dn);
        _a.Db(0x8a,0x06);                            // mov al, [rsi]
        _a.Db(0x88,0x01);                            // mov [rcx], al
        _a.Db(0x48,0xff,0xc6);                       // inc rsi
        _a.Db(0x48,0xff,0xc1);                       // inc rcx
        _a.Db(0x49,0xff,0xce);                       // dec r14
        _a.JmpRel(lp);
        _a.Label(dn);
    }

    // File + socket runtime via raw Linux syscalls. (Linux target only.)
    // ---- Windows runtime: sockets via ws2_32, files via kernel32 (Win64 ABI) ----
    // Import indices: 0 GetStdHandle,1 WriteFile,2 ExitProcess,3 CreateThread,
    // 4 WaitForSingleObject,5 CloseHandle,6 CreateFileA,7 ReadFile,
    // 8 WSAStartup,9 socket,10 connect,11 bind,12 listen,13 accept,14 send,15 recv,16 closesocket
    void EmitWinRuntime() {
        // call helper: assumes args already in rcx/rdx/r8/r9; allocates shadow space.
        // We inline shadow-space management at each call site.

        // ensure_wsa(): call WSAStartup(0x0202, &wsadata) once. wsadata = 512 bytes on heap.
        // We keep an "initialized" flag at heapBase+0 region? Simpler: just call every time
        // (WSAStartup is refcounted & cheap). Uses a 512-byte stack buffer.
        if (_usesNet) EmitWinSockets();     // Winsock is imported only by programs that use sockets
        EmitWinFiles();
    }

    void EmitWinSockets() {
        _a.Label("ensure_wsa");
        _a.Db(0x55);                                  // push rbp
        _a.Db(0x48,0x89,0xe5);                        // mov rbp, rsp
        _a.Db(0xbf,0x00,0x02,0x00,0x00);              // mov edi, 512
        _a.CallRel("alloc");                          // rax = wsadata buf
        _a.Db(0x48,0x89,0xc2);                        // mov rdx, rax (lpWSAData)
        _a.Db(0x48,0xc7,0xc1,0x02,0x02,0,0);          // mov rcx, 0x0202 (version 2.2)
        _a.AndRspImm8(-16);                           // align stack to 16 for the API call
        _a.Db(0x48,0x83,0xec,0x20);                   // sub rsp, 32 (shadow, keeps 16-align)
        EmitIatCall("WSAStartup");                               // WSAStartup
        _a.Db(0x48,0x89,0xec);                        // mov rsp, rbp  (undo align+shadow)
        _a.Db(0x5d);                                  // pop rbp
        _a.Ret();

        // win_sockaddr(rdi=dest16, rsi=ipstr_or_0, rdx=port): fill sockaddr_in.
        // If rsi==0 -> INADDR_ANY. family=AF_INET(2), port big-endian, addr.
        _a.Label("win_sockaddr");
        // family
        _a.Db(0x66,0xc7,0x07,0x02,0x00);              // mov word [rdi], 2
        // port BE: [rdi+2]=high, [rdi+3]=low
        _a.Db(0x48,0x89,0xd0);                        // mov rax, rdx (port)
        _a.Db(0x88,0x47,0x03);                        // mov [rdi+3], al (low)
        _a.Db(0x48,0xc1,0xe8,0x08);                   // shr rax, 8
        _a.Db(0x88,0x47,0x02);                        // mov [rdi+2], al (high)
        // addr (4 bytes at rdi+4)
        _a.Db(0x48,0x85,0xf6);                        // test rsi, rsi
        string anyaddr = NewLabel("anyaddr"), addrdone = NewLabel("addrdone");
        _a.JzRel(anyaddr);
        // parse dotted IP from rsi into [rdi+4]; reuse parse_ip(rdi=str,rsi=dest)
        _a.Db(0x57);                                   // push rdi (dest base)
        _a.Db(0x48,0x8d,0x57,0x04);                   // lea rdx, [rdi+4] (dest octets) -- use rdx temp
        _a.Db(0x48,0x89,0xf7);                        // mov rdi, rsi (ip str)
        _a.Db(0x48,0x89,0xd6);                        // mov rsi, rdx (dest octets)
        _a.CallRel("parse_ip");
        _a.Db(0x5f);                                   // pop rdi
        _a.JmpRel(addrdone);
        _a.Label(anyaddr);
        _a.Db(0xc7,0x47,0x04,0,0,0,0);                // mov dword [rdi+4], 0 (INADDR_ANY)
        _a.Label(addrdone);
        // zero the 8-byte tail [rdi+8]
        _a.Db(0x48,0xc7,0x47,0x08,0,0,0,0);           // mov qword [rdi+8], 0
        _a.Ret();

        // sock_close(rdi=fd) -> closesocket(fd)
        _a.Label("sock_close");
        _a.Db(0x55,0x48,0x89,0xe5);                   // push rbp; mov rbp,rsp
        _a.AndRspImm8(-16);
        _a.Db(0x48,0x89,0xf9);                        // mov rcx, rdi
        _a.Db(0x48,0x83,0xec,0x20); EmitIatCall("closesocket");
        Movsxd(); IoCheckWin();                       // 0, or SOCKET_ERROR (-1)
        _a.Db(0x48,0x89,0xec); _a.Db(0x5d);           // mov rsp,rbp ; pop rbp
        _a.Ret();

        // sock_send(rdi=fd, rsi=str) -> send(fd, buf, strlen(buf), 0)
        _a.Label("sock_send");
        _a.Db(0x55,0x48,0x89,0xe5);                   // push rbp; mov rbp,rsp
        _a.Db(0x53,0x41,0x54);                        // push rbx, r12
        _a.AndRspImm8(-16);
        _a.Db(0x48,0x89,0xfb);                        // mov rbx, rdi (fd)
        _a.Db(0x49,0x89,0xf4);                        // mov r12, rsi (buf)
        _a.Db(0x48,0x89,0xf7); _a.CallRel("strlen");  // rax = len
        _a.Db(0x48,0x89,0xd9);                        // mov rcx, rbx (fd)
        _a.Db(0x4c,0x89,0xe2);                        // mov rdx, r12 (buf)
        _a.Db(0x49,0x89,0xc0);                        // mov r8, rax (len)
        _a.Db(0x4d,0x31,0xc9);                        // xor r9, r9 (flags)
        _a.Db(0x48,0x83,0xec,0x20); EmitIatCall("send"); _a.Db(0x48,0x83,0xc4,0x20); // send
        Movsxd(); IoCheckWin();                       // bytes sent, or SOCKET_ERROR (-1)
        _a.Db(0x48,0x8d,0x65,0xf0);                   // lea rsp, [rbp-16]
        _a.Db(0x41,0x5c,0x5b);                        // pop r12, rbx
        _a.Db(0x5d);                                  // pop rbp
        _a.Ret();

        // sock_recv(rdi=fd, rsi=max) -> recv(fd, buf, max, 0); null-terminate; return buf
        _a.Label("sock_recv");
        _a.Db(0x55,0x48,0x89,0xe5);                   // push rbp; mov rbp,rsp
        _a.Db(0x53,0x41,0x54,0x41,0x55);              // push rbx, r12, r13
        _a.AndRspImm8(-16);
        _a.Db(0x48,0x89,0xfb);                        // mov rbx, fd
        _a.Db(0x49,0x89,0xf4);                        // mov r12, max
        _a.Db(0x48,0x8d,0x7e,0x01);                   // lea rdi,[rsi+1]
        _a.CallRel("alloc"); _a.Db(0x49,0x89,0xc5);   // mov r13, buf
        _a.Db(0x48,0x89,0xd9);                        // mov rcx, rbx (fd)
        _a.Db(0x4c,0x89,0xea);                        // mov rdx, r13 (buf)
        _a.Db(0x4d,0x89,0xe0);                        // mov r8, r12 (max)
        _a.Db(0x4d,0x31,0xc9);                        // xor r9, r9 (flags)
        _a.Db(0x48,0x83,0xec,0x20); EmitIatCall("recv"); _a.Db(0x48,0x83,0xc4,0x20); // recv -> eax = n
        // recv returns a 32-bit int: sign-extend, or SOCKET_ERROR (0xFFFFFFFF) would
        // look like a 4 GB read and the terminator would land 4 GB past the buffer.
        Movsxd();
        string wrn = NewLabel("wrn"), rerr = NewLabel("wr_err");
        _a.TestRR(RAX, RAX); _a.Jcc(0x8, rerr);       // js: error
        _a.CallRel("io_ok"); _a.JmpRel(wrn);          // n >= 0 (0 = the peer closed the connection)
        _a.Label(rerr);
        _a.CallRel("io_err_win");
        _a.Db(0x48,0x31,0xc0);                        // error -> "" (ioerr says why)
        _a.Label(wrn);
        _a.Db(0x41,0xc6,0x44,0x05,0x00,0x00);         // mov byte [r13+rax], 0
        _a.Db(0x4c,0x89,0xe8);                        // mov rax, r13
        _a.Db(0x48,0x8d,0x65,0xe8);                   // lea rsp, [rbp-24]
        _a.Db(0x41,0x5d,0x41,0x5c,0x5b);              // pop r13,r12,rbx
        _a.Db(0x5d);                                  // pop rbp
        _a.Ret();

        // win_resolve(rdi=host, rsi=dest 4 bytes) -> rax = 1 ok / 0 unknown host.
        // Dotted IPs are parsed directly; anything else goes through gethostbyname.
        _a.Label("win_resolve");
        {
            string dns = NewLabel("rs_dns"), fail = NewLabel("rs_fail"), done = NewLabel("rs_done");
            _a.Db(0x55,0x48,0x89,0xe5);                   // push rbp; mov rbp,rsp
            _a.Db(0x53,0x41,0x54);                        // push rbx; push r12
            _a.AndRspImm8(-16);
            _a.Db(0x48,0x89,0xfb);                        // mov rbx, rdi (host)
            _a.Db(0x49,0x89,0xf4);                        // mov r12, rsi (dest)
            _a.CallRel("is_ip");
            _a.Db(0x48,0x85,0xc0); _a.JzRel(dns);
            _a.Db(0x48,0x89,0xdf);                        // mov rdi, rbx
            _a.Db(0x4c,0x89,0xe6);                        // mov rsi, r12
            _a.CallRel("parse_ip");
            _a.MovRaxImm(1); _a.JmpRel(done);
            _a.Label(dns);
            _a.Db(0x48,0x89,0xd9);                        // mov rcx, rbx (name)
            _a.Db(0x48,0x83,0xec,0x20); EmitIatCall("gethostbyname"); _a.Db(0x48,0x83,0xc4,0x20); // gethostbyname
            _a.Db(0x48,0x85,0xc0); _a.JzRel(fail);        // NULL -> unknown host
            _a.Db(0x48,0x8b,0x40,0x18);                   // mov rax, [rax+24] (h_addr_list)
            _a.Db(0x48,0x85,0xc0); _a.JzRel(fail);
            _a.Db(0x48,0x8b,0x00);                        // mov rax, [rax]    (first address)
            _a.Db(0x48,0x85,0xc0); _a.JzRel(fail);
            _a.Db(0x8b,0x00);                             // mov eax, [rax]    (4 bytes, network order)
            _a.Db(0x41,0x89,0x04,0x24);                   // mov [r12], eax
            _a.MovRaxImm(1); _a.JmpRel(done);
            _a.Label(fail);
            _a.Db(0x48,0x31,0xc0);                        // xor rax, rax
            _a.Label(done);
            _a.Db(0x48,0x8d,0x65,0xf0);                   // lea rsp, [rbp-16]
            _a.Db(0x41,0x5c,0x5b,0x5d);                   // pop r12; pop rbx; pop rbp
            _a.Ret();
        }

        // tcp_connect(rdi=host, rsi=port) -> fd, or -1 if the host is unknown / unreachable
        _a.Label("tcp_connect");
        _a.Db(0x55,0x53,0x41,0x54,0x41,0x55);         // push rbp,rbx,r12,r13
        _a.Db(0x48,0x89,0xe5);                        // mov rbp, rsp
        _a.AndRspImm8(-16);                           // 16-align the frame for API calls
        _a.Db(0x49,0x89,0xfc);                        // mov r12, host
        _a.Db(0x49,0x89,0xf5);                        // mov r13, port
        _a.CallRel("ensure_wsa");
        // socket(AF_INET=2, SOCK_STREAM=1, 0)
        _a.Db(0x48,0xc7,0xc1,2,0,0,0);                // mov rcx,2
        _a.Db(0x48,0xc7,0xc2,1,0,0,0);                // mov rdx,1
        _a.Db(0x4d,0x31,0xc0);                        // xor r8,r8
        _a.Db(0x48,0x83,0xec,0x20); EmitIatCall("socket"); _a.Db(0x48,0x83,0xc4,0x20); // socket
        _a.Db(0x48,0x89,0xc3);                        // mov rbx, fd
        string csfail = NewLabel("csfail");
        _a.AluRI(7, RAX, -1); _a.Jcc(0x4, csfail);    // INVALID_SOCKET
        // build sockaddr_in on stack (16 bytes, keep 16-aligned)
        _a.Db(0x48,0x83,0xec,0x10);                   // sub rsp,16
        _a.Db(0x48,0x89,0xe7);                        // mov rdi, rsp (dest)
        _a.Db(0x4c,0x89,0xe6);                        // mov rsi, r12 (ip str)
        _a.Db(0x4c,0x89,0xea);                        // mov rdx, r13 (port)
        _a.CallRel("win_sockaddr");
        // host may be a name ("example.com"): resolve it into the address field
        string cfail = NewLabel("cfail"), cdone = NewLabel("cdone");
        _a.Db(0x4c,0x89,0xe7);                        // mov rdi, r12 (host)
        _a.Db(0x48,0x8d,0x74,0x24,0x04);              // lea rsi, [rsp+4]
        _a.CallRel("win_resolve");
        _a.Db(0x48,0x85,0xc0); _a.JzRel(cfail);       // unknown host -> -1
        // connect(fd, &addr, 16)
        _a.Db(0x48,0x89,0xd9);                        // mov rcx, rbx (fd)
        _a.Db(0x48,0x89,0xe2);                        // mov rdx, rsp (&addr)
        _a.Db(0x49,0xc7,0xc0,16,0,0,0);               // mov r8, 16
        _a.Db(0x48,0x83,0xec,0x20); EmitIatCall("connect"); _a.Db(0x48,0x83,0xc4,0x20); // connect
        _a.Db(0x85,0xc0); _a.JnzRel(cfail);           // SOCKET_ERROR -> -1
        _a.Db(0x48,0x89,0xd8);                        // mov rax, rbx (return fd)
        _a.CallRel("io_ok");
        _a.JmpRel(cdone);
        _a.Label(cfail);
        _a.CallRel("io_err_win");                     // before closesocket can change the error code
        _a.Db(0x48,0x89,0xd9);                        // mov rcx, rbx (fd)
        _a.Db(0x48,0x83,0xec,0x20); EmitIatCall("closesocket"); _a.Db(0x48,0x83,0xc4,0x20); // closesocket
        _a.Db(0x48,0xc7,0xc0,0xff,0xff,0xff,0xff);    // mov rax, -1
        _a.JmpRel(cdone);
        _a.Label(csfail);                             // socket() itself failed: nothing to close
        _a.CallRel("io_err_win");
        _a.MovRI(RAX, -1);
        _a.Label(cdone);
        _a.Db(0x48,0x89,0xec);                        // mov rsp, rbp
        _a.Db(0x41,0x5d,0x41,0x5c,0x5b,0x5d);         // pop r13,r12,rbx,rbp
        _a.Ret();

        // tcp_listen(rdi=port) -> server fd
        _a.Label("tcp_listen");
        _a.Db(0x55,0x53,0x41,0x54);                   // push rbp,rbx,r12
        _a.Db(0x48,0x89,0xe5);                        // mov rbp, rsp
        _a.AndRspImm8(-16);                           // 16-align the frame
        _a.Db(0x49,0x89,0xfc);                        // mov r12, port
        _a.CallRel("ensure_wsa");
        _a.Db(0x48,0xc7,0xc1,2,0,0,0);                // socket(AF_INET,
        _a.Db(0x48,0xc7,0xc2,1,0,0,0);                //   SOCK_STREAM,
        _a.Db(0x4d,0x31,0xc0);                        //   0)
        _a.Db(0x48,0x83,0xec,0x20); EmitIatCall("socket"); _a.Db(0x48,0x83,0xc4,0x20);
        _a.Db(0x48,0x89,0xc3);                        // mov rbx, fd
        string lsfail = NewLabel("lsfail"), lfail = NewLabel("lfail"), ldone = NewLabel("ldone");
        _a.AluRI(7, RAX, -1); _a.Jcc(0x4, lsfail);    // INVALID_SOCKET
        // bind sockaddr_in {AF_INET, port, INADDR_ANY}
        _a.Db(0x48,0x83,0xec,0x10);                   // sub rsp,16
        _a.Db(0x48,0x89,0xe7);                        // mov rdi, rsp
        _a.Db(0x48,0x31,0xf6);                        // xor rsi, rsi (INADDR_ANY)
        _a.Db(0x4c,0x89,0xe2);                        // mov rdx, r12 (port)
        _a.CallRel("win_sockaddr");
        _a.Db(0x48,0x89,0xd9);                        // mov rcx, rbx (fd)
        _a.Db(0x48,0x89,0xe2);                        // mov rdx, rsp (&addr)
        _a.Db(0x49,0xc7,0xc0,16,0,0,0);               // mov r8, 16
        _a.Db(0x48,0x83,0xec,0x20); EmitIatCall("bind"); _a.Db(0x48,0x83,0xc4,0x20); // bind
        _a.Db(0x85,0xc0); _a.JnzRel(lfail);           // e.g. the port is already in use
        // listen(fd, 512): a deep backlog so concurrent clients aren't refused
        _a.Db(0x48,0x89,0xd9);                        // mov rcx, rbx
        _a.Db(0x48,0xc7,0xc2,0x00,0x02,0,0);          // mov rdx, 512
        _a.Db(0x48,0x83,0xec,0x20); EmitIatCall("listen"); _a.Db(0x48,0x83,0xc4,0x20); // listen
        _a.Db(0x85,0xc0); _a.JnzRel(lfail);
        _a.Db(0x48,0x89,0xd8);                        // mov rax, rbx
        _a.CallRel("io_ok");
        _a.JmpRel(ldone);
        _a.Label(lfail);
        _a.CallRel("io_err_win");
        _a.Db(0x48,0x89,0xd9);                        // mov rcx, rbx
        _a.Db(0x48,0x83,0xec,0x20); EmitIatCall("closesocket"); _a.Db(0x48,0x83,0xc4,0x20);
        _a.MovRI(RAX, -1); _a.JmpRel(ldone);
        _a.Label(lsfail);
        _a.CallRel("io_err_win");
        _a.MovRI(RAX, -1);
        _a.Label(ldone);
        _a.Db(0x48,0x89,0xec);                        // mov rsp, rbp
        _a.Db(0x41,0x5c,0x5b,0x5d);                   // pop r12,rbx,rbp
        _a.Ret();

        // sock_accept(rdi=serverfd) -> client fd ; accept(fd, NULL, NULL)
        _a.Label("sock_accept");
        _a.Db(0x55,0x48,0x89,0xe5);                   // push rbp; mov rbp,rsp
        _a.AndRspImm8(-16);
        _a.Db(0x48,0x89,0xf9);                        // mov rcx, rdi
        _a.Db(0x48,0x31,0xd2);                        // xor rdx, rdx
        _a.Db(0x4d,0x31,0xc0);                        // xor r8, r8
        _a.Db(0x48,0x83,0xec,0x20); EmitIatCall("accept"); _a.Db(0x48,0x83,0xc4,0x20); // accept
        IoCheckWin();                                 // client socket, or INVALID_SOCKET (-1)
        _a.Db(0x48,0x89,0xec); _a.Db(0x5d);           // mov rsp,rbp ; pop rbp
        _a.Ret();

    }

    void EmitWinFiles() {
        // readfile(rdi=path) -> the whole file as a string ("" if it cannot be opened).
        // CreateFileA, GetFileSizeEx, ReadFile until done, CloseHandle.
        {
            _a.Label("readfile");
            string fail = NewLabel("rf_fail"), loop = NewLabel("rf_loop"), done = NewLabel("rf_done"), small = NewLabel("rf_small");
            string rerr = NewLabel("rf_rerr"), szfail = NewLabel("rf_szfail"), empty = NewLabel("rf_empty");
            _a.PushR(RBP); _a.MovRR(RBP, RSP);
            _a.PushR(RBX); _a.PushR(R12); _a.PushR(R13); _a.PushR(R14);
            _a.AndRspImm8(-16); _a.AluRI(5, RSP, 64);
            _a.CallRel("io_ok");
            _a.MovRR(RCX, RDI); _a.MovRI(RDX, unchecked((int)0x80000000)); _a.Db(0x89, 0xd2);   // edx = GENERIC_READ (zero-extended)
            _a.MovRI(R8, 1); _a.MovRI(R9, 0);
            _a.StImm(RSP, 32, 3); _a.StImm(RSP, 40, 0x80); _a.StImm(RSP, 48, 0);
            EmitIatCall("CreateFileA");
            _a.MovRR(RBX, RAX);
            _a.AluRI(7, RAX, -1); _a.Jcc(0x4, fail);                    // INVALID_HANDLE_VALUE
            _a.MovRR(RCX, RBX); _a.Lea(RDX, RSP, 56);
            EmitIatCall("GetFileSizeEx");
            _a.Db(0x85, 0xc0); _a.Jcc(0x4, szfail);                     // e.g. the path is a directory
            _a.Ld(R12, RSP, 56);                                        // r12 = size
            _a.Lea(RDI, R12, 1); _a.CallRel("alloc"); _a.MovRR(R13, RAX);   // r13 = buffer
            _a.MovRI(R14, 0);                                           // r14 = bytes so far
            _a.Label(loop);
            _a.AluRR(0x39, R14, R12); _a.Jcc(0xd, done);                // total >= size
            _a.MovRR(R8, R12); _a.AluRR(0x29, R8, R14);                 // r8 = remaining
            _a.MovRI(RAX, 1 << 30); _a.AluRR(0x39, R8, RAX); _a.Jcc(0x6, small);
            _a.MovRR(R8, RAX);                                          // at most 1 GB per call
            _a.Label(small);
            _a.MovRR(RCX, RBX); _a.MovRR(RDX, R13); _a.AluRR(0x01, RDX, R14);
            _a.Lea(R9, RSP, 48); _a.StImm(RSP, 32, 0); _a.StImm(RSP, 48, 0);
            EmitIatCall("ReadFile");
            _a.Db(0x85, 0xc0); _a.Jcc(0x4, rerr);                       // failed
            _a.Db(0x8b, 0x44, 0x24, 0x30);                              // mov eax, [rsp+48] (bytes read)
            _a.TestRR(RAX, RAX); _a.Jcc(0x4, done);
            _a.AluRR(0x01, R14, RAX); _a.JmpRel(loop);
            _a.Label(rerr);
            _a.CallRel("io_err_win");                                   // keep what was read; ioerr says it is incomplete
            _a.Label(done);
            _a.MovRR(RAX, R13); _a.AluRR(0x01, RAX, R14); _a.Db(0xc6, 0x00, 0x00);   // terminating 0
            _a.MovRR(RCX, RBX); EmitIatCall("CloseHandle");
            _a.MovRR(RAX, R13);
            string outl = NewLabel("rf_out");
            _a.JmpRel(outl);
            _a.Label(szfail);
            _a.CallRel("io_err_win");
            _a.MovRR(RCX, RBX); EmitIatCall("CloseHandle");
            _a.JmpRel(empty);
            _a.Label(fail);
            _a.CallRel("io_err_win");                                   // e.g. 2 = file not found
            _a.Label(empty);
            _a.MovRI(RDI, 8); _a.CallRel("alloc"); _a.Db(0xc6, 0x00, 0x00);          // ""
            _a.Label(outl);
            _a.Lea(RSP, RBP, -32);
            _a.PopR(R14); _a.PopR(R13); _a.PopR(R12); _a.PopR(RBX); _a.PopR(RBP);
            _a.Ret();
        }

        // writefile(rdi=path, rsi=data) -> bytes written, or -1 (ioerr says why)
        {
            _a.Label("writefile");
            string ofail = NewLabel("wf_ofail"), wfail = NewLabel("wf_wfail"), outl = NewLabel("wf_out");
            _a.PushR(RBP); _a.MovRR(RBP, RSP);
            _a.PushR(RBX); _a.PushR(R12); _a.PushR(R13);
            _a.AndRspImm8(-16); _a.AluRI(5, RSP, 64);
            _a.MovRR(R12, RSI);                                         // r12 = data
            _a.CallRel("io_ok");
            // CreateFileA(path, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL)
            _a.MovRR(RCX, RDI); _a.MovRI(RDX, 0x40000000); _a.MovRI(R8, 0); _a.MovRI(R9, 0);
            _a.StImm(RSP, 32, 2); _a.StImm(RSP, 40, 0x80); _a.StImm(RSP, 48, 0);
            EmitIatCall("CreateFileA");
            _a.MovRR(RBX, RAX);
            _a.AluRI(7, RAX, -1); _a.Jcc(0x4, ofail);                   // INVALID_HANDLE_VALUE
            _a.MovRR(RDI, R12); _a.CallRel("strlen"); _a.MovRR(R13, RAX);   // r13 = len
            // WriteFile(handle, data, len, &written, NULL)
            _a.MovRR(RCX, RBX); _a.MovRR(RDX, R12); _a.MovRR(R8, R13);
            _a.StImm(RSP, 56, 0); _a.Lea(R9, RSP, 56); _a.StImm(RSP, 32, 0);
            EmitIatCall("WriteFile");
            _a.Db(0x85, 0xc0); _a.Jcc(0x4, wfail);
            _a.Db(0x44, 0x8b, 0x64, 0x24, 0x38);                        // mov r12d, [rsp+56] (written)
            _a.MovRR(RCX, RBX); EmitIatCall("CloseHandle");
            _a.MovRR(RAX, R12); _a.JmpRel(outl);
            _a.Label(wfail);                                            // e.g. the disk is full
            _a.CallRel("io_err_win");
            _a.MovRR(RCX, RBX); EmitIatCall("CloseHandle");
            _a.MovRI(RAX, -1); _a.JmpRel(outl);
            _a.Label(ofail);                                            // e.g. 3 = path not found, 5 = access denied
            _a.CallRel("io_err_win");
            _a.MovRI(RAX, -1);
            _a.Label(outl);
            _a.Lea(RSP, RBP, -24);
            _a.PopR(R13); _a.PopR(R12); _a.PopR(RBX); _a.PopR(RBP);
            _a.Ret();
        }
    }

    void EmitSyscallRuntime() {
        // sock_close(rdi=fd) -> 0, or -1 (ioerr = errno)
        _a.Label("sock_close");
        _a.Db(0x48,0xc7,0xc0,3,0,0,0);               // mov rax, 3 (close)
        _a.Syscall(); IoCheckSys(); _a.Ret();

        // sock_send(rdi=fd, rsi=str) -> rax = bytes written;  write(fd, buf, strlen(buf))
        _a.Label("sock_send");
        _a.Db(0x53,0x55);                            // push rbx, rbp
        _a.Db(0x48,0x89,0xfb);                       // mov rbx, rdi (fd)
        _a.Db(0x48,0x89,0xf5);                       // mov rbp, rsi (buf)
        _a.Db(0x48,0x89,0xf7);                       // mov rdi, rsi
        _a.CallRel("strlen");                         // rax = len
        _a.Db(0x48,0x89,0xc2);                       // mov rdx, rax (len)
        _a.Db(0x48,0x89,0xee);                       // mov rsi, rbp (buf)
        _a.Db(0x48,0x89,0xdf);                       // mov rdi, rbx (fd)
        _a.Db(0x48,0xc7,0xc0,1,0,0,0);               // mov rax, 1 (write)
        _a.Syscall();
        IoCheckSys();                                // bytes written, or -1
        _a.Db(0x5d,0x5b);                            // pop rbp, rbx
        _a.Ret();

        // sock_recv(rdi=fd, rsi=max) -> rax = string (null-terminated). Allocates max+1.
        _a.Label("sock_recv");
        _a.Db(0x53,0x55,0x41,0x54);                  // push rbx,rbp,r12
        _a.Db(0x48,0x89,0xfb);                       // mov rbx, rdi (fd)
        _a.Db(0x49,0x89,0xf4);                       // mov r12, rsi (max)
        _a.Db(0x48,0x8d,0x7e,0x01);                  // lea rdi, [rsi+1]
        _a.CallRel("alloc"); _a.Db(0x48,0x89,0xc5);  // mov rbp, rax (buf)
        _a.Db(0x48,0x89,0xdf);                       // mov rdi, rbx (fd)
        _a.Db(0x48,0x89,0xee);                       // mov rsi, rbp (buf)
        _a.Db(0x4c,0x89,0xe2);                       // mov rdx, r12 (max)
        _a.Db(0x48,0x31,0xc0);                       // xor rax, rax (read = 0)
        _a.Syscall();                                 // rax = n read (>=0) or -errno
        string rn = NewLabel("rn"), rerr = NewLabel("rn_err");
        _a.TestRR(RAX, RAX); _a.Jcc(0x8, rerr);      // js
        _a.CallRel("io_ok"); _a.JmpRel(rn);          // n >= 0 (0 = the peer closed the connection)
        _a.Label(rerr);
        _a.CallRel("io_err_neg");
        _a.Db(0x48,0x31,0xc0);                       // error -> "" (ioerr says why)
        _a.Label(rn);
        _a.Db(0xc6,0x44,0x05,0x00,0x00);             // mov byte [rbp+rax], 0
        _a.Db(0x48,0x89,0xe8);                       // mov rax, rbp (string)
        _a.Db(0x41,0x5c,0x5d,0x5b);                  // pop r12,rbp,rbx
        _a.Ret();

        // tcp_connect(rdi=host_str (dotted IP), rsi=port) -> rax = fd
        _a.Label("tcp_connect");
        _a.Db(0x55,0x53,0x41,0x54,0x41,0x55);        // push rbp,rbx,r12,r13
        _a.Db(0x48,0x89,0xe5);                       // mov rbp, rsp
        _a.Db(0x49,0x89,0xfc);                       // mov r12, rdi (host)
        _a.Db(0x49,0x89,0xf5);                       // mov r13, rsi (port)
        _a.Db(0x48,0xc7,0xc7,2,0,0,0);               // socket(AF_INET,
        _a.Db(0x48,0xc7,0xc6,1,0,0,0);               //   SOCK_STREAM,
        _a.Db(0x48,0x31,0xd2);                       //   0)
        _a.Db(0x48,0xc7,0xc0,41,0,0,0);              // mov rax,41 (socket)
        _a.Syscall();
        _a.Db(0x48,0x89,0xc3);                       // mov rbx, rax (fd)
        string lfail = NewLabel("lcfail"), ldone = NewLabel("lcdone"), lhost = NewLabel("lchost"), lsfail = NewLabel("lcsfail");
        _a.TestRR(RAX, RAX); _a.Jcc(0x8, lsfail);    // socket failed
        _a.Db(0x48,0x83,0xec,0x10);                  // sub rsp, 16  (sockaddr_in)
        _a.Db(0x66,0xc7,0x04,0x24,2,0);              // word [rsp]=2 (AF_INET)
        _a.Db(0x4c,0x89,0xe8);                       // mov rax, r13 (port)
        _a.Db(0x88,0x44,0x24,0x03);                  // [rsp+3]=al  (port low, BE)
        _a.Db(0x48,0xc1,0xe8,0x08);                  // shr rax, 8
        _a.Db(0x88,0x44,0x24,0x02);                  // [rsp+2]=al  (port high)
        _a.Db(0x4c,0x89,0xe7);                       // mov rdi, r12 (host)
        _a.CallRel("is_ip");                         // no DNS on Linux (no libc): names fail
        _a.Db(0x48,0x85,0xc0); _a.JzRel(lhost);
        _a.Db(0x4c,0x89,0xe7);                       // mov rdi, r12 (host)
        _a.Db(0x48,0x8d,0x74,0x24,0x04);             // lea rsi, [rsp+4]
        _a.CallRel("parse_ip");
        _a.Db(0x48,0xc7,0x44,0x24,0x08,0,0,0,0);     // qword [rsp+8]=0 (pad)
        _a.Db(0x48,0x89,0xdf);                       // mov rdi, rbx (fd)
        _a.Db(0x48,0x89,0xe6);                       // mov rsi, rsp (&addr)
        _a.Db(0x48,0xc7,0xc2,16,0,0,0);              // mov rdx, 16
        _a.Db(0x48,0xc7,0xc0,42,0,0,0);              // mov rax,42 (connect)
        _a.Syscall();
        _a.Db(0x48,0x85,0xc0); _a.JnzRel(lfail);     // connect error -> -1
        _a.Db(0x48,0x89,0xd8);                       // mov rax, rbx (return fd)
        _a.CallRel("io_ok");
        _a.JmpRel(ldone);
        _a.Label(lhost);                             // a host name (no DNS without libc) or a bad IP
        _a.MovRI(RAX, -22);                          // EINVAL
        _a.Label(lfail);                             // rax = -errno
        _a.CallRel("io_err_neg");
        _a.Db(0x48,0x89,0xdf);                       // mov rdi, rbx (fd)
        _a.Db(0x48,0xc7,0xc0,3,0,0,0);               // mov rax,3 (close)
        _a.Syscall();
        _a.Db(0x48,0xc7,0xc0,0xff,0xff,0xff,0xff);   // mov rax, -1
        _a.JmpRel(ldone);
        _a.Label(lsfail);                            // socket() failed: nothing to close
        _a.CallRel("io_err_neg");
        _a.MovRI(RAX, -1);
        _a.Label(ldone);
        _a.Db(0x48,0x89,0xec);                       // mov rsp, rbp
        _a.Db(0x41,0x5d,0x41,0x5c,0x5b,0x5d);        // pop r13,r12,rbx,rbp
        _a.Ret();

        // parse_ip is defined in the shared runtime (used by both targets).

        // tcp_listen(rdi=port) -> rax = server fd
        _a.Label("tcp_listen");
        _a.Db(0x55,0x53,0x41,0x54);                  // push rbp,rbx,r12
        _a.Db(0x48,0x89,0xe5);                       // mov rbp, rsp
        _a.Db(0x49,0x89,0xfc);                       // mov r12, rdi (port)
        _a.Db(0x48,0xc7,0xc7,2,0,0,0);               // socket(AF_INET,
        _a.Db(0x48,0xc7,0xc6,1,0,0,0);               //   SOCK_STREAM,
        _a.Db(0x48,0x31,0xd2);                       //   0)
        _a.Db(0x48,0xc7,0xc0,41,0,0,0);              // mov rax,41
        _a.Syscall(); _a.Db(0x48,0x89,0xc3);         // mov rbx, fd
        string tlfail = NewLabel("tlfail"), tlsfail = NewLabel("tlsfail"), tldone = NewLabel("tldone");
        _a.TestRR(RAX, RAX); _a.Jcc(0x8, tlsfail);   // socket failed
        _a.Db(0x48,0x83,0xec,0x10);                  // sub rsp,16
        _a.Db(0xc7,0x04,0x24,1,0,0,0);               // dword [rsp]=1
        _a.Db(0x48,0x89,0xdf);                       // mov rdi, rbx
        _a.Db(0x48,0xc7,0xc6,1,0,0,0);               // mov rsi, 1 (SOL_SOCKET)
        _a.Db(0x48,0xc7,0xc2,2,0,0,0);               // mov rdx, 2 (SO_REUSEADDR)
        // syscall arguments are rdi, rsi, rdx, r10, r8, r9 (r10, not rcx, is the 4th).
        // This used to pass optval/optlen in r8/r9, so SO_REUSEADDR was never set and a
        // restarted server could not bind its port while old connections sat in TIME_WAIT.
        _a.MovRR(R10, RSP);                          // optval = &1
        _a.MovRI(R8, 4);                             // optlen
        _a.Db(0x48,0xc7,0xc0,54,0,0,0);              // mov rax,54 (setsockopt)
        _a.Syscall();
        _a.Db(0x48,0x83,0xc4,0x10);                  // add rsp,16
        _a.Db(0x48,0x83,0xec,0x10);                  // sub rsp,16
        _a.Db(0x66,0xc7,0x04,0x24,2,0);              // family=2
        _a.Db(0x4c,0x89,0xe0);                       // mov rax, r12 (port)
        _a.Db(0x88,0x44,0x24,0x03);                  // [rsp+3]=low
        _a.Db(0x48,0xc1,0xe8,0x08);                  // shr rax,8
        _a.Db(0x88,0x44,0x24,0x02);                  // [rsp+2]=high
        _a.Db(0x48,0xc7,0x44,0x24,0x04,0,0,0,0);     // addr=0 (INADDR_ANY) + pad start
        _a.Db(0x48,0xc7,0x44,0x24,0x08,0,0,0,0);     // pad
        _a.Db(0x48,0x89,0xdf);                       // mov rdi, rbx
        _a.Db(0x48,0x89,0xe6);                       // mov rsi, rsp
        _a.Db(0x48,0xc7,0xc2,16,0,0,0);              // mov rdx,16
        _a.Db(0x48,0xc7,0xc0,49,0,0,0);              // mov rax,49 (bind)
        _a.Syscall();
        _a.Db(0x48,0x83,0xc4,0x10);                  // add rsp,16
        _a.TestRR(RAX, RAX); _a.Jcc(0x5, tlfail);    // e.g. EADDRINUSE: the port is taken
        _a.Db(0x48,0x89,0xdf);                       // mov rdi, rbx
        _a.Db(0x48,0xc7,0xc6,0x00,0x02,0,0);         // mov rsi,512 (backlog)
        _a.Db(0x48,0xc7,0xc0,50,0,0,0);              // mov rax,50 (listen)
        _a.Syscall();
        _a.TestRR(RAX, RAX); _a.Jcc(0x5, tlfail);
        _a.Db(0x48,0x89,0xd8);                       // mov rax, rbx (server fd)
        _a.CallRel("io_ok");
        _a.JmpRel(tldone);
        _a.Label(tlfail);                            // rax = -errno
        _a.CallRel("io_err_neg");
        _a.MovRR(RDI, RBX); _a.MovRI(RAX, 3); _a.Syscall();   // close
        _a.MovRI(RAX, -1); _a.JmpRel(tldone);
        _a.Label(tlsfail);
        _a.CallRel("io_err_neg");
        _a.MovRI(RAX, -1);
        _a.Label(tldone);
        _a.Db(0x48,0x89,0xec);                       // mov rsp, rbp
        _a.Db(0x41,0x5c,0x5b,0x5d);                  // pop r12,rbx,rbp
        _a.Ret();

        // sock_accept(rdi=serverfd) -> rax = client fd
        _a.Label("sock_accept");
        _a.Db(0x48,0x31,0xf6);                       // xor rsi, rsi (NULL)
        _a.Db(0x48,0x31,0xd2);                       // xor rdx, rdx (NULL)
        _a.Db(0x48,0xc7,0xc0,43,0,0,0);              // mov rax,43 (accept)
        _a.Syscall(); IoCheckSys(); _a.Ret();

        // readfile(rdi=path) -> the whole file as a string ("" if it cannot be opened).
        // open, fstat for the size (files that report 0, like /proc, read up to 1 MB), read until done.
        {
            _a.Label("readfile");
            string fail = NewLabel("rf_fail"), loop = NewLabel("rf_loop"), done = NewLabel("rf_done"), sized = NewLabel("rf_sized");
            string rfErr = NewLabel("rf_rerr");
            _a.PushR(RBP); _a.MovRR(RBP, RSP);
            _a.PushR(RBX); _a.PushR(R12); _a.PushR(R13); _a.PushR(R14);
            _a.AndRspImm8(-16); _a.AluRI(5, RSP, 160);
            _a.CallRel("io_ok");
            _a.MovRI(RSI, 0); _a.MovRI(RDX, 0); _a.MovRI(RAX, 2); _a.Syscall();   // open(path, O_RDONLY)
            _a.MovRR(RBX, RAX); _a.TestRR(RAX, RAX); _a.Jcc(0x8, fail);           // js
            _a.MovRR(RDI, RBX); _a.MovRR(RSI, RSP); _a.MovRI(RAX, 5); _a.Syscall(); // fstat
            _a.Ld(R12, RSP, 48);                                                   // st_size
            _a.TestRR(R12, R12); _a.Jcc(0xf, sized);                               // jg
            _a.MovRI(R12, 1 << 20);
            _a.Label(sized);
            _a.Lea(RDI, R12, 1); _a.CallRel("alloc"); _a.MovRR(R13, RAX);
            _a.MovRI(R14, 0);
            _a.Label(loop);
            _a.AluRR(0x39, R14, R12); _a.Jcc(0xd, done);
            _a.MovRR(RDI, RBX); _a.MovRR(RSI, R13); _a.AluRR(0x01, RSI, R14);
            _a.MovRR(RDX, R12); _a.AluRR(0x29, RDX, R14);
            _a.MovRI(RAX, 0); _a.Syscall();                                        // read
            _a.TestRR(RAX, RAX); _a.Jcc(0x8, rfErr);                                // js: error (e.g. EISDIR)
            _a.Jcc(0x4, done);                                                     // jz: end of file
            _a.AluRR(0x01, R14, RAX); _a.JmpRel(loop);
            _a.Label(rfErr);
            _a.CallRel("io_err_neg");                                              // keep what was read; ioerr says it is incomplete
            _a.Label(done);
            _a.MovRR(RAX, R13); _a.AluRR(0x01, RAX, R14); _a.Db(0xc6, 0x00, 0x00);
            _a.MovRR(RDI, RBX); _a.MovRI(RAX, 3); _a.Syscall();                    // close
            _a.MovRR(RAX, R13);
            string outl = NewLabel("rf_out");
            _a.JmpRel(outl);
            _a.Label(fail);                                                        // rax = -errno (e.g. ENOENT)
            _a.CallRel("io_err_neg");
            _a.MovRI(RDI, 8); _a.CallRel("alloc"); _a.Db(0xc6, 0x00, 0x00);
            _a.Label(outl);
            _a.Lea(RSP, RBP, -32);
            _a.PopR(R14); _a.PopR(R13); _a.PopR(R12); _a.PopR(RBX); _a.PopR(RBP);
            _a.Ret();
        }

        // writefile(rdi=path, rsi=data) -> rax = bytes written, or -1 (ioerr = errno)
        {
            _a.Label("writefile");
            string ofail = NewLabel("wf_ofail"), wfail = NewLabel("wf_wfail"), outl = NewLabel("wf_out");
            _a.Db(0x55,0x53,0x41,0x54);                  // push rbp,rbx,r12
            _a.Db(0x49,0x89,0xf4);                       // mov r12, rsi (data)
            _a.CallRel("io_ok");
            _a.Db(0x48,0xc7,0xc6,0x41,0x02,0,0);         // mov rsi, 0x241 (O_WRONLY|O_CREAT|O_TRUNC)
            _a.Db(0x48,0xc7,0xc2,0xa4,0x01,0,0);         // mov rdx, 0x1a4 (0644)
            _a.Db(0x48,0xc7,0xc0,2,0,0,0);               // mov rax,2 (open)
            _a.Syscall(); _a.Db(0x48,0x89,0xc3);         // mov rbx, fd
            _a.TestRR(RAX, RAX); _a.Jcc(0x8, ofail);     // e.g. ENOENT, EACCES
            _a.Db(0x4c,0x89,0xe7);                       // mov rdi, r12
            _a.CallRel("strlen"); _a.Db(0x48,0x89,0xc2); // mov rdx, rax (len)
            _a.Db(0x48,0x89,0xdf);                       // mov rdi, rbx (fd)
            _a.Db(0x4c,0x89,0xe6);                       // mov rsi, r12 (data)
            _a.Db(0x48,0xc7,0xc0,1,0,0,0);               // mov rax,1 (write)
            _a.Syscall();
            _a.TestRR(RAX, RAX); _a.Jcc(0x8, wfail);     // e.g. ENOSPC
            _a.Db(0x50);                                  // push rax (count)
            _a.Db(0x48,0x89,0xdf); _a.Db(0x48,0xc7,0xc0,3,0,0,0); _a.Syscall(); // close
            _a.Db(0x58);                                  // pop rax
            _a.JmpRel(outl);
            _a.Label(wfail);
            _a.CallRel("io_err_neg");
            _a.Db(0x48,0x89,0xdf); _a.Db(0x48,0xc7,0xc0,3,0,0,0); _a.Syscall(); // close
            _a.MovRI(RAX, -1); _a.JmpRel(outl);
            _a.Label(ofail);
            _a.CallRel("io_err_neg");
            _a.MovRI(RAX, -1);
            _a.Label(outl);
            _a.Db(0x41,0x5c,0x5b,0x5d);                  // pop r12,rbx,rbp
            _a.Ret();
        }
    }

    // emit: mov rax, [IAT slot for import i]; call rax
    void EmitIatCall(string name) {
        int importIdx = Pe.RuntimeImport(name);
        if (importIdx < 0) throw new InvalidOperationException("no import " + name);
        _a.Db(0x48, 0xa1);                 // mov rax, [abs64]
        _iatRelocs.Add((_a.Pos, importIdx));
        for (int i = 0; i < 8; i++) _a.Db(0);
        _a.CallRax();
    }

    // mov reg, imm64(slot address); recorded for patching. reg via opcode byte.
    void EmitSlotAddr(byte movOpcode, int which) {
        _a.Db(0x48, movOpcode);
        _slotRelocs.Add((_a.Pos, which));
        for (int i = 0; i < 8; i++) _a.Db(0);
    }

    // mov r9, imm64(slot address)  (needs REX.B prefix 0x49)
    void EmitSlotAddrR9(int which) {
        _a.Db(0x49, 0xb9);
        _slotRelocs.Add((_a.Pos, which));
        for (int i = 0; i < 8; i++) _a.Db(0);
    }

    // serial_init(): program the COM1 16550 UART for 38400 8N1 (matches the
    // validated reference). Each pair is: mov dx, port ; mov al, val ; out dx, al.
    void EmitSerial() {
        _a.Label("serial_init");
        void OutB(int port, int val) {
            _a.Db(0x66, 0xba, (byte)(port & 0xff), (byte)((port >> 8) & 0xff)); // mov dx, port
            _a.Db(0xb0, (byte)val);                                            // mov al, val
            _a.Db(0xee);                                                        // out dx, al
        }
        OutB(0x3F9, 0x00);   // disable interrupts
        OutB(0x3FB, 0x80);   // enable DLAB
        OutB(0x3F8, 0x03);   // divisor low (38400)
        OutB(0x3F9, 0x00);   // divisor high
        OutB(0x3FB, 0x03);   // 8 bits, no parity, 1 stop
        OutB(0x3FA, 0xC7);   // enable + clear FIFO
        OutB(0x3FC, 0x0B);   // DTR/RTS/OUT2
        _a.Ret();
    }

    // write_out(rsi=ptr, rdx=len) -> writes to stdout. Clobbers volatiles only.
    void EmitWriteOut() {
        _a.Label("write_out");
        if (_target == Target.Linux) {
            _a.MovRdiImm(1);               // fd = stdout
            _a.MovRaxImm(1);               // sys_write
            _a.Syscall();
            _a.Ret();
            return;
        }
        if (_target == Target.Kernel) {
            // write rdx bytes from [rsi] to COM1. Port I/O uses dx (= low 16 bits
            // of rdx), so move the length into rcx and pointer into r8 first --
            // otherwise each 'mov dx, port' corrupts the loop counter.
            string loop = NewLabel("ser_loop"), done = NewLabel("ser_done"), spin = NewLabel("ser_spin");
            _a.Db(0x48,0x89,0xd1);                 // mov rcx, rdx   (count)
            _a.Db(0x49,0x89,0xf0);                 // mov r8, rsi    (ptr)
            _a.Label(loop);
            _a.Db(0x48,0x85,0xc9);                 // test rcx, rcx
            _a.JzRel(done);
            _a.Db(0x45,0x8a,0x18);                 // mov r11b, [r8]  (next byte)
            _a.Label(spin);
            _a.Db(0x66,0xba,0xfd,0x03);            // mov dx, 0x3FD
            _a.Db(0xec);                            // in al, dx
            _a.Db(0xa8,0x20);                       // test al, 0x20  (THR empty?)
            _a.JzRel(spin);
            _a.Db(0x44,0x88,0xd8);                 // mov al, r11b
            _a.Db(0x66,0xba,0xf8,0x03);            // mov dx, 0x3F8
            _a.Db(0xee);                            // out dx, al
            _a.Db(0x49,0xff,0xc0);                 // inc r8
            _a.Db(0x48,0xff,0xc9);                 // dec rcx
            _a.JmpRel(loop);
            _a.Label(done);
            _a.Ret();
            return;
        }
        // Windows: WriteFile(handle, buf, len, &written, NULL)
        // arg regs: rcx=handle, rdx=buf, r8=len, r9=&written, [rsp+32]=0
        _a.PushRbp(); _a.MovRbpRsp();
        _a.AndRspImm8(-16);
        _a.SubRspImm8(48);                 // 32 shadow + 16 (5th arg + alignment)
        _a.MovR8Rdx();                     // r8 = len      (do before rdx is overwritten)
        _a.MovRdxRsi();                    // rdx = buffer
        EmitSlotAddr(0xb8, 0);             // mov rax, imm64(&handle)
        _a.Db(0x48, 0x8b, 0x08);           // mov rcx, [rax]   (rcx = handle value)
        EmitSlotAddrR9(1);                 // mov r9, imm64(&scratch)
        _a.MovQwordRspDispImm0(32);        // [rsp+32] = 0 (lpOverlapped)
        EmitIatCall("WriteFile");                    // WriteFile
        _a.MovRspRbp(); _a.PopRbp();
        _a.Ret();
    }

    // win_init(): handle = GetStdHandle(STD_OUTPUT_HANDLE = -11)
    void EmitWinInit() {
        _a.Label("win_init");
        _a.PushRbp(); _a.MovRbpRsp();
        _a.AndRspImm8(-16);
        _a.SubRspImm8(32);                 // shadow space
        _a.MovRcxImm(unchecked((long)0xFFFFFFFFFFFFFFF5)); // -11
        EmitIatCall("GetStdHandle");                    // GetStdHandle -> rax = handle
        EmitSlotAddr(0xa3, 0);             // mov [handle slot], rax
        _a.MovRspRbp(); _a.PopRbp();
        _a.Ret();
    }


    // working value and r8 as the digit count. Correct two's-complement sign.
    void EmitPrintInt() {
        _a.Label("print_int");
        _a.Db(0x53);                       // push rbx
        _a.Prologue(64);

        string notZero = NewLabel("pi_nz");
        string notNeg  = NewLabel("pi_nn");
        string loop    = NewLabel("pi_loop");
        string ploop   = NewLabel("pi_print");
        string pdone   = NewLabel("pi_pdone");
        string done    = NewLabel("pi_done");

        _a.Db(0x48,0x89,0xfb);             // mov rbx, rdi   (value)
        _a.Db(0x48,0x85,0xdb);             // test rbx, rbx
        _a.JnzRel(notZero);
        EmitPrintLiteral("0");
        _a.JmpRel(done);

        _a.Label(notZero);
        _a.Db(0x48,0x83,0xfb,0x00);        // cmp rbx, 0
        _a.SetccRax2_ge(notNeg);           // jge notNeg
        EmitPrintLiteral("-");
        _a.Db(0x48,0xf7,0xdb);             // neg rbx
        _a.Label(notNeg);

        // Build digits into a scratch buffer (the heap base) BACKWARD from the end,
        // so they end up in print order. rdi = write cursor, starts at heapBase+24.
        _a.Db(0x48,0x89,0xef);             // mov rdi, rbp  (buffer = this frame, below rbp: thread-safe)
        _a.Label(loop);
        _a.Db(0x48,0x89,0xd8);             // mov rax, rbx
        _a.Db(0x48,0x31,0xd2);             // xor rdx, rdx
        _a.Db(0x49,0xc7,0xc1,10,0,0,0);    // mov r9, 10
        _a.Db(0x49,0xf7,0xf1);             // div r9
        _a.Db(0x48,0x89,0xc3);             // mov rbx, rax            (quotient)
        _a.Db(0x48,0x83,0xc2,0x30);        // add rdx, '0'
        _a.Db(0x48,0xff,0xcf);             // dec rdi                 (move cursor left)
        _a.Db(0x88,0x17);                  // mov [rdi], dl           (store digit)
        _a.Db(0x48,0x85,0xdb);             // test rbx, rbx
        _a.JnzRel(loop);

        // print from rdi (first digit) for (heapBase+24 - rdi) bytes
        _a.Db(0x48,0x89,0xfe);             // mov rsi, rdi            (start)
        _a.Db(0x48,0x89,0xe8);             // mov rax, rbp            (end)
        _a.Db(0x48,0x29,0xf0);             // sub rax, rsi            (length)
        _a.Db(0x48,0x89,0xc2);             // mov rdx, rax            (len)
        _a.CallRel("write_out");

        _a.Label(done);
        _a.Db(0x48,0x89,0xec);             // mov rsp, rbp
        _a.Db(0x5d);                       // pop rbp
        _a.Db(0x5b);                       // pop rbx
        _a.Ret();
    }

    void EmitPrintStr() { /* reserved for future string variables */ }

    // ----------------------------------------------------------------- layout
    public byte[] LowerToFile(long entryOverride = 0) {
        if (_target == Target.Linux) return LowerElf();
        if (_target == Target.Kernel) return LowerKernel();
        return LowerPe();
    }

    byte[] LowerPe() {
        var code = Compile();
        // build the string blob
        var stringBlob = BuildStringBlob();
        var lay = Pe.PlanLayout(code.Length, stringBlob.Length, _usesNet);
        // patch string literal addresses
        foreach (var (codePos, dataOff) in _strRelocs) {
            long addr = lay.StringAddr(dataOff);
            PatchU64(code, codePos, addr);
        }
        // patch IAT call targets
        foreach (var (codePos, idx) in _iatRelocs)
            PatchU64(code, codePos, lay.IatSlot(idx));
        // patch mutable slot addresses
        foreach (var (codePos, which) in _slotRelocs)
            PatchU64(code, codePos, which switch {
                0 => lay.HandleAddr,
                1 => lay.ScratchAddr,
                2 => lay.HeapPtrAddr,
                _ => lay.HeapBaseAddr,
            });
        // patch code-label absolute addresses (e.g. thread start routines for spawn)
        long textBase = Pe.ImageBase + Pe.SectAlignPublic;
        foreach (var (codePos, label) in _a.CodeAddrFixups)
            PatchU64(code, codePos, textBase + _a.Labels[label]);
        long entry = Pe.ImageBase + (Pe.SectAlignPublic) + 0; // _start is first byte of .text
        if (!DebugInfo) return Pe.Build(code, stringBlob, lay, entry);
        // -g: unwind info, the debug directory and the PDB (Pdb.cs). They go after the code,
        // so the layout is planned again with the longer .text (string addresses move).
        int textLen = code.Length;
        var withDebug = new List<byte>(code);
        var wd = AppendWinDebug(withDebug, lay._textRva, lay._headers);
        var lay2 = Pe.PlanLayout(withDebug.Count, stringBlob.Length, _usesNet);
        if (lay2.DataVaddr != lay.DataVaddr) {
            foreach (var (codePos, dataOff) in _strRelocs) PatchU64(code, codePos, lay2.StringAddr(dataOff));
            foreach (var (codePos, idx) in _iatRelocs) PatchU64(code, codePos, lay2.IatSlot(idx));
            foreach (var (codePos, which) in _slotRelocs)
                PatchU64(code, codePos, which switch { 0 => lay2.HandleAddr, 1 => lay2.ScratchAddr, 2 => lay2.HeapPtrAddr, _ => lay2.HeapBaseAddr });
        }
        var full = withDebug.ToArray();
        Array.Copy(code, full, code.Length);
        var image = Pe.Build(full, stringBlob, lay2, entry, wd.PdataOff, wd.PdataLen, wd.DebugDirOff);
        PdbBytes = BuildPdb(image[Pe.SectionHeadersOffset..(Pe.SectionHeadersOffset + 80)], textLen);
        return image;
    }

    // Build a Multiboot1 ELF32 kernel: [multiboot header][32-bit long-mode stub]
    // [our 64-bit code][data], with page tables + stack in a zero-filled tail.
    // QEMU's -kernel loader enters the 32-bit stub, which sets up paging and long
    // mode, then far-jumps to our 64-bit _start. Output goes via the COM1 serial
    // port (see EmitWriteOut / EmitSerial).
    byte[] LowerKernel() {
        const long LOAD = 0x100000;           // load at 1 MB (multiboot standard)
        const int KernelStack = 64 * 1024;    // the boot stack

        // The 64-bit code begins after: multiboot header (un-padded 12B) +
        // stub. We reserve a fixed 0x400-byte header region; code starts at
        // LOAD + 0x400 so addresses are stable regardless of stub length.
        const int HEADER_REGION = 0x400;
        long codeVaddr = LOAD + HEADER_REGION;

        // generate our 64-bit code with _start at codeVaddr
        _kernelCodeVaddr = codeVaddr;
        var code = Compile();

        // data (strings + heapPtr) immediately after code -- MUST match the
        // contiguous image layout below, so addresses we patch are correct.
        long dataVaddr = codeVaddr + code.Length;
        var strings = BuildStringBlob();
        int heapPtrOff = 0, stringsOff = 8;
        var data = new byte[stringsOff + strings.Length];
        Array.Copy(strings, 0, data, stringsOff, strings.Length);
        long heapPtrAddr = dataVaddr + heapPtrOff;
        // heap lives high, well clear of code/data (1MB region), page tables and
        // stack: a zero-filled region from 2MB.
        long heapBaseAddr = 0x200000;

        // The page tables (PML4, PDPT, PD) and the stack go in the zero-filled tail
        // right after the image, page aligned. They used to sit at fixed addresses
        // (0x101000..0x108000), which a kernel with more than ~3 KB of code overlapped:
        // the boot stub then wrote table entries over the code and the CPU faulted on
        // the code bytes as page-table entries.
        long imageEnd = codeVaddr + code.Length + data.Length;
        long tables = (imageEnd + 0xfff) & ~0xfffL;
        long PML4 = tables, PDPT = tables + 0x1000, PD = tables + 0x2000;
        long STACK_TOP = tables + 0x3000 + KernelStack;
        if (STACK_TOP > heapBaseAddr)
            throw new ForgeException($"kernel too large: code and data end at 0x{imageEnd:X}; with page tables and a {KernelStack / 1024} KB stack they must stay below the heap at 0x{heapBaseAddr:X}", 1);

        // patch relocations in code
        foreach (var (p, off) in _strRelocs) PatchU64(code, p, dataVaddr + stringsOff + off);
        foreach (var (p, which) in _slotRelocs) PatchU64(code, p, which == 2 ? heapPtrAddr : heapBaseAddr);
        foreach (var (p, label) in _a.CodeAddrFixups) PatchU64(code, p, codeVaddr + _a.Labels[label]);

        // build the 32-bit boot stub, which ends by jumping to codeVaddr
        var stub = BuildBootStub(PML4, PDPT, PD, STACK_TOP, codeVaddr);

        // assemble the loaded image: [0x00 multiboot header][stub ...][pad to 0x400][code][data]
        int imgLen = HEADER_REGION + code.Length + data.Length;
        var img = new byte[imgLen];
        // multiboot header at offset 0
        WriteMultiboot(img, 0);
        // stub right after the 12-byte header
        Array.Copy(stub, 0, img, 12, stub.Length);
        // 64-bit code at HEADER_REGION
        Array.Copy(code, 0, img, HEADER_REGION, code.Length);
        Array.Copy(data, 0, img, HEADER_REGION + code.Length, data.Length);

        // memsz extends past the file image to cover page tables + stack + heap
        long memEnd = Math.Max(heapBaseAddr + Pe.HeapSize, STACK_TOP + 0x1000);
        int memSize = (int)(memEnd - LOAD);

        return Elf32.Build(img, LOAD, LOAD + 12, memSize);   // entry = the 32-bit stub
    }

    static void WriteMultiboot(byte[] buf, int off) {
        // magic, flags=0, checksum
        void U32(int o, long v) { for (int i = 0; i < 4; i++) buf[o + i] = (byte)((v >> (8 * i)) & 0xff); }
        long magic = 0x1BADB002, flags = 0;
        U32(off + 0, magic);
        U32(off + 4, flags);
        U32(off + 8, (-(magic + flags)) & 0xffffffffL);
    }

    // The validated 32-bit -> 64-bit trampoline, generated to jump to codeVaddr.
    static byte[] BuildBootStub(long pml4, long pdpt, long pd, long stackTop, long codeVaddr) {
        var b = new List<byte>();
        void Db(params int[] xs) { foreach (var x in xs) b.Add((byte)x); }
        void Imm32(long v) { for (int i = 0; i < 4; i++) b.Add((byte)((v >> (8 * i)) & 0xff)); }
        // mov esp, stackTop
        Db(0xbc); Imm32(stackTop);
        // mov eax, pdpt ; or eax,3 ; mov [pml4], eax
        Db(0xb8); Imm32(pdpt); Db(0x83, 0xc8, 0x03); Db(0xa3); Imm32(pml4);
        // mov eax, pd ; or eax,3 ; mov [pdpt], eax
        Db(0xb8); Imm32(pd); Db(0x83, 0xc8, 0x03); Db(0xa3); Imm32(pdpt);
        // mov ecx, 0
        Db(0xb9); Imm32(0);
        // .map: mov eax,0x200000 ; mul ecx ; or eax,0x83 ; mov [pd+ecx*8],eax ; mov dword [pd+ecx*8+4],0 ; inc ecx ; cmp ecx,512 ; jne .map
        int mapStart = b.Count;
        Db(0xb8); Imm32(0x200000);                 // mov eax,0x200000
        Db(0xf7, 0xe1);                            // mul ecx
        Db(0x0d); Imm32(0x83);                     // or eax,0x83
        Db(0x89, 0x04, 0xcd); Imm32(pd);           // mov [pd + ecx*8], eax
        Db(0xc7, 0x04, 0xcd); Imm32(pd + 4); Imm32(0); // mov dword [pd+ecx*8+4], 0
        Db(0x41);                                  // inc ecx
        Db(0x81, 0xf9); Imm32(512);                // cmp ecx, 512
        int rel = mapStart - (b.Count + 2);        // jne rel8 back to mapStart
        Db(0x75, rel & 0xff);
        // mov eax, pml4 ; mov cr3, eax
        Db(0xb8); Imm32(pml4); Db(0x0f, 0x22, 0xd8);
        // mov eax, cr4 ; or eax, 1<<5 ; mov cr4, eax
        Db(0x0f, 0x20, 0xe0); Db(0x83, 0xc8, 0x20); Db(0x0f, 0x22, 0xe0);
        // mov ecx, 0xC0000080 ; rdmsr ; or eax, 1<<8 ; wrmsr
        Db(0xb9); Imm32(0xC0000080); Db(0x0f, 0x32); Db(0x0d); Imm32(0x100); Db(0x0f, 0x30);
        // mov eax, cr0 ; or eax, 1<<31 ; mov cr0, eax
        Db(0x0f, 0x20, 0xc0); Db(0x0d); Imm32(0x80000000); Db(0x0f, 0x22, 0xc0);
        // lgdt [gdtptr] ; jmp 0x8:codeVaddr  -- the GDT lives at the end of the stub
        // we compute the GDT address as codeVaddr won't include it; place GDT inside
        // the stub at a known offset and reference it absolutely.
        // First emit lgdt with a placeholder, fix after we know the GDT address.
        int lgdtPos = b.Count;
        Db(0x0f, 0x01, 0x15); Imm32(0);            // lgdt [abs32]  (patch later)
        // Load the data segments from our GDT (selector 0x10). SS still held the
        // boot loader's selector, which our GDT didn't define, so the first iretq
        // from an interrupt handler (it reloads SS) raised a general protection fault.
        Db(0x66, 0xb8, 0x10, 0x00);                // mov ax, 0x10
        Db(0x8e, 0xd8); Db(0x8e, 0xc0); Db(0x8e, 0xd0);   // mov ds, ax ; mov es, ax ; mov ss, ax
        // far jump: EA <off32> <sel16>
        Db(0xea); Imm32(codeVaddr); Db(0x08, 0x00);
        // --- GDT ---
        int gdtOff = b.Count;
        // null, then 64-bit code descriptor
        for (int i = 0; i < 8; i++) b.Add(0);
        // code: (1<<43)|(1<<44)|(1<<47)|(1<<53)
        long codeDesc = (1L << 43) | (1L << 44) | (1L << 47) | (1L << 53);
        for (int i = 0; i < 8; i++) b.Add((byte)((codeDesc >> (8 * i)) & 0xff));
        // 0x10: flat 4 GB data segment (present, writable, 4 KB granularity, 32-bit)
        long dataDesc = 0x00CF92000000FFFFL;
        for (int i = 0; i < 8; i++) b.Add((byte)((dataDesc >> (8 * i)) & 0xff));
        int gdtPtrOff = b.Count;
        // gdt pointer: limit (dw), base (dq for lgdt in... actually lgdt in 32-bit uses 6 bytes: limit16+base32)
        // We are still in 32-bit mode at lgdt, so use 16-bit limit + 32-bit base.
        int limit = (gdtPtrOff - gdtOff) - 1;
        b.Add((byte)(limit & 0xff)); b.Add((byte)((limit >> 8) & 0xff));
        // base = absolute address of GDT = codeRegionBase? The stub is loaded at LOAD+12.
        // Compute absolute GDT address: LOAD + 12 + gdtOff
        long stubBase = 0x100000 + 12;
        long gdtAbs = stubBase + gdtOff;
        Imm32(gdtAbs);
        // patch the lgdt operand to point at gdtPtr (absolute)
        long gdtPtrAbs = stubBase + gdtPtrOff;
        b[lgdtPos + 3] = (byte)(gdtPtrAbs & 0xff);
        b[lgdtPos + 4] = (byte)((gdtPtrAbs >> 8) & 0xff);
        b[lgdtPos + 5] = (byte)((gdtPtrAbs >> 16) & 0xff);
        b[lgdtPos + 6] = (byte)((gdtPtrAbs >> 24) & 0xff);
        return b.ToArray();
    }

    long _kernelCodeVaddr;

    byte[] LowerElf() {
        var code = Compile();
        long codeEnd = CODE_VADDR + code.Length;
        _dataVaddr = (codeEnd + 0xfff) & ~0xfffL;

        // .data layout (Linux): [heapPtr slot (8)] [GOT: one slot per extern] [strings...]
        // [dynamic-linking tables, if any externs] then a virtual heap.
        var strings = BuildStringBlob();
        var exts = _mod.Externs;
        int heapPtrOff = 0;
        int gotOff = 8;
        int stringsOff = gotOff + 8 * exts.Count;
        var dyn = exts.Count > 0 ? BuildDynamic(exts, _dataVaddr, gotOff, (stringsOff + strings.Length + 7) & ~7) : ((byte[] Bytes, int Start, int End, int InterpOff, int InterpLen, int DynOff, int DynLen)?)null;
        int contentLen = dyn != null ? dyn.Value.End : stringsOff + strings.Length;
        int heapBaseOff = (contentLen + 15) & ~15;
        var data = new byte[contentLen];
        Array.Copy(strings, 0, data, stringsOff, strings.Length);
        if (dyn != null) Array.Copy(dyn.Value.Bytes, 0, data, dyn.Value.Start, dyn.Value.Bytes.Length);
        foreach (var (codePos, slot) in _gotRelocs)
            PatchU64(code, codePos, _dataVaddr + gotOff + 8 * slot);

        long heapPtrAddr  = _dataVaddr + heapPtrOff;
        long heapBaseAddr = _dataVaddr + heapBaseOff;

        foreach (var (codePos, dataOff) in _strRelocs)
            PatchU64(code, codePos, _dataVaddr + stringsOff + dataOff);
        foreach (var (codePos, which) in _slotRelocs)
            PatchU64(code, codePos, which == 2 ? heapPtrAddr : heapBaseAddr);
        foreach (var (codePos, label) in _a.CodeAddrFixups)
            PatchU64(code, codePos, CODE_VADDR + _a.Labels[label]);

        long heapMemSize = Pe.HeapSize;
        var debug = DebugInfo ? DwarfSections(CODE_VADDR, code.Length) : null;     // -g (DebugInfo.cs)
        var syms = DebugInfo ? ElfSymbols(CODE_VADDR) : null;
        if (dyn == null) return Elf.Build(code, CODE_VADDR, data, _dataVaddr, CODE_VADDR, heapBaseOff + (int)heapMemSize, debug: debug, symbols: syms);
        var d = dyn.Value;
        return Elf.Build(code, CODE_VADDR, data, _dataVaddr, CODE_VADDR, heapBaseOff + (int)heapMemSize,
                         d.InterpOff, d.InterpLen, d.DynOff, d.DynLen, debug, syms);
    }

    // The tables the dynamic loader (ld.so) needs to fill the GOT: interpreter path,
    // string table, symbol table, a SysV hash table, GLOB_DAT relocations, .dynamic.
    // Placed at data offset 'start'; all addresses are absolute (ET_EXEC).
    (byte[] Bytes, int Start, int End, int InterpOff, int InterpLen, int DynOff, int DynLen)
        BuildDynamic(List<ExternDecl> exts, long dataVaddr, int gotOff, int start) {
        var b = new List<byte>();
        void Pad8() { while ((start + b.Count) % 8 != 0) b.Add(0); }
        void U32(long v) { for (int i = 0; i < 4; i++) b.Add((byte)(v >> (8 * i))); }
        void U64(long v) { for (int i = 0; i < 8; i++) b.Add((byte)(v >> (8 * i))); }
        long Addr() => dataVaddr + start + b.Count;

        int interpOff = start + b.Count;
        var ip = Encoding.ASCII.GetBytes(LinuxInterp + "\0"); b.AddRange(ip);
        // dynstr
        long strtab = Addr(); var str = new List<byte> { 0 };
        int AddStr(string x) { int o = str.Count; str.AddRange(Encoding.ASCII.GetBytes(x)); str.Add(0); return o; }
        var libs = exts.Select(e => e.Lib).Distinct().ToList();
        var libOff = libs.Select(AddStr).ToList();
        var symOff = exts.Select(e => AddStr(e.Name)).ToList();
        b.AddRange(str); Pad8();
        // dynsym: entry 0 is the null symbol
        long symtab = Addr();
        for (int i = 0; i < 24; i++) b.Add(0);
        foreach (int so in symOff) { U32(so); b.Add(0x12); b.Add(0); b.Add(0); b.Add(0); U64(0); U64(0); }  // GLOBAL FUNC, undefined
        // rela: R_X86_64_GLOB_DAT for each GOT slot
        long rela = Addr();
        for (int i = 0; i < exts.Count; i++) { U64(dataVaddr + gotOff + 8 * i); U64(((long)(i + 1) << 32) | 6); U64(0); }
        // hash: one empty bucket (nothing here is exported)
        long hash = Addr();
        U32(1); U32(exts.Count + 1); U32(0); for (int i = 0; i <= exts.Count; i++) U32(0);
        Pad8();
        int dynOff = start + b.Count;
        foreach (int lo in libOff) { U64(1); U64(lo); }      // DT_NEEDED
        U64(4); U64(hash);                                   // DT_HASH
        U64(5); U64(strtab);                                 // DT_STRTAB
        U64(6); U64(symtab);                                 // DT_SYMTAB
        U64(10); U64(str.Count);                             // DT_STRSZ
        U64(11); U64(24);                                    // DT_SYMENT
        U64(7); U64(rela);                                   // DT_RELA
        U64(8); U64(24L * exts.Count);                       // DT_RELASZ
        U64(9); U64(24);                                     // DT_RELAENT
        U64(30); U64(8);                                     // DT_FLAGS = BIND_NOW
        U64(21); U64(0);                                     // DT_DEBUG (for debuggers)
        U64(0); U64(0);                                      // DT_NULL
        int dynLen = start + b.Count - dynOff;
        return (b.ToArray(), start, start + b.Count, interpOff, ip.Length, dynOff, dynLen);
    }

    byte[] BuildStringBlob() {
        var data = new byte[_dataLen];
        int p = 0;
        foreach (var blob in _data) { Array.Copy(blob, 0, data, p, blob.Length); p += blob.Length; }
        return data;
    }

    static void PatchU64(byte[] buf, int pos, long val) {
        for (int i = 0; i < 8; i++) buf[pos + i] = (byte)((val >> (8 * i)) & 0xff);
    }

    public long CodeVaddr => CODE_VADDR;
    public long DataVaddr => _dataVaddr;
    public long EntryVaddr => CODE_VADDR; // _start is first emitted
}
