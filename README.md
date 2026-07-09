# Valheim Profiler


## 0.8.7

- fixed IMGUI tooltip positioning in `Prevent mouse` mode by using the current Unity mouse position for repaint/layout hover events, preventing tooltips from appearing at the upper-left corner or only after a mouse click.

## 0.8.6

- Patch Profiler now makes the separate `Transpiled methods` selection explicit: the Mods tab highlights that whole-target timing is independently enabled, provides a direct enable button, and explains that `Transpiler call` rows still follow their source mod;
- the main table and transpiler details now state that zero-sample entries are hidden until the target code path actually executes, and details show detected versus sampled call counts;
- text columns sort ascending while numeric columns remain descending, with an active `▲` or `▼` direction marker;
- ordinary method and whole-target timing is installed before exact transpiler call-site instrumentation, so a call-site wrapper failure no longer removes the entire target measurement; start status and logs now report instrumentation failures separately.

## 0.8.5

- Patch Profiler now replaces each detected injected call with a generated wrapper that has the same stack signature as the original call, allowing transpiler helpers with parameters, such as `ModifyPlantGrow(Plant, GameObject)`, to collect live timing correctly on Mono;
- generated wrappers preserve the original `call` or `callvirt` semantics internally, return values and exceptions, and close timing through `finally`; zero-argument helpers such as `GetCustomKey` remain supported;
- active `Transpiler call` rows therefore appear under their source mod in grouped views once the injected call executes.

## 0.8.4

- Patch Profiler now injects timing directly around detected net-new transpiler call sites, so small or inlined helper methods such as `GetCustomKey` produce live `Transpiler call` rows in the main table and details window;
- injected-call discovery now keeps the final per-method call ordinals from an ordered original/final call-stream diff, while profiling cleanup handles target exceptions without leaving stale timing state;
- `Prevent mouse` now resolves the active Unity Input System control and also filters Attack, SecondaryAttack and Block at the `Player.SetControls` boundary, covering character actions that bypass or inline the patched `ZInput.GetButton*` methods.

## 0.8.3

- MonoBehaviour Frame Profiler grouped rows now show the short component type and callback, for example `Heightmap.Update`; the `Mod / callback` column keeps its natural content width instead of consuming all remaining window space;
- Patch Profiler now compares original target IL with final transpiled target IL, exposes net-new direct calls as normal `Transpiler call` rows, aggregates duplicate call sites, and keeps live timing plus source-transpiler details in the drill-down window;
- `Prevent mouse` now patches every boolean `ZInput.GetButton`, `GetButtonDown` and `GetButtonUp` overload whose first parameter is an action name, reads configured mouse bindings when available, and suppresses mouse-triggered Attack, Block and other rebound actions without suppressing keyboard-only activation.

## 0.8.2

- MonoBehaviour Call Profiler now shows the component class in the Method column, for example `Heightmap.Awake`;
- Valheim Update Profiler adds Patch Profiler-style p95/p99 exceedance statistics, removes the mostly redundant Source column, shortens built-in scope names and prefixes unknown third-party scopes with `Mod |`;
- Patch Profiler transpiled-target details now list direct runtime calls detected in transpiler IL together with the called method, source transpiler, target method and collected timing statistics;
- the launcher marks active collectors with an accent button and status dot, adds a separate `Prevent mouse` switch, and reorganizes default hotkeys around F8-F11;
- Network Profiler keeps Reset available after Unsubscribe, fixes duplicate unknown RPC hash text, improves registration-based RPC context, and adds persistent descending sorting plus tooltips to all tables;
- Client and Server Log Monitor add an explicit multi-selection hint, hide the detail pane while multiple rows are selected, color Message rows white, and support compact LogOutput-like clipboard formatting with optional metadata.

## 0.8.1

- added multi-row selection to Client Log Monitor and Server Log Monitor Stream views;
- plain click selects one row, Ctrl-click toggles individual rows and Shift-click selects a continuous filtered range;
- added `Copy selected` actions that copy selected entries in chronological order while preserving full details and stack traces.

