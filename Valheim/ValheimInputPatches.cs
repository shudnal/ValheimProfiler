#nullable disable

using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace ValheimProfiler.Valheim;

internal enum ValheimInputBlockMode
{
    Off,
    Mouse,
    All
}

internal static class ValheimInputState
{
    private static int _releaseUntilFrame = -1;
    private static ValheimInputBlockMode _releasedMode;
    private static ValheimInputBlockMode _lastLiveMode;
    private static readonly List<Vector2> NoTouchPoints = new();

    internal static bool ShouldBlockAll => CurrentMode == ValheimInputBlockMode.All;
    internal static bool ShouldBlockMouse => CurrentMode != ValheimInputBlockMode.Off;
    internal static List<Vector2> EmptyTouchPoints
    {
        get
        {
            NoTouchPoints.Clear();
            return NoTouchPoints;
        }
    }

    private static ValheimInputBlockMode CurrentMode
    {
        get
        {
            ValheimProfilerApp app = ValheimProfilerPlugin.Instance?.App;
            if (app?.HasVisibleWindows == true)
                return ResolveLiveMode(app);

            return Time.frameCount <= _releaseUntilFrame
                ? _releasedMode
                : ValheimInputBlockMode.Off;
        }
    }

    internal static void Synchronize()
    {
        ValheimProfilerApp app = ValheimProfilerPlugin.Instance?.App;
        ValheimInputBlockMode liveMode = app?.HasVisibleWindows == true
            ? ResolveLiveMode(app)
            : ValheimInputBlockMode.Off;

        if (liveMode == _lastLiveMode)
            return;

        ValheimInputBlockMode previous = _lastLiveMode;
        _lastLiveMode = liveMode;

        if (liveMode != ValheimInputBlockMode.Off)
        {
            _releaseUntilFrame = -1;
            _releasedMode = ValheimInputBlockMode.Off;
            CancelInventoryDrag();
            ResetGameButtonStates();
            return;
        }

        if (previous != ValheimInputBlockMode.Off)
        {
            // Do not let the click/key that closed the profiler leak into a later Update or
            // the next FixedUpdate. Keep the previous blocking mode for one frame boundary.
            _releasedMode = previous;
            _releaseUntilFrame = Time.frameCount + 1;
            ResetGameButtonStates();
            if (previous == ValheimInputBlockMode.All)
                PlayerController.SetTakeInputDelay(Mathf.Max(PlayerController.takeInputDelay, 0.1f));
        }
    }

    internal static void Reset()
    {
        _releaseUntilFrame = -1;
        _releasedMode = ValheimInputBlockMode.Off;
        _lastLiveMode = ValheimInputBlockMode.Off;
    }

    private static ValheimInputBlockMode ResolveLiveMode(ValheimProfilerApp app)
    {
        if (app == null)
            return ValheimInputBlockMode.Off;
        if (app.Config.BlockGameInput.Value)
            return ValheimInputBlockMode.All;
        if (app.Config.BlockMouseInput.Value)
            return ValheimInputBlockMode.Mouse;
        return ValheimInputBlockMode.Off;
    }

    private static void CancelInventoryDrag()
    {
        if (InventoryGui.instance)
            InventoryGui.instance.SetupDragItem(null, null, 0);
    }

    private static void ResetGameButtonStates()
    {
        if (ZInput.instance != null)
            ZInput.ResetAllButtonStates();
    }
}

internal static class ZInputPatchMethods
{
    private const BindingFlags AllMethods = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    internal static IEnumerable<MethodBase> FindBooleanMethods(params string[] names)
    {
        var acceptedNames = new HashSet<string>(names ?? Array.Empty<string>(), StringComparer.Ordinal);
        return typeof(ZInput)
            .GetMethods(AllMethods)
            .Where(method => method.ReturnType == typeof(bool) && acceptedNames.Contains(method.Name))
            .Cast<MethodBase>()
            .Distinct();
    }

