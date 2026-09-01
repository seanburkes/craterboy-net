# SameBoy-to-Craterboy port map

Baseline: SameBoy 1.0.3, commit
`213a12ce93d66b105a113debd9396306066a7cfc`.

Status meanings: **partial** is implemented but not oracle-complete;
**deferred** has no claim of implementation.

| SameBoy source | Craterboy area | Status | Current evidence |
|---|---|---|---|
| `Core/model.h`, reset portions of `Core/gb.c` | `GameBoyModel`, `Emulator.Reset` | partial | native model-ID and post-boot register comparisons; explicit DMG, MGB, CGB-revision, AGB, GBP, SGB, and SGB2 family classification helpers; CGB/AGB/GBP model-identity state-hash comparisons |
| `Core/memory.c` | `Emulator.Read/Write` | partial | native CGB-family SVBK work-RAM banking, CGB-family VBK VRAM banking, WRAM, echo-RAM, CGB DMA boot-ROM precedence and E000-EFFF source normalization, OAM, I/O, and high-RAM/IE DMA source reads, 8 KiB VRAM destination wrapping, general and HBlank DMA timing/block transfers including immediate start during an active visible HBlank, halt/stop gating and HBlank wake transfer, and stop-request/LCD-disable cancellation or one-block transfer outside HBlank, OAM DMA progress hashing, CGB DMA source/destination/active-progress hashing, high-RAM state hashing, CGB-family VRAM/WRAM bank-selection hashing, cartridge mapper/RTC control-state and DMG/CGB-family ROM, boot-ROM identity, and boot-ROM mapping-state hashing, DMG unusable-range, absolute/high-page, and auto-indexed CPU transfer comparisons |
| `Core/mbc.c` | `Cartridge` implementations | partial | ROM/MBC1/MBC1M/MBC2/MBC5/MBC3 banking and RAM tests; MMM01 startup mapping, lock, mask, multiplex, ROM/RAM banking, battery, and native-oracle coverage; HuC1 ROM/RAM banking and infrared-register endpoint/oracle coverage; HuC3 banking, nibble RTC/alarm commands, host-time advancement, infrared, SameBoy battery/BESS state, and oracle coverage; MBC6 independent 8 KiB ROM and 4 KiB SRAM windows plus flash-control state from Pan Docs, with flash commands/persistence deferred because pinned SameBoy has no MBC6 implementation; MBC7 ROM/register gating, caller-owned motion, accelerometer latching, serial EEPROM commands, battery/BESS persistence, and oracle coverage, with rumble unresolved because no control register is documented; TAMA5 two-nibble ROM banking, 32-byte nibble-command EEPROM, TAMA6 clock/alarm/free-page access, host-time advancement, and battery persistence, with managed protocol tests because pinned SameBoy 1.0.3 marks the mapper unsupported; MBC3 RTC fixture, SameBoy-compatible battery format, and stream persistence |
| `Core/sm83_cpu.c` | `Emulator.Execute` | partial | exhaustive base and CB opcode state/cycle differential matrices; register-transfer block, register ADC/SBC/XOR, indirect HL jump and STOP including padding-read timing, HALT state and HALT-bug hashing, immediate accumulator ALU, signed SP-relative operations, 16-bit pair arithmetic, accumulator rotates/status, delayed EI/DI control and interrupt-boundary state hashing, conditional calls/returns, RETI and RST, `(HL)` INC/DEC, explicit immediate load/store, ALU, INC/DEC, CB-prefixed rotate/shift, BIT/RES/SET, relative/absolute branch, stack, CALL/RET instructions, and SameBoy-compatible illegal-opcode halt behavior |
| `Core/timing.c` | `Scheduler`, `EmulatorState`, and cycle execution authority | partial | per-instruction native T-cycle comparisons; participant scheduler tests; timer divider precision and delayed reload state hashing; serial transfer progress hashing; joypad button and model-specific selection-progress hashing through explicit DMG/MGB predicates, including DMG-B and shortened MGB selection timing plus immediate CGB-family selection; CGB-family KEY1 current-speed and preparation-state hashing, prepared-STOP, and double-speed CPU cadence behavior across CGB/AGB/GBP; prioritized interrupt dispatch and 20-T-cycle service tests; IF/IE interrupt-register bus behavior, state hashing, and high-bit wake/dispatch edge coverage |
| `Core/timing.c`, timer portions of `Core/gb.c` | `TimerDevice` | partial | divider cadence, timer falling-edge, TAC/DIV write glitches, delayed TIMA reload window including zero-valued TIMA reads during active reload, and overflow interrupt tests |
| `Core/random.c` | `IEntropyProvider` | partial | injectable boundary defined |
| MBC3/RTC interoperability and remaining mappers | cartridge subsystem | deferred/partial | deterministic managed RTC exists; BESS MBC3 live/latched RTC state now round-trips through `Emulator.SaveBess`/`LoadBess`; battery saves write SameBoy's padded 64-bit timestamp format and read compact/32-bit legacy variants; remaining mapper parity remains deferred |
| timer, joypad, serial, DMA | device state/scheduler | partial | timer, OAM DMA bus blocking and transfer, internal/external serial clocks including the CGB fast cadence, model-specific SC fixed-bit readback, SC-write transfer-progress reset, DMG/MGB JOYP selection delay, opt-in SameBoy-compatible DMG/MGB/AGB joypad bouncing and faux-analog dithering, opposing-direction filtering, and FF00 joypad edge tests |
| `Core/display.c` | `PpuDevice` | partial | LCD mode timing/reset and LCD state hashing, SCX fine-scroll and WX=0 window/sprite-fetch penalties, edge-triggered LY/LYC/STAT interrupts and coincidence-state hashing, CPU VRAM/OAM access blocking including early-CGB double-speed mode-2 access, the double-speed first-HBlank bus delay, and the delayed STAT mode transition, first DMG OAM-search row-corruption behavior including the row-0x80 copy-to-start variant, CGB-family VBK bank selection, background/window and sprite VRAM-bank, flip, and palette attributes, CGB BG-priority composition including RGB15 output, indexed BG/OBJ palette RAM and CGB/AGB/GBP RGB15 background/window/sprite palette-index frames, stable 160×144 model-native raw-frame access with explicit monochrome-shade/RGB15 formats, preallocated sprite scratch storage and allocation-free warmed frame execution, CGB revision and AGB/GBP indexed-frame tests, CGB background/window/sprite palette-frame tests including transparent sprites, CGB/AGB/GBP sprite-behind-background priority, CGB/AGB/GBP banked/flipped window tile data, CGB/AGB/GBP banked/flipped background and sprite tile data, CGB-family LCD-disable color-frame reset, CGB-family BG/OBJ palette-index/auto-increment and CGB-family palette/timing/window state hashing, CGB-family mode-3 and first-HBlank palette-data access blocking with auto-increment preservation, CGB-family OPRI register-mask/state hashing and indexed/RGB15 overlap-order tests, DMG palette-register state hashing, DMG background/window/sprite tile and palette rendering including 8×16 sprites and overlap priority, and raw frame-buffer tests |
| `Core/apu.c` | `ApuDevice` | partial | DMG/MGB channel and power-control behavior, NR50/NR51 mixer routing and mixer-state hashing, preallocated interleaved stereo output with independent left/right routing and caller-owned frame draining, NR52 power-cycle timing/sample reset and power-state hashing, APU register read masks, live DAC-disable gating, channel 1/2 frequency-driven pulse phases and live frequency writes, channel 1 sweep period including zero-as-eight timing and frame-sequencer cadence, all four channel length-enable divider-edge behavior including the older-CGB exception, channel 1/2/3/4 length steps 0/2/4/6, trigger shadow frequency, live NR10 writes, negate-transition overflow, channel 1/2/4 envelope step-7 timing, trigger-overflow behavior, channel 3 frequency/wave RAM and NR32 mute, DMG/MGB retrigger wave-RAM corruption copy groups, active DMG restrictions, CGB-D/E current-byte wave-RAM access, AGB/GBP active-wave open-bus behavior, CGB-family PCM12/PCM34 readback including CGB-0 through CGB-C revision masks at envelope, pulse-waveform, and noise-LFSR edges plus the CGB-0 channel-two rising-envelope exception, channel 4 NR43 noise cadence including immediate countdown restart on live writes, four channel trigger/length/status, bounded PCM mixing, and deterministic channel/sample-state hashing; hardware fidelity remains partial |
| `Core/save_state.c` | `InputRecording` / BESS | partial | BESS reader validates the little-endian footer, block bounds, required CORE/END structure, duplicate known blocks, known-block ordering, and empty END while preserving repeated unknown blocks; BESS writer appends validated ordered blocks and emits END/footer, including field-by-field CORE serialization plus typed INFO/NAME/MBC/RTC/XOAM/MBC7/HUC3/TPP1/SGB serialization and writer-side descriptor zero-offset and 32-bit range invariants; CORE parsing validates version/model/execution metadata, exposes CPU/I/O/external-buffer descriptors, rejects overflow or out-of-file buffer ranges, and provides validated external-buffer extraction; optional INFO parsing exposes ROM title bytes and global checksum, optional NAME parsing exposes the ASCII producer identifier, optional MBC parsing exposes ordered mapper register writes with address/length validation, optional RTC parsing exposes current/latched clock fields and save timestamp with exact-length validation, optional XOAM parsing exposes the fixed extra-OAM bytes with exact-length validation, optional MBC7 parsing exposes EEPROM/motion state with exact-length and reserved-flag validation, optional HUC3 parsing exposes RTC/alarm state with exact-length and boolean validation, optional TPP1 parsing exposes raw RTC/MR4 state with exact-length validation, and optional SGB parsing exposes border/palette/attribute descriptors, validated multiplayer state, and in-file buffer bounds; emulator BESS saves now emit ROM INFO/NAME metadata and loads validate matching INFO metadata; versioned streaming input-recording reads from seekable/forward-only sources and writes to seekable/forward-only destinations, with read-only event exposure, null-argument validation, caller-owned stream lifetime and primary-player scope, exact-cycle replay, DMG/MGB/CGB checkpoint-hash replay coverage, header and event-count bounds, ordering and invalid-field/truncation/trailing-data validation with a uniform `InvalidDataException` parse contract, and deterministic hashing including cartridge state; field-level BESS state remains deferred |
| `Core/camera.c`, camera portions of `Core/memory.c` | Pocket Camera cartridge | partial | MBC5-style ROM/RAM banking, register aperture/masks/readback, busy-state RAM gating, battery storage, capture-completion timing, and exposure/gain/edge/dither image reads from caller-owned pixels, plus pinned SameBoy mapper differential coverage; platform-specific image acquisition remains deferred |
| `Core/printer.c` | `GameBoyPrinter` serial endpoint | partial | packet framing, additive checksums, ACK/status responses, compressed and uncompressed data, 2bpp tile decoding, palette/margin/exposure print jobs, and INIT handling; mechanical printing duration and completion notification remain deferred |
| rumble, WorkBoy | accessories | deferred | — |
| SGB, debugger, cheats, rewind | extended services | deferred | — |

