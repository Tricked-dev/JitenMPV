using NWayland;
using NWayland.Interop;
using NWayland.Protocols.Wayland;

namespace JitenMPV.App.Platform.WaylandProtocol;

/// <summary>
/// Read-only subset of Plasma's window-management protocol. KWin publishes each toplevel's PID
/// and absolute client geometry through this interface; no window-management requests are sent.
/// </summary>
internal sealed class PlasmaWindowManagementProxy : WlProxy, IWlProxyTypeDescriptorProvider
{
    public static WlProxyTypeDescriptor ProxyType { get; } = new(
        WlInterfaceDescription.Create("org_kde_plasma_window_management", 19)
            .AddMethod(WlMessageDescription.Create("show_desktop")
                .Add(WlMessageArgumentDescription.UInt32)
                .Build())
            .AddMethod(WlMessageDescription.Create("get_window")
                .Add(WlMessageArgumentDescription.NewId(PlasmaWindowProxy.ProxyType))
                .Add(WlMessageArgumentDescription.UInt32)
                .Build())
            .AddMethod(WlMessageDescription.Create("get_window_by_uuid")
                .SinceVersion(12)
                .Add(WlMessageArgumentDescription.NewId(PlasmaWindowProxy.ProxyType))
                .Add(WlMessageArgumentDescription.String)
                .Build())
            .AddEvent(WlMessageDescription.Create("show_desktop_changed")
                .Add(WlMessageArgumentDescription.UInt32)
                .Build())
            .AddEvent(WlMessageDescription.Create("window")
                .Add(WlMessageArgumentDescription.UInt32)
                .Build())
            .AddEvent(WlMessageDescription.Create("stacking_order_changed")
                .SinceVersion(11)
                .Add(WlMessageArgumentDescription.Array)
                .Build())
            .AddEvent(WlMessageDescription.Create("stacking_order_uuid_changed")
                .SinceVersion(12)
                .Add(WlMessageArgumentDescription.String)
                .Build())
            .AddEvent(WlMessageDescription.Create("window_with_uuid")
                .SinceVersion(13)
                .Add(WlMessageArgumentDescription.UInt32)
                .Add(WlMessageArgumentDescription.String)
                .Build())
            .AddEvent(WlMessageDescription.Create("stacking_order_changed_2")
                .SinceVersion(17)
                .Build())
            .Build(),
        typeof(PlasmaWindowManagementProxy),
        context => new PlasmaWindowManagementProxy(context));

    private PlasmaWindowManagementProxy(WlProxyCreationContext context) : base(context)
    {
    }

    public PlasmaWindowProxy GetWindowByUuid(string uuid, IWlEventsListener listener)
    {
        using var call = WaylandCallBuilder.Create(this, 2);
        call.ArgNewId();
        call.Arg(uuid);
        return call.InvokeNewId<PlasmaWindowProxy>(listener, Queue);
    }
}

