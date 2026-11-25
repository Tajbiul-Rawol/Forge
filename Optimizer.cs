using System;
using System.Collections.Generic;
using System.Linq;

namespace ForgeC;

// AST-level optimizer. Runs after type checking, before code generation, on every
// ordinary function (not async functions, not the compiler's async runtime).
//
//   1. Tail-recursion elimination with an accumulator:
//        return f(a);          ->  a becomes the new params; jump to the top
//        return L + f(a);      ->  acc += L; params = a; jump to the top
//      Every other `return E` becomes `return acc + E`. (Integer addition wraps
//      mod 2^64, so it is associative and the rewrite is exact.)
//   2. Inlining: calls to small functions are replaced by the callee's body. A
//      self-recursive call is unrolled two levels deep (two inlining rounds).
//   3. Partial inlining: when a callee starts with `if (cond) return value;`
//      (cond/value pure, params only), that test runs at the call site and the
//      call is made only when it fails.
//   4. Constant folding: c1 op c2, and (x +/- c1) +/- c2 -> x +/- c.
//
// The register allocator and instruction selection that consume this live in
// CodeGen.cs (EmitFunc, EmitJump, EmitBinary).
sealed partial class CodeGen {
    public bool OptimizeEnabled = true;

    int _optId;
    readonly Dictionary<string, List<Stmt>> _orig = new();                    // pre-optimization bodies
    readonly Dictionary<string, Dictionary<string, string>> _localTypes = new(); // per function: local -> type

    const int InlineMaxNodes = 48;     // callee size limit for full inlining
    const int InlineMaxPerFunc = 12;   // full inlines per caller per round (code-growth cap)
    const int InlineRounds = 2;        // so a recursive call is unrolled two levels deep

    static bool Optimizable(Func f) => !f.IsAsync && !f.IsRuntime;

    void Optimize() {
        foreach (var f in _mod.Funcs) {
            _orig[f.Name] = new AstCopier().Copy(f.Body);
            _cur = f; SetupLocalsForCheck(f);
            _localTypes[f.Name] = _locals.ToDictionary(kv => kv.Key, kv => kv.Value.Type);
        }
        foreach (var f in _mod.Funcs) {
            if (!Optimizable(f)) continue;
            _cur = f;
            var body = f.Body;
            if (f.Throws == null) body = TailRecursion(f, body);
            for (int round = 0; round < InlineRounds; round++) {   // round 2 inlines inside round-1 copies
                body = InlinePass(f, body);
                body = new AstCopier { PostExpr = Fold }.Copy(body);
            }
            body = PartialInlinePass(f, body);
            body = new AstCopier { PostExpr = Fold }.Copy(body);
            body = HoistLengths(f, body);
            f.Body = body;
        }
    }

