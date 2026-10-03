# General Method Profiler: 1.1.13

## Source baseline and delivery

This document describes the current 1.1.13 source state. It retains the 1.1.5 resolver-cache workaround, checked patch/unpatch lifecycle, struct declaring-type guard, failure diagnostics, one-row toolbar/search layout and call-tree behavior. Normal General installation/removal is spread across Unity frames with a 400 ms / 2560-operation budget while **Gradual start** is enabled. Disabling the option processes the complete transition in one Unity frame for maximum transition speed. Existing local assembly references and packaging scripts are unchanged. The delivered archive contains source, not a compiled release. No DLL was built and the mod was not run or tested in Unity/Valheim.

## User workflow

1. Open General Method Profiler, the second tool in the launcher. No direct hotkey is assigned by default; one can be configured.
2. In **Methods to profile**, select a mod/module/class or individual methods. Search, **Selected only**, **Show unavailable**, expansion and filtered bulk operations are supported. No methods are enabled by default.
3. **Apply selection** saves the selection and restarts an active capture. **Start profiling** also saves it. An unsaved change does not silently alter an already running capture.
4. Trigger the selected methods on the main thread. Use the normal timing views, filters and sortable headers.
5. Choose **Call tree**. When the method has multiple runtime contexts, choose the appropriate path. Use **Parent**, **Root**, a child's **Open**, or **Contexts of current method** to navigate.
6. **Pause profiling** disables sampling, attempts checked owner-scoped removal for every tracked method and freezes the data. Failed removal is not shown as an ordinary clean pause. **Reset stats** clears statistics/topology without removing active patches or clearing safety failures. Closing or hiding a window does not stop capture.

## Scope and method identity

Discovery is restricted to assemblies containing loaded BepInEx plugin instances. It never scans the whole AppDomain or recursively follows plugin dependencies. Multiple plugin instances in the same assembly are grouped and the assembly is scanned once. Modules and classes remain explicit selection levels.

Persistent method keys contain sorted plugin GUIDs, assembly and module names, and complete signatures including declaring type, static/instance distinction, return and parameter types. Keys do not use metadata tokens or assembly versions. Overloads remain distinct. Selection-file writes use a temporary file and replacement; read/write failures are reported. Selections for temporarily absent methods/mods are retained.

Unsupported rows can be inspected with **Show unavailable**, but cannot be newly selected. This includes registered Harmony callbacks, conventional attributed patch infrastructure, MonoBehaviour messages, async/iterator state-machine entry points, generated async/iterator state machines, and methods rejected by the shared safety layer. Ordinary synchronous delegates/events/RPC handlers are eligible. Constructors are not part of discovery. Only main-thread invocations contribute; worker invocations return before Unity APIs or the mutable tracking stack are accessed.

### Value-type declaring types

All directly declared methods of a value type are unavailable, including static helpers and conversion operators. The rule uses `MethodBase.DeclaringType.IsValueType`, not type names or a Seasons dependency. It applies both during discovery and immediately before reservation/installation, so old saved selections cannot bypass it. Constructors were already outside General discovery.

This is a conservative exclusion prompted by the user's isolation: the Seasons selection without `HSLColor` showed no immediate visual failure, while selecting only `HSLColor` immediately disturbed colors. Individual members and the precise Harmony/MonoMod/runtime mechanism were not isolated. Static members are included deliberately to exclude the complete affected type rather than assume only its instance member is responsible. This does not establish that every value-type member is inherently unpatchable.

Methods declared on classes are not rejected merely because their return values, parameters or locals are ordinary structs. Existing separate generic, pointer, byref-like, by-reference-return and runtime restrictions still apply. An eligible caller can measure inclusive work performed by excluded struct helpers; these helpers do not have individual samples or call-tree nodes.

Use **Show unavailable** to inspect the exclusion. Stored selected members stay visible so they can be deselected; at capture start they receive an ordinary **Not measured** entry in **Skipped methods**, not a yellow installation/removal failure. Fresh bulk selection does not select unavailable methods, so it does not create an attempted-method report for them. Real failures remain before ordinary exclusions. There is no override for this policy.

