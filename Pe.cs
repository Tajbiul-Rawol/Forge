using System.Linq;
using System.Collections.Generic;
using System;
using System.Text;

namespace ForgeC;

// Writes a minimal PE32+ (64-bit) Windows executable with a kernel32 import
// table. Two sections: .text (code, R-X) and .data (R-W) which holds the import
// apparatus, the program's string literals, and two mutable 8-byte slots
// (the stdout handle and a "bytes written" scratch for WriteFile).
//
// The import table is the load-bearing structure: the Windows loader walks the
// Import Directory, and for each named function writes its real address into the
// Import Address Table (IAT). Our code calls through those IAT slots.
static class Pe {
    public const long ImageBase = 0x400000;
    const int SectAlign = 0x1000;
    public const int SectAlignPublic = 0x1000;
    const int FileAlign = 0x200;
    public const int HeapSize = 0x1000000;   // 16 MB zero-filled heap (virtual-only)

    // Imports grouped by DLL: the runtime's kernel32 functions, then the program's
    // extern libraries, then ws2_32 (last, so it can be left out when unused).
    // The flat index across all functions in this order is an IAT slot.
    static readonly (string Dll, string[] Funcs) Kernel32 =
        ("kernel32.dll", new[] { "GetStdHandle", "WriteFile", "ExitProcess",
                                  "CreateThread", "WaitForSingleObject", "CloseHandle",
                                  "CreateFileA", "ReadFile", "TlsAlloc", "VirtualAlloc",
                                  "VirtualFree", "SwitchToThread", "Sleep", "GetSystemInfo",
                                  "QueryPerformanceCounter", "QueryPerformanceFrequency",
                                  "AcquireSRWLockExclusive", "ReleaseSRWLockExclusive",
                                  "GetFileSizeEx" });
    static readonly (string Dll, string[] Funcs) Ws2 =
        ("ws2_32.dll",   new[] { "WSAStartup", "socket", "connect", "bind", "listen",
                                  "accept", "send", "recv", "closesocket",
                                  "gethostbyname" });
    // the program's extern libraries (set by CodeGen before code generation)
    public static List<(string Dll, string[] Funcs)> ExternGroups = new();
    public static (string Dll, string[] Funcs)[] ImportGroups => new[] { Kernel32 }.Concat(ExternGroups).Append(Ws2).ToArray();
    public static int ExternBase => Kernel32.Funcs.Length;      // flat index of the first extern function
    // flat index of one of the runtime's own imports
    public static int RuntimeImport(string name) {
        int k = Array.IndexOf(Kernel32.Funcs, name);
        if (k >= 0) return k;
        int w = Array.IndexOf(Ws2.Funcs, name);
        if (w >= 0) return GroupBase(ImportGroups.Length - 1) + w;
        return -1;
    }
    // Flattened view of all imported function names, in IAT order.
    public static string[] Imports => FlattenImports();
    static string[] FlattenImports() {
        var list = new System.Collections.Generic.List<string>();
        foreach (var g in ImportGroups) list.AddRange(g.Funcs);
        return list.ToArray();
    }
    // Index of the first function of each DLL group within the flat Imports array.
    static int GroupBase(int gi) { int b = 0; for (int i = 0; i < gi; i++) b += ImportGroups[i].Funcs.Length; return b; }

    static int Align(int v, int a) => (v + a - 1) & ~(a - 1);

    // Result of laying out .data so CodeGen can resolve absolute addresses.
    public sealed class Layout {
        public long DataVaddr;          // ImageBase + dataRVA
        public int  IatOffset;          // offset of the (combined) IAT within .data
        public int  HandleOffset;       // stdout handle slot
        public int  ScratchOffset;      // bytes-written scratch slot
        public int  HeapPtrOffset;      // bump-allocator pointer slot
        public int  HeapBaseOffset;     // start of the heap region (virtual-only)
        public int  StringsOffset;      // where program string literals begin
        // flat function index -> IAT slot offset (accounts for per-DLL terminators)
        public int[] _iatSlotOff = Array.Empty<int>();
        public long IatSlot(int i) => DataVaddr + _iatSlotOff[i];
        public long HandleAddr => DataVaddr + HandleOffset;
        public long ScratchAddr => DataVaddr + ScratchOffset;
        public long HeapPtrAddr => DataVaddr + HeapPtrOffset;
        public long HeapBaseAddr => DataVaddr + HeapBaseOffset;
        public long StringAddr(int off) => DataVaddr + StringsOffset + off;

