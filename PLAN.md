# SameBoy-to-.NET 10 Port Plan

## Summary

Create a pure-managed, headless `Craterboy` emulator in the `craterboy-net`
repository, derived from SameBoy 1.0.3 commit
`213a12ce93d66b105a113debd9396306066a7cfc`.

The port will:

- Prioritize behavioral and timing fidelity over early performance.
- Use idiomatic C# rather than preserve SameBoy's C API or memory layout.
- Support BESS interoperability, but not SameBoy's private native-state format.
- Deliver a reusable core library, CLI conformance harness, tests, benchmarks,
  and documentation.
- Stage hardware as DMG/MGB, then CGB/AGB/GBP compatibility, then SGB/SGB2.
- Port debugger, rewind, cheats, accessories, GBS, and ISX after base DMG/CGB
  parity.
- Remain safe managed code by default; permit narrowly scoped `unsafe` code
  only after profiling demonstrates a meaningful benefit.
- Use the pinned C implementation as a development and CI differential oracle.

## Current execution priority: retail-title playability

The numbered layers below remain the architectural decomposition, but the
remaining implementation order is now driven by the ability to play licensed
commercial DMG/MGB and CGB titles reliably. Source-file coverage, obscure
hardware breadth, and accessory completeness do not outrank defects that block
ordinary retail games.

Execute remaining work in this order:

1. **Retail compatibility harness and evidence.** Add a tester workflow for
   caller-supplied, legally obtained ROMs that records cartridge metadata,
   boot/run outcome, deterministic checkpoints, frame/audio activity, battery
   behavior, and failures without copying ROM data into reports or the
   repository. Keep CI restricted to redistributable test ROMs and native
   differential fixtures.
2. **DMG/MGB playable closure.** Finish CPU opcode/timing coverage, PPU FIFO and
   rendering behavior, APU fidelity, timer/DMA/serial/joypad edge cases, and
   reset/load/run stability that affect ordinary DMG and MGB retail titles.
3. **CGB retail closure.** Finish CGB DMA, palette/priority, double-speed,
   revision-specific bus behavior, and CGB audio/video regressions before
   pursuing AGB/GBP refinements that do not unblock games.
4. **Long-session and persistence reliability.** Validate repeated ROM loads,
   deterministic runs, battery RAM/RTC round trips, and transactional saves for
   common retail mappers. BESS restoration of transient device timing follows
   battery-backed progress unless a title requires it to run.
5. **Released special-hardware titles.** Complete only hardware required by
   shipped games: MBC7 motion/EEPROM, HuC/TAMA RTC behavior, rumble where the
   register contract is known, and Pocket Camera behavior. Add title-driven
   evidence for each path.
6. **Post-playability breadth.** Finish printer mechanics, WorkBoy, unusual or
   unlicensed mappers, SGB/SGB2, debugger/cheats/rewind, and other convenience
   tooling after the DMG/MGB and CGB retail gates are met.

For local retail-title qualification, a representative title must:

- load without unsupported cartridge or model errors;
- reach interactive gameplay under a deterministic input recording;
- produce changing video and bounded audio for at least ten emulated minutes;
- survive reset and repeated load/run cycles without state leakage;
- preserve battery RAM and RTC state when the cartridge uses them; and
- reproduce checkpoint hashes when all injected inputs, time, and entropy are
  fixed.

This priority order overrides the numeric layer order for unfinished work.
Frontend adapters remain deferred and cannot be used to redefine the playable
gate.

## Architecture and Public API

Create a .NET 10 solution with these logical components:

- `Craterboy.Core`: dependency-free emulator, hardware state, serializers, and
  public API.
- `Craterboy.Tester`: headless ROM runner, trace recorder,
  screenshot/audio exporter, and conformance CLI.
- Test projects for unit, differential, ROM-suite, interoperability, and
  performance testing.
- A test-only native adapter that builds the pinned SameBoy core and exposes
  deterministic comparison operations through P/Invoke.

Model emulator state as one owned aggregate divided into explicit state groups:
CPU, scheduler/timers, bus/DMA, cartridge, PPU, APU, joypad/serial, RTC, SGB,
accessories, and tooling. The scheduler remains the sole authority for
advancing hardware cycles; components must not use wall-clock time directly.

Expose an idiomatic API centered on:

- `Emulator(GameBoyModel model, EmulatorOptions options)`
- ROM and boot-ROM loading from `ReadOnlyMemory<byte>` and streams.
- `StepInstruction()`, `RunCycles(int)`, and `RunFrame()` deterministic
  execution methods.
- Button state and per-player input methods.
- Read-only CPU register snapshots plus safe and side-effecting memory access
  APIs.
- Preallocated video-frame and audio-sample buffers.
- Delegates/interfaces for serial, camera input, printer output, rumble,
  infrared, boot-ROM resolution, logging, and execution hooks.
