using System;

namespace ForgeC;

// OS threads for the Windows and Linux targets: spawn/join, per-thread heap
// arenas, atomics, an OS-backed mutex (SRWLOCK / futex), cpu_count, sleep_ms and now_ns.
//
// Memory model. Every thread allocates from its own arena, which it finds
// through thread-local storage (a TEB TLS slot on Windows, the gs base on Linux (fs stays free for libc)),
// so malloc / strings never contend and heapreset() frees only the calling
// thread's memory. An arena starts with one chunk and grows by chunks from the
// OS (VirtualAlloc / mmap) when full. The main thread's first chunk is the static
// heap in .data. A spawned thread's memory stays valid until it is joined
// (join frees it, or zeroes it and keeps the region for the next spawn).
//
// Thread region: one OS allocation per spawned thread.
//   +0    control block  [0] OS handle (Windows) / tid word (Linux, cleared by the kernel at exit)
//                        [8] result   [16] region size   [24] next (while cached)
//                        [32] argument   [40] function address
//   +64   arena block    [0] cur  [8] end  [16] first chunk start  [24] first chunk end
//                        [32] extra-chunk list  [40] self pointer
//   +128 .. +ArenaChunk  the thread's first arena chunk
//   Linux only: +ArenaChunk .. +ArenaChunk+ThreadStack is the thread's stack.
// Extra chunks start with a 16-byte header: [0] next chunk, [8] chunk size.
//
// Region cache. join does not hand a region back to the OS right away: if the
// thread stayed inside its first chunk, join zeroes the part it used and pushes
// the region on a small cache (a list guarded by a spin lock), and the next
// spawn takes it from there. That skips a VirtualAlloc/VirtualFree (mmap/munmap)
// pair per thread, and new threads still start with zeroed memory.
sealed partial class CodeGen {
    const int RtTlsOff = 4096;           // heapBase+4096: Windows gs-relative offset of our TLS slot
    const int RtMainBlk = 4160;          // heapBase+4160: the main thread's arena block
    const int RtCacheLock = 4224;        // heapBase+4224: spin lock guarding the region cache
    const int RtCacheHead = 4232;        // heapBase+4232: first cached (joined, reusable) thread region
    const int RtCacheCount = 4240;       // heapBase+4240: number of cached regions
    const int RtCacheMax = 64;           // keep at most this many joined regions for reuse
    const int ArenaChunk = 4 << 20;      // first chunk of a thread, and the minimum growth chunk
    const int ThreadStack = 1 << 20;     // Linux thread stack (Windows uses the PE default, 1 MB)
    int ThreadRegion => _target == Target.Linux ? ArenaChunk + ThreadStack : ArenaChunk;
    bool Threaded => _target != Target.Kernel;

    // Register numbers for the Asm general forms.
    const int RAX = 0, RCX = 1, RDX = 2, RBX = 3, RSP = 4, RBP = 5, RSI = 6, RDI = 7,
              R8 = 8, R9 = 9, R10 = 10, R11 = 11, R12 = 12, R13 = 13, R14 = 14, R15 = 15;

    // r10 = this thread's arena block.
    void EmitLoadBlockR10() {
        if (_target == Target.Linux) {
            _a.Db(0x65, 0x4c, 0x8b, 0x14, 0x25, 0x28, 0, 0, 0);   // mov r10, gs:[40]  (block.self; fs is libc's)
        } else {
            _a.Db(0x49, 0xba); _slotRelocs.Add((_a.Pos, 3)); for (int i = 0; i < 8; i++) _a.Db(0); // mov r10, heapBase
            _a.Ld(R10, R10, RtTlsOff);                             // r10 = TLS slot offset in the TEB
            _a.Db(0x65); _a.Ld(R10, R10, 0);                       // mov r10, gs:[r10]
        }
    }

