# Forge 1.0: production-ready checklist

"Is Forge production ready?" is answered against this list, and only this list.
Forge is production ready when every box is checked. Anything not on the list is
post-1.0 work. It does not reopen the question.

Each item needs its tests in `tests/run.sh` and its documentation in `README.md`.

- [x] **1. Array bounds checks.** Every `a[i]` / `a[i] = v` on an array is checked and
      reports the line; `--no-bounds-check` opts out. (`BoundsCheck.cs`,
      `examples/bounds.fg`, `examples/bounds_loop.fg`)
- [x] **2. Multi-file programs.** One `.fg` file can import another: `import "lib/x.fg";`.
      (`Imports.cs`, `examples/imports.fg`, `tests/errors/import_*.fg`)
- [ ] **3. Better errors.** Errors carry line and column, and the parser reports more than
      one syntax error.
- [ ] **4. Debug info.** A compiled program can be stepped through in a debugger (PDB on
      Windows, DWARF on Linux).
- [ ] **5. Indexers.** `list[i]` and `dict[key]` on `List<T>` and `Dictionary<K,V>`.
- [ ] **6. README matches the compiler.** Remove stale claims such as "function calls
      (up to 4 parameters)".
- [ ] **7. Tests stay green.** The suite, the fuzzer and CI pass, with tests for each item
      above.

## Post-1.0 (not part of the definition)

Classes and inheritance, interfaces, generic constraints, 32-bit `float`, a garbage
collector or destructors, string bounds checks, `double` on the kernel target, booting
on real hardware.