    // ------------------------------------------------------------ loop-invariant lengths
    // for (...; i < s.length; ...)  re-evaluates s.length (strlen for a string) on every
    // iteration. When the loop cannot change it, compute it once before the loop.
    // Arrays never change length; a string's bytes could change only through a pointer
    // store or a call, so for strings the loop must contain neither.
    static readonly HashSet<string> NoWriteBuiltins = new() { "putc", "str", "toint", "substr", "strlen", "inb", "inw", "inl", "outb", "outw", "outl", "pause" };
    List<Stmt> HoistLengths(Func f, List<Stmt> body) {
        var types = new Dictionary<string, string>(_localTypes[f.Name]);
        foreach (var (t, n) in f.ExtraLocals) types.TryAdd(n, t);
        var addr = AddrTaken(body);
        var outp = new List<Stmt>();
        foreach (var st in body) {
            switch (st) {
                case IfStmt i: i.Then = HoistLengths(f, i.Then); if (i.Else != null) i.Else = HoistLengths(f, i.Else); break;
                case WhileStmt w: w.Body = HoistLengths(f, w.Body); w.Cond = HoistIn(f, types, addr, w.Cond, new List<Stmt>(w.Body), null, outp); break;
                case ForStmt fs:
                    fs.Body = HoistLengths(f, fs.Body);
                    if (fs.Cond != null) {
                        var loop = new List<Stmt>(fs.Body); if (fs.Step != null) loop.Add(fs.Step);
                        if (fs.Init != null) { outp.Add(fs.Init); fs.Init = null; }   // init runs first either way
                        fs.Cond = HoistIn(f, types, addr, fs.Cond, loop, null, outp);
                    }
                    break;
            }
            outp.Add(st);
        }
        return outp;
    }
    Expr HoistIn(Func f, Dictionary<string, string> types, HashSet<string> addr, Expr cond, List<Stmt> loop, Expr? _, List<Stmt> before) {
        var assigned = new HashSet<string>();
        bool writesMemory = false;
        Walker.Walk(loop, e => {
            if (e is CallExpr c && !NoWriteBuiltins.Contains(c.Name)) writesMemory = true;
            if (e is SpawnExpr or AwaitExpr or WhenAllExpr or YieldExpr) writesMemory = true;
        }, s => {
            if (s is Assign a) assigned.Add(a.Name);
            else if (s is VarDecl v) assigned.Add(v.Name);
            else if (s is IndexAssign or DerefAssign or AsmStmt) writesMemory = true;
        });
        Walker.WalkExpr(cond, e => { if (e is CallExpr c && !NoWriteBuiltins.Contains(c.Name)) writesMemory = true; },
                        s => { if (s is Assign a) assigned.Add(a.Name); });
        var hoisted = new Dictionary<string, string>();
        Expr Rewrite(Expr e) {
            if (e is LengthExpr { Arr: VarRef v } && types.TryGetValue(v.Name, out var t) && !assigned.Contains(v.Name) && !addr.Contains(v.Name)
                && (t.EndsWith("[]") || (t == "string" && !writesMemory))) {
                if (!hoisted.TryGetValue(v.Name, out var tn)) {
                    tn = $"{v.Name}$len{++_optId}";
                    f.ExtraLocals.Add(("int", tn));
                    before.Add(new Assign { Line = e.Line, Name = tn, E = new LengthExpr { Line = e.Line, Arr = new VarRef { Line = e.Line, Name = v.Name } } });
                    hoisted[v.Name] = tn;
                }
                return new VarRef { Line = e.Line, Name = tn };
            }
            return e;
        }
        return new AstCopier { PostExpr = Rewrite }.CopyExpr(cond);
    }

    // ------------------------------------------------------------ helpers
    static int Size(List<Stmt> body) { int n = 0; Walker.Walk(body, _ => n++, _ => n++); return n; }

    static HashSet<string> AddrTaken(List<Stmt> body) {
        var set = new HashSet<string>();
        Walker.Walk(body, e => { if (e is AddrOf a) set.Add(a.Name); }, null);
        return set;
    }

    // Names written anywhere in the body (assignment, declaration, for-init, address-of).
    static HashSet<string> Written(List<Stmt> body) {
        var set = new HashSet<string>();
        Walker.Walk(body, e => { if (e is AddrOf a) set.Add(a.Name); },
            s => { if (s is Assign a) set.Add(a.Name); else if (s is VarDecl v) set.Add(v.Name); });
        return set;
    }

    static HashSet<string> NamesRead(Expr e) {
        var set = new HashSet<string>();
        Walker.WalkExpr(e, x => { if (x is VarRef v) set.Add(v.Name); }, null);
        return set;
    }

    // No calls, no memory effects: literals, variables, arithmetic, casts.
    static bool IsPure(Expr e) => e switch {
        IntLit or BoolLit or VarRef => true,
        Unary u => IsPure(u.E),
        Binary b => IsPure(b.L) && IsPure(b.R) && b.Op is not ("/" or "%"),   // division can fault
        CastExpr c => IsPure(c.E),
        _ => false,
    };

    // Callee bodies we are willing to copy into another function.
    static bool InlineSafe(List<Stmt> body) {
        bool ok = true;
        Walker.Walk(body,
            e => { if (e is SpawnExpr or TryExpr or AwaitExpr or YieldExpr or WhenAllExpr or Interp or AddrOf or InlineExpr) ok = false; },
            s => { if (s is ThrowStmt or LabelStmt or GotoStmt or AsmStmt) ok = false; });
        return ok;
    }