    // alloc(rdi = bytes) -> rax = rdi = 8-byte aligned pointer. Clobbers r10 and flags.
    void EmitAllocThreaded() {
        _a.Label("alloc");
        EmitLoadBlockR10();
        _a.AluRI(0, RDI, 7); _a.AluRI(4, RDI, -8);   // round the size up to 8
        _a.Ld(RAX, R10, 0);                           // rax = cur
        _a.AluRR(0x01, RAX, RDI);                     // rax = cur + size
        _a.Mem(0x3b, RAX, R10, 8);                    // cmp rax, end
        _a.Jcc(0x7, "alloc_slow");                    // ja: chunk full
        _a.St(R10, 0, RAX);                           // cur += size
        _a.AluRR(0x29, RAX, RDI);                     // rax = old cur
        _a.MovRR(RDI, RAX);
        _a.Ret();

        // alloc_slow(rdi = aligned size, r10 = block): get a new chunk from the OS.
        // Preserves everything except rax, rdi (= result) and flags.
        _a.Label("alloc_slow");
        _a.PushR(RBP); _a.MovRR(RBP, RSP);
        foreach (var r in new[] { RCX, RDX, RSI, RDI, R8, R9, R10, R11 }) _a.PushR(r); // rbp-8 .. rbp-64
        _a.AndRspImm8(-16);
        _a.Lea(RSI, RDI, 16 + 4095); _a.AluRI(4, RSI, -4096);   // rsi = round_up(size + header, 4 KB)
        _a.MovRI(RAX, ArenaChunk);
        _a.AluRR(0x39, RSI, RAX);                    // cmp rsi, ArenaChunk
        string big = NewLabel("as_big");
        _a.Jcc(0x3, big);                            // jae: keep the larger size
        _a.MovRR(RSI, RAX);
        _a.Label(big);
        EmitOsAlloc();                               // rax = chunk (rsi = size, preserved)
        _a.Ld(R10, RBP, -56); _a.Ld(RDI, RBP, -32);  // block, size
        _a.St(RAX, 8, RSI);                          // header.size
        _a.Ld(RCX, R10, 32); _a.St(RAX, 0, RCX);     // header.next = block.extra
        _a.St(R10, 32, RAX);                         // block.extra = chunk
        _a.MovRR(RCX, RAX); _a.AluRR(0x01, RCX, RSI); _a.St(R10, 8, RCX);  // end = chunk + size
        _a.Lea(RCX, RAX, 16);                        // rcx = result (past the header)
        _a.MovRR(RDX, RCX); _a.AluRR(0x01, RDX, RDI); _a.St(R10, 0, RDX);  // cur = result + size
        _a.MovRR(RAX, RCX);
        _a.Lea(RSP, RBP, -64);
        foreach (var r in new[] { R11, R10, R9, R8, RDI, RSI, RDX, RCX }) _a.PopR(r);
        _a.PopR(RBP);
        _a.MovRR(RDI, RAX);
        _a.Ret();
    }

    // reg = heapBase (the runtime's reserved area lives there)
    void EmitHeapBase(int reg) {
        _a.Db((byte)(reg >= 8 ? 0x49 : 0x48), (byte)(0xb8 + (reg & 7)));
        _slotRelocs.Add((_a.Pos, 3)); for (int i = 0; i < 8; i++) _a.Db(0);
    }
    // take / release the region-cache spin lock (hb = register holding heapBase). Clobbers rax.
    void EmitCacheLock(int hb) {
        string retry = NewLabel("cl_retry"), got = NewLabel("cl_got");
        _a.Label(retry);
        _a.MovRI(RAX, 1); _a.Mem(0x87, RAX, hb, RtCacheLock);   // xchg [hb+lock], rax
        _a.TestRR(RAX, RAX); _a.Jcc(0x4, got);
        _a.Db(0xf3, 0x90); _a.JmpRel(retry);                    // pause
        _a.Label(got);
    }
    void EmitCacheUnlock(int hb) => _a.StImm(hb, RtCacheLock, 0);

    // rax = fresh zeroed read/write memory of rsi bytes (rsi preserved). rsp must be 16-aligned.
    void EmitOsAlloc() {
        if (_target == Target.Windows) {
            _a.PushR(RSI); _a.PushR(RSI);            // keep rsi, stay 16-aligned
            _a.AluRI(5, RSP, 32);                    // shadow space
            _a.MovRI(RCX, 0); _a.MovRR(RDX, RSI);
            _a.MovRI(R8, 0x3000); _a.MovRI(R9, 4);   // MEM_COMMIT|MEM_RESERVE, PAGE_READWRITE
            EmitIatCall("VirtualAlloc");
            _a.AluRI(0, RSP, 32);
            _a.PopR(RSI); _a.PopR(RSI);
        } else {
            _a.MovRI(RDI, 0); _a.MovRI(RDX, 3);      // PROT_READ|PROT_WRITE
            _a.MovRI(R10, 0x22); _a.MovRI(R8, -1); _a.MovRI(R9, 0);   // MAP_PRIVATE|MAP_ANONYMOUS
            _a.MovRI(RAX, 9); _a.Syscall();          // mmap
        }
    }

