using System;
using System.Windows.Threading;

namespace Jer.Core;

/// <summary>Runs the latest scheduled action once things have been quiet for a moment (UI thread).</summary>
public sealed class Debouncer
{
    private readonly DispatcherTimer _timer;
    private Action? _pending;

    public Debouncer(TimeSpan delay)
    {
        _timer = new DispatcherTimer { Interval = delay };
        _timer.Tick += (_, _) => Flush();
    }

    public void Run(Action action)
    {
        _pending = action;
        _timer.Stop();
        _timer.Start();
    }

    public void Flush()
    {
        _timer.Stop();
        var action = _pending;
        _pending = null;
        action?.Invoke();
    }
}
