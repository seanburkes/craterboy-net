# Craterboy

Craterboy is a pure-managed, headless Game Boy emulator for .NET 10. It is an
idiomatic C# port derived from [SameBoy](https://sameboy.github.io/) 1.0.3 at
commit `213a12ce93d66b105a113debd9396306066a7cfc`.

This repository currently contains the repository/provenance foundation,
deterministic state kernel, ROM header and memory bus, ROM-only/MBC1/MBC2/MBC5
cartridges, battery RAM, and an intentionally small SM83 execution slice.
It is not yet a complete or playable emulator; see `docs/port-map.md`.

```sh
dotnet test Craterboy.slnx
dotnet run --project src/Craterboy.Tester -- game.gb --cycles 1000
```

The shipping core is dependency-free and contains no native SameBoy code.
SameBoy remains the behavioral differential oracle during development.
Frontend adapters are future work after the documented playable milestone;
they do not drive the current emulator port sequence.

Differential tests expect the pinned SameBoy checkout at `../SameBoy`, or at
the path supplied through `SAMEBOY_SOURCE_DIR`. The test build compiles a
native library locally; it is copied only into test output and excluded from
Craterboy packages.

## License

Craterboy and the SameBoy portions from which it is derived are distributed
under the Expat license. See `LICENSE`.
