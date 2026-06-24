#nullable disable

using System;
using System.Collections.Generic;

namespace ValheimProfiler.Core.Logging;

internal static class LogHistoryMerge
{
    internal static int FindOverlap<TLeft, TRight>(
        IReadOnlyList<TLeft> older,
        IReadOnlyList<TRight> newer,
        Func<TLeft, string> olderFingerprint,
        Func<TRight, string> newerFingerprint,
        int minimumRun = 3)
    {
        if (older == null || newer == null || older.Count == 0 || newer.Count == 0)
            return -1;

        int required = Math.Max(1, Math.Min(minimumRun, Math.Min(older.Count, newer.Count)));
        int olderStart = Math.Max(0, older.Count - 512);
        int newerLimit = Math.Min(newer.Count, 128);

        for (int n = 0; n < newerLimit; n++)
        {
            string first = newerFingerprint(newer[n]);
            for (int o = olderStart; o < older.Count; o++)
            {
                if (!string.Equals(first, olderFingerprint(older[o]), StringComparison.Ordinal))
                    continue;

                int run = 1;
                while (o + run < older.Count && n + run < newer.Count &&
                       string.Equals(olderFingerprint(older[o + run]), newerFingerprint(newer[n + run]), StringComparison.Ordinal))
                {
                    run++;
                }

                if (run >= required)
                    return o;
            }
        }

        return -1;
    }
}