    // Can this argument be substituted for the parameter instead of copied into a
    // temporary? Only cheap, pure expressions over variables that cannot change.
    bool Substitutable(Expr arg, HashSet<string> callerAddr, int uses) => arg switch {
        IntLit or BoolLit => true,
        VarRef v => !callerAddr.Contains(v.Name),
        Binary { Op: "+" or "-" } b when uses <= 4 =>
            (b.L is VarRef lv && !callerAddr.Contains(lv.Name) && b.R is IntLit) ||
            (b.R is VarRef rv && !callerAddr.Contains(rv.Name) && b.L is IntLit && b.Op == "+"),
        _ => false,
    };

    static int Uses(List<Stmt> body, string name) {
        int n = 0;
        Walker.Walk(body, e => { if (e is VarRef v && v.Name == name) n++; }, null);
        return n;
    }

    // ------------------------------------------------------------ 1. tail recursion
    List<Stmt> TailRecursion(Func f, List<Stmt> body) {
        if (!IsInt(f.Ret) || AddrTaken(body).Count > 0) return body;
        int kind(Expr? e) => e switch {
            CallExpr c when c.Name == f.Name => 1,
            Binary { Op: "+", R: CallExpr c } when c.Name == f.Name => 2,
            Binary { Op: "+", L: CallExpr c, R: IntLit or VarRef } when c.Name == f.Name => 3,
            _ => 0,
        };
        bool any = false, acc = false;
        Walker.Walk(body, null, s => { if (s is RetStmt r) { int k = kind(r.E); if (k > 0) any = true; if (k > 1) acc = true; } });
        if (!any) return body;

        int id = ++_optId;
        string top = $".tre{id}", accName = $"acc${id}";
        if (acc) f.ExtraLocals.Add((f.Ret, accName));
        VarRef A() => new VarRef { Name = accName };

        List<Stmt> rebind(List<Expr> args, int line) {
            var outp = new List<Stmt>();
            var pairs = new List<(string P, string T, Expr A)>();
            for (int i = 0; i < args.Count; i++) {
                var p = f.Params[i].Name;
                if (args[i] is VarRef v && v.Name == p) continue;   // unchanged
                pairs.Add((p, f.Params[i].Type, args[i]));
            }
            // sequential assignment is safe if no later arg reads an earlier-updated param
            bool seqOk = true;
            for (int i = 0; i < pairs.Count; i++)
                for (int k = 0; k < i; k++) if (NamesRead(pairs[i].A).Contains(pairs[k].P)) seqOk = false;
            if (seqOk) foreach (var (p, _, a) in pairs) outp.Add(new Assign { Name = p, E = a, Line = line });
            else {
                var temps = new List<string>();
                foreach (var (p, t, a) in pairs) {
                    var tn = $"{p}$t{++_optId}"; f.ExtraLocals.Add((t, tn)); temps.Add(tn);
                    outp.Add(new Assign { Name = tn, E = a, Line = line });
                }
                for (int i = 0; i < pairs.Count; i++) outp.Add(new Assign { Name = pairs[i].P, E = new VarRef { Name = temps[i] }, Line = line });
            }
            outp.Add(new GotoStmt { Name = top, Line = line });
            return outp;
        }

        var cp = new AstCopier {
            PostStmt = s => {
                if (s is not RetStmt r) return new() { s };
                switch (kind(r.E)) {
                    case 1: return rebind(((CallExpr)r.E!).Args, r.Line);
                    case 2: {
                        var b = (Binary)r.E!;
                        var l = new List<Stmt> { new Assign { Name = accName, E = new Binary { Op = "+", L = A(), R = b.L, Line = r.Line }, Line = r.Line } };
                        l.AddRange(rebind(((CallExpr)b.R).Args, r.Line)); return l;
                    }
                    case 3: {
                        var b = (Binary)r.E!;
                        var l = new List<Stmt> { new Assign { Name = accName, E = new Binary { Op = "+", L = A(), R = b.R, Line = r.Line }, Line = r.Line } };
                        l.AddRange(rebind(((CallExpr)b.L).Args, r.Line)); return l;
                    }
                    default:
                        if (acc) r.E = new Binary { Op = "+", L = A(), R = r.E!, Line = r.Line };
                        return new() { r };
                }
            }
        };
        var nb = new List<Stmt>();
        if (acc) nb.Add(new Assign { Name = accName, E = new IntLit { Val = 0 } });
        nb.Add(new LabelStmt { Name = top });
        nb.AddRange(cp.Copy(body));
        if (acc) nb.Add(new RetStmt { E = A() });
        return nb;
    }

