# Architecture

`Craterboy.Core` owns all emulated state. `Emulator` is the public aggregate
and the only authority that advances T-cycles. Host time, entropy, and serial
I/O enter through explicit interfaces; file access and presentation stay out
of the core.

The current state kernel groups CPU registers and the master scheduler in
`EmulatorState`. The scheduler advances registered participants one T-cycle at
a time; the timer device is the first participant and owns DIV/TIMA/TMA/TAC
edge behavior, including TAC/DIV write falling-edge increments and the delayed
TIMA reload window. This keeps device timing deterministic and prevents
individual devices from advancing themselves or consulting wall-clock time.

OAM DMA and serial transfer devices are also scheduler participants. DMA copies
one byte per four T-cycles for 160 bytes, while the serial endpoint completes an
internal-clock transfer after eight 512-T-cycle bit periods. During OAM DMA,
CPU accesses are blocked outside HRAM and IE while the DMA source bus remains
active. `Emulator.ClockSerialBit()` supplies external serial clock edges for
link endpoints. CGB-family general DMA transfers immediate 16-byte blocks, while
HBlank DMA transfers one 16-byte block at each visible-line HBlank for the
CGB-family models, from the
CPU address space into the selected VRAM bank through FF51-FF55.
Active HBlank transfers cancel on an FF55 stop request or LCD disable. DMA source,
destination, status, and active-transfer state are included in deterministic hashes.
High RAM state is included as well so checkpoint data covers the complete
FF80-FFFE scratch range.

The joypad is an active-low, scheduler-owned bus device with explicit button
state injection. DMG/MGB FF00 selection changes model their hardware switching
delay; MGB uses the shorter model-specific delay. Selection changes or button
presses request the joypad interrupt on a high-to-low line transition.
Opposing direction inputs are filtered to the hardware-compatible
single-direction result; SGB multiplayer input is deferred.

The PPU timing kernel is also scheduler-owned. It models DMG mode 2/3/0
transitions, including the SCX fine-scroll and WX=0 window-fetch penalties,
VBlank lines, LY/LYC coincidence, and STAT interrupts. The first
renderer slice draws the DMG background tile map with SCX/SCY and BGP into raw
160×144 pixels. Window positioning and its independent tile-map line counter
are modeled, along with DMG sprite composition, 8×16 tile selection, and DMG
overlap priority; FIFO behavior remains separate.
CGB indexed BG/OBJ palette registers and their auto-incrementing palette RAM
are modeled at the bus boundary and feed a caller-owned raw RGB15 color frame.
CGB VBK selects the active 8 KiB CPU VRAM bank; bank-aware tile composition
remains deferred with the rest of the color renderer, and its selection is
included in deterministic hashes.
CGB SVBK selects the active 4 KiB D000-DFFF work-RAM bank and its echo; banked
work-RAM state is included in deterministic hashes.
CGB-family BG/OBJ palette RAM is also included in deterministic hashes so color state
changes are visible to replay checkpoints.
PPU timing, LCD mode, coincidence, and window-line state are included alongside
the palette state.
DMG BGP palette-register state is included in deterministic hashes as well.
Timer divider precision and delayed reload state are included as well.
DMG/MGB APU channel phases, frame-sequencer state, and queued samples are
included too.
APU mixer volumes and channel routing are included in deterministic hashes too.
Serial internal-clock and external-bit transfer progress are included as well.
OAM DMA source, phase, and byte progress are included too.
Joypad button and model-specific delayed-selection state are included as well;
DMG-B/MGB selection switching is delayed, while CGB-family selection changes are
immediate.
Cartridge mapper bank, enable, latch, and RTC control state are included too.
Cartridge ROM and configured DMG/CGB-family boot-ROM identities are included in
the hash input, along with whether the boot ROM is currently mapped. The
selected hardware model identity is included as well, so CGB, AGB, and GBP
checkpoints remain distinct.
CGB-family KEY1 exposes the current-speed and speed-switch preparation bits, and
both are included in deterministic hashes; actual STOP with preparation toggles
the modeled speed state and clears preparation on CGB, AGB, and GBP models;
following CPU instructions consume half as many scheduler T-cycles on CGB, AGB,
and GBP models while PPU and timer participants continue at the normal hardware
cadence.
CGB-family OPRI exposes the object-priority mode bit, which is included in
deterministic hashes; the sprite compositor uses it to select OAM-index versus
X-based overlap order for both indexed and RGB15 color frames.
CGB, AGB, and GBP background/window fetches consume VRAM-bank and X/Y-flip tile attributes;
CGB, AGB, and GBP palette-index composition honors background and sprite palette indices, while
nonzero background pixels honor both the CGB BG-priority attribute and the sprite
behind-background attribute against sprites across CGB, AGB, and GBP models. CGB
sprite fetches consume the OAM tile-bank, X/Y-flip, and palette-index attributes
across CGB, AGB, and GBP models.
CPU-visible VRAM and OAM access is blocked during the DMG transfer modes and
restored during HBlank/VBlank. STAT sources share edge-triggered line logic so
enabling an already-active source raises the interrupt once.
Disabling the LCD resets LY/timing state and blanks the raw frame buffer.

