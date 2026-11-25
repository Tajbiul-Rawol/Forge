using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ForgeC;

// Inline assembly: asm("mov rax, cr3; mov {root}, rax");
// Intel syntax, one instruction per line or ';'. Labels "name:" are local to the
// block. {var} is a Forge variable (kept in memory for the function, so it is a
// qword memory operand [rbp-n]). The block may use any scratch register (rax rcx
// rdx rsi rdi r8-r11); it must preserve rbx rbp rsp r12-r15.
sealed class InlineAsm {
    readonly Asm _a;
    readonly Func<string, int> _varOffset;     // variable -> rbp-relative offset
    readonly string _prefix;                   // makes this block's labels unique
    readonly int _line;

    public InlineAsm(Asm a, Func<string, int> varOffset, string labelPrefix, int line) {
        _a = a; _varOffset = varOffset; _prefix = labelPrefix; _line = line;
    }

    // ---- operands
    enum K { Reg, Imm, Mem, Label }
    sealed class Op {
        public K Kind; public int Size;            // bytes: 1 2 4 8 (0 = unknown, for memory)
        public int Reg;                            // Reg: number;  Mem: base (-1 none)
        public int Index = -1, Scale = 1; public long Disp;
        public string Cls = "gp";                  // gp | cr | seg
        public string Label = "";
        public long Imm;
    }

    static readonly string[] R64 = { "rax","rcx","rdx","rbx","rsp","rbp","rsi","rdi","r8","r9","r10","r11","r12","r13","r14","r15" };
    static readonly string[] R32 = { "eax","ecx","edx","ebx","esp","ebp","esi","edi","r8d","r9d","r10d","r11d","r12d","r13d","r14d","r15d" };
    static readonly string[] R16 = { "ax","cx","dx","bx","sp","bp","si","di","r8w","r9w","r10w","r11w","r12w","r13w","r14w","r15w" };
    static readonly string[] R8  = { "al","cl","dl","bl","spl","bpl","sil","dil","r8b","r9b","r10b","r11b","r12b","r13b","r14b","r15b" };
    static readonly string[] Seg = { "es","cs","ss","ds","fs","gs" };
    static readonly Dictionary<string, int> Cc = new() {
        ["o"]=0,["no"]=1,["b"]=2,["c"]=2,["nae"]=2,["ae"]=3,["nb"]=3,["nc"]=3,["e"]=4,["z"]=4,["ne"]=5,["nz"]=5,
        ["be"]=6,["na"]=6,["a"]=7,["nbe"]=7,["s"]=8,["ns"]=9,["p"]=10,["pe"]=10,["np"]=11,["po"]=11,
        ["l"]=12,["nge"]=12,["ge"]=13,["nl"]=13,["le"]=14,["ng"]=14,["g"]=15,["nle"]=15 };

    ForgeException Err(string m) => new ForgeException("asm: " + m, _line);

    bool TryReg(string t, out Op op) {
        op = new Op { Kind = K.Reg };
        int i;
        if ((i = Array.IndexOf(R64, t)) >= 0) { op.Reg = i; op.Size = 8; return true; }
        if ((i = Array.IndexOf(R32, t)) >= 0) { op.Reg = i; op.Size = 4; return true; }
        if ((i = Array.IndexOf(R16, t)) >= 0) { op.Reg = i; op.Size = 2; return true; }
        if ((i = Array.IndexOf(R8, t)) >= 0)  { op.Reg = i; op.Size = 1; return true; }
        if ((i = Array.IndexOf(Seg, t)) >= 0) { op.Reg = i; op.Size = 2; op.Cls = "seg"; return true; }
        if (t.StartsWith("cr") && int.TryParse(t[2..], out int c) && c is 0 or 2 or 3 or 4 or 8) { op.Reg = c; op.Size = 8; op.Cls = "cr"; return true; }
        return false;
    }
    static bool TryNum(string t, out long v) {
        t = t.Trim(); bool neg = false;
        if (t.StartsWith("-")) { neg = true; t = t[1..].Trim(); }
        bool ok = t.StartsWith("0x") ? long.TryParse(t[2..], NumberStyles.HexNumber, null, out v)
                : t.EndsWith("h") && t.Length > 1 && char.IsDigit(t[0]) ? long.TryParse(t[..^1], NumberStyles.HexNumber, null, out v)
                : long.TryParse(t, out v);
        if (neg) v = -v;
        return ok;
    }

