using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using JitenMPV.App.Platform;
using JitenMPV.App.ViewModels;
using JitenMPV.App.Views;
using JitenMPV.Core.Config;
using JitenMPV.Core.Interaction;

namespace JitenMPV.App.Popup;

public sealed class AvaloniaPopupPresenter : IPopupPresenter
{
    private DictionaryPopupWindow? _window;
    private PopupViewModel? _viewModel;
    private volatile bool _isVisible;
    private PixelPoint? _lastCursorPos;
    private PopupPositionMode _positionMode = PopupPositionMode.AboveSubtitle;
    private PopupAnchor _fixedAnchor = PopupAnchor.TopCenter;
    private int _offsetPx = 60;
    private double _lastFontScale = -1;
    private int _lastMaxWidth = -1;
    private volatile PopupWindowContext _windowContext = PopupWindowContext.Empty;
    private bool _repositionQueued;
    private readonly PlasmaWaylandPopupBridge _plasmaWayland = new();
    private readonly PlasmaWindowGeometryTracker _plasmaWindowGeometry = new();

    public bool IsVisible => _isVisible;

    public event Action<PopupAction>? ActionClicked;
    public event Action<int>? DeckSelected;
    public event Action? MouseEntered;
    public event Action? MouseLeft;

    public void UpdateWindowContext(PopupWindowContext context)
    {
        _windowContext = context;
        Dispatcher.UIThread.Post(() =>
        {
            if (_window?.IsVisible == true)
            {
                X11MpvWindowBridge.SetTransientOwner(_window, _windowContext.WindowId);
                QueuePositionWindow();
            }
        });
    }

    public Task ShowAsync(PopupData data, PopupPointerPosition pointer, CancellationToken ct)
    {
        return Dispatcher.UIThread.InvokeAsync(
            () => ShowOnUiThreadAsync(data, pointer, ct));
    }

    private async Task ShowOnUiThreadAsync(
        PopupData data, PopupPointerPosition pointer, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        EnsureWindow();

        _positionMode = data.PositionMode;
        _fixedAnchor = data.FixedAnchor;
        _offsetPx = data.OffsetPx;
        _viewModel!.Update(data);
        ApplyFontScale(data.FontScale);
        ApplyMaxWidth(data.MaxWidthPx);
        _lastCursorPos = await ResolveCursorPositionAsync(pointer);

        await PositionWindowAsync(_lastCursorPos);

        if (!_window!.IsVisible)
        {
            _window.Show();

            // A Wayland hide destroys its wl_surface. Re-showing creates the replacement in
            // Window.Show(), so attach Plasma metadata once more before the first settled frame.
            if (_plasmaWayland.IsNativeWayland(_window))
                await PositionWindowAsync(_lastCursorPos);
        }

        _isVisible = true;
        X11MpvWindowBridge.SetTransientOwner(_window, _windowContext.WindowId);
        QueuePositionWindow();
    }

