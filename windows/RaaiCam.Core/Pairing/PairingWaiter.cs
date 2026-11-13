namespace RaaiCam.Core.Pairing;

/// <summary>
/// Waits for a phone to complete a QR pairing, trying several routes at once (USB and Wi-Fi). Each route is a function that
/// makes ONE attempt (connect, handshake in pairing mode) and returns the stored phone on success, or null / throws on
/// failure; failed attempts are retried after <see cref="RetryDelay"/> until the session expires. The first success wins and
/// cancels the other routes. A single-use session token guarantees at most one route can actually pair.
/// </summary>
public static class PairingWaiter
{
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    /// <returns>The paired phone, or null when the session expired (or all routes ended) without a pairing.</returns>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    public static async Task<PairedPhone?> WaitAsync(
        PairingSession session,
        IReadOnlyList<Func<CancellationToken, Task<PairedPhone?>>> routes,
        TimeProvider time,
        CancellationToken ct,
        Action<string>? onRouteFailure = null)
    {
        var remaining = session.ExpiresAt - time.GetUtcNow();
        if (remaining <= TimeSpan.Zero || session.IsExpired) return null;

        using var expiry = new CancellationTokenSource(remaining, time);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, expiry.Token);
        var token = stop.Token;
        var tasks = routes.Select(r => RunRouteAsync(r, time, token, onRouteFailure)).ToList();

        PairedPhone? winner = null;
        try
        {
            while (tasks.Count > 0)
            {
                var done = await Task.WhenAny(tasks).ConfigureAwait(false);
                tasks.Remove(done);
                if (done.IsCompletedSuccessfully && done.Result is { } phone) { winner = phone; break; }
            }
        }
        finally
        {
            stop.Cancel();
            try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch (Exception) { }
        }
        ct.ThrowIfCancellationRequested();
        return winner;
    }

    static async Task<PairedPhone?> RunRouteAsync(Func<CancellationToken, Task<PairedPhone?>> route, TimeProvider time,
        CancellationToken ct, Action<string>? onFailure)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (await route(ct).ConfigureAwait(false) is { } phone) return phone;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
            catch (Exception e)
            {
                try { onFailure?.Invoke(e.GetType().Name); } catch (Exception) { }
            }
            try { await Task.Delay(RetryDelay, time, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return null; }
        }
        return null;
    }
}
