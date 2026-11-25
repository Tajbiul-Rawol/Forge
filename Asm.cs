using System;
using System.Collections.Generic;

namespace ForgeC;

// A growable machine-code buffer with helpers for the x86-64 instructions we need,
// plus label/fixup support for forward jumps and calls. Every method appends raw
// opcode bytes -- this is the actual assembler.
sealed class Asm {
    public readonly List<byte> Code = new();
    readonly Dictionary<string, int> _labels = new();
    readonly List<(int Pos, string Label)> _rel32 = new(); // 4-byte rel fixups (jmp/call/jcc)

    public int Pos => Code.Count;
    public void Db(params byte[] b) => Code.AddRange(b);
    public void Db(int b) => Code.Add((byte)b);

    void Imm32(int v) { Db(v & 0xff); Db((v >> 8) & 0xff); Db((v >> 16) & 0xff); Db((v >> 24) & 0xff); }
    void Imm64(long v) { for (int i = 0; i < 8; i++) Db((int)((v >> (8 * i)) & 0xff)); }

    public void Label(string name) {
        if (_labels.ContainsKey(name)) throw new Exception("dup label " + name);
        _labels[name] = Pos;
    }

    // Records: "patch the imm64 at this code position with the absolute VA of <label>".
    // The lowering stage fills these in once the image base/layout is known.
    public readonly List<(int pos, string label)> CodeAddrFixups = new();
    public IReadOnlyDictionary<string, int> Labels => _labels;

    // mov r8, imm64(&label)  -- emits a placeholder, records a CodeAddrFixup.
    public void MovR8LabelAddr(string label) {
        Db(0x49, 0xb8);                 // mov r8, imm64
        CodeAddrFixups.Add((Pos, label));
        for (int i = 0; i < 8; i++) Db(0);
    }

    // mov <reg>, imm64(&label) for rax/rcx/rdx/rbx/rsp/rbp/rsi/rdi (reg 0..7).
    public void MovRegLabelAddr(int reg, string label) {
        Db(0x48, (byte)(0xb8 + reg));
        CodeAddrFixups.Add((Pos, label));
        for (int i = 0; i < 8; i++) Db(0);
    }

    // ---- register moves / stack ------------------------------------------------
    // Registers we use: rax=0, rcx=1, rdx=2, rbx=3, rsp=4, rbp=5, rsi=6, rdi=7
    public void MovRaxImm(long v) { Db(0x48, 0xb8); Imm64(v); }          // mov rax, imm64
    public void MovRdiImm(long v) { Db(0x48, 0xbf); Imm64(v); }          // mov rdi, imm64
    public void MovRsiImm(long v) { Db(0x48, 0xbe); Imm64(v); }          // mov rsi, imm64
    public void MovRdxImm(long v) { Db(0x48, 0xba); Imm64(v); }          // mov rdx, imm64
    public void PushRax() => Db(0x50);
    public void PopRax()  => Db(0x58);
    public void PopRcx()  => Db(0x59);
    public void PopRdi()  => Db(0x5f);
    public void PushReg(int r) => Db(0x50 + r);
    public void PopReg(int r)  => Db(0x58 + r);

    // mov rax, [rbp + disp32]   and   mov [rbp + disp32], rax
    public void LoadLocal(int disp)  { Db(0x48, 0x8b, 0x85); Imm32(disp); }
    public void StoreLocal(int disp) { Db(0x48, 0x89, 0x85); Imm32(disp); }
    // lea rax, [rbp + disp32]
    public void LeaLocal(int disp)   { Db(0x48, 0x8d, 0x85); Imm32(disp); }

    public void MovRcxRax() => Db(0x48, 0x89, 0xc1);   // mov rcx, rax
    public void MovRaxRcx() => Db(0x48, 0x89, 0xc8);   // mov rax, rcx
    public void MovRdiRax() => Db(0x48, 0x89, 0xc7);   // mov rdi, rax
    public void MovRaxRdi() => Db(0x48, 0x89, 0xf8);   // mov rax, rdi
    public void MovRsiRax() => Db(0x48, 0x89, 0xc6);   // mov rsi, rax

    // ---- arithmetic on rax with rcx (rcx = left, rax = right after our convention)
    public void AddRaxRcx() => Db(0x48, 0x01, 0xc8);   // add rax, rcx
    public void SubRcxRax_toRax() { Db(0x48, 0x29, 0xc1); MovRaxRcx(); } // rcx-=rax; rax=rcx
    public void ImulRaxRcx() => Db(0x48, 0x0f, 0xaf, 0xc1); // imul rax, rcx
    // signed divide: dividend in rax, divisor in rcx -> quotient rax, remainder rdx
    public void Cqo() => Db(0x48, 0x99);
    public void IdivRcx() => Db(0x48, 0xf7, 0xf9);
    public void NegRax() => Db(0x48, 0xf7, 0xd8);