## 0.8.0

- added a dedicated-server-only Network Profiler;
- added private admin subscription and one-second aggregated snapshots for RPC, ZDO and per-peer transport diagnostics;
- records RPC registration owner, component, delegate method, layer, prefab, calls, logical payload, routed physical fanout, handler CPU time and interval peaks;
- records RPC handler exceptions and routing failures for missing direct/global/object handlers, unavailable target peers, missing target ZDOs and missing instantiated ZNetViews, including the known handler/component and peer where available;
- records ZDO mutations, full serialized traffic, serialization CPU, prefab totals, top individual ZDOs and key mutation triggers;
- records per-peer send queues, rates, serialized traffic deltas, connection quality, ZDO sync CPU and candidate/selected counts;
- detects Better Networking, Network, NetworkTweaks, VBNetTweaks and VAGhettoNetworking and reports runtime compatibility caveats inside the Help view;
- added `Close` buttons to Client Log Monitor and Server Log Monitor.

## 0.7.0

- added a separate Valheim Update Profiler;
- instruments the four centralized `MonoUpdatersExtra` batch methods and automatically discovers Valheim or third-party `profileScope` values;
- reports rolling one-second milliseconds, calls, scheduled objects, per-batch and per-object averages, plus rolling 60-second maximums and approximate percentiles;
- measures complete `MonoUpdaters.FixedUpdate`, `Update` and `LateUpdate` phases and records their unattributed remainder outside `MonoUpdatersExtra`;
- adds low-overhead `WearNTearUpdater.UpdateWearNTear` measurements split into Full pass, Wear batch and Total without per-piece patches;
- reports WearNTear instance count, adaptive updates/frame, cycle progress, cycle duration and schedule lag.

## 0.6.6

- removed the global `Enable remote access` workflow from Server Log Monitor;
- opening the window no longer subscribes automatically: an authorized admin explicitly presses `Subscribe`;
- snapshots and live batches are sent only to the current connection IDs stored in the server subscriber list;
- `Unsubscribe`, disconnect and admin revocation remove that peer from the stream;
- the first subscription automatically loads and merges one bounded `LogOutput.log` page before the live entries, restoring normal server startup messages that occurred before Valheim Profiler initialized.

## 0.6.5

- fixed cursor restoration when connecting to or disconnecting from a world while profiler windows remain open;
- `FejdStartup.Start` changes the profiler cursor release baseline to the unlocked, visible main-menu state;
- `FejdStartup.OnDestroy` changes the release baseline to the locked, hidden in-world state;
- the active profiler cursor remains unlocked during the transition, while closing the UI later restores the correct state for the destination scene.

## 0.6.4

- fixed Server Log Monitor rejecting every live entry because live entries intentionally use `FileOffset = -1` while the decoder required all file offsets to be non-negative;
- live entries now require a positive sequence and the `-1` non-file sentinel, while historical entries require sequence `0` and a non-negative file offset;
- numeric protocol errors now include the rejected metadata values for easier diagnosis;
- Server Log wire format remains version `4` because the serialized layout did not change; before release this is only an internal mismatch guard, not a backward-compatibility promise.

## 0.6.3

- fixed remote `Enable remote access` control for administrators connected to a dedicated server;
- the server-confirmed admin result is now authoritative because `LocalPlayerIsAdminOrHost()` may remain false on a remote dedicated client;
- the server still revalidates the sender before changing and saving the setting;
- updated the post-build copy destination to `BepInEx/plugins/shudnal-ValheimProfiler/`;
- Server Log protocol remains version `4` and is compatible with `0.6.2` servers and clients.

## 0.6.2

