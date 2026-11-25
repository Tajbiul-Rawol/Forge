using System.Collections.Generic;
using System.Linq;

namespace ForgeC;

// =====================================================================================
//  async / await runtime
// =====================================================================================
//
// Forge implements async/await with *stackful coroutines* driven by a cooperative,
// single-threaded scheduler. Nothing here depends on an operating system: it is plain
// stack switching, so it works identically on the Windows, Linux and bare-metal kernel
// targets.
//
//   * Every call to an `async` function creates a Task: a small heap record plus its own
//     32 KB stack. The task starts running *immediately* (C# semantics) on its own stack
//     and runs until it finishes or first suspends; then control returns to the caller,
//     which receives the Task handle.
//   * `await t` returns t's result if it has finished. Otherwise the current task parks
//     itself on t's waiter list and the scheduler switches to the next ready task. When t
//     finishes, all its waiters are made ready again.
//   * `await Task.Yield()` moves the current task to the back of the ready queue.
//   * Main is itself a task (it runs on the original OS stack). When Main returns, the
//     runtime keeps running ready tasks until none remain, so fire-and-forget tasks
//     complete before the program exits.
//   * If every task is blocked waiting on another, the runtime reports a deadlock and
//     exits with status 1 instead of hanging.
//
// Only two pieces are hand-written machine code (see CodeGen.EmitAsyncAsm):
//   __ctx_switch(i64* saveSp, i64 newSp)  save callee-saved regs, swap stacks, restore
//   __ctx_init(i64 top, i64 limit) -> sp  build a fresh stack whose first switch "returns"
//                                         into __fiber_entry
//   __fiber_entry                         calls the task's function with its arguments,
//                                         then hands the result to __rt_finish
// On Windows __ctx_switch also swaps the TEB StackBase/StackLimit fields (exactly what
// SwitchToFiber does) so stack probes in system DLLs see a consistent stack.
//
// Everything else - the ready queue, waiter lists, stack recycling - is ordinary Forge
// source below, compiled by the same compiler as the user's program.
//
// Task record (i64 slots):
//   [0] state (0 running/pending, 1 done)  [1] result   [2] saved stack pointer
//   [3] function address  [4..7] arguments  [8] head of waiter list
//   [9] link (ready queue / waiter list)   [10] stack block  [11] stack top  [12] limit
// Scheduler block (i64 slots, in the writable data section):
//   [0] current task   [1] ready head   [2] ready tail   [3] free stack pool
static class AsyncRuntime {
    public const int StackSize = 32768;
    public const int TaskBytes = 13 * 8;
    public const int SchedBytes = 8 * 8;

    // Intrinsics the prelude uses (implemented directly in CodeGen).
    public static readonly HashSet<string> Intrinsics = new() { "__sched", "__ctx_switch", "__ctx_init" };

