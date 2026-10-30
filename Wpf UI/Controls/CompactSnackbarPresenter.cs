using System.Diagnostics;
using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace LLM_Wiki.Controls;

/// <summary>One visible notice; its own timer can pause without an old timer hiding a later notice.</summary>
public sealed class CompactSnackbarPresenter : SnackbarPresenter
{
    private readonly Queue<Snackbar> _pending = new();
    private readonly DispatcherTimer _timer;
    private double _remainingMs;
    private long _lastTick;

    public CompactSnackbarPresenter()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += OnTick;
    }

    public override void AddToQue(Snackbar snackbar)
    {
        if (Content != null && Content.Title is string oldTitle && snackbar.Title is string title
            && Content.Content is string oldText && snackbar.Content is string text && oldTitle == title && oldText == text)
        { Content.IsShown = false; Display(snackbar); return; }
        if (Content == null) Display(snackbar);
        else
        {
            if (_pending.Count >= 5) _pending.Dequeue();
            _pending.Enqueue(snackbar);
        }
    }

    public override Task ImmediatelyDisplay(Snackbar snackbar)
    {
        _timer.Stop();
        if (Content != null) Content.IsShown = false;
        Display(snackbar);
        return Task.CompletedTask;
    }

    public override Task HideCurrent()
    {
        _timer.Stop();
        if (Content != null) Content.IsShown = false;
        Content = null;
        if (_pending.TryDequeue(out var next)) Display(next);
        return Task.CompletedTask;
    }

    private void Display(Snackbar snackbar)
    {
        Content = snackbar;
        snackbar.IsShown = true;
        _remainingMs = Math.Max(1000, snackbar.Timeout.TotalMilliseconds);
        _lastTick = Stopwatch.GetTimestamp();
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = (now - _lastTick) * 1000d / Stopwatch.Frequency;
        _lastTick = now;
        if (IsMouseOver || IsKeyboardFocusWithin) return;
        _remainingMs -= elapsed;
        if (_remainingMs <= 0) _ = HideCurrent();
    }

    protected override void OnUnloaded()
    {
        _timer.Stop(); _pending.Clear();
        if (Content != null) Content.IsShown = false;
        Content = null;
        base.OnUnloaded();
    }
}
