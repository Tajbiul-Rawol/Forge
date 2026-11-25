using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ForgeC;

// ----------------------------------------------------------------- diagnostics
sealed class ForgeException : Exception {
    public readonly int Line;
    public ForgeException(string msg, int line) : base(msg) { Line = line; }
}

// Several errors at once (the type checker keeps going after the first one).
sealed class ForgeErrors : Exception {
    public readonly List<Diagnostic> Errors;
    public ForgeErrors(List<Diagnostic> errors) : base($"{errors.Count} error(s)") { Errors = errors; }
}

// ----------------------------------------------------------------- tokens
enum TokKind { Id, Kw, Int, Float, Str, Op, Eof }

readonly struct Tok {
    public readonly TokKind Kind;
    public readonly string Text;
    public readonly long IntVal;
    public readonly double FloatVal;
    public readonly int Line;
    public Tok(TokKind k, string t, int line, long iv = 0, double fv = 0) {
        Kind = k; Text = t; Line = line; IntVal = iv; FloatVal = fv;
    }
    public bool Is(TokKind k, string t) => Kind == k && Text == t;
}

sealed class Lexer {
    static readonly HashSet<string> Keywords = new() {
        "module","void","int","uint","ushort","long","double","bool","string",
        "i8","u8","i16","u16","i32","u32","i64","u64",
        "var","if","else","while","for","return","true","false","throws","throw","new","try","spawn",
        "async","await","struct","packed","null","extern"
    };
    static readonly string[] TwoChar = { "<<", ">>", "<=", ">=", "==", "!=", "&&", "||" };

    readonly string _s; int _i; int _line = 1;
    public Lexer(string s, int firstLine = 1) { _s = s; _line = firstLine; }

    char Cur => _i < _s.Length ? _s[_i] : '\0';
    char Nxt => _i + 1 < _s.Length ? _s[_i + 1] : '\0';

    public List<Tok> Lex() {
        var toks = new List<Tok>();
        while (_i < _s.Length) {
            char c = Cur;
            if (c == '\n') { _line++; _i++; continue; }
            if (c is ' ' or '\t' or '\r') { _i++; continue; }
            if (c == '/' && Nxt == '/') { while (_i < _s.Length && _s[_i] != '\n') _i++; continue; }
            if (c == '"') { toks.Add(LexString()); continue; }
            if (c == '$' && Nxt == '"') { _i++; toks.Add(LexString()); continue; }
            if (char.IsDigit(c)) { toks.Add(LexNumber()); continue; }
            if (char.IsLetter(c) || c == '_') { toks.Add(LexWord()); continue; }
            bool matched = false;
            foreach (var tc in TwoChar)
                if (c == tc[0] && Nxt == tc[1]) {
                    toks.Add(new Tok(TokKind.Op, tc, _line)); _i += 2; matched = true; break;
                }
            if (matched) continue;
            if ("+-*/%<>=!(){};,.[]&|^".IndexOf(c) >= 0) {
                toks.Add(new Tok(TokKind.Op, c.ToString(), _line)); _i++; continue;
            }
            throw new ForgeException($"unexpected character '{c}'", _line);
        }
        toks.Add(new Tok(TokKind.Eof, "", _line));
        return toks;
    }

    Tok LexString() {
        int line = _line; _i++; var sb = new System.Text.StringBuilder();
        while (true) {
            if (_i >= _s.Length) throw new ForgeException("unterminated string", line);
            char c = _s[_i];
            if (c == '"') { _i++; break; }
            if (c == '\\') {
                char e = Nxt;
                sb.Append(e switch { 'n' => '\n', 'r' => '\r', 't' => '\t', '0' => '\0', '\\' => '\\', '"' => '"', '\'' => '\'', _ => e });
                _i += 2; continue;
            }
            if (c == '\n') throw new ForgeException("unterminated string", line);
            sb.Append(c); _i++;
        }
        return new Tok(TokKind.Str, sb.ToString(), line);
    }

    Tok LexNumber() {
        int line = _line, start = _i;
        // hex literal: 0x....
        if (_s[_i] == '0' && _i + 1 < _s.Length && (_s[_i + 1] == 'x' || _s[_i + 1] == 'X')) {
            _i += 2;
            int hstart = _i;
            while (_i < _s.Length && Uri.IsHexDigit(_s[_i])) _i++;
            string hex = _s.Substring(hstart, _i - hstart);
            if (hex.Length == 0) throw new ForgeException("malformed hex literal", line);
            if (hex.TrimStart('0').Length > 16)
                throw new ForgeException($"hex literal '0x{hex}' does not fit in 64 bits", line);
            return new Tok(TokKind.Int, "0x" + hex, line, iv: Convert.ToInt64(hex, 16));
        }
        while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
        bool isFloat = false;
        if (_i + 1 < _s.Length && _s[_i] == '.' && char.IsDigit(_s[_i + 1])) {
            isFloat = true; _i++;
            while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
        }
        string txt = _s.Substring(start, _i - start);
        if (isFloat) return new Tok(TokKind.Float, txt, line, fv: double.Parse(txt, CultureInfo.InvariantCulture));
        if (!long.TryParse(txt, NumberStyles.None, CultureInfo.InvariantCulture, out long iv))
            throw new ForgeException($"integer literal '{txt}' is too large (the largest is 9223372036854775807)", line);
        return new Tok(TokKind.Int, txt, line, iv: iv);
    }

    Tok LexWord() {
        int line = _line, start = _i;
        while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_')) _i++;
        string w = _s.Substring(start, _i - start);
        return new Tok(Keywords.Contains(w) ? TokKind.Kw : TokKind.Id, w, line);
    }
}