The APU mapping now also covers SameBoy's writable wave RAM while the APU is powered off.
Timer mapping now also covers the DIV reset on STOP entry, including prepared
CGB speed switches, DIV/TIMA pause during STOP, and resumption on wake.
The CPU mapping also covers SameBoy's immediate STOP exit when the active-low
JOYP line is already asserted, including suppression of a prepared CGB speed
switch, and preserves the STOP padding byte when an interrupt is pending.
The exhaustive base-opcode matrix also runs every opcode from varied register,
flag, stack, and indirect-memory states instead of relying only on reset state.
Common retail MBC1, MBC2, MBC3, and MBC5 reset coverage restores mapper controls
while retaining battery-backed contents and pending-save state.
The retail qualification report now replays its first deterministic checkpoint
after same-instance ROM reload and reset to expose cross-session state leakage.
Its default run is the full ten-minute retail gate at the DMG hardware clock,
with bounded one-second checkpoints and explicit duration completion evidence.
Input-assisted qualification distinguishes supplied events from events applied
within the requested run, reports frame changes observed after input begins,
and compares checkpoint and final frames with a deterministic no-input control
replay, including the first divergent checkpoint cycle.
DMG/MGB powered-off writes to the four channel-length registers are covered as well; CGB-family channel writes remain gated.
The display mapping also covers VBlank interrupt requests, SameBoy's read-only
LY register and later-CGB normal-speed first-HBlank
OAM-read block; OAM writes remain available during that one-T-cycle window.
Background and window output now advances progressively through mode 3, including
mid-scanline DMG palette changes. A visible window start pauses output for its
six-dot fetcher restart, including the WX=0 fine-scroll edge case.
Mode-2-selected sprites compose at the same progressive pixel boundary.
DMG/MGB sprite fetches now apply the documented
six-to-eleven-dot penalty, shared-background-tile suppression, and OAM X=0
exception. Disabling objects cancels an active DMG/MGB fetch and pending fetches,
while reenabling restores not-yet-reached penalties. Mid-scanline WX writes move
only a pending window trigger; an active window retains its triggered position.
The enabled-window LY=WY match latches for the frame and ignores retroactive or
post-match WY changes. Clearing the LCDC window bit clears an active horizontal
trigger, except that a DMG/MGB tile fetch already in progress completes its
eight fetched pixels before returning to the background. Reenabling can retrigger
only at a future WX boundary. Native CGB-mode stalls remain partial.

