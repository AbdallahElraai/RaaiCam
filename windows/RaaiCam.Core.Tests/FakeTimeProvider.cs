namespace RaaiCam.Core.Tests;

/// <summary>Manual-time TimeProvider: timers fire only when Advance is called.</summary>
sealed class FakeTimeProvider : TimeProvider
{
    readonly object _gate = new();
    readonly List<FakeTimer> _timers = new();
    readonly SemaphoreSlim _created = new(0);
    long _nowTicks;

    public override long GetTimestamp() => Interlocked.Read(ref _nowTicks);
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var t = new FakeTimer(this, callback, state);
        lock (_gate) _timers.Add(t);
        t.Change(dueTime, period);
        return t;
    }

    bool HasPending(TimeSpan due)
    {
        lock (_gate)
            return _timers.Any(t => t.DueAt is { } d && d - _nowTicks == due.Ticks);
    }

    /// <summary>Completes once a live timer is armed to fire exactly <paramref name="due"/> from now.</summary>
    public async Task WaitForTimerAsync(TimeSpan due)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!HasPending(due))
            await _created.WaitAsync(cts.Token);
    }

    public void Advance(TimeSpan by)
    {
        List<FakeTimer> due;
        lock (_gate)
        {
            _nowTicks += by.Ticks;
            due = _timers.Where(t => t.DueAt is { } d && d <= _nowTicks).ToList();
            foreach (var t in due) t.DueAt = null;
        }
        foreach (var t in due) t.Fire();
    }

    sealed class FakeTimer(FakeTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public long? DueAt;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner._nowTicks + dueTime.Ticks;
            owner._created.Release();
            return true;
        }
        public void Fire() => callback(state);
        public void Dispose() { lock (owner._gate) { DueAt = null; owner._timers.Remove(this); } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
