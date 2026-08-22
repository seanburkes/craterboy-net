# Craterboy repository guidance

## Scope

This is the `craterboy-net` repository. The surrounding `gbemu` workspace
contains several unrelated emulator checkouts; all Craterboy changes belong
under this directory.

## Branch workflow

- Start from `origin/main`, not from another feature branch.
- Use branch names of the form `agent/<short-description>`.
- Never commit directly on `main`.
- Use `scripts/prepare-branch.sh "short description"` to fetch `main`, verify
  that the worktree and local `main` are safe, and create the feature branch.
- Stage only files belonging to the task and keep commits focused.
- Open draft pull requests targeting `main` after pushing the branch.

If local `main` has unpublished commits, stop and preserve them on their
feature branch before synchronizing it. Do not silently discard commits.

## Validation

Run the full suite from this directory:

```sh
DOTNET_CLI_HOME=/tmp/craterboy-dotnet \
NUGET_PACKAGES=/tmp/craterboy-nuget \
dotnet test Craterboy.slnx --configuration Release --disable-build-servers -m:1
```

The differential tests build the pinned SameBoy oracle from `../SameBoy`.
Missing optional local tools such as `rgbasm`, SDL2, OpenGL, or libpng may
produce setup warnings; the test result must still be checked explicitly.

## Implementation expectations

- Preserve deterministic timing and compare new hardware behavior with the
  pinned SameBoy oracle whenever practical.
- Update `docs/port-map.md` and `docs/architecture.md` when a port layer gains
  meaningful coverage.
- Keep the shipping core dependency-free and managed-only.

## Future frontend boundary

- The Craterboy port plan is the sole authority for emulator priorities.
  External frontend or framework roadmaps must not reorder it.
- Do not create a libretro publishing project, add a libretro dependency, or
  introduce native ABI entry points or frontend lifecycle concepts before the
  documented playable milestone and a fresh explicit go decision.
- Accept host-neutral public APIs only when they are independently useful to
  Craterboy and justified by the current emulator subsystem.
- Any future libretro adapter belongs in this repository as a separate
  publishing project; `Craterboy.Core` remains frontend-independent.