        // internals carried from PlanLayout to Build
        public int _nDll, _idtOff, _headers, _textRva, _dataRva, _codeLen, _stringBlobLen;
        public int[] _nameOff = Array.Empty<int>();        // per-function hint/name offset (flat)
        public int[] _iltGroupOff = Array.Empty<int>();    // per-DLL ILT start offset
        public int[] _iatGroupOff = Array.Empty<int>();    // per-DLL IAT start offset
        public int[] _dllNameOff = Array.Empty<int>();     // per-DLL name offset
    }

    // First pass: decide the .data layout (needs only code length + string blob length).
    public static Layout PlanLayout(int codeLen, int stringBlobLen, bool winsock = true) {
        int headers = Align(0x40 + 4 + 20 + 240 + 40 * 2, FileAlign); // -> 0x200
        int textRva = SectAlign;
        int dataRva = textRva + Align(codeLen, SectAlign);
        // ws2_32 is the last group and is left out when the program has no sockets:
        // every DLL a process loads is notified on each thread start, so it costs.
        int nDll = winsock ? ImportGroups.Length : ImportGroups.Length - 1;
        int nFunc = GroupBase(nDll);

        int p = 0;
        // ILT: one table per DLL, each with a null terminator
        int[] iltGroupOff = new int[nDll];
        for (int g = 0; g < nDll; g++) { iltGroupOff[g] = p; p += (ImportGroups[g].Funcs.Length + 1) * 8; }
        // IAT: one table per DLL, each with a null terminator (loader patches these)
        int[] iatGroupOff = new int[nDll];
        int[] iatSlotOff = new int[nFunc];
        int fi = 0;
        for (int g = 0; g < nDll; g++) {
            iatGroupOff[g] = p;
            for (int k = 0; k < ImportGroups[g].Funcs.Length; k++) { iatSlotOff[fi++] = p; p += 8; }
            p += 8; // null terminator
        }
        int iatOff = iatGroupOff[0];
        // Import Directory: one descriptor per DLL + a null terminator
        int idtOff = p; p += (nDll + 1) * 20;
        // hint/name entries (flat)
        int[] nameOff = new int[nFunc];
        for (int i = 0; i < nFunc; i++) {
            nameOff[i] = p;
            p += 2;                                       // hint
            p += Imports[i].Length + 1;                   // name + NUL
            if ((p & 1) != 0) p++;                        // word align
        }
        // DLL name strings
        int[] dllNameOff = new int[nDll];
        for (int g = 0; g < nDll; g++) { dllNameOff[g] = p; p += ImportGroups[g].Dll.Length + 1; if ((p & 1) != 0) p++; }
        // mutable slots, 8-byte aligned
        p = Align(p, 8);
        int handleOff = p; p += 8;
        int scratchOff = p; p += 8;
        int heapPtrOff = p; p += 8;
        int stringsOff = p;

        var lay = new Layout {
            DataVaddr = ImageBase + dataRva,
            IatOffset = iatOff,
            HandleOffset = handleOff,
            ScratchOffset = scratchOff,
            HeapPtrOffset = heapPtrOff,
            StringsOffset = stringsOff,
        };
        lay._idtOff = idtOff; lay._nameOff = nameOff;
        lay._iltGroupOff = iltGroupOff; lay._iatGroupOff = iatGroupOff; lay._iatSlotOff = iatSlotOff;
        lay._dllNameOff = dllNameOff; lay._headers = headers;
        lay._textRva = textRva; lay._dataRva = dataRva; lay._codeLen = codeLen;
        lay._stringBlobLen = stringBlobLen; lay._nDll = nDll;
        lay.HeapBaseOffset = Align(stringsOff + stringBlobLen, 16);
        return lay;
    }

