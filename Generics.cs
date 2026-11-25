using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ForgeC;

// Generics by monomorphization (like C++ templates or Rust): every use of a generic
// struct or function with concrete type arguments gets its own copy of the code, named
// after them (List<int>, List<int>.Add, Max<double>). Later stages only ever see these
// ordinary, concrete declarations, so codegen, the optimizer and the register
// allocator are unchanged.
//
//   - generic structs are instantiated up front from the types written in the program
//     (EnsureType on every type string), together with their methods;
//   - generic functions are instantiated by the type checker when it meets a call,
//     with the type arguments written out (Max<int>(..)) or inferred from the
//     arguments (Max(3, 7)); an instance is then type checked like any function.
//
// A generic body is checked per instance (as with C++ templates): `a + b` in Max<T>
// is fine for Max<int> and an error, naming Max<string>, for Max<string>.
sealed partial class CodeGen {
    readonly Dictionary<string, Func> _genericFuncs = new();            // Max -> template
    readonly Dictionary<string, StructDecl> _genericStructDecls = new(); // List -> template
    readonly Dictionary<string, List<Func>> _genericMethods = new();    // List -> its method templates
    readonly HashSet<string> _instantiated = new();                     // List<int>, ... (also in progress)
    bool _registered;                                                   // past the constructor: register instances directly
    int _instanceSeq;
    const int MaxInstances = 2000;

    // Constructor step: take the templates out of the module, then instantiate every
    // generic struct type the remaining (concrete) code mentions.
    void ExtractGenerics(ModuleNode mod) {
        foreach (var sd in mod.Structs.Where(s => s.TypeParams != null).ToList()) {
            _genericStructDecls[sd.Name] = sd;
            _genericMethods[sd.Name] = new List<Func>();
            mod.Structs.Remove(sd);
        }
        foreach (var f in mod.Funcs.ToList()) {
            if (f.GenericOwner != null) { _genericMethods[f.GenericOwner].Add(f); mod.Funcs.Remove(f); }
            else if (f.TypeParams != null) {
                if (f.IsInterrupt || f.IsCallback) throw new ForgeException($"'{f.Name}' can't be generic (it is called from outside Forge)", f.Line);
                _genericFuncs[f.Name] = f; mod.Funcs.Remove(f);
            }
        }
        if (_genericStructDecls.Count == 0 && _genericFuncs.Count == 0) return;
        foreach (var sd in mod.Structs.ToList())
            foreach (var (t, _) in sd.Fields) EnsureType(t, sd.Line);
        foreach (var f in mod.Funcs.ToList()) EnsureTypesIn(f);
        foreach (var ex in mod.Externs) { EnsureType(ex.Ret, ex.Line); foreach (var (t, _) in ex.Params) EnsureType(t, ex.Line); }
    }

    // ---- type strings ---------------------------------------------------------------
    // Split "List<int>[]*" into head "List", args ["int"] and suffix "[]*".
    static bool SplitGeneric(string t, out string head, out List<string> args, out string suffix) {
        head = t; args = new(); suffix = "";
        int lt = t.IndexOf('<');
        if (lt < 0) {
            int s = t.IndexOfAny(new[] { '[', '*' });
            if (s >= 0) { head = t[..s]; suffix = t[s..]; }
            return false;
        }
        int depth = 0, close = -1;
        for (int k = lt; k < t.Length; k++) {
            if (t[k] == '<') depth++;
            else if (t[k] == '>' && --depth == 0) { close = k; break; }
        }
        if (close < 0) return false;
        head = t[..lt];
        suffix = t[(close + 1)..];
        args = SplitTopLevel(t[(lt + 1)..close]);
        return true;
    }