- Battery save/load methods using streams and buffers.
- Explicit BESS save/load methods.
- Later tooling services for symbols, breakpoints, watchpoints, disassembly,
  cheats, search, rewind, and backstepping.

Use injectable time and entropy providers. Default host implementations may use
system time and randomness, while tests use deterministic implementations. File
access, audio playback, windowing, and input polling remain outside the core.

Embed reproducibly built versions of SameBoy's open boot ROMs as attributed
resources. Include a build script that regenerates them with RGBDS and verifies
their hashes; library consumers must not need RGBDS installed.

## Layer-by-Layer Implementation

### 1. Repository and provenance foundation

- Establish .NET 10, nullable references, warnings-as-errors, formatting,
  analyzers, xUnit, and BenchmarkDotNet.
- Copy the applicable Expat license notice and document the pinned SameBoy
  commit.
- Add a C-to-C# mapping document recording each source module, port status,
  intentional deviation, and validation evidence.
- Build the SameBoy native oracle in CI without including it in shipping
  packages.

### 2. Deterministic state kernel

- Port model identifiers, registers, interrupt state, reset values, constants,
  endian helpers, deterministic random behavior, and grouped state containers.
- Replace C macros and bitfields with explicit enums, masks, and methods.
- Implement a master T-cycle scheduler with stubbed device participants and
  preserve SameBoy's pending-cycle semantics.
- Validate reset state, model predicates, cycle accounting, and deterministic
  seeds against the oracle.

### 3. ROM, bus, and cartridge foundation

- Implement ROM/header parsing, boot-ROM mapping, RAM/VRAM/OAM/HRAM/I/O routing,
  open-bus behavior, and safe reads.
- Port ROM-only and common MBC1, MBC1M, MBC2, MBC3/RTC, and MBC5 cartridges.
- Implement battery-backed RAM and RTC buffer formats compatible with SameBoy.
- Differentially test every mapped address range, banking transition,
  disabled-RAM result, RTC latch, and dirty-battery transition.

### 4. SM83 CPU vertical slice

- Port the CPU control flow faithfully, including pending reads/writes,
  bus-conflict timing, interrupts, EI delay, HALT bug, STOP, and illegal
  opcodes.
- Keep opcode implementations explicit and reviewable; generation may produce
  repetitive dispatch tables, but generated output must be checked in and
  deterministically reproducible.
- Run serial-reporting CPU ROMs before attaching full graphics or audio.
- Gate completion on exhaustive opcode state/cycle comparisons and Blargg
  CPU/instruction-timing tests.

### 5. Timers, DMA, serial, and joypad

- Port DIV/TIMA edge behavior, reload glitches, speed-switch timing, OAM DMA,
  CGB HDMA/GDMA scaffolding, serial clocking, joypad interrupts, bounce
  emulation, illegal combinations, and faux analog inputs.
- Define serial as a replaceable endpoint so it supports test-ROM output, link
  emulation, printer, and WorkBoy without bus special cases.
- Compare state at T-cycle boundaries around timer overflow, DMA conflicts,
  serial edges, and input transitions.

### 6. DMG PPU

- Port the display state machine, pixel FIFOs, fetcher, OAM search, window
  behavior, STAT/LYC interrupts, access blocking, OAM corruption, palette
  writes, and known LCD glitches.
- Preserve raw hardware pixels separately from any future presentation filters.
- Validate per-line state traces and exact frame hashes for DMG boot, Acid2,
  OAM bug, Mooneye, and SameSuite cases.
- DMG-B becomes the first playable milestone only after CPU, PPU, timer, DMA,
  and serial suites pass together.

### 7. DMG APU and MGB completion

- Port all four sound channels, frame sequencer, envelopes, sweep, length
  counters, wave RAM behavior, PCM registers where applicable, mixing,
  filtering, and sample scheduling.
- Emit raw interleaved samples through a preallocated sink; audio device
  playback remains a frontend concern.
- Add MGB reset/model differences after DMG behavior is stable.
- Compare register traces and PCM output with the native oracle and run
  SameBoy's DMG sound tests.

### 8. Persistence and deterministic replay

- Implement explicit field-by-field managed state serialization with versioned
  sections; never serialize object memory layouts.
- Implement BESS read/write, unknown-block skipping, model validation, bounds
  checking, RTC/MBC/accessory blocks, and transactional loading.
- Add deterministic input recordings used by tests and future rewind.
- Verify managed BESS states load in SameBoy and SameBoy BESS states load in
  Craterboy; reject malformed states without mutating the active emulator.

### 9. CGB, AGB, and GBP compatibility

- Add CGB revisions 0/A-E, double-speed mode, banked RAM/VRAM, color palettes,
  CGB DMA, priority rules, revision-specific bus conflicts, and color
  correction.
- Add AGB-A and GBP-A compatibility modes after CGB-E is stable.
- Run CGB Acid2, CGB sound, Mooneye, SameSuite, boot-ROM, DMA, palette, and
  revision-specific tests.