- launcher tooltips can extend beyond the compact launcher window;
- Server Log remains visually available in the launcher and reports backend/access state inside its own window;
- server administrators can read and change `Enable remote access` directly from Server Log Monitor through a dedicated admin-authorized RPC;
- Server Log protocol version is now `4`; client and dedicated server must both use Valheim Profiler `0.6.2` or newer.

Valheim-specific in-game profiling and diagnostics tools for BepInEx mod development.

The mod provides a shared launcher, window system, input handling, scaling and theme. Tools are independent and can be opened or run at the same time.


## Version 0.6.1

- hardened the private Server Log transport after reviewing the current ConditionalConfigSync networking implementation;
- added optional Deflate compression for repetitive server-log payloads;
- added targeted fragmentation and reassembly for larger snapshots and history pages;
- added hard decoded-payload, fragment-count, per-sender cache and global cache limits;
- fragment assemblies now expire and reject duplicate, inconsistent or malformed fragments;
- individual log entries are length-prefixed and decoded with bounded strings and validated metadata;
- subscriber sequence progress advances only after the complete response is accepted for sending, so failed sends can be retried;
- increased the Server Log protocol version to `3`; client and server must both use Valheim Profiler `0.6.1` or newer.

## Current tools

### Patch Profiler

Profiles runtime work performed by Harmony patches:

- prefixes, postfixes and finalizers;
- complete transpiled target methods;
- net-new direct calls detected in final transpiled target IL;
- rolling one-second cost per frame;
- rolling 60-second maximums and approximate percentiles;
- GC-associated slow samples;
- grouping by BepInEx mod;
- frozen statistics after `Pause profiling`;
- transpiler contributor and detected injected-call details, including live timing, aggregated call-site counts and the transpiled target.

Patch Profiler measures wall-clock time on the Unity main thread. Each supported transpiler-call site is replaced with a generated wrapper that preserves the original call signature and measures the exact net-new direct call in the final transpiled target IL. Its duration is inclusive of work performed by the called method and its nested calls.

The `Mods to profile` list is loaded when its tab is opened. Selection overrides are stored in:

```text
BepInEx/config/shudnal.ValheimProfiler/PatchProfilerSelection.cfg
```

Missing entries remain enabled by default, so newly installed mods are profiled unless explicitly disabled.

### MonoBehaviour Frame Profiler

Profiles regular managed Unity callbacks:

- `Update`;
- `FixedUpdate`;
- `LateUpdate`;
- `OnGUI`.

Callbacks declared by mod assemblies are selected by default. Valheim and Other callbacks are disabled by default and can be enabled manually.

Reported callback time is **inclusive**. It includes nested method calls and Harmony patches executed inside the callback. Values from nested or related rows must not be added together as exclusive frame cost.

The selection window provides:

- exclusive `Mods`, `Valheim` and `Other` source tabs;
- compact search with a `Clear` button;
- expand/collapse all;
- filtered `Enable all` and other bulk actions;
- an optional `Present in active scene` view filter;
- explicit `Apply selection` state;
- automatic expansion of paths containing selected callbacks.

Selection overrides are stored in:

```text
BepInEx/config/shudnal.ValheimProfiler/MonoBehaviourFrameSelection.cfg
```


### MonoBehaviour Call Profiler

Profiles rare and lifecycle MonoBehaviour methods with lifetime statistics:

- `Awake`;
- `Start`;
- `OnEnable`;
- `OnDisable`;
- `OnDestroy`;
- manually selected managed instance methods declared by MonoBehaviour types.

Synchronous lifecycle methods from mod assemblies are selected by default. Declared methods, Valheim methods and Other methods are opt-in. Regular `Update`, `FixedUpdate`, `LateUpdate` and `OnGUI` callbacks remain the responsibility of MonoBehaviour Frame Profiler and are intentionally excluded from declared-method discovery.

The table reports the component class and method name, calls, total CPU time, average, maximum, last call, approximate lifetime p95/p99 and first/last call time relative to the profiling session. Samples are never removed by age and remain until statistics are reset or instrumentation is restarted.

