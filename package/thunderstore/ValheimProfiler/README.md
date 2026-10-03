# Valheim Profiler

Valheim Profiler is an in-game diagnostics toolkit for Valheim mod development and server troubleshooting. It profiles Harmony patches, ordinary mod methods and call contexts, MonoBehaviour callbacks, Valheim update loops, logs, and selected dedicated-server network activity directly from an IMGUI overlay.

The mod is intended for short diagnostic sessions, not for normal gameplay. Most profilers instrument hot paths and can add overhead while they are enabled.

## Features

- **Launcher**: opens the profiler suite from one compact window.
- **Patch Profiler**: measures Harmony Prefix, Postfix, Finalizer, transpiled target methods, and detected transpiler-injected call sites.
- **General Method Profiler**: measures selected synchronous mod methods on the main thread, with per-method statistics and navigable path-specific call trees. It is the second tool in the launcher.
- **MonoBehaviour Frame Profiler**: measures `Update`, `FixedUpdate`, `LateUpdate`, and `OnGUI` callbacks with grouping by mod and component.
- **MonoBehaviour Call Profiler**: measures lifecycle and selected declared MonoBehaviour methods such as `Awake`, `Start`, `OnEnable`, `OnDisable`, and `OnDestroy`.
- **Valheim Update Profiler**: measures centralized Valheim update batches, MonoUpdaters scopes, and WearNTear update scheduling.
- **Client Log Monitor**: shows the current client `LogOutput.log`, supports filtering, issue detection, history loading, and multi-row copy.
- **Server Log Monitor**: streams a dedicated server log to authorized admins through Valheim RPCs.
- **Network Profiler**: measures selected dedicated-server RPC, routed RPC, ZDO, and transport activity while an authorized admin is subscribed.
- **Input control helpers**: optional `Prevent input` and `Prevent mouse` modes make it easier to inspect UI tables without accidentally controlling the character.

## Default hotkeys

| Tool | Hotkey |
|---|---:|
| Launcher / UI | `F7` |
| Patch Profiler | `F8` |
| Client Log Monitor | `F9` |
| Server Log Monitor | `F10` |
| Network Profiler | `F11` |

The General Method Profiler, MonoBehaviour Frame Profiler, MonoBehaviour Call Profiler, and Valheim Update Profiler are available from the launcher but have no default direct hotkey.

## Installation

Install with a mod manager or extract the package contents to your profile's `BepInEx/plugins` folder.

For client-only local profiling, install the mod on the client.

For remote dedicated-server diagnostics, install the same Valheim Profiler version on both:

- the headless dedicated server;
- the admin client that will subscribe to server diagnostics.

The only Thunderstore dependency is BepInExPack Valheim.

## Dedicated server access

Server Log Monitor and Network Profiler are private admin tools:

- the client must have the same compatible Valheim Profiler build;
- the current player must be listed as a server admin;
- Network Profiler starts collecting server metrics only after an explicit `Subscribe` action.

Self-hosted/open-server sessions are intentionally not treated as dedicated-server network profiling sessions, because local rendering, gameplay, and loopback traffic make the measurements misleading.

## Patch Profiler notes

Patch Profiler has two related but separate concepts:

- selecting a mod measures that mod's regular Harmony patches and detected `Transpiler call` rows;
- selecting `Transpiled methods` enables whole-target timing for methods modified by transpilers.

A transpiler-injected call is only shown in the main table after it receives runtime samples. Static zero-sample entries remain visible in the transpiled method details window.

Large Patch/General instrumentation changes are applied on the Unity main thread over multiple frames. With **Gradual start** enabled (default), each frame receives up to a 400 ms / 2560-operation backend budget. Disabling it processes the complete transition in one Unity frame, causing one long blocking freeze. An individual Harmony rebuild remains indivisible and can exceed either budget by itself. The toolbar shows install/remove/rollback progress and the current method; rollback reverses the reached install progress back toward zero. Sampling starts only after the complete installation plan finishes, and stops before removal begins.

