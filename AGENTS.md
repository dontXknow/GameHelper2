# Codex guide for GameHelper2

Read [CLAUDE.md](CLAUDE.md) for architecture, plugin loading, localization, and the
existing build conventions. This file adds the Codex workflow for this repository.

## Build

- Windows x64 and the .NET 10 SDK are required.
- Build the **whole solution** so launcher, plugins, localization, and assets are
  staged together:

  ```powershell
  dotnet build GameOverlay.sln -c Release
  ```

- Runtime output: `GameHelper/bin/Release/net10.0-windows/win-x64/`.
- Start an intended runtime through its `Launcher.exe`, at the game's privilege
  level. Do not stop an existing GameHelper process to make a build succeed.
- Separate compilation failures from file-copy failures and report the checks
  that actually completed. Existing NuGet audit warnings are not a clean audit.

## Preserve the user's working version

- Inspect the requested base directory and current Git changes before editing.
  Preserve unrelated work, profiles, input-priority settings, and plugin DLLs.
- Prepare a separate runtime copy for testing changes to a live installation.
  Copy only the intended payload and verify the other copied files remain intact.
- A source checkout is not proof of the source of an installed DLL. If exact
  provenance matters, check that the PDB debug ID matches the DLL and compare its
  source checksums before replacing an installed custom core.
- A keypress sent by the plugin alone does not confirm a successful cast. Keep
  observed buffs, Actor data, and predictions distinct when changing skill logic.

## Memory-reader contracts

- `BaseOffsets.DisplayNameOffset` identifies a `StdWString`. Short names such as
  Gold are stored inline; use the existing string reader for inline/heap storage.
- A null optional UI/text/item pointer means no data is ready for that read.
  Validate it before dereferencing. Do not replace missing values with guesses.
- The entity map is live. Keep node alignment, nil/color checks, parent backlinks,
  and traversal limits. Invalid branches are retried on later updates; preserve
  valid neighboring branches and genuine failed-read diagnostics.
- Failed component refreshes must not leave entities/items valid with old cached
  identity. Rebuild when metadata changes even if the object address is reused.
- Generic UI elements and specialized stash tabs can expose unrelated values at
  an apparent item field. Use `PluginUiElementReflection.TryValidateItemAddress`
  before constructing/pricing item candidates.
- Distinguish signature patterns from struct field offsets. Require unique
  pattern matches and plausible referenced data; keep the `^` marker semantics.
  A heuristic diagnostic label does not prove an offset is wrong.
- Extra debug sampling/logging may be gated by its debug control. Existing entity
  discovery, nearby-monster checks, buffs, stats, and flask collection must retain
  their independent behavior.

## Validate changes

After building the solution, run the relevant checks from
[Verification/README.md](Verification/README.md). For memory/UI-reader work:

```powershell
$runtime = (Resolve-Path 'GameHelper/bin/Release/net10.0-windows/win-x64').Path
dotnet run --project Verification/RuntimeCheck/RuntimeCheck.csproj -c Release -- reader-tests $runtime Verification/Artifacts/readers
dotnet run --project Verification/RuntimeCheck/RuntimeCheck.csproj -c Release -- smoke $runtime Verification/Artifacts/smoke
```

These checks use the actual staged assemblies and local fixtures. They do not
start the overlay or execute profile rules. Live probe modes require a separately
selected game process and sufficient read privileges; use only read access.

Report separately: build result, fixture/staged-runtime checks, user-confirmed
behavior, and any outstanding in-game tests. Do not infer a particular skill or
UI action from an aggregate error counter without a controlled comparison.

## Comments, docs, and Git

- Follow the existing C# namespace, XML documentation, copyright, and keyed
  localization conventions from `CLAUDE.md`.
- Comment the reason for non-obvious pointer/layout checks, including the evidence
  or failure condition, rather than repeating the code.
- Keep the changelog and verification instructions aligned with the final change.
- Commit source and reusable checks explicitly. Keep local memory captures,
  personal profiles, generated binaries, and one-off staging reports outside Git.
- Use descriptive commit messages stating the behavior fixed and validation done.
  Push to the intended repository/branch only within the user's requested scope.
