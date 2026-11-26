using System.Collections.Generic;

namespace ForgeC;

// Array bounds checks.
//
// An array's length sits in the 8 bytes before its first element ([ptr - 8]), so the
// check for a[i] is one compare against memory and one branch that is never taken:
//
//     cmp  idx, [arr - 8]       ; unsigned: a negative index is a huge number
//     jae  oob$N                ; -> cold stub at the end of the function
//
// It needs no scratch register (the register allocator may hold variables in all of
// them). a[i + k] (k folded into the displacement) adds k to the index register for
// the compare and subtracts it again; a constant index compares the length with an
// immediate. The cold stub passes index, length and line to bounds_fail, which prints
//
//     Index 5 is out of range for an array of length 3 (line 12)
//
// (or "(geometry.fg line 12)" for code from an imported file: one bounds_fail per file)
//
// and exits with code 1, like the List / Substring checks in the standard library.
// Pointers (p[i]) are never checked, and neither are strings (their length is not
// stored) or the compiler's own runtime code. `--no-bounds-check` turns checks off.
sealed partial class CodeGen {
    public bool BoundsChecks = true;
    readonly SortedSet<int> _boundsFiles = new();           // file numbers that need a bounds_fail
    readonly List<(string Label, int Base, int Index, int K, int Line)> _boundsStubs = new();

    bool ChecksBounds => BoundsChecks && !_cur.IsRuntime;

    // m addresses element (index + m.Disp / 8) of the array whose pointer is in m.Base.
    // Leaves every register as it was (flags aside).
    void EmitBoundsCheck(MemRef m, int line) {
        if (!ChecksBounds) return;
        string fail = NewLabel("oob");
        int k = m.Disp / 8;
        if (m.Index < 0) {                                   // constant index k
            if (k < 0) _a.JmpRel(fail);
            else {
                _a.MemSib(0x81, 0, true, false, 7, m.Base, -1, 1, -8);   // cmp qword [base - 8], k
                EmitImm32(k);
                _a.Jcc(0x6, fail);                                        // jbe: length <= k
            }
        } else {
            if (k != 0) _a.AluRI(0, m.Index, k);                          // add idx, k
            _a.MemSib(0x3b, 0, true, false, m.Index, m.Base, -1, 1, -8);  // cmp idx, [base - 8]
            _a.Jcc(0x3, fail);                                            // jae (unsigned)
            if (k != 0) _a.AluRI(5, m.Index, k);                          // sub idx, k
        }
        _boundsStubs.Add((fail, m.Base, m.Index, k, line));
    }

    // The cold stubs of the function just emitted: rdi = index, rsi = length, rdx = line.
    // Pushes first, so it does not matter which registers hold the base and the index.
    void EmitBoundsStubs() {
        foreach (var s in _boundsStubs) {
            _a.Label(s.Label);
            if (s.Index < 0) { _a.Db(0x68); EmitImm32(s.K); }          // push k
            else _a.PushR(s.Index);                                    // push idx (+ k already added)
            _a.MemSib(0xff, 0, false, false, 6, s.Base, -1, 1, -8);    // push qword [base - 8]
            _a.PopR(RSI); _a.PopR(RDI);
            int file = Sources.FileOf(s.Line);
            _a.MovRI(RDX, Sources.LineOf(s.Line));
            _a.JmpRel(BoundsFailLabel(file));
            _boundsFiles.Add(System.Math.Max(file, 0));
        }
        _boundsStubs.Clear();
    }

    // Never returns. rbx/r12/r13 survive print_int and write_out on every target.
    static string BoundsFailLabel(int file) => file <= 0 ? "bounds_fail" : "bounds_fail$" + file;   // -1: compiler-supplied code
    void EmitBoundsFail() { foreach (int f in _boundsFiles) EmitBoundsFail(f); }
    void EmitBoundsFail(int file) {
        _a.Label(BoundsFailLabel(file));
        _a.MovRR(RBX, RDI); _a.MovRR(R12, RSI); _a.MovRR(R13, RDX);
        _a.AndRspImm8(-16);
        EmitPrintLiteral("Index ");
        _a.MovRR(RDI, RBX); _a.CallRel("print_int");
        EmitPrintLiteral(" is out of range for an array of length ");
        _a.MovRR(RDI, R12); _a.CallRel("print_int");
        string where = file <= 0 || file >= Sources.Files.Count ? "" : System.IO.Path.GetFileName(Sources.Files[file]) + " ";
        EmitPrintLiteral($" ({where}line ");
        _a.MovRR(RDI, R13); _a.CallRel("print_int");
        EmitPrintLiteral(")\n");
        _a.MovRaxImm(1);
        EmitExit();
    }
}