## General Method Profiler


The Patch and General `Frame budget` views use the last one second of accepted measurements and divide measured milliseconds/calls by a shared configurable Target FPS (default 60), independent of the game's current FPS. `avg call` shows mean accepted call cost with automatic ms/us/ns units. `Max over 60 sec` keeps its existing per-call spike/percentile semantics.

Open the second launcher tool, then select **Methods to profile**. Expand a mod, module and class to select methods, or use the filtered bulk-selection controls. Nothing is selected by default. Top-level mod names are normal-weight until that mod contains selected methods, then the mod name is shown in bold for quick scanning. **Apply selection** saves the selection; **Start profiling** also applies it before capture.

The main toolbar always shows **Gradual start** immediately before **Start/Pause profiling**, so it remains accessible on every tab. On the Profiler tab, **View**, **Group by Mod**, **Ignore gc allocations**, **Exclude nested calls** and **Search** share the controls row. The timing table follows Patch Profiler. Columns are **Mod**, the existing one-second or 60-second statistics, **Class**, then **Class:Method**. Grouped child rows do not repeat the mod name. The Class column fits the longest displayed declaring type; wide tables scroll horizontally. Text columns sort ascending and numeric columns descending.

**Call tree**, next to the method name, opens its observed calling contexts. Choose a context and navigate through **Parent**, **Root**, or a child's **Open** button. Calls from different parents and recursive levels remain distinct. Root/current details include inclusive and self-time summaries.

Only successfully instrumented main-thread methods participate. The tree connects the nearest instrumented ancestors; it is not a complete program call graph. Self time subtracts measured direct children, not all descendant work or profiler overhead. Node and depth limits are configurable and any omissions are reported. Context totals cover the capture session; rolling columns retain their one-second or 60-second meaning.

The shared safety guard runs before Patch and General instrumentation. It excludes unsupported methods and prevents concurrent instrumentation of the same MethodBase. Equal type/method names and shared Harmony owners in separate assemblies are not exclusions; per-operation resolver-cache resets replace the former duplicate heuristic. **Skipped methods** explains each actual exclusion or failure. The broad **Skip embedded helper callbacks** switch was removed.

Methods declared on value types (`struct`), including static methods and operators, are excluded from direct Patch/General instrumentation before any Harmony mutation. This conservative policy follows a user-reproduced color failure when profiling only Seasons' `HSLColor`; the specific member and underlying runtime defect have not been isolated. It is **not** a blanket ban on methods of classes that return or accept `Color`, `Vector3` or other structs. Use **Show unavailable** in General to inspect blocked members. Previously selected blocked methods remain visible and are reported as ordinary safety skips when capture starts; they do not make the tab yellow or require a restart by themselves. Their execution is unchanged, and their cost remains inside an eligible caller's inclusive time. The rule has no unsafe override; it does not repair an already affected game process.

**Exclude nested calls** is off by default. When enabled, the main statistics subtract nested intervals measured by the same profiler and the profiler timing-bookkeeping performed around those nested measurements. Unmeasured calls remain included; this is measured-code self time rather than complete native exclusive CPU time. Inclusive mode is unchanged. Changing the option resets statistics.

**Ignore gc allocations** preserves the existing frame-level GC-collection heuristic. When collection counters differ from the current frame baseline, affected samples are treated as contaminated and can be discarded before method or context statistics, including counts and self time. The profiler does not try to prove which individual method triggered the collection, and this is not allocated-byte tracking.

Selection is stored in `BepInEx/config/shudnal.ValheimProfiler/GeneralMethodSelection.cfg`. Window geometry, filters, views, grouping and sorting use the normal persisted configuration. For source-level design and verification notes, see `GENERAL_METHOD_PROFILER.md` in the source project.

## Patch and unpatch safety

Patch and General share a mutation ledger. Every attempted method is tracked before Harmony is called. Installation checks the registered callback set and foreign patch registrations. Removal drops **all callbacks for this profiler owner in one rebuild per method**, continues after individual failures, checks remaining owner registrations, and never restores or removes another mod's patches from a saved snapshot.

