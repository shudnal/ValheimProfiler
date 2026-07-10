# Valheim Profiler

Valheim Profiler is an in-game diagnostics toolkit for Valheim mod development and server troubleshooting. It profiles Harmony patches, MonoBehaviour callbacks, Valheim update loops, logs, and selected dedicated-server network activity directly from an IMGUI overlay.

The mod is intended for short diagnostic sessions, not for normal gameplay. Most profilers instrument hot paths and can add overhead while they are enabled.

## Features

- **Launcher**: opens the profiler suite from one compact window.
- **Patch Profiler**: measures Harmony Prefix, Postfix, Finalizer, transpiled target methods, and detected transpiler-injected call sites.
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

The MonoBehaviour Frame Profiler, MonoBehaviour Call Profiler, and Valheim Update Profiler are available from the launcher but have no default direct hotkey.

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