internal sealed class PlasmaWindowProxy : WlProxy, IWlProxyTypeDescriptorProvider
{
    public static WlProxyTypeDescriptor ProxyType { get; } = new(
        WlInterfaceDescription.Create("org_kde_plasma_window", 18)
            .AddMethod(WlMessageDescription.Create("set_state")
                .Add(WlMessageArgumentDescription.UInt32)
                .Add(WlMessageArgumentDescription.UInt32)
                .Build())
            .AddMethod(WlMessageDescription.Create("set_virtual_desktop")
                .Add(WlMessageArgumentDescription.UInt32)
                .Build())
            .AddMethod(WlMessageDescription.Create("set_minimized_geometry")
                .Add(WlMessageArgumentDescription.Object(WlSurface.ProxyType))
                .Add(WlMessageArgumentDescription.UInt32)
                .Add(WlMessageArgumentDescription.UInt32)
                .Add(WlMessageArgumentDescription.UInt32)
                .Add(WlMessageArgumentDescription.UInt32)
                .Build())
            .AddMethod(WlMessageDescription.Create("unset_minimized_geometry")
                .Add(WlMessageArgumentDescription.Object(WlSurface.ProxyType))
                .Build())
            .AddMethod(WlMessageDescription.Create("close").Build())
            .AddMethod(WlMessageDescription.Create("request_move").Build())
            .AddMethod(WlMessageDescription.Create("request_resize").Build())
            .AddMethod(WlMessageDescription.Create("destroy")
                .SinceVersion(4)
                .IsDestructor()
                .Build())
            .AddEvent(WlMessageDescription.Create("title_changed")
                .Add(WlMessageArgumentDescription.String)
                .Build())
            .AddEvent(WlMessageDescription.Create("app_id_changed")
                .Add(WlMessageArgumentDescription.String)
                .Build())
            .AddEvent(WlMessageDescription.Create("state_changed")
                .Add(WlMessageArgumentDescription.UInt32)
                .Build())
            .AddEvent(WlMessageDescription.Create("virtual_desktop_changed")
                .Add(WlMessageArgumentDescription.Int32)
                .Build())
            .AddEvent(WlMessageDescription.Create("themed_icon_name_changed")
                .Add(WlMessageArgumentDescription.String)
                .Build())
            .AddEvent(WlMessageDescription.Create("unmapped").Build())
            .AddEvent(WlMessageDescription.Create("initial_state")
                .SinceVersion(4)
                .Build())
            .AddEvent(WlMessageDescription.Create("parent_window")
                .SinceVersion(5)
                // Only the wire type matters here; this read-only client ignores parent changes.
                .Add(WlMessageArgumentDescription.Object(WlSurface.ProxyType).AsNullable())
                .Build())
            .AddEvent(WlMessageDescription.Create("geometry")
                .SinceVersion(6)
                .Add(WlMessageArgumentDescription.Int32)
                .Add(WlMessageArgumentDescription.Int32)
                .Add(WlMessageArgumentDescription.UInt32)
                .Add(WlMessageArgumentDescription.UInt32)
                .Build())
            .AddEvent(WlMessageDescription.Create("icon_changed")
                .SinceVersion(7)
                .Build())
            .AddEvent(WlMessageDescription.Create("pid_changed")
                .SinceVersion(8)
                .Add(WlMessageArgumentDescription.UInt32)
                .Build())
            .AddEvent(WlMessageDescription.Create("virtual_desktop_entered")
                .SinceVersion(9)
                .Add(WlMessageArgumentDescription.String)
                .Build())
            .AddEvent(WlMessageDescription.Create("virtual_desktop_left")
                .SinceVersion(9)
                .Add(WlMessageArgumentDescription.String)
                .Build())
            .AddEvent(WlMessageDescription.Create("application_menu")
                .SinceVersion(10)
                .Add(WlMessageArgumentDescription.String)
                .Add(WlMessageArgumentDescription.String)
                .Build())
            .AddEvent(WlMessageDescription.Create("activity_entered")
                .SinceVersion(11)
                .Add(WlMessageArgumentDescription.String)
                .Build())
            .AddEvent(WlMessageDescription.Create("activity_left")
                .SinceVersion(11)
                .Add(WlMessageArgumentDescription.String)
                .Build())
            .AddEvent(WlMessageDescription.Create("resource_name_changed")
                .SinceVersion(16)
                .Add(WlMessageArgumentDescription.String)
                .Build())
            .AddEvent(WlMessageDescription.Create("client_geometry")
                .SinceVersion(18)
                .Add(WlMessageArgumentDescription.Int32)
                .Add(WlMessageArgumentDescription.Int32)
                .Add(WlMessageArgumentDescription.UInt32)
                .Add(WlMessageArgumentDescription.UInt32)
                .Build())
            .Build(),
        typeof(PlasmaWindowProxy),
        context => new PlasmaWindowProxy(context));

    private PlasmaWindowProxy(WlProxyCreationContext context) : base(context)
    {
    }

    public void Destroy()
    {
        using var call = WaylandCallBuilder.Create(this, 7);
        call.Invoke();
    }
}
