using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using JitenMPV.App.Platform;
using JitenMPV.Core.Config;
using JitenMPV.Core.Interaction;
using JitenMPV.Core.Rendering;

namespace JitenMPV.App.Overlay;

/// Draws the subtitle into a transparent window of this process, positioned over mpv's client
/// area. Everything below the interface runs on the UI thread; the plugin host calls in from its
/// own render loop.
public sealed class AvaloniaSubtitleOverlaySurface : ISubtitleOverlaySurface
{
    private static readonly TimeSpan InitialGeometryWait = TimeSpan.FromMilliseconds(750);

    private readonly SubtitleFontStore _fonts = new();
    private readonly SubtitleLayoutEngine _engine;
    private readonly SubtitleOverlayLifecycle _lifecycle = new();
    private readonly SemaphoreSlim _geometryApplyLock = new(1, 1);

    private volatile PluginSettings? _settings;
    private volatile PopupWindowContext _context = PopupWindowContext.Empty;
    private volatile int _osdWidth;
    private volatile int _osdHeight;
    private volatile bool _unavailable;

    private SubtitleOverlayWindow? _window;
    private SubtitleOverlayControl? _control;
    private IMpvOverlayBackend? _backend;
    private (int? ProcessId, long? WindowId)? _preparedFor;
    private TaskCompletionSource _geometryChanged = NewGeometrySignal();

    public AvaloniaSubtitleOverlaySurface()
    {
        _engine = new SubtitleLayoutEngine(_fonts);
    }

    public bool IsAvailable => !_unavailable;

    public event Action? AvailabilityChanged;

    public Task<IReadOnlyList<WordRect>?> ShowAsync(SubtitleFrame frame, CancellationToken ct)
    {
        long revision = _lifecycle.BeginFrame(frame);
        return Dispatcher.UIThread.InvokeAsync(() => ShowCoreAsync(frame, revision, ct));
    }

    public Task ClearAsync(CancellationToken ct)
    {
        _lifecycle.Clear();
        return Dispatcher.UIThread.InvokeAsync(HideFrame).GetTask();
    }

    public void UpdateSettings(PluginSettings settings)
    {
        var previous = _settings;
        _settings = settings;

        if (previous?.ExperimentalAvaloniaRenderer == false
            && settings.ExperimentalAvaloniaRenderer)
            _unavailable = false;

        if (previous?.ExperimentalAvaloniaRenderer == true && !settings.ExperimentalAvaloniaRenderer)
            _ = Dispatcher.UIThread.InvokeAsync(TearDownAsync);
    }

    public void UpdateOsd(int width, int height)
    {
        _osdWidth = width;
        _osdHeight = height;
    }

    public void UpdateWindowContext(PopupWindowContext context) => _context = context;

    public Task ShutdownAsync() => Dispatcher.UIThread.InvokeAsync(TearDownAsync);

    private async Task<IReadOnlyList<WordRect>?> ShowCoreAsync(
        SubtitleFrame frame, long revision, CancellationToken ct)
    {
        var settings = _settings;
        if (settings is null || _unavailable) return null;
        if (!_lifecycle.Owns(revision, ct)) return [];

        int osdWidth = _osdWidth;
        int osdHeight = _osdHeight;
        if (osdWidth <= 0 || osdHeight <= 0) return null;

        var context = _context;
        var window = EnsureWindow();
        var backend = await EnsureBackendAsync(window, context, ct);
        if (!backend.IsSupported)
        {
            _unavailable = true;
            HideFrame();
            return null;
        }

        if (await WaitForGeometryAsync(backend, ct) is not { } geometry)
        {
            HideFrame();
            return null;
        }

        var size = await ApplyGeometryAsync(backend, window, geometry, ct);
        if (size.Height <= 0) return null;

        // A slower frame must never overwrite a newer one, and the marshal to this thread is where
        // the two can cross.
        if (!_lifecycle.Owns(revision, ct)) return [];

        var laidOut = _engine.Layout(frame, settings, osdWidth, osdHeight, size.Height);
        if (laidOut is null) return null;
        if (!_lifecycle.Owns(revision, ct)) return [];

        Present(laidOut);
        if (_control?.LeaseUnavailable == true)
        {
            _unavailable = true;
            HideFrame();
            return null;
        }

        return laidOut.Rects;
    }

    private SubtitleOverlayWindow EnsureWindow()
    {
        if (_window is not null) return _window;

        _control = new SubtitleOverlayControl(_fonts);
        _window = new SubtitleOverlayWindow { Content = _control };

        // The platform handle every backend configures only exists once the window has been mapped.
        _window.Show();
        return _window;
    }

    /// Preparation attaches hooks and window-manager state, so it is redone only when mpv's window
    /// identity changes rather than once per subtitle line.
    private async ValueTask<IMpvOverlayBackend> EnsureBackendAsync(
        Window window, PopupWindowContext context, CancellationToken ct)
    {
        var key = (context.ProcessId, context.WindowId);
        if (_backend is { } existing)
        {
            if (_preparedFor != key)
            {
                _preparedFor = key;
                await existing.PrepareAsync(window, context, ct);
            }

            return existing;
        }

        _preparedFor = key;
        var backend = await CreateBackendAsync(window, context, ct);
        _backend = backend;
        backend.GeometryChanged += OnBackendGeometryChanged;
        return backend;
    }