    public static byte[] Build(byte[] code, byte[] stringBlob, Layout lay, long entryVaddr) {
        int dataContentLen = lay.StringsOffset + stringBlob.Length;
        var data = new byte[dataContentLen];

        void PutU16(int off, int v) { data[off] = (byte)(v & 0xff); data[off+1] = (byte)((v>>8)&0xff); }
        void PutU32(int off, long v) { for (int i=0;i<4;i++) data[off+i]=(byte)((v>>(8*i))&0xff); }
        void PutU64(int off, long v) { for (int i=0;i<8;i++) data[off+i]=(byte)((v>>(8*i))&0xff); }

        int dataRva = lay._dataRva;
        int nDll = lay._nDll;
        // hint/name entries (flat) + fill each group's ILT and IAT
        int fi = 0;
        for (int g = 0; g < nDll; g++) {
            int nf = ImportGroups[g].Funcs.Length;
            for (int k = 0; k < nf; k++) {
                int no = lay._nameOff[fi];
                PutU16(no, 0); // hint
                var nb = Encoding.ASCII.GetBytes(Imports[fi]);
                Array.Copy(nb, 0, data, no + 2, nb.Length);
                PutU64(lay._iltGroupOff[g] + k * 8, dataRva + no);   // ILT entry
                PutU64(lay._iatGroupOff[g] + k * 8, dataRva + no);   // IAT entry (loader overwrites)
                fi++;
            }
            // ILT/IAT terminators for this group are already zero.
        }
        // DLL name strings + one import directory descriptor per DLL
        for (int g = 0; g < nDll; g++) {
            var dll = Encoding.ASCII.GetBytes(ImportGroups[g].Dll);
            Array.Copy(dll, 0, data, lay._dllNameOff[g], dll.Length);
            int d = lay._idtOff + g * 20;
            PutU32(d + 0,  dataRva + lay._iltGroupOff[g]);   // OriginalFirstThunk -> this DLL's ILT
            PutU32(d + 4,  0);                               // TimeDateStamp
            PutU32(d + 8,  0);                               // ForwarderChain
            PutU32(d + 12, dataRva + lay._dllNameOff[g]);    // Name
            PutU32(d + 16, dataRva + lay._iatGroupOff[g]);   // FirstThunk -> this DLL's IAT
        }
        // final import directory descriptor is the zero terminator (already zero)
        // program string literals
        Array.Copy(stringBlob, 0, data, lay.StringsOffset, stringBlob.Length);

        // ---- assemble the file ----
        int headers = lay._headers;
        int textFileOff = headers;
        int textFileLen = Align(lay._codeLen, FileAlign);
        int dataFileOff = textFileOff + textFileLen;
        int dataFileLen = Align(data.Length, FileAlign);
        // .data is larger in memory than on disk: the tail is the zero-filled heap.
        int dataVirtualSize = lay.HeapBaseOffset + HeapSize;
        int imageSize = lay._dataRva + Align(dataVirtualSize, SectAlign);

        var buf = new byte[dataFileOff + dataFileLen];
        void W8(int off, int v) => buf[off] = (byte)v;
        void W16(int off, int v) { buf[off]=(byte)(v&0xff); buf[off+1]=(byte)((v>>8)&0xff); }
        void W32(int off, long v) { for (int i=0;i<4;i++) buf[off+i]=(byte)((v>>(8*i))&0xff); }
        void W64(int off, long v) { for (int i=0;i<8;i++) buf[off+i]=(byte)((v>>(8*i))&0xff); }

        // DOS header: 'MZ', e_lfanew at 0x3C points to PE sig at 0x40
        W8(0, (byte)'M'); W8(1, (byte)'Z');
        W32(0x3c, 0x40);
        int pe = 0x40;
        W8(pe, (byte)'P'); W8(pe+1, (byte)'E'); W8(pe+2, 0); W8(pe+3, 0);
        // COFF file header
        int coff = pe + 4;
        W16(coff + 0, 0x8664);     // machine x86-64
        W16(coff + 2, 2);          // number of sections
        W32(coff + 4, 0);          // timestamp
        W32(coff + 8, 0);          // ptr to symbol table
        W32(coff + 12, 0);         // num symbols
        W16(coff + 16, 240);       // size of optional header
        W16(coff + 18, 0x0023);    // characteristics: EXECUTABLE | RELOCS_STRIPPED | LARGE_ADDRESS_AWARE
        // Optional header (PE32+)
        int opt = coff + 20;
        W16(opt + 0, 0x20b);       // magic PE32+
        W8(opt + 2, 14); W8(opt + 3, 0); // linker version
        W32(opt + 4, textFileLen); // size of code
        W32(opt + 8, dataFileLen); // size of initialized data
        W32(opt + 12, 0);          // size of uninit data
        W32(opt + 16, entryVaddr - ImageBase); // entry point RVA
        W32(opt + 20, lay._textRva);           // base of code RVA
        W64(opt + 24, ImageBase);  // image base
        W32(opt + 32, SectAlign);  // section alignment
        W32(opt + 36, FileAlign);  // file alignment
        W16(opt + 40, 6); W16(opt + 42, 0);    // OS version
        W16(opt + 44, 0); W16(opt + 46, 0);    // image version
        W16(opt + 48, 6); W16(opt + 50, 0);    // subsystem version
        W32(opt + 52, 0);          // win32 version
        W32(opt + 56, imageSize);  // size of image
        W32(opt + 60, headers);    // size of headers
        W32(opt + 64, 0);          // checksum
        W16(opt + 68, 3);          // subsystem = CONSOLE
        W16(opt + 70, 0x8100);     // dll characteristics: NX_COMPAT | TERMINAL_SERVER_AWARE
                                   // (deliberately NO dynamic-base/high-entropy: we have no
                                   //  relocation table and hardcode addresses at ImageBase)
        W64(opt + 72, 0x100000);   // stack reserve
        W64(opt + 80, 0x1000);     // stack commit
        W64(opt + 88, 0x100000);   // heap reserve
        W64(opt + 96, 0x1000);     // heap commit
        W32(opt + 104, 0);         // loader flags
        W32(opt + 108, 16);        // number of data directories
        // data directories: [1] import table, [12] IAT
        int dd = opt + 112;
        W32(dd + 1*8 + 0, lay._dataRva + lay._idtOff);     // import dir RVA
        W32(dd + 1*8 + 4, (lay._nDll + 1) * 20);    // import dir size (one descriptor per DLL + null)
        W32(dd + 12*8 + 0, lay._dataRva + lay.IatOffset);   // IAT RVA (start of first group)
        // IAT spans all groups incl. per-group terminators: from first slot to end of last group.
        W32(dd + 12*8 + 4, (lay._idtOff - lay.IatOffset));  // IAT size
        // section headers
        int sh = opt + 240;
        void Section(int baseOff, string name, int vsize, int rva, int rawsize, int rawptr, uint chars) {
            var nm = Encoding.ASCII.GetBytes(name);
            for (int i = 0; i < 8; i++) W8(baseOff + i, i < nm.Length ? nm[i] : 0);
            W32(baseOff + 8, vsize);
            W32(baseOff + 12, rva);
            W32(baseOff + 16, rawsize);
            W32(baseOff + 20, rawptr);
            W32(baseOff + 24, 0); W32(baseOff + 28, 0); W16(baseOff + 32, 0); W16(baseOff + 34, 0);
            W32(baseOff + 36, (long)chars);
        }
        Section(sh,      ".text", lay._codeLen, lay._textRva, textFileLen, textFileOff, 0x60000020); // CODE|EXEC|READ
        Section(sh + 40, ".data", dataVirtualSize, lay._dataRva, dataFileLen, dataFileOff, 0xC0000040); // INIT|READ|WRITE
        // code + data payloads
        Array.Copy(code, 0, buf, textFileOff, code.Length);
        Array.Copy(data, 0, buf, dataFileOff, data.Length);
        return buf;
    }
}