    // ---- comparisons: result (0/1) into rax ----------------------------------
    public void CmpRcxRax() => Db(0x48, 0x39, 0xc1);   // cmp rcx, rax
    public void CmpRaxRcx() => Db(0x48, 0x39, 0xc8);   // cmp rax, rcx
    // jge label  (signed greater-or-equal)
    public void SetccRax2_ge(string label) { Db(0x0f, 0x8d); _rel32.Add((Pos, label)); Db(0,0,0,0); }
    // setcc al ; movzx rax, al
    public void SetccRax(byte cc) { Db(0x0f, cc, 0xc0); Db(0x48, 0x0f, 0xb6, 0xc0); }
    public void TestRaxRax() => Db(0x48, 0x85, 0xc0);

    // ---- control flow ---------------------------------------------------------
    public void JmpRel(string label)  { Db(0xe9); _rel32.Add((Pos, label)); Imm32(0); }
    // Record a 4-byte rel32 fixup at the current position (caller already emitted the opcode).
    public void Rel32To(string label) { _rel32.Add((Pos, label)); Imm32(0); }
    public void JzRel(string label)   { Db(0x0f, 0x84); _rel32.Add((Pos, label)); Imm32(0); }
    public void JnzRel(string label)  { Db(0x0f, 0x85); _rel32.Add((Pos, label)); Imm32(0); }
    public void CallRel(string label) { Db(0xe8); _rel32.Add((Pos, label)); Imm32(0); }
    public void Ret() => Db(0xc3);
    public void Syscall() => Db(0x0f, 0x05);

    // ---- extras for the Windows ABI -------------------------------------------
    public void MovRcxImm(long v) { Db(0x48, 0xb9); Imm64(v); }          // mov rcx, imm64
    public void MovR8Imm(long v)  { Db(0x49, 0xb8); Imm64(v); }          // mov r8, imm64
    public void MovR9Imm(long v)  { Db(0x49, 0xb9); Imm64(v); }          // mov r9, imm64
    public void MovR8Rdx()  => Db(0x49, 0x89, 0xd0);                     // mov r8, rdx
    public void MovRdxRsi() => Db(0x48, 0x89, 0xf2);                     // mov rdx, rsi
    public void MovRaxAbs(long addr) { Db(0x48, 0xa1); Imm64(addr); }    // mov rax, [abs64]
    public void MovAbsRax(long addr) { Db(0x48, 0xa3); Imm64(addr); }    // mov [abs64], rax
    public void CallRax()   => Db(0xff, 0xd0);                           // call rax
    public void MovRcxFromAbs(long addr) {                              // mov rcx, [abs64]
        // load via rax-free path: mov rcx, [rip?]; simplest: mov rax,[abs]; mov rcx,rax
        MovRaxAbs(addr); Db(0x48, 0x89, 0xc1);                          // mov rcx, rax
    }
    public void AndRspImm8(sbyte v) { Db(0x48, 0x83, 0xe4, (byte)v); }   // and rsp, imm8 (e.g. -16)
    public void SubRspImm8(byte v)  { Db(0x48, 0x83, 0xec, v); }         // sub rsp, imm8
    public void MovRbpRsp() => Db(0x48, 0x89, 0xe5);                     // mov rbp, rsp
    public void MovRspRbp() => Db(0x48, 0x89, 0xec);                     // mov rsp, rbp
    public void PushRbp()   => Db(0x55);
    public void PopRbp()    => Db(0x5d);
    // mov qword [rsp+disp8], imm32 (sign-extended) — used to zero the 5th arg
    public void MovQwordRspDispImm0(byte disp) { Db(0x48, 0xc7, 0x44, 0x24, disp, 0,0,0,0); }
    // lea r9, [abs] isn't simple; we use mov r9, imm64 with the absolute scratch address
    public void MovR9Abs(long addr) { MovR9Imm(addr); }

    // ---- heap + array support --------------------------------------------------
    public void MovR10Rax() => Db(0x49, 0x89, 0xc2);                    // mov r10, rax
    public void MovRaxR10() => Db(0x4c, 0x89, 0xd0);                    // mov rax, r10
    public void AddRaxRdi() => Db(0x48, 0x01, 0xf8);                    // add rax, rdi
    // lea rdi, [rax*8 + 8]   (bytes for an n-element array incl. 8-byte length header)
    public void LeaRdiArrayBytes() { Db(0x48, 0x8d, 0x3c, 0xc5, 0x08, 0x00, 0x00, 0x00); }
    public void LoadElemRax()  => Db(0x48, 0x8b, 0x04, 0xc8);           // mov rax, [rax + rcx*8]
    public void StoreElemRdx() => Db(0x48, 0x89, 0x14, 0xc8);          // mov [rax + rcx*8], rdx
    public void LoadLenRax()   => Db(0x48, 0x8b, 0x40, 0xf8);          // mov rax, [rax - 8]
    public void MovQwordPtrRaxRcx() => Db(0x48, 0x89, 0x08);           // mov [rax], rcx
    public void AddRaxImm8(byte v) => Db(0x48, 0x83, 0xc0, v);         // add rax, imm8

