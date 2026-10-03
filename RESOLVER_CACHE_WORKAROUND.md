# Per-operation MonoMod resolver-cache reset (1.1.5)

## Scope

This is a profiler-side workaround for legacy `MonoMod.Utils.ReflectionHelper.ResolveReflectionCache`. It is used by Patch Profiler and General Method Profiler through their shared mutation session, and by Patch's existing original/current IL discovery. Other specialized tools retain their previous implementations in this delivery. It does not replace or patch Harmony, MonoMod, Cecil, selected mods, or the Unity/Mono runtime.

The supplied MonoMod.Utils 22.1.29.1 uses a string-keyed dictionary whose keys can omit assembly identity inside constructed generic arguments. The reported crash followed successful timing instrumentation of AdventureBackpacks' `ItemManager.Item.Patch_FejdStartup` and stopped while installing the MagicPlugin copy. These observations motivate the workaround. A particular wrong cache hit has not been captured directly; the maintainer subsequently reported the successful local checks recorded below. No game execution was performed while preparing this source delivery.

The user explicitly chose frequent unconditional resets instead of signature/dependency collision analysis or repairing a foreign resolver. No new target-method analysis, runtime self-test, body fingerprint, or backend patch is introduced.

## Exact reset boundaries

| Owned operation | Reset policy |
| --- | --- |
| Patch/General timing prefix + finalizer installation on one method | Clear before the single Harmony.Patch call and in finally afterward. |
| Patch additional exact-call-site transpiler installation | Independent before/finally pair; not combined with the timing install. |
| Failed installation rollback | Fresh before/finally pair, even when the preceding installation failed. |
| Pause, selection refresh, shutdown and other tracked removal paths | Each method's owner-scoped Harmony.Unpatch call gets its own before/finally pair. A failed method does not stop cleanup attempts for the rest. |
| Patch original IL discovery for one target | Independent before/finally pair around GetOriginalInstructions. |
| Patch current/transpiled IL discovery for one target | Independent before/finally pair around GetCurrentInstructions. |

The policy applies to the first method and every subsequent method, even when their assemblies are identical. There is no once-per-DLL optimization and no logging-dependent bypass. Instruction API calls produce materialized lists inside their scopes; no lazy enumeration is allowed to escape the reset boundary. Actual callbacks comprising a timing pair are still installed/removed together to avoid intermediate wrappers with incomplete state support.

The dictionary is not cleared during ordinary sample collection, frame updates or analytics processing. One backend invocation is the smallest boundary used here. No hooks are installed inside the foreign resolver to clear on every recursive member lookup, and no global observer intercepts other mods' patching.

## Cache access and locking

The binding is resolved once from the MonoMod.Utils dependency referenced by the loaded Harmony assembly. The expected field is a static `Dictionary<string, WeakReference>` named `ResolveReflectionCache` on `MonoMod.Utils.ReflectionHelper`. A missing or incompatible binding fails closed rather than selecting an arbitrary assembly by namespace or continuing without resets.

Every reset uses the original dictionary object and its monitor, matching the verified legacy reader/writer. It clears dictionary contents, not the field object. The monitor is released before entering Harmony and before optional tracing. It is never held over a Patch/Unpatch call, which would invert the backend's Harmony/chain-to-cache lock order. Cache-lock acquisition has a 2000 ms timeout so an unavailable cache monitor produces a managed failure; this does not impose a timeout on Harmony, native JIT or foreign callbacks.

No AssemblyCache, AssembliesCache, native detour state, pinning registry or JIT cache is cleared. No GC.Collect is used. Correctly emitted wrappers remain referenced by the normal backend; clearing lookup results is not an unpatch operation or a rollback of emitted code.

## Failure behavior

