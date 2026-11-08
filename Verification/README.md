# Runtime and memory-reader checks

`RuntimeCheck` is a manual Windows x64 verification executable. It loads the
actual assemblies in a selected runtime directory. It does not start GameHelper,
drive the overlay event loop, call plugin `OnEnable`, or execute profile rules.

## Build first

From the repository root, build the whole solution so launcher and plugin copy
targets populate the runtime output:

```powershell
dotnet build GameOverlay.sln -c Release
$runtime = (Resolve-Path 'GameHelper/bin/Release/net10.0-windows/win-x64').Path
```

The check project references Newtonsoft.Json and GameOffsets from that output by
default. For a different installation, supply its full path at both build time
and run time:

```powershell
dotnet run --project Verification/RuntimeCheck/RuntimeCheck.csproj -c Release -p:RuntimeRoot="$runtime" -- reader-tests "$runtime" Verification/Artifacts/readers
```

## Local reader fixtures

```powershell
dotnet run --project Verification/RuntimeCheck/RuntimeCheck.csproj -c Release -- reader-tests "$runtime" Verification/Artifacts/readers
```

The checks allocate data only in the test process and use the production readers
on that data. Cases cover:

- Null/invalid map roots, node alignment, child backlinks, valid parallel branches,
  and continued reporting of genuine unreadable pointers.
- Inline/heap item names and Unicode; empty UI text and a missing text buffer
  read through the unchanged LootValue plugin.
- Ritual item candidates and the plugin's real grid resolver: an unready candidate
  is skipped, valid neighbors remain available, and metadata appearing later is
  accepted.
- Invalid component metadata, recovery at the same item address, and refreshed
  identity when metadata changes.
- Missing terrain data with unchanged empty/flat output and retained valid tiles.
- Delayed world-area rows and repeat transitions into the same area.

Core initialization creates disposable configuration under
`<output-dir>/ReaderTestIsolation`; it does not overwrite the selected runtime's
configuration. No game process is required for this mode.

## Staged-runtime smoke check

```powershell
dotnet run --project Verification/RuntimeCheck/RuntimeCheck.csproj -c Release -- smoke "$runtime" Verification/Artifacts/smoke
```

This loads core/plugin types, checks that each plugin exposes one sealed core,
deserializes the existing AHK settings when present, and compiles their distinct
expressions without evaluating them. A fresh build uses default AHK settings.
It also checks profile/input-priority round trips and transforms a copy of the
apphost to verify launcher resource handling. The transformed EXE is not run.

## Optional read-only live probes

Select the actual game PID explicitly. Use sufficient read privileges when the
game is elevated. The probe opens only a read handle and never injects input or
writes game memory.

```text
RuntimeCheck probe <runtime-dir> <pid> <output-dir>
RuntimeCheck entities <runtime-dir> <pid> <output-dir>
RuntimeCheck atlas-live <runtime-dir> <pid> <output-dir>
RuntimeCheck base-live <runtime-dir> <pid> <output-dir>
RuntimeCheck layouts <baseline-runtime-dir> <updated-offsets-dll> <output-dir>
```

`probe` compares the six signatures against executable/live bytes and reads the
core state chain. `entities` additionally walks bounded entity-map samples and
examines item-name/terrain data, the world and loading-state area rows, and any
Ritual wisp entities it encounters. `base-live` consumes an `entity-probe.json` from
the same output directory and checks production readers against live data; take
a fresh capture when areas/allocations change. `layouts` compares type/field/
constant layouts in two offset assemblies.

With the endgame Atlas open, `atlas-live` checks the production reader's player
Render component, panel visibility, map names, and distinct grid coordinates.

A point-in-time live read does not establish behavior during prolonged combat,
area changes, or opening/closing the actual UI. Keep those validation results
separate. Raw probe output includes local paths and addresses; keep it local.

## Output

Use `Verification/Artifacts/` for generated output; it is ignored by Git. Historic
one-off capture/staging directories are also ignored. Commit the reusable source
and these instructions, not local dumps, profiles, logs, or generated executables.