The rule prevents new direct detours; it does not validate all remaining methods, repair existing detours, or restore scene state from a previous unsafe capture. Start validation from a fresh game process.

## Runtime collection and lifecycle

Each method is installed once with a timing prefix and a void finalizer. Existing Harmony owners are ordered inside the timing boundary, so measurements are inclusive wrapper wall time and can include other patches or a skipped original. They are not isolated native CPU cycles. Finalizers do not replace or suppress application exceptions. State is consumed once to tolerate finalizer re-entry.

The main-thread stack stores a session generation, invocation sequence, stack index, method, context node, start timestamp and accumulated direct-child ticks. A matching exit must be the current top frame. Recursion uses separate stack frames and distinct path nodes. A mismatch or hook failure discards the active stack instead of fabricating edges; error counters are visible. Reset, pause, selection restart and shutdown invalidate outstanding tokens. Profiler callbacks are guarded against re-entry, and the profiler's own assembly is never eligible for this instrumentation.

The tree is an aggregate of observed full runtime paths, not a static call graph or a complete trace of individual invocations. Node identity is a parent's node identity plus the current method. A method invoked below different roots/parents or recursive depths therefore has separate contexts. A root is the outermost currently instrumented method, not necessarily the application's true entry point. Unselected intermediate calls are not represented.

## Statistics and GC filtering

Method aggregates and each context use the existing `RollingProfilerStat` histogram engine: raw, second and third maxima, approximate p95/p99, counts and mean durations above these boundaries, sample counts and GC counts. Histogram maintenance uses the same numeric analytics-worker pattern as the existing profiler. Application methods are still measured only on the main thread.

General's Frame budget uses a rolling one-second measurement window but normalizes measured milliseconds and accepted calls to the shared configurable Target FPS (default 60), not to the game's current FPS. The fixed denominator therefore includes target frames with no call. `avg call` is measured time divided by accepted calls and is independent of Target FPS. Sparse samples retain their sample timestamp rather than acquiring a fresh timestamp merely because a table is opened. `Max over 60 sec` remains unchanged.

Main-table durations are inclusive by default. With **Exclude nested calls**, each completed invocation subtracts the inclusive duration of nested invocations also measured by General Method Profiler and the General profiler timing-bookkeeping performed around those nested measurements before its sample enters rolling statistics. Uninstrumented nested work remains included, so this is measured-code self time rather than complete exclusive CPU time. Aggregating inclusive rows can double-count overlapping work; mod summaries are not exclusive frame budgets. Context lifetime totals/counts and self summaries persist until reset or the next capture session. The normal one-second and 60-second fields remain rolling. Pause freezes both navigation and the reporting time.

Self time is the accepted invocation's inclusive duration minus measured direct-child durations and the General profiler timing-bookkeeping performed around those measured child calls. It still includes unselected work, omitted descendants, Harmony/wrapper overhead outside those profiler timing boundaries and other unmeasured work. It is not a claim of exact exclusive/native CPU time.

GC classification preserves the existing frame-level heuristic: a sample of at least 1 ms is associated with GC when collection counters differ from the latest frame baseline. The intent is to reject potentially contaminated work from that frame, not to identify which method triggered the collection. It does not measure allocated bytes or attribute ownership of a collection. With **Ignore gc allocations**, a flagged invocation is discarded before method statistics, context statistics, lifetime counts, maxima, percentiles and self-time counters. A rejected child's duration still reduces a retained parent's self time. Structural zero-sample nodes can remain to retain accepted child paths. The separate **GC discarded** diagnostic counter is not a statistics sample count. Changing the UI option resets the data.

## Limits and failure handling

| Setting in General Method Profiler | Default | Allowed range | Effect |
|---|---:|---:|---|
| Maximum call tree nodes | 4096 | 256-32768 | Existing contexts continue; new contexts are omitted, but their method aggregates remain measured. |
| Maximum call depth | 128 | 16-1024 | Deeper invocations are omitted from both aggregate timing and topology. |