// ----------------------------------------------------------------- AST
abstract class Node { public int Line; }

abstract class Expr : Node { }
sealed class IntLit  : Expr { public long Val; }
sealed class FloatLit: Expr { public double Val; }
sealed class StrLit  : Expr { public string Val = ""; }
sealed class BoolLit : Expr { public bool Val; }
sealed class VarRef  : Expr { public string Name = ""; }
sealed class Unary   : Expr { public string Op = ""; public Expr E = null!; }
sealed class Binary  : Expr { public string Op = ""; public Expr L = null!, R = null!; }
sealed class CallExpr: Expr { public string Name = ""; public List<Expr> Args = new(); public bool NoInline; }
sealed class TryExpr : Expr { public CallExpr Call = null!; public Expr? Else; }
sealed class Interp  : Expr { public List<object> Parts = new(); } // string | (Expr, fmt)
sealed class NewArray: Expr { public string ElemType = ""; public Expr Size = null!; }
sealed class IndexExpr:Expr { public Expr Arr = null!; public Expr Index = null!; }
sealed class LengthExpr:Expr{ public Expr Arr = null!; }
sealed class AddrOf  : Expr { public string Name = ""; }          // &local
sealed class Deref   : Expr { public Expr Ptr = null!; }          // *p
sealed class CastExpr: Expr { public string TargetType = ""; public Expr E = null!; } // (T)e
sealed class SpawnExpr: Expr { public string FuncName = ""; public Expr? Arg; }   // spawn Worker / spawn Worker(arg) -> thread handle
sealed class AwaitExpr: Expr { public Expr Task = null!; }       // await t       -> task result
sealed class YieldExpr: Expr { }                                 // await Task.Yield()
sealed class WhenAllExpr: Expr { public List<Expr> Tasks = new(); } // await Task.WhenAll(a, b, ...)
// Optimizer-only: an inlined function body used as an expression. Its InlineRet
// statements leave the value in rax and jump to End.
sealed class MemberExpr: Expr { public Expr Obj = null!; public string Name = ""; }   // obj.field (also .length)
sealed class NullLit : Expr { }
sealed class SizeOfExpr: Expr { public string Type = ""; }
sealed class NewStruct: Expr { public string Type = ""; public List<(string Name, Expr E)> Inits = new(); }  // new T { f = v, ... }
sealed class InlineExpr: Expr { public List<Stmt> Body = new(); public string End = ""; public string ResultType = ""; }

abstract class Stmt : Node { }
sealed class VarDecl : Stmt { public string? DeclType; public string Name = ""; public Expr E = null!; }
sealed class Assign  : Stmt { public string Name = ""; public Expr E = null!; }
sealed class IndexAssign : Stmt { public Expr Arr = null!; public Expr Index = null!; public Expr E = null!; }
sealed class AsmStmt : Stmt { public string Text = ""; }                // asm("...")
sealed class MemberAssign : Stmt { public Expr Obj = null!; public string Name = ""; public Expr E = null!; }  // obj.field = v
sealed class DerefAssign : Stmt { public Expr Ptr = null!; public Expr E = null!; }   // *p = v
sealed class IfStmt  : Stmt { public Expr Cond = null!; public List<Stmt> Then = new(); public List<Stmt>? Else; }
sealed class WhileStmt:Stmt { public Expr Cond = null!; public List<Stmt> Body = new(); }
sealed class ForStmt:Stmt { public Stmt? Init; public Expr? Cond; public Stmt? Step; public List<Stmt> Body = new(); }
sealed class RetStmt : Stmt { public Expr? E; }
sealed class ThrowStmt:Stmt { public string Err = ""; public string Msg = ""; }
sealed class ExprStmt: Stmt { public Expr E = null!; }
sealed class PrintStmt:Stmt { public Expr Arg = null!; } // Console.WriteLine
// Optimizer-only statements (never produced by the parser).
sealed class LabelStmt:Stmt { public string Name = ""; }
sealed class GotoStmt:Stmt { public string Name = ""; }
sealed class InlineRet:Stmt { public Expr? E; public string End = ""; }

sealed class Func : Node {
    public string Ret = "";
    public string Name = "";
    public List<(string Type, string Name)> Params = new();
    public string? Throws;
    public bool IsAsync;      // async Task / Task<T>: Ret holds the *result* type (void or T)
    public bool IsRuntime;    // compiler-supplied runtime function (async scheduler prelude)
    public bool IsCallback;   // 'extern' with a body: callable from C (its address is &Name)
    public bool IsInterrupt;  // 'interrupt' handler: entered by the CPU, returns with iretq (address &Name)
    public List<(string Type, string Name)> ExtraLocals = new(); // optimizer temporaries
    public List<Stmt> Body = new();
}
sealed class StructDecl : Node {
    public string Name = "";
    public bool Packed;                                   // no padding between fields
    public List<(string Type, string Name)> Fields = new();
}
// extern "user32.dll" i32 MessageBoxA(long hwnd, string text, string caption, u32 type);
sealed class ExternDecl : Node {
    public string Lib = "", Name = "", Ret = "";
    public List<(string Type, string Name)> Params = new();
}
sealed class ModuleNode : Node {
    public string Name = "";
    public List<Func> Funcs = new();
    public List<StructDecl> Structs = new();
    public List<ExternDecl> Externs = new();
}

