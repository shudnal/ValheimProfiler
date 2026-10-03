# Instrumentation lifecycle and diagnostics (1.1.13)

## Scope

This layer is shared by Patch Profiler and General Method Profiler. It does not replace the independent instrumentation implementations in the specialized profilers. It does not patch scene lighting, materials, global render settings, game initialization, or the selected mods' configuration.

All profiler-owned patch mutations run on the Unity main thread. Worker invocations of measured methods still execute normally but do not contribute samples. Read-only preflight does not invoke targets, obtain native function pointers, JIT methods, or force class initialization as a speculative safety check.

## Components

| Component | Responsibility |
| --- | --- |
| `InstrumentationSafety` | Common eligibility checks, cross-tool same-MethodBase reservations, process-lifetime failed-method quarantine and the restart-required latch. |
| `MethodBodyPreflight` | Decode IL instruction boundaries and reject malformed encodings or bodies without `ret`, without executing the method. |
| `InstrumentationPatchSnapshot` | Read immutable registration identities for callbacks, owners, patch kinds, registration indexes, priorities and ordering constraints. Includes optional HarmonyX IL manipulators. |
| `MonoModResolverCache` | Clear only the legacy member-resolution dictionary before/after each owned backend call, including exceptional exits; fail closed if unavailable. |
| `InstrumentationSession` | Track every attempted mutation, install/verify callbacks, remove the complete owner-scoped callback set per method, audit remaining registrations, and retain unresolved entries. |
| `InstrumentationReport` | Keep one diagnostic entry per method with individual failure stages and a concise cause, independently of the timing data and optional logging. |
| `InstrumentationReportView` | Show failures before ordinary exclusions, yellow failure indicators, filtering and the persistent recovery banner. |
| `InstrumentationLog` | Keep ordinary logging bounded and write optional detailed per-operation reports. |

## Installation

Prepare callback metadata and perform the eligibility/ownership checks before mutation. The common guard reserves a candidate. The session then captures foreign registrations, adds the method to its ledger **before** calling `Harmony.Patch`, and verifies the registered callback set afterwards. A method is not dispatched to timing statistics until installation has succeeded.

If installation throws or verification fails, record the exact method and stage and quarantine that method for the current process. Attempt one owner-scoped rollback. Successful rollback requires the rebuild to return, the profiler owner to be absent, and the captured foreign registrations to match. Only then release the reservation. This outcome permits other eligible methods to be measured; it does not permit repeatedly retrying the failed method in the same process.

A failed rollback is not a routine safety skip. Raise the restart latch before optional diagnostic formatting, stop attempting additional candidates, and clean every other tracked method. The unattempted candidates remain distinguishable from methods that actually failed.

Patch's additional exact-call-site pass uses the same ledger. Failure removes the whole profiler callback set, including any partially registered transpiler, rather than assuming that the earlier ordinary-timing wrapper survived. Call-site preparation works on a separate instruction list so a failure cannot return partially edited source. If a later foreign Harmony rebuild fails only in profiler call-site preparation, the profiler returns the untouched incoming instruction sequence and queues diagnostics for its next main-thread update, where capture is stopped and cleanup is attempted.

## Resolver cache boundaries (1.1.5)

`InstrumentationSession` surrounds each timing install, additional call-site install, owner-scoped rollback and owner-scoped removal with `MonoModResolverCache.Run`. Patch discovery separately surrounds every `GetOriginalInstructions` and `GetCurrentInstructions` call. These calls return materialized lists so deferred enumeration cannot escape the reset boundary.

The initial reset completes before entering Harmony. A reset failure before the first mutation releases the untouched reservation without calling Unpatch. If a mutation was attempted, checked rollback remains mandatory. Final reset runs in managed `finally`; if both the backend and reset throw, both errors are retained. Cache failures raise the existing process-lifetime restart latch, stop additional installation and leave unresolved removals visible. No unsafe fallback or resolver patch is installed. Cache-reset failure is sticky: remaining removals cannot bypass it and are marked unresolved without repeatedly waiting for the same cache lock.

The backend's actual dictionary is resolved once through the loaded Harmony assembly's MonoMod.Utils dependency and checked for the expected field type. Every reset clears that same dictionary under its own monitor. The monitor is released before calling Harmony or logging; an acquisition timeout is treated as a reset failure, not an excuse to continue. The dictionary is not replaced and native/JIT/pinning/assembly caches are not cleared. No global patch on foreign mutations is introduced. See `RESOLVER_CACHE_WORKAROUND.md` for residual limits.

## Removal