    Op Operand(string t) {
        t = t.Trim();
        int size = 0;
        foreach (var (w, n) in new[] { ("byte", 1), ("word", 2), ("dword", 4), ("qword", 8) })
            if (t.StartsWith(w + " ")) { size = n; t = t[w.Length..].Trim(); if (t.StartsWith("ptr ")) t = t[4..].Trim(); break; }
        if (t.StartsWith("{") && t.EndsWith("}")) {           // Forge variable
            string name = t[1..^1].Trim();
            return new Op { Kind = K.Mem, Size = size == 0 ? 8 : size, Reg = 5, Disp = _varOffset(name) };
        }
        if (t.StartsWith("[") && t.EndsWith("]")) return Memory(t[1..^1], size);
        if (TryReg(t, out var r)) return r;
        if (TryNum(t, out long v)) return new Op { Kind = K.Imm, Imm = v, Size = size };
        if (t.Length > 0 && (char.IsLetter(t[0]) || t[0] == '_' || t[0] == '.')) return new Op { Kind = K.Label, Label = t };
        throw Err($"bad operand '{t}'");
    }
    Op Memory(string inner, int size) {
        var op = new Op { Kind = K.Mem, Size = size, Reg = -1 };
        string s = inner.Replace(" ", "").Replace("-", "+-");
        foreach (var part0 in s.Split('+', StringSplitOptions.RemoveEmptyEntries)) {
            string part = part0;
            if (part.Contains('*')) {
                var f = part.Split('*');
                string rn = f[0], sc = f[1];
                if (!TryReg(rn, out var ir)) { (rn, sc) = (sc, rn); if (!TryReg(rn, out ir)) throw Err($"bad index in [{inner}]"); }
                if (!TryNum(sc, out long scale) || scale is not (1 or 2 or 4 or 8)) throw Err($"bad scale in [{inner}]");
                op.Index = ir.Reg; op.Scale = (int)scale;
            } else if (TryReg(part, out var br) && br.Cls == "gp" && br.Size == 8) {
                if (op.Reg < 0) op.Reg = br.Reg; else if (op.Index < 0) { op.Index = br.Reg; op.Scale = 1; } else throw Err($"too many registers in [{inner}]");
            } else if (TryNum(part, out long d)) op.Disp += d;
            else if (op.Reg < 0 && op.Index < 0 && (char.IsLetter(part[0]) || part[0] == '_')) { op.Kind = K.Label; op.Label = part; }
            else throw Err($"bad memory operand [{inner}]");
        }
        if (op.Index == 4) throw Err("rsp cannot be an index register");
        if (!Asm.Fits32(op.Disp)) throw Err("displacement too large");
        return op;
    }

