#nullable disable

using UnityEngine;

namespace ValheimProfiler.Valheim;

internal sealed class ValheimCursorController
{
    private bool _captured;
    private CursorLockMode _previousLockState;
    private bool _previousVisible;

    internal void Update(bool active)
    {
        if (active)
            Unlock();
        else
            Release();
    }

    internal void LateUpdate(bool active)
    {
        if (active)
            Unlock();
    }

    internal void OnGUI(bool active)
    {
        if (active)
            Unlock();
    }

    internal void Release()
    {
        if (!_captured)
            return;

        Cursor.lockState = _previousLockState;
        Cursor.visible = _previousVisible;
        _captured = false;
    }

    internal void OverrideReleaseState(CursorLockMode lockState, bool visible)
    {
        if (!_captured)
            return;

        // Scene transitions may replace the native cursor state while a profiler
        // window keeps the actual cursor unlocked. Update only the state restored
        // when the profiler UI closes; leave the currently visible cursor alone.
        _previousLockState = lockState;
        _previousVisible = visible;
    }

    private void Unlock()
    {
        if (!_captured || Cursor.lockState != CursorLockMode.None || !Cursor.visible)
        {
            _previousLockState = Cursor.lockState;
            _previousVisible = Cursor.visible;
            _captured = true;
        }

        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }
}