Disable sampling before removal. General invalidates its active invocation tokens. Iterate tracked mutations in reverse installation order; that order is not assumed to be a call graph.

For each method, issue one call equivalent to:

```csharp
harmony.Unpatch(method, HarmonyPatchType.All, harmony.Id);
```

The owner is explicit and non-wildcard. Prefix/finalizer/transpiler removal is not split into intermediate wrapper rebuilds. An exception on one method does not stop the attempts for the other methods.

Always attempt a rebuild for a tracked mutation even when its owner is already absent in metadata. Harmony can remove registration metadata before a failing wrapper rebuild; a later empty `GetPatchInfo` alone does not establish recovery. After successful removal, verify owner absence and unchanged foreign registration identities. At the end of a tracked cleanup, audit Harmony's registry for residual owner registrations.

A failed rebuild, failed registration inspection, changed foreign registration set, or unexpected residual owner entry retains an unresolved diagnostic and raises the process-lifetime restart latch. Do not overwrite foreign registrations from the snapshot, erase metadata manually to make an audit pass, release an unresolved reservation, or repeatedly rebuild the same uncertain method on refresh/reset/shutdown. Subsequent cleanup can still process other known-good tracked methods.

`Instrumented` in the installation summary describes methods retained for dispatch at completion, not all methods ever patched during an aborted operation. The separate removal summary reports attempted, removed, unresolved, residual-owner and audit-failure counts.

## User-visible outcomes

| Outcome | Report | Further capture |
| --- | --- | --- |
| Same-named method in another assembly | No exclusion based on names or shared owners. | Independently eligible; each backend operation gets its own cache resets. |
| Resolver cache reset failed | Yellow failure with the failing method/stage and a persistent restart warning. | No unprotected continuation; checked cleanup is attempted for other tracked mutations. |
| Unsupported method safely excluded before mutation | Reason in the ordinary-exclusions section. Inspection failures are distinguished from expected exclusions. | Other eligible methods continue. |
| Installation failed; owner-scoped rollback checks passed | Yellow failure entry with the installation cause and cleanup result. | Other methods continue; this method is quarantined until restart. |
| Rollback, removal or recovery audit failed | Yellow restart-required entry first; `RECOVERY UNCERTAIN` banner and a prominent restart warning. | No new Patch/General capture until full game restart. An active counterpart stops on its next update; its measurement callbacks immediately reject samples after the latch is set. |

The report combines stages for the same `MethodBase`; equal names in separate assemblies are still separate entries. Full method signatures and all collected stage messages are available through the row tooltip. `Failures only` and search only change the view. Expected skips can be refreshed on a new capture, but failures survive captures, selection changes and `Reset stats` in the loaded tool. The restart latch and failed-method quarantine last for the process and have no UI reset switch.

## Preflight boundaries

The existing generic/native/infrastructure checks and same-method reservations remain; name/owner duplicate heuristics were removed. The new IL decoder handles operand widths and switch tables so a byte inside an operand is never mistaken for `ret`. Return-less bodies, including throw-only methods, are excluded rather than rewritten speculatively. Unsafe local-variable types are excluded in addition to unsafe signature types.

General recognizes generated async/iterator state machines using interfaces and generated type naming as well as compiler attributes. This covers embedded/merged code where `CompilerGeneratedAttribute` is missing without excluding every ordinary handwritten enumerator. Registered HarmonyX IL manipulators are classified as callbacks rather than General methods.

Identical Harmony IDs alone are not errors. There is no namespace-wide PieceManager, ServerSync, LocalizationManager or YamlDotNet blacklist. There is no duplicate-name rule or inferred helper-family ownership. Only the identity of the actual MethodBase participates in cross-tool reservations.

### Conservative value-type exclusion (1.1.4)

The common preflight rejects a method when its actual `DeclaringType.IsValueType` is true. The check is before target body inspection and every Patch/General reservation and mutation. It never calls the target, obtains its native address or forces its JIT compilation. No Seasons assembly reference or `HSLColor` name-based blacklist is introduced.

The user's control run profiled Seasons except `HSLColor` without an immediate visual failure. Profiling only `HSLColor` immediately disturbed colors despite successful installation/removal checks in earlier reports. This establishes an affected instrumentation group in that environment, not the failing individual member, the exact ABI path, or a general defect in every method returning a struct. The implementation therefore conservatively excludes all members declared on value types, **including static helpers and operators**, while leaving ordinary class methods returning/accepting structs subject to the existing checks only. In particular, an enclosing struct does not exclude a method declared on a nested reference type.

