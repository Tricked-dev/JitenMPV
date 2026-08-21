using System.Threading;
using JitenMPV.Core.Rendering;

namespace JitenMPV.App.Overlay;

/// Owns the desired subtitle and the geometry generation that its pixels were laid out for.
/// Platform adapters report geometry; the surface uses the resulting decision to present, hide,
/// or replay the desired frame without letting an older asynchronous render win.
internal sealed class SubtitleOverlayLifecycle
{
    private readonly object _gate = new();
    private long _revision;
    private long _geometryGeneration;
    private SubtitleFrame? _desiredFrame;
    private bool _geometryAvailable;
    private OverlayGeometry? _observedGeometry;
    private double _observedScaling;
    private OverlayGeometry? _appliedGeometry;
    private double _appliedScaling;
    private OverlayLogicalSize _appliedSize;

    public SubtitleFrame? DesiredFrame
    {
        get { lock (_gate) return _desiredFrame; }
    }

    public OverlayLogicalSize AppliedSize
    {
        get { lock (_gate) return _appliedSize; }
    }

    public long BeginFrame(SubtitleFrame frame)
    {
        lock (_gate)
        {
            _desiredFrame = frame;
            return ++_revision;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _desiredFrame = null;
            ++_revision;
        }
    }

    public bool Owns(long revision, CancellationToken ct)
    {
        lock (_gate)
            return revision == _revision && !ct.IsCancellationRequested;
    }

    public OverlayGeometryDecision ObserveGeometry(
        OverlayGeometry? geometry, double scaling)
    {
        lock (_gate)
        {
            if (geometry is null)
            {
                bool hide = _geometryAvailable;
                if (_geometryAvailable || _observedGeometry is not null)
                    ++_geometryGeneration;
                _geometryAvailable = false;
                _observedGeometry = null;
                _observedScaling = 0;
                return new OverlayGeometryDecision(
                    false, hide, false, hide, _geometryGeneration);
            }

            bool becameAvailable = !_geometryAvailable;
            bool observationChanged = becameAvailable
                                      || _observedGeometry != geometry
                                      || _observedScaling != scaling;
            if (observationChanged)
            {
                ++_geometryGeneration;
                _observedGeometry = geometry;
                _observedScaling = scaling;
            }

            bool changed = observationChanged
                           || _appliedGeometry != geometry
                           || _appliedScaling != scaling
                           || _appliedSize.Height <= 0;
            _geometryAvailable = true;

            return new OverlayGeometryDecision(
                changed, false, changed && _desiredFrame is not null,
                becameAvailable || changed && _desiredFrame is not null,
                _geometryGeneration);
        }
    }

    public bool IsGeometryCurrent(
        OverlayGeometry geometry, double scaling, long generation)
    {
        lock (_gate)
        {
            return _geometryAvailable
                   && generation == _geometryGeneration
                   && _observedGeometry == geometry
                   && _observedScaling == scaling;
        }
    }

    public bool TryMarkApplied(
        OverlayGeometry geometry,
        double scaling,
        OverlayLogicalSize size,
        long generation)
    {
        lock (_gate)
        {
            if (!_geometryAvailable
                || generation != _geometryGeneration
                || _observedGeometry != geometry
                || _observedScaling != scaling)
                return false;

            _appliedGeometry = geometry;
            _appliedScaling = scaling;
            _appliedSize = size;
            return true;
        }
    }

    public void ResetGeometry()
    {
        lock (_gate)
        {
            ++_geometryGeneration;
            _geometryAvailable = false;
            _observedGeometry = null;
            _observedScaling = 0;
            _appliedGeometry = null;
            _appliedScaling = 0;
            _appliedSize = default;
        }
    }
}

internal readonly record struct OverlayGeometryDecision(
    bool Apply,
    bool Hide,
    bool ReplayDesiredFrame,
    bool NotifyAvailability,
    long Generation);
