using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ForgeC;

// Mutation fuzzer for forgec.
//
// Takes every examples/*.fg, mutates it (token deletes, duplicates, swaps, truncation,
// random inserts, boundary numbers, deep nesting) and compiles it for all three
// targets, in-process. A ForgeException/ForgeErrors is a correct outcome (the program
// is broken; the compiler said so). Anything else, or a compile that hangs, is a
// compiler bug: the input is written to tests/fuzz/crashes/ and the run fails.
//
//   dotnet run -c Release --project tests/fuzz -- [iterations=2000] [seed=1]
static class Fuzz {
    static readonly Regex Chunk = new(@"\$?""(\\.|[^""\\])*""|'(\\.|[^'\\])'|\w+|\s+|//[^\n]*|.", RegexOptions.Compiled);
    static readonly string[] Inserts = {
        "(", ")", "{", "}", "[", "]", ";", ",", ".", "=", "==", "+", "-", "*", "/", "%", "<<", ">>",
        "&", "|", "^", "!", "~", "<", ">", "&&", "||", "?", ":", "=>", "\"", "$\"{", "'",
        "if", "else", "while", "for", "return", "var", "new", "null", "struct", "extern", "asm",
        "spawn", "await", "async", "int", "long", "u8", "string", "bool", "void", "sizeof", "goto",
        "break", "continue", "switch", "case", "default", "++", "--", "+=", "-=", "<<=", ":",
        "double", "1.5", "0.0", "(double)", "(int)",
        "<T>", "T", "<int>", "List<int>", "<string,int>", ">>",
        "Math.Sqrt(", "Math.Pow(", "Math.PI", "Math.Abs(", "Math.Sin(",
        "0", "-1", "9223372036854775807", "99999999999999999999", "0x", "1e400", "Main", "x",
    };
    const int TimeoutMs = 10_000;

    static int Main(string[] args) {
        if (args.Length == 2 && args[0] == "--time") {
            var th = new Thread(() => TimePhases(File.ReadAllText(args[1])), 256 * 1024 * 1024);
            th.Start(); th.Join(); return 0;
        }
        int iterations = args.Length > 0 ? int.Parse(args[0]) : 2000;
        int seed = args.Length > 1 ? int.Parse(args[1]) : 1;
        string root = FindRoot();
        var seeds = Directory.GetFiles(Path.Combine(root, "examples"), "*.fg")
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => (Name: Path.GetFileNameWithoutExtension(p), Src: File.ReadAllText(p)))
            .ToList();
        string crashDir = Path.Combine(root, "tests", "fuzz", "crashes");
        var rng = new Random(seed);
        var seen = new HashSet<string>();   // one saved input per distinct bug signature
        int bugs = 0, clean = 0, rejected = 0;

        // Hand-written stress inputs first, then random mutations.
        var fixedCases = new List<(string Name, string Src)> {
            ("empty", ""),
            ("only-module", "module m;"),
            ("deep-parens", "module m; void Main() { long x = " + new string('(', 5000) + "1" + new string(')', 5000) + "; }"),
            ("deep-blocks", "module m; void Main() { " + string.Concat(Enumerable.Repeat("if (true) { ", 3000)) + string.Concat(Enumerable.Repeat("} ", 3000)) + "}"),
            ("long-chain", "module m; void Main() { long x = 1" + string.Concat(Enumerable.Repeat(" + 1", 20000)) + "; Console.WriteLine(x); }"),
            ("huge-int", "module m; void Main() { long x = 999999999999999999999999; }"),
            ("nul-bytes", "module m;\0\0 void Main() { }"),
            ("unterminated-string", "module m; void Main() { Console.WriteLine(\"abc); }"),
            ("unterminated-interp", "module m; void Main() { Console.WriteLine($\"{1 + ); }"),
            ("unterminated-comment", "module m; void Main() { /* never ends"),
        };

