using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using JitenMPV.Core.Interaction;
using NWayland;
using NWayland.Protocols.Plasma.PlasmaWindowManagement;
using NWayland.Protocols.Wayland;

namespace JitenMPV.App.Platform;

/// <summary>
/// Tracks KWin toplevel client areas on a private Wayland connection. Wayland intentionally gives
/// ordinary clients no global window coordinates; Plasma's task-manager protocol is the compositor
/// API that supplies them. Matching by PID lets mpv remain a native Wayland client.
/// </summary>
internal sealed class PlasmaWindowGeometryTracker
{
    private const string ManagementInterface = "org_kde_plasma_window_management";
    private static readonly TimeSpan InitialGeometryTimeout = TimeSpan.FromMilliseconds(750);

    private readonly object _gate = new();
    private readonly Dictionary<uint, TrackedWindow> _windows = [];
    private readonly TaskCompletionSource<bool> _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _started;
    private bool _reportedFailure;

    public async Task<PixelPoint?> TranslateFromClientAsync(
        int processId, PopupPointerPosition pointer)
    {
        EnsureStarted();

        try
        {
            await _ready.Task.WaitAsync(InitialGeometryTimeout);
        }
        catch (TimeoutException)
        {
            return null;
        }

        lock (_gate)
        {
            foreach (var window in _windows.Values)
            {
                if (window.ProcessId != (uint)processId)
                    continue;

                var geometry = window.ClientGeometry ?? window.FrameGeometry;
                if (geometry is null)
                    continue;

                return new PixelPoint(
                    geometry.Value.X + (int)Math.Round(pointer.X),
                    geometry.Value.Y + (int)Math.Round(pointer.Y));
            }
        }

        return null;
    }

    private void EnsureStarted()
    {
        lock (_gate)
        {
            if (_started)
                return;
            _started = true;
            _ = Task.Factory.StartNew(
                DispatchLoop, CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
    }

    private void DispatchLoop()
    {
        try
        {
            using var display = WlDisplay.Connect(null, new WlDisplayOptions());
            if (display is null)
                throw new InvalidOperationException("Could not connect to the Wayland display.");

            uint managementName = 0;
            uint managementVersion = 0;
            var registry = display.GetRegistry(
                new RegistryListener((name, interfaceName, version) =>
                {
                    if (interfaceName == ManagementInterface)
                    {
                        managementName = name;
                        managementVersion = version;
                    }
                }),
                display);

            display.Roundtrip();
            if (managementName == 0 || managementVersion < 13)
                throw new InvalidOperationException(
                    "KWin did not advertise Plasma window management v13 or newer.");

            var managementListener = new OrgKdePlasmaWindowManagement.Listener.Relay
            {
                OnWindowWithUuid = (management, _, uuid) => AddWindow(management, uuid)
            };
            _ = registry.Bind<OrgKdePlasmaWindowManagement>(
                managementName, Math.Min(managementVersion, 19u),
                managementListener, display);

            // First trip receives the list of windows and issues get_window_by_uuid requests.
            // The second receives each requested window's PID and geometry snapshot.
            display.Roundtrip();
            display.Roundtrip();
            _ready.TrySetResult(true);

            while (display.Dispatch() >= 0)
            {
            }
        }
        catch (Exception ex)
        {
            _ready.TrySetResult(false);
            ReportFailure(ex);
        }
    }

    private void AddWindow(OrgKdePlasmaWindowManagement management, string uuid)
    {
        var tracked = new TrackedWindow();
        var listener = new OrgKdePlasmaWindow.Listener.Relay
        {
            OnUnmapped = _ => RemoveWindow(tracked),
            OnGeometry = (_, x, y, width, height) =>
                UpdateGeometry(tracked, clientArea: false, x, y, width, height),
            OnPidChanged = (_, processId) =>
            {
                lock (_gate)
                    tracked.ProcessId = processId;
            },
            OnClientGeometry = (_, x, y, width, height) =>
                UpdateGeometry(tracked, clientArea: true, x, y, width, height)
        };
        var proxy = management.GetWindowByUuid(uuid, listener);
        tracked.Proxy = proxy;
        lock (_gate)
            _windows[proxy.Id] = tracked;
    }

    private void UpdateGeometry(
        TrackedWindow window, bool clientArea,
        int x, int y, uint width, uint height)
    {
        var geometry = new PixelRect(
            x, y, checked((int)width), checked((int)height));
        lock (_gate)
        {
            if (clientArea)
                window.ClientGeometry = geometry;
            else
                window.FrameGeometry = geometry;
        }
    }

    private void RemoveWindow(TrackedWindow window)
    {
        lock (_gate)
        {
            if (window.Proxy is not null)
                _windows.Remove(window.Proxy.Id);
        }

        if (window.Proxy is { IsDisposed: false, Version: >= 4 })
            window.Proxy.Destroy();
    }

    private void ReportFailure(Exception exception)
    {
        lock (_gate)
        {
            if (_reportedFailure)
                return;
            _reportedFailure = true;
        }

        Trace.TraceWarning(
            "Plasma Wayland window geometry is unavailable: {0}",
            exception.GetBaseException().Message);
    }

    private sealed class RegistryListener(
        Action<uint, string, uint> globalAdded) : WlRegistry.Listener
    {
        protected override void Global(
            WlRegistry eventSender, uint name, string interfaceName, uint version) =>
            globalAdded(name, interfaceName, version);
    }

    private sealed class TrackedWindow
    {
        public OrgKdePlasmaWindow? Proxy { get; set; }
        public uint ProcessId { get; set; }
        public PixelRect? FrameGeometry { get; set; }
        public PixelRect? ClientGeometry { get; set; }
    }
}