    // release memory at rbx (Linux: size at [rbx+8] is NOT used here; caller passes rsi)
    void EmitOsFree(int ptrReg, int sizeReg) {
        if (_target == Target.Windows) {
            _a.AluRI(5, RSP, 32);
            _a.MovRR(RCX, ptrReg); _a.MovRI(RDX, 0); _a.MovRI(R8, 0x8000);   // MEM_RELEASE
            EmitIatCall("VirtualFree");
            _a.AluRI(0, RSP, 32);
        } else {
            _a.MovRR(RDI, ptrReg); _a.MovRR(RSI, sizeReg);
            _a.MovRI(RAX, 11); _a.Syscall();         // munmap
        }
    }

    void EmitThreadRuntime() {
        bool win = _target == Target.Windows;

        // ---- arena_reset(r10 = block): free the extra chunks, rewind to the first chunk
        _a.Label("arena_reset");
        {
            _a.PushR(RBP); _a.MovRR(RBP, RSP); _a.PushR(RBX); _a.PushR(R12); _a.PushR(R13); _a.PushR(R14);
            _a.AndRspImm8(-16);
            _a.MovRR(R12, R10);
            string loop = NewLabel("ar_loop"), done = NewLabel("ar_done");
            _a.Label(loop);
            _a.Ld(RBX, R12, 32); _a.TestRR(RBX, RBX); _a.Jcc(0x4, done);
            _a.Ld(RAX, RBX, 0); _a.St(R12, 32, RAX);           // unlink
            _a.Ld(R13, RBX, 8);
            EmitOsFree(RBX, R13);
            _a.JmpRel(loop);
            _a.Label(done);
            _a.Ld(RAX, R12, 16); _a.St(R12, 0, RAX);           // cur = first start
            _a.Ld(RAX, R12, 24); _a.St(R12, 8, RAX);           // end = first end
            _a.Lea(RSP, RBP, -32);
            _a.PopR(R14); _a.PopR(R13); _a.PopR(R12); _a.PopR(RBX); _a.PopR(RBP);
            _a.Ret();
        }

        // ---- arena_reset_to(r10 = block, rsi = mark from heapmark): free every chunk
        // allocated after the mark's chunk and rewind to the mark
        _a.Label("arena_reset_to");
        {
            _a.PushR(RBP); _a.MovRR(RBP, RSP); _a.PushR(RBX); _a.PushR(R12); _a.PushR(R13); _a.PushR(R14);
            _a.AndRspImm8(-16);
            _a.MovRR(R12, R10); _a.MovRR(R14, RSI);
            string loop = NewLabel("rt_loop"), notIn = NewLabel("rt_notin"), first = NewLabel("rt_first"), done = NewLabel("rt_done");
            _a.Label(loop);
            _a.Ld(RBX, R12, 32); _a.TestRR(RBX, RBX); _a.Jcc(0x4, first);    // no extra chunks left
            _a.Ld(R13, RBX, 8);                                               // chunk size
            _a.MovRR(RAX, RBX); _a.AluRR(0x01, RAX, R13);                     // chunk end
            _a.AluRR(0x39, R14, RBX); _a.Jcc(0x6, notIn);                     // mark <= start
            _a.AluRR(0x39, R14, RAX); _a.Jcc(0x7, notIn);                     // mark > end
            _a.St(R12, 0, R14); _a.St(R12, 8, RAX); _a.JmpRel(done);          // mark is in this chunk
            _a.Label(notIn);
            _a.Ld(RAX, RBX, 0); _a.St(R12, 32, RAX);                          // unlink and free it
            EmitOsFree(RBX, R13);
            _a.JmpRel(loop);
            _a.Label(first);
            _a.St(R12, 0, R14); _a.Ld(RAX, R12, 24); _a.St(R12, 8, RAX);      // mark is in the first chunk
            _a.Label(done);
            _a.Lea(RSP, RBP, -32);
            _a.PopR(R14); _a.PopR(R13); _a.PopR(R12); _a.PopR(RBX); _a.PopR(RBP);
            _a.Ret();
        }

        // ---- rt_init: set up the main thread's arena block and TLS
        _a.Label("rt_init");
        {
            _a.PushR(RBP); _a.MovRR(RBP, RSP); _a.PushR(RBX); _a.PushR(R12);
            _a.AndRspImm8(-16);
            EmitSlotAddr(0xbb, 3);                             // mov rbx, heapBase
            _a.Lea(RAX, RBX, HeapReserved);
            _a.St(RBX, RtMainBlk + 0, RAX); _a.St(RBX, RtMainBlk + 16, RAX);
            _a.MovRR(RAX, RBX); _a.AluRI(0, RAX, Pe.HeapSize);
            _a.St(RBX, RtMainBlk + 8, RAX); _a.St(RBX, RtMainBlk + 24, RAX);
            _a.StImm(RBX, RtMainBlk + 32, 0);
            _a.Lea(RAX, RBX, RtMainBlk); _a.St(RBX, RtMainBlk + 40, RAX);
            if (win) {
                _a.AluRI(5, RSP, 32);
                EmitIatCall("TlsAlloc");                       // eax = index (< 64 in practice)
                _a.AluRI(0, RSP, 32);
                _a.Db(0x89, 0xc0);                             // mov eax, eax (zero-extend)
                _a.ShiftRI(4, RAX, 3); _a.AluRI(0, RAX, 0x1480);  // TEB.TlsSlots[index]
                _a.St(RBX, RtTlsOff, RAX);
                _a.Lea(RCX, RBX, RtMainBlk);
                _a.Db(0x65); _a.St(RAX, 0, RCX);               // mov gs:[rax], rcx
            } else {
                _a.MovRI(RDI, 0x1001); _a.Lea(RSI, RBX, RtMainBlk);    // ARCH_SET_GS
                _a.MovRI(RAX, 158); _a.Syscall();              // arch_prctl(ARCH_SET_FS, block)
            }
            _a.Lea(RSP, RBP, -16); _a.PopR(R12); _a.PopR(RBX); _a.PopR(RBP);
            _a.Ret();
        }

        // ---- thr_spawn(rdi = function, rsi = argument) -> rax = handle (the region)
        _a.Label("thr_spawn");
        {
            _a.PushR(RBP); _a.MovRR(RBP, RSP);
            _a.PushR(RBX); _a.PushR(R12); _a.PushR(R13); _a.PushR(R14);
            _a.AndRspImm8(-16);
            _a.MovRR(R12, RDI); _a.MovRR(R13, RSI);
            // reuse a cached region if there is one, else ask the OS
            string miss = NewLabel("ts_miss"), have = NewLabel("ts_have");
            EmitHeapBase(R14);
            EmitCacheLock(R14);
            _a.Ld(RBX, R14, RtCacheHead); _a.TestRR(RBX, RBX); _a.Jcc(0x4, miss);
            _a.Ld(RAX, RBX, 24); _a.St(R14, RtCacheHead, RAX);
            _a.Mem(0xff, 1, R14, RtCacheCount);                 // dec count
            EmitCacheUnlock(R14);
            _a.JmpRel(have);
            _a.Label(miss);
            EmitCacheUnlock(R14);
            _a.MovRI(RSI, ThreadRegion); EmitOsAlloc(); _a.MovRR(RBX, RAX);
            _a.Label(have);
            _a.StImm(RBX, 16, ThreadRegion); _a.St(RBX, 32, R13); _a.St(RBX, 40, R12);
            _a.Lea(RAX, RBX, 128); _a.St(RBX, 64, RAX); _a.St(RBX, 80, RAX);
            _a.MovRR(RAX, RBX); _a.AluRI(0, RAX, ArenaChunk); _a.St(RBX, 72, RAX); _a.St(RBX, 88, RAX);
            _a.StImm(RBX, 96, 0);
            _a.Lea(RAX, RBX, 64); _a.St(RBX, 104, RAX);
            if (win) {
                // CreateThread(NULL, 0, thr_entry, region, 0, NULL)
                _a.AluRI(5, RSP, 48);
                _a.MovRI(RCX, 0); _a.MovRI(RDX, 0);
                _a.MovR8LabelAddr("thr_entry"); _a.MovRR(R9, RBX);
                _a.StImm(RSP, 32, 0); _a.StImm(RSP, 40, 0);
                EmitIatCall("CreateThread");
                _a.AluRI(0, RSP, 48);
                _a.St(RBX, 0, RAX);
            } else {
                // child stack top holds the region pointer
                _a.MovRR(RSI, RBX); _a.AluRI(0, RSI, ThreadRegion - 16); _a.St(RSI, 0, RBX);
                // clone(VM|FS|FILES|SIGHAND|THREAD|SYSVSEM|SETTLS|PARENT_SETTID|CHILD_CLEARTID,
                //       stack, &tid, &tid, tls = arena block)
                _a.MovRI(RDI, 0x350F00);                               // (no CLONE_SETTLS: the child sets gs itself)
                _a.MovRR(RDX, RBX); _a.MovRR(R10, RBX); _a.Lea(R8, RBX, 64);
                _a.MovRI(RAX, 56); _a.Syscall();
                _a.TestRR(RAX, RAX); _a.Jcc(0x4, "thr_child");
            }
            _a.MovRR(RAX, RBX);
            _a.Lea(RSP, RBP, -32);
            _a.PopR(R14); _a.PopR(R13); _a.PopR(R12); _a.PopR(RBX); _a.PopR(RBP);
            _a.Ret();
        }

        if (win) {
            // ---- thr_entry(rcx = region): the Windows thread start routine
            _a.Label("thr_entry");
            foreach (var r in new[] { RBX, RBP, RSI, RDI, R12, R13, R14, R15 }) _a.PushR(r);  // Win64 non-volatiles
            _a.AluRI(5, RSP, 40);                              // 8 + 64 + 40: 16-aligned
            _a.MovRR(RBX, RCX);
            EmitSlotAddr(0xb8, 3); _a.Ld(RAX, RAX, RtTlsOff);  // rax = TLS slot offset
            _a.Lea(RCX, RBX, 64); _a.Db(0x65); _a.St(RAX, 0, RCX);   // gs:[slot] = arena block
            _a.Ld(RDI, RBX, 32);
            _a.Db(0xff, 0x53, 0x28);                           // call [rbx+40]
            _a.St(RBX, 8, RAX);                                // result
            _a.AluRI(0, RSP, 40);
            foreach (var r in new[] { R15, R14, R13, R12, RDI, RSI, RBP, RBX }) _a.PopR(r);
            _a.MovRI(RAX, 0);
            _a.Ret();
        } else {
            // ---- thr_child: the new Linux thread starts here (rsp = stack top, fs = its arena)
            _a.Label("thr_child");
            _a.Ld(RBX, RSP, 0);
            _a.MovRI(RDI, 0x1001); _a.Lea(RSI, RBX, 64); _a.MovRI(RAX, 158); _a.Syscall();   // arch_prctl(ARCH_SET_GS, arena block)
            _a.Ld(RDI, RBX, 32);
            _a.Db(0xff, 0x53, 0x28);                           // call [rbx+40]
            _a.St(RBX, 8, RAX);
            _a.MovRI(RDI, 0); _a.MovRI(RAX, 60); _a.Syscall(); // exit (this thread only)
        }

        // ---- thr_join(rdi = handle) -> rax = the thread's result; frees its memory
        _a.Label("thr_join");
        {
            _a.PushR(RBP); _a.MovRR(RBP, RSP); _a.PushR(RBX); _a.PushR(R12); _a.PushR(R13); _a.PushR(R14);
            _a.AndRspImm8(-16);
            _a.MovRR(RBX, RDI);
            if (win) {
                _a.AluRI(5, RSP, 32);
                _a.Ld(RCX, RBX, 0); _a.MovRI(RDX, -1);         // INFINITE
                EmitIatCall("WaitForSingleObject");
                _a.Ld(RCX, RBX, 0);
                EmitIatCall("CloseHandle");
                _a.AluRI(0, RSP, 32);
            } else {
                string wait = NewLabel("tj_wait"), done = NewLabel("tj_done");
                _a.Label(wait);
                _a.Db(0x8b, 0x03);                             // mov eax, [rbx]  (tid word)
                _a.Db(0x85, 0xc0); _a.Jcc(0x4, done);          // 0 -> the thread has exited
                _a.MovRR(RDI, RBX); _a.MovRI(RSI, 0);          // FUTEX_WAIT (shared: the kernel's wake is not private)
                _a.Db(0x89, 0xc2);                             // mov edx, eax (expected value)
                _a.MovRI(R10, 0); _a.MovRI(RAX, 202); _a.Syscall();
                _a.JmpRel(wait);
                _a.Label(done);
            }
            _a.Ld(R12, RBX, 8);                                // result
            string free = NewLabel("tj_free"), full = NewLabel("tj_full"), outl = NewLabel("tj_out");
            _a.Ld(RAX, RBX, 96); _a.TestRR(RAX, RAX); _a.Jcc(0x5, free);   // grew past its first chunk: give it back
            // zero what the thread used of its first chunk: [region+128, cur)
            _a.Lea(RDI, RBX, 128); _a.Ld(RCX, RBX, 64); _a.AluRR(0x29, RCX, RDI);
            _a.ShiftRI(5, RCX, 3); _a.Db(0x31, 0xc0);
            _a.Db(0xf3, 0x48, 0xab);                           // rep stosq
            EmitHeapBase(R14);
            EmitCacheLock(R14);
            _a.Mem(0x83, 7, R14, RtCacheCount); _a.Db(RtCacheMax);   // cmp count, max
            _a.Jcc(0x3, full);                                 // jae
            _a.Ld(RAX, R14, RtCacheHead); _a.St(RBX, 24, RAX); _a.St(R14, RtCacheHead, RBX);
            _a.Mem(0xff, 0, R14, RtCacheCount);                // inc count
            EmitCacheUnlock(R14);
            _a.JmpRel(outl);
            _a.Label(full);
            EmitCacheUnlock(R14);
            _a.Label(free);
            _a.Lea(R10, RBX, 64); _a.CallRel("arena_reset");   // free the thread's extra chunks
            _a.Ld(RSI, RBX, 16);
            EmitOsFree(RBX, RSI);                              // free the region (first chunk, stack)
            _a.Label(outl);
            _a.MovRR(RAX, R12);
            _a.Lea(RSP, RBP, -32); _a.PopR(R14); _a.PopR(R13); _a.PopR(R12); _a.PopR(RBX); _a.PopR(RBP);
            _a.Ret();
        }

        // ---- os_yield(): give up the rest of this time slice. Clobbers volatiles.
        _a.Label("os_yield");
        if (win) {
            _a.PushR(RBP); _a.MovRR(RBP, RSP); _a.AndRspImm8(-16); _a.AluRI(5, RSP, 32);
            EmitIatCall("SwitchToThread");
            _a.MovRR(RSP, RBP); _a.PopR(RBP);
        } else {
            _a.MovRI(RAX, 24); _a.Syscall();                   // sched_yield
        }
        _a.Ret();

        // ---- rt_lock(rdi = int*) / rt_unlock(rdi = int*): a mutex on a zeroed int cell.
        // Windows: the cell IS an SRWLOCK (SRWLOCK_INIT is 0), the same lock C uses.
        // Linux: a futex mutex (0 free, 1 locked, 2 locked with sleepers) that spins
        // briefly before sleeping in the kernel.
        if (win) {
            foreach (var (label, api) in new[] { ("rt_lock", "AcquireSRWLockExclusive"), ("rt_unlock", "ReleaseSRWLockExclusive") }) {
                _a.Label(label);
                _a.PushR(RBP); _a.MovRR(RBP, RSP); _a.AndRspImm8(-16); _a.AluRI(5, RSP, 32);
                _a.MovRR(RCX, RDI);
                EmitIatCall(api);
                _a.MovRR(RSP, RBP); _a.PopR(RBP);
                _a.Ret();
            }
        } else {
            string done = NewLabel("lk_done"), spin = NewLabel("lk_spin"), next = NewLabel("lk_next"), wait = NewLabel("lk_wait");
            _a.Label("rt_lock");
            _a.Db(0x31, 0xc0); _a.MovRI(RDX, 1);
            _a.Db(0xf0, 0x48, 0x0f, 0xb1, 0x17);               // lock cmpxchg [rdi], rdx   (0 -> 1)
            _a.Jcc(0x4, done);
            _a.MovRI(R8, 100);
            _a.Label(spin);
            _a.Db(0xf3, 0x90);                                 // pause
            _a.Db(0x48, 0x83, 0x3f, 0x00); _a.Jcc(0x5, next);  // still held?
            _a.Db(0x31, 0xc0); _a.MovRI(RDX, 1);
            _a.Db(0xf0, 0x48, 0x0f, 0xb1, 0x17); _a.Jcc(0x4, done);
            _a.Label(next);
            _a.AluRI(5, R8, 1); _a.Jcc(0x5, spin);
            _a.MovRI(RAX, 2); _a.Db(0x48, 0x87, 0x07);         // xchg [rdi], rax  (mark contended)
            _a.TestRR(RAX, RAX); _a.Jcc(0x4, done);            // it was free: ours
            _a.Label(wait);
            _a.MovRI(RSI, 128); _a.MovRI(RDX, 2); _a.MovRI(R10, 0);
            _a.MovRI(RAX, 202); _a.Syscall();                  // futex(p, FUTEX_WAIT_PRIVATE, 2)
            _a.MovRI(RAX, 2); _a.Db(0x48, 0x87, 0x07);
            _a.TestRR(RAX, RAX); _a.Jcc(0x5, wait);
            _a.Label(done);
            _a.Ret();

            string udone = NewLabel("ul_done");
            _a.Label("rt_unlock");
            _a.MovRI(RAX, 0); _a.Db(0x48, 0x87, 0x07);         // xchg [rdi], rax  (release)
            _a.AluRI(7, RAX, 2); _a.Jcc(0x5, udone);           // nobody sleeping
            _a.MovRI(RSI, 129); _a.MovRI(RDX, 1);
            _a.MovRI(RAX, 202); _a.Syscall();                  // futex(p, FUTEX_WAKE_PRIVATE, 1)
            _a.Label(udone);
            _a.Ret();
        }

        // ---- rt_cpu_count() -> rax = logical processors available to this process
        _a.Label("rt_cpu_count");
        _a.PushR(RBP); _a.MovRR(RBP, RSP);
        if (win) {
            _a.AluRI(5, RSP, 96); _a.AndRspImm8(-16);
            _a.Lea(RCX, RSP, 32);
            EmitIatCall("GetSystemInfo");
            _a.Db(0x8b, 0x44, 0x24, 0x40);                     // mov eax, [rsp+64] (dwNumberOfProcessors)
        } else {
            _a.AluRI(5, RSP, 128);
            _a.MovRI(RDI, 0); _a.MovRI(RSI, 128); _a.MovRR(RDX, RSP);
            _a.MovRI(RAX, 204); _a.Syscall();                  // sched_getaffinity -> bytes of mask
            _a.MovRR(RCX, RAX); _a.MovRI(RAX, 0); _a.MovRI(RDX, 0);
            string loop = NewLabel("cc_loop"), done = NewLabel("cc_done");
            _a.Label(loop);
            _a.AluRR(0x39, RDX, RCX); _a.Jcc(0xd, done);      // idx >= bytes
            _a.Db(0x4c, 0x8b, 0x04, 0x14);                     // mov r8, [rsp+rdx]
            _a.Db(0xf3, 0x4d, 0x0f, 0xb8, 0xc0);               // popcnt r8, r8
            _a.AluRR(0x01, RAX, R8);
            _a.AluRI(0, RDX, 8);
            _a.JmpRel(loop);
            _a.Label(done);
            string ok = NewLabel("cc_ok");
            _a.TestRR(RAX, RAX); _a.Jcc(0x5, ok); _a.MovRI(RAX, 1); _a.Label(ok);
        }
        _a.MovRR(RSP, RBP); _a.PopR(RBP);
        _a.Ret();

        // ---- rt_now_ns() -> rax = monotonic time in nanoseconds
        _a.Label("rt_now_ns");
        _a.PushR(RBP); _a.MovRR(RBP, RSP);
        if (win) {
            // t = QueryPerformanceCounter, f = QueryPerformanceFrequency
            // ns = (t / f) * 1e9 + (t % f) * 1e9 / f   (no 64-bit overflow)
            _a.AluRI(5, RSP, 48); _a.AndRspImm8(-16);
            _a.Lea(RCX, RSP, 32); EmitIatCall("QueryPerformanceCounter");
            _a.Lea(RCX, RSP, 40); EmitIatCall("QueryPerformanceFrequency");
            _a.Ld(RAX, RSP, 32); _a.Ld(RCX, RSP, 40);
            _a.MovRI(RDX, 0); _a.Db(0x48, 0xf7, 0xf1);         // div rcx: rax = t/f, rdx = t%f
            _a.ImulRRI(RAX, RAX, 1000000000);
            _a.MovRR(R8, RAX);
            _a.ImulRRI(RAX, RDX, 1000000000);
            _a.MovRI(RDX, 0); _a.Db(0x48, 0xf7, 0xf1);         // div rcx
            _a.AluRR(0x01, RAX, R8);
        } else {
            _a.AluRI(5, RSP, 16);
            _a.MovRI(RDI, 1); _a.MovRR(RSI, RSP);              // CLOCK_MONOTONIC
            _a.MovRI(RAX, 228); _a.Syscall();                  // clock_gettime
            _a.Ld(RAX, RSP, 0); _a.ImulRRI(RAX, RAX, 1000000000);
            _a.Ld(RCX, RSP, 8); _a.AluRR(0x01, RAX, RCX);
        }
        _a.MovRR(RSP, RBP); _a.PopR(RBP);
        _a.Ret();

        // ---- rt_sleep_ms(rdi = milliseconds)
        _a.Label("rt_sleep_ms");
        _a.PushR(RBP); _a.MovRR(RBP, RSP);
        if (win) {
            _a.AndRspImm8(-16); _a.AluRI(5, RSP, 32);
            _a.MovRR(RCX, RDI);
            EmitIatCall("Sleep");
        } else {
            _a.AluRI(5, RSP, 16);
            _a.MovRR(RAX, RDI); _a.MovRI(RDX, 0); _a.MovRI(RCX, 1000);
            _a.Db(0x48, 0xf7, 0xf1);                           // div rcx
            _a.St(RSP, 0, RAX);                                // tv_sec
            _a.ImulRRI(RDX, RDX, 1000000); _a.St(RSP, 8, RDX); // tv_nsec
            _a.MovRR(RDI, RSP); _a.MovRI(RSI, 0);
            _a.MovRI(RAX, 35); _a.Syscall();                   // nanosleep
        }
        _a.MovRR(RSP, RBP); _a.PopR(RBP);
        _a.Ret();
    }

