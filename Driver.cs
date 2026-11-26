using System;
using System.Collections.Generic;
using System.IO;

namespace ForgeC;

// Writes a minimal static ELF64 executable for x86-64 Linux with two PT_LOAD
// segments: one for code (r-x) and one for data (rw-). No interpreter, no libc.
static class Elf {
    // interpOff/dynOff (>= 0): offsets inside 'data' of the interpreter path and the
    // .dynamic table, for a dynamically linked program (one that calls externs).
    // debug (-g): DWARF sections and function symbols, written after the data with a
    // section header table (an executable without -g has no section headers at all).
    public static byte[] Build(byte[] code, long codeVaddr, byte[] data, long dataVaddr, long entry, int dataMemSize = -1,
                               int interpOff = -1, int interpLen = 0, int dynOff = -1, int dynLen = 0,
                               List<(string Name, byte[] Bytes)>? debug = null, List<(string Name, long Addr, long Size)>? symbols = null) {
        const int EHSIZE = 64;
        const int PHSIZE = 56;
        const int PHNUM = 4;      // [INTERP|NULL] [LOAD code] [LOAD data] [DYNAMIC|NULL]: fixed, so code always starts at the same address
        int phoff = EHSIZE;
        int headerBytes = EHSIZE + PHSIZE * PHNUM;

        // Map the whole file from offset 0 at a page-aligned base. Then file
        // offset N maps to vaddr base+N, satisfying the p_align congruence
        // (vaddr % page == offset % page) automatically.
        long baseVaddr = 0x400000;
        int codeFileOff = headerBytes;          // code starts right after headers

        int afterCode = codeFileOff + code.Length;
        int dataFileOff = (afterCode + 0xfff) & ~0xfff;

        var buf = new List<byte>();

        void U8(int v) => buf.Add((byte)v);
        void U16(int v) { U8(v & 0xff); U8((v>>8)&0xff); }
        void U32(long v) { U8((int)(v&0xff)); U8((int)((v>>8)&0xff)); U8((int)((v>>16)&0xff)); U8((int)((v>>24)&0xff)); }
        void U64(long v) { U32(v & 0xffffffff); U32((v>>32)&0xffffffff); }

        // ---- ELF header ----
        U8(0x7f); U8('E'); U8('L'); U8('F');
        U8(2); U8(1); U8(1); U8(0);          // 64-bit, little-endian, version, SysV
        for (int i = 0; i < 8; i++) U8(0);   // padding
        U16(2);                               // e_type = ET_EXEC
        U16(0x3e);                            // e_machine = x86-64
        U32(1);                               // e_version
        U64(entry);                           // e_entry
        U64(phoff);                           // e_phoff
        U64(0);                               // e_shoff
        U32(0);                               // e_flags
        U16(EHSIZE);                          // e_ehsize
        U16(PHSIZE);                          // e_phentsize
        U16(PHNUM);                           // e_phnum
        U16(0);                               // e_shentsize
        U16(0);                               // e_shnum
        U16(0);                               // e_shstrndx

        void Seg(int type, int flags, long off, long vaddr, long filesz, long memsz, long align) {
            U32(type); U32(flags); U64(off); U64(vaddr); U64(vaddr); U64(filesz); U64(memsz); U64(align);
        }
        // ---- program header 0: interpreter (dynamic programs) or unused ----
        if (interpOff >= 0) Seg(3, 4, dataFileOff + interpOff, dataVaddr + interpOff, interpLen, interpLen, 1);
        else Seg(0, 0, 0, 0, 0, 0, 0);

        // ---- program header 1: code (also covers ELF headers) r-x ----
        U32(1);                               // p_type = PT_LOAD
        U32(5);                               // p_flags = R|X
        U64(0);                               // p_offset (map from file start)
        U64(baseVaddr);                       // p_vaddr
        U64(baseVaddr);                       // p_paddr
        U64(afterCode);                       // p_filesz (headers + code)
        U64(afterCode);                       // p_memsz
        U64(0x1000);                          // p_align

        // ---- program header 2: data rw- ----
        // p_memsz may exceed p_filesz; the kernel zero-fills the difference,
        // which is our heap region.
        long dataMem = dataMemSize < 0 ? data.Length : dataMemSize;
        U32(1);                               // PT_LOAD
        U32(6);                               // R|W
        U64(dataFileOff);                     // p_offset
        U64(dataVaddr);                       // p_vaddr
        U64(dataVaddr);                       // p_paddr
        U64(data.Length);                     // p_filesz
        U64(dataMem);                         // p_memsz (content + heap)
        U64(0x1000);                          // p_align

        // ---- program header 3: .dynamic (dynamic programs) or unused ----
        if (dynOff >= 0) Seg(2, 6, dataFileOff + dynOff, dataVaddr + dynOff, dynLen, dynLen, 8);
        else Seg(0, 0, 0, 0, 0, 0, 0);

        // ---- code ----
        buf.AddRange(code);

        // ---- pad to data file offset ----
        while (buf.Count < dataFileOff) U8(0);

        // ---- data ----
        buf.AddRange(data);

        if (debug != null) AddSections(buf, codeVaddr, code.Length, codeFileOff, dataVaddr, data.Length, dataFileOff, debug, symbols ?? new());
        return buf.ToArray();
    }