    // ---- pointer load/store: address in rax, value in rdx (store) / rax (load) -
    public void Store8()  => Db(0x88, 0x10);             // mov [rax], dl
    public void Store16() => Db(0x66, 0x89, 0x10);       // mov [rax], dx
    public void Store32() => Db(0x89, 0x10);             // mov [rax], edx
    public void Store64() => Db(0x48, 0x89, 0x10);       // mov [rax], rdx
    public void LoadU8()  => Db(0x48, 0x0f, 0xb6, 0x00); // movzx rax, byte [rax]
    public void LoadI8()  => Db(0x48, 0x0f, 0xbe, 0x00); // movsx rax, byte [rax]
    public void LoadU16() => Db(0x48, 0x0f, 0xb7, 0x00); // movzx rax, word [rax]
    public void LoadI16() => Db(0x48, 0x0f, 0xbf, 0x00); // movsx rax, word [rax]
    public void LoadU32() => Db(0x8b, 0x00);             // mov eax, [rax]  (zero-extends)
    public void LoadI32() => Db(0x48, 0x63, 0x00);       // movsxd rax, [rax]
    public void Load64()  => Db(0x48, 0x8b, 0x00);       // mov rax, [rax]
    public void ShlRcxK(byte k) => Db(0x48, 0xc1, 0xe1, k); // shl rcx, imm8
    public void AddRaxRcx2() => Db(0x48, 0x01, 0xc8);    // add rax, rcx

    // ---- general-register forms (any of rax..r15, numbered 0..15) --------------
    // Used by the optimizing code generator: register-allocated variables live in
    // rbx/r12..r15, and these encoders handle the REX bits for r8..r15.
    static byte Rex(int reg, int rm) => (byte)(0x48 | (reg >= 8 ? 4 : 0) | (rm >= 8 ? 1 : 0));
    static byte ModRR(int reg, int rm) => (byte)(0xC0 | ((reg & 7) << 3) | (rm & 7));
    public static bool Fits8(long v) => v >= -128 && v <= 127;
    public static bool Fits32(long v) => v >= int.MinValue && v <= int.MaxValue;

    public void MovRR(int dst, int src) { if (dst != src) Db(Rex(src, dst), 0x89, ModRR(src, dst)); }
    // mov dst, imm (never touches flags)
    public void MovRI(int dst, long v) {
        if (Fits32(v)) { Db(Rex(0, dst), 0xc7, ModRR(0, dst)); Imm32((int)v); }
        else { Db(Rex(0, dst), (byte)(0xb8 + (dst & 7))); Imm64(v); }
    }
    // <op> dst, src   for op = 0x01 add, 0x29 sub, 0x21 and, 0x09 or, 0x31 xor, 0x39 cmp
    public void AluRR(byte op, int dst, int src) => Db(Rex(src, dst), op, ModRR(src, dst));
    // <op> dst, imm32 for ext = 0 add, 1 or, 4 and, 5 sub, 6 xor, 7 cmp
    public void AluRI(int ext, int dst, int imm) {
        if (Fits8(imm)) Db(Rex(0, dst), 0x83, ModRR(ext, dst), (byte)(sbyte)imm);
        else { Db(Rex(0, dst), 0x81, ModRR(ext, dst)); Imm32(imm); }
    }
    // <op> reg, [rbp+disp]  (op = the "reg, r/m" opcode: 0x03 add, 0x2b sub, 0x23 and, 0x0b or, 0x33 xor, 0x3b cmp, 0x8b mov)
    public void AluRM(byte op, int reg, int disp) { Db(Rex(reg, 5), op, (byte)(0x80 | ((reg & 7) << 3) | 5)); Imm32(disp); }
    public void LoadSlot(int reg, int disp)  => AluRM(0x8b, reg, disp);                 // mov reg, [rbp+disp]
    public void StoreSlot(int reg, int disp) { Db(Rex(reg, 5), 0x89, (byte)(0x80 | ((reg & 7) << 3) | 5)); Imm32(disp); }
    // lea dst, [base + disp]
    public void Lea(int dst, int bas, int disp) {
        bool d8 = Fits8(disp);
        Db(Rex(dst, bas), 0x8d, (byte)((d8 ? 0x40 : 0x80) | ((dst & 7) << 3) | (bas & 7)));
        if ((bas & 7) == 4) Db(0x24);                  // rsp/r12 base needs a SIB byte
        if (d8) Db((byte)(sbyte)disp); else Imm32(disp);
    }
    // <op> reg, [base + disp] / [base + disp], reg  (op 0x8b load, 0x89 store, 0x3b cmp, 0x03 add, 0x85 test)
    public void Mem(byte op, int reg, int bas, int disp) {
        bool d8 = Fits8(disp);
        Db(Rex(reg, bas), op, (byte)((d8 ? 0x40 : 0x80) | ((reg & 7) << 3) | (bas & 7)));
        if ((bas & 7) == 4) Db(0x24);                  // rsp/r12 base needs a SIB byte
        if (d8) Db((byte)(sbyte)disp); else Imm32(disp);
    }
    // <op> reg, [bas + idx*scale + disp]  - the full x86 address form. idx = -1 for none
    // (never rsp). w: REX.W (64-bit operand). p66: operand-size prefix (16-bit stores).
    // op: one opcode byte, or 0x0f followed by op2.
    public void MemSib(int op, int op2, bool w, bool p66, int reg, int bas, int idx, int scale, int disp) {
        if (p66) Db(0x66);
        int rex = (w ? 8 : 0) | ((reg >> 3) << 2) | (idx >= 0 ? ((idx >> 3) << 1) : 0) | (bas >> 3);
        Db((byte)(0x40 | rex));                        // always a REX: harmless, and makes dl/sil/dil byte regs
        Db((byte)op); if (op == 0x0f) Db((byte)op2);
        int mod = disp == 0 && (bas & 7) != 5 ? 0 : Fits8(disp) ? 1 : 2;
        if (idx < 0 && (bas & 7) != 4) {
            Db((byte)((mod << 6) | ((reg & 7) << 3) | (bas & 7)));
        } else {
            int ss = scale == 1 ? 0 : scale == 2 ? 1 : scale == 4 ? 2 : 3;
            Db((byte)((mod << 6) | ((reg & 7) << 3) | 4));
            Db((byte)((ss << 6) | ((idx < 0 ? 4 : idx) & 7) << 3 | (bas & 7)));
        }
        if (mod == 1) Db((byte)(sbyte)disp); else if (mod == 2) Imm32(disp);
    }