These are prevented attempts, not observed installation/removal failures. They use the normal informational exclusion reason and do not set the restart latch, quarantine, or yellow failure state. General discovery exposes the reason through **Show unavailable** and does not allow newly selecting these methods. Old selected rows are preserved, rechecked at capture start, and listed in **Skipped methods** without any Harmony mutation; fresh bulk selection already omits unavailable members. The existing report severity/order and bounded logging remain unchanged.

The policy covers direct targets of the shared Patch/General backend; other specialized tools have not been broadened or refactored in this release. It does not claim that every remaining signature is safe, and does not modify the application methods' behavior. Eligible caller timings still include their uninstrumented struct-helper work. No automatic self-test or unsafe override attempts to patch suspected targets in a live scene. An already affected game process still needs a restart.

## Logging

The existing `General / Logging enabled` key and default are unchanged. Without detailed logging, operations produce aggregate summaries and brief session failures; uncertain recovery additionally produces one mandatory restart warning. The UI always retains method-level diagnostics.

With detailed logging, each operation retains the existing limit of 10 ordinary-log failure identities and 8 informational skip groups. Full errors stay in the optional reports. Removal uses `*-Removal.log`; deferred Patch call-site reapplication uses `*-Reapplication.log`, alongside existing installation/discovery/session reports. Each operation retains its current and previous detailed report. Patch's separately configured synchronous crash trace remains independent.

## What is not guaranteed

Registration checks are not a snapshot of native machine code, a proof that all execution paths are safe, or a restore of the game state. Even successful patch removal cannot undo static constructors that ran early or mutations already applied to scene/material/global state. A managed exception handler cannot recover a native process crash. No workaround here force-runs a target initializer or claims to reset a scene.

The user has reproduced the color anomaly by selecting only `HSLColor`; the precise member and runtime defect remain unconfirmed. This release prevents direct instrumentation of that entire declaring-type category. It does not establish that arbitrary bulk profiling is safe or explain every earlier lighting report.

## Local verification checklist (not executed here)

- Use a clean process and a narrow selection; verify normal capture, pause, restart and the unchanged timing/call-tree UI.
- Show unavailable General methods and verify all `HSLColor` members are unavailable, including static helpers/operators. Check old selected members produce ordinary safety skips without installing patches; fresh bulk selection omits them.
- Verify ordinary class methods returning/accepting structs remain eligible when no other rule excludes them. Compare the same Seasons scenario from a fresh process; absence of immediate visual errors is not a proof of all code paths.
- Confirm two otherwise eligible same-name helper copies are both attempted, with separate cache-reset boundaries and no duplicate-avoidance rows. Do not interpret absence of immediate errors as proof of every path.
- When a managed installation failure occurs, verify its method/cause appears first, remains after resetting statistics, and is not retried during a later capture in the same process.
- If a real removal failure occurs, verify cleanup attempts continue for other methods, the restart banner remains after reset/refresh, and both Patch/General Start actions are blocked. Restart the game rather than continuing normal play in this state.
- Compare enabled and disabled logging; verify bounded ordinary output, separate removal reports and the mandatory restart warning.
- Check yellow text, long signatures, row tooltips, filtering, scrolling and window/font scaling in the user's game environment.

No mod compilation, assembly loading, Unity execution or runtime testing was performed for this source delivery. See `SOURCE_CHANGE_SUMMARY.md` for source/artifact review details.

## Primary reference points

- Harmony edge cases: https://harmony.pardeike.net/v2/articles/patching-edgecases.html
- HarmonyX patch/unpatch registration and rebuild order: https://github.com/BepInEx/HarmonyX/blob/master/Harmony/Public/PatchProcessor.cs
- HarmonyX patch families: https://github.com/BepInEx/HarmonyX/blob/master/Harmony/Public/Patches.cs

## Frame-spread mutation scheduling (1.1.8)

Patch and General normal Start/Pause operations no longer run the complete mutation set in one Unity frame. Each tool advances installation/removal from `Update`. With **Gradual start** enabled (default) the backend-work limits are 400 ms and 2560 operations per frame; disabling it removes the frame budget so the complete transition runs in one Unity frame. Resolver-cache reset boundaries remain inside each individual backend mutation. One Harmony rebuild is not preempted, so a single unusually expensive method can still exceed the budget for its frame. Rollback uses the same installation progress scale in reverse, decreasing from the reached installation position toward zero while hooks are removed.

Sampling is disabled for the entire install phase and becomes active only after the plan completes. Pause disables sampling before any unpatch. Cancel during install enters checked rollback. Removal retains reverse mutation order and performs the existing final residual-owner audit after the incremental plan is exhausted. Shutdown keeps a synchronous cleanup fallback because the application cannot depend on future frames.
