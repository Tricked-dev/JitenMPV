using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using JitenMPV.App.Platform;
using JitenMPV.Core.Interaction;

namespace JitenMPV.App.Popup;

/// <summary>
/// Selects a geometry provider and surface adapter from their probed capabilities. The presenter
/// does not know about XIDs, Plasma protocols, screen scaling or Wayland positioning limitations.
/// </summary>
internal sealed class PopupBackendCoordinator : IAsyncDisposable
{
    private readonly PlasmaPopupSurfaceBackend _plasmaSurface;
    private readonly KWinMpvWindowGeometryProvider _kwinGeometry;
    private readonly X11MpvWindowGeometryProvider _x11Geometry = new();
    private readonly X11PopupSurfaceBackend _x11Surface = new();
    private readonly GenericPopupSurfaceBackend _genericSurface = new();

    private IPopupSurfaceBackend _surface;
    private IMpvWindowGeometryProvider? _geometry;

    public PopupBackendCoordinator()
    {
        var plasmaConnections = new PlasmaWaylandConnectionStore();
        _plasmaSurface = new PlasmaPopupSurfaceBackend(plasmaConnections);
        _kwinGeometry = new KWinMpvWindowGeometryProvider(plasmaConnections);
        _surface = _genericSurface;
        _kwinGeometry.GeometryChanged += () => GeometryChanged?.Invoke();
    }

    public event Action? GeometryChanged;

    public PopupBackendCapabilities Capabilities =>
        _surface.Capabilities
        | (_geometry?.Status == GeometryProviderStatus.Ready
            ? PopupBackendCapabilities.WindowGeometry
            : PopupBackendCapabilities.None);

    public PopupSupportLevel SupportLevel { get; private set; } =
        PopupSupportLevel.Unknown;

    public bool UsesNativeWayland { get; private set; }

    // Until Avalonia exposes wl_surface destruction as a supported lifecycle hook, closing is the
    // only safe way to guarantee Plasma metadata dies before the surface it decorates.
    public bool RequiresWindowRecreationAfterHide =>
        UsesNativeWayland && ReferenceEquals(_surface, _plasmaSurface);

    public async ValueTask PrepareAsync(
        Window window,
        PopupWindowContext context,
        CancellationToken ct)
    {
        UsesNativeWayland = WaylandSurfaceInterop.IsNativeWayland(window);
        _x11Surface.UpdateContext(context);

        if (UsesNativeWayland)
        {
            await _plasmaSurface.PrepareAsync(window, ct);
            if (_plasmaSurface.IsSupported
                && _kwinGeometry.Status == GeometryProviderStatus.Ready)
            {
                _surface = _plasmaSurface;
                _geometry = _kwinGeometry;
                SupportLevel = PopupSupportLevel.Full;
                return;
            }

            _surface = _genericSurface;
            _geometry = null;
            SupportLevel = PopupSupportLevel.Approximate;
            return;
        }

        if (context.Backend == MpvWindowBackend.X11
            || context.WindowId is > 0)
        {
            _surface = _x11Surface;
            _geometry = _x11Geometry;
            SupportLevel = PopupSupportLevel.Full;
            await _surface.PrepareAsync(window, ct);
            return;
        }

        _surface = _genericSurface;
        _geometry = null;
        SupportLevel = PopupSupportLevel.Approximate;
        await _surface.PrepareAsync(window, ct);
    }

    public async ValueTask PositionAsync(
        Window window,
        PopupWindowContext context,
        PopupPointerPosition pointer,
        PopupPlacementRequest request,
        LogicalSize logicalPopupSize,
        IPopupPositionCalculator calculator,
        CancellationToken ct)
    {
        var discovered = _geometry is null
            ? null
            : await _geometry.GetGeometryAsync(context, ct);

        var screen = SelectScreen(window, context, discovered);
        if (screen is null)
            return;

        var output = NormalizeOutput(screen, context);
        var geometry = discovered is null
            ? FallbackGeometry(context, output)
            : discovered with
            {
                Output = output,
                Scale = output.Scale
            };

        var globalPointer = ResolvePointer(
            context,
            new SurfacePoint(pointer.X, pointer.Y),
            geometry,
            output,
            UsesNativeWayland,
            discovered is not null);
        var popupSize = UsesNativeWayland
            ? logicalPopupSize
            : new LogicalSize(
                logicalPopupSize.Width * output.Scale,
                logicalPopupSize.Height * output.Scale);

        var placement = calculator.Calculate(
            request with { Pointer = globalPointer },
            geometry,
            popupSize);
        await _surface.SetPositionAsync(
            window, placement.Position, popupSize, placement.Output, ct);
    }