    public void Ld(int dst, int bas, int disp) => Mem(0x8b, dst, bas, disp);     // mov dst, [base+disp]
    public void St(int bas, int disp, int src) => Mem(0x89, src, bas, disp);     // mov [base+disp], src
    public void StImm(int bas, int disp, int imm) { Mem(0xc7, 0, bas, disp); Imm32(imm); } // mov qword [base+disp], imm32
    public void TestRR(int a, int b) => Db(Rex(b, a), 0x85, ModRR(b, a));
    public void ImulRR(int dst, int src) => Db(Rex(dst, src), 0x0f, 0xaf, ModRR(dst, src));
    public void ImulRRI(int dst, int src, int imm) { Db(Rex(dst, src), 0x69, ModRR(dst, src)); Imm32(imm); }
    public void ImulRM(int dst, int disp) { Db(Rex(dst, 5), 0x0f, 0xaf, (byte)(0x80 | ((dst & 7) << 3) | 5)); Imm32(disp); }
    public void ShiftRI(int ext, int dst, byte k) => Db(Rex(0, dst), 0xc1, ModRR(ext, dst), k); // ext 4 shl, 5 shr
    public void PushR(int r) { if (r >= 8) Db(0x41); Db(0x50 + (r & 7)); }
    public void PopR(int r)  { if (r >= 8) Db(0x41); Db(0x58 + (r & 7)); }
    // jcc rel32; cc is the condition nibble (4 e, 5 ne, 0xc l, 0xd ge, 0xe le, 0xf g)
    public void Jcc(int cc, string label) { Db(0x0f, (byte)(0x80 | cc)); _rel32.Add((Pos, label)); Imm32(0); }

    // ---- frame -----------------------------------------------------------------
    public void Prologue(int frameBytes) {
        Db(0x55);                       // push rbp
        Db(0x48, 0x89, 0xe5);           // mov rbp, rsp
        if (frameBytes > 0) { Db(0x48, 0x81, 0xec); Imm32(frameBytes); } // sub rsp, imm32
    }
    public void Epilogue() {
        Db(0x48, 0x89, 0xec);           // mov rsp, rbp
        Db(0x5d);                       // pop rbp
        Ret();
    }

    public void Resolve() {
        foreach (var (pos, label) in _rel32) {
            if (!_labels.TryGetValue(label, out int target))
                throw new Exception("unresolved label: " + label);
            int rel = target - (pos + 4);
            Code[pos]     = (byte)(rel & 0xff);
            Code[pos + 1] = (byte)((rel >> 8) & 0xff);
            Code[pos + 2] = (byte)((rel >> 16) & 0xff);
            Code[pos + 3] = (byte)((rel >> 24) & 0xff);
        }
    }
}
