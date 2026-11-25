using System;
using System.Collections.Generic;

namespace ForgeC;

// Minimal ELF32 writer for a Multiboot1 kernel. One PT_LOAD segment that maps
// the whole image at the load address; p_memsz exceeds p_filesz to provide
// zero-filled space for page tables, stack, and heap. The entry point is the
// 32-bit boot stub (right after the multiboot header).
static class Elf32 {
    // img        = bytes to place in the file at the load address
    // loadVaddr  = where the image loads (e.g. 0x100000)
    // entryVaddr = entry point (the 32-bit stub, = loadVaddr + 12)
    // memSize    = total in-memory size from loadVaddr (image + bss tail)
    public static byte[] Build(byte[] img, long loadVaddr, long entryVaddr, int memSize) {
        const int EHSIZE = 52;       // ELF32 header
        const int PHSIZE = 32;       // ELF32 program header
        int phoff = EHSIZE;
        int fileImgOff = 0x1000;     // place the image one page into the file

        var buf = new List<byte>();
        void U8(int v) => buf.Add((byte)v);
        void U16(int v) { U8(v & 0xff); U8((v >> 8) & 0xff); }
        void U32(long v) { U8((int)(v & 0xff)); U8((int)((v >> 8) & 0xff)); U8((int)((v >> 16) & 0xff)); U8((int)((v >> 24) & 0xff)); }

        // ELF header (ELF32, little-endian, x86)
        U8(0x7f); U8('E'); U8('L'); U8('F');
        U8(1);                 // EI_CLASS = ELFCLASS32
        U8(1);                 // EI_DATA  = little-endian
        U8(1);                 // EI_VERSION
        U8(0);                 // EI_OSABI
        for (int i = 0; i < 8; i++) U8(0);
        U16(2);                // e_type = ET_EXEC
        U16(3);                // e_machine = EM_386
        U32(1);                // e_version
        U32(entryVaddr);       // e_entry
        U32(phoff);            // e_phoff
        U32(0);                // e_shoff
        U32(0);                // e_flags
        U16(EHSIZE);           // e_ehsize
        U16(PHSIZE);           // e_phentsize
        U16(1);                // e_phnum
        U16(0);                // e_shentsize
        U16(0);                // e_shnum
        U16(0);                // e_shstrndx

        // program header (PT_LOAD, RWX)
        U32(1);                // p_type = PT_LOAD
        U32(fileImgOff);       // p_offset
        U32(loadVaddr);        // p_vaddr
        U32(loadVaddr);        // p_paddr
        U32(img.Length);       // p_filesz
        U32(memSize);          // p_memsz  (>= filesz: zero-filled tail)
        U32(7);                // p_flags = R|W|X
        U32(0x1000);           // p_align

        // pad to the image file offset
        while (buf.Count < fileImgOff) U8(0);
        // the loaded image
        buf.AddRange(img);
        return buf.ToArray();
    }
}