    public ValueTask DetachAsync(Window window, CancellationToken ct) =>
        _surface.DetachAsync(window, ct);

    public async ValueTask DisposeAsync()
    {
        await _kwinGeometry.DisposeAsync();
        await _plasmaSurface.DisposeAsync();
        await _x11Geometry.DisposeAsync();
        await _x11Surface.DisposeAsync();
        await _genericSurface.DisposeAsync();
    }

    private static Screen? SelectScreen(
        Window window,
        PopupWindowContext context,
        MpvWindowGeometry? geometry)
    {
        var byName = window.Screens.All.FirstOrDefault(screen =>
            screen.DisplayName is { } name
            && context.DisplayNames.Any(display =>
                string.Equals(display, name, StringComparison.OrdinalIgnoreCase)));
        if (byName is not null)
            return byName;

        if (geometry is not null)
        {
            var center = new PixelPoint(
                checked((int)Math.Round(
                    geometry.ClientOrigin.X + geometry.ClientSize.Width / 2)),
                checked((int)Math.Round(
                    geometry.ClientOrigin.Y + geometry.ClientSize.Height / 2)));
            var byGeometry = window.Screens.ScreenFromPoint(center);
            if (byGeometry is not null)
                return byGeometry;
        }

        return window.Screens.Primary;
    }

    private static OutputInfo NormalizeOutput(
        Screen screen,
        PopupWindowContext context)
    {
        var bounds = screen.Bounds;
        var workingArea = screen.WorkingArea;
        return new OutputInfo(
            context.DisplayNames.FirstOrDefault(display =>
                string.Equals(
                    display,
                    screen.DisplayName,
                    StringComparison.OrdinalIgnoreCase))
            ?? screen.DisplayName,
            screen.DisplayName,
            new LogicalRect(bounds.X, bounds.Y, bounds.Width, bounds.Height),
            new LogicalRect(
                workingArea.X, workingArea.Y,
                workingArea.Width, workingArea.Height),
            screen.Scaling,
            null);
    }

    private static MpvWindowGeometry FallbackGeometry(
        PopupWindowContext context,
        OutputInfo output) =>
        new(
            new GlobalLogicalPoint(output.Bounds.X, output.Bounds.Y),
            new LogicalSize(output.Bounds.Width, output.Bounds.Height),
            output,
            context.IsFullscreen,
            output.Scale);

    private static GlobalLogicalPoint? ResolvePointer(
        PopupWindowContext context,
        SurfacePoint pointer,
        MpvWindowGeometry geometry,
        OutputInfo output,
        bool nativeWayland,
        bool hasDiscoveredGeometry)
    {
        if ((context.Backend == MpvWindowBackend.X11
             || context.WindowId is > 0)
            && hasDiscoveredGeometry)
        {
            return new GlobalLogicalPoint(
                geometry.ClientOrigin.X + pointer.X,
                geometry.ClientOrigin.Y + pointer.Y);
        }

        if (nativeWayland && hasDiscoveredGeometry)
        {
            return new GlobalLogicalPoint(
                geometry.ClientOrigin.X + pointer.X,
                geometry.ClientOrigin.Y + pointer.Y);
        }

        if (!OperatingSystem.IsLinux()
            && CursorPositionHelper.GetCursorPosition() is { } cursor)
            return new GlobalLogicalPoint(cursor.X, cursor.Y);

        // A fullscreen Wayland surface covers its output exactly, even without foreign-toplevel
        // geometry. Windowed fallback intentionally has no pretend global pointer.
        if (nativeWayland && context.IsFullscreen)
            return new GlobalLogicalPoint(
                output.Bounds.X + pointer.X,
                output.Bounds.Y + pointer.Y);

        return null;
    }

    private sealed class GenericPopupSurfaceBackend : IPopupSurfaceBackend
    {
        public PopupBackendCapabilities Capabilities =>
            PopupBackendCapabilities.NonActivating
            | PopupBackendCapabilities.Interactive;

        public ValueTask PrepareAsync(Window window, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask SetPositionAsync(
            Window window,
            GlobalLogicalPoint position,
            LogicalSize size,
            OutputInfo output,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!WaylandSurfaceInterop.IsNativeWayland(window))
            {
                window.Position = new PixelPoint(
                    checked((int)Math.Round(position.X)),
                    checked((int)Math.Round(position.Y)));
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask DetachAsync(Window window, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