    // ------------------------------------------------------------ 2. inlining
    List<Stmt> InlinePass(Func f, List<Stmt> body) {
        int budget = InlineMaxPerFunc;
        var callerAddr = AddrTaken(body);
        var cp = new AstCopier {
            PostExpr = e => {
                if (e is not CallExpr c || budget <= 0 || !_funcs.TryGetValue(c.Name, out var g)) return e;
                if (!Optimizable(g) || g.Throws != null || g.Name == "Main") return e;
                var gb = _orig[g.Name];
                if (Size(gb) > InlineMaxNodes || !InlineSafe(gb)) return e;
                budget--;
                return InlineCall(f, c, g, gb, callerAddr);
            }
        };
        return cp.Copy(body);
    }

    InlineExpr InlineCall(Func f, CallExpr c, Func g, List<Stmt> gb, HashSet<string> callerAddr) {
        int id = ++_optId;
        string end = $".inl{id}";
        var written = Written(gb);
        var subst = new Dictionary<string, Expr>();
        var rename = new Dictionary<string, string>();
        var pre = new List<Stmt>();
        for (int i = 0; i < g.Params.Count; i++) {
            var (pt, pn) = g.Params[i];
            if (!written.Contains(pn) && Substitutable(c.Args[i], callerAddr, Uses(gb, pn))) { subst[pn] = c.Args[i]; continue; }
            var tn = $"{pn}${id}"; rename[pn] = tn; f.ExtraLocals.Add((pt, tn));
            pre.Add(new Assign { Name = tn, E = c.Args[i], Line = c.Line });
        }
        var paramNames = g.Params.Select(p => p.Name).ToHashSet();
        foreach (var (ln, lt) in _localTypes[g.Name]) {
            if (paramNames.Contains(ln)) continue;
            var tn = $"{ln}${id}"; rename[ln] = tn; f.ExtraLocals.Add((lt, tn));
        }
        var cp = new AstCopier {
            Rename = n => rename.TryGetValue(n, out var r) ? r : n,
            Subst = subst,
            PostStmt = s => s is RetStmt r ? new() { new InlineRet { E = r.E, End = end, Line = r.Line } } : new() { s },
        };
        var bodyOut = new List<Stmt>(pre);
        bodyOut.AddRange(cp.Copy(gb));
        if (bodyOut.Count == 0 || bodyOut[^1] is not InlineRet)
            bodyOut.Add(new InlineRet { E = g.Ret == "void" ? null : new IntLit { Val = 0 }, End = end });
        return new InlineExpr { Body = bodyOut, End = end, ResultType = g.Ret, Line = c.Line };
    }

    // ------------------------------------------------------------ 3. partial inlining
    List<Stmt> PartialInlinePass(Func f, List<Stmt> body) {
        var callerAddr = AddrTaken(body);
        var cp = new AstCopier {
            PostExpr = e => {
                if (e is not CallExpr c || c.NoInline || !_funcs.TryGetValue(c.Name, out var g)) return e;
                if (!Optimizable(g) || g.Throws != null || g.Ret == "void") return e;
                var gb = _orig[g.Name];
                if (gb.Count < 2 || gb[0] is not IfStmt { Else: null } guard) return e;
                if (guard.Then.Count != 1 || guard.Then[0] is not RetStmt { E: not null } gr) return e;
                var pnames = g.Params.Select(p => p.Name).ToHashSet();
                if (!IsPure(guard.Cond) || !IsPure(gr.E!)) return e;
                if (!NamesRead(guard.Cond).IsSubsetOf(pnames) || !NamesRead(gr.E!).IsSubsetOf(pnames)) return e;

                int id = ++_optId;
                string end = $".pin{id}";
                var subst = new Dictionary<string, Expr>();
                var pre = new List<Stmt>();
                var callArgs = new List<Expr>();
                var guardOnly = new List<Stmt> { guard };
                for (int i = 0; i < g.Params.Count; i++) {
                    var (pt, pn) = g.Params[i];
                    if (Substitutable(c.Args[i], callerAddr, Uses(guardOnly, pn) + 1)) {
                        subst[pn] = c.Args[i];
                        callArgs.Add(new AstCopier().CopyExpr(c.Args[i]));
                    } else {
                        var tn = $"{pn}$p{id}"; f.ExtraLocals.Add((pt, tn));
                        pre.Add(new Assign { Name = tn, E = c.Args[i], Line = c.Line });
                        subst[pn] = new VarRef { Name = tn };
                        callArgs.Add(new VarRef { Name = tn });
                    }
                }
                var sc = new AstCopier { Subst = subst };
                var outBody = new List<Stmt>(pre) {
                    new IfStmt { Cond = sc.CopyExpr(guard.Cond), Line = c.Line,
                                 Then = new() { new InlineRet { E = sc.CopyExpr(gr.E!), End = end } } },
                    new InlineRet { E = new CallExpr { Name = c.Name, Args = callArgs, NoInline = true, Line = c.Line }, End = end },
                };
                return new InlineExpr { Body = outBody, End = end, ResultType = g.Ret, Line = c.Line };
            }
        };
        return cp.Copy(body);
    }

