using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ForgeC;

// Multi-file programs.
//
//     module shapes;
//     import "geometry.fg";          // relative to this file
//     import "lib/strings.fg";
//
// Imports come right after the module line. They are transitive, each file is loaded
// once (so cycles are fine), and every file's functions, structs and externs go into
// one program: there is a single global namespace, as in one C# assembly.
//
// Locations: file number f's lines are numbered from (f << FileShift) + 1, so every
// node, and every copy the inliner or the generics make of it, carries its file in
// its Line. The main file is file 0, so its lines are plain line numbers and a
// single-file program compiles exactly as before. Sources.Loc(line) prints "file:line".
static class Sources {
    public const int FileShift = 20;                         // up to 1,048,575 lines per file
    const int LineMask = (1 << FileShift) - 1;
    const int MaxFiles = 1 << (30 - FileShift);
    public static readonly List<string> Files = new();       // display path, by file number

    public static int FileOf(int line) => line >> FileShift;
    public static int LineOf(int line) => line & LineMask;
    public static string Loc(int line) {
        int f = FileOf(line);
        return f < Files.Count ? $"{Files[f]}:{LineOf(line)}" : $"{LineOf(line)}";
    }

    // Reads the main file's imports (transitively) and parses everything into one module.
    public static ModuleNode Load(string path, string src) {
        Files.Clear();
        var canon = new List<string>();
        var toks = new List<List<Tok>>();
        void Add(string display, string text, int importLine) {
            if (Files.Count >= MaxFiles) throw new ForgeException($"too many imported files (at most {MaxFiles})", importLine);
            if (text.Count(c => c == '\n') >= LineMask)
                throw new ForgeException($"'{display}' is too long (at most {LineMask - 1} lines)", importLine);
            int id = Files.Count;
            Files.Add(display);
            canon.Add(Canonical(display));
            toks.Add(new Lexer(text, (id << FileShift) + 1).Lex());
        }
        Add(path, src, 1);
        for (int i = 0; i < toks.Count; i++) {
            foreach (var (rel, line) in ImportsOf(toks[i])) {
                string display = Combine(Files[i], rel);
                if (canon.Contains(Canonical(display), PathComparer)) continue;
                if (!File.Exists(display)) throw new ForgeException($"cannot find imported file '{rel}'" + (display != rel ? $" (looked for {display})" : ""), line);
                string text;
                try { text = File.ReadAllText(display); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                    throw new ForgeException($"cannot read imported file '{rel}': {ex.Message}", line);
                }
                Add(display, text, line);
            }
        }
        if (toks.Count == 1) return new Parser(toks[0]).ParseModule();

        // Struct and generic names are found by a scan over every file's tokens, so a
        // file can use a type declared in another. Each file is then parsed on its own,
        // so a missing '}' is reported in the file that has it.
        var all = toks.SelectMany(t => t.Where(x => x.Kind != TokKind.Eof)).ToList();
        all.Add(toks[0][^1]);
        var scan = new Parser(all);
        var mods = toks.Select(t => new Parser(t, scan.StructNames, scan).ParseModule()).ToList();
        var m = mods[0];
        foreach (var other in mods.Skip(1)) {
            m.Funcs.AddRange(other.Funcs);
            m.Structs.AddRange(other.Structs);
            m.Externs.AddRange(other.Externs);
        }
        return m;
    }

    // import "x.fg"; statements right after  module a.b;  (the parser checks their form)
    static IEnumerable<(string Path, int Line)> ImportsOf(List<Tok> t) {
        int k = 0;
        if (k < t.Count && t[k].Is(TokKind.Kw, "module")) {
            while (k < t.Count && !t[k].Is(TokKind.Op, ";") && t[k].Kind != TokKind.Eof) k++;
            k++;
        }
        while (k + 2 < t.Count && t[k].Is(TokKind.Id, "import") && t[k + 1].Kind == TokKind.Str && t[k + 2].Is(TokKind.Op, ";")) {
            yield return (t[k + 1].Text, t[k].Line);
            k += 3;
        }
    }

    // The imported file's path as the user will see it: the importing file's folder
    // plus the import's relative path, with '/' (it works on every OS).
    static string Combine(string importer, string rel) {
        if (Path.IsPathRooted(rel)) return rel;
        int cut = Math.Max(importer.LastIndexOf('/'), importer.LastIndexOf('\\'));
        return cut < 0 ? rel : importer[..(cut + 1)] + rel;
    }
    static string Canonical(string p) => Path.GetFullPath(p);
    static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
