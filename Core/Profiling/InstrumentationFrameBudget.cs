#nullable disable

using System.Diagnostics;
using System.Reflection;

namespace ValheimProfiler.Core.Profiling;

internal static class InstrumentationFrameBudget
{
    internal const double Milliseconds = 400.0;
    internal const int MaxBackendOperations = 2560;
    internal static double EffectiveMilliseconds(bool gradual) => gradual ? Milliseconds : double.PositiveInfinity;
    internal static int EffectiveMaxBackendOperations(bool gradual) => gradual ? MaxBackendOperations : int.MaxValue;

    private static readonly double TicksPerMillisecond = Stopwatch.Frequency / 1000.0;

    internal static string MethodLabel(MethodBase method)
    {
        if (method == null)
            return string.Empty;
        try
        {
            string assembly = method.DeclaringType?.Assembly.GetName().Name ?? "Unknown";
            string type = method.DeclaringType?.Name ?? "<global>";
            return assembly + " :: " + type + "." + method.Name;
        }
        catch
        {
            return method.Name ?? string.Empty;
        }
    }

    internal static bool IsExhausted(long startTicks, int backendOperations, bool gradual)
    {
        if (!gradual)
            return false;
        if (backendOperations >= MaxBackendOperations)
            return true;
        return (Stopwatch.GetTimestamp() - startTicks) / TicksPerMillisecond >= Milliseconds;
    }
}