- If the initial reset fails before the first attempted mutation, the backend action is not called. The untouched ledger reservation is released without speculative Unpatch.
- The final reset runs after success or a managed backend exception. When both backend and reset fail, an AggregateException retains both causes rather than discarding one.
- A reset failure in the shared session or IL discovery raises the existing process-lifetime restart latch. Further Patch/General capture is blocked, the failure appears first in Skipped methods, and the ordinary restart warning is not hidden by disabled logging.
- A cache-reset failure is sticky for the process: later reset requests fail immediately rather than waiting again for the same cache monitor for every remaining method. No backend call runs without the required initial reset.
- If a mutation had already been attempted, rollback/cleanup still runs through the same reset boundary. Other tracked methods are processed even when a removal fails. If cache isolation itself has failed, their removal is marked unresolved without an unsafe backend call; the profiler does not claim they were cleaned. Unverified removal keeps the ledger entry and reservation; Reset stats cannot hide it or clear the latch.
- Existing installation quarantine and registration audits remain. A managed finally block is not a way to catch or repair a native process crash.

## Removed heuristics

The cross-assembly duplicate key (related Harmony owner plus assembly-independent method signature) and helper-family inference are removed. Different MethodBase identities remain separate even when their type names, signatures and owners coincide. The obsolete broad Skip embedded helper callbacks config entry, selection filter and duplicate-report category are removed as well. Old orphaned config text is ignored; it cannot silently re-enable the previous exclusion.

Reservations still prevent two profiler tools from instrumenting the same actual method. The value-type declaring-type exclusion introduced after the HSLColor reproduction is retained, as are the existing generic/native/signature/IL checks. Clearing this resolver cache is not evidence that struct instrumentation is safe.

## Diagnostics

Patch's independent crash trace adds `RESOLVER CACHE CLEAR` records with before/after phase, removed entry count, monotonically increasing successful-reset total and the resolved backend identity. The session attaches the method descriptor to each record. Empty-cache resets also count. A successful final reset says nothing by itself about whether the enclosed backend action succeeded; its outcome is recorded by the existing installation/rollback/removal reporting.

Ordinary summaries add the successful-reset delta for their operation, including any nested discovery or cleanup work. Nested scopes can overlap in these deltas; do not sum overlapping summaries as independent totals. Individual reset details are never sent to the normal log. Existing caps for method failures and skip groups, full optional reports and yellow failure-first UI remain.

## Residual limits and local verification

Clearing before/after each owned backend invocation prevents reuse across those boundaries, not every possible collision. Conflicting types can still be resolved during one invocation, and foreign resolver users can populate the shared cache concurrently. A short monitor around Clear protects dictionary integrity, not the entire generation transaction. Already emitted wrappers, cached instruction operands elsewhere and prior scene state are not repaired. Foreign reapplication outside the profiler is not automatically wrapped.

Build locally with the existing dependency references, restart the game, and repeat the previous failing Patch selection with both AdventureBackpacks and MagicPlugin still enabled for measurement. Preserve the trace before another Start. Check both ItemManager callbacks for completed timing-install records and independent reset pairs, then check Pause and a subsequent Start. Repeat with the earlier building-piece pair and General selections excluding the still-unavailable struct methods. These runtime checks have not been performed here.

## Maintainer-reported validation

After the 1.1.5 cache-reset change, the maintainer reported that the previously affected BuildPieces mods and the AdventureBackpacks + MagicPlugin combination work in their local setup without duplicate-patch filtering. This is user-reported runtime evidence for those combinations, not an independent reproduction, a new Marcus trace, or proof for every mod selection. The report does not establish the results of every removal/restart path.

The conservative guard for methods declared on structs remains unchanged, including static methods and operators. It is independent of both the removed duplicate-name heuristic and the resolver-cache workaround. Class methods accepting or returning ordinary structs remain subject to the other existing checks.

Release defaults leave both `Logging enabled` and `Write crash trace` off. An existing config that contains `Write crash trace = true` retains that explicit value and should be switched off after diagnostics. No diagnostic `.cfg` or `.log` file is bundled with this source delivery.
