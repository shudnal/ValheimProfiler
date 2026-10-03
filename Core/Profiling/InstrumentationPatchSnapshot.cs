#nullable disable

using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ValheimProfiler.Core.Profiling;

/// <summary>Immutable patch-registration identities, not a snapshot of native code or game state.</summary>
internal sealed class InstrumentationPatchSnapshot
{
    // HarmonyX exposes this additional patch family. Keep compatibility with Harmony builds without it.
    private static readonly FieldInfo ManipulatorsField = typeof(Patches).GetField("ILManipulators");
    private static readonly PropertyInfo ManipulatorsProperty = typeof(Patches).GetProperty("ILManipulators");
    private readonly Item[] _items;

    private InstrumentationPatchSnapshot(Patches patches) => _items = Read(patches).ToArray();

    internal static InstrumentationPatchSnapshot Capture(MethodBase method) => new InstrumentationPatchSnapshot(Harmony.GetPatchInfo(method));
    internal bool HasOwner(string owner) => _items.Any(item => item.Owner == owner);

    internal bool SameForeignPatches(InstrumentationPatchSnapshot other, string owner)
    {
        Item[] before = _items.Where(item => item.Owner != owner).ToArray();
        Item[] after = other._items.Where(item => item.Owner != owner).ToArray();
        return before.Length == after.Length && before.Zip(after, (a, b) => a.SameAs(b)).All(equal => equal);
    }

    internal bool MatchesOwned(string owner, MethodInfo prefix, MethodInfo finalizer, MethodInfo transpiler)
    {
        Item[] owned = _items.Where(item => item.Owner == owner).ToArray();
        int expected = (prefix == null ? 0 : 1) + (finalizer == null ? 0 : 1) + (transpiler == null ? 0 : 1);
        return owned.Length == expected &&
            Matches(owned, "Prefix", prefix) && Matches(owned, "Finalizer", finalizer) && Matches(owned, "Transpiler", transpiler);
    }

    private static bool Matches(Item[] items, string kind, MethodInfo method) =>
        items.Count(item => item.Kind == kind && Equals(item.Method, method)) == (method == null ? 0 : 1);

    internal static IEnumerable<Item> Read(Patches patches)
    {
        if (patches == null)
            yield break;
        foreach (Patch patch in patches.Prefixes) yield return new Item("Prefix", patch);
        foreach (Patch patch in patches.Postfixes) yield return new Item("Postfix", patch);
        foreach (Patch patch in patches.Finalizers) yield return new Item("Finalizer", patch);
        foreach (Patch patch in patches.Transpilers) yield return new Item("Transpiler", patch);
        object extra = ManipulatorsField != null ? ManipulatorsField.GetValue(patches) : ManipulatorsProperty?.GetValue(patches, null);
        if (extra is IEnumerable<Patch> manipulators)
            foreach (Patch patch in manipulators) yield return new Item("ILManipulator", patch);
    }

    internal sealed class Item
    {
        internal readonly string Kind;
        internal readonly Patch Patch;
        internal readonly MethodInfo Method;
        internal readonly string Owner;
        private readonly int _index;
        private readonly int _priority;
        private readonly string[] _before;
        private readonly string[] _after;

        internal Item(string kind, Patch patch)
        {
            if (patch == null)
                throw new InvalidOperationException("Patch metadata contains a null registration.");
            Kind = kind;
            Patch = patch;
            Method = patch.PatchMethod;
            Owner = patch.owner;
            _index = patch.index;
            _priority = patch.priority;
            _before = patch.before?.ToArray() ?? Array.Empty<string>();
            _after = patch.after?.ToArray() ?? Array.Empty<string>();
        }

        internal bool SameAs(Item other) => Kind == other.Kind && Equals(Method, other.Method) && Owner == other.Owner &&
            _index == other._index && _priority == other._priority && _before.SequenceEqual(other._before) && _after.SequenceEqual(other._after);
    }
}