    // ---- encoding: [66] [REX] opcode... modrm [sib] [disp]
    void Enc(int size, byte[] opc, int regField, Op rm, bool needW = true, int imm = 0, int immBytes = 0) {
        if (size == 2) _a.Db(0x66);
        int rex = (size == 8 && needW ? 8 : 0) | (((regField >> 3) & 1) << 2);
        bool rmByte = size == 1 || IsByteRegOp;                  // spl/bpl/sil/dil need a REX prefix
        bool forceRex = (rmByte && rm.Kind == K.Reg && rm.Reg is >= 4 and <= 7) || (size == 1 && regField is >= 4 and <= 7);
        if (rm.Kind == K.Reg) rex |= (rm.Reg >> 3) & 1;
        else { if (rm.Index >= 0) rex |= ((rm.Index >> 3) & 1) << 1; if (rm.Reg >= 0) rex |= (rm.Reg >> 3) & 1; }
        if (rex != 0 || forceRex) _a.Db((byte)(0x40 | rex));
        foreach (var b in opc) _a.Db(b);
        int rf = regField & 7;
        if (rm.Kind == K.Reg) _a.Db((byte)(0xc0 | (rf << 3) | (rm.Reg & 7)));
        else if (rm.Reg < 0 && rm.Index < 0) {                 // absolute [disp32]
            _a.Db((byte)((rf << 3) | 4)); _a.Db(0x25); Imm32((int)rm.Disp);
        } else {
            int bas = rm.Reg, disp = (int)rm.Disp;
            bool noBase = bas < 0;
            int mod = noBase ? 0 : disp == 0 && (bas & 7) != 5 ? 0 : Asm.Fits8(disp) ? 1 : 2;
            if (rm.Index < 0 && !noBase && (bas & 7) != 4) _a.Db((byte)((mod << 6) | (rf << 3) | (bas & 7)));
            else {
                int ss = rm.Scale == 1 ? 0 : rm.Scale == 2 ? 1 : rm.Scale == 4 ? 2 : 3;
                _a.Db((byte)((mod << 6) | (rf << 3) | 4));
                _a.Db((byte)((ss << 6) | ((rm.Index < 0 ? 4 : rm.Index & 7) << 3) | (noBase ? 5 : bas & 7)));
            }
            if (noBase || mod == 2) Imm32(disp); else if (mod == 1) _a.Db((byte)(sbyte)disp);
        }
        if (immBytes == 1) _a.Db((byte)imm); else if (immBytes == 2) { _a.Db((byte)imm); _a.Db((byte)(imm >> 8)); } else if (immBytes == 4) Imm32(imm);
    }
    bool IsByteRegOp;
    void Imm32(int v) { _a.Db((byte)v); _a.Db((byte)(v >> 8)); _a.Db((byte)(v >> 16)); _a.Db((byte)(v >> 24)); }
    string L(string name) => _prefix + name;

    int SizeOf(Op a, Op b) {
        int s = a.Kind == K.Reg ? a.Size : b.Kind == K.Reg ? b.Size : a.Size != 0 ? a.Size : b.Size;
        if (s == 0) throw Err("operand size unknown: add byte/word/dword/qword ptr");
        return s;
    }

    public void Assemble(string text) {
        foreach (var raw in text.Split(new[] { '\n', ';' })) {
            string line = raw.Trim();
            int hash = line.IndexOf('#'); if (hash >= 0) line = line[..hash].Trim();
            if (line.Length == 0) continue;
            if (line.EndsWith(":") && !line.Contains(' ')) { _a.Label(L(line[..^1])); continue; }
            Instr(line);
        }
    }

