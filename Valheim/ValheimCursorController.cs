#nullable disable

using UnityEngine;

namespace ValheimProfiler.Valheim;

internal sealed class ValheimCursorController
{
    private bool _acquired;
    private CursorLockMode _savedLockState;
    private bool _savedRequested;
    private bool _savedVisible;
    private bool _savedHardwareVisible;

    internal void Update(bool active)
    {
        if (active)
            Acquire();
        else
            Release();
    }

    internal void LateUpdate(bool active)
    {
        if (active)
            ApplyWindowCursor();
    }

    internal void OnGUI(bool active)
    {
        if (active)
            ApplyWindowCursor();
    }

    internal void OnApplicationFocus(bool focused, bool active)
    {
        if (focused && active)
            ApplyWindowCursor();
    }

    internal void ApplyWindowCursor()
    {
        if (ValheimProfilerPlugin.Instance?.App?.HasVisibleWindows != true || !Application.isFocused)
            return;

        ZCursor.LockState = CursorLockMode.None;
        if (ZInput.instance != null)
            ZCursor.Show();
        else
            Cursor.visible = true;
    }

    internal void Release()
    {
        if (!_acquired)
            return;

        _acquired = false;

        if (ZInput.instance != null)
        {
            // Let the current scene decide the cursor state. This is more reliable than restoring
            // a snapshot captured before a menu/world transition.
            if (GameCamera.instance)
            {
                GameCamera.instance.UpdateMouseCapture();
                if (Menu.instance && Menu.IsActive())
                    Menu.instance.UpdateCursor();
                return;
            }

            if (FejdStartup.instance)
            {
                FejdStartup.instance.UpdateCursor();
                return;
            }

            if (Menu.instance && Menu.IsActive())
            {
                Menu.instance.UpdateCursor();
                return;
            }

            ZCursor.LockState = _savedLockState;
            ZCursor.SetRequested(_savedRequested);
            ZCursor.SetVisible(_savedVisible);
            return;
        }

        ZCursor.LockState = _savedLockState;
        Cursor.visible = _savedHardwareVisible;
    }

    private void Acquire()
    {
        if (!_acquired)
        {
            _savedLockState = ZCursor.LockState;
            _savedRequested = ZCursor.IsRequested;
            _savedVisible = ZCursor.IsVisible;
            _savedHardwareVisible = Cursor.visible;
            _acquired = true;
        }

        ApplyWindowCursor();
    }
}