    static readonly string Prelude = $@"
module __async_runtime;

i64* __rt_task_alloc() {{
    var t = (i64*)malloc({TaskBytes});
    for (var i = 0; i < 13; i = i + 1) {{ t[i] = 0; }}
    return t;
}}

// The running task. Main's task record is created lazily on first use.
i64* __rt_cur() {{
    var s = __sched();
    if (s[0] == 0) {{ s[0] = (i64)__rt_task_alloc(); }}
    return (i64*)s[0];
}}

void __rt_push_back(i64* t) {{
    var s = __sched();
    t[9] = 0;
    if (s[2] == 0) {{ s[1] = (i64)t; s[2] = (i64)t; }}
    else {{ var tail = (i64*)s[2]; tail[9] = (i64)t; s[2] = (i64)t; }}
}}

void __rt_push_front(i64* t) {{
    var s = __sched();
    t[9] = s[1];
    s[1] = (i64)t;
    if (s[2] == 0) {{ s[2] = (i64)t; }}
}}

i64* __rt_pop() {{
    var s = __sched();
    var t = (i64*)s[1];
    if (s[1] != 0) {{
        s[1] = t[9];
        if (s[1] == 0) {{ s[2] = 0; }}
        t[9] = 0;
    }}
    return t;
}}

void __rt_deadlock() {{
    Console.WriteLine(""fatal: async deadlock - every task is waiting on another task"");
    exit(1);
}}

// Transfer control to 'next'. The caller has already queued, parked or finished
// the current task.
void __rt_switch_to(i64* next) {{
    var cur = __rt_cur();
    if ((i64)next == 0) {{ __rt_deadlock(); }}
    if ((i64)next == (i64)cur) {{ return; }}
    var s = __sched();
    s[0] = (i64)next;
    __ctx_switch(cur + 2, next[2]);
}}

// Create a task for function address fn. Arguments are stored by the caller,
// then __rt_start runs it.
i64* __rt_new(i64 fn) {{
    var s = __sched();
    var t = __rt_task_alloc();
    t[3] = fn;
    var stk = (i64*)s[3];
    if (s[3] != 0) {{ s[3] = stk[0]; }}
    else {{ stk = (i64*)malloc({StackSize}); }}
    var top = (i64)stk + {StackSize};
    t[10] = (i64)stk;
    t[11] = top;
    t[12] = (i64)stk;
    t[2] = __ctx_init(top, (i64)stk);
    return t;
}}

// Start a new task eagerly: the caller goes to the FRONT of the ready queue, so it
// resumes as soon as the new task first suspends (or finishes).
void __rt_start(i64* t) {{
    __rt_push_front(__rt_cur());
    __rt_switch_to(t);
}}

void __rt_yield() {{
    __rt_push_back(__rt_cur());
    __rt_switch_to(__rt_pop());
}}

i64 __rt_await(i64* t) {{
    if (t[0] == 0) {{
        var cur = __rt_cur();
        cur[9] = t[8];
        t[8] = (i64)cur;
        __rt_switch_to(__rt_pop());
    }}
    return t[1];
}}

// Called (from __fiber_entry) when a task's function returns. Never returns.
void __rt_finish(i64 result) {{
    var s = __sched();
    var cur = __rt_cur();
    cur[1] = result;
    cur[0] = 1;
    var w = (i64*)cur[8];
    while ((i64)w != 0) {{
        var nx = (i64*)w[9];
        __rt_push_back(w);
        w = nx;
    }}
    cur[8] = 0;
    // recycle the stack: safe because nothing else runs until we switch away
    var stk = (i64*)cur[10];
    stk[0] = s[3];
    s[3] = (i64)stk;
    __rt_switch_to(__rt_pop());
}}

// After Main returns: let every ready task run to completion.
void __rt_drain() {{
    var s = __sched();
    while (s[1] != 0) {{ __rt_yield(); }}
}}
";

    public static bool Uses(ModuleNode m) {
        foreach (var f in m.Funcs) {
            if (f.IsAsync) return true;
            if (BodyAwaits(f.Body)) return true;
        }
        return false;
    }

    // Append the runtime functions to the module (only called when async is used).
    public static void Inject(ModuleNode m) {
        if (m.Funcs.Any(f => f.IsRuntime)) return;
        var rt = new Parser(new Lexer(Prelude).Lex()).ParseModule();
        foreach (var f in rt.Funcs) { f.IsRuntime = true; f.Line = 0; m.Funcs.Add(f); }
    }

    static bool BodyAwaits(List<Stmt> body) => body.Any(StmtAwaits);
    static bool StmtAwaits(Stmt? s) => s switch {
        null => false,
        VarDecl v => ExprAwaits(v.E),
        Assign a => ExprAwaits(a.E),
        IndexAssign ia => ExprAwaits(ia.Arr) || ExprAwaits(ia.Index) || ExprAwaits(ia.E),
        DerefAssign da => ExprAwaits(da.Ptr) || ExprAwaits(da.E),
        IfStmt i => ExprAwaits(i.Cond) || BodyAwaits(i.Then) || (i.Else != null && BodyAwaits(i.Else)),
        WhileStmt w => ExprAwaits(w.Cond) || BodyAwaits(w.Body),
        ForStmt f => StmtAwaits(f.Init) || ExprAwaits(f.Cond) || StmtAwaits(f.Step) || BodyAwaits(f.Body),
        RetStmt r => ExprAwaits(r.E),
        PrintStmt p => ExprAwaits(p.Arg),
        ExprStmt e => ExprAwaits(e.E),
        _ => false,
    };
    public static bool ExprAwaits(Expr? e) => e switch {
        null => false,
        AwaitExpr or YieldExpr or WhenAllExpr => true,
        Unary u => ExprAwaits(u.E),
        Binary b => ExprAwaits(b.L) || ExprAwaits(b.R),
        CallExpr c => c.Args.Any(ExprAwaits),
        TryExpr t => t.Call.Args.Any(ExprAwaits) || ExprAwaits(t.Else),
        NewArray na => ExprAwaits(na.Size),
        IndexExpr ix => ExprAwaits(ix.Arr) || ExprAwaits(ix.Index),
        LengthExpr le => ExprAwaits(le.Arr),
        Deref d => ExprAwaits(d.Ptr),
        CastExpr ce => ExprAwaits(ce.E),
        Interp itp => itp.Parts.Any(p => p is System.ValueTuple<Expr, string?> t && ExprAwaits(t.Item1)),
        _ => false,
    };
}