No SameBoy private native-state compatibility is planned. BESS will be the
interoperable save-state format. A layer moves from partial to ported only
after differential evidence is added against the pinned native oracle.

The typed `BessWriter.WriteCoreWithBuffers` path now lays out CORE external
buffers, patches absolute descriptors, and emits the matching CORE/END block
container; empty buffers use the format's zero descriptor convention.
The writer overload accepts ordered pre-CORE and post-CORE typed blocks so
external memory and optional metadata can be emitted as one state container.
CORE model validation accepts the defined SameBoy family/revision identifiers
and rejects unsupported prefixes or revision placements at both boundaries.
`BessReader.ReadCoreWithBuffers` provides one-pass validated CORE metadata and
owned external-buffer snapshots for transactional state loading.
`BessReader.ReadSnapshot` adds the typed optional metadata to the same
one-pass, owned aggregate and preserves null for absent sections.
SGB descriptors in that aggregate use the same in-file bounds checks as the
dedicated SGB reader.
Validated SGB snapshots include owned border, palette, and attribute bytes for
transactional loading.
`BessWriter.WriteCoreAndSgbWithBuffers` emits matching CORE/SGB external
buffers without caller-managed file offsets.
`BessWriter.WriteSnapshot` serializes the aggregate typed snapshot back into a
complete BESS container and requires SGB external buffers when SGB is present.
Round-trip coverage now exercises every typed optional persistence section.
`Emulator.SaveBess` emits live register, I/O, memory, and cartridge battery
state through the managed BESS snapshot path. `Emulator.LoadBess` now validates
the model, required buffer sizes, and unsupported optional blocks before
resetting and applying CORE CPU/I/O/memory/battery state, then replays ordered
`MBC ` mapper writes and restores MBC3 RTC state; device timing and other
accessory state remain separate follow-up work.
Writer preflight rejects invalid ordering before emitting external data,
preserving the transactional write boundary.
MBC3 BESS snapshots now carry and restore the live and latched RTC registers;
timestamp and register-range validation happens before core state is mutated.

The test-only oracle is built from the pinned checkout by
`tests/native/build-oracle.sh`. Its ABI deliberately exposes only model IDs,
register snapshots, memory operations, and single-instruction execution.
SameBoy's 8 MHz tick result is normalized to T-cycles at that boundary. The
native artifacts are ignored by Git and are not referenced by the shipping
core project.