Normal Start/Pause operations execute those per-method rebuilds incrementally through each tool's `Update`. With Gradual start enabled the per-frame limits are 400 ms and 2560 backend operations; with it disabled the frame budget is not checked and the complete transition is processed in one Unity frame. Preparation/discovery remains synchronous. Cancel during installation does not leave a partial capture running: it switches to checked rollback of the methods already installed. Shutdown and exceptional lifecycle cleanup retain a synchronous fallback because no later Unity frames are guaranteed.

A failed install with a successful, checked cleanup skips and quarantines that method for the current process; other methods can still be profiled. An unsuccessful rollback, removal or final audit is different: further installation stops, cleanup continues for other tracked methods, and **RECOVERY UNCERTAIN** requires a full game restart. New Patch/General captures are blocked. An active counterpart stops on its next update; its measurement callbacks already reject samples once the shared warning is raised. Unresolved methods are not automatically rebuilt again by refresh, reset or shutdown.

**Skipped methods** is yellow when there are actual failures. The report shows one entry per method with installation/rollback/removal stages and a concise root cause. Restart-required entries come first, then other failures and normal exclusions. Search and **Failures only** do not erase the report. Errors survive **Reset stats**, selection changes and subsequent captures in the loaded tool. Ordinary exclusions alone do not turn the tab yellow and never disable the original method.

Read-only preflight rejects malformed/return-less IL and unsafe local-variable types before detouring. General also recognizes generated async/iterator state machines when embedded library merging has removed their compiler attribute. There is no blanket PieceManager, ServerSync or YamlDotNet ban. These checks do not JIT or invoke target methods to establish safety.

A successful removal check is not a snapshot restore of the game. Native crashes, static-initialization side effects and changes already made to scene/material/global state cannot be undone by clearing profiler statistics. Avoid broad initialization-sensitive captures in the main menu; narrow the selection and use an appropriately initialized scene. See `INSTRUMENTATION_SAFETY.md` in the source project for the lifecycle and verification boundaries.

## Instrumentation logging

**General / Logging enabled** is disabled by default. Patch Profiler and General Method Profiler log one aggregate summary per installation: selected and instrumented methods, safety skips, installation failures, unresolved methods and unattempted candidates. Removal has its own aggregate counts for attempted/removed/unresolved methods, residual registrations and audit failures. Patch also reports call-site failures and methods skipped by its installation limit. Expected safety exclusions are informational; failures make the summary a warning. Fatal session errors still produce a brief error without method details or exception stacks.

```ini
[General]
Logging enabled = false
```

Set this to `true` for compact diagnostics in the ordinary BepInEx log:

- Actual failures appear before routine exclusions. Installation and cleanup failures for the same method share one line; identical root causes are shown once. The innermost exception and failing IL instruction are retained instead of repeated wrapper stacks.
- Expected safety skips are informational groups by assembly and reason, not individual warnings. Equal helper names in different assemblies are no longer exclusions.
- Each operation displays up to 10 failure identities and 8 safety-skip groups. Additional events are counted explicitly instead of flooding the log. These display limits never limit discovery, installation attempts or the in-window report.

Full identities, skip reasons and complete exceptions are written to separate reports, with the path printed once:

```text
BepInEx/config/shudnal.ValheimProfiler/GeneralMethodProfiler-Installation.log
BepInEx/config/shudnal.ValheimProfiler/PatchProfiler-Installation.log
```

Discovery issues use the corresponding `-Discovery.log`; removal failures use `-Removal.log`; deferred Patch call-site rebuild failures use `-Reapplication.log`; standalone session failures use `-Session.log`. A report is created lazily on the first diagnostic event when logging is enabled. The previous report with details is retained as `*.previous.log`, so each profiler/operation keeps at most two reports. Output is buffered and closed at the end of the operation. An I/O failure produces one brief warning and does not abort instrumentation or trigger an unbounded console fallback. The logging setting is captured when the operation starts; disabled logging does not format per-method log details or open report files.