    public Task UpdateAsync(PopupData data, CancellationToken ct)
    {
        return Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (ct.IsCancellationRequested) return;
            EnsureWindow();
            _viewModel!.Update(data);
            ApplyFontScale(data.FontScale);
            ApplyMaxWidth(data.MaxWidthPx);
        }).GetTask();
    }

    public Task HideAsync(CancellationToken ct)
    {
        return Dispatcher.UIThread.InvokeAsync(
            () => HideOnUiThreadAsync(ct));
    }

    private async Task HideOnUiThreadAsync(CancellationToken ct)
    {
        _isVisible = false;
        _viewModel?.CloseDeckPicker();
        if (_window is null) return;

        // Plasma requires its metadata object to be destroyed before the wl_surface it decorates.
        var nativeWayland = _plasmaWayland.IsNativeWayland(_window);
        await _plasmaWayland.DetachAsync(_window);
        if (nativeWayland)
        {
            // Avalonia's Wayland Hide() tears down the worker surface and its connection. Reusing
            // the same high-level Window after that leaves our separately-bound Plasma metadata
            // coupled to a retired lifecycle and the replacement surface eventually stops mapping.
            // Closing and recreating this small popup gives every show one coherent surface,
            // worker connection and Plasma role.
            _window.Close();
            _window = null;
            _viewModel = null;
            _lastCursorPos = null;
            _repositionQueued = false;
        }
        else
        {
            _window.Hide();
        }
    }

    private void EnsureWindow()
    {
        if (_window is not null) return;

        _viewModel = new PopupViewModel();
        _viewModel.ActionClicked += action => ActionClicked?.Invoke(action);
        _viewModel.DeckSelected += deckId => DeckSelected?.Invoke(deckId);

        _window = new DictionaryPopupWindow { DataContext = _viewModel };
        _lastFontScale = -1;
        _lastMaxWidth = -1;

        _window.PointerEntered += (_, _) => MouseEntered?.Invoke();
        _window.PointerExited += (_, _) => MouseLeft?.Invoke();
        _window.SizeChanged += (_, _) => QueuePositionWindow();
    }

    private void QueuePositionWindow()
    {
        if (_repositionQueued) return;
        _repositionQueued = true;
        Dispatcher.UIThread.Post(async () =>
        {
            _repositionQueued = false;
            if (_isVisible)
                await PositionWindowAsync(_lastCursorPos);
        }, DispatcherPriority.Render);
    }

    private async Task<PixelPoint?> ResolveCursorPositionAsync(PopupPointerPosition pointer)
    {
        if (!OperatingSystem.IsLinux())
            return CursorPositionHelper.GetCursorPosition();

        // Native Wayland does not expose a global position to this XWayland process. An absent mpv
        // XID therefore means "anchor deterministically", not "reuse X11's last known pointer".
        var translated = X11MpvWindowBridge.TranslateToRoot(
            _windowContext.WindowId, pointer.X, pointer.Y);
        if (translated is not null)
            return translated;
        if (_windowContext.WindowId is > 0)
            return CursorPositionHelper.GetCursorPosition();

        // A fullscreen native-Wayland mpv surface exactly covers the output it reports. mpv's
        // pointer coordinates can therefore be translated to Plasma's global logical space by
        // adding that output's origin, including in a multi-monitor layout.
        if (_windowContext.IsFullscreen && ScreenFromMpvDisplayName() is { } screen)
        {
            return new PixelPoint(
                screen.Bounds.X + (int)Math.Round(pointer.X),
                screen.Bounds.Y + (int)Math.Round(pointer.Y));
        }

        // A windowed Wayland surface can be moved anywhere, so its output alone is insufficient.
        // KWin's window-management protocol publishes the absolute client-area origin; adding
        // mpv's local pointer coordinates gives the same global point X11's translate call does.
        if (_windowContext.ProcessId is { } processId)
            return await _plasmaWindowGeometry.TranslateFromClientAsync(processId, pointer);

        return null;
    }

    private void ApplyFontScale(double scale)
    {
        if (_window is null || scale == _lastFontScale) return;
        _lastFontScale = scale;
        var container = _window.FindControl<LayoutTransformControl>("ScaleContainer");
        if (container is not null)
            container.LayoutTransform = new ScaleTransform(scale, scale);
    }

    private void ApplyMaxWidth(int maxWidthPx)
    {
        if (_window is null || maxWidthPx == _lastMaxWidth || maxWidthPx <= 0) return;
        _lastMaxWidth = maxWidthPx;
        _window.MaxWidth = maxWidthPx;
    }

    private async Task PositionWindowAsync(PixelPoint? cursorPos)
    {
        if (_window is null) return;

        var screen = cursorPos is { } known
            ? _window.Screens.ScreenFromPoint(known)
            : ScreenFromMpvDisplayName() ?? _window.Screens.Primary;
        if (screen is null) return;

        var workArea = screen.WorkingArea;
        var scaling = screen.Scaling;

        var bounds = _window.Bounds.Size;
        int windowWidth = bounds.Width > 0 ? (int)(bounds.Width * scaling) : 350;
        int windowHeight = bounds.Height > 0 ? (int)(bounds.Height * scaling) : 250;

        // Without a cursor there is nothing to be relative to, and the clamped result would pin the
        // popup to the top-left corner. Anchoring it near the subtitles keeps it usable on systems
        // that cannot report a global pointer position, such as a Wayland session.
        var (x, y) = _positionMode == PopupPositionMode.Fixed || cursorPos is null
            ? AnchoredPosition(workArea, windowWidth, windowHeight,
                cursorPos is null ? PopupAnchor.BottomCenter : _fixedAnchor)
            : CursorRelativePosition(cursorPos.Value, workArea, windowWidth, windowHeight);

        var position = new PixelPoint(
            Math.Clamp(x, workArea.X, Math.Max(workArea.X, workArea.Right - windowWidth)),
            Math.Clamp(y, workArea.Y, Math.Max(workArea.Y, workArea.Bottom - windowHeight)));

        if (!await _plasmaWayland.TrySetPositionAsync(_window, position))
            _window.Position = position;
    }

    private Avalonia.Platform.Screen? ScreenFromMpvDisplayName()
    {
        if (_window is null || _windowContext.DisplayNames.Count == 0) return null;

        return _window.Screens.All.FirstOrDefault(screen =>
            screen.DisplayName is { } name
            && _windowContext.DisplayNames.Any(display =>
                string.Equals(display, name, StringComparison.OrdinalIgnoreCase)));
    }

    /// The pointer sits inside the subtitle line it is pointing at, so the offset has to clear the
    /// text rather than merely separate the popup from the cursor hotspot.
    private (int X, int Y) CursorRelativePosition(
        PixelPoint cursor, PixelRect workArea, int width, int height)
    {
        int x = cursor.X - width / 2;

        if (_positionMode == PopupPositionMode.BelowSubtitle)
        {
            int below = cursor.Y + _offsetPx;
            return (x, below + height > workArea.Bottom ? cursor.Y - height - _offsetPx : below);
        }

        int above = cursor.Y - height - _offsetPx;
        return (x, above < workArea.Y ? cursor.Y + _offsetPx : above);
    }

    private (int X, int Y) AnchoredPosition(
        PixelRect workArea, int width, int height, PopupAnchor anchor)
    {
        int x = anchor switch
        {
            PopupAnchor.TopLeft or PopupAnchor.BottomLeft => workArea.X + _offsetPx,
            PopupAnchor.TopRight or PopupAnchor.BottomRight => workArea.Right - width - _offsetPx,
            _ => workArea.X + (workArea.Width - width) / 2
        };

        bool top = anchor is PopupAnchor.TopLeft or PopupAnchor.TopCenter or PopupAnchor.TopRight;
        return (x, top ? workArea.Y + _offsetPx : workArea.Bottom - height - _offsetPx);
    }
}