The APU is now a scheduler-owned register and power-control device. NR52
gates access to the channel registers; its power state is included in
deterministic hashes. Powering it off resets channel state, frame-sequencer
timing, and queued samples. Channel sequencing and sample emission
remain deferred to later APU slices. Channel 1 has basic trigger, length, and
NR52 status timing plus the envelope frame step.
NR50/NR51 mixer volumes and channel routing are included in deterministic
hashes as well.
MGB follows the DMG-class channel and power-control behavior; CGB-family PCM
register behavior remains model-specific.
Clearing a channel DAC control immediately disables that channel and updates
NR52 status.
APU register reads apply SameBoy’s fixed high-bit and write-only masks, including
the fixed NR52 status bits.
Channel 1 frequency sweep updates are clocked on frame-sequencer steps 2 and
6, with the NR10 period selecting the number of sweep events between
updates; live NR10 writes reconfigure active sweep timing, and trigger-time
overflow disables the channel. The sweep shadow frequency remains tied to the
trigger while live NR13/NR14 writes update playback. The core emits channel
samples into a bounded managed ring and exposes
caller-owned buffer draining; host playback remains outside the core.
Channel 2 trigger, length timing, status, and PCM mixing are now present.
Channel 3 wave RAM, volume coding, trigger/length timing, and PCM mixing are
also present.
DMG channel 3 wave-RAM reads and writes are restricted while the channel is
active, matching SameBoy’s inaccessible active-wave behavior.
CGB PCM12 and PCM34 reads expose the current digital channel amplitudes.
Channel 4 noise LFSR, trigger/length timing, status, and PCM mixing are also
present.
Channel 1 and channel 2 pulse phases reset on trigger and advance from their
frequency registers, keeping their duty waveforms independent. Active channels
also apply high-frequency register writes without requiring a retrigger.
NR50 master volume and NR51 per-channel routing now shape the emitted PCM.
Channel 1, channel 2, and channel 4 envelopes clock on frame-sequencer step 7.
Length counters clock on frame-sequencer steps 0, 2, 4, and 6.
Channel 3 frequency registers now drive its managed wave phase progression.
Channel 4 NR43 divisor and shift fields control the deterministic LFSR cadence.
Channel 3 NR32 volume code 0 correctly mutes its PCM contribution.

The implementation favors explicit state and opcode behavior over object
layout compatibility with C. BESS container parsing now validates the
little-endian footer, block bounds, required `CORE`/`END` structure, and
known-block ordering while preserving unknown blocks for forward-compatible
callers. Its `CORE` parser validates version/model/execution metadata and
exposes CPU, I/O, and external-buffer descriptors; emulator state loading
remains field-by-field and transactional.
`PeekMemory` is intended to remain side-effect free while
`ReadMemory` and `WriteMemory` represent bus operations as devices gain
read/write side effects.

CPU memory-transfer instructions now cover absolute A loads/stores, high-page
(`FF00+n`) loads/stores, C-indexed high-page loads/stores, and storing SP to an
absolute address. Their operand widths and 8/12/16/20 T-cycle timings are kept
explicit in the decoder and compared with SameBoy.

The `(HL+)` and `(HL-)` forms perform the bus access before updating HL, while
`LD (HL),d8` consumes its immediate operand without changing flags. These
forms use 8 and 12 T-cycles respectively.

`JP (HL)` transfers control directly to the current 16-bit HL value in 4
T-cycles. `STOP` consumes its required padding byte, enters the halted state,
and also takes 4 T-cycles in the current DMG execution model.

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

`EI` tracks a one-instruction delayed IME enable in CPU state, and `DI` clears
both IME and a pending enable. The pending state is included in deterministic
state hashes so replay checkpoints distinguish the interrupt boundary.

When IME is active, pending IE/IF bits are serviced before the next opcode in
priority order (VBlank, STAT, timer, serial, joypad). Service clears the chosen
IF bit, pushes PC, jumps to the vector, disables IME, and consumes 20 T-cycles.
An interrupt request wakes HALT even when IME is disabled; in that case the
CPU resumes instruction execution without servicing the request.

The DMG interrupt bus exposes IF (`FF0F`) with fixed high bits (`111`) and only
stores its low five request bits. IE (`FFFF`) is retained as the raw enable
register value; interrupt arbitration uses its low five bits, so fixed or
unmapped high bits cannot wake HALT or dispatch an interrupt.

Signed SP-relative operations use the unsigned low-nibble and low-byte views
of the offset for H/C, as required by the SM83, while applying the offset as a
signed byte to the 16-bit result. `ADD SP,e8` takes 16 T-cycles, `LD HL,SP+e8`
takes 12, and `LD SP,HL` takes 8 without changing flags.

The register ALU decoder now covers `ADC A,r`, `SBC A,r`, and `XOR A,r`,
including `(HL)` forms. These operations share the immediate ALU flag rules
and retain 4 T-cycles for registers and 8 T-cycles for `(HL)`.

The existing `ADD`, `SUB`, `AND`, `OR`, and `CP` register ALU paths use the
same 4/8-T-cycle register-versus-memory timing. The complete `0x80–0xBF`
ALU block is covered by an opcode-by-opcode differential sweep.

`InputRecording` provides a versioned, cycle-ordered event stream for
deterministic replay; malformed recordings—including invalid headers, fields,
out-of-order events, bounded event counts, truncation, and trailing data—are
rejected as `InvalidDataException` while parsing the non-null source stream;
null stream arguments are rejected.
`Emulator.ReplayInputRecording` applies events at exact emulated cycles. Replay
tests compare complete `ComputeStateHash` checkpoints
across DMG, MGB, and CGB.
Recording reads and writes leave caller-owned streams open; writes finish at the
current end position without closing the destination.
Reads do not require seekable sources, and writes do not require seekable
destinations, so recordings can use forward-only adapters in both directions.
The exposed event list is read-only so replay inputs cannot be mutated behind
the recording's validation boundary.
Recordings currently accept only the primary player; SGB multiplayer input is
deferred with the rest of the SGB host bridge.
`ComputeStateHash` provides a stable SHA-256 digest for replay/conformance
comparisons without exposing mutable emulator state, including cartridge battery
state when a cartridge is loaded.

`Craterboy.Tester` is a headless conformance entry point. A native SameBoy
adapter will live only in tests/CI and will never be included in packages.
