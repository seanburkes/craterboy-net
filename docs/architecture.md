# Architecture

`Craterboy.Core` owns all emulated state. `Emulator` is the public aggregate
and the only authority that advances T-cycles. Host time, entropy, and serial
I/O enter through explicit interfaces; file access and presentation stay out
of the core.

The current state kernel groups CPU registers and the master scheduler in
`EmulatorState`. The scheduler advances registered participants one T-cycle at
a time; the timer device is the first participant and owns DIV/TIMA/TMA/TAC
edge behavior. This keeps device timing deterministic and prevents individual
devices from advancing themselves or consulting wall-clock time.

OAM DMA and serial transfer devices are also scheduler participants. DMA copies
one byte per four T-cycles for 160 bytes, while the serial endpoint completes an
internal-clock transfer after eight 512-T-cycle bit periods. CPU bus blocking,
external serial clocks, and CGB HDMA remain deferred.

The joypad is an active-low bus device with explicit button state injection.
FF00 selection changes and button presses request the joypad interrupt on a
high-to-low line transition; SGB multiplayer input is deferred.

The PPU timing kernel is also scheduler-owned. It models DMG mode 2/3/0
transitions, VBlank lines, LY/LYC coincidence, and STAT interrupts. The first
renderer slice draws the DMG background tile map with SCX/SCY and BGP into raw
160×144 pixels. Window positioning and its independent tile-map line counter
are modeled, along with DMG 8×8 sprite composition; FIFO behavior and 8×16
sprite selection remain separate.

The implementation favors explicit state and opcode behavior over object
layout compatibility with C. Serialization will be field-by-field and
transactional. `PeekMemory` is intended to remain side-effect free while
`ReadMemory` and `WriteMemory` represent bus operations as devices gain
read/write side effects.

`Craterboy.Tester` is a headless conformance entry point. A native SameBoy
adapter will live only in tests/CI and will never be included in packages.
