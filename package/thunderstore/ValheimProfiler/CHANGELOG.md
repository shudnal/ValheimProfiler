# 1.1.14
* Updated for Valheim 1.0.30.

# 1.1.13
* Added General Method Profiler with mod/class/method selection, call-tree navigation, and inclusive/self timing.
* Added Frame budget view with configurable Target FPS, normalized ms/frame and calls/frame, and average cost per call; kept Max over 60 sec for spikes, percentiles, and long-running behavior.
* Added Exclude nested calls mode to focus on a method's own measured work, including exclusion of profiler bookkeeping around measured child calls.
* Added Gradual start with visible install/remove/rollback progress and cancellation for large profiling selections; it can be disabled for the fastest one-frame transition.
* Improved Patch Profiler timing accuracy, transpiler-call measurements, sparse-call accounting, and consistency with General Method Profiler.
* Added UI improvements for large selections, including inline search, sorting, grouping, clearer method/class columns, and selected-mod highlighting.
* Added consistent filtering of samples from frames affected by GC when Ignore gc allocations is enabled.
* Fixed crashes and corrupted runtime state when profiling problematic methods or multiple mods that embed similar helper libraries, with safer installation, rollback, removal, and recovery behavior.
* Improved diagnostics and logging while keeping crash tracing optional and disabled by default.

# 1.0.2
* Reworked cursor ownership around Valheim 1.0.7 `ZCursor` and native cursor update methods.
* Reworked input blocking to suppress gameplay/UI consumers without stopping Valheim input-source collection.
* Hardened IMGUI tooltip handling and removed the synthetic mouse-position workaround.

# 1.0.1
* Updated for the Valheim 1.0.7 release.
* Updated required dependencies to BepInExPack Valheim 5.4.2350.

# 1.0.0
* Initial public release.
* Added in-game profiling tools for Harmony patches, MonoBehaviour callbacks, Valheim update loops, client/server logs, and dedicated-server network diagnostics.