    internal static IEnumerable<MethodBase> FindStringButtonMethods()
    {
        return FindBooleanMethods(
                nameof(ZInput.GetButton),
                nameof(ZInput.GetButtonDown),
                nameof(ZInput.GetButtonUp))
            .Where(method =>
            {
                ParameterInfo[] parameters = method.GetParameters();
                return parameters.Length > 0 && parameters[0].ParameterType == typeof(string);
            });
    }
}

internal static class ZInputMouseBindingResolver
{
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Dictionary<string, CachedMouseBindings> BindingCache = new Dictionary<string, CachedMouseBindings>(StringComparer.Ordinal);

    private sealed class CachedMouseBindings
    {
        public bool DefinitionFound;
        public bool BindingSourceResolved;
        public object ButtonAction;
        public object[] MouseControls = Array.Empty<object>();
        public int MouseButtonMask;
    }

    internal static void ClearCache() => BindingCache.Clear();

    internal static void Invalidate(string action)
    {
        if (!string.IsNullOrWhiteSpace(action))
            BindingCache.Remove(action);
    }

    internal static bool IsMouseBindingActive(
        string action,
        string queryMethodName,
        bool actionResultIsKnownTrue = false)
    {
        if (string.IsNullOrWhiteSpace(action))
            return false;

        CachedMouseBindings bindings = GetConfiguredMouseBindings(action);
        if (bindings.DefinitionFound)
        {
            object activeControl = GetMemberValue(bindings.ButtonAction, "activeControl");
            if (activeControl != null)
            {
                bool activeControlIsMouse = IsMouseControl(activeControl);
                bool activeControlMatchesQuery = IsControlActive(activeControl, queryMethodName);

                if (activeControlIsMouse && (actionResultIsKnownTrue || activeControlMatchesQuery))
                    return true;

                // When the action itself says a keyboard or controller control produced the
                // successful query, keep that source available even if the same action also
                // has a mouse binding.
                if (!activeControlIsMouse && (actionResultIsKnownTrue || activeControlMatchesQuery))
                    return false;
            }

            for (int i = 0; i < bindings.MouseControls.Length; i++)
            {
                if (IsControlActive(bindings.MouseControls[i], queryMethodName))
                    return true;
            }

            int mask = bindings.MouseButtonMask;
            for (int mouseButton = 0; mask != 0 && mouseButton < 31; mouseButton++, mask >>= 1)
            {
                if ((mask & 1) != 0 && IsMouseButtonActive(mouseButton, queryMethodName))
                    return true;
            }

            // A real binding source was resolved. If it contains no active mouse binding,
            // preserve a keyboard or controller activation of the same action.
            if (bindings.BindingSourceResolved)
                return false;
        }

        int fallbackButton = action switch
        {
            "Attack" => 0,
            "Block" or "BuildMenu" => 1,
            "SecondaryAttack" or "Remove" => 2,
            _ => -1
        };

        return fallbackButton >= 0 && IsMouseButtonActive(fallbackButton, queryMethodName);
    }

    private static CachedMouseBindings GetConfiguredMouseBindings(string action)
    {
        if (BindingCache.TryGetValue(action, out CachedMouseBindings cached))
            return cached;

        CachedMouseBindings resolved = ResolveConfiguredMouseBindings(action);
        BindingCache[action] = resolved;
        return resolved;
    }