    static List<string> SplitTopLevel(string s) {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int k = 0; k < s.Length; k++) {
            if (s[k] == '<') depth++;
            else if (s[k] == '>') depth--;
            else if (s[k] == ',' && depth == 0) { parts.Add(s[start..k]); start = k + 1; }
        }
        parts.Add(s[start..]);
        return parts;
    }

    // Replace type parameters in a type string (T -> int, List<T> -> List<int>). Inside
    // a generic struct's methods its bare name means the instance (this: List -> List<int>).
    static string SubstType(string t, Dictionary<string, string> map, string? owner = null, string? inst = null) =>
        Regex.Replace(t, @"[A-Za-z_]\w*", m => {
            if (map.TryGetValue(m.Value, out var r)) return r;
            if (owner != null && m.Value == owner) {
                int after = m.Index + m.Length;
                if (after >= t.Length || t[after] != '<') return inst!;
            }
            return m.Value;
        });

    // Make sure every generic struct type mentioned in t exists (List<List<int>>, ...).
    void EnsureType(string t, int line) {
        if (string.IsNullOrEmpty(t)) return;
        if (!SplitGeneric(t, out var head, out var args, out _)) {
            if (_genericStructDecls.ContainsKey(head))
                throw new ForgeException($"'{head}' is generic: give it type arguments, e.g. {head}<{string.Join(", ", _genericStructDecls[head].TypeParams!.Select(_ => "int"))}>", line);
            return;
        }
        foreach (var a in args) EnsureType(a, line);
        if (_genericStructDecls.TryGetValue(head, out var sd)) InstantiateStruct(sd, args, line);
    }

    void EnsureTypesIn(Func f) {
        EnsureType(f.Ret, f.Line);
        foreach (var (t, _) in f.Params) EnsureType(t, f.Line);
        var scan = new GenericCloner(t => t, "") { OnType = (t, line) => EnsureType(t, line) };
        scan.CloneBody(f.Body);
    }

    string InstantiateStruct(StructDecl sd, List<string> args, int line) {
        var tps = sd.TypeParams!;
        if (args.Count != tps.Count)
            throw new ForgeException($"'{sd.Name}' takes {tps.Count} type argument(s), got {args.Count}", line);
        string inst = $"{sd.Name}<{string.Join(",", args)}>";
        if (!_instantiated.Add(inst)) return inst;
        if (_instantiated.Count > MaxInstances)
            throw new ForgeException($"too many generic instances (is a generic type defined in terms of a bigger version of itself, like Box<Box<T>>?)", line);
        var map = tps.Zip(args).ToDictionary(p => p.First, p => p.Second);
        var copy = new StructDecl {
            Line = sd.Line, Name = inst, Packed = sd.Packed,
            Fields = sd.Fields.Select(fl => (SubstType(fl.Type, map, sd.Name, inst), fl.Name)).ToList(),
        };
        var methods = _genericMethods[sd.Name].Select(m => CloneFunc(m, map, inst + m.Name[sd.Name.Length..], sd.Name, inst)).ToList();
        AddStruct(copy);                          // registered first: a field may be this very type (Node<T> next)
        foreach (var (t, _) in copy.Fields) EnsureType(t, sd.Line);
        if (_registered) LayoutStruct(_structs[inst]);
        foreach (var m in methods) { AddFunc(m); EnsureTypesIn(m); }
        return inst;
    }

    // A generic call: pick (or infer) the type arguments, instantiate, and point the
    // call at the instance. Called by TypeOf before the call is checked.
    void ResolveGenericCall(CallExpr c) {
        if (!_genericFuncs.TryGetValue(c.Name, out var gf))
            throw new ForgeException($"'{c.Name}' is not generic: remove the <...> type arguments", c.Line);
        var targs = c.TypeArgs ?? InferTypeArgs(gf, c);
        if (targs.Count != gf.TypeParams!.Count)
            throw new ForgeException($"'{gf.Name}' takes {gf.TypeParams.Count} type argument(s), got {targs.Count}", c.Line);
        foreach (var a in targs) {
            if (a == "void") throw new ForgeException($"'void' can't be a type argument of '{gf.Name}'", c.Line);
            if (!KnownType(a) && !SplitGeneric(a, out _, out _, out _)) throw new ForgeException($"unknown type '{a}' in '{gf.Name}<...>'", c.Line);
            EnsureType(a, c.Line);
        }
        string inst = $"{gf.Name}<{string.Join(",", targs)}>";
        if (!_funcs.ContainsKey(inst)) {
            if (++_instanceSeq > MaxInstances) throw new ForgeException("too many generic instances (unbounded generic recursion?)", c.Line);
            var map = gf.TypeParams.Zip(targs).ToDictionary(p => p.First, p => p.Second);
            var f = CloneFunc(gf, map, inst, null, null);
            AddFunc(f);
            EnsureTypesIn(f);
        }
        c.Name = inst;
        c.TypeArgs = null;
    }

    // Infer type arguments from the argument types: T from (T a, ...), T from T[] / T*,
    // T from List<T>. Two different bindings unify if one converts to the other.
    List<string> InferTypeArgs(Func gf, CallExpr c) {
        if (c.Args.Count != gf.Params.Count)
            throw new ForgeException($"'{gf.Name}' expects {gf.Params.Count} arg(s), got {c.Args.Count}", c.Line);
        var tps = new HashSet<string>(gf.TypeParams!);
        var bound = new Dictionary<string, string>();
        void Unify(string p, string a) {
            if (tps.Contains(p)) {
                if (a is "null" or "void") return;
                if (!bound.TryGetValue(p, out var have)) bound[p] = a;
                else if (have != a) {
                    if (Assignable(a, have)) bound[p] = a;            // int then double -> double
                    else if (!Assignable(have, a))
                        throw new ForgeException($"'{gf.Name}': {p} can't be both {have} and {a}; write {gf.Name}<...>(...)", c.Line);
                }
                return;
            }
            if (p.EndsWith("[]") && a.EndsWith("[]")) { Unify(p[..^2], a[..^2]); return; }
            if (p.EndsWith("*") && a.EndsWith("*")) { Unify(p[..^1], a[..^1]); return; }
            if (SplitGeneric(p, out var ph, out var pa, out var ps) && SplitGeneric(a, out var ah, out var aa, out var asx)
                && ph == ah && pa.Count == aa.Count && ps == asx)
                for (int k = 0; k < pa.Count; k++) Unify(pa[k], aa[k]);
        }
        for (int k = 0; k < c.Args.Count; k++) Unify(gf.Params[k].Type, TypeOf(c.Args[k]));
        var tparams = gf.TypeParams!;
        var missing = tparams.Where(t => !bound.ContainsKey(t)).ToList();
        if (missing.Count > 0)
            throw new ForgeException($"can't infer {string.Join(", ", missing)} for '{gf.Name}': write the type arguments, e.g. {gf.Name}<{string.Join(", ", tparams.Select(_ => "int"))}>(...)", c.Line);
        return tparams.Select(t => bound[t]).ToList();
    }

    Func CloneFunc(Func g, Dictionary<string, string> map, string name, string? owner, string? inst) {
        string Map(string t) => SubstType(t, map, owner, inst);
        var cl = new GenericCloner(Map, "$g" + (++_instanceSeq));
        return new Func {
            Line = g.Line, Name = name, Ret = Map(g.Ret), Throws = g.Throws, IsAsync = g.IsAsync,
            Params = g.Params.Select(p => (Map(p.Type), p.Name)).ToList(),
            Body = cl.CloneBody(g.Body),
            InstanceOf = owner ?? g.Name,
        };
    }

    void AddStruct(StructDecl sd) {
        _mod.Structs.Add(sd);
        if (_registered) _structs[sd.Name] = new StructInfo(sd);   // laid out once its field types exist
    }

    void AddFunc(Func f) {
        _mod.Funcs.Add(f);
        if (_registered) _funcs[f.Name] = f;
    }
}

