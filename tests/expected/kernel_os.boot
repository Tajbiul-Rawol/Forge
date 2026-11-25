Forge OS: 64-bit, ring 0, no libraries.
interrupts on: timer 100 Hz, PS/2 keyboard. Type 'help'.
forge> help
commands: ticks uptime timer keys paging divzero reboot halt
forge> timer
timer: 10 ticks of IRQ 0 (100 Hz)
forge> paging
paging: 0-1 GB identity mapped with 2 MB pages
paging: wrote through the new 1 GB mapping, read the same value from physical memory
forge> frobnicate
unknown command 'frobnicate'
forge> keys
8 key(s) from the PS/2 keyboard: forge os
forge> divzero
EXCEPTION: divide error (#DE)
system halted.