Limits are read when a new capture starts. Descendants of a missing context are not reattached to an unrelated parent/root. Deeper omitted work remains in a retained ancestor's measured duration. Diagnostics report omission attempts, stack mismatches, hook failures and discarded GC samples. Node/depth caps bound topology and active tracking depth; they do not promise a hard bound on all histogram queues under arbitrary workload.

A failed installation is recorded with the concrete method and followed by one owner-scoped rollback. Checked rollback releases the reservation but quarantines the failed method for the current process. Failed rollback/removal retains the ledger entry and reservation, stops further installation, and triggers cleanup of the other methods. A process-lifetime restart latch blocks new Patch/General captures. Neither statistics resets nor a later metadata query clears that latch; an absent owner after a failing rebuild is not evidence of native recovery.

The shared `InstrumentationSession` compares foreign patch-registration identities around each mutation, checks the requested owned callbacks after installation, and audits owner registrations after removal. It does not restore foreign snapshots or erase other owners. Exact Patch call-site installation uses the same ledger: failure of the second phase rolls back the entire owner instead of assuming that the earlier timing wrapper survived. See `INSTRUMENTATION_SAFETY.md` for precise guarantees and limits. Other specialized profilers keep their independent lifecycle implementations.

## Shared safety layer

`InstrumentationSafety` is owned by `ValheimProfilerApp` and used by Patch and General. The catalog captures actual Harmony callback registrations, including transpilers and HarmonyX IL manipulators when exposed, without requesting function pointers. The catalog retains actual callback identities so General does not instrument Harmony callbacks as ordinary methods. It does not infer ownership families from names or namespaces.

Equal method/type names and shared Harmony owners in different assemblies do not cause an exclusion. Actual MethodBase reservations still span concurrent Patch/General sessions and prevent measuring the same method twice. The old cross-assembly duplicate heuristic is removed; per-operation MonoMod resolver-cache resets replace it. PieceManager, ServerSync and similar names are not blacklisted.

The general preflight excludes the profiler assembly, runtime/Harmony/MonoMod/BepInEx infrastructure, dynamic or IL-less methods, abstract/interface methods, generic methods or generic declaring types (including constructed generics because bodies may be shared), static constructors, object finalizers, remoting types, native/internal-call/extern implementations, varargs, pointer/byref-like signatures, unsafe locals and by-reference returns. It decodes IL instruction boundaries and excludes bodies without an actual return instruction; a matching byte inside an operand is not treated as a return. Generated iterator detection does not depend on CompilerGeneratedAttribute alone. An incomplete Harmony catalog fails closed. Static checks reduce specific risks; they are not a proof that arbitrary Mono detouring is safe, nor can managed cleanup recover a native process crash.

The original Patch crash trace remains available, but **Skip embedded helper callbacks** was removed. Existing config text for that key is ignored. Cache resets are always enabled before/after timing installation, rollback and removal, independently of logging. Reset failure blocks unsafe continuation. See `RESOLVER_CACHE_WORKAROUND.md` and `PATCH_PROFILER_CRASH_DIAGNOSTICS.md` for the exact scope and verification steps.

## UI integration and persistence

Both windows use the existing `WindowManager`, `ProfilerWindow`, theme, tooltip, cursor, input-blocking and GUI scaling infrastructure. On the Profiler tab, tab navigation, timing-view selection, grouping, GC filtering and search share one horizontal row. Search immediately follows the GC filter; clearing and group expansion controls remain available on the same row.

The timing table is ordered **Mod**, statistics, **Class**, **Class:Method**. Only the mod name is shown in the first column; grouped child rows leave that cell empty. Class contains the full declaring type, including its namespace and nested-type path. Its font-aware width fits the longest class in the filtered rows of expanded groups, or all filtered rows when grouping is off. Widths are cached and recalculated when rows, expansion, filters, view, window size or GUI skin change, not merely when timing values refresh. Wide tables retain horizontal scrolling and row virtualization.

