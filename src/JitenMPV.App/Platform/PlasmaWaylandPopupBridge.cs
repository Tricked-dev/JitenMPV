using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using NWayland.Protocols.Plasma.PlasmaShell;
using NWayland.Protocols.Wayland;

namespace JitenMPV.App.Platform;

/// <summary>
/// Supplies the two operations that the native Avalonia Wayland backend deliberately does not:
/// global positioning and an above-fullscreen popup role. The public Avalonia surface API stops at
/// the xdg_toplevel abstraction, so this narrowly-contained adapter reaches the worker-owned
/// wl_surface and attaches Plasma's metadata protocol to it.
/// </summary>
internal sealed class PlasmaWaylandPopupBridge
{
    private static readonly BindingFlags InstanceFlags =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    // Plasma allows org_kde_plasma_shell to be bound only once per Wayland connection.
    private readonly ConditionalWeakTable<object, ConnectionState> _connections = new();
    private readonly ConditionalWeakTable<WlSurface, SurfaceState> _surfaces = new();
    private bool _reportedFailure;

    public bool IsNativeWayland(Window window) =>
        window.PlatformImpl?.GetType().Assembly.GetName().Name == "Avalonia.Wayland";

    public async Task<bool> TrySetPositionAsync(Window window, PixelPoint position)
    {
        if (!TryGetWorkerContext(window, createSurfaceIfMissing: true, out var context))
            return false;

        var positioned = false;
        try
        {
            await context.InvokeAsync(() =>
            {
                if (!TryGetSurface(context.SurfaceProxy, out var globals, out var wlSurface))
                    return;

                var connection = _connections.GetValue(globals, CreateConnection);
                if (connection.Shell is null)
                    return;

                var state = _surfaces.GetValue(wlSurface,
                    surface => CreateSurface(connection.Shell, surface));
                state.Surface.SetPosition(position.X, position.Y);
                // Plasma surface state is synchronized with wl_surface state. Without a commit,
                // KWin keeps using the previous position until Avalonia happens to draw another
                // frame, which makes a visible popup appear stuck at its initial location.
                wlSurface.Commit();
                positioned = true;
            });
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
        }

        return positioned;
    }

    public async Task DetachAsync(Window window)
    {
        if (!TryGetWorkerContext(window, createSurfaceIfMissing: false, out var context))
            return;

        try
        {
            await context.InvokeAsync(() =>
            {
                if (!TryGetSurface(context.SurfaceProxy, out _, out var wlSurface)
                    || !_surfaces.TryGetValue(wlSurface, out var state))
                    return;

                state.Surface.Destroy();
                _surfaces.Remove(wlSurface);
            });
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
        }
    }

    private ConnectionState CreateConnection(object globals)
    {
        var bindMethod = globals.GetType()
            .GetMethods(InstanceFlags)
            .Single(method => method.Name == "Bind"
                              && method.IsGenericMethodDefinition
                              && method.GetParameters().Length == 3);

        var shell = (OrgKdePlasmaShell?)bindMethod
            .MakeGenericMethod(typeof(OrgKdePlasmaShell))
            .Invoke(globals, [1u, 8u, null]);
        return new ConnectionState(shell);
    }

    private static SurfaceState CreateSurface(OrgKdePlasmaShell shell, WlSurface wlSurface)
    {
        var surface = shell.GetSurface(wlSurface);
        surface.SetRole((uint)OrgKdePlasmaSurface.RoleEnum.Onscreendisplay);
        if (surface.IsSetSkipTaskbarAvailable)
            surface.SetSkipTaskbar(1);
        if (surface.IsSetSkipSwitcherAvailable)
            surface.SetSkipSwitcher(1);

        // Plasma shell roles are deliberately non-activating by default. Keep that default:
        // allowing this surface to take focus deactivates fullscreen mpv and reveals Plasma's
        // panel. Pointer events still reach the popup without transferring keyboard focus.
        return new SurfaceState(surface);
    }

    private static bool TryGetWorkerContext(
        Window window, bool createSurfaceIfMissing, out WorkerContext context)
    {
        context = default;
        var impl = window.PlatformImpl;
        if (impl?.GetType().Assembly.GetName().Name != "Avalonia.Wayland")
            return false;

        var surfaceProxy = GetSurfaceProxy(impl);
        if (surfaceProxy is null && createSurfaceIfMissing)
        {
            // Avalonia normally creates the worker surface from its public Window.Show(). Calling
            // the platform half first creates the still-unmapped xdg_toplevel, giving us the one
            // lifecycle point where Plasma metadata can be attached before the first buffer maps.
            var showMethod = impl.GetType().GetMethod(
                "Show", InstanceFlags, null, [typeof(bool), typeof(bool)], null);
            showMethod?.Invoke(impl, [false, false]);
            surfaceProxy = GetSurfaceProxy(impl);
        }

        var client = FindProperty(impl.GetType(), "Client")?.GetValue(impl);
        var invokeMethod = client?.GetType()
            .GetMethods(InstanceFlags)
            .SingleOrDefault(method => method.Name == "InvokeOobAsync"
                                       && method.IsGenericMethodDefinition
                                       && method.GetGenericArguments().Length == 1
                                       && method.GetParameters().Length == 1);

        if (surfaceProxy is null || client is null || invokeMethod is null)
            return false;

        var closedInvoke = invokeMethod.MakeGenericMethod(typeof(bool));
        context = new WorkerContext(surfaceProxy, action =>
        {
            Func<bool> callback = () =>
            {
                action();
                return true;
            };
            return (Task)(closedInvoke.Invoke(client, [callback])
                          ?? throw new InvalidOperationException(
                              "Avalonia Wayland worker returned no task."));
        });
        return true;
    }

    private static object? GetSurfaceProxy(object impl) =>
        FindProperty(impl.GetType(), "SurfaceProxy")?.GetValue(impl)
        ?? FindField(impl.GetType(), "_surfaceProxy")?.GetValue(impl);

    private static bool TryGetSurface(
        object surfaceProxy, out object globals, out WlSurface wlSurface)
    {
        globals = null!;
        wlSurface = null!;

        var target = FindProperty(surfaceProxy.GetType(), "ProxyTarget")
            ?.GetValue(surfaceProxy);
        if (target is null)
            return false;

        globals = FindProperty(target.GetType(), "Globals")?.GetValue(target)!;
        wlSurface = FindProperty(target.GetType(), "WlSurface")?.GetValue(target) as WlSurface
                    ?? null!;
        return globals is not null && wlSurface is not null;
    }

    private static FieldInfo? FindField(Type? type, string name)
    {
        while (type is not null)
        {
            var field = type.GetField(name, InstanceFlags | BindingFlags.DeclaredOnly);
            if (field is not null)
                return field;
            type = type.BaseType;
        }
        return null;
    }

    private static PropertyInfo? FindProperty(Type? type, string name)
    {
        while (type is not null)
        {
            var property = type.GetProperty(name, InstanceFlags | BindingFlags.DeclaredOnly);
            if (property is not null)
                return property;
            type = type.BaseType;
        }
        return null;
    }

    private void ReportFailure(Exception exception)
    {
        if (_reportedFailure)
            return;
        _reportedFailure = true;
        Trace.TraceWarning(
            "Plasma Wayland popup integration is unavailable: {0}",
            exception.GetBaseException().Message);
    }

    private sealed record ConnectionState(OrgKdePlasmaShell? Shell);
    private sealed record SurfaceState(OrgKdePlasmaSurface Surface);
    private readonly record struct WorkerContext(
        object SurfaceProxy,
        Func<Action, Task> InvokeAsync);
}