    // Appends the non-loaded sections and a section header table, then points the ELF
    // header at it. Sections: null, .text, .data, the debug sections, .symtab, .strtab, .shstrtab.
    static void AddSections(List<byte> buf, long codeVaddr, int codeLen, int codeOff, long dataVaddr, int dataLen, int dataOff,
                            List<(string Name, byte[] Bytes)> debug, List<(string Name, long Addr, long Size)> symbols) {
        var shstr = new ByteWriter(); shstr.U8(0);
        int Name(string n) { int at = shstr.Count; shstr.Str(n); return at; }
        var headers = new List<(int Name, int Type, long Flags, long Addr, long Off, long Size, int Link, int Info, long Align, long EntSize)>();
        headers.Add((0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
        headers.Add((Name(".text"), 1, 6, codeVaddr, codeOff, codeLen, 0, 0, 16, 0));          // PROGBITS, ALLOC|EXEC
        headers.Add((Name(".data"), 1, 3, dataVaddr, dataOff, dataLen, 0, 0, 16, 0));          // PROGBITS, ALLOC|WRITE
        foreach (var (n, bytes) in debug) {
            int off = buf.Count; buf.AddRange(bytes);
            headers.Add((Name(n), 1, 0, 0, off, bytes.Length, 0, 0, 1, 0));
        }
        // .symtab: a null symbol, then one global FUNC symbol per routine, all in .text (section 1)
        var strtab = new ByteWriter(); strtab.U8(0);
        var sym = new ByteWriter();
        for (int i = 0; i < 24; i++) sym.U8(0);
        foreach (var (n, addr, size) in symbols) {
            int nameAt = strtab.Count; strtab.Str(n);
            sym.U32(nameAt); sym.U8(0x12); sym.U8(0); sym.U16(1); sym.U64(addr); sym.U64(size);   // GLOBAL FUNC, section 1
        }
        int symtabIndex = headers.Count, strtabIndex = symtabIndex + 1;
        while (buf.Count % 8 != 0) buf.Add(0);
        int symOff = buf.Count; buf.AddRange(sym.ToArray());
        headers.Add((Name(".symtab"), 2, 0, 0, symOff, sym.Count, strtabIndex, 1, 8, 24));     // info = first global
        int strOff = buf.Count; buf.AddRange(strtab.ToArray());
        headers.Add((Name(".strtab"), 3, 0, 0, strOff, strtab.Count, 0, 0, 1, 0));
        int shstrIndex = headers.Count;
        int shstrNameAt = Name(".shstrtab");
        int shstrOff = buf.Count; buf.AddRange(shstr.ToArray());
        headers.Add((shstrNameAt, 3, 0, 0, shstrOff, shstr.Count, 0, 0, 1, 0));

        while (buf.Count % 8 != 0) buf.Add(0);
        long shoff = buf.Count;
        var sh = new ByteWriter();
        foreach (var h in headers) {
            sh.U32(h.Name); sh.U32(h.Type); sh.U64(h.Flags); sh.U64(h.Addr); sh.U64(h.Off); sh.U64(h.Size);
            sh.U32(h.Link); sh.U32(h.Info); sh.U64(h.Align); sh.U64(h.EntSize);
        }
        buf.AddRange(sh.ToArray());
        void Put(int at, long v, int n) { for (int i = 0; i < n; i++) buf[at + i] = (byte)(v >> (8 * i)); }
        Put(0x28, shoff, 8);                 // e_shoff
        Put(0x3a, 64, 2);                    // e_shentsize
        Put(0x3c, headers.Count, 2);         // e_shnum
        Put(0x3e, shstrIndex, 2);            // e_shstrndx
    }

    static long CodeGenBase(long codeVaddr) => codeVaddr;
}

static class Program {
    // Exit codes: 0 ok, 1 errors in the program, 2 bad command line, 3 internal compiler error.
    const int ExitUsage = 2, ExitInternal = 3;
    const int CompilerStackBytes = 256 * 1024 * 1024;   // deep nesting must not overflow the 1 MB default
    static string? _currentFile;

    static int Main(string[] args) {
        // Run on a thread with a big stack, and turn any unexpected exception into a
        // one-line "internal compiler error" instead of a .NET stack trace.
        int rc = ExitInternal;
        var t = new System.Threading.Thread(() => {
            try { rc = RealMain(args); }
            catch (Exception ex) { rc = InternalError(ex); }
        }, CompilerStackBytes);
        t.Start();
        t.Join();
        return rc;
    }

    static int InternalError(Exception ex) {
        Console.Out.Flush();
        string where = _currentFile != null ? $"{_currentFile}: " : "";
        Console.Error.WriteLine($"{where}internal compiler error: {ex.GetType().Name}: {ex.Message}");
        if (Environment.GetEnvironmentVariable("FORGE_DEBUG") == "1") Console.Error.WriteLine(ex);
        else Console.Error.WriteLine("This is a bug in forgec, not in your program. Please report it with the source file at");
        Console.Error.WriteLine("https://github.com/Tajbiul-Rawol/Forge/issues  (set FORGE_DEBUG=1 for a stack trace)");
        return ExitInternal;
    }

    static int Usage(string? problem = null) {
        if (problem != null) Console.Error.WriteLine($"forgec: {problem}");
        Console.Error.WriteLine("usage:");
        Console.Error.WriteLine("  forgec <build|run> <file.fg> [-o out] [--target win|linux|kernel] [-O0] [-g] [--no-bounds-check] [--interp /lib/ld-musl-x86_64.so.1]");
        Console.Error.WriteLine("  forgec check <file.fg>                  # lint: errors + warnings, no binary");
        Console.Error.WriteLine("  forgec complete <file.fg> <line> <col>  # autocomplete candidates at a position");
        return ExitUsage;
    }

    // Print collected errors as  file:line: error: message  plus a count line.
    static void PrintErrors(TextWriter w, string path, IEnumerable<Diagnostic> errors) {
        int n = 0;
        foreach (var e in errors) { w.WriteLine($"{Sources.Loc(e.Line)}: error: {e.Message}"); n++; }
        string more = n >= CodeGen.MaxErrors ? " (stopped after the first " + CodeGen.MaxErrors + ")" : "";
        w.WriteLine($"{path}: {n} error{(n == 1 ? "" : "s")}{more}.");
    }

    static int RealMain(string[] args) {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help") return Usage();
        if (args[0] == "check") return args.Length == 2 ? RunCheck(args[1]) : Usage("check takes exactly one file");
        if (args[0] == "complete") return RunComplete(args);
        if (args[0] != "build" && args[0] != "run") return Usage($"unknown command '{args[0]}'");
        if (args.Length < 2) return Usage($"{args[0]}: missing source file");
        string cmd = args[0], path = args[1];
        string outPath = Path.GetFileNameWithoutExtension(path);
        Target target = Target.Windows;   // Windows first, per the brief
        bool optimize = true, bounds = true, debugInfo = false;
        string? interp = null;
        for (int i = 2; i < args.Length; i++) {
            string a = args[i];
            bool needsValue = a is "-o" or "--interp" or "--target";
            if (needsValue && i + 1 >= args.Length) return Usage($"option '{a}' needs a value");
            if (a == "-o") outPath = args[++i];
            else if (a == "-O0") optimize = false;      // disable the optimizer (debugging / comparison)
            else if (a == "--no-bounds-check") bounds = false;   // unchecked array indexing
            else if (a == "-g") debugInfo = true;                // DWARF (Linux) / PDB (Windows) debug info
            else if (a == "--interp") interp = args[++i];   // Linux dynamic loader (externs)
            else if (a == "--target") {
                string t = args[++i];
                Target? parsed = t is "win" or "windows" ? Target.Windows
                       : t is "linux" or "elf" ? Target.Linux
                       : t is "kernel" or "bare" ? Target.Kernel
                       : null;
                if (parsed == null) return Usage($"unknown target '{t}' (expected win, linux or kernel)");
                target = parsed.Value;
            }
            else return Usage($"unknown option '{a}'");
        }
        if (debugInfo && target == Target.Kernel) return Usage("-g is not supported for --target kernel yet");
        _currentFile = path;
        if (target == Target.Windows && !outPath.EndsWith(".exe")) outPath += ".exe";
        if (target == Target.Kernel && !outPath.EndsWith(".elf")) outPath += ".elf";

        string src;
        try { src = File.ReadAllText(path); }
        catch (Exception ex) { Console.Error.WriteLine($"cannot read {path}: {ex.Message}"); return 1; }

        try {
            var mod = Sources.Load(path, src);        // the file and everything it imports (Imports.cs)
            var cg = new CodeGen(mod, target) { OptimizeEnabled = optimize, BoundsChecks = bounds, DebugInfo = debugInfo };
            string pdbPath = Path.ChangeExtension(outPath, ".pdb");
            if (debugInfo && target == Target.Windows) cg.PdbPath = Path.GetFullPath(pdbPath);
            if (interp != null) cg.LinuxInterp = interp;
            var image = cg.LowerToFile();
            try {
                File.WriteAllBytes(outPath, image);
                if (cg.PdbBytes != null) File.WriteAllBytes(pdbPath, cg.PdbBytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                Console.Error.WriteLine($"forgec: cannot write {outPath}: {ex.Message}");
                return 1;
            }
            foreach (var w in cg.Warnings)
                Console.Error.WriteLine($"{Sources.Loc(w.Line)}: warning: {w.Message}");

            if (target == Target.Linux && !OperatingSystem.IsWindows()) {
                // mark executable (Windows has no exec bit; run `chmod +x` on the Linux side)
                File.SetUnixFileMode(outPath, File.GetUnixFileMode(outPath)
                    | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }

            string kind = target == Target.Windows ? "PE32+ x86-64 (.exe)"
                        : target == Target.Kernel ? "Multiboot ELF32 kernel"
                        : "ELF64 x86-64";
            Console.WriteLine($"forgec: wrote {outPath} ({image.Length} bytes, {kind})");

            if (cmd == "run") {
                Console.WriteLine("forgec: running\n--------------------------------------------");
                if (target == Target.Kernel) {
                    // boot the kernel in QEMU; serial -> our stdout
                    var qpsi = new System.Diagnostics.ProcessStartInfo("qemu-system-x86_64",
                        $"-kernel \"{Path.GetFullPath(outPath)}\" -nographic -serial mon:stdio -no-reboot");
                    qpsi.UseShellExecute = false;
                    try {
                        var q = System.Diagnostics.Process.Start(qpsi)!;
                        q.WaitForExit();
                        return q.ExitCode;
                    } catch (Exception ex) {
                        Console.Error.WriteLine($"forgec: could not launch QEMU: {ex.Message}");
                        Console.Error.WriteLine("forgec: the kernel built fine; boot it with:");
                        Console.Error.WriteLine($"  qemu-system-x86_64 -kernel {outPath} -nographic -serial mon:stdio");
                        return 1;
                    }
                }
                string full = Path.GetFullPath(outPath);
                bool hostIsWindows = System.Runtime.InteropServices.RuntimeInformation
                    .IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);
                // A Windows .exe runs natively on a Windows host; only fall back to
                // Wine when we're on a non-Windows host trying to run a Windows target.
                System.Diagnostics.ProcessStartInfo psi =
                    (target == Target.Windows && !hostIsWindows)
                        ? new System.Diagnostics.ProcessStartInfo("wine", $"\"{full}\"")
                        : new System.Diagnostics.ProcessStartInfo(full);
                psi.UseShellExecute = false;
                try {
                    var run = System.Diagnostics.Process.Start(psi)!;
                    run.WaitForExit();
                    return run.ExitCode;
                } catch (Exception ex) {
                    Console.Error.WriteLine($"forgec: could not launch {outPath}: {ex.Message}");
                    Console.Error.WriteLine($"forgec: the file was built fine - run it directly with:  .\\{Path.GetFileName(outPath)}");
                    return 1;
                }
            }
            return 0;
        } catch (ForgeException fe) {
            Console.Error.WriteLine($"{Sources.Loc(fe.Line)}: error: {fe.Message}");
            return 1;
        } catch (ForgeErrors fes) {
            PrintErrors(Console.Error, path, fes.Errors);
            return 1;
        }
    }

    // ---- `forgec check <file>` : lint without producing a binary ----
    static int RunCheck(string path) {
        string src;
        try { src = File.ReadAllText(path); }
        catch (Exception ex) { Console.Error.WriteLine($"cannot read {path}: {ex.Message}"); return 1; }
        _currentFile = path;
        try {
            var mod = Sources.Load(path, src);
            var cg = new CodeGen(mod, Target.Windows);
            cg.Analyze();                       // type-check + analyses, no codegen
            int warns = cg.Warnings.Count;
            foreach (var w in cg.Warnings)
                Console.WriteLine($"{Sources.Loc(w.Line)}: warning: {w.Message}");
            if (warns == 0) Console.WriteLine($"{path}: no problems found.");
            else Console.WriteLine($"{path}: {warns} warning(s), 0 error(s).");
            return 0;
        } catch (ForgeException fe) {
            Console.WriteLine($"{Sources.Loc(fe.Line)}: error: {fe.Message}");
            Console.WriteLine($"{path}: 1 error.");
            return 1;
        } catch (ForgeErrors fes) {
            PrintErrors(Console.Out, path, fes.Errors);
            return 1;
        }
    }

    // ---- `forgec complete <file> <line> <col>` : autocomplete candidates ----
    static int RunComplete(string[] args) {
        if (args.Length < 4) { Console.Error.WriteLine("usage: forgec complete <file.fg> <line> <col>"); return 2; }
        string path = args[1];
        if (!int.TryParse(args[2], out int line) || !int.TryParse(args[3], out int col)) {
            Console.Error.WriteLine("line and col must be integers (1-based)"); return 2;
        }
        string src;
        try { src = File.ReadAllText(path); }
        catch (Exception ex) { Console.Error.WriteLine($"cannot read {path}: {ex.Message}"); return 1; }

        var lines = src.Replace("\r\n", "\n").Split('\n');
        string lineText = (line >= 1 && line <= lines.Length) ? lines[line - 1] : "";
        // partial identifier immediately before the cursor
        int c = Math.Min(col - 1, lineText.Length);
        int start = c;
        while (start > 0 && (char.IsLetterOrDigit(lineText[start - 1]) || lineText[start - 1] == '_')) start--;
        string prefix = lineText.Substring(start, c - start);

        // candidate symbols: keywords + types + function names + locals of the enclosing function
        var candidates = new List<string> {
            "if","else","while","return","var","new","try","throw","true","false","module","throws",
            "for","spawn","async","await","Task","Task.Yield","Task.WhenAll",
            "int","uint","ushort","long","bool","string","void",
            "Console.WriteLine",
        };
        try {
            // Parse a copy with the half-typed identifier at the cursor removed,
            // so an in-progress line doesn't break the parse and hide symbols.
            var editable = (string[])lines.Clone();
            if (line >= 1 && line <= editable.Length) {
                var lt = editable[line - 1];
                int cc = Math.Min(col - 1, lt.Length);
                int st = cc;
                while (st > 0 && (char.IsLetterOrDigit(lt[st - 1]) || lt[st - 1] == '_')) st--;
                editable[line - 1] = lt.Substring(0, st) + lt.Substring(cc);
            }
            var stripped = string.Join("\n", editable);
            var mod = Sources.Load(path, stripped);   // imported files' functions are candidates too
            foreach (var f in mod.Funcs) candidates.Add(f.Name);
            // enclosing function = greatest start line <= cursor line, in this file (file 0)
            Func? enc = null;
            foreach (var f in mod.Funcs) if (Sources.FileOf(f.Line) == 0 && Sources.LineOf(f.Line) <= line && (enc == null || f.Line > enc.Line)) enc = f;
            if (enc != null) {
                foreach (var (_, n) in enc.Params) candidates.Add(n);
                CollectLocalsBefore(enc.Body, line, candidates);
            }
        } catch (Exception e) when (e is ForgeException or ForgeErrors) {
            // file may still be mid-edit / invalid; offer keywords, types, and visible names
        }

        foreach (var s in Suggest.Complete(prefix, candidates)) Console.WriteLine(s);
        return 0;
    }

    static void CollectLocalsBefore(System.Collections.Generic.List<Stmt> body, int line, System.Collections.Generic.List<string> outNames) {
        foreach (var s in body) {
            switch (s) {
                case VarDecl v when Sources.LineOf(v.Line) < line: outNames.Add(v.Name); break;
                case IfStmt i: CollectLocalsBefore(i.Then, line, outNames); if (i.Else != null) CollectLocalsBefore(i.Else, line, outNames); break;
                case WhileStmt w: CollectLocalsBefore(w.Body, line, outNames); break;
            }
        }
    }
}