// ----------------------------------------------------------------- parser
sealed class Parser {
    static readonly HashSet<string> Types = new() { "void","int","uint","ushort","long","double","bool","string",
        "i8","u8","i16","u16","i32","u32","i64","u64" };
    readonly List<Tok> _t; int _i;
    readonly HashSet<string> _structs;                   // struct names declared anywhere in the file
    public Parser(List<Tok> t, HashSet<string>? structs = null) {
        _t = t;
        _structs = structs ?? new();
        for (int k = 0; k + 1 < t.Count; k++)
            if (t[k].Is(TokKind.Kw, "struct") && t[k + 1].Kind == TokKind.Id) _structs.Add(t[k + 1].Text);
    }
    bool IsStructName(Tok t) => t.Kind == TokKind.Id && _structs.Contains(t.Text);
    // does a type start at token k?  (built-in type keyword or a struct name)
    bool TypeAt(int k) => (Peek(k).Kind == TokKind.Kw && Types.Contains(Peek(k).Text)) || IsStructName(Peek(k));

    Tok Cur => _t[_i];
    Tok Peek(int k = 1) => _t[Math.Min(_i + k, _t.Count - 1)];
    bool AtOp(string s) => Cur.Kind == TokKind.Op && Cur.Text == s;
    bool AtKw(string s) => Cur.Kind == TokKind.Kw && Cur.Text == s;

    Tok Eat() { return _t[_i++]; }
    bool Accept(TokKind k, string s) { if (Cur.Kind == k && Cur.Text == s) { _i++; return true; } return false; }
    Tok Expect(TokKind k, string s) {
        if (Cur.Kind == k && Cur.Text == s) return _t[_i++];
        throw new ForgeException($"expected '{s}' but found '{Cur.Text}'", Cur.Line);
    }
    Tok ExpectId() {
        if (Cur.Kind == TokKind.Id) return _t[_i++];
        throw new ForgeException($"expected an identifier but found '{Cur.Text}'", Cur.Line);
    }

    public ModuleNode ParseModule() {
        var m = new ModuleNode { Line = Cur.Line };
        Expect(TokKind.Kw, "module");
        m.Name = ExpectId().Text;
        while (Accept(TokKind.Op, ".")) m.Name += "." + ExpectId().Text;
        Expect(TokKind.Op, ";");
        while (Cur.Kind != TokKind.Eof) {
            if (AtKw("struct") || (AtKw("packed") && Peek().Is(TokKind.Kw, "struct"))) ParseStruct(m);
            else if (AtKw("extern") || AtOp("[")) ParseExtern(m);
            else if (Cur.Is(TokKind.Id, "interrupt") && TypeAt(1)) {
                // interrupt void Timer() { ... }   /   interrupt void PageFault(long error) { ... }
                Eat();
                var f = ParseFunc();
                f.IsInterrupt = true;
                if (f.Ret != "void" || f.Params.Count > 1 || f.IsAsync || f.Throws != null)
                    throw new ForgeException("an interrupt handler is 'interrupt void Name()' or 'interrupt void Name(long errorCode)'", f.Line);
                m.Funcs.Add(f);
            }
            else m.Funcs.Add(ParseFunc());
        }
        return m;
    }

    // Foreign functions:
    //   extern "user32.dll" i32 MessageBoxA(long h, string text, string title, u32 type);
    //   [DllImport("user32.dll")] static extern i32 MessageBoxA(...);      (C# spelling)
    // and Forge functions callable from C (window procedures, qsort comparators, ...):
    //   extern long WndProc(long hwnd, long msg, long wp, long lp) { ... }   -> &WndProc
    void ParseExtern(ModuleNode m) {
        int line = Cur.Line;
        string? lib = null;
        if (Accept(TokKind.Op, "[")) {
            var attr = ExpectId();
            if (attr.Text != "DllImport") throw new ForgeException($"unknown attribute '{attr.Text}'" + Suggest.Hint(attr.Text, new[] { "DllImport" }), attr.Line);
            Expect(TokKind.Op, "("); lib = Expect(TokKind.Str, Cur.Text).Text; Expect(TokKind.Op, ")");
            Expect(TokKind.Op, "]");
            if (Cur.Is(TokKind.Id, "static")) Eat();
        }
        Expect(TokKind.Kw, "extern");
        if (lib == null && Cur.Kind == TokKind.Str) lib = Eat().Text;
        string ret = ParseType();
        string name = ExpectId().Text;
        var ps = new List<(string, string)>();
        Expect(TokKind.Op, "(");
        if (!AtOp(")")) do { string pt = ParseType(); ps.Add((pt, ExpectId().Text)); } while (Accept(TokKind.Op, ","));
        Expect(TokKind.Op, ")");
        if (lib != null) {
            Expect(TokKind.Op, ";");
            m.Externs.Add(new ExternDecl { Line = line, Lib = lib, Name = name, Ret = ret, Params = ps });
            return;
        }
        if (AtOp(";")) throw new ForgeException($"extern '{name}' needs a library: extern \"lib\" ... or a body", line);
        var f = new Func { Line = line, Ret = ret, Name = name, Params = ps, IsCallback = true, Body = ParseBlock() };
        m.Funcs.Add(f);
    }