Coroutine and async methods only measure the synchronous call that creates or advances their state machine. Calls that occurred before instrumentation started, especially earlier `Awake` and `Start` calls, cannot be reconstructed.

Selection overrides are stored in:

```text
BepInEx/config/shudnal.ValheimProfiler/MonoBehaviourCallSelection.cfg
```

### Valheim Update Profiler

Profiles fixed centralized Valheim update loops without requiring manual method selection.

The MonoUpdaters section instruments one batch call for each `MonoUpdatersExtra.CustomFixedUpdate`, `CustomUpdate`, `CustomLateUpdate` and `UpdateAI` scope. Rows are keyed by the supplied `profileScope`, so third-party mods using the same extension methods appear automatically. Known Valheim 0.219.14 scopes are shortened relative to their group; unknown scopes are prefixed with `Mod |`. The profiler records scheduled objects as `container.Count + source.Count` before `AddRange`, total milliseconds and calls over one second, average batch/object cost, rolling 60-second maximums, p95/p99 thresholds and slower-sample averages.

Complete `MonoUpdaters.FixedUpdate`, `Update` and `LateUpdate` phases are also measured. `Unattributed remainder` is the phase time not observed inside the centralized extension methods, including WaterVolume work and other code outside those batches.

The WearNTear section instruments `WearNTearUpdater.UpdateWearNTear` once per invocation and separates the once-per-cycle Full pass from incremental Wear batches. It reports current instances, adaptive updates/frame, cycle progress, wall-clock cycle duration and schedule lag. Individual `WearNTear.UpdateCover`, `UpdateAshlandsMaterialValues` and `UpdateWear` methods are intentionally not patched; use MonoBehaviour Frame Profiler for an explicit detailed investigation.

The tool is intended for developer timing measurements. It reports execution time directly and does not estimate FPS or frame-budget percentages.

### Network Profiler

A separate remote profiler for the network stack of a headless dedicated server. It is intentionally unavailable for self-hosted/open-server sessions so rendering, local gameplay and loopback traffic do not blur server measurements.

Requirements:

- the same compatible Valheim Profiler build on the headless dedicated server;
- the current player in the server admin list;
- an explicit private `Subscribe` action from the client window.

The dedicated server collects hot-path measurements only while at least one authorized administrator is explicitly subscribed, and sends bounded one-second aggregates only to those connection-scoped peer IDs. Valheim Profiler's own diagnostic RPCs are excluded from RPC tables so the stream does not recursively profile itself, although its real socket traffic can still contribute to the observed transport queue.

`RPC` reports:

- direct, global routed and object-routed registration layer;
- RPC name/hash, component, delegate handler, BepInEx plugin and prefab;
- remote incoming, server-local and outgoing calls with separate logical bytes per interval;
- routed physical fanout sends and bytes;
- handler CPU milliseconds, average, maximum, max payload and max calls per frame;
- routing/handler errors.

`Routing errors` reports handler exceptions, missing direct/global/object handlers, unavailable target peers, missing target ZDOs and missing instantiated `ZNetView` objects. The row includes the sender/target peer, known component/handler, prefab and latest diagnostic details. For a server-local failed send, the profiler captures the initiating caller only when the error occurs. Incoming client RPCs cannot expose their original client-side call site without running a corresponding client-side capture, so those rows report the source peer and known protocol/handler context instead.

`ZDO` has three views:

- `By prefab`: mutations, unique changed ZDOs, sent/received count and bytes, serialization/deserialization CPU, average/max full ZDO size, creates, destroys and ownership changes;
- `Top ZDOs`: individual active ZDO IDs, prefab, owner and interval traffic;
- `Keys`: mutation triggers by known key name/hash, value type and affected ZDO count.

A key mutation is not presented as owning the complete transmitted byte count. Vanilla synchronization increases the whole ZDO revision and serializes the complete ZDO, so mutation triggers and full-payload traffic are reported separately.

