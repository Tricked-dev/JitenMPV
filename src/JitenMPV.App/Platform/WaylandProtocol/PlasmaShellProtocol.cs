using NWayland;
using NWayland.Interop;
using NWayland.Protocols.Wayland;

namespace JitenMPV.App.Platform.WaylandProtocol;

/// <summary>
/// Minimal NWayland client binding for the Plasma shell metadata protocol. Keeping only this
/// two-interface subset avoids shipping the entire private Plasma protocol bundle.
/// </summary>
internal sealed class PlasmaShellProxy : WlProxy, IWlProxyTypeDescriptorProvider
{
    private static readonly IWlEventsListener IgnoredSurfaceEvents =
        new IgnorePlasmaSurfaceEvents();

    public static WlProxyTypeDescriptor ProxyType { get; } = new(
        WlInterfaceDescription.Create("org_kde_plasma_shell", 8)
            .AddMethod(WlMessageDescription.Create("get_surface")
                .Add(WlMessageArgumentDescription.NewId(PlasmaSurfaceProxy.ProxyType))
                .Add(WlMessageArgumentDescription.Object(WlSurface.ProxyType))
                .Build())
            .Build(),
        typeof(PlasmaShellProxy),
        context => new PlasmaShellProxy(context));

    private PlasmaShellProxy(WlProxyCreationContext context) : base(context)
    {
    }

    public PlasmaSurfaceProxy GetSurface(WlSurface surface)
    {
        using var call = WaylandCallBuilder.Create(this, 0);
        call.ArgNewId();
        call.Arg(surface);
        return call.InvokeNewId<PlasmaSurfaceProxy>(IgnoredSurfaceEvents, Queue);
    }

    private sealed class IgnorePlasmaSurfaceEvents : IWlEventsListener
    {
        void IWlEventsListener.DispatchEvent(WlEventArgs arguments)
        {
            // The two panel auto-hide notifications do not apply to an OSD-role surface.
        }
    }
}

internal sealed class PlasmaSurfaceProxy : WlProxy, IWlProxyTypeDescriptorProvider
{
    public enum Role : uint
    {
        OnScreenDisplay = 3
    }

    public static WlProxyTypeDescriptor ProxyType { get; } = new(
        WlInterfaceDescription.Create("org_kde_plasma_surface", 8)
            .AddMethod(WlMessageDescription.Create("destroy")
                .IsDestructor()
                .Build())
            .AddMethod(WlMessageDescription.Create("set_output")
                .Add(WlMessageArgumentDescription.Object(WlOutput.ProxyType))
                .Build())
            .AddMethod(WlMessageDescription.Create("set_position")
                .Add(WlMessageArgumentDescription.Int32)
                .Add(WlMessageArgumentDescription.Int32)
                .Build())
            .AddMethod(WlMessageDescription.Create("set_role")
                .Add(WlMessageArgumentDescription.UInt32)
                .Build())
            .AddMethod(WlMessageDescription.Create("set_panel_behavior")
                .Add(WlMessageArgumentDescription.UInt32)
                .Build())
            .AddMethod(WlMessageDescription.Create("set_skip_taskbar")
                .SinceVersion(2)
                .Add(WlMessageArgumentDescription.UInt32)
                .Build())
            .AddMethod(WlMessageDescription.Create("panel_auto_hide_hide")
                .SinceVersion(4)
                .Build())
            .AddMethod(WlMessageDescription.Create("panel_auto_hide_show")
                .SinceVersion(4)
                .Build())
            .AddMethod(WlMessageDescription.Create("set_panel_takes_focus")
                .SinceVersion(4)
                .Add(WlMessageArgumentDescription.UInt32)
                .Build())
            .AddMethod(WlMessageDescription.Create("set_skip_switcher")
                .SinceVersion(5)
                .Add(WlMessageArgumentDescription.UInt32)
                .Build())
            .AddMethod(WlMessageDescription.Create("open_under_cursor")
                .SinceVersion(7)
                .Build())
            .AddEvent(WlMessageDescription.Create("auto_hidden_panel_hidden")
                .SinceVersion(4)
                .Build())
            .AddEvent(WlMessageDescription.Create("auto_hidden_panel_shown")
                .SinceVersion(4)
                .Build())
            .Build(),
        typeof(PlasmaSurfaceProxy),
        context => new PlasmaSurfaceProxy(context));

    private PlasmaSurfaceProxy(WlProxyCreationContext context) : base(context)
    {
    }

    public bool IsSetSkipTaskbarAvailable => Version >= 2;
    public bool IsSetSkipSwitcherAvailable => Version >= 5;

    public void Destroy()
    {
        using var call = WaylandCallBuilder.Create(this, 0);
        call.Invoke();
    }

    public void SetPosition(int x, int y)
    {
        using var call = WaylandCallBuilder.Create(this, 2);
        call.Arg(x);
        call.Arg(y);
        call.Invoke();
    }

    public void SetRole(uint role)
    {
        using var call = WaylandCallBuilder.Create(this, 3);
        call.Arg(role);
        call.Invoke();
    }

    public void SetSkipTaskbar(uint skip)
    {
        using var call = WaylandCallBuilder.Create(this, 5);
        call.Arg(skip);
        call.Invoke();
    }

    public void SetSkipSwitcher(uint skip)
    {
        using var call = WaylandCallBuilder.Create(this, 9);
        call.Arg(skip);
        call.Invoke();
    }
}