The compact **Call tree** action is next to Class:Method, matching Patch's placement of its Calls action. Clicking the active method's action hides the tree window; selecting another method opens its contexts. Root/current details show full signatures through tooltips and child/context rows expose path IDs for disambiguation.

Text columns sort ascending; numeric columns retain descending sorting. Active headers show the direction. Existing persisted `Source` and `MethodType` values are accepted as aliases for `Mod` and `Class`, respectively. Column enum positions are preserved, and `Method` is appended.

Geometry, filters, selected-only/unavailable filters, grouping, timing view, per-view sort columns, tree sorting and GC preference use normal config persistence. Method selections are separate in `BepInEx/config/shudnal.ValheimProfiler/GeneralMethodSelection.cfg`. Tree topology and capture data are intentionally session-local, not persisted across game restarts.

## Shared instrumentation logging

`InstrumentationLog` applies one policy to Patch and General. **General / Logging enabled** defaults to `false`. Each installation emits aggregate selected, instrumented, safety-skipped, installation-failure, unresolved-method and unattempted-candidate counts. Removal emits separate attempted/removed/unresolved, residual-owner and audit-failure counts. General also consolidates incomplete metadata discovery into a summary. Patch includes call-site failures and its deterministic installation-limit count. Routine exclusions use informational summaries; installation or cleanup failures use warnings.

With the setting disabled, routine per-method names and exception stacks are not written to the ordinary profiler log. Uncertain recovery is an exception: one mandatory restart warning identifies the first problem independently of logging. Fatal session operations still emit a brief message containing the exception type. No diagnostic-report files are opened and no log-only method identities or exception stacks are formatted.

With logging enabled, each discovery/installation/removal owns a disposable `InstrumentationLog.Operation`. Actual failures are presented before expected exclusions. Up to 10 distinct method/metadata identities are expanded, with at most 4 distinct failure parts per identity; further failure events are counted. Installation and cleanup for the same method occupy one line. Equal compact root causes are shared, retaining the innermost exception and failing IL instruction instead of dumping Harmony wrapper stacks.

Expected safety exclusions are grouped as informational output. Groups use assembly and reason. Same-name helper copies are no longer skipped or grouped as duplicates. The first 8 distinct groups are retained for console expansion; subsequent skips outside those groups have an explicit omitted count. Full signatures and reasons remain in the file and the existing Skipped methods report. Logging-only presentation normalization does not participate in safety decisions. Recognized inspection errors are reported as failures rather than routine exclusions.

The complete event stream is written to `GeneralMethodProfiler-Installation.log` or `PatchProfiler-Installation.log` under the existing profiler config directory. Metadata operations use `-Discovery.log`, cleanup uses `-Removal.log`, deferred Patch reapplication failures use `-Reapplication.log`, and standalone session failures use `-Session.log`. Reports open lazily on the first detailed event, use buffered UTF-8 output, and are closed when the scope exits, including early returns and managed failures. One `*.previous.log` is retained per profiler/operation. Events beyond console limits are still written in full; console buffers have bounded cardinality. File errors disable that operation's file writer, are reported once, and do not cause instrumentation rollback or a per-event console fallback. Exceptions from the ordinary logger's listeners are isolated from instrumentation as well.

The setting is captured at operation entry, keeping a nested discovery scope independent of installation aggregation and file lifetime. The file output is buffered, not a synchronous native-crash breadcrumb trace.

Exception handling, cache resets, same-method reservations and the in-window **Skipped methods** report are never disabled by logging. Installation failures and cleanup failures are different events within one method entry. Failed cleanup keeps its reservation and requires restart. The tab and failing rows are yellow; unresolved recovery is sorted first, other failures next, ordinary exclusions afterward. Failure history survives timing resets and new captures in the loaded tool. A dedicated Failures only filter hides routine exclusions without changing the stored diagnostics.

Patch's separate **Write crash trace** option remains independent, including its detailed file output and synchronous flush behavior. Turning off ordinary detailed logging does not turn off an explicitly configured crash trace.

## Verification boundary and manual checks