`Peers / Transport` reports each peer independently: backend/socket chain, readiness, ping/quality, reported transport rates, serialized RPC byte deltas, current/max send queue, current send rate, PlayFab actual in-flight bytes when available, `SendZDOs` CPU, `CreateSyncList` CPU and candidate/selected/sent ZDO counts.

The profiler detects known networking modifications and shows caveats instead of silently presenting vanilla interpretations:

- Better Networking and Network mostly change limits, buffering and compression; logical instrumentation remains usable and queue/rate values reflect the modified runtime;
- NetworkTweaks changes peers processed per ZDO update;
- VBNetTweaks can change routed fanout and ZDO scheduling, so vanilla candidate semantics may differ;
- VAGhettoNetworking replaces substantial routed-RPC and ZDO serialization behavior. RPC registry/handler timing and physical peer data remain useful, while ZDO byte and candidate columns may be partial.

All Network tables have persistent descending sorting and metric tooltips. `Unsubscribe` stops server collection for this peer but retains the last local snapshot; `Reset` remains available to clear that retained data. RPC names and hashes are captured from registration paths and rescanned handler dictionaries, while unknown hashes are displayed only once as `#hash`.

Default hot-path collection avoids payload cloning, content capture, LINQ and stack traces. Package sizes are read around the game's actual serialization rather than serializing a second time. Registration discovery also has a periodic fallback scan while subscribed so handlers remain visible when another mod replaces or buffers registration paths.

### Client Log Monitor

Captures BepInEx and forwarded Unity log events from the current client process.

`Stream` provides level filters, case-insensitive search, Follow, row details, single-entry copy, `Copy selected` and `Copy filtered`. Shift-click selects a range and Ctrl-click toggles individual rows; the detail pane is hidden while multiple rows are selected. A strict leading Unity timestamp such as `06/23/2026 06:54:11:` is removed from Message because the parsed time is already displayed in the Time column. The unmodified raw text remains available internally for search and diagnostics. Clipboard output defaults to the compact BepInEx `LogOutput.log` form (`[Level : Source] raw message`); `Copy metadata` adds timestamp, thread, scene and history/sequence metadata and uses the expanded two-line format.

After `Chainloader startup complete` is observed, the monitor reads the current `LogOutput.log` once and merges entries written before Valheim Profiler initialized. It uses a run of normalized fingerprints to locate the live/file overlap. `Load older` reads additional bounded pages on demand; manually loaded history is stored beyond the bounded live ring and can be unloaded separately.

`Issues` conservatively groups identical Error/Fatal events, with Warning groups optional. It reports count, first/last occurrence, source and message, with persistent descending sorting.

### Server Log Monitor

A separate window can be open alongside Client Log Monitor. Its Stream view uses the same multi-row selection and compact/metadata clipboard modes as the client log. It is available only when:

- the client is connected to a headless dedicated server;
- the same compatible Valheim Profiler version is installed on that server;
- the connected player is in the server admin list and explicitly presses `Subscribe`.

The launcher keeps Server Log visually available at all times. Opening it shows the current backend and authorization state but does not subscribe automatically. If the server mod is absent, a bounded capability probe times out without enabling the Subscribe action.

After an explicit subscription, the server sends an initial bounded recent snapshot followed by live batches carrying monotonic sequence numbers. The client then automatically requests one bounded `LogOutput.log` history page and merges the pre-profiler startup records before the live stream. Server timestamps are transferred as UTC and shown in the client local time zone so Client and Server windows can be compared directly. The client detects gaps and requests a fresh snapshot. Older server `LogOutput.log` data is requested in bounded pages by file cursor; server restart or log-file replacement invalidates the cursor.

The RPC payload uses a private bounded transport envelope. Repetitive log payloads are compressed, larger payloads are fragmented into targeted packets, fragment assemblies expire and are limited per sender and globally, and decoded packages have a hard 4 MiB limit. Individual log entries are length-prefixed. Subscriber sequence progress advances only after the complete response was accepted for sending, so a transient send failure can be retried instead of silently skipping entries.

