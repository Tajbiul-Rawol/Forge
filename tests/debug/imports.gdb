# Debug info across files: a breakpoint in an imported file, a backtrace through two files.
set pagination off
set print address off
break geometry.fg:9
break Corner
run
bt
p *a
p *b
p dx
continue
bt
p k
finish
delete
continue