// Deep copy of a function body as the parser builds it (before lowering), with type
// strings mapped through MapType and labels renamed (break/continue labels are global
// assembler labels, so each instance needs its own). With OnType set it also reports
// every type string it meets (used to find generic types to instantiate).
sealed class GenericCloner {
    readonly Func<string, string> _map;
    readonly string _labelSuffix;
    public Action<string, int>? OnType;

    public GenericCloner(Func<string, string> mapType, string labelSuffix) { _map = mapType; _labelSuffix = labelSuffix; }

    string T(string t, int line) { var r = _map(t); OnType?.Invoke(r, line); return r; }
    string L(string label) => label + _labelSuffix;

    public List<Stmt> CloneBody(List<Stmt> body) => body.Select(S).ToList();
    List<Stmt>? Body(List<Stmt>? b) => b?.Select(S).ToList();

    Stmt S(Stmt s) {
        Stmt r = s switch {
            VarDecl v => new VarDecl { DeclType = v.DeclType == null ? null : T(v.DeclType, v.Line), Name = v.Name, E = E(v.E) },
            Assign a => new Assign { Name = a.Name, E = E(a.E) },
            IndexAssign ia => new IndexAssign { Arr = E(ia.Arr), Index = E(ia.Index), E = E(ia.E) },
            AsmStmt asm => new AsmStmt { Text = asm.Text },
            MemberAssign ma => new MemberAssign { Obj = E(ma.Obj), Name = ma.Name, E = E(ma.E) },
            DerefAssign da => new DerefAssign { Ptr = E(da.Ptr), E = E(da.E) },
            IfStmt i => new IfStmt { Cond = E(i.Cond), Then = Body(i.Then)!, Else = Body(i.Else) },
            WhileStmt w => new WhileStmt { Cond = E(w.Cond), Body = Body(w.Body)! },
            ForStmt f => new ForStmt { Init = f.Init == null ? null : S(f.Init), Cond = f.Cond == null ? null : E(f.Cond), Step = f.Step == null ? null : S(f.Step), Body = Body(f.Body)! },
            RetStmt rt => new RetStmt { E = rt.E == null ? null : E(rt.E) },
            ThrowStmt th => new ThrowStmt { Err = th.Err, Msg = th.Msg },
            ExprStmt es => new ExprStmt { E = E(es.E) },
            PrintStmt p => new PrintStmt { Arg = E(p.Arg) },
            LabelStmt l => new LabelStmt { Name = L(l.Name) },
            GotoStmt g => new GotoStmt { Name = L(g.Name) },
            InlineRet ir => new InlineRet { E = ir.E == null ? null : E(ir.E), End = L(ir.End) },
            _ => throw new InvalidOperationException("generics: cannot copy statement " + s.GetType().Name),
        };
        r.Line = s.Line;
        return r;
    }

