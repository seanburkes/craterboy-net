# SameBoy-to-Craterboy port map

Baseline: SameBoy 1.0.3, commit
`213a12ce93d66b105a113debd9396306066a7cfc`.

Status meanings: **partial** is implemented but not oracle-complete;
**deferred** has no claim of implementation.

| SameBoy source | Craterboy area | Status | Current evidence |
|---|---|---|---|
| `Core/model.h`, reset portions of `Core/gb.c` | `GameBoyModel`, `Emulator.Reset` | partial | native model-ID and post-boot register comparisons |
| `Core/memory.c` | `Emulator.Read/Write` | partial | native WRAM, echo-RAM, and DMG unusable-range comparisons |
| `Core/mbc.c` | `Cartridge` implementations | partial | ROM/MBC1/MBC2/MBC5/MBC3 banking and RAM tests; MBC3 RTC fixture and stream persistence |
| `Core/sm83_cpu.c` | `Emulator.Execute` | partial | explicit load/store, ALU, INC/DEC, relative/absolute branch, stack, CALL/RET instructions with per-instruction native comparisons |
| `Core/timing.c` | `Scheduler`, `EmulatorState`, and cycle execution authority | partial | per-instruction native T-cycle comparisons; participant scheduler tests |
| `Core/timing.c`, timer portions of `Core/gb.c` | `TimerDevice` | partial | divider cadence, timer falling-edge, and overflow interrupt tests |
| `Core/random.c` | `IEntropyProvider` | partial | injectable boundary defined |
| MBC3/RTC interoperability and remaining mappers | cartridge subsystem | deferred/partial | deterministic managed RTC exists; SameBoy battery/RTC byte-format parity remains deferred |
| timer, joypad, serial, DMA | device state/scheduler | partial | timer, OAM DMA, serial endpoint, and FF00 joypad edge tests |
| `Core/display.c` | `PpuDevice` | partial | LCD mode timing/reset, edge-triggered LY/LYC/STAT interrupts, CPU VRAM/OAM access blocking, DMG background/window/sprite tile and palette rendering including 8×16 sprites and overlap priority, and raw frame-buffer tests |
| `Core/apu.c` | `ApuDevice` | partial | NR50/NR51 mixer routing, channel 1/2/4 envelope and sweep foundations, channel 3 frequency/wave RAM and NR32 mute, channel 4 NR43 noise cadence, four channel trigger/length/status, and bounded PCM mixing; hardware fidelity remains partial |
| `Core/save_state.c` | `InputRecording` / BESS | partial | versioned cycle-ordered input recording and exact-cycle replay; full BESS state remains deferred |
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
