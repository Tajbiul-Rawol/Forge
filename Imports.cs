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
// Locations: a node's Line is a position id, an index into a table of (file, line,
// column) that the lexer fills in, one entry per token. Every node, and every copy the
// inliner or the generics make of it, so carries its file and column. Ids grow in
// source order (the main file is lexed first), so sorting by Line sorts by place.
// Sources.Loc(id) prints "file:line:col". Id 0 means "no position".
static class Sources {
    public static readonly List<string> Files = new();       // display path, by file number
    public const int NoFile = -1;                            // compiler-supplied source (runtime, standard library)
    static readonly List<(int File, int Line, int Col)> _pos = new() { (0, 0, 0) };

    // locked: the fuzzer may still have a timed-out compile running on another thread
    public static int NewPos(int file, int line, int col) { lock (_pos) { _pos.Add((file, line, col)); return _pos.Count - 1; } }
    static (int File, int Line, int Col) At(int id) { lock (_pos) return id > 0 && id < _pos.Count ? _pos[id] : (0, 0, 0); }
    public static int FileOf(int id) => At(id).File;
    public static int LineOf(int id) => At(id).Line;
    public static int ColOf(int id) => At(id).Col;
    // sort key in source order (ids are not: tokens inside $"{...}" holes are lexed later)
    public static (int, int, int) Order(int id) => At(id);
    public static string Loc(int id) {
        var (f, l, c) = At(id);
        string file = f >= 0 && f < Files.Count ? Files[f] + ":" : "";
        return c > 0 ? $"{file}{l}:{c}" : $"{file}{l}";
    }
    // a fresh table (one per compile; the fuzzer compiles thousands of programs)
    public static void Reset() { lock (_pos) { Files.Clear(); _pos.RemoveRange(1, _pos.Count - 1); } }

    // Reads the main file's imports (transitively) and parses everything into one module.
    public static ModuleNode Load(string path, string src) {
        Reset();
        var canon = new List<string>();
        var toks = new List<List<Tok>>();
        void Add(string display, string text) {
            int id = Files.Count;
            Files.Add(display);
            canon.Add(Canonical(display));
            toks.Add(new Lexer(text, id).Lex());
        }
        Add(path, src);
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
                Add(display, text);
            }
        }
        if (toks.Count == 1) return new Parser(toks[0]).ParseModule();

        // Struct and generic names are found by a scan over every file's tokens, so a
        // file can use a type declared in another. Each file is then parsed on its own,
        // so a missing '}' is reported in the file that has it.
        var all = toks.SelectMany(t => t.Where(x => x.Kind != TokKind.Eof)).ToList();
        all.Add(toks[0][^1]);
        var scan = new Parser(all);
        var mods = new List<ModuleNode>();
        var errors = new List<Diagnostic>();             // syntax errors from every file
        foreach (var t in toks) {
            try { mods.Add(new Parser(t, scan.StructNames, scan).ParseModule()); }
            catch (ForgeErrors fe) { errors.AddRange(fe.Errors); }
        }
        if (errors.Count > 0) throw new ForgeErrors(errors.OrderBy(e => Order(e.Line)).Take(CodeGen.MaxErrors).ToList());
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