    // [packed] struct Name { Type field; Type a, b; Ret Method(params) { ... } }
    // Methods become functions named "Name.Method" whose first parameter is 'this'.
    void ParseStruct(ModuleNode m) {
        var sd = new StructDecl { Line = Cur.Line, Packed = Accept(TokKind.Kw, "packed") };
        Expect(TokKind.Kw, "struct");
        sd.Name = ExpectId().Text;
        Expect(TokKind.Op, "{");
        var methods = new List<Func>();
        while (!AtOp("}")) {
            int line = Cur.Line;
            string ty = ParseType();
            string name = ExpectId().Text;
            if (AtOp("(")) {
                var f = new Func { Line = line, Ret = ty, Name = sd.Name + "." + name };
                f.Params.Add((sd.Name, "this"));
                Eat();
                if (!AtOp(")")) do { string pt = ParseType(); f.Params.Add((pt, ExpectId().Text)); } while (Accept(TokKind.Op, ","));
                Expect(TokKind.Op, ")");
                if (Accept(TokKind.Kw, "throws")) f.Throws = ExpectId().Text;
                f.Body = ParseBlock();
                methods.Add(f);
                continue;
            }
            sd.Fields.Add((ty, name));
            while (Accept(TokKind.Op, ",")) sd.Fields.Add((ty, ExpectId().Text));
            Expect(TokKind.Op, ";");
        }
        Expect(TokKind.Op, "}");
        foreach (var f in methods) { new MethodScope(sd, methods).Rewrite(f); m.Funcs.Add(f); }
        m.Structs.Add(sd);
    }

    // Task / Task<T> start a type?  (Task is a contextual identifier, not a keyword)
    bool AtTaskType(int k = 0) => Peek(k).Is(TokKind.Id, "Task")
        && (Peek(k + 1).Is(TokKind.Op, "<") || Peek(k + 1).Kind == TokKind.Id);

    string ParseType() {
        if (Cur.Is(TokKind.Id, "Task")) {
            int line = Eat().Line;
            if (!Accept(TokKind.Op, "<")) return "Task";
            if (Cur.Is(TokKind.Id, "Task"))
                throw new ForgeException("nested Task<Task<...>> is not supported", line);
            string inner = ParseType();
            if (inner == "void") throw new ForgeException("use 'Task' instead of 'Task<void>'", line);
            Expect(TokKind.Op, ">");
            return "Task<" + inner + ">";
        }
        if ((Cur.Kind == TokKind.Kw && Types.Contains(Cur.Text)) || IsStructName(Cur)) {
            string t = Eat().Text;
            // array type:  int[]
            if (AtOp("[") && Peek().Is(TokKind.Op, "]")) { Eat(); Eat(); t += "[]"; }
            // pointer type(s): u32*  int**
            while (AtOp("*")) { Eat(); t += "*"; }
            return t;
        }
        throw new ForgeException($"expected a type but found '{Cur.Text}'"
            + Suggest.Hint(Cur.Text, Types), Cur.Line);
    }

    Func ParseFunc() {
        int fline = Cur.Line;
        bool isAsync = Accept(TokKind.Kw, "async");
        string ret = ParseType();
        if (isAsync) {
            // async Task<T> -> result T;  async Task / async void -> result void
            if (ret.StartsWith("Task<")) ret = ret.Substring(5, ret.Length - 6);
            else if (ret == "Task" || ret == "void") ret = "void";
            else throw new ForgeException($"an async function must return Task or Task<T>, not {ret}", fline);
        } else if (ret.StartsWith("Task")) {
            throw new ForgeException("a function returning a Task must be declared 'async'", fline);
        }
        var f = new Func { Line = fline, Ret = ret, IsAsync = isAsync, Name = ExpectId().Text };
        Expect(TokKind.Op, "(");
        if (!AtOp(")")) {
            do {
                string pt = ParseType();
                string pn = ExpectId().Text;
                f.Params.Add((pt, pn));
            } while (Accept(TokKind.Op, ","));
        }
        Expect(TokKind.Op, ")");
        if (Accept(TokKind.Kw, "throws")) f.Throws = ExpectId().Text;
        f.Body = ParseBlock();
        return f;
    }

    List<Stmt> ParseBlock() {
        Expect(TokKind.Op, "{");
        var list = new List<Stmt>();
        while (!AtOp("}")) list.Add(ParseStmt());
        Expect(TokKind.Op, "}");
        return list;
    }

    List<Stmt> StmtAsList() {
        if (AtOp("{")) return ParseBlock();
        return new List<Stmt> { ParseStmt() };
    }

    // A declaration or assignment with NO trailing ';' (used in for(...) headers).
    Stmt ParseSimpleStmt() {
        int line = Cur.Line;
        if (AtKw("var")) {
            Eat(); string name = ExpectId().Text; Expect(TokKind.Op, "=");
            return new VarDecl { Line = line, DeclType = null, Name = name, E = ParseExpr() };
        }
        if ((Cur.Kind == TokKind.Kw && Types.Contains(Cur.Text) && Cur.Text != "void") || AtTaskType() || IsStructName(Cur)) {
            string ct = ParseType(); string name = ExpectId().Text; Expect(TokKind.Op, "=");
            return new VarDecl { Line = line, DeclType = ct, Name = name, E = ParseExpr() };
        }
        var lhs = ParseExpr();
        if (AtOp("=")) {
            Eat(); var rhs = ParseExpr();
            if (lhs is VarRef vr) return new Assign { Line = line, Name = vr.Name, E = rhs };
            if (lhs is IndexExpr ix) return new IndexAssign { Line = line, Arr = ix.Arr, Index = ix.Index, E = rhs };
            if (lhs is Deref dr) return new DerefAssign { Line = line, Ptr = dr.Ptr, E = rhs };
            if (lhs is MemberExpr me) return new MemberAssign { Line = line, Obj = me.Obj, Name = me.Name, E = rhs };
            throw new ForgeException("left side of '=' is not assignable", line);
        }
        return new ExprStmt { Line = line, E = lhs };
    }