    // ------------------------------------------------------------ 4. constant folding
    static Expr Fold(Expr e) {
        if (e is not Binary b) return e;
        if (b.L is IntLit l && b.R is IntLit r) {
            long a = l.Val, c = r.Val;
            switch (b.Op) {
                case "+": return new IntLit { Val = unchecked(a + c), Line = b.Line };
                case "-": return new IntLit { Val = unchecked(a - c), Line = b.Line };
                case "*": return new IntLit { Val = unchecked(a * c), Line = b.Line };
                case "&": return new IntLit { Val = a & c, Line = b.Line };
                case "|": return new IntLit { Val = a | c, Line = b.Line };
                case "^": return new IntLit { Val = a ^ c, Line = b.Line };
                case "<": return new BoolLit { Val = a < c, Line = b.Line };
                case ">": return new BoolLit { Val = a > c, Line = b.Line };
                case "<=": return new BoolLit { Val = a <= c, Line = b.Line };
                case ">=": return new BoolLit { Val = a >= c, Line = b.Line };
                case "==": return new BoolLit { Val = a == c, Line = b.Line };
                case "!=": return new BoolLit { Val = a != c, Line = b.Line };
            }
        }
        // (x +/- c1) +/- c2  ->  x + (+/-c1 +/- c2)
        if (b.Op is "+" or "-" && b.R is IntLit c2 && b.L is Binary { Op: "+" or "-", R: IntLit c1 } inner) {
            long k = unchecked((inner.Op == "+" ? c1.Val : -c1.Val) + (b.Op == "+" ? c2.Val : -c2.Val));
            if (k == 0) return inner.L;
            return k > 0 ? new Binary { Op = "+", L = inner.L, R = new IntLit { Val = k }, Line = b.Line }
                         : new Binary { Op = "-", L = inner.L, R = new IntLit { Val = -k }, Line = b.Line };
        }
        return e;
    }
}

