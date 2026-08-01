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
are modeled, along with DMG sprite composition, 8×16 tile selection, and DMG
overlap priority; FIFO behavior remains separate.
CPU-visible VRAM and OAM access is blocked during the DMG transfer modes and
restored during HBlank/VBlank. STAT sources share edge-triggered line logic so
enabling an already-active source raises the interrupt once.
Disabling the LCD resets LY/timing state and blanks the raw frame buffer.

The APU is now a scheduler-owned register and power-control device. NR52
gates access to the channel registers; channel sequencing and sample emission
remain deferred to later APU slices. Channel 1 has basic trigger, length, and
NR52 status timing plus the envelope frame step.
Channel 1 frequency sweep updates are also clocked at the four-step sweep
cadence. The core emits channel samples into a bounded managed ring and exposes
caller-owned buffer draining; host playback remains outside the core.
Channel 2 trigger, length timing, status, and PCM mixing are now present.
Channel 3 wave RAM, volume coding, trigger/length timing, and PCM mixing are
also present.
Channel 4 noise LFSR, trigger/length timing, status, and PCM mixing are also
present.
NR50 master volume and NR51 per-channel routing now shape the emitted PCM.
Channel 2 envelope timing now mirrors channel 1’s frame-step behavior.
Channel 4 envelope timing is covered by the same deterministic cadence.
Channel 3 frequency registers now drive its managed wave phase progression.
Channel 4 NR43 divisor and shift fields control the deterministic LFSR cadence.
Channel 3 NR32 volume code 0 correctly mutes its PCM contribution.

The implementation favors explicit state and opcode behavior over object
layout compatibility with C. Serialization will be field-by-field and
transactional. `PeekMemory` is intended to remain side-effect free while
`ReadMemory` and `WriteMemory` represent bus operations as devices gain
read/write side effects.

The CPU decoder includes the complete CB-prefixed instruction family. Rotate,
shift, and SWAP operations update Z/N/H/C explicitly, while BIT preserves the
carry flag and sets H. Register and `(HL)` forms share the decoder but retain
their distinct 8/12/16 T-cycle timings. Every CB opcode is compared with the
pinned SameBoy oracle in the differential suite.

The ordinary register-transfer block (`LD r,r'`) is also decoded generically,
including the memory forms that use `(HL)`. The HALT opcode remains a separate
control-flow operation, and the transfer block is covered opcode-by-opcode
against SameBoy.

Immediate accumulator arithmetic and logic (`ADD`, `ADC`, `SUB`, `SBC`, `AND`,
`XOR`, `OR`, and `CP` with an 8-bit operand) uses the same flag helpers as the
register ALU operations while retaining the instruction-specific 8 T-cycle
timing. These immediate forms are included in the differential suite.

The memory forms `INC (HL)` and `DEC (HL)` reuse the byte flag behavior while
performing the read-modify-write through the bus and taking 12 T-cycles.

The 16-bit pair operations (`INC`/`DEC` on BC, DE, HL, and SP, plus `ADD HL,
rr`) are explicit and retain the Game Boy rule that `ADD HL,rr` preserves Z
while recalculating H and C. Each operation takes 8 T-cycles.

The unprefixed accumulator rotations (`RLCA`, `RRCA`, `RLA`, and `RRA`) clear
Z/N/H and expose the shifted-out bit through C, with the prior C entering only
the non-circular forms. They execute in 4 T-cycles.

`DAA` now performs decimal correction after addition and subtraction, while
`CPL`, `SCF`, and `CCF` implement their documented flag-preservation rules.
These accumulator/status instructions also execute in 4 T-cycles.

Conditional `CALL` and `RET` consume their shorter not-taken timings and use
the shared stack path when taken. `RETI` restores the return address and enables
IME, while the eight `RST` vectors push the post-instruction PC before jumping
to their fixed destinations.

`InputRecording` provides a versioned, cycle-ordered event stream for
deterministic replay; malformed recordings are rejected before publication and
`Emulator.ReplayInputRecording` applies events at exact emulated cycles.
`ComputeStateHash` provides a stable SHA-256 digest for replay/conformance
comparisons without exposing mutable emulator state, including cartridge battery
state when a cartridge is loaded.

`Craterboy.Tester` is a headless conformance entry point. A native SameBoy
adapter will live only in tests/CI and will never be included in packages.