        for (int i = 0; i < fixedCases.Count + iterations; i++) {
            string name, src;
            if (i < fixedCases.Count) (name, src) = fixedCases[i];
            else {
                var s = seeds[rng.Next(seeds.Count)];
                name = $"{s.Name}-{i}";
                src = Mutate(s.Src, rng);
            }
            foreach (var target in new[] { Target.Windows, Target.Linux, Target.Kernel }) {
                foreach (bool opt in new[] { true, false }) {
                    var (outcome, detail) = CompileOnce(src, target, opt);
                    if (outcome == Outcome.Ok) clean++;
                    else if (outcome == Outcome.Rejected) rejected++;
                    else {
                        string sig = outcome + ":" + detail;
                        if (!seen.Add(sig)) continue;
                        bugs++;
                        Directory.CreateDirectory(crashDir);
                        string file = Path.Combine(crashDir, $"{bugs:D3}-{name}.fg");
                        File.WriteAllText(file, $"// fuzz: {outcome} target={target} opt={opt}\n// {detail.Replace("\n", "\n// ")}\n{src}");
                        Console.WriteLine($"BUG  {outcome,-7} {target,-7} {(opt ? "-O2" : "-O0")}  {Path.GetFileName(file)}");
                        Console.WriteLine($"     {detail.Split('\n')[0]}");
                    }
                }
            }
            if (i > 0 && i % 500 == 0) Console.WriteLine($"  ... {i} inputs");
        }
        Console.WriteLine($"fuzz: seed {seed}, {fixedCases.Count + iterations} inputs, {clean} compiled, {rejected} rejected with an error, {bugs} compiler bug(s)");
        return bugs == 0 ? 0 : 1;
    }

    enum Outcome { Ok, Rejected, Crash, Hang }

    static (Outcome, string) CompileOnce(string src, Target target, bool opt) {
        Outcome outcome = Outcome.Hang;
        string detail = "compile did not finish in " + TimeoutMs + " ms";
        var t = new Thread(() => {
            try {
                var mod = new Parser(new Lexer(src).Lex()).ParseModule();
                var cg = new CodeGen(mod, target) { OptimizeEnabled = opt };
                cg.LowerToFile();
                outcome = Outcome.Ok; detail = "";
            } catch (ForgeException) { outcome = Outcome.Rejected; detail = ""; }
            catch (ForgeErrors) { outcome = Outcome.Rejected; detail = ""; }
            catch (Exception ex) {
                outcome = Outcome.Crash;
                // Signature: exception type + the first compiler frame, so one bug is saved once.
                string frame = (ex.StackTrace ?? "").Split('\n').Select(l => l.Trim())
                    .FirstOrDefault(l => l.StartsWith("at ForgeC.")) ?? "";
                frame = Regex.Replace(frame, @" in .*$", "");
                detail = $"{ex.GetType().Name}: {ex.Message}\n{frame}";
            }
        }, 256 * 1024 * 1024) { IsBackground = true };
        t.Start();
        t.Join(TimeoutMs);
        return (outcome, detail);
    }

    static string Mutate(string src, Random rng) {
        var c = Chunk.Matches(src).Select(m => m.Value).ToList();
        int edits = 1 + rng.Next(4);
        for (int e = 0; e < edits && c.Count > 0; e++) {
            int k = rng.Next(c.Count);
            switch (rng.Next(8)) {
                case 0: c.RemoveAt(k); break;                                       // delete
                case 1: c.Insert(k, c[rng.Next(c.Count)]); break;                   // duplicate another token here
                case 2: if (k + 1 < c.Count) (c[k], c[k + 1]) = (c[k + 1], c[k]); break; // swap neighbours
                case 3: c.Insert(k, " " + Inserts[rng.Next(Inserts.Length)] + " "); break;
                case 4: c[k] = Inserts[rng.Next(Inserts.Length)]; break;            // replace
                case 5: c.RemoveRange(k, c.Count - k); break;                       // truncate
                case 6: if (Regex.IsMatch(c[k], @"^\d+$")) c[k] = rng.Next(3) switch { 0 => "0", 1 => "9223372036854775807", _ => "18446744073709551616" }; break;
                case 7: { int n = rng.Next(1, c.Count - k + 1); c.InsertRange(k, c.GetRange(k, Math.Min(n, 40))); break; } // repeat a run
            }
        }
        return string.Concat(c);
    }

    // --time <file.fg>: how long each compiler phase takes (for slow-compile bugs).
    static int TimePhases(string src) {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        void Lap(string phase) { Console.WriteLine($"{phase,-12} {sw.ElapsedMilliseconds,6} ms"); sw.Restart(); }
        var toks = new Lexer(src).Lex(); Lap("lex");
        var mod = new Parser(toks).ParseModule(); Lap("parse");
        new CodeGen(mod, Target.Windows).Analyze(); Lap("analyze");
        foreach (bool opt in new[] { false, true }) {
            mod = new Parser(new Lexer(src).Lex()).ParseModule(); sw.Restart();
            new CodeGen(mod, Target.Windows) { OptimizeEnabled = opt }.LowerToFile(); Lap(opt ? "build -O2" : "build -O0");
        }
        return 0;
    }

    static string FindRoot() {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "ForgeC.csproj"))) d = d.Parent;
        return d?.FullName ?? throw new Exception("cannot find the repo root (ForgeC.csproj)");
    }
}