    static Expr DefaultValue(string type, int line) => type switch {
        "bool" => new BoolLit { Line = line },
        "int" or "uint" or "ushort" or "long" or "i8" or "u8" or "i16" or "u16" or "i32" or "u32" or "i64" or "u64" => new IntLit { Line = line },
        "double" => new FloatLit { Line = line },
        _ => new NullLit { Line = line },
    };

    Stmt ParseStmt() {
        int line = Cur.Line;
        if (AtKw("var")) {
            Eat(); string name = ExpectId().Text; Expect(TokKind.Op, "=");
            var e = ParseExpr(); Expect(TokKind.Op, ";");
            return new VarDecl { Line = line, DeclType = null, Name = name, E = e };
        }
        if ((Cur.Kind == TokKind.Kw && Types.Contains(Cur.Text) && Cur.Text != "void") || AtTaskType() || IsStructName(Cur)) {
            string ct = ParseType(); string name = ExpectId().Text;
            Expr e;
            if (Accept(TokKind.Op, ";")) e = DefaultValue(ct, line);       // T x;  -> zero / false / null
            else { Expect(TokKind.Op, "="); e = ParseExpr(); Expect(TokKind.Op, ";"); }
            return new VarDecl { Line = line, DeclType = ct, Name = name, E = e };
        }
        if (AtKw("if")) {
            Eat(); Expect(TokKind.Op, "("); var c = ParseExpr(); Expect(TokKind.Op, ")");
            var then = StmtAsList(); List<Stmt>? els = null;
            if (Accept(TokKind.Kw, "else")) els = StmtAsList();
            return new IfStmt { Line = line, Cond = c, Then = then, Else = els };
        }
        if (AtKw("while")) {
            Eat(); Expect(TokKind.Op, "("); var c = ParseExpr(); Expect(TokKind.Op, ")");
            return new WhileStmt { Line = line, Cond = c, Body = StmtAsList() };
        }
        if (AtKw("for")) {
            Eat(); Expect(TokKind.Op, "(");
            // init: a declaration or assignment, terminated by ';'
            Stmt? init = AtOp(";") ? null : ParseSimpleStmt();
            Expect(TokKind.Op, ";");
            Expr? cond = AtOp(";") ? null : ParseExpr();
            Expect(TokKind.Op, ";");
            Stmt? step = AtOp(")") ? null : ParseSimpleStmt();
            Expect(TokKind.Op, ")");
            return new ForStmt { Line = line, Init = init, Cond = cond, Step = step, Body = StmtAsList() };
        }
        if (AtKw("return")) {
            Eat(); Expr? e = null;
            if (!AtOp(";")) e = ParseExpr();
            Expect(TokKind.Op, ";");
            return new RetStmt { Line = line, E = e };
        }
        if (AtKw("throw")) {
            Eat(); Expect(TokKind.Kw, "new"); string en = ExpectId().Text;
            Expect(TokKind.Op, "("); string msg = Expect(TokKind.Str, Cur.Text).Text; Expect(TokKind.Op, ")");
            Expect(TokKind.Op, ";");
            return new ThrowStmt { Line = line, Err = en, Msg = msg };
        }
        // asm("mov rax, cr3; mov {root}, rax");  -- raw text, {var} operands
        if (Cur.Is(TokKind.Id, "asm") && Peek(1).Is(TokKind.Op, "(") && Peek(2).Kind == TokKind.Str) {
            _i += 2;
            var sb = new System.Text.StringBuilder(Eat().Text);
            while (Accept(TokKind.Op, ",")) sb.Append('\n').Append(Expect(TokKind.Str, Cur.Text).Text);
            Expect(TokKind.Op, ")"); Expect(TokKind.Op, ";");
            return new AsmStmt { Line = line, Text = sb.ToString() };
        }
        // Console.WriteLine(...) ;
        if (Cur.Kind == TokKind.Id && Cur.Text == "Console"
            && Peek(1).Is(TokKind.Op, ".") && Peek(2).Is(TokKind.Id, "WriteLine")) {
            _i += 3; Expect(TokKind.Op, "(");
            var arg = ParseExpr(); Expect(TokKind.Op, ")"); Expect(TokKind.Op, ";");
            return new PrintStmt { Line = line, Arg = arg };
        }
        // a misspelled type at the start of a declaration:  itn x = ...
        if (Cur.Kind == TokKind.Id && Peek(1).Kind == TokKind.Id && Peek(2).Is(TokKind.Op, "=")) {
            var maybeType = Cur.Text;
            var hint = Suggest.Closest(maybeType, Types);
            if (hint != null)
                throw new ForgeException($"unknown type '{maybeType}' (did you mean '{hint}'?)", Cur.Line);
        }
        // expression, possibly an assignment (lvalue = rvalue)
        var lhs = ParseExpr();
        if (AtOp("=")) {
            Eat();
            var rhs = ParseExpr(); Expect(TokKind.Op, ";");
            if (lhs is VarRef vr) return new Assign { Line = line, Name = vr.Name, E = rhs };
            if (lhs is IndexExpr ix) return new IndexAssign { Line = line, Arr = ix.Arr, Index = ix.Index, E = rhs };
            if (lhs is Deref dr) return new DerefAssign { Line = line, Ptr = dr.Ptr, E = rhs };
            if (lhs is MemberExpr me) return new MemberAssign { Line = line, Obj = me.Obj, Name = me.Name, E = rhs };
            throw new ForgeException("left side of '=' is not assignable", line);
        }
        Expect(TokKind.Op, ";");
        return new ExprStmt { Line = line, E = lhs };
    }

