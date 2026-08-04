# SameBoy-to-Craterboy port map

Baseline: SameBoy 1.0.3, commit
`213a12ce93d66b105a113debd9396306066a7cfc`.

Status meanings: **partial** is implemented but not oracle-complete;
**deferred** has no claim of implementation.

| SameBoy source | Craterboy area | Status | Current evidence |
|---|---|---|---|
| `Core/model.h`, reset portions of `Core/gb.c` | `GameBoyModel`, `Emulator.Reset` | partial | native model-ID and post-boot register comparisons |
| `Core/memory.c` | `Emulator.Read/Write` | partial | native CGB-family SVBK work-RAM banking, VRAM, WRAM, echo-RAM, general and HBlank DMA timing/block transfers with cancellation, OAM DMA progress hashing, cartridge mapper/RTC control-state and ROM-identity hashing, DMG unusable-range, absolute/high-page, and auto-indexed CPU transfer comparisons |
| `Core/mbc.c` | `Cartridge` implementations | partial | ROM/MBC1/MBC2/MBC5/MBC3 banking and RAM tests; MBC3 RTC fixture and stream persistence |
| `Core/sm83_cpu.c` | `Emulator.Execute` | partial | register-transfer block, register ADC/SBC/XOR, indirect HL jump and STOP, immediate accumulator ALU, signed SP-relative operations, 16-bit pair arithmetic, accumulator rotates/status, delayed EI/DI control, conditional calls/returns, RETI and RST, `(HL)` INC/DEC, explicit immediate load/store, ALU, INC/DEC, CB-prefixed rotate/shift, BIT/RES/SET, relative/absolute branch, stack, CALL/RET instructions with per-instruction native comparisons |
| `Core/timing.c` | `Scheduler`, `EmulatorState`, and cycle execution authority | partial | per-instruction native T-cycle comparisons; participant scheduler tests; timer divider precision and delayed reload state hashing; serial transfer progress hashing; joypad button and selection-progress hashing; CGB KEY1, prepared-STOP, and double-speed CPU cadence behavior; prioritized interrupt dispatch and 20-T-cycle service tests; IF/IE interrupt-register bus behavior and high-bit wake/dispatch edge coverage |
| `Core/timing.c`, timer portions of `Core/gb.c` | `TimerDevice` | partial | divider cadence, timer falling-edge, TAC/DIV write glitches, delayed TIMA reload window, and overflow interrupt tests |
| `Core/random.c` | `IEntropyProvider` | partial | injectable boundary defined |
| MBC3/RTC interoperability and remaining mappers | cartridge subsystem | deferred/partial | deterministic managed RTC exists; SameBoy battery/RTC byte-format parity remains deferred |
| timer, joypad, serial, DMA | device state/scheduler | partial | timer, OAM DMA bus blocking and transfer, internal/external serial clocks, DMG/MGB JOYP selection delay, opposing-direction filtering, and FF00 joypad edge tests |
| `Core/display.c` | `PpuDevice` | partial | LCD mode timing/reset, SCX fine-scroll and WX=0 window/sprite-fetch penalties, edge-triggered LY/LYC/STAT interrupts, CPU VRAM/OAM access blocking, CGB VBK bank selection, background/window and sprite VRAM-bank, flip, and palette attributes, CGB BG-priority composition including RGB15 output, indexed BG/OBJ palette RAM and CGB/AGB/GBP RGB15 background/object/window palette-index frames, CGB revision and AGB/GBP indexed-frame tests, CGB background/window/sprite palette-frame tests including transparent sprites, CGB/AGB/GBP sprite-behind-background priority, CGB/AGB/GBP banked/flipped window tile data, CGB/AGB/GBP banked/flipped background and sprite tile data, LCD-disable color-frame reset, CGB palette/timing/window state hashing, OPRI object-priority register and indexed/RGB15 overlap-order tests, DMG background/window/sprite tile and palette rendering including 8×16 sprites and overlap priority, and raw frame-buffer tests |
| `Core/apu.c` | `ApuDevice` | partial | NR50/NR51 mixer routing, NR52 power-cycle timing/sample reset, APU register read masks, live DAC-disable gating, channel 1/2 frequency-driven pulse phases and live frequency writes, channel 1 sweep period and frame-sequencer cadence, channel 1/2/3/4 length steps 0/2/4/6, trigger shadow frequency, live NR10 writes, negate-transition overflow, channel 1/2/4 envelope step-7 timing, trigger-overflow behavior, channel 3 frequency/wave RAM and NR32 mute, active DMG wave-RAM access restrictions, CGB PCM12/PCM34 readback, channel 4 NR43 noise cadence, four channel trigger/length/status, bounded PCM mixing, and deterministic channel/sample-state hashing; hardware fidelity remains partial |
| `Core/save_state.c` | `InputRecording` / BESS | partial | versioned input recording, exact-cycle replay, deterministic hashing including cartridge state; full BESS state remains deferred |
| camera, printer, rumble, WorkBoy | accessories | deferred | — |
| SGB, debugger, cheats, rewind | extended services | deferred | — |

No SameBoy private native-state compatibility is planned. BESS will be the
interoperable save-state format. A layer moves from partial to ported only
after differential evidence is added against the pinned native oracle.

The test-only oracle is built from the pinned checkout by
`tests/native/build-oracle.sh`. Its ABI deliberately exposes only model IDs,
register snapshots, memory operations, and single-instruction execution.
SameBoy's 8 MHz tick result is normalized to T-cycles at that boundary. The
native artifacts are ignored by Git and are not referenced by the shipping
core project.