    // Builtin calls that belong to threading. Returns false if c is not one of them.
    bool EmitThreadBuiltin(CallExpr c) {
        switch (c.Name) {
            case "join":
                EmitExpr(c.Args[0]); _a.MovRR(RDI, RAX); _a.CallRel("thr_join"); return true;
            case "atomic_add":
                EmitArgsTo(c, "rdi", "rsi"); _a.MovRR(RAX, RSI);
                _a.Db(0xf0, 0x48, 0x0f, 0xc1, 0x07); return true;      // lock xadd [rdi], rax
            case "atomic_xchg":
            case "atomic_store":
                EmitArgsTo(c, "rdi", "rsi"); _a.MovRR(RAX, RSI);
                _a.Db(0x48, 0x87, 0x07); return true;                  // xchg [rdi], rax (implicitly locked)
            case "atomic_load":
                EmitExpr(c.Args[0]); _a.Db(0x48, 0x8b, 0x00); return true;   // mov rax, [rax]
            case "atomic_cas":
                EmitArgsTo(c, "rdi", "rsi", "rdx"); _a.MovRR(RAX, RSI);
                _a.Db(0xf0, 0x48, 0x0f, 0xb1, 0x17);                   // lock cmpxchg [rdi], rdx
                _a.Db(0x0f, 0x94, 0xc0, 0x48, 0x0f, 0xb6, 0xc0);       // sete al; movzx rax, al
                return true;
            case "lock":
                EmitExpr(c.Args[0]); _a.MovRR(RDI, RAX); _a.CallRel("rt_lock"); return true;
            case "unlock":
                EmitExpr(c.Args[0]); _a.MovRR(RDI, RAX); _a.CallRel("rt_unlock"); return true;
            case "cpu_count":
                _a.CallRel("rt_cpu_count"); return true;
            case "now_ns":
                _a.CallRel("rt_now_ns"); return true;
            case "sleep_ms":
                EmitExpr(c.Args[0]); _a.MovRR(RDI, RAX); _a.CallRel("rt_sleep_ms"); return true;
        }
        return false;
    }
}