**Skipped methods**, exception handling and safety reservations remain active in both modes. **Patch Profiler diagnostics / Write crash trace** is disabled by default and independently controls the detailed, synchronously flushed crash-trace file. Enable it only for an investigation, then disable it afterward. Existing config values are preserved: a previously saved `true` must be changed to `false` explicitly. Cache resets and struct safety remain active with tracing disabled. These buffered reports do not replace that native-crash diagnostic.

Uncertain recovery always emits one prominent restart warning, even with detailed logging disabled. Method-level console expansion remains optional; UI diagnostics remain available in both modes. The safety changes prevent specific risky attempts and strengthen cleanup handling; they are not a universal repair for every Harmony `IL Compile Error`.

## Resolver cache isolation

Patch Profiler and General Method Profiler clear the loaded MonoMod `ResolveReflectionCache` **before and after every individual backend operation**: timing installation, the additional call-site transpiler pass, rollback and removal. Patch's original/current instruction discovery calls have separate reset boundaries as well. Resets are unconditional, independent of logging and assembly order, and never run from sample callbacks or on every frame.

The old same-name/shared-owner duplicate heuristic and **Skip embedded helper callbacks** setting have been removed. Equal names in different assemblies are independent candidates. Actual same-method cross-tool reservations, failed-method quarantine, unsupported-signature checks and the conservative struct guard remain.

A reset failure blocks further Patch/General instrumentation and is shown in **Skipped methods** with the existing restart warning. If the initial reset failed before entering Harmony, the untouched method is not speculatively unpatched. Other tracked methods still go through checked cleanup. Ordinary logging stays compact; its summaries include successful reset counts. Patch crash tracing adds `RESOLVER CACHE CLEAR` records with phase, entry count and backend identity.

No backend DLL or resolver implementation is replaced. Only the member-resolution dictionary is cleared under its own short monitor; native detours, pinning, assembly caches and JIT state are untouched. The cache monitor is not held while Harmony runs. This trades startup/removal speed for less reuse of ambiguous cached resolutions, but cannot guarantee recovery from a native crash or prevent a collision entirely within one backend operation. Start verification from a fresh game process. See `RESOLVER_CACHE_WORKAROUND.md` for the exact boundaries.

## Performance notes

Profilers should be enabled only while investigating a specific issue. Broad selections, especially Valheim methods, MonoBehaviour frame callbacks, and network instrumentation, can add measurable overhead on heavily modded clients or busy dedicated servers.

Use `Reset` before a focused measurement run and stop or unsubscribe when the diagnostic session is done.

## Configuration

Configuration is stored under:

```text
BepInEx/config/shudnal.ValheimProfiler.cfg
BepInEx/config/shudnal.ValheimProfiler/
```

The recommended way to edit configs is with a configuration manager:

- [Configuration Manager](https://thunderstore.io/c/valheim/p/shudnal/ConfigurationManager/)
- [Official BepInEx Configuration Manager](https://valheim.thunderstore.io/package/Azumatt/Official_BepInEx_ConfigurationManager/)

## Compatibility

Valheim Profiler is a developer tool and patches a broad set of diagnostic targets when profilers are enabled. Avoid leaving intensive profilers running during normal gameplay, and avoid using server diagnostics with incompatible client/server versions.

## Links

- [GitHub](https://github.com/shudnal/ValheimProfiler)
- [Discord](https://discord.gg/e3UtQB8GFK)
- [Buy Me a Coffee](https://buymeacoffee.com/shudnal)

## Dependencies

- [BepInExPack Valheim 5.4.2350](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/)

Install ConditionalConfigSync as a separate dependency; do not copy its DLLs into this mod's package.

## Donation
[Buy Me a Coffee](https://buymeacoffee.com/shudnal)

## Discord
[Join server](https://discord.gg/e3UtQB8GFK)