- Require exact frame hashes and matching hardware-state traces for the pinned
  oracle corpus.

### 10. Extended cartridges and physical accessories

- Port MMM01, HuC1, HuC3, MBC6, MBC7 accelerometer/EEPROM/rumble, TAMA5, TPP1,
  Game Boy Camera, infrared, alarms, and unusual wiring.
- Port Printer and WorkBoy as serial accessories behind public device
  interfaces.
- Provide deterministic camera, motion, time, and printer fixtures.
- Validate battery formats, peripheral protocols, sensor conversion, RTC/alarm
  behavior, and mapper edge cases independently.
- Treat this layer as title-driven after common retail compatibility work.
  Printer mechanics, WorkBoy, speculative rumble controls, and obscure or
  unlicensed mapper breadth do not block the DMG/MGB or CGB playable gates.

### 11. SGB and SGB2

- Port SGB packet reception, multiplayer input, palettes, attribute maps,
  borders, masking, sound/animation state, and SNES integration callbacks.
- Keep the SGB host bridge optional so the ordinary DMG/CGB core has no SNES
  dependency.
- Add SGB/SGB2 BESS blocks and exact border/frame comparisons.
- Treat SGB parity as a separate milestone after DMG/CGB release readiness.

### 12. Developer and convenience tooling

- Port ISX and GBS loading plus GBS track control.
- Port symbol maps, disassembler, expression evaluator, conditional
  breakpoints/watchpoints, execution hooks, backtraces, debugger undo, and
  backstepping.
- Port cheats, cheat import/export, memory search, and filter expressions.
- Port rewind using versioned managed state snapshots and delta compression;
  keep storage strategy replaceable.
- Expose these through structured services and CLI commands rather than
  recreate SameBoy's terminal UI verbatim.

### 13. Optimization and 1.0 hardening

- Profile Release builds before introducing pooling, specialized dispatch,
  `ref` access, SIMD, or unsafe code.
- Eliminate steady-state per-frame allocations after buffers and callbacks are
  warmed.
- Require video and audio enabled to sustain at least real-time DMG and CGB
  execution on the current development workstation, with median frame
  execution below 16.7 ms in a documented benchmark corpus.
- Track oracle-relative performance in CI without using variable shared-runner
  timing as a hard pass/fail gate.
- Package only the managed library, tester, resources, licenses, and symbols;
  exclude the native oracle.

## Test and Acceptance Plan

- **Unit tests:** flags, ALU operations, every opcode, registers, timers, FIFO
  operations, palettes, MBC banking, RTC, DMA, APU units, serializers,
  compression, and expression parsing.
- **Differential tests:** compare C and C# register state, buses, memory,
  interrupts, device state, frames, audio, callbacks, battery data, and BESS at
  deterministic checkpoints.
- **ROM suites:** run SameBoy sanity ROMs plus Blargg, Mooneye, SameSuite,
  DMG/CGB Acid2, sound, OAM bug, and boot-ROM tests where licensing permits
  redistribution.
- **Property tests:** determinism, serializer round trips, bank normalization,
  state-load transactional safety, malformed input handling, and repeated
  reset/load/run sequences.
- **Interoperability tests:** exchange BESS and battery data in both directions
  with pinned SameBoy.
- **Regression artifacts:** store only redistributable ROMs, compact traces,
  hashes, and failure seeds; require explicit configuration for proprietary
  games.
- **Layer gate:** a layer is complete only when its focused tests pass, all
  earlier layer tests remain green, and its mapping document identifies every
  corresponding SameBoy routine as ported, intentionally deferred, or
  intentionally changed.
- **Final acceptance:** managed-only shipping output; DMG/MGB and CGB/AGB/GBP
  suites pass; BESS interoperability succeeds; deterministic runs reproduce
  identical hashes; extended hardware and tooling milestones pass their own
  suites; real-time performance target is met.
- **Retail playable gate:** a documented local corpus of caller-supplied retail
  titles satisfies the boot, gameplay, video/audio, reset, persistence, and
  deterministic-checkpoint requirements above, while CI remains reproducible
  using only redistributable artifacts.

## Assumptions

- `craterboy-net` is the destination repository.
- The public identity is Craterboy, with prominent SameBoy derivation and
  license attribution.
- The initial deliverable has no desktop UI, shader pipeline, audio backend, or
  platform-specific input layer.
- SameBoy's C API and private native save-state representation are not
  compatibility requirements.
- SameBoy commit `213a12c` remains the behavioral baseline until an explicit
  reconciliation updates the mapping document and golden results.
- Correctness changes are kept separate from optimization changes so oracle
  mismatches remain diagnosable.
- Frontend adapters, including libretro, are deferred until Craterboy reaches
  its documented playable milestone and receives a fresh explicit go decision.
  They must not drive emulator sequencing or enter `Craterboy.Core`.