    private static CachedMouseBindings ResolveConfiguredMouseBindings(string action)
    {
        var resolved = new CachedMouseBindings();

        try
        {
            ZInput input = ZInput.instance;
            if (input?.m_buttons == null || !input.m_buttons.TryGetValue(action, out ZInput.ButtonDef definition))
                return resolved;

            resolved.DefinitionFound = true;
            ResolveDirectButtonDefinition(definition, resolved);

            object buttonAction = GetMemberValue(definition, "ButtonAction");
            if (buttonAction == null)
                buttonAction = GetMemberValue(definition, "m_buttonAction");

            if (buttonAction == null)
                return resolved;

            resolved.BindingSourceResolved = true;
            resolved.ButtonAction = buttonAction;

            object bindings = GetMemberValue(buttonAction, "bindings");
            if (bindings is IEnumerable bindingEnumerable)
            {
                foreach (object binding in bindingEnumerable)
                {
                    string path = GetMemberValue(binding, "effectivePath") as string;
                    if (string.IsNullOrWhiteSpace(path))
                        path = GetMemberValue(binding, "path") as string;

                    if (TryParseMouseButton(path, out int mouseButton) && mouseButton < 31)
                        resolved.MouseButtonMask |= 1 << mouseButton;
                }
            }

            object controls = GetMemberValue(buttonAction, "controls");
            if (controls is IEnumerable controlEnumerable)
            {
                var mouseControls = new List<object>();
                foreach (object control in controlEnumerable)
                {
                    if (control != null && IsMouseControl(control))
                        mouseControls.Add(control);
                }

                resolved.MouseControls = mouseControls.ToArray();
            }
        }
        catch
        {
            return new CachedMouseBindings();
        }

        return resolved;
    }


    private static void ResolveDirectButtonDefinition(object definition, CachedMouseBindings resolved)
    {
        if (definition == null || resolved == null)
            return;

        bool mouseBindingFlagFound = TryGetBooleanMember(definition, "m_bMouseButtonSet", out bool mouseBindingSet) ||
                                     TryGetBooleanMember(definition, "m_mouseButtonSet", out mouseBindingSet);

        object mouseButton = GetMemberValue(definition, "m_mouseButton");
        if (mouseBindingFlagFound)
        {
            resolved.BindingSourceResolved = true;
            if (mouseBindingSet && TryConvertMouseButton(mouseButton, out int mouseButtonIndex))
                resolved.MouseButtonMask |= 1 << mouseButtonIndex;
        }
        else if (mouseButton != null && TryConvertMouseButton(mouseButton, out int mouseButtonIndex))
        {
            // Some ZInput revisions omit the explicit flag and use a sentinel enum value.
            string enumName = mouseButton.ToString() ?? string.Empty;
            if (!enumName.Equals("None", StringComparison.OrdinalIgnoreCase) &&
                !enumName.Equals("Back", StringComparison.OrdinalIgnoreCase))
            {
                resolved.BindingSourceResolved = true;
                resolved.MouseButtonMask |= 1 << mouseButtonIndex;
            }
        }

        object key = GetMemberValue(definition, "m_key");
        if (key != null)
        {
            string keyName = key.ToString() ?? string.Empty;
            if (TryParseLegacyMouseKeyName(keyName, out int keyMouseButton))
            {
                resolved.BindingSourceResolved = true;
                resolved.MouseButtonMask |= 1 << keyMouseButton;
            }
            else if (!keyName.Equals("None", StringComparison.OrdinalIgnoreCase))
            {
                resolved.BindingSourceResolved = true;
            }
        }
    }