This update adds per-operation resolver-cache resets and removes same-name duplicate exclusions. The value-type declaring-type exclusion, checked patch/unpatch lifecycle and failure presentation are retained, with explicit reporting for reset failures. Timing callbacks are unchanged. Shared statistics, GC-discard rules, table layout, inline-search toolbar and call-tree navigation are retained. Removal checks cannot undo earlier static initialization or mutations of game/scene state, and cannot prove arbitrary Mono detouring safe. Source/artifact checks and their exact boundaries are recorded in `SOURCE_CHANGE_SUMMARY.md`; they are not a compilation or runtime validation.

The following checks remain **unperformed** and belong in the user's game environment after building with local references:

- From a fresh process, show unavailable methods and verify all HSLColor members are blocked. Confirm old selected members become normal safety skips and that new bulk selection omits them. Do not re-enable the known failing group as a runtime self-test.
- From a fresh process, select both affected embedded-helper mods and inspect installation/removal traces. Both same-named copies should be attempted when otherwise eligible, each with separate before/after cache resets. Observe whether the previously reported crash still occurs; this has not been verified here.
- Select synchronous methods with multiple parents, recursion and thrown exceptions; verify distinct paths, balanced navigation and unchanged exception behavior.
- Trigger a selected method on a worker and confirm no samples or contexts are added for that invocation.
- Pause/reset/restart while viewing a nested context; verify frozen data and cleared navigation. Confirm failure history survives resets and selection changes, and that a restart-required latch prevents further captures rather than being cleared by UI actions.
- Exercise node/depth limits and GC filtering; inspect omission counters and confirm that rejected samples do not enter timing/count statistics.
- Check window resizing, font scaling, tooltip placement, scroll alignment, filtering, sorting and existing profiler behavior in game. Confirm the one-row toolbar and Mod/statistics/Class/Class:Method layout in both timing views and grouping modes.
- Filter and expand/collapse groups with short, long and nested class names. Confirm Class fits the displayed values and the header stays aligned during horizontal scrolling. Restore old `Source`/`MethodType` sort settings and verify the matching text columns.
- Repeat a selection containing expected skips and managed installation failures with `Logging enabled` false and true. Confirm summary-only disabled output, grouped informational skips, one compact failure line per method and complete stacks only in the enabled diagnostic report. Verify failure-first ordering, yellow failure markers, unhighlighted ordinary exclusions and independent Patch crash tracing.
- Exercise the console caps, a report path that is not writable, report rotation and a partial/failing metadata discovery. Confirm explicit omission counts, one report-I/O warning per operation, deterministic report closure and cleanup continuing for other methods when one removal fails.

## Primary implementation references

- Harmony finalizers: https://harmony.pardeike.net/articles/patching-finalizer.html
- Harmony injected arguments/state: https://harmony.pardeike.net/articles/patching-injections.html
- Harmony edge cases (generics, inlining, native methods): https://harmony.pardeike.net/articles/patching-edgecases.html

## Frame-spread instrumentation lifecycle (1.1.8)

Normal General Method Profiler installation and removal are processed from the tool `Update` on the Unity main thread instead of completing every Harmony rebuild in the button event. With **Gradual start** enabled, a frame receives a 400 ms backend-work budget and at most 2560 backend mutations. With it disabled no per-frame limit is applied and the complete transition is processed in one Unity frame. The budget is checked between backend calls; one Harmony operation is indivisible and may exceed the selected budget by itself. Preparation and discovery remain synchronous.

During installation `_profilingActive` remains false. Installed callbacks therefore return at their normal inactive gate and no partial selection contributes samples. After the complete plan finishes, frame/GC baselines are reset and capture becomes active in one step. Pause clears the active flag and call stack before gradual reverse-order removal. Cancel during installation changes to rollback and removes the already installed subset. The same progress bar reverses from the reached installation position back toward zero; its displayed count and percentage decrease as removals are verified instead of starting a separate forward removal progress. Selection changes use the same gradual removal before restarting.

The toolbar displays phase, completed/total work, percentage and the current short method identity. The progress row is 22 px high (up from 16 px). The install plan is a snapshot; changing visible selection while it is running does not alter that plan.
