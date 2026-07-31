using System;
using System.Threading;
using System.Threading.Tasks;
using JitenMPV.App.Popup;
using JitenMPV.Core.Interaction;

namespace JitenMPV.App.Platform;

/// <summary>Normalizes KWin's exact v18 client geometry behind the shared provider interface.</summary>
internal sealed class KWinMpvWindowGeometryProvider : IMpvWindowGeometryProvider
{
    private static readonly OutputInfo UnknownOutput = new(
        null, null, default, default, 1, null);

    private readonly PlasmaWaylandConnectionStore _connections;
    private PlasmaWaylandConnection? _connection;

    public KWinMpvWindowGeometryProvider(
        PlasmaWaylandConnectionStore connections)
    {
        _connections = connections;
        _connections.ConnectionChanged += SetConnection;
    }

    public event Action? GeometryChanged;

    public GeometryProviderStatus Status =>
        _connection?.GeometryStatus ?? GeometryProviderStatus.Initializing;

    public ValueTask<MpvWindowGeometry?> GetGeometryAsync(
        PopupWindowContext context,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_connection?.TryGetGeometry(context, out var geometry) != true)
            return ValueTask.FromResult<MpvWindowGeometry?>(null);

        return ValueTask.FromResult<MpvWindowGeometry?>(new MpvWindowGeometry(
            geometry.Origin,
            geometry.Size,
            UnknownOutput,
            context.IsFullscreen,
            1));
    }

    public ValueTask DisposeAsync()
    {
        _connections.ConnectionChanged -= SetConnection;
        SetConnection(null);
        return ValueTask.CompletedTask;
    }

    private void SetConnection(PlasmaWaylandConnection? connection)
    {
        if (ReferenceEquals(connection, _connection))
            return;
        if (_connection is not null)
            _connection.GeometryChanged -= OnGeometryChanged;
        _connection = connection;
        if (_connection is not null)
            _connection.GeometryChanged += OnGeometryChanged;
    }

    private void OnGeometryChanged() => GeometryChanged?.Invoke();
}
