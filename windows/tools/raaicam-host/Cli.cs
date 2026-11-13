using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using RaaiCam.Core.Discovery;
using RaaiCam.Core.Pairing;
using RaaiCam.Core.Phone;
using RaaiCam.Core.Usbmux;

namespace RaaiCam.HostApp;

/// <summary>Exit codes of raaicam-host (scripts rely on these).</summary>
static class ExitCodes
{
    public const int Ok = 0;
    public const int Failed = 1;          // --pair timed out / cancelled
    public const int NoDevice = 2;        // --list-devices: nothing attached
    public const int NotPaired = 3;       // normal run with no paired phone
    public const int StoreError = 4;      // the pairing file could not be read or written
}

/// <summary>Turns on virtual-terminal (ANSI colour) processing for the console, which the QR code needs.</summary>
static class ConsoleVt
{
    const int StdOutputHandle = -11;
    const uint EnableVirtualTerminalProcessing = 0x0004;

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr GetStdHandle(int nStdHandle);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetConsoleMode(IntPtr h, out uint mode);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetConsoleMode(IntPtr h, uint mode);

    public static bool TryEnable()
    {
        try
        {
            var h = GetStdHandle(StdOutputHandle);
            if (h == IntPtr.Zero || h == new IntPtr(-1)) return false;
            if (!GetConsoleMode(h, out var mode)) return false;
            return (mode & EnableVirtualTerminalProcessing) != 0 || SetConsoleMode(h, mode | EnableVirtualTerminalProcessing);
        }
        catch (Exception) { return false; }
    }
}

/// <summary>--pair and --unpair.</summary>
static class HostCli
{
    const ushort PhonePort = 47820;

    static ulong NowUs() => (ulong)(Stopwatch.GetTimestamp() * (1_000_000.0 / Stopwatch.Frequency));

    public static async Task<int> PairAsync(PairingStore store, ILogSink log, CancellationToken ct)
    {
        PcIdentity identity;
        PairedPhone? existing;
        try { identity = store.GetOrCreateIdentity(); existing = store.Phone; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("raaicam-host: cannot read or write the pairing file (" + e.GetType().Name + "). Close other programs using it and try again.");
            return ExitCodes.StoreError;
        }

        var time = TimeProvider.System;
        var session = PairingSession.Begin(identity, Environment.MachineName, time);
        if (existing is not null)
            Console.WriteLine($"Note: this PC is already paired with \"{existing.Label}\". Pairing again replaces it.");

        if (!ConsoleVt.TryEnable())
            Console.WriteLine("Warning: this window may not draw the code correctly. If it looks like garbled text, run this in Windows Terminal.");
        Console.WriteLine();
        Console.Write(PairingQr.ToConsoleString(session.QrUrl)); // the only place the pairing URL ever appears: inside the QR
        Console.WriteLine();
        Console.WriteLine("Scan this with the RaaiCam app on your iPhone (valid 5 minutes).");
        Console.WriteLine("Keep the iPhone unlocked with the RaaiCam app open. Press Ctrl+C to cancel.");

        await using var wifi = new WifiPairingRoute(log, store, identity, session);
        var routes = new Func<CancellationToken, Task<PairedPhone?>>[]
        {
            t => UsbPairAttemptAsync(log, store, identity, session, t),
            wifi.AttemptAsync,
        };

        using var ticker = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tick = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), ticker.Token);
                    var left = session.ExpiresAt - time.GetUtcNow();
                    if (left > TimeSpan.Zero) Console.WriteLine($"Still waiting for the phone ({(int)left.TotalSeconds} s left)...");
                }
            }
            catch (OperationCanceledException) { }
        });

        PairedPhone? phone;
        bool cancelled = false;
        try
        {
            phone = await PairingWaiter.WaitAsync(session, routes, time, ct,
                onRouteFailure: name => log.Write("pc", "pairing attempt failed: " + name));
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            phone = null;
        }
        finally
        {
            ticker.Cancel();
            await tick;
        }

        // A phone may have been stored just as we gave up (race between the handshake finishing and the wait ending).
        if (phone is null)
        {
            try
            {
                if (store.Phone is { } saved && (existing is null || saved.PairedAt > existing.PairedAt)) phone = saved;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        if (phone is null && cancelled)
        {
            Console.WriteLine("Pairing cancelled.");
            return ExitCodes.Failed;
        }
        if (phone is null)
        {
            Console.WriteLine("Pairing timed out: no phone paired within 5 minutes. Run raaicam-host --pair to try again.");
            return ExitCodes.Failed;
        }
        Console.WriteLine($"Paired with {phone.Label}");
        return ExitCodes.Ok;
    }

    /// <summary>One USB pairing attempt: nothing attached or the app not answering counts as a failed attempt.</summary>
    static async Task<PairedPhone?> UsbPairAttemptAsync(ILogSink log, PairingStore store, PcIdentity id, PairingSession session, CancellationToken ct)
    {
        var usb = new UsbmuxClient();
        Stream stream;
        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectCts.CancelAfter(TimeSpan.FromSeconds(10));
            var devices = await usb.ListDevicesAsync(connectCts.Token);
            if (devices.Count == 0) return null;
            stream = await usb.ConnectAsync(devices[0].DeviceId, PhonePort, connectCts.Token);
        }
        return await PairOverAsync(stream, log, store, id, session, ct);
    }

    /// <summary>Runs the mode-2 handshake over an open transport; returns the phone once the session confirmed and stored it, else null.</summary>
    internal static async Task<PairedPhone?> PairOverAsync(Stream transport, ILogSink log, PairingStore store, PcIdentity id,
        PairingSession session, CancellationToken ct)
    {
        PhoneSession ps;
        try { ps = new PhoneSession(transport, NowUs, log, new SecureLinkOptions(id, store, null, session)); }
        catch { await transport.DisposeAsync(); throw; }
        var result = new TaskCompletionSource<PairedPhone?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ps.Paired += p => result.TrySetResult(p);
        ps.Disconnected += _ => result.TrySetResult(null);
        _ = ps.RunAsync(ct);
        try { return await result.Task.WaitAsync(ct); }
        finally { await ps.DisposeAsync(); }
    }

    public static async Task<int> UnpairAsync(PairingStore store, ILogSink log, CancellationToken ct)
    {
        PairedPhone? paired;
        try { paired = store.Phone; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("raaicam-host: cannot read the pairing file (" + e.GetType().Name + "). Close other programs using it and try again.");
            return ExitCodes.StoreError;
        }
        if (paired is null)
        {
            Console.WriteLine("No phone is paired with this PC.");
            return ExitCodes.Ok;
        }

        bool told = false;
        try { told = await TellPhoneToUnpairAsync(store, log, paired, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Console.WriteLine("Cancelled; nothing was changed.");
            return ExitCodes.Failed;
        }
        catch (Exception e)
        {
            // InvalidOperationException (link not confirmed), IOException / SocketException (no route), protocol errors:
            // the phone could not be told; the local pairing is cleared below regardless.
            log.Write("pc", "unpair: could not reach the phone: " + e.GetType().Name);
        }

        try { store.Unpair(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("raaicam-host: could not update the pairing file (" + e.GetType().Name + ").");
            return ExitCodes.StoreError;
        }
        Console.WriteLine(told
            ? "Unpaired on this PC and on the phone."
            : "Unpaired on this PC; the phone was not reachable - unpair it in the app too.");
        return ExitCodes.Ok;
    }

    /// <summary>Within 5 s: connect over USB, authenticate as the paired phone, send unpair. False when that was not possible.</summary>
    static async Task<bool> TellPhoneToUnpairAsync(PairingStore store, ILogSink log, PairedPhone paired, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var usb = new UsbmuxClient();
            var devices = await usb.ListDevicesAsync(limit.Token);
            if (devices.Count == 0) return false;
            var stream = await usb.ConnectAsync(devices[0].DeviceId, PhonePort, limit.Token);
            PhoneLink created;
            try { created = PhoneLinks.Create(stream, log, store, paired); }
            catch { await stream.DisposeAsync(); throw; }
            await using var link = created;
            bool gone = false;
            link.Disconnected += _ => gone = true;
            _ = link.RunAsync(limit.Token);
            while (link.Phone is null)
            {
                if (gone) return false;
                await Task.Delay(50, limit.Token);
            }
            await link.UnpairAsync();
            // Success once the command went out. The phone closes the link itself right after handling it, so a close
            // from here on is expected, not a failure. Short delay so the frame is flushed before we dispose.
            try { await Task.Delay(300, limit.Token); } catch (OperationCanceledException) { }
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false; // our 5 s limit
        }
    }
}