    private static bool TryConvertMouseButton(object value, out int mouseButton)
    {
        mouseButton = -1;
        if (value == null)
            return false;

        string name = value.ToString() ?? string.Empty;
        if (name.Equals("Left", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("LeftButton", StringComparison.OrdinalIgnoreCase))
        {
            mouseButton = 0;
            return true;
        }

        if (name.Equals("Right", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("RightButton", StringComparison.OrdinalIgnoreCase))
        {
            mouseButton = 1;
            return true;
        }

        if (name.Equals("Middle", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("MiddleButton", StringComparison.OrdinalIgnoreCase))
        {
            mouseButton = 2;
            return true;
        }

        if (name.Equals("Forward", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("ForwardButton", StringComparison.OrdinalIgnoreCase))
        {
            mouseButton = 3;
            return true;
        }

        if (name.Equals("Back", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("BackButton", StringComparison.OrdinalIgnoreCase))
        {
            mouseButton = 4;
            return true;
        }

        try
        {
            int numeric = Convert.ToInt32(value);
            if (numeric >= 0 && numeric < 31)
            {
                mouseButton = numeric;
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool TryParseLegacyMouseKeyName(string keyName, out int mouseButton)
    {
        mouseButton = -1;
        if (string.IsNullOrWhiteSpace(keyName) ||
            !keyName.StartsWith("Mouse", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return int.TryParse(keyName.Substring(5), out mouseButton) && mouseButton >= 0 && mouseButton < 31;
    }

    private static object GetMemberValue(object instance, string memberName)
    {
        if (instance == null || string.IsNullOrEmpty(memberName))
            return null;

        try
        {
            Type type = instance.GetType();

            PropertyInfo property = type.GetProperty(memberName, InstanceMembers);
            if (property != null && property.GetIndexParameters().Length == 0)
                return property.GetValue(instance, null);

            FieldInfo field = type.GetField(memberName, InstanceMembers);
            return field?.GetValue(instance);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetBooleanMember(object instance, string memberName, out bool value)
    {
        value = false;
        object raw = GetMemberValue(instance, memberName);
        if (!(raw is bool boolean))
            return false;

        value = boolean;
        return true;
    }

    private static bool IsMouseControl(object control)
    {
        if (control == null)
            return false;

        string path = GetMemberValue(control, "path") as string;
        if (!string.IsNullOrWhiteSpace(path) &&
            path.IndexOf("/Mouse/", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        object device = GetMemberValue(control, "device");
        string deviceType = device?.GetType().FullName ?? string.Empty;
        if (deviceType.EndsWith(".Mouse", StringComparison.Ordinal) ||
            string.Equals(device?.GetType().Name, "Mouse", StringComparison.Ordinal))
        {
            return true;
        }

        string displayName = GetMemberValue(device, "displayName") as string;
        return string.Equals(displayName, "Mouse", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsControlActive(object control, string queryMethodName)
    {
        if (control == null)
            return false;

        string memberName = queryMethodName switch
        {
            nameof(ZInput.GetButtonDown) => "wasPressedThisFrame",
            nameof(ZInput.GetButtonUp) => "wasReleasedThisFrame",
            _ => "isPressed"
        };

        if (TryGetBooleanMember(control, memberName, out bool active))
            return active;

        MethodInfo readValueAsButton = control.GetType().GetMethod(
            "ReadValueAsButton",
            InstanceMembers,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);

        if (readValueAsButton != null && readValueAsButton.ReturnType == typeof(bool))
        {
            try
            {
                return (bool)readValueAsButton.Invoke(control, null);
            }
            catch
            {
            }
        }

        return false;
    }

    private static bool TryParseMouseButton(string path, out int mouseButton)
    {
        mouseButton = -1;
        if (string.IsNullOrWhiteSpace(path) || path.IndexOf("<Mouse>", StringComparison.OrdinalIgnoreCase) < 0)
            return false;

        string normalized = path.Replace(" ", string.Empty).ToLowerInvariant();

        if (normalized.EndsWith("/leftbutton", StringComparison.Ordinal))
            mouseButton = 0;
        else if (normalized.EndsWith("/rightbutton", StringComparison.Ordinal))
            mouseButton = 1;
        else if (normalized.EndsWith("/middlebutton", StringComparison.Ordinal))
            mouseButton = 2;
        else if (normalized.EndsWith("/forwardbutton", StringComparison.Ordinal))
            mouseButton = 3;
        else if (normalized.EndsWith("/backbutton", StringComparison.Ordinal))
            mouseButton = 4;
        else
        {
            int marker = normalized.LastIndexOf("/button", StringComparison.Ordinal);
            if (marker < 0 || !int.TryParse(normalized.Substring(marker + 7), out mouseButton))
                return false;
        }

        return mouseButton >= 0;
    }

    private static bool IsMouseButtonActive(int mouseButton, string queryMethodName)
    {
        if (TryGetInputSystemMouseButton(mouseButton, out object control) &&
            IsControlActive(control, queryMethodName))
        {
            return true;
        }

        try
        {
            return queryMethodName switch
            {
                nameof(ZInput.GetButtonDown) => Input.GetMouseButtonDown(mouseButton),
                nameof(ZInput.GetButtonUp) => Input.GetMouseButtonUp(mouseButton),
                _ => Input.GetMouseButton(mouseButton)
            };
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetInputSystemMouseButton(int mouseButton, out object control)
    {
        control = null;

        try
        {
            Type mouseType = Type.GetType("UnityEngine.InputSystem.Mouse, Unity.InputSystem", throwOnError: false);
            object mouse = mouseType?.GetProperty("current", BindingFlags.Static | BindingFlags.Public)?.GetValue(null, null);
            if (mouse == null)
                return false;

            string memberName = mouseButton switch
            {
                0 => "leftButton",
                1 => "rightButton",
                2 => "middleButton",
                3 => "forwardButton",
                4 => "backButton",
                _ => null
            };

            if (memberName == null)
                return false;

            control = GetMemberValue(mouse, memberName);
            return control != null;
        }
        catch
        {
            return false;
        }
    }
}

[HarmonyPatch]
internal static class ZInputMouseBindingCachePatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        return typeof(ZInput)
            .GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method =>
            {
                if (method.Name == nameof(ZInput.Load))
                    return true;

                if (method.Name != nameof(ZInput.AddButton))
                    return false;

                ParameterInfo[] parameters = method.GetParameters();
                return parameters.Length > 0 && parameters[0].ParameterType == typeof(string);
            })
            .Cast<MethodBase>()
            .Distinct();
    }

    private static void Postfix(MethodBase __originalMethod, object[] __args)
    {
        if (__originalMethod?.Name == nameof(ZInput.Load))
        {
            ZInputMouseBindingResolver.ClearCache();
            return;
        }

        string action = __args != null && __args.Length > 0 ? __args[0] as string : null;
        ZInputMouseBindingResolver.Invalidate(action);
    }
}

[HarmonyPatch(typeof(PlayerController), nameof(PlayerController.TakeInput), new[] { typeof(bool) })]
[HarmonyPriority(Priority.Last)]
internal static class PlayerControllerTakeInputPatch
{
    private static void Postfix(ref bool __result)
    {
        // Keep native FixedUpdate/LateUpdate running so Valheim executes its own zero-controls
        // and zero-look paths instead of skipping controller simulation entirely.
        if (ValheimInputState.ShouldBlockAll)
            __result = false;
    }
}

[HarmonyPatch(typeof(TextInput), nameof(TextInput.IsVisible))]
[HarmonyPriority(Priority.Last)]
internal static class TextInputIsVisiblePatch
{
    private static void Postfix(ref bool __result)
    {
        if (ValheimInputState.ShouldBlockAll)
            __result = true;
    }
}

internal static class ValheimInputPatchUtilities
{
    internal static MethodInfo InputMethod(Type type, string name) => AccessTools.DeclaredMethod(type, name);

    internal static MethodInfo AnalogInputMethod(Type valueType)
    {
        MethodInfo generic = InputMethod(typeof(ZInput), nameof(ZInput.ReadValueDef));
        return generic?.MakeGenericMethod(valueType);
    }

    internal static MethodInfo OptionalMethod(string typeName, string methodName)
    {
        Type type = AccessTools.TypeByName(typeName);
        return type == null ? null : AccessTools.Method(type, methodName);
    }
}

[HarmonyPatch]
internal static class ZInputAllBooleanQueriesPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        MethodInfo[] methods =
        {
            ValheimInputPatchUtilities.InputMethod(typeof(ZInput), nameof(ZInput.TryGetButtonState)),
            ValheimInputPatchUtilities.InputMethod(typeof(ZInput), nameof(ZInput.TryGetKeyStateLowLevel)),
            ValheimInputPatchUtilities.InputMethod(typeof(ZInput), nameof(ZInput.GetRadialTap)),
            ValheimInputPatchUtilities.InputMethod(typeof(ZInput), nameof(ZInput.GetRadialMultiTap)),
            ValheimInputPatchUtilities.InputMethod(typeof(ZInput), nameof(ZInput.HasDoubleTapped))
        };

        return methods.Where(method => method != null).Cast<MethodBase>();
    }

    [HarmonyPriority(Priority.First)]
    private static bool Prefix(ref bool __result)
    {
        // Deliberately do not patch ShouldAcceptInputFromSource. Valheim uses it while processing
        // action cancellation and device switching; blocking it can leave held controls stuck and
        // can starve mouse/cursor state that IMGUI tooltips rely on.
        if (!ValheimInputState.ShouldBlockAll)
            return true;

        __result = false;
        return false;
    }
}

[HarmonyPatch]
internal static class ZInputAllFloatQueriesPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        MethodInfo[] methods =
        {
            ValheimInputPatchUtilities.AnalogInputMethod(typeof(float)),
            ValheimInputPatchUtilities.InputMethod(typeof(ZInput), nameof(ZInput.GetButtonPressedTimer)),
            ValheimInputPatchUtilities.InputMethod(typeof(ZInput), nameof(ZInput.GetButtonLastPressedTimer)),
            ValheimInputPatchUtilities.InputMethod(typeof(ZInput), nameof(ZInput.GetLongPressProgress))
        };

        return methods.Where(method => method != null).Cast<MethodBase>();
    }

    [HarmonyPriority(Priority.Last)]
    private static void Postfix(ref float __result)
    {
        if (ValheimInputState.ShouldBlockAll)
            __result = 0f;
    }
}

[HarmonyPatch]
internal static class ZInputAllVectorQueriesPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        MethodInfo[] methods =
        {
            ValheimInputPatchUtilities.AnalogInputMethod(typeof(Vector2)),
            ValheimInputPatchUtilities.InputMethod(typeof(ZInput), nameof(ZInput.GetGyro))
        };

        return methods.Where(method => method != null).Cast<MethodBase>();
    }

    [HarmonyPriority(Priority.Last)]
    private static void Postfix(ref Vector2 __result)
    {
        if (ValheimInputState.ShouldBlockAll)
            __result = Vector2.zero;
    }
}

[HarmonyPatch(typeof(ZInput), nameof(ZInput.GetTouchPinchPoints))]
internal static class ZInputTouchPinchBlockPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(ref List<Vector2> __result)
    {
        if (ValheimInputState.ShouldBlockAll)
            __result = ValheimInputState.EmptyTouchPoints;
    }
}

[HarmonyPatch]
internal static class ValheimPointerInteractionBlockPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        MethodInfo[] methods =
        {
            AccessTools.Method(typeof(InventoryGrid), nameof(InventoryGrid.OnLeftClick)),
            AccessTools.Method(typeof(InventoryGrid), nameof(InventoryGrid.OnLeftDown)),
            AccessTools.Method(typeof(InventoryGrid), nameof(InventoryGrid.OnRightDown)),
            AccessTools.Method(typeof(InventoryGrid), nameof(InventoryGrid.OnBeginDrag)),
            AccessTools.Method(typeof(InventoryGrid), nameof(InventoryGrid.OnReleasedOn)),
            AccessTools.Method(typeof(InventoryGrid), nameof(InventoryGrid.OnDragEnd)),
            AccessTools.Method(typeof(InventoryGui), nameof(InventoryGui.OnSelectedItem)),
            AccessTools.Method(typeof(InventoryGui), nameof(InventoryGui.OnReleasedItem)),
            AccessTools.Method(typeof(InventoryGui), nameof(InventoryGui.OnRightClickItem)),
            AccessTools.Method(typeof(UIInputHandler), nameof(UIInputHandler.OnPointerDown)),
            AccessTools.Method(typeof(UIInputHandler), nameof(UIInputHandler.OnPointerClick)),
            AccessTools.Method(typeof(UIDragHandler), nameof(UIDragHandler.OnBeginDrag)),
            AccessTools.Method(typeof(UIDragHandler), nameof(UIDragHandler.OnDrag)),
            AccessTools.Method(typeof(UIDragHandler), nameof(UIDragHandler.OnEndDrag)),
            AccessTools.Method(typeof(UIDragHandler), nameof(UIDragHandler.OnReleasedOn)),
            AccessTools.Method(typeof(Button), nameof(Button.OnPointerClick)),
            AccessTools.Method(typeof(Toggle), nameof(Toggle.OnPointerClick)),
            AccessTools.Method(typeof(Selectable), nameof(Selectable.OnPointerDown)),
            AccessTools.Method(typeof(Slider), nameof(Slider.OnPointerDown)),
            AccessTools.Method(typeof(Slider), nameof(Slider.OnDrag)),
            AccessTools.Method(typeof(Scrollbar), nameof(Scrollbar.OnPointerDown)),
            AccessTools.Method(typeof(Scrollbar), nameof(Scrollbar.OnDrag)),
            AccessTools.Method(typeof(Scrollbar), nameof(Scrollbar.OnBeginDrag)),
            AccessTools.Method(typeof(ScrollRect), nameof(ScrollRect.OnScroll)),
            AccessTools.Method(typeof(ScrollRect), nameof(ScrollRect.OnBeginDrag)),
            AccessTools.Method(typeof(ScrollRect), nameof(ScrollRect.OnDrag)),
            AccessTools.Method(typeof(InputField), nameof(InputField.OnPointerClick)),
            AccessTools.Method(typeof(Dropdown), nameof(Dropdown.OnPointerClick)),
            ValheimInputPatchUtilities.OptionalMethod("TMPro.TMP_InputField", "OnPointerClick"),
            ValheimInputPatchUtilities.OptionalMethod("TMPro.TMP_Dropdown", "OnPointerClick")
        };

        return methods.Where(method => method != null).Cast<MethodBase>().Distinct();
    }

    [HarmonyPriority(Priority.First)]
    private static bool Prefix() => !ValheimInputState.ShouldBlockMouse;
}

[HarmonyPatch(typeof(UIInputHandler), nameof(UIInputHandler.OnPointerUp))]
internal static class UIInputHandlerPointerUpBlockPatch
{
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(UIInputHandler __instance)
    {
        if (!ValheimInputState.ShouldBlockMouse)
            return true;

        // Do not dispatch the background UI action, but release InventoryGrid's pressed-item
        // state so opening the profiler during a click cannot leave the inventory stuck.
        InventoryGrid grid = __instance.GetComponentInParent<InventoryGrid>();
        if (grid)
            grid.OnLeftRelease(__instance);

        return false;
    }
}

[HarmonyPatch]
internal static class ValheimAllInputInteractionBlockPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        MethodInfo[] methods =
        {
            AccessTools.Method(typeof(InventoryGrid), nameof(InventoryGrid.EquipHovered)),
            AccessTools.Method(typeof(Player), nameof(Player.UseHotbarItem)),
            AccessTools.Method(typeof(Button), nameof(Button.OnSubmit)),
            AccessTools.Method(typeof(Button), "Press"),
            AccessTools.Method(typeof(Toggle), nameof(Toggle.OnSubmit)),
            AccessTools.Method(typeof(Selectable), nameof(Selectable.OnMove)),
            AccessTools.Method(typeof(Slider), nameof(Slider.OnMove)),
            AccessTools.Method(typeof(Scrollbar), nameof(Scrollbar.OnMove)),
            AccessTools.Method(typeof(InputField), nameof(InputField.OnUpdateSelected)),
            AccessTools.Method(typeof(InputField), nameof(InputField.OnSubmit)),
            AccessTools.Method(typeof(Dropdown), nameof(Dropdown.OnSubmit)),
            ValheimInputPatchUtilities.OptionalMethod("TMPro.TMP_InputField", "OnUpdateSelected"),
            ValheimInputPatchUtilities.OptionalMethod("TMPro.TMP_InputField", "OnSubmit"),
            ValheimInputPatchUtilities.OptionalMethod("TMPro.TMP_Dropdown", "OnSubmit")
        };

        return methods.Where(method => method != null).Cast<MethodBase>().Distinct();
    }

    [HarmonyPriority(Priority.First)]
    private static bool Prefix() => !ValheimInputState.ShouldBlockAll;
}

[HarmonyPatch]
internal static class ZInputMouseMappedButtonBlockPatch
{
    private static IEnumerable<MethodBase> TargetMethods() => ZInputPatchMethods.FindStringButtonMethods();

    [HarmonyPriority(Priority.Last)]
    private static Exception Finalizer(
        Exception __exception,
        MethodBase __originalMethod,
        object[] __args,
        ref bool __result)
    {
        if (__exception != null ||
            !__result ||
            ValheimInputState.ShouldBlockAll ||
            !ValheimInputState.ShouldBlockMouse)
        {
            return __exception;
        }

        string action = __args != null && __args.Length > 0 ? __args[0] as string : null;
        if (ZInputMouseBindingResolver.IsMouseBindingActive(
                action,
                __originalMethod?.Name,
                actionResultIsKnownTrue: true))
        {
            __result = false;
        }

        return __exception;
    }
}

[HarmonyPatch]
internal static class PlayerSetControlsMouseBlockPatch
{
    private const BindingFlags AllMethods = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static IEnumerable<MethodBase> TargetMethods()
    {
        string[] requiredNames =
        {
            "attack",
            "attackHold",
            "secondaryAttack",
            "secondaryAttackHold",
            "block",
            "blockHold"
        };

        return typeof(Player)
            .GetMethods(AllMethods)
            .Where(method => method.Name == "SetControls")
            .Where(method =>
            {
                var names = new HashSet<string>(
                    method.GetParameters().Select(parameter => parameter.Name),
                    StringComparer.Ordinal);
                return requiredNames.All(names.Contains);
            })
            .Cast<MethodBase>()
            .Distinct();
    }

    [HarmonyPriority(Priority.Last)]
    private static void Prefix(
        ref bool attack,
        ref bool attackHold,
        ref bool secondaryAttack,
        ref bool secondaryAttackHold,
        ref bool block,
        ref bool blockHold)
    {
        if (!ValheimInputState.ShouldBlockMouse || ValheimInputState.ShouldBlockAll)
            return;

        if ((attack || attackHold) &&
            ZInputMouseBindingResolver.IsMouseBindingActive(
                "Attack",
                nameof(ZInput.GetButton),
                actionResultIsKnownTrue: true))
        {
            attack = false;
            attackHold = false;
        }

        if ((secondaryAttack || secondaryAttackHold) &&
            ZInputMouseBindingResolver.IsMouseBindingActive(
                "SecondaryAttack",
                nameof(ZInput.GetButton),
                actionResultIsKnownTrue: true))
        {
            secondaryAttack = false;
            secondaryAttackHold = false;
        }

        if ((block || blockHold) &&
            ZInputMouseBindingResolver.IsMouseBindingActive(
                "Block",
                nameof(ZInput.GetButton),
                actionResultIsKnownTrue: true))
        {
            block = false;
            blockHold = false;
        }
    }
}

[HarmonyPatch]
internal static class ZInputMouseBooleanBlockPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        return ZInputPatchMethods.FindBooleanMethods(
            nameof(ZInput.GetMouseButton),
            nameof(ZInput.GetMouseButtonDown),
            nameof(ZInput.GetMouseButtonUp));
    }

    [HarmonyPriority(Priority.First)]
    private static bool Prefix(ref bool __result)
    {
        if (!ValheimInputState.ShouldBlockMouse)
            return true;

        __result = false;
        return false;
    }
}

[HarmonyPatch(typeof(ZInput), nameof(ZInput.GetMouseScrollWheel))]
internal static class ZInputMouseScrollBlockPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(ref float __result)
    {
        if (ValheimInputState.ShouldBlockMouse)
            __result = 0f;
    }
}

[HarmonyPatch(typeof(ZInput), nameof(ZInput.GetMouseDelta))]
internal static class ZInputMouseDeltaBlockPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(ref Vector2 __result)
    {
        if (ValheimInputState.ShouldBlockMouse)
            __result = Vector2.zero;
    }
}