    Expr ParseExpr() => ParseOr();
    Expr ParseOr()  { var e = ParseAnd(); while (AtOp("||")) { int l = Eat().Line; e = new Binary { Line = l, Op = "||", L = e, R = ParseAnd() }; } return e; }
    Expr ParseAnd() { var e = ParseBitOr();  while (AtOp("&&")) { int l = Eat().Line; e = new Binary { Line = l, Op = "&&", L = e, R = ParseBitOr() }; } return e; }
    Expr ParseBitOr()  { var e = ParseBitXor(); while (AtOp("|")) { var o = Eat(); e = new Binary { Line = o.Line, Op = "|", L = e, R = ParseBitXor() }; } return e; }
    Expr ParseBitXor() { var e = ParseBitAnd(); while (AtOp("^")) { var o = Eat(); e = new Binary { Line = o.Line, Op = "^", L = e, R = ParseBitAnd() }; } return e; }
    Expr ParseBitAnd() { var e = ParseEq();     while (AtOp("&")) { var o = Eat(); e = new Binary { Line = o.Line, Op = "&", L = e, R = ParseEq() }; } return e; }
    Expr ParseEq()  { var e = ParseCmp(); while (AtOp("==") || AtOp("!=")) { var o = Eat(); e = new Binary { Line = o.Line, Op = o.Text, L = e, R = ParseCmp() }; } return e; }
    Expr ParseCmp() { var e = ParseShift(); while (AtOp("<")||AtOp(">")||AtOp("<=")||AtOp(">=")) { var o = Eat(); e = new Binary { Line = o.Line, Op = o.Text, L = e, R = ParseShift() }; } return e; }
    Expr ParseShift(){ var e = ParseAdd(); while (AtOp("<<")||AtOp(">>")) { var o = Eat(); e = new Binary { Line = o.Line, Op = o.Text, L = e, R = ParseAdd() }; } return e; }
    Expr ParseAdd() { var e = ParseMul(); while (AtOp("+")||AtOp("-")) { var o = Eat(); e = new Binary { Line = o.Line, Op = o.Text, L = e, R = ParseMul() }; } return e; }
    Expr ParseMul() { var e = ParseUnary(); while (AtOp("*")||AtOp("/")||AtOp("%")) { var o = Eat(); e = new Binary { Line = o.Line, Op = o.Text, L = e, R = ParseUnary() }; } return e; }

    Expr ParseUnary() {
        if (AtKw("await")) {
            int line = Eat().Line;
            // await Task.Yield()   /   await Task.WhenAll(t1, t2, ...)
            if (Cur.Is(TokKind.Id, "Task") && Peek(1).Is(TokKind.Op, ".") && Peek(2).Kind == TokKind.Id) {
                string member = Peek(2).Text;
                _i += 3;
                Expect(TokKind.Op, "(");
                var args = new List<Expr>();
                if (!AtOp(")")) do { args.Add(ParseExpr()); } while (Accept(TokKind.Op, ","));
                Expect(TokKind.Op, ")");
                if (member == "Yield") {
                    if (args.Count != 0) throw new ForgeException("Task.Yield() takes no arguments", line);
                    return new YieldExpr { Line = line };
                }
                if (member == "WhenAll") {
                    if (args.Count == 0) throw new ForgeException("Task.WhenAll(...) needs at least one task", line);
                    return new WhenAllExpr { Line = line, Tasks = args };
                }
                throw new ForgeException($"unknown member 'Task.{member}'"
                    + Suggest.Hint(member, new[] { "Yield", "WhenAll" }), line);
            }
            return new AwaitExpr { Line = line, Task = ParseUnary() };
        }
        if (AtOp("!") || AtOp("-")) { var o = Eat(); return new Unary { Line = o.Line, Op = o.Text, E = ParseUnary() }; }
        if (AtOp("*")) { var o = Eat(); return new Deref { Line = o.Line, Ptr = ParseUnary() }; }
        if (AtOp("&")) {
            var o = Eat();
            var target = ParseUnary();
            if (target is not VarRef vr) throw new ForgeException("'&' requires a variable", o.Line);
            return new AddrOf { Line = o.Line, Name = vr.Name };
        }
        return ParsePrimary();
    }

