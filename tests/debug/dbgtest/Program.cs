// dbgtest <program.exe> <script>
//
// Runs a Forge program under the Win32 debug API and drives it from a script, reading the
// program's PDB through dbghelp.dll (the symbol engine of WinDbg). Script commands:
//
//   break <file>:<line> | break <function>   one-shot breakpoint (set before 'run')
//   run | continue                           run to the next breakpoint (or the end)
//   bt                                       backtrace (StackWalk64: uses the .exe's unwind info)
//   locals                                   parameters and locals of the current function
//   print <name>                             one variable, struct fields expanded
//   next                                     step to the next line, over calls
//   finish                                   run until the current function returns
//
// Output is stable text (no addresses), compared with tests/expected/<name>.win.out.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

static unsafe class DbgTest {
    // ---- kernel32
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcessW(string? app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags,
                                      IntPtr env, string? dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool WaitForDebugEvent(byte* ev, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool ContinueDebugEvent(uint pid, uint tid, uint status);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadProcessMemory(IntPtr h, ulong addr, byte* buf, nint n, out nint read);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteProcessMemory(IntPtr h, ulong addr, byte* buf, nint n, out nint written);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool FlushInstructionCache(IntPtr h, ulong addr, nint n);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenThread(uint access, bool inherit, uint tid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetThreadContext(IntPtr t, byte* ctx);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetThreadContext(IntPtr t, byte* ctx);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr mod, string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr LoadLibraryW(string name);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO { public int cb; public IntPtr r, desktop, title; public int x, y, w, h, xc, yc, fill, flags; public short show, r2; public IntPtr r3, hIn, hOut, hErr; }
    [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public uint pid, tid; }

    // ---- dbghelp
    [DllImport("dbghelp.dll")] static extern uint SymSetOptions(uint o);
    [DllImport("dbghelp.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SymInitializeW(IntPtr h, string? path, bool invade);
    [DllImport("dbghelp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern ulong SymLoadModuleExW(IntPtr h, IntPtr file, string image, string? module, ulong baseOfDll, uint size, IntPtr data, uint flags);
    [DllImport("dbghelp.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SymGetModuleInfoW64(IntPtr h, ulong addr, byte* info);
    [DllImport("dbghelp.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SymFromNameW(IntPtr h, string name, byte* sym);
    [DllImport("dbghelp.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SymFromAddrW(IntPtr h, ulong addr, out ulong disp, byte* sym);
    [DllImport("dbghelp.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SymGetLineFromAddrW64(IntPtr h, ulong addr, out uint disp, byte* line);
    [DllImport("dbghelp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SymGetLineFromNameW64(IntPtr h, string? module, string file, uint line, out int disp, byte* outLine);
    [DllImport("dbghelp.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SymGetLineNextW64(IntPtr h, byte* line);
    [DllImport("dbghelp.dll", SetLastError = true)] static extern ulong SymSetScopeFromAddr(IntPtr h, ulong addr);
    delegate bool EnumCb(byte* sym, uint size, IntPtr ctx);
    [DllImport("dbghelp.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SymEnumSymbolsW(IntPtr h, ulong baseOfDll, string? mask, EnumCb cb, IntPtr ctx);
    [DllImport("dbghelp.dll", SetLastError = true)] static extern bool SymGetTypeInfo(IntPtr h, ulong modBase, uint typeId, int what, void* info);
    [DllImport("dbghelp.dll", SetLastError = true)]
    static extern bool StackWalk64(uint machine, IntPtr h, IntPtr thread, byte* frame, byte* ctx, IntPtr read, IntPtr fta, IntPtr gmb, IntPtr tr);

    const int TI_GET_SYMTAG = 0, TI_GET_SYMNAME = 1, TI_GET_LENGTH = 2, TI_GET_TYPE = 3, TI_GET_TYPEID = 4, TI_GET_BASETYPE = 5,
              TI_FINDCHILDREN = 7, TI_GET_OFFSET = 10, TI_GET_CHILDRENCOUNT = 13;
    const int SymTagUDT = 11, SymTagPointerType = 14, SymTagBaseType = 16;

    static IntPtr _proc;
    static ulong _modBase;
    static readonly Dictionary<ulong, byte> _bps = new();          // address -> original byte
    static readonly StringBuilder _out = new();

    static int Main(string[] args) {
        if (args.Length != 2) { Console.Error.WriteLine("usage: dbgtest <program.exe> <script>"); return 2; }
        string exe = Path.GetFullPath(args[0]);
        var script = File.ReadAllLines(args[1]).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#")).ToList();
        int pc = 0;

        var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
        if (!CreateProcessW(exe, new StringBuilder($"\"{exe}\""), IntPtr.Zero, IntPtr.Zero, true, 0x2 /* DEBUG_ONLY_THIS_PROCESS */,
                            IntPtr.Zero, Path.GetDirectoryName(exe), ref si, out var pi)) {
            Console.Error.WriteLine($"cannot start {exe}: error {Marshal.GetLastWin32Error()}"); return 1;
        }
        _proc = pi.hProcess;
        var ev = stackalloc byte[256];
        var ctxBuf = new byte[1232 + 16];
        bool started = false;
        // stepping state
        string mode = "run";                 // run | next | finish
        string stepFunc = ""; uint stepLine = 0; ulong stepRsp = 0;
        ulong overBp = 0;                    // a temporary breakpoint at a return address (stepping over a call)

        while (true) {
            if (!WaitForDebugEvent(ev, 0xffffffff)) { Console.Error.WriteLine("WaitForDebugEvent failed"); return 1; }
            uint code = *(uint*)ev, pid = *(uint*)(ev + 4), tid = *(uint*)(ev + 8);
            uint status = 0x00010002;        // DBG_CONTINUE
            if (code == 3) {                 // CREATE_PROCESS_DEBUG_EVENT
                IntPtr hFile = *(IntPtr*)(ev + 16);
                ulong baseAddr = *(ulong*)(ev + 16 + 24);
                SymSetOptions(0x10 /* LOAD_LINES */ | 0x2 /* UNDNAME */);
                if (!SymInitializeW(_proc, Path.GetDirectoryName(exe), false)) return Fail("SymInitialize");
                _modBase = SymLoadModuleExW(_proc, IntPtr.Zero, exe, null, baseAddr, 0, IntPtr.Zero, 0);
                if (_modBase == 0) return Fail("SymLoadModuleEx");
                var mi = (byte*)Marshal.AllocHGlobal(0x1000); new Span<byte>(mi, 0x1000).Clear(); *(uint*)mi = 0xcc0;     // IMAGEHLP_MODULEW64
                if (SymGetModuleInfoW64(_proc, _modBase, mi)) {
                    int symType = *(int*)(mi + 32);                        // SYM_TYPE: 3 = SymPdb
                    Say($"symbols: {(symType == 3 ? "pdb" : "type " + symType)}");
                } else Say($"SymGetModuleInfo failed: error {Marshal.GetLastWin32Error()}");
                if (hFile != IntPtr.Zero) CloseHandle(hFile);
                // breakpoints given before 'run'
                while (pc < script.Count && script[pc].StartsWith("break ")) SetBreak(script[pc++][6..].Trim());
                if (pc < script.Count && script[pc] == "run") pc++;
                started = true;
            } else if (code == 6) {          // LOAD_DLL_DEBUG_EVENT
                IntPtr hFile = *(IntPtr*)(ev + 16);
                if (hFile != IntPtr.Zero) CloseHandle(hFile);
            } else if (code == 5) {          // EXIT_PROCESS_DEBUG_EVENT
                Say($"exited with code {*(uint*)(ev + 16)}");
                Flush();
                return 0;
            } else if (code == 1 && started) {  // EXCEPTION_DEBUG_EVENT
                uint exc = *(uint*)(ev + 16);
                ulong at = *(ulong*)(ev + 16 + 16);
                IntPtr th = OpenThread(0x1fffff, false, tid);
                fixed (byte* raw = ctxBuf) {
                    byte* ctx = (byte*)(((ulong)raw + 15) & ~15UL);
                    *(uint*)(ctx + 0x30) = 0x10000b;                        // CONTEXT_FULL
                    GetThreadContext(th, ctx);
                    bool stop = false;
                    if (exc == 0x80000003 && _bps.ContainsKey(at)) {         // one of our breakpoints
                        Unset(at);
                        *(ulong*)(ctx + 0xf8) = at;                          // back to the original instruction
                        if (at == overBp) { overBp = 0; Step(ctx); }        // returned from a call we stepped over
                        else stop = mode == "run" || mode == "finish";
                        if (mode == "finish" && stop) Say($"returned {(long)*(ulong*)(ctx + 0x78)}");
                    } else if (exc == 0x80000004 && mode == "next") {        // single step
                        ulong rip = *(ulong*)(ctx + 0xf8);
                        string fn = FuncAt(rip);
                        if (fn == stepFunc) {
                            if (LineStart(rip, out uint ln) && ln != stepLine) stop = true;
                            else Step(ctx);
                        } else if (*(ulong*)(ctx + 0x98) < stepRsp) {          // entered a call: run to its return
                            ulong ret; ReadMem(*(ulong*)(ctx + 0x98), (byte*)&ret, 8);
                            overBp = ret; SetBreakAt(ret);
                        } else if (LineStart(rip, out _)) stop = true;      // returned to the caller
                        else Step(ctx);
                    } else if (exc == 0x80000003 || exc == 0x80000004) {
                        // the loader's initial breakpoint, or a step we no longer need
                    } else {
                        status = 0x80010001;                                 // DBG_EXCEPTION_NOT_HANDLED
                    }
                    if (stop) {
                        mode = "run";
                        Say($"stopped in {Where(*(ulong*)(ctx + 0xf8))}");
                        // run script commands until one resumes the program
                        while (pc < script.Count) {
                            string cmd = script[pc++];
                            Say($"> {cmd}");
                            if (cmd is "continue" or "run") break;
                            if (cmd == "next") {
                                ulong rip = *(ulong*)(ctx + 0xf8);
                                mode = "next"; stepFunc = FuncAt(rip); LineStart(rip, out stepLine, anyOffset: true);
                                stepRsp = *(ulong*)(ctx + 0x98);
                                Step(ctx); break;
                            }
                            if (cmd == "finish") {
                                var frames = Walk(th, ctx);
                                if (frames.Count < 2) { Say("cannot find the caller"); continue; }
                                mode = "finish"; SetBreakAt(frames[1]); break;
                            }
                            if (cmd == "bt") { int i = 0; foreach (var f in Walk(th, ctx)) Say($"#{i++} {Where(f)}"); continue; }
                            if (cmd == "locals") { foreach (var (n, v) in Locals(ctx)) Say($"{n} = {v}"); continue; }
                            if (cmd.StartsWith("print ")) {
                                string want = cmd[6..].Trim();
                                var hit = Locals(ctx).Where(x => x.Name == want).ToList();
                                Say(hit.Count > 0 ? $"{want} = {hit[0].Value}" : $"no variable '{want}'");
                                continue;
                            }
                            if (cmd.StartsWith("break ")) { SetBreak(cmd[6..].Trim()); continue; }
                            Say($"unknown command '{cmd}'");
                        }
                    }
                    SetThreadContext(th, ctx);
                }
                CloseHandle(th);
            }
            Flush();
            ContinueDebugEvent(pid, tid, status);
        }
    }

    static int Fail(string what) { Console.Error.WriteLine($"{what} failed: error {Marshal.GetLastWin32Error()}"); return 1; }
    static void Say(string s) => _out.AppendLine(s);
    static void Flush() { Console.Out.Write(_out.ToString()); Console.Out.Flush(); _out.Clear(); }
    static void Step(byte* ctx) => *(uint*)(ctx + 0x44) |= 0x100;          // trap flag

    static void ReadMem(ulong addr, byte* buf, int n) { ReadProcessMemory(_proc, addr, buf, n, out _); }
    static void SetBreakAt(ulong addr) {
        if (_bps.ContainsKey(addr)) return;
        byte orig, cc = 0xcc;
        ReadMem(addr, &orig, 1);
        WriteProcessMemory(_proc, addr, &cc, 1, out _);
        FlushInstructionCache(_proc, addr, 1);
        _bps[addr] = orig;
    }
    static void Unset(ulong addr) {
        byte orig = _bps[addr];
        WriteProcessMemory(_proc, addr, &orig, 1, out _);
        FlushInstructionCache(_proc, addr, 1);
        _bps.Remove(addr);
    }
    static void SetBreak(string where) {
        int colon = where.LastIndexOf(':');
        if (colon > 0 && uint.TryParse(where[(colon + 1)..], out uint line)) {
            var l = stackalloc byte[40]; *(uint*)l = 40;
            if (!SymGetLineFromNameW64(_proc, null, where[..colon], line, out _, l)) { Say($"no code at {where}"); return; }
            SetBreakAt(*(ulong*)(l + 32));
        } else {
            var s = SymBuf();
            if (!SymFromNameW(_proc, where, s)) { Say($"no function '{where}'"); return; }
            ulong addr = *(ulong*)(s + 56);
            // like a debugger: stop after the prologue, at the next line-table row of the function
            var l = stackalloc byte[40]; *(uint*)l = 40;
            ulong size = *(uint*)(s + 28);
            if (SymGetLineFromAddrW64(_proc, addr, out _, l) && SymGetLineNextW64(_proc, l)) {
                ulong next = *(ulong*)(l + 32);
                if (next > addr && (size == 0 || next < addr + size)) addr = next;
            }
            SetBreakAt(addr);
        }
        Say($"breakpoint at {where}");
    }

    static byte* SymBuf() {
        var s = (byte*)Marshal.AllocHGlobal(88 + 2 * 256);
        new Span<byte>(s, 88 + 2 * 256).Clear();
        *(uint*)s = 88; *(uint*)(s + 80) = 256;                             // SizeOfStruct, MaxNameLen
        return s;
    }
    static string SymName(byte* s) => new string((char*)(s + 84), 0, (int)*(uint*)(s + 76)).TrimEnd((char)0);
    static string FuncAt(ulong addr) { var s = SymBuf(); return SymFromAddrW(_proc, addr, out _, s) ? SymName(s) : "?"; }
    // the line whose code starts exactly here (or, anyOffset, the line containing addr)
    static bool LineStart(ulong addr, out uint line, bool anyOffset = false) {
        var l = stackalloc byte[40]; *(uint*)l = 40;
        line = 0;
        if (!SymGetLineFromAddrW64(_proc, addr, out uint d, l) || (d != 0 && !anyOffset)) return false;
        line = *(uint*)(l + 16);
        return true;
    }
    static string Where(ulong addr) {
        string fn = FuncAt(addr);
        var l = stackalloc byte[40]; *(uint*)l = 40;
        if (!SymGetLineFromAddrW64(_proc, addr, out _, l)) return fn;
        string file = Path.GetFileName(new string(*(char**)(l + 24)));
        return $"{fn} at {file}:{*(uint*)(l + 16)}";
    }

    // return addresses of the stack, innermost first, while they are inside the program's .text
    static List<ulong> Walk(IntPtr thread, byte* ctx) {
        var frames = new List<ulong>();
        var copy = stackalloc byte[1232 + 16];
        byte* c = (byte*)(((ulong)copy + 15) & ~15UL);
        Buffer.MemoryCopy(ctx, c, 1232, 1232);
        var sf = stackalloc byte[1024];
        new Span<byte>(sf, 1024).Clear();
        *(ulong*)sf = *(ulong*)(c + 0xf8); *(uint*)(sf + 12) = 3;            // AddrPC (flat)
        *(ulong*)(sf + 32) = *(ulong*)(c + 0xa0); *(uint*)(sf + 44) = 3;     // AddrFrame
        *(ulong*)(sf + 48) = *(ulong*)(c + 0x98); *(uint*)(sf + 60) = 3;     // AddrStack
        IntPtr dbghelp = LoadLibraryW("dbghelp.dll");
        IntPtr fta = GetProcAddress(dbghelp, "SymFunctionTableAccess64"), gmb = GetProcAddress(dbghelp, "SymGetModuleBase64");
        for (int i = 0; i < 16 && StackWalk64(0x8664, _proc, thread, sf, c, IntPtr.Zero, fta, gmb, IntPtr.Zero); i++) {
            ulong pcAddr = *(ulong*)sf;
            if (pcAddr == 0 || FuncAt(pcAddr) == "?") break;
            frames.Add(pcAddr);
            if (FuncAt(pcAddr) == "_start") break;
        }
        return frames;
    }

    // ---- variables
    static List<(string Name, string Value)> Locals(byte* ctx) {
        var res = new List<(string, string)>();
        ulong rip = *(ulong*)(ctx + 0xf8), rbp = *(ulong*)(ctx + 0xa0), rsp = *(ulong*)(ctx + 0x98);
        SymSetScopeFromAddr(_proc, rip);
        SymEnumSymbolsW(_proc, 0, null, (s, size, _) => {
            string name = SymName(s);
            uint flags = *(uint*)(s + 40), type = *(uint*)(s + 4), reg = *(uint*)(s + 64);
            if ((flags & 0x10) == 0) { res.Add((name, "<optimized out>")); return true; }   // not SYMFLAG_REGREL
            ulong bas = reg == 334 ? rbp : reg == 335 ? rsp : 0;
            res.Add((name, Format(type, bas + *(ulong*)(s + 56), 0)));
            return true;
        }, IntPtr.Zero);
        return res;
    }

    static uint Info32(uint type, int what) { uint v = 0; SymGetTypeInfo(_proc, _modBase, type, what, &v); return v; }
    static string Format(uint type, ulong addr, int depth) {
        uint tag = Info32(type, TI_GET_SYMTAG);
        ulong len = 0; SymGetTypeInfo(_proc, _modBase, type, TI_GET_LENGTH, &len);
        if (tag == SymTagBaseType) {
            ulong raw = 0; ReadMem(addr, (byte*)&raw, (int)Math.Min(len, 8));
            uint bt = Info32(type, TI_GET_BASETYPE);
            return bt switch {
                8 => BitConverter.Int64BitsToDouble((long)raw).ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                10 => raw != 0 ? "true" : "false",
                2 => $"'{(char)raw}'",
                7 or 14 => raw.ToString(),
                _ => len switch { 1 => ((sbyte)raw).ToString(), 2 => ((short)raw).ToString(), 4 => ((int)raw).ToString(), _ => ((long)raw).ToString() },
            };
        }
        if (tag == SymTagPointerType) {
            ulong p = 0; ReadMem(addr, (byte*)&p, 8);
            if (p == 0) return "null";
            uint to = Info32(type, TI_GET_TYPE);
            uint toTag = Info32(to, TI_GET_SYMTAG);
            if (toTag == SymTagBaseType && Info32(to, TI_GET_BASETYPE) == 2) {      // char*: a string
                var sb = new StringBuilder();
                for (int i = 0; i < 200; i++) { byte ch; ReadMem(p + (ulong)i, &ch, 1); if (ch == 0) break; sb.Append((char)ch); }
                return $"\"{sb}\"";
            }
            if (depth > 0) return "{...}";
            return toTag == SymTagUDT ? Format(to, p, depth + 1) : "-> " + Format(to, p, depth + 1);
        }
        if (tag == SymTagUDT) {
            uint n = Info32(type, TI_GET_CHILDRENCOUNT);
            var buf = stackalloc uint[(int)n + 2];
            buf[0] = n; buf[1] = 0;
            SymGetTypeInfo(_proc, _modBase, type, TI_FINDCHILDREN, buf);
            var parts = new List<string>();
            for (int i = 0; i < n; i++) {
                uint child = buf[2 + i];
                IntPtr namePtr = IntPtr.Zero; SymGetTypeInfo(_proc, _modBase, child, TI_GET_SYMNAME, &namePtr);
                string fname = namePtr != IntPtr.Zero ? Marshal.PtrToStringUni(namePtr)! : "?";
                if (namePtr != IntPtr.Zero) LocalFree(namePtr);
                uint off = Info32(child, TI_GET_OFFSET);
                parts.Add($"{fname} = {Format(Info32(child, TI_GET_TYPEID), addr + off, depth + 1)}");
            }
            return "{" + string.Join(", ", parts) + "}";
        }
        return $"<type tag {tag}>";
    }
}
