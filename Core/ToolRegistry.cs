#nullable disable

using System;
using System.Collections.Generic;

namespace ValheimProfiler.Core;

internal sealed class ToolRegistry
{
    private readonly List<IProfilerTool> _tools = new List<IProfilerTool>();

    internal IReadOnlyList<IProfilerTool> Tools => _tools;

    internal T Register<T>(T tool) where T : class, IProfilerTool
    {
        if (tool == null)
            throw new ArgumentNullException(nameof(tool));

        for (int i = 0; i < _tools.Count; i++)
        {
            if (string.Equals(_tools[i].Id, tool.Id, StringComparison.Ordinal))
                throw new InvalidOperationException($"Profiler tool '{tool.Id}' is already registered.");
        }

        _tools.Add(tool);
        return tool;
    }

    internal void Update()
    {
        for (int i = 0; i < _tools.Count; i++)
            _tools[i].Update();
    }

    internal void Shutdown()
    {
        for (int i = _tools.Count - 1; i >= 0; i--)
            _tools[i].Shutdown();

        _tools.Clear();
    }
}