    Expr ParsePrimary() {
        var t = Cur;
        // spawn FuncName  -> start a thread, yields a handle
        if (AtKw("spawn")) {
            Eat();
            string fn = ExpectId().Text;
            Expr? arg = null;
            if (Accept(TokKind.Op, "(")) {
                if (!AtOp(")")) arg = ParseExpr();
                Expect(TokKind.Op, ")");
            }
            return new SpawnExpr { Line = t.Line, FuncName = fn, Arg = arg };
        }
        // new T[n]  -> array allocation
        if (AtKw("new") && Peek(1).Kind == TokKind.Kw && Types.Contains(Peek(1).Text)) {
            Eat();                       // new
            string elem = Eat().Text;    // element type keyword
            Expect(TokKind.Op, "[");
            var size = ParseExpr();
            Expect(TokKind.Op, "]");
            return Postfix(new NewArray { Line = t.Line, ElemType = elem, Size = size });
        }
        if (AtKw("new") && IsStructName(Peek(1))) {
            Eat(); string sn = Eat().Text;
            if (AtOp("[")) {
                Eat(); var size = ParseExpr(); Expect(TokKind.Op, "]");
                return Postfix(new NewArray { Line = t.Line, ElemType = sn, Size = size });
            }
            var ns = new NewStruct { Line = t.Line, Type = sn };
            if (Accept(TokKind.Op, "(")) Expect(TokKind.Op, ")");
            if (Accept(TokKind.Op, "{")) {
                if (!AtOp("}")) do {
                    if (AtOp("}")) break;                      // trailing comma
                    string fn = ExpectId().Text; Expect(TokKind.Op, "=");
                    ns.Inits.Add((fn, ParseExpr()));
                } while (Accept(TokKind.Op, ","));
                Expect(TokKind.Op, "}");
            }
            return Postfix(ns);
        }
        if (AtKw("new") && Peek(1).Kind == TokKind.Id)
            throw new ForgeException($"unknown type '{Peek(1).Text}'" + Suggest.Hint(Peek(1).Text, _structs.Concat(Types)), t.Line);
        if (AtKw("null")) { Eat(); return new NullLit { Line = t.Line }; }
        if (t.Is(TokKind.Id, "sizeof") && Peek(1).Is(TokKind.Op, "(") && TypeAt(2)) {
            Eat(); Eat(); string st = ParseType(); Expect(TokKind.Op, ")");
            return new SizeOfExpr { Line = t.Line, Type = st };
        }
        if (t.Kind == TokKind.Int)   { Eat(); return new IntLit   { Line = t.Line, Val = t.IntVal }; }
        if (t.Kind == TokKind.Float) { Eat(); return new FloatLit { Line = t.Line, Val = t.FloatVal }; }
        if (t.Kind == TokKind.Str)   { Eat(); return ParseInterp(t.Text, t.Line); }
        if (AtKw("true"))  { Eat(); return new BoolLit { Line = t.Line, Val = true }; }
        if (AtKw("false")) { Eat(); return new BoolLit { Line = t.Line, Val = false }; }
        if (AtKw("try")) {
            Eat(); var inner = ParsePrimary();
            if (inner is not CallExpr ce) throw new ForgeException("'try' must be applied to a function call", t.Line);
            Expr? els = null;
            if (Accept(TokKind.Kw, "else")) els = ParseExpr();
            return new TryExpr { Line = t.Line, Call = ce, Else = els };
        }
        if (AtOp("(")) {
            // cast:  (Type) expr   -- detected by a type keyword right after '('
            if ((Peek(1).Kind == TokKind.Kw && Types.Contains(Peek(1).Text)) || AtTaskType(1)
                || (IsStructName(Peek(1)) && (Peek(2).Is(TokKind.Op, ")") || Peek(2).Is(TokKind.Op, "*")))
                || (Peek(1).Is(TokKind.Id, "Task") && Peek(2).Is(TokKind.Op, ")"))) {
                int line = Eat().Line;          // (
                string ty = ParseType();
                Expect(TokKind.Op, ")");
                return Postfix(new CastExpr { Line = line, TargetType = ty, E = ParseUnary() });
            }
            Eat(); var e = ParseExpr(); Expect(TokKind.Op, ")"); return Postfix(e);
        }
        if (t.Kind == TokKind.Id) {
            string name = Eat().Text;
            if (AtOp("(")) {
                Eat(); var args = new List<Expr>();
                if (!AtOp(")")) do { args.Add(ParseExpr()); } while (Accept(TokKind.Op, ","));
                Expect(TokKind.Op, ")");
                return Postfix(new CallExpr { Line = t.Line, Name = name, Args = args });
            }
            return Postfix(new VarRef { Line = t.Line, Name = name });
        }
        throw new ForgeException($"unexpected token '{t.Text}' in expression", t.Line);
    }

    // handle chained postfix operators: a[i], a.length
    Expr Postfix(Expr e) {
        while (true) {
            if (AtOp("[")) {
                int line = Eat().Line;
                var idx = ParseExpr();
                Expect(TokKind.Op, "]");
                e = new IndexExpr { Line = line, Arr = e, Index = idx };
            } else if (AtOp(".") && Peek(1).Kind == TokKind.Id && Peek(2).Is(TokKind.Op, "(")) {
                // obj.Method(args) -> CallExpr ".Method"(obj, args); resolved by the type checker
                int line = Cur.Line; Eat(); string mn = Eat().Text; Eat();
                var args = new List<Expr> { e };
                if (!AtOp(")")) do { args.Add(ParseExpr()); } while (Accept(TokKind.Op, ","));
                Expect(TokKind.Op, ")");
                e = new CallExpr { Line = line, Name = "." + mn, Args = args };
            } else if (AtOp(".") && Peek(1).Kind == TokKind.Id) {
                // obj.field  (or arr.length: the type checker decides)
                int line = Cur.Line; Eat();
                e = new MemberExpr { Line = line, Obj = e, Name = Eat().Text };
            } else break;
        }
        return e;
    }