    Expr E(Expr e) {
        Expr r = e switch {
            IntLit i => new IntLit { Val = i.Val },
            FloatLit fl => new FloatLit { Val = fl.Val },
            StrLit st => new StrLit { Val = st.Val },
            BoolLit b => new BoolLit { Val = b.Val },
            NullLit => new NullLit(),
            VarRef v => new VarRef { Name = v.Name },
            Unary u => new Unary { Op = u.Op, E = E(u.E) },
            Binary b => new Binary { Op = b.Op, L = E(b.L), R = E(b.R) },
            CallExpr c => Call(c),
            TryExpr t => new TryExpr { Call = Call(t.Call), Else = t.Else == null ? null : E(t.Else) },
            Interp itp => new Interp { Parts = itp.Parts.Select(p => p is ValueTuple<Expr, string?> h ? (object)(E(h.Item1), h.Item2) : p).ToList() },
            NewArray na => new NewArray { ElemType = T(na.ElemType, na.Line), Size = E(na.Size) },
            IndexExpr ix => new IndexExpr { Arr = E(ix.Arr), Index = E(ix.Index) },
            LengthExpr le => new LengthExpr { Arr = E(le.Arr) },
            AddrOf ao => new AddrOf { Name = ao.Name },
            Deref d => new Deref { Ptr = E(d.Ptr) },
            CastExpr ce => new CastExpr { TargetType = T(ce.TargetType, ce.Line), E = E(ce.E) },
            SpawnExpr sp => new SpawnExpr { FuncName = sp.FuncName, Arg = sp.Arg == null ? null : E(sp.Arg) },
            AwaitExpr aw => new AwaitExpr { Task = E(aw.Task) },
            YieldExpr => new YieldExpr(),
            WhenAllExpr wa => new WhenAllExpr { Tasks = wa.Tasks.Select(E).ToList() },
            MemberExpr me => new MemberExpr { Obj = E(me.Obj), Name = me.Name },
            SizeOfExpr so => new SizeOfExpr { Type = T(so.Type, so.Line) },
            NewStruct ns => new NewStruct { Type = T(ns.Type, ns.Line), Inits = ns.Inits.Select(i => (i.Name, E(i.E))).ToList() },
            InlineExpr ie => new InlineExpr { Body = Body(ie.Body)!, End = L(ie.End), ResultType = T(ie.ResultType, ie.Line) },
            IncDecExpr id => new IncDecExpr { Op = id.Op, Target = E(id.Target) },
            _ => throw new InvalidOperationException("generics: cannot copy expression " + e.GetType().Name),
        };
        r.Line = e.Line;
        return r;
    }

    CallExpr Call(CallExpr c) => new CallExpr {
        Line = c.Line, Name = c.Name, NoInline = c.NoInline,
        Args = c.Args.Select(E).ToList(),
        TypeArgs = c.TypeArgs?.Select(t => T(t, c.Line)).ToList(),
    };
}