// Read-only traversal of statements and expressions (including inlined bodies).
static class Walker {
    public static void Walk(List<Stmt> body, Action<Expr>? onExpr, Action<Stmt>? onStmt) {
        foreach (var s in body) WalkStmt(s, onExpr, onStmt);
    }
    public static void WalkStmt(Stmt s, Action<Expr>? fe, Action<Stmt>? fs) {
        fs?.Invoke(s);
        switch (s) {
            case VarDecl v: WalkExpr(v.E, fe, fs); break;
            case Assign a: WalkExpr(a.E, fe, fs); break;
            case IndexAssign ia: WalkExpr(ia.Arr, fe, fs); WalkExpr(ia.Index, fe, fs); WalkExpr(ia.E, fe, fs); break;
            case DerefAssign da: WalkExpr(da.Ptr, fe, fs); WalkExpr(da.E, fe, fs); break;
            case IfStmt i: WalkExpr(i.Cond, fe, fs); Walk(i.Then, fe, fs); if (i.Else != null) Walk(i.Else, fe, fs); break;
            case WhileStmt w: WalkExpr(w.Cond, fe, fs); Walk(w.Body, fe, fs); break;
            case ForStmt f:
                if (f.Init != null) WalkStmt(f.Init, fe, fs);
                if (f.Cond != null) WalkExpr(f.Cond, fe, fs);
                if (f.Step != null) WalkStmt(f.Step, fe, fs);
                Walk(f.Body, fe, fs); break;
            case RetStmt r: if (r.E != null) WalkExpr(r.E, fe, fs); break;
            case InlineRet ir: if (ir.E != null) WalkExpr(ir.E, fe, fs); break;
            case ExprStmt e: WalkExpr(e.E, fe, fs); break;
            case PrintStmt p: WalkExpr(p.Arg, fe, fs); break;
            case MemberAssign ma: WalkExpr(ma.Obj, fe, fs); WalkExpr(ma.E, fe, fs); break;   // before lowering
        }
    }
    public static void WalkExpr(Expr? e, Action<Expr>? fe, Action<Stmt>? fs) {
        if (e == null) return;
        fe?.Invoke(e);
        switch (e) {
            case Unary u: WalkExpr(u.E, fe, fs); break;
            case Binary b: WalkExpr(b.L, fe, fs); WalkExpr(b.R, fe, fs); break;
            case CallExpr c: foreach (var a in c.Args) WalkExpr(a, fe, fs); break;
            case TryExpr t: WalkExpr(t.Call, fe, fs); WalkExpr(t.Else, fe, fs); break;
            case NewArray na: WalkExpr(na.Size, fe, fs); break;
            case NewStruct ns: foreach (var (_, ie) in ns.Inits) WalkExpr(ie, fe, fs); break;
            case IndexExpr ix: WalkExpr(ix.Arr, fe, fs); WalkExpr(ix.Index, fe, fs); break;
            case LengthExpr le: WalkExpr(le.Arr, fe, fs); break;
            case Deref de: WalkExpr(de.Ptr, fe, fs); break;
            case CastExpr ce: WalkExpr(ce.E, fe, fs); break;
            case SpawnExpr sp: if (sp.Arg != null) WalkExpr(sp.Arg, fe, fs); break;
            case AwaitExpr aw: WalkExpr(aw.Task, fe, fs); break;
            case WhenAllExpr wa: foreach (var t in wa.Tasks) WalkExpr(t, fe, fs); break;
            case Interp itp: foreach (var part in itp.Parts) if (part is ValueTuple<Expr, string?> tup) WalkExpr(tup.Item1, fe, fs); break;
            case InlineExpr ie: Walk(ie.Body, fe, fs); break;
            case MemberExpr me: WalkExpr(me.Obj, fe, fs); break;          // before lowering
            case IncDecExpr id: WalkExpr(id.Target, fe, fs); break;
        }
    }
}

// Deep copy of an AST, with optional variable renaming, parameter substitution
// (each use gets its own copy of the substituted expression) and rewrite hooks.
sealed class AstCopier {
    public Func<string, string>? Rename;
    public Dictionary<string, Expr>? Subst;
    public Func<Expr, Expr>? PostExpr;           // applied bottom-up to every copied expression
    public Func<Stmt, List<Stmt>>? PostStmt;     // applied to every copied statement in a list

    string N(string n) => Rename != null ? Rename(n) : n;

    public List<Stmt> Copy(List<Stmt> body) {
        var outp = new List<Stmt>(body.Count);
        foreach (var s in body) {
            var c = CopyStmt(s);
            if (PostStmt != null) outp.AddRange(PostStmt(c)); else outp.Add(c);
        }
        return outp;
    }

