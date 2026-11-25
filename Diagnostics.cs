using System;
using System.Collections.Generic;
using System.Linq;

namespace ForgeC;

enum Severity { Error, Warning }

readonly struct Diagnostic {
    public readonly Severity Severity;
    public readonly int Line;
    public readonly string Message;
    public Diagnostic(Severity sev, int line, string msg) { Severity = sev; Line = line; Message = msg; }
}

// Suggestion helpers: "did you mean X?" via edit distance, and completion ranking.
static class Suggest {
    public static int Levenshtein(string a, string b) {
        int n = a.Length, m = b.Length;
        if (n == 0) return m;
        if (m == 0) return n;
        var prev = new int[m + 1];
        var cur = new int[m + 1];
        for (int j = 0; j <= m; j++) prev[j] = j;
        for (int i = 1; i <= n; i++) {
            cur[0] = i;
            for (int j = 1; j <= m; j++) {
                int cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[m];
    }

    // Closest candidate within a sensible distance threshold, or null.
    public static string? Closest(string target, IEnumerable<string> candidates) {
        string? best = null;
        int bestD = int.MaxValue;
        int threshold = Math.Max(2, target.Length / 2);
        foreach (var c in candidates) {
            int d = Levenshtein(target, c);
            if (d < bestD) { bestD = d; best = c; }
        }
        return (best != null && bestD <= threshold) ? best : null;
    }

    // Formatted " (did you mean 'X'?)" or "" when nothing is close.
    public static string Hint(string target, IEnumerable<string> candidates) {
        var c = Closest(target, candidates);
        return c == null ? "" : $" (did you mean '{c}'?)";
    }

    // Completion candidates: prefix matches first (alphabetical), then near matches.
    public static List<string> Complete(string prefix, IEnumerable<string> candidates) {
        var all = candidates.Distinct().ToList();
        if (prefix.Length == 0) return all.OrderBy(x => x).ToList();
        var pre = all.Where(c => c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(c => c).ToList();
        var near = all.Where(c => !pre.Contains(c) && Levenshtein(prefix, c) <= Math.Max(2, prefix.Length / 2))
                      .OrderBy(c => Levenshtein(prefix, c)).ToList();
        pre.AddRange(near);
        return pre;
    }
}
