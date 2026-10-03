# Patch Profiler crash diagnostics and shared safety (1.1.13)

The current 1.1.13 source retains the resolver-cache reset workaround and struct guard. With Gradual start enabled, normal Patch/General installation and removal use a 400 ms / 2560-operation per-frame backend budget; with it disabled, the complete transition is processed in one Unity frame. Checked cleanup, compact logs and independent crash tracing remain. The source delivery contains no rebuilt DLL and has not been run in Valheim.

## Reported native-crash scenario

The handoff reports a native Mono crash inside `GetFunctionPointer`, reached through MonoMod RuntimeDetour while `Harmony.Patch` installs profiler timing. With ClayBuildPieces/FineWoodBuildPieces/CoreWoodPieces, either selected alone worked, but selecting two together crashed. Both could be installed without both being profiled.

The recorded sequence was a successful installation on `ClayBuildPieces`'s `PieceManager.BuildPiece::Patch_FejdStartup`, then an unmatched `HARMONY TIMING PATCH BEGIN` for the equivalent method in `FineWoodBuildPieces`. The callbacks shared `org.bepinex.helpers.PieceManager`. The user reported that `Skip embedded helper callbacks = true` avoided the crash for that selection.

A later 1.1.4 trace reached `ItemManager.Item::Patch_FejdStartup` in MagicPlugin after successfully instrumenting the same-named method from AdventureBackpacks. Their owners differ. The supplied MonoMod.Utils 22.1.29.1 contains a legacy member-resolution cache whose key omits assembly identities inside generic arguments; its involvement in that exact crash remains a hypothesis, not an observed cache hit. In 1.1.5, frequent cache resets replace the name/owner heuristic. No resolver implementation is patched. The original methods are still subject to the retained struct/signature and same-method safety checks.

## Verify per-operation cache resets

1. Build this source locally and replace the installed DLL. Confirm version **1.1.13** and avoid a second installed copy.
2. Open **Patch Profiler / Mods to profile**, disable all, select the affected two mods, and apply. Leave **Transpiled methods** disabled for the initial isolation run.
3. Enable tracing; resolver-cache resets are automatic and independent of these options:

   ```ini
   [Patch Profiler diagnostics]
   Write crash trace = true
   Limit instrumented methods = 0
   ```

4. Start profiling and inspect `RESOLVER CACHE CLEAR` before/after records. Otherwise eligible same-named methods should both be attempted. Pause profiling and inspect the per-method removal reset boundaries, then repeat only after preserving the previous trace. **Skipped methods** still shows unsupported methods and real failures, but no duplicate-name exclusions.
5. If a native crash still occurs, preserve the files before the next run:

   ```text
   BepInEx/config/shudnal.ValheimProfiler/PatchProfiler-CrashTrace.log
   BepInEx/LogOutput.log
   Player.log or the Unity crash report
   ```

The trace is recreated on every Start and each entry is synchronously flushed. Installation order is now deterministic regardless of tracing. Disable crash tracing after the investigation to remove its synchronous disk overhead; the safety guard remains active.

## Ordinary log versus crash trace

**General / Logging enabled** defaults to `false` and applies to both Patch and General profiling. Installation reports one summary of selected and instrumented methods, safety skips, installation and cleanup failures, plus Patch call-site failures and installation-limit skips. Session-level errors remain visible without full exception stacks.

With logging enabled, failures are compact lines grouped by method, with identical installation/cleanup causes printed once. Expected safety exclusions are informational groups, not per-method warnings. Each operation expands at most 10 failure identities and 8 skip groups; additional events have explicit counts. Complete method signatures, reasons and exception stacks are written to `BepInEx/config/shudnal.ValheimProfiler/PatchProfiler-Installation.log` (or the General equivalent), with one `*.previous.log` retained. Discovery, removal, deferred call-site reapplication and standalone session failures have separate `-Discovery.log`, `-Removal.log`, `-Reapplication.log` and `-Session.log` reports. A report opens only when detailed events exist. A failed file write produces one short warning without aborting instrumentation. The logging option is captured at operation entry.

These reports are buffered and completed at the end of the operation; they do not replace the separately flushed native-crash trace below.

The in-window **Skipped methods** report and all safety checks remain active regardless of this option. Real failures are shown first and highlighted in yellow; ordinary exclusions follow. Same-name helper copies are not skipped. Failed recovery stops new installation, cleans the other tracked methods, retains unresolved entries and requires a full game restart. A mandatory restart warning is independent of detailed logging. These changes are not a universal repair for every runtime IL error; see `INSTRUMENTATION_SAFETY.md`.

**Write crash trace** is a separate, retained diagnostic option, disabled by default. When enabled, it writes method details to `PatchProfiler-CrashTrace.log` even when ordinary detailed logging is disabled. It must be disabled separately after a crash investigation. The `true` example above is an explicit diagnostic opt-in, not a release default. Existing config values are not migrated: change a previously saved `true` to `false` manually. Cache resets, struct/signature checks and recovery reporting are independent of this option.

## Interpreting the trace

The last unmatched `BEGIN` identifies the stage where the process stopped. `HARMONY TIMING PATCH BEGIN` without its matching `END` identifies an ordinary timing-wrapper rebuild. `HARMONY CALLSITE PATCH BEGIN` identifies the later exact transpiler-call-site pass. A `SAFETY SKIP` means installation was not attempted for that candidate, not that a native crash was caught.

Trace data also includes selection/context counts, owners shared across selected mods, assembly/module identity and metadata tokens, existing patches, discovery/runtime-map phases, failure counts and safety skips. A `SHARED OWNER` entry alone is informational and is not a conflict decision.

## Removed broad workaround

`Skip embedded helper callbacks` and the automatic cross-assembly name/owner guard were removed in 1.1.5. An orphaned entry in an existing config no longer changes the selection. Do not exclude AdventureBackpacks, MagicPlugin or manager namespaces merely to validate the new workaround. The deterministic candidate limit remains available for isolating a later failure and still means intentionally partial capture.

## Additional reset evidence

`RUN BEGIN` identifies the per-operation cache policy and disabled name heuristic. `RESOLVER CACHE CLEAR` records contain `phase=before` or `phase=after`, the removed entry count, the successful-clear total, the actual backend assembly identity and the method descriptor. Two resets bracket every instrumented backend call; original/current IL discovery has separate pairs. Empty dictionaries are also cleared and counted. An `after` record indicates completion of that reset, not necessarily a successful backend operation: managed failures still execute the final reset before the checked rollback starts.

Successful resets are summarized as counts in ordinary installation/removal logs. Per-method reset details go only to the separately enabled crash trace, not to the ordinary log. An unmatched patch BEGIN still localizes the operation; a completed final reset does not prove execution equivalence or undo a previously damaged process.

Use a fresh game process for verification. This workaround does not repair already generated wrappers, exclude new methods by dependency analysis, patch foreign resolver APIs, or clear native/JIT/pinning state. A collision inside a single backend operation and concurrent foreign cache activity remain outside its guarantee. See `RESOLVER_CACHE_WORKAROUND.md`.
