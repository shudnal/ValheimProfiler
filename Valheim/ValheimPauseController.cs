#nullable disable

namespace ValheimProfiler.Valheim;

internal sealed class ValheimPauseController
{
    private readonly ValheimProfilerConfig _config;
    private bool _pausedByProfiler;

    internal ValheimPauseController(ValheimProfilerConfig config) => _config = config;

    internal void Update(bool hasVisibleWindows)
    {
        if (!_config.PauseGame.Value || !Game.instance)
        {
            if (_pausedByProfiler)
                Release();
            return;
        }

        if (hasVisibleWindows)
        {
            if (!_pausedByProfiler && !Game.IsPaused() && Game.CanPause())
            {
                Game.Pause();
                _pausedByProfiler = true;
            }
        }
        else if (_pausedByProfiler)
        {
            Release();
        }
    }

    internal void Release()
    {
        if (_pausedByProfiler && Game.instance && !Menu.IsActive() && Game.IsPaused())
            Game.Unpause();
        _pausedByProfiler = false;
    }
}