    private static async ValueTask<IMpvOverlayBackend> CreateBackendAsync(
        Window window, PopupWindowContext context, CancellationToken ct)
    {
        IMpvOverlayBackend backend = OperatingSystem.IsWindows()
            ? new WindowsOverlayBackend()
            : OperatingSystem.IsMacOS()
                ? new MacOsOverlayBackend()
                : OperatingSystem.IsLinux()
                    ? LinuxBackend(window, context)
                    : new UnsupportedOverlayBackend();

        await backend.PrepareAsync(window, context, ct);
        if (backend.IsSupported) return backend;

        await backend.DisposeAsync();
        return new UnsupportedOverlayBackend();
    }

    /// Every other native-Wayland compositor is left to mpv: without foreign-window geometry the
    /// overlay could only be placed by guessing, and a subtitle in the wrong place is worse than
    /// one drawn by mpv.
    private static IMpvOverlayBackend LinuxBackend(Window window, PopupWindowContext context)
    {
        if (WaylandSurfaceInterop.IsNativeWayland(window))
            return new PlasmaOverlayBackend();

        return context.Backend == MpvWindowBackend.X11 || context.WindowId is > 0
            ? new X11OverlayBackend()
            : new UnsupportedOverlayBackend();
    }

    private async Task<OverlayGeometry?> WaitForGeometryAsync(
        IMpvOverlayBackend backend, CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            var signal = Volatile.Read(ref _geometryChanged).Task;
            if (backend.TryGetGeometry(_context) is { } geometry) return geometry;

            var remaining = InitialGeometryWait - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) return null;

            try
            {
                await signal.WaitAsync(remaining, ct);
            }
            catch (TimeoutException)
            {
                return null;
            }
        }
    }

    /// The trackers report far more often than mpv's window actually moves, and a re-apply costs a
    /// round trip to the window manager on every platform.
    private async ValueTask<OverlayLogicalSize> ApplyGeometryAsync(
        IMpvOverlayBackend backend, Window window, OverlayGeometry geometry, CancellationToken ct)
    {
        double scaling = window.RenderScaling;
        var decision = _lifecycle.ObserveGeometry(geometry, scaling);
        if (!decision.Apply) return _lifecycle.AppliedSize;

        await _geometryApplyLock.WaitAsync(ct);
        try
        {
            if (!_lifecycle.IsGeometryCurrent(
                    geometry, scaling, decision.Generation))
                return default;

            var size = await backend.ApplyAsync(window, geometry, ct);
            return _lifecycle.TryMarkApplied(
                geometry, scaling, size, decision.Generation)
                ? size
                : default;
        }
        finally
        {
            _geometryApplyLock.Release();
        }
    }

    private void OnBackendGeometryChanged()
    {
        SignalGeometryChanged();
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => _ = RefreshGeometryAsync());
            return;
        }

        _ = RefreshGeometryAsync();
    }

    private async Task RefreshGeometryAsync()
    {
        if (_window is not { } window || _backend is not { } backend) return;

        var geometry = backend.TryGetGeometry(_context);
        var decision = _lifecycle.ObserveGeometry(geometry, window.RenderScaling);
        if (geometry is null)
        {
            if (decision.Hide)
                HideFrame();
            if (decision.NotifyAvailability)
                AvailabilityChanged?.Invoke();
            return;
        }

        if (decision.Apply)
        {
            try
            {
                await _geometryApplyLock.WaitAsync();
                try
                {
                    if (!_lifecycle.IsGeometryCurrent(
                            geometry.Value, window.RenderScaling,
                            decision.Generation))
                        return;

                    var size = await backend.ApplyAsync(
                        window, geometry.Value, CancellationToken.None);
                    if (!_lifecycle.TryMarkApplied(
                            geometry.Value, window.RenderScaling, size,
                            decision.Generation))
                        return;
                }
                finally
                {
                    _geometryApplyLock.Release();
                }
            }
            catch (Exception ex)
            {
                Trace.TraceWarning(
                    "Native subtitle overlay geometry refresh failed: {0}",
                    ex.GetBaseException().Message);
                HideFrame();
                return;
            }
        }

        if (decision.ReplayDesiredFrame)
            PresentDesiredFrame();
        if (decision.NotifyAvailability)
            AvailabilityChanged?.Invoke();
    }

    private void PresentDesiredFrame()
    {
        if (_lifecycle.DesiredFrame is not { } frame
            || _settings is not { } settings
            || _lifecycle.AppliedSize.Height <= 0
            || _osdWidth <= 0
            || _osdHeight <= 0)
            return;

        if (_engine.Layout(
                frame, settings, _osdWidth, _osdHeight,
                _lifecycle.AppliedSize.Height) is { } laidOut)
            Present(laidOut);
    }

    private void Present(LaidOutSubtitle frame)
    {
        _control!.SetFrame(frame);
        _window!.Opacity = 1;
    }

    private void HideFrame()
    {
        if (_window is not null) _window.Opacity = 0;
        _control?.SetFrame(null);
    }

    private void SignalGeometryChanged()
    {
        var next = NewGeometrySignal();
        Interlocked.Exchange(ref _geometryChanged, next).TrySetResult();
    }

    private static TaskCompletionSource NewGeometrySignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task TearDownAsync()
    {
        _lifecycle.Clear();
        _preparedFor = null;
        _lifecycle.ResetGeometry();
        _unavailable = false;
        SignalGeometryChanged();

        if (_backend is { } backend)
        {
            backend.GeometryChanged -= OnBackendGeometryChanged;
            await backend.DisposeAsync();
            _backend = null;
        }

        HideFrame();
        _control?.DisposePainter();
        _control = null;

        _window?.Close();
        _window = null;

        _fonts.Dispose();
    }
}
