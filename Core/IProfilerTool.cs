#nullable disable

namespace ValheimProfiler.Core;

internal interface IProfilerTool
{
    string Id { get; }
    string DisplayName { get; }
    bool IsWindowVisible { get; }
    bool IsActive { get; }

    void ShowWindow();
    void ToggleWindow();
    void Update();
    void Shutdown();
}

internal interface IProfilerToolAvailability
{
    bool IsAvailable { get; }
    bool CanOpenWhenUnavailable { get; }
    string AvailabilityTooltip { get; }
}