    void Instr(string line) {
        string mn, rest = "";
        int sp = line.IndexOf(' ');
        if (sp < 0) mn = line.ToLowerInvariant(); else { mn = line[..sp].ToLowerInvariant(); rest = line[(sp + 1)..].Trim(); }
        if (mn is "lock" or "rep" or "repe" or "repz" or "repne" or "repnz") {
            _a.Db(mn == "lock" ? (byte)0xf0 : mn is "repne" or "repnz" ? (byte)0xf2 : (byte)0xf3);
            Instr(rest); return;
        }
        var ops = SplitOps(rest).Select(Operand).ToList();
        IsByteRegOp = false;

        switch (mn) {   // no operands
            case "cli": _a.Db(0xfa); return;  case "sti": _a.Db(0xfb); return;  case "hlt": _a.Db(0xf4); return;
            case "nop": _a.Db(0x90); return;  case "pause": _a.Db(0xf3, 0x90); return;
            case "iretq": _a.Db(0x48, 0xcf); return;  case "ret": _a.Db(0xc3); return;
            case "retfq": case "lretq": _a.Db(0x48, 0xcb); return;
            case "cpuid": _a.Db(0x0f, 0xa2); return;  case "rdtsc": _a.Db(0x0f, 0x31); return;
            case "rdmsr": _a.Db(0x0f, 0x32); return;  case "wrmsr": _a.Db(0x0f, 0x30); return;
            case "swapgs": _a.Db(0x0f, 0x01, 0xf8); return;  case "int3": _a.Db(0xcc); return;
            case "ud2": _a.Db(0x0f, 0x0b); return;  case "cld": _a.Db(0xfc); return;  case "std": _a.Db(0xfd); return;
            case "mfence": _a.Db(0x0f, 0xae, 0xf0); return;  case "lfence": _a.Db(0x0f, 0xae, 0xe8); return;
            case "sfence": _a.Db(0x0f, 0xae, 0xf8); return;  case "wbinvd": _a.Db(0x0f, 0x09); return;
            case "clts": _a.Db(0x0f, 0x06); return;  case "cqo": _a.Db(0x48, 0x99); return;
            case "pushfq": _a.Db(0x9c); return;  case "popfq": _a.Db(0x9d); return;
            case "syscall": _a.Db(0x0f, 0x05); return;  case "sysretq": _a.Db(0x48, 0x0f, 0x07); return;
            case "movsb": _a.Db(0xa4); return;  case "movsq": _a.Db(0x48, 0xa5); return;
            case "stosb": _a.Db(0xaa); return;  case "stosq": _a.Db(0x48, 0xab); return;  case "lodsb": _a.Db(0xac); return;
        }
        if (ops.Count == 0 && mn != "int") throw Err($"unknown instruction '{mn}'");

        // ---- jumps and calls to labels / registers
        if (mn == "jmp" || mn == "call" || (mn.StartsWith("j") && Cc.ContainsKey(mn[1..]))) {
            var t = ops[0];
            if (t.Kind == K.Label) {
                if (mn == "jmp") _a.JmpRel(L(t.Label));
                else if (mn == "call") _a.CallRel(L(t.Label));
                else _a.Jcc((byte)Cc[mn[1..]], L(t.Label));
                return;
            }
            if (mn == "jmp" || mn == "call") { Enc(4, new byte[] { 0xff }, mn == "jmp" ? 4 : 2, t); return; }
            throw Err($"{mn} needs a label");
        }
        if (mn.StartsWith("set") && Cc.TryGetValue(mn[3..], out int scc)) { Enc(1, new byte[] { 0x0f, (byte)(0x90 + scc) }, 0, ops[0]); return; }
        if (mn.StartsWith("cmov") && Cc.TryGetValue(mn[4..], out int mcc)) { Need(ops, 2); Enc(ops[0].Size, new byte[] { 0x0f, (byte)(0x40 + mcc) }, ops[0].Reg, ops[1]); return; }

        switch (mn) {
            case "int": { if (ops.Count != 1 || ops[0].Kind != K.Imm) throw Err("int needs a number"); _a.Db(0xcd, (byte)ops[0].Imm); return; }
            case "push": {
                var o = ops[0];
                if (o.Kind == K.Reg) { if (o.Reg >= 8) _a.Db(0x41); _a.Db((byte)(0x50 + (o.Reg & 7))); return; }
                if (o.Kind == K.Imm) { if (Asm.Fits8(o.Imm)) _a.Db(0x6a, (byte)o.Imm); else { _a.Db(0x68); Imm32((int)o.Imm); } return; }
                if (o.Kind == K.Label) { _a.MovRegLabelAddr(11, L(o.Label)); _a.Db(0x41, 0x53); return; }   // via r11
                Enc(4, new byte[] { 0xff }, 6, o); return;
            }
            case "pop": {
                var o = ops[0];
                if (o.Kind == K.Reg) { if (o.Reg >= 8) _a.Db(0x41); _a.Db((byte)(0x58 + (o.Reg & 7))); return; }
                Enc(4, new byte[] { 0x8f }, 0, o); return;
            }
            case "mov": Mov(ops); return;
            case "lea": {
                Need(ops, 2);
                if (ops[1].Kind == K.Label) { _a.MovRegLabelAddr(ops[0].Reg, L(ops[1].Label)); return; }   // absolute address of a label
                Enc(8, new byte[] { 0x8d }, ops[0].Reg, ops[1]); return;
            }
            case "add": case "or": case "adc": case "sbb": case "and": case "sub": case "xor": case "cmp":
                Alu(Array.IndexOf(new[] { "add", "or", "adc", "sbb", "and", "sub", "xor", "cmp" }, mn), ops); return;
            case "test": {
                Need(ops, 2); int sz = SizeOf(ops[0], ops[1]);
                if (ops[1].Kind == K.Imm) { Enc(sz, new byte[] { sz == 1 ? (byte)0xf6 : (byte)0xf7 }, 0, ops[0], true, (int)ops[1].Imm, Math.Min(sz, 4)); return; }
                Enc(sz, new byte[] { sz == 1 ? (byte)0x84 : (byte)0x85 }, ops[1].Reg, ops[0]); return;
            }
            case "xchg": { Need(ops, 2); int sz = SizeOf(ops[0], ops[1]); var (rm, rg) = ops[1].Kind == K.Reg ? (ops[0], ops[1]) : (ops[1], ops[0]);
                Enc(sz, new byte[] { sz == 1 ? (byte)0x86 : (byte)0x87 }, rg.Reg, rm); return; }
            case "cmpxchg": case "xadd": { Need(ops, 2); int sz = SizeOf(ops[0], ops[1]);
                byte o2 = (byte)((mn == "cmpxchg" ? 0xb0 : 0xc0) + (sz == 1 ? 0 : 1));
                Enc(sz, new byte[] { 0x0f, o2 }, ops[1].Reg, ops[0]); return; }
            case "inc": case "dec": case "not": case "neg": case "mul": case "div": case "idiv": {
                int sz = SizeOf(ops[0], ops[0]);
                int ext = mn switch { "inc" => 0, "dec" => 1, "not" => 2, "neg" => 3, "mul" => 4, "div" => 6, _ => 7 };
                byte opc = mn is "inc" or "dec" ? (sz == 1 ? (byte)0xfe : (byte)0xff) : (sz == 1 ? (byte)0xf6 : (byte)0xf7);
                Enc(sz, new byte[] { opc }, ext, ops[0]); return;
            }
            case "imul": {
                if (ops.Count == 1) { int sz = SizeOf(ops[0], ops[0]); Enc(sz, new byte[] { sz == 1 ? (byte)0xf6 : (byte)0xf7 }, 5, ops[0]); return; }
                if (ops.Count == 2) { Enc(ops[0].Size, new byte[] { 0x0f, 0xaf }, ops[0].Reg, ops[1]); return; }
                Enc(ops[0].Size, new byte[] { 0x69 }, ops[0].Reg, ops[1], true, (int)ops[2].Imm, 4); return;
            }
            case "shl": case "sal": case "shr": case "sar": case "rol": case "ror": {
                Need(ops, 2); int sz = SizeOf(ops[0], ops[0]);
                int ext = mn switch { "rol" => 0, "ror" => 1, "shl" or "sal" => 4, "shr" => 5, _ => 7 };
                if (ops[1].Kind == K.Reg && ops[1].Reg == 1 && ops[1].Size == 1) { Enc(sz, new byte[] { sz == 1 ? (byte)0xd2 : (byte)0xd3 }, ext, ops[0]); return; }
                if (ops[1].Kind != K.Imm) throw Err($"{mn} count must be a number or cl");
                Enc(sz, new byte[] { sz == 1 ? (byte)0xc0 : (byte)0xc1 }, ext, ops[0], true, (int)ops[1].Imm, 1); return;
            }
            case "bt": case "bts": case "btr": case "btc": {
                Need(ops, 2); int sz = SizeOf(ops[0], ops[0]);
                int ext = mn switch { "bt" => 4, "bts" => 5, "btr" => 6, _ => 7 };
                if (ops[1].Kind != K.Imm) throw Err($"{mn}: bit number must be a constant");
                Enc(sz, new byte[] { 0x0f, 0xba }, ext, ops[0], true, (int)ops[1].Imm, 1); return;
            }
            case "movzx": case "movsx": {
                Need(ops, 2); int srcSz = ops[1].Size == 0 ? throw Err($"{mn}: give the source size (byte/word ptr)") : ops[1].Size;
                byte o2 = (byte)((mn == "movzx" ? 0xb6 : 0xbe) + (srcSz == 2 ? 1 : 0));
                IsByteRegOp = srcSz == 1;
                Enc(ops[0].Size, new byte[] { 0x0f, o2 }, ops[0].Reg, ops[1]); return;
            }
            case "movsxd": { Need(ops, 2); Enc(8, new byte[] { 0x63 }, ops[0].Reg, ops[1]); return; }
            case "in": {
                Need(ops, 2); var d = ops[0]; if (d.Kind != K.Reg || d.Reg != 0) throw Err("in: destination must be al, ax or eax");
                if (d.Size == 2) _a.Db(0x66);
                byte b0 = d.Size == 1 ? (byte)0xe4 : (byte)0xe5;
                if (ops[1].Kind == K.Imm) _a.Db(b0, (byte)ops[1].Imm);
                else if (ops[1].Kind == K.Reg && ops[1].Reg == 2 && ops[1].Size == 2) _a.Db((byte)(b0 + 8));
                else throw Err("in: port must be a number or dx");
                return;
            }
            case "out": {
                Need(ops, 2); var v = ops[1]; if (v.Kind != K.Reg || v.Reg != 0) throw Err("out: value must be al, ax or eax");
                if (v.Size == 2) _a.Db(0x66);
                byte b0 = v.Size == 1 ? (byte)0xe6 : (byte)0xe7;
                if (ops[0].Kind == K.Imm) _a.Db(b0, (byte)ops[0].Imm);
                else if (ops[0].Kind == K.Reg && ops[0].Reg == 2 && ops[0].Size == 2) _a.Db((byte)(b0 + 8));
                else throw Err("out: port must be a number or dx");
                return;
            }
            case "lgdt": Enc(4, new byte[] { 0x0f, 0x01 }, 2, ops[0]); return;
            case "lidt": Enc(4, new byte[] { 0x0f, 0x01 }, 3, ops[0]); return;
            case "sgdt": Enc(4, new byte[] { 0x0f, 0x01 }, 0, ops[0]); return;
            case "sidt": Enc(4, new byte[] { 0x0f, 0x01 }, 1, ops[0]); return;
            case "invlpg": Enc(4, new byte[] { 0x0f, 0x01 }, 7, ops[0]); return;
            case "ltr": Enc(4, new byte[] { 0x0f, 0x00 }, 3, ops[0]); return;
            case "lldt": Enc(4, new byte[] { 0x0f, 0x00 }, 2, ops[0]); return;
        }
        throw Err($"unknown instruction '{mn}'");
    }