    public Stmt CopyStmt(Stmt s) {
        Stmt r = s switch {
            VarDecl v => new VarDecl { DeclType = v.DeclType, Name = N(v.Name), E = CopyExpr(v.E) },
            Assign a => new Assign { Name = N(a.Name), E = CopyExpr(a.E) },
            IndexAssign ia => new IndexAssign { Arr = CopyExpr(ia.Arr), Index = CopyExpr(ia.Index), E = CopyExpr(ia.E) },
            DerefAssign da => new DerefAssign { Ptr = CopyExpr(da.Ptr), E = CopyExpr(da.E) },
            IfStmt i => new IfStmt { Cond = CopyExpr(i.Cond), Then = Copy(i.Then), Else = i.Else == null ? null : Copy(i.Else) },
            WhileStmt w => new WhileStmt { Cond = CopyExpr(w.Cond), Body = Copy(w.Body) },
            ForStmt f => new ForStmt { Init = f.Init == null ? null : CopyStmt(f.Init), Cond = f.Cond == null ? null : CopyExpr(f.Cond),
                                       Step = f.Step == null ? null : CopyStmt(f.Step), Body = Copy(f.Body) },
            RetStmt rt => new RetStmt { E = rt.E == null ? null : CopyExpr(rt.E) },
            InlineRet ir => new InlineRet { E = ir.E == null ? null : CopyExpr(ir.E), End = ir.End },
            ThrowStmt th => new ThrowStmt { Err = th.Err, Msg = th.Msg },
            ExprStmt es => new ExprStmt { E = CopyExpr(es.E) },
            PrintStmt p => new PrintStmt { Arg = CopyExpr(p.Arg) },
            LabelStmt l => new LabelStmt { Name = l.Name },
            GotoStmt g => new GotoStmt { Name = g.Name },
            AsmStmt a => new AsmStmt { Text = System.Text.RegularExpressions.Regex.Replace(a.Text, @"\{\s*(\w+)\s*\}", m => "{" + N(m.Groups[1].Value) + "}") },
            _ => throw new InvalidOperationException("optimizer: unknown statement " + s.GetType().Name),
        };
        r.Line = s.Line;
        return r;
    }

    public Expr CopyExpr(Expr e) {
        if (e is VarRef sv && Subst != null && Subst.TryGetValue(sv.Name, out var sub))
            return new AstCopier().CopyExpr(sub);              // fresh copy, no renaming
        Expr r = e switch {
            IntLit i => new IntLit { Val = i.Val },
            FloatLit fl => new FloatLit { Val = fl.Val },
            StrLit s => new StrLit { Val = s.Val },
            BoolLit b => new BoolLit { Val = b.Val },
            VarRef v => new VarRef { Name = N(v.Name) },
            Unary u => new Unary { Op = u.Op, E = CopyExpr(u.E) },
            Binary b => new Binary { Op = b.Op, L = CopyExpr(b.L), R = CopyExpr(b.R) },
            CallExpr c => new CallExpr { Name = c.Name, Args = c.Args.Select(CopyExpr).ToList(), NoInline = c.NoInline },
            TryExpr t => new TryExpr { Call = (CallExpr)CopyCallNoHook(t.Call), Else = t.Else == null ? null : CopyExpr(t.Else) },
            Interp itp => new Interp { Parts = itp.Parts.Select(p => p is ValueTuple<Expr, string?> tup ? (object)(CopyExpr(tup.Item1), tup.Item2) : p).ToList() },
            NewArray na => new NewArray { ElemType = na.ElemType, Size = CopyExpr(na.Size) },
            NewStruct ns => new NewStruct { Type = ns.Type, Inits = ns.Inits.Select(i => (i.Name, CopyExpr(i.E))).ToList() },
            NullLit => new NullLit(),
            IndexExpr ix => new IndexExpr { Arr = CopyExpr(ix.Arr), Index = CopyExpr(ix.Index) },
            LengthExpr le => new LengthExpr { Arr = CopyExpr(le.Arr) },
            AddrOf ao => new AddrOf { Name = N(ao.Name) },
            Deref de => new Deref { Ptr = CopyExpr(de.Ptr) },
            CastExpr ce => new CastExpr { TargetType = ce.TargetType, E = CopyExpr(ce.E) },
            SpawnExpr sp => new SpawnExpr { FuncName = sp.FuncName, Arg = sp.Arg == null ? null : CopyExpr(sp.Arg) },
            AwaitExpr aw => new AwaitExpr { Task = CopyExpr(aw.Task) },
            YieldExpr => new YieldExpr(),
            WhenAllExpr wa => new WhenAllExpr { Tasks = wa.Tasks.Select(CopyExpr).ToList() },
            InlineExpr ie => new InlineExpr { Body = Copy(ie.Body), End = ie.End, ResultType = ie.ResultType },
            _ => throw new InvalidOperationException("optimizer: unknown expression " + e.GetType().Name),
        };
        r.Line = e.Line;
        return PostExpr != null ? PostExpr(r) : r;
    }

    // a try-call must stay a plain call (it carries the error protocol)
    Expr CopyCallNoHook(CallExpr c) => new CallExpr { Name = c.Name, Args = c.Args.Select(CopyExpr).ToList(), NoInline = true, Line = c.Line };
}
