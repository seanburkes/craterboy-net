# Architecture

`Craterboy.Core` owns all emulated state. `Emulator` is the public aggregate
and the only authority that advances T-cycles. Host time, entropy, and serial
I/O enter through explicit interfaces; file access and presentation stay out
of the core.

The implementation favors explicit state and opcode behavior over object
layout compatibility with C. Serialization will be field-by-field and
transactional. `PeekMemory` is intended to remain side-effect free while
`ReadMemory` and `WriteMemory` represent bus operations as devices gain
read/write side effects.

`Craterboy.Tester` is a headless conformance entry point. A native SameBoy
adapter will live only in tests/CI and will never be included in packages.
