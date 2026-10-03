#nullable disable

using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace ValheimProfiler.Core.Profiling;

/// <summary>
/// Clears legacy MonoMod member-resolution results at each profiler-owned backend boundary.
/// This is a cache-lifetime workaround, not a resolver patch or a method-name heuristic.
/// </summary>
internal static class MonoModResolverCache
{
    private const int CacheLockTimeoutMilliseconds = 2000;
    private static readonly Lazy<CacheBinding> Binding = new Lazy<CacheBinding>(ResolveBinding);
    private static long _clearCount;
    private static Exception _resetFailure;

    internal static long ClearCount => Interlocked.Read(ref _clearCount);

    /// <summary>
    /// The operation must finish its backend work before returning; do not return a lazy iterator.
    /// No cache lock is held while Harmony, a transpiler or the detour backend executes.
    /// Patch and General mutations already run on the same Unity main thread.
    /// </summary>
    internal static T Run<T>(Func<T> operation, Action<string> trace = null)
    {
        if (operation == null)
            throw new ArgumentNullException(nameof(operation));

        Clear("before", operationStarted: false, trace, null);
        Exception operationFailure = null;
        try
        {
            return operation();
        }
        catch (Exception ex)
        {
            operationFailure = ex;
            throw;
        }
        finally
        {
            // Also discard entries produced by a failed rebuild before rollback starts.
            // Managed finally is not guaranteed to run after a native process crash.
            Clear("after", operationStarted: true, trace, operationFailure);
        }
    }

    internal static void Run(Action operation, Action<string> trace = null)
    {
        if (operation == null)
            throw new ArgumentNullException(nameof(operation));
        Run(() => { operation(); return 0; }, trace);
    }

    private static void Clear(string phase, bool operationStarted, Action<string> trace, Exception operationFailure)
    {
        // Once cache isolation failed, this process is already restart-only. Do not wait for
        // the same stuck lock once per remaining method or continue with an unprotected rebuild.
        Exception previousFailure = Volatile.Read(ref _resetFailure);
        if (previousFailure != null)
            throw ResetException(phase, operationStarted, operationFailure, previousFailure);
        try
        {
            CacheBinding binding = Binding.Value;
            Dictionary<string, WeakReference> cache = binding.Cache;
            bool entered = false;
            int removed;
            try
            {
                // Match the backend's own monitor, not IDictionary.SyncRoot or a replacement object.
                // A stuck cache lock must abort this operation instead of hanging indefinitely here.
                Monitor.TryEnter(cache, CacheLockTimeoutMilliseconds, ref entered);
                if (!entered)
                    throw new TimeoutException("The MonoMod resolver cache lock could not be acquired.");
                removed = cache.Count;
                cache.Clear();
            }
            finally
            {
                if (entered)
                    Monitor.Exit(cache);
            }
            long total = Interlocked.Increment(ref _clearCount);
            if (trace != null)
            {
                // Trace outside the monitor. Logging must not introduce a lock-order dependency.
                try { trace("RESOLVER CACHE CLEAR | phase=" + phase + " entries=" + removed +
                    " total=" + total + " backend=" + binding.AssemblyIdentity); }
                catch { /* Optional crash tracing cannot invalidate a completed reset. */ }
            }
        }
        catch (Exception ex)
        {
            Interlocked.CompareExchange(ref _resetFailure, ex, null);
            throw ResetException(phase, operationStarted, operationFailure, ex);
        }
    }

    private static ResolverCacheException ResetException(string phase, bool operationStarted,
        Exception operationFailure, Exception resetFailure)
    {
        Exception cause = operationFailure == null ? resetFailure : new AggregateException(
            "The backend operation and the following resolver cache reset both failed.", operationFailure, resetFailure);
        return new ResolverCacheException(phase, operationStarted, cause);
    }

    private static CacheBinding ResolveBinding()
    {
        // Resolve the dependency requested by the loaded Harmony assembly. Do not choose an
        // unrelated embedded copy by namespace, replace backend DLLs or patch the resolver itself.
        AssemblyName reference = typeof(Harmony).Assembly.GetReferencedAssemblies()
            .SingleOrDefault(name => name.Name == "MonoMod.Utils");
        if (reference == null)
            throw new NotSupportedException("The loaded Harmony assembly does not reference MonoMod.Utils; resolver cache reset is unavailable.");

        Assembly backend = Assembly.Load(reference);
        Type helper = backend.GetType("MonoMod.Utils.ReflectionHelper", throwOnError: true);
        FieldInfo field = helper.GetField("ResolveReflectionCache",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        if (field == null || !field.IsStatic || field.FieldType != typeof(Dictionary<string, WeakReference>))
            throw new NotSupportedException("The loaded MonoMod resolver does not expose the expected ResolveReflectionCache dictionary.");
        if (!(field.GetValue(null) is Dictionary<string, WeakReference> cache))
            throw new InvalidOperationException("The MonoMod resolver cache dictionary is unavailable.");

        return new CacheBinding { Cache = cache, AssemblyIdentity = backend.FullName };
    }

    private sealed class CacheBinding
    {
        internal Dictionary<string, WeakReference> Cache;
        internal string AssemblyIdentity;
    }
}

/// <summary>Distinguishes a blocked operation from a rebuild whose final reset failed.</summary>
internal sealed class ResolverCacheException : Exception
{
    internal bool OperationStarted { get; }
    internal string Phase { get; }

    internal ResolverCacheException(string phase, bool operationStarted, Exception innerException)
        : base("MonoMod resolver cache reset failed " + phase + " the backend operation. " +
               (operationStarted ? "The operation may have changed instrumentation." : "The backend operation was not attempted."), innerException)
    {
        Phase = phase;
        OperationStarted = operationStarted;
    }
}
