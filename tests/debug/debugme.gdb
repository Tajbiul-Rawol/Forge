# Linux debug info (DWARF): breakpoints, backtrace, variables, struct fields, next, finish.
set pagination off
set print address off
break debugme.fg:10
break SumTo
run
bt
p scale
p w
p *p
p p->X * scale
continue
next
next
next
info locals
finish
p a
p name
p ratio
p ok
p squares[2]
continue