    void Need(List<Op> ops, int n) { if (ops.Count != n) throw Err($"expected {n} operand(s)"); }

    void Alu(int ext, List<Op> ops) {
        Need(ops, 2);
        var (d, s) = (ops[0], ops[1]);
        int sz = SizeOf(d, s);
        if (s.Kind == K.Imm) {
            if (sz == 1) { Enc(1, new byte[] { 0x80 }, ext, d, true, (int)s.Imm, 1); return; }
            if (Asm.Fits8(s.Imm)) { Enc(sz, new byte[] { 0x83 }, ext, d, true, (int)s.Imm, 1); return; }
            Enc(sz, new byte[] { 0x81 }, ext, d, true, (int)s.Imm, sz == 2 ? 2 : 4); return;
        }
        if (s.Kind == K.Reg) { Enc(sz, new byte[] { (byte)(ext * 8 + (sz == 1 ? 0 : 1)) }, s.Reg, d); return; }
        if (d.Kind == K.Reg) { Enc(sz, new byte[] { (byte)(ext * 8 + (sz == 1 ? 2 : 3)) }, d.Reg, s); return; }
        throw Err("two memory operands");
    }

    void Mov(List<Op> ops) {
        Need(ops, 2);
        var (d, s) = (ops[0], ops[1]);
        if (d.Cls == "cr" || s.Cls == "cr") {                 // mov cr3, rax / mov rax, cr3
            if (d.Cls == "cr") Enc(4, new byte[] { 0x0f, 0x22 }, d.Reg, s);
            else Enc(4, new byte[] { 0x0f, 0x20 }, s.Reg, d);
            return;
        }
        if (d.Cls == "seg") { Enc(4, new byte[] { 0x8e }, d.Reg, s); return; }        // mov ds, ax
        if (s.Cls == "seg") { Enc(d.Kind == K.Reg && d.Size == 8 ? 8 : 4, new byte[] { 0x8c }, s.Reg, d); return; }
        if (s.Kind == K.Label) { _a.MovRegLabelAddr(d.Reg, L(s.Label)); return; }
        int sz = SizeOf(d, s);
        if (s.Kind == K.Imm) {
            if (d.Kind == K.Reg) {
                if (sz == 8 && !Asm.Fits32(s.Imm)) { _a.Db((byte)(0x48 | (d.Reg >> 3))); _a.Db((byte)(0xb8 + (d.Reg & 7))); for (int i = 0; i < 8; i++) _a.Db((byte)(s.Imm >> (8 * i))); return; }
                if (sz == 8) { Enc(8, new byte[] { 0xc7 }, 0, d, true, (int)s.Imm, 4); return; }
                if (sz == 2) _a.Db(0x66);
                if (d.Reg >= 8 || (sz == 1 && d.Reg is >= 4 and <= 7)) _a.Db((byte)(0x40 | (d.Reg >> 3)));
                if (sz == 1) { _a.Db((byte)(0xb0 + (d.Reg & 7)), (byte)s.Imm); return; }
                _a.Db((byte)(0xb8 + (d.Reg & 7)));
                if (sz == 2) { _a.Db((byte)s.Imm); _a.Db((byte)(s.Imm >> 8)); } else Imm32((int)s.Imm);
                return;
            }
            Enc(sz, new byte[] { sz == 1 ? (byte)0xc6 : (byte)0xc7 }, 0, d, true, (int)s.Imm, sz == 1 ? 1 : sz == 2 ? 2 : 4);
            return;
        }
        if (s.Kind == K.Reg) { IsByteRegOp = sz == 1; Enc(sz, new byte[] { sz == 1 ? (byte)0x88 : (byte)0x89 }, s.Reg, d); return; }
        if (d.Kind == K.Reg) { IsByteRegOp = sz == 1; Enc(sz, new byte[] { sz == 1 ? (byte)0x8a : (byte)0x8b }, d.Reg, s); return; }
        throw Err("two memory operands");
    }

    static List<string> SplitOps(string s) {
        var outp = new List<string>(); int depth = 0, start = 0;
        for (int i = 0; i < s.Length; i++) {
            if (s[i] is '[' or '{') depth++; else if (s[i] is ']' or '}') depth--;
            else if (s[i] == ',' && depth == 0) { outp.Add(s[start..i]); start = i + 1; }
        }
        if (s.Trim().Length > 0) outp.Add(s[start..]);
        return outp;
    }
}