/// <summary>
/// Wi-Fi route of --pair: browses Bonjour WITHOUT an id filter (an unpaired phone only advertises while it shows the pairing
/// screen, and we do not know its id yet) and tries a mode-2 handshake on every address seen.
/// </summary>
sealed class WifiPairingRoute : IAsyncDisposable
{
    readonly ILogSink _log;
    readonly PairingStore _store;
    readonly PcIdentity _id;
    readonly PairingSession _session;
    readonly SettingsStore _settings = new(SettingsStore.DefaultPath);
    readonly ConcurrentDictionary<(IPAddress, int), byte> _candidates = new();
    readonly CancellationTokenSource _cts = new();
    UdpMdnsTransport? _transport;
    Task? _browse;
    int _started;

    public WifiPairingRoute(ILogSink log, PairingStore store, PcIdentity id, PairingSession session)
    {
        _log = log; _store = store; _id = id; _session = session;
    }

    void EnsureBrowsing()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        _transport = new UdpMdnsTransport(_log);
        var discovery = new WifiDiscovery(_transport, _settings, expectedId: null, log: _log);
        _browse = Task.Run(async () =>
        {
            try
            {
                await foreach (var p in discovery.BrowseAsync(_cts.Token))
                    _candidates[(p.Address, p.Port)] = 0;
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { _log.Write("pc", "pairing discovery stopped: " + e.GetType().Name); }
        });
    }

    public async Task<PairedPhone?> AttemptAsync(CancellationToken ct)
    {
        EnsureBrowsing();
        foreach (var (addr, port) in _candidates.Keys.ToArray())
        {
            ct.ThrowIfCancellationRequested();
            var client = new TcpClient(addr.AddressFamily) { NoDelay = true };
            try
            {
                using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    connectCts.CancelAfter(TimeSpan.FromSeconds(5));
                    await client.ConnectAsync(addr, port, connectCts.Token);
                }
                var phone = await HostCli.PairOverAsync(client.GetStream(), _log, _store, _id, _session, ct);
                if (phone is not null)
                {
                    try { _settings.SetLastPhoneIp(addr); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                    return phone;
                }
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _log.Write("pc", "pairing wifi attempt failed: " + e.GetType().Name);
            }
            finally
            {
                client.Dispose(); // also on cancellation; the session has already disposed the stream on the normal path
            }
        }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        if (_browse is not null) { try { await _browse; } catch (Exception) { } }
        if (_transport is not null) await _transport.DisposeAsync();
        _cts.Dispose();
    }
}