    // interpolation:  $"..."  is written as a normal string with {holes} here; we treat
    // any string containing { ... } as interpolated, matching the spec's $"" lowering.
    Expr ParseInterp(string raw, int line) {
        if (raw.IndexOf('{') < 0) return new StrLit { Line = line, Val = raw };
        var parts = new List<object>(); var sb = new System.Text.StringBuilder();
        for (int k = 0; k < raw.Length; k++) {
            char c = raw[k];
            if (c == '{') {
                if (sb.Length > 0) { parts.Add(sb.ToString()); sb.Clear(); }
                int depth = 1, j = k + 1; var inner = new System.Text.StringBuilder(); string? fmt = null;
                while (j < raw.Length && depth > 0) {
                    char d = raw[j];
                    if (d == '{') depth++;
                    else if (d == '}') { depth--; if (depth == 0) break; }
                    if (d == ':' && depth == 1) { fmt = ""; j++; while (j < raw.Length && raw[j] != '}') { fmt += raw[j]; j++; } break; }
                    inner.Append(d); j++;
                }
                var sub = new Parser(new Lexer(inner.ToString(), line).Lex(), _structs);
                var e = sub.ParseExpr();
                if (sub.Cur.Kind != TokKind.Eof)
                    throw new ForgeException($"unexpected '{sub.Cur.Text}' in interpolation hole {{{inner}}}", line);
                parts.Add((e, fmt));
                k = j;
            } else sb.Append(c);
        }
        if (sb.Length > 0) parts.Add(sb.ToString());
        return new Interp { Line = line, Parts = parts };
    }
}


// Inside a struct method, a bare field name means this.field and a bare method name
// means this.Method(...), unless a parameter or local of that name hides it.
sealed class MethodScope {
    readonly HashSet<string> _fields = new(), _methods = new();
    HashSet<string> _locals = new();
    public MethodScope(StructDecl sd, List<Func> methods) {
        foreach (var (_, n) in sd.Fields) _fields.Add(n);
        foreach (var m in methods) _methods.Add(m.Name.Substring(m.Name.IndexOf('.') + 1));
    }
    public void Rewrite(Func f) {
        _locals = new HashSet<string>(f.Params.Select(p => p.Name));
        Walker.Walk(f.Body, null, s => { if (s is VarDecl v) _locals.Add(v.Name); });
        f.Body = Body(f.Body);
    }
    Expr This(int line) => new VarRef { Line = line, Name = "this" };
    bool IsField(string n) => _fields.Contains(n) && !_locals.Contains(n);
    List<Stmt> Body(List<Stmt> b) => b.Select(St).ToList();
    Stmt St(Stmt s) {
        switch (s) {
            case Assign a when IsField(a.Name): return new MemberAssign { Line = a.Line, Obj = This(a.Line), Name = a.Name, E = Ex(a.E) };
            case Assign a: a.E = Ex(a.E); return a;
            case VarDecl v: v.E = Ex(v.E); return v;
            case IndexAssign ia: ia.Arr = Ex(ia.Arr); ia.Index = Ex(ia.Index); ia.E = Ex(ia.E); return ia;
            case DerefAssign da: da.Ptr = Ex(da.Ptr); da.E = Ex(da.E); return da;
            case MemberAssign ma: ma.Obj = Ex(ma.Obj); ma.E = Ex(ma.E); return ma;
            case IfStmt i: i.Cond = Ex(i.Cond); i.Then = Body(i.Then); if (i.Else != null) i.Else = Body(i.Else); return i;
            case WhileStmt w: w.Cond = Ex(w.Cond); w.Body = Body(w.Body); return w;
            case ForStmt fs:
                if (fs.Init != null) fs.Init = St(fs.Init);
                if (fs.Cond != null) fs.Cond = Ex(fs.Cond);
                if (fs.Step != null) fs.Step = St(fs.Step);
                fs.Body = Body(fs.Body); return fs;
            case RetStmt r: if (r.E != null) r.E = Ex(r.E); return r;
            case ExprStmt es: es.E = Ex(es.E); return es;
            case PrintStmt p: p.Arg = Ex(p.Arg); return p;
            default: return s;
        }
    }
    Expr Ex(Expr e) {
        switch (e) {
            case VarRef v when IsField(v.Name): return new MemberExpr { Line = v.Line, Obj = This(v.Line), Name = v.Name };
            case Unary u: u.E = Ex(u.E); return u;
            case Binary b: b.L = Ex(b.L); b.R = Ex(b.R); return b;
            case CallExpr c:
                for (int k = 0; k < c.Args.Count; k++) c.Args[k] = Ex(c.Args[k]);
                if (_methods.Contains(c.Name) && !_locals.Contains(c.Name)) { c.Args.Insert(0, This(c.Line)); c.Name = "." + c.Name; }
                return c;
            case TryExpr t: Ex(t.Call); if (t.Else != null) t.Else = Ex(t.Else); return t;
            case NewArray na: na.Size = Ex(na.Size); return na;
            case NewStruct ns: for (int k = 0; k < ns.Inits.Count; k++) ns.Inits[k] = (ns.Inits[k].Name, Ex(ns.Inits[k].E)); return ns;
            case IndexExpr ix: ix.Arr = Ex(ix.Arr); ix.Index = Ex(ix.Index); return ix;
            case LengthExpr le: le.Arr = Ex(le.Arr); return le;
            case MemberExpr me: me.Obj = Ex(me.Obj); return me;
            case Deref de: de.Ptr = Ex(de.Ptr); return de;
            case CastExpr ce: ce.E = Ex(ce.E); return ce;
            case SpawnExpr sp: if (sp.Arg != null) sp.Arg = Ex(sp.Arg); return sp;
            case AwaitExpr aw: aw.Task = Ex(aw.Task); return aw;
            case WhenAllExpr wa: for (int k = 0; k < wa.Tasks.Count; k++) wa.Tasks[k] = Ex(wa.Tasks[k]); return wa;
            case Interp itp:
                for (int k = 0; k < itp.Parts.Count; k++)
                    if (itp.Parts[k] is ValueTuple<Expr, string?> tup) itp.Parts[k] = (Ex(tup.Item1), tup.Item2);
                return itp;
            default: return e;
        }
    }
}