Server logs may contain paths, player names, network information and third-party mod data. The server validates every subscription as admin-only and sends data only to peer IDs currently present in the subscriber list; no log stream is broadcast to all administrators.

On a graphics-null headless instance, Valheim Profiler initializes only the bounded Server Log and Network Profiler backends with their RPC bridges. Patch/MonoBehaviour discovery, IMGUI, input, cursor and pause systems are not created.

## Hotkeys

Defaults:

- `F7`: show or hide the complete Valheim Profiler UI;
- `F8`: show or hide Patch Profiler;
- `F9`: show or hide Client Log Monitor;
- `F10`: show or hide Server Log Monitor; unavailable states open the Help view and an authorized admin subscribes explicitly;
- `F11`: show or hide Network Profiler; unavailable states open the Help view and an authorized admin subscribes explicitly.

MonoBehaviour Frame Profiler, MonoBehaviour Call Profiler and Valheim Update Profiler retain configurable hotkey entries but have no default shortcut. `F12` remains free for RuntimeUnityEditor or other tools.

Hiding windows does not stop active profilers or log capture. Use each tool's pause control when its instrumentation or capture must stop.

## Valheim integration

The shared UI layer includes:

- Valheim-specific gameplay input suppression while profiler windows are visible;
- launcher activity indicators for tools that are currently collecting data;
- a launcher `Prevent input` switch that blocks all gameplay input;
- a separate `Prevent mouse` switch that blocks UI pointer input, mouse wheel, camera movement and the default mouse-backed gameplay actions while keyboard movement and hotkeys remain available;
- cursor unlock and restoration;
- optional game pause;
- Valheim GUI-scale integration;
- Unity 6000+ temporary startup-window handling;
- scalable IMGUI rendering;
- resizable and persistent windows;
- explicit window borders;
- square scrollbars;
- multi-line tooltips;
- configurable colors and font size.

Default additional UI scale is `1.0`. Default font size is `12`.

The normal BepInEx configuration remains at:

```text
BepInEx/config/shudnal.ValheimProfiler.cfg
```

Additional profiler-owned files are stored under:

```text
BepInEx/config/shudnal.ValheimProfiler/
```

The MonoBehaviour profiler sections also contain `Include Valheim Profiler callbacks` settings. When enabled, Valheim Profiler's own callbacks and lifecycle methods are included in discovery so the profilers can measure themselves during development.

## Build

The project targets .NET Framework 4.7.2 and uses C# 11 with file-scoped namespaces.

Expected reference directories in the supplied project:

```text
../Assemblies/Harmony/
../Assemblies/stable/
../Assemblies/stable/publicized_assemblies/
```

The project enables unsafe blocks for future low-overhead profiler implementations, although current tools do not require unsafe code in their hot paths.

## Project structure

```text
Configuration/                     BepInEx settings
Core/                              application, tool registry, common profiling data
UI/                                window manager, scaling, theme and tooltips
Valheim/                           input, cursor, pause and GUI-scale integration
Tools/PatchProfiler/               Harmony patch profiler
Tools/MonoBehaviourProfiler/       MonoBehaviour Frame Profiler
Tools/MonoBehaviourCallProfiler/   MonoBehaviour Call Profiler
Tools/ValheimUpdateProfiler/       centralized MonoUpdaters and WearNTear measurements
Tools/LogMonitor/                  client log stream, history and grouped issues
Tools/ServerLogMonitor/            remote server log window
Tools/NetworkProfiler/             remote dedicated-server network diagnostics
Server/                            headless server backend and RPC protocol
```

## Planned work

The current product scope is intentionally focused on developer-facing timing, logs and networking. Potential future work is limited to fixes and measurements justified by practical mod-development cases rather than broad scene, rendering, AI or pathfinding inspection.
