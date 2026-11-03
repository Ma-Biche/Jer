using System;
using System.Collections.Generic;
using System.Windows.Input;
using System.Windows.Media;

namespace Jer.Services;

/// <summary>A short overlay: volume, Caps Lock, charging, network, keyboard language.</summary>
public sealed record HudInfo(string Kind, Geometry Icon, string Title, string? Detail, double? Level, Brush Accent);

/// <summary>A button on an alert.</summary>
public sealed class AlertAction
{
    public AlertAction(string label, Action? run = null, bool isPrimary = false)
    {
        Label = label;
        Run = run;
        IsPrimary = isPrimary;
    }

    public string Label { get; }
    public Action? Run { get; }
    public bool IsPrimary { get; }

    /// <summary>Set by the notch when the alert is shown: runs the action and dismisses the alert.</summary>
    public ICommand? Command { get; internal set; }
}

/// <summary>A message that waits for the user: reminders, finished timers, welcome.</summary>
public sealed class AlertInfo
{
    public required Geometry Icon { get; init; }
    public required string Title { get; init; }
    public string? Message { get; init; }
    public required Brush Accent { get; init; }
    public IReadOnlyList<AlertAction> Actions { get; init; } = Array.Empty<AlertAction>();

    /// <summary>Dismiss automatically after this long. Null keeps the alert until the user answers.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Runs when the alert closes without a button (timeout or replaced).</summary>
    public Action? OnDismissed { get; init; }
}

/// <summary>The one place services and pages use to talk to the notch.</summary>
public sealed class NotchEvents
{
    public event Action<HudInfo>? HudRequested;
    public event Action<AlertInfo>? AlertRequested;

    public void ShowHud(HudInfo hud) => HudRequested?.Invoke(hud);

    public void ShowAlert(AlertInfo alert) => AlertRequested?.Invoke(alert);
}
