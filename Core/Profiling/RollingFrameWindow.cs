#nullable disable

using System.Collections.Generic;

namespace ValheimProfiler.Core.Profiling;

/// <summary>Main-thread frame denominator for sparse general-method samples.</summary>
internal sealed class RollingFrameWindow
{
    private readonly Queue<float> _completed = new Queue<float>();
    private int _frame = -1;
    private float _frameTime;

    internal void Observe(int frame, float now)
    {
        if (_frame != frame)
        {
            if (frame < _frame || now < _frameTime)
                _completed.Clear();
            else if (_frame >= 0)
                _completed.Enqueue(now); // The previous frame is complete when the next frame is observed.
            _frame = frame;
            _frameTime = now;
        }
        Trim(now);
    }

    internal int Count(float now)
    {
        Trim(now);
        return _completed.Count;
    }

    internal void Reset(int frame, float now)
    {
        _completed.Clear();
        _frame = frame;
        _frameTime = now;
    }

    private void Trim(float now)
    {
        while (_completed.Count > 0 && now - _completed.Peek() > 1f)
            _completed.Dequeue();
    }
}
