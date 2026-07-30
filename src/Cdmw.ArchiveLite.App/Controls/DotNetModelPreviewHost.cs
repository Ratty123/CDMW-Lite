using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Cdmw.ArchiveLite.App.Infrastructure;
using Cdmw.ArchiveLite.App.Services;

namespace Cdmw.ArchiveLite.App.Controls;

public sealed class DotNetModelPreviewHost : HwndHost
{
    private const string RequiredRendererBackend = "d3d11_vortice_shader";
    private const long ArchivePreviewProcessGeneration = 1;
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipChildren = 0x02000000;
    private const int WsClipSiblings = 0x04000000;
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int WmEraseBackground = 0x0014;
    /// <summary>The renderer's own default clear colour, as a COLORREF (0x00BBGGRR).</summary>
    private const int DefaultHostBackgroundColorRef = 0x1A1412;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;
    private readonly SemaphoreSlim _switchGate = new(1, 1);
    private readonly object _sessionGate = new();
    private readonly CancellationTokenSource _warmupCancellation = new();
    private CancellationTokenSource? _switchCancellation;
    private CancellationTokenSource? _cameraInputCancellation;
    private ModelRendererSession? _currentSession;
    private ModelRendererSession? _startingSession;
    private IntPtr _hostHandle;
    private IntPtr _warmupHostHandle;
    private long _generation;
    private long _cameraInputGeneration;
    private IntPtr _backgroundBrush;
    private int _backgroundBrushColorRef = -1;
    private int _prewarmStarted;
    private int _shutdown;
    private bool _hasPresentedPackage;

    public static readonly DependencyProperty PackagePathProperty = DependencyProperty.Register(
        nameof(PackagePath),
        typeof(string),
        typeof(DotNetModelPreviewHost),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.None, OnPackagePathChanged));

    private static readonly DependencyPropertyKey StatusTextPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(StatusText),
        typeof(string),
        typeof(DotNetModelPreviewHost),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty StatusTextProperty = StatusTextPropertyKey.DependencyProperty;

    private static readonly DependencyPropertyKey IsLoadingPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsLoading),
        typeof(bool),
        typeof(DotNetModelPreviewHost),
        new PropertyMetadata(false));

    public static readonly DependencyProperty IsLoadingProperty = IsLoadingPropertyKey.DependencyProperty;

    private static readonly DependencyPropertyKey IsReadyPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsReady),
        typeof(bool),
        typeof(DotNetModelPreviewHost),
        new PropertyMetadata(false));

    public static readonly DependencyProperty IsReadyProperty = IsReadyPropertyKey.DependencyProperty;

    public static readonly DependencyProperty PrewarmProperty = DependencyProperty.Register(
        nameof(Prewarm),
        typeof(bool),
        typeof(DotNetModelPreviewHost),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.None, OnPrewarmChanged));

    public static readonly DependencyProperty OrbitSensitivityProperty = DependencyProperty.Register(
        nameof(OrbitSensitivity),
        typeof(double),
        typeof(DotNetModelPreviewHost),
        new FrameworkPropertyMetadata(0.22, FrameworkPropertyMetadataOptions.None, OnCameraInputChanged));

    public static readonly DependencyProperty PanSensitivityProperty = DependencyProperty.Register(
        nameof(PanSensitivity),
        typeof(double),
        typeof(DotNetModelPreviewHost),
        new FrameworkPropertyMetadata(0.60, FrameworkPropertyMetadataOptions.None, OnCameraInputChanged));

    public static readonly DependencyProperty InvertOrbitXProperty = DependencyProperty.Register(
        nameof(InvertOrbitX),
        typeof(bool),
        typeof(DotNetModelPreviewHost),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.None, OnCameraInputChanged));

    public static readonly DependencyProperty InvertOrbitYProperty = DependencyProperty.Register(
        nameof(InvertOrbitY),
        typeof(bool),
        typeof(DotNetModelPreviewHost),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.None, OnCameraInputChanged));

    public static readonly DependencyProperty InvertPanXProperty = DependencyProperty.Register(
        nameof(InvertPanX),
        typeof(bool),
        typeof(DotNetModelPreviewHost),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.None, OnCameraInputChanged));

    public static readonly DependencyProperty InvertPanYProperty = DependencyProperty.Register(
        nameof(InvertPanY),
        typeof(bool),
        typeof(DotNetModelPreviewHost),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.None, OnCameraInputChanged));

    /// <summary>An empty value keeps the renderer's own viewport background.</summary>
    public static readonly DependencyProperty PreviewBackgroundColorProperty = DependencyProperty.Register(
        nameof(PreviewBackgroundColor),
        typeof(string),
        typeof(DotNetModelPreviewHost),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.None, OnCameraInputChanged));

    public string? PackagePath
    {
        get => (string?)GetValue(PackagePathProperty);
        set => SetValue(PackagePathProperty, value);
    }

    public string StatusText => (string)GetValue(StatusTextProperty);
    public bool IsLoading => (bool)GetValue(IsLoadingProperty);
    public bool IsReady => (bool)GetValue(IsReadyProperty);
    public bool Prewarm
    {
        get => (bool)GetValue(PrewarmProperty);
        set => SetValue(PrewarmProperty, value);
    }

    public double OrbitSensitivity
    {
        get => (double)GetValue(OrbitSensitivityProperty);
        set => SetValue(OrbitSensitivityProperty, value);
    }

    public double PanSensitivity
    {
        get => (double)GetValue(PanSensitivityProperty);
        set => SetValue(PanSensitivityProperty, value);
    }

    public bool InvertOrbitX
    {
        get => (bool)GetValue(InvertOrbitXProperty);
        set => SetValue(InvertOrbitXProperty, value);
    }

    public bool InvertOrbitY
    {
        get => (bool)GetValue(InvertOrbitYProperty);
        set => SetValue(InvertOrbitYProperty, value);
    }

    public bool InvertPanX
    {
        get => (bool)GetValue(InvertPanXProperty);
        set => SetValue(InvertPanXProperty, value);
    }

    public bool InvertPanY
    {
        get => (bool)GetValue(InvertPanYProperty);
        set => SetValue(InvertPanYProperty, value);
    }

    public string PreviewBackgroundColor
    {
        get => (string)GetValue(PreviewBackgroundColorProperty);
        set => SetValue(PreviewBackgroundColorProperty, value);
    }

    public async Task ShutdownAsync()
    {
        if (Interlocked.Exchange(ref _shutdown, 1) != 0)
        {
            return;
        }
        Interlocked.Increment(ref _generation);
        Interlocked.Increment(ref _cameraInputGeneration);
        _warmupCancellation.Cancel();
        Interlocked.Exchange(ref _switchCancellation, null)?.Cancel();
        Interlocked.Exchange(ref _cameraInputCancellation, null)?.Cancel();
        await _switchGate.WaitAsync().ConfigureAwait(true);
        try
        {
            ModelRendererSession? current;
            ModelRendererSession? starting;
            lock (_sessionGate)
            {
                current = _currentSession;
                starting = _startingSession;
                _currentSession = null;
                _startingSession = null;
            }
            if (starting is not null && !ReferenceEquals(starting, current))
            {
                await starting.ShutdownAsync().ConfigureAwait(true);
            }
            if (current is not null)
            {
                await current.ShutdownAsync().ConfigureAwait(true);
            }
            SetValue(IsLoadingPropertyKey, false);
            SetValue(IsReadyPropertyKey, false);
            SetValue(StatusTextPropertyKey, string.Empty);
            DestroyWarmupHost();
        }
        finally
        {
            _switchGate.Release();
        }
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _hostHandle = CreateWindowExW(
            0,
            "STATIC",
            string.Empty,
            WsChild | WsVisible | WsClipChildren | WsClipSiblings,
            0,
            0,
            1,
            1,
            hwndParent.Handle,
            IntPtr.Zero,
            GetModuleHandleW(null),
            IntPtr.Zero);
        if (_hostHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Could not create the .NET preview host window (Win32 {Marshal.GetLastWin32Error()}).");
        }
        if (Prewarm)
        {
            BeginPrewarm();
        }
        if (!string.IsNullOrWhiteSpace(PackagePath))
        {
            BeginSwitch(PackagePath);
        }
        return new HandleRef(this, _hostHandle);
    }

    protected override void OnWindowPositionChanged(Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        if (_hostHandle == IntPtr.Zero || Volatile.Read(ref _shutdown) != 0)
        {
            return;
        }
        // The base call has already sized the host window from this same rect. Let the
        // resize read that result back rather than rounding the rect a second time: the
        // renderer reconciles itself against the host's client rect on its own timer, so
        // a second rounding of one layout pass would leave the two fighting over a pixel.
        GetCurrentSession()?.TryResizeAttachedRenderer(_hostHandle);
    }

    /// <summary>
    /// Paints the host window itself. WPF cannot draw inside a hosted window's region, so
    /// without this any margin the renderer has not covered yet — during a resize, a DPI
    /// change, or before the first frame — is left unpainted and reads as black.
    /// </summary>
    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmEraseBackground
            && wParam != IntPtr.Zero
            && GetClientRect(hwnd, out var rect))
        {
            var brush = EnsureBackgroundBrush();
            if (brush != IntPtr.Zero && FillRect(wParam, ref rect, brush))
            {
                handled = true;
                return new IntPtr(1);
            }
        }
        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    private IntPtr EnsureBackgroundBrush()
    {
        var colorRef = ResolveBackgroundColorRef();
        if (_backgroundBrush != IntPtr.Zero && _backgroundBrushColorRef == colorRef)
        {
            return _backgroundBrush;
        }
        var brush = CreateSolidBrush(colorRef);
        if (brush == IntPtr.Zero)
        {
            return _backgroundBrush;
        }
        DestroyBackgroundBrush();
        _backgroundBrush = brush;
        _backgroundBrushColorRef = colorRef;
        return brush;
    }

    /// <summary>Converts the renderer's "#RRGGBB" clear colour into a COLORREF (0x00BBGGRR).</summary>
    private int ResolveBackgroundColorRef()
    {
        var hex = (PreviewBackgroundColor ?? string.Empty).AsSpan().Trim();
        if (hex.Length == 7
            && hex[0] == '#'
            && int.TryParse(
                hex[1..],
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out var rgb))
        {
            return ((rgb >> 16) & 0xFF) | (rgb & 0xFF00) | ((rgb & 0xFF) << 16);
        }
        return DefaultHostBackgroundColorRef;
    }

    private void DestroyBackgroundBrush()
    {
        var brush = _backgroundBrush;
        _backgroundBrush = IntPtr.Zero;
        _backgroundBrushColorRef = -1;
        if (brush != IntPtr.Zero)
        {
            _ = DeleteObject(brush);
        }
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        _hostHandle = IntPtr.Zero;
        if (Volatile.Read(ref _shutdown) == 0)
        {
            DetachResidentFromVisibleHost(hwnd.Handle);
        }
        else
        {
            DisposeSessionsImmediately();
        }
        if (hwnd.Handle != IntPtr.Zero)
        {
            _ = DestroyWindow(hwnd.Handle);
        }
        DestroyBackgroundBrush();
    }

    private static void OnPackagePathChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        ((DotNetModelPreviewHost)dependencyObject).BeginSwitch(args.NewValue as string);
    }

    private static void OnPrewarmChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (args.NewValue is true)
        {
            ((DotNetModelPreviewHost)dependencyObject).BeginPrewarm();
        }
    }

    private static void OnCameraInputChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args) =>
        ((DotNetModelPreviewHost)dependencyObject).BeginCameraInputUpdate();

    private void BeginCameraInputUpdate()
    {
        if (Volatile.Read(ref _shutdown) != 0)
        {
            return;
        }
        if (_hostHandle != IntPtr.Zero)
        {
            // The clear colour may have moved, so repaint the margin the renderer never covers.
            _ = InvalidateRect(_hostHandle, IntPtr.Zero, true);
        }
        var generation = Interlocked.Increment(ref _cameraInputGeneration);
        var operation = new CancellationTokenSource();
        operation.CancelAfter(TimeSpan.FromSeconds(5));
        Interlocked.Exchange(ref _cameraInputCancellation, operation)?.Cancel();
        _ = UpdateCameraInputAsync(generation, operation);
    }

    private async Task UpdateCameraInputAsync(long generation, CancellationTokenSource operation)
    {
        try
        {
            await Task.Delay(40, operation.Token).ConfigureAwait(true);
            await _switchGate.WaitAsync(operation.Token).ConfigureAwait(true);
            try
            {
                if (generation != Volatile.Read(ref _cameraInputGeneration))
                {
                    return;
                }
                var session = GetCurrentSession();
                if (session is { IsAlive: true, SupportsPreviewCameraInput: true })
                {
                    await session.ApplyCameraInputAsync(CaptureCameraInput(), operation.Token).ConfigureAwait(true);
                }
            }
            finally
            {
                _switchGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // A newer slider/check-box change, package switch, or shutdown owns the camera input state.
        }
        catch (Exception exception)
        {
            await DiagnosticLog.WriteAsync(
                "renderer-camera-input-update",
                exception.ToString(),
                CancellationToken.None).ConfigureAwait(true);
        }
        finally
        {
            Interlocked.CompareExchange(ref _cameraInputCancellation, null, operation);
            operation.Dispose();
        }
    }

    private PreviewCameraInput CaptureCameraInput() => new(
        Math.Clamp(OrbitSensitivity, 0.05, 1.0),
        Math.Clamp(PanSensitivity, 0.05, 3.0),
        InvertOrbitX,
        InvertOrbitY,
        InvertPanX,
        InvertPanY,
        PreviewBackgroundColor ?? string.Empty);

    private void BeginPrewarm()
    {
        if (Volatile.Read(ref _shutdown) != 0
            || Interlocked.Exchange(ref _prewarmStarted, 1) != 0)
        {
            return;
        }
        _ = PrewarmAsync();
    }

    private async Task PrewarmAsync()
    {
        var cancellationToken = _warmupCancellation.Token;
        try
        {
            await _switchGate.WaitAsync(cancellationToken).ConfigureAwait(true);
            try
            {
                if (GetCurrentSession() is { IsAlive: true })
                {
                    return;
                }
                var package = await PreviewRendererWarmupPackage.GetOrCreateAsync(cancellationToken).ConfigureAwait(true);
                var warmupHost = EnsureWarmupHost();
                var session = await ModelRendererSession.StartAsync(
                    package,
                    warmupHost,
                    static _ => { },
                    cancellationToken).ConfigureAwait(true);
                lock (_sessionGate)
                {
                    _startingSession = session;
                }
                try
                {
                    using var readyTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    readyTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                    var backend = await session.Ready.WaitAsync(readyTimeout.Token).ConfigureAwait(true);
                    if (!string.Equals(backend, RequiredRendererBackend, StringComparison.Ordinal)
                        || !session.SupportsResidentPackageLoad
                        || !session.SupportsResidentHostAttach)
                    {
                        throw new InvalidDataException("The preview renderer does not support safe resident warmup.");
                    }
                    lock (_sessionGate)
                    {
                        _currentSession = session;
                        _startingSession = null;
                    }
                }
                catch
                {
                    lock (_sessionGate)
                    {
                        if (ReferenceEquals(_startingSession, session))
                        {
                            _startingSession = null;
                        }
                    }
                    await session.ShutdownAsync().ConfigureAwait(true);
                    throw;
                }
            }
            finally
            {
                _switchGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Application shutdown owns renderer warmup cancellation.
        }
        catch
        {
            // Warmup is opportunistic; the first real package retains the normal launch fallback.
        }
    }

    private void BeginSwitch(string? packagePath)
    {
        if (Volatile.Read(ref _shutdown) != 0
            || (!string.IsNullOrWhiteSpace(packagePath) && _hostHandle == IntPtr.Zero))
        {
            return;
        }
        Interlocked.Increment(ref _cameraInputGeneration);
        Interlocked.Exchange(ref _cameraInputCancellation, null)?.Cancel();
        var generation = Interlocked.Increment(ref _generation);
        var operation = new CancellationTokenSource();
        Interlocked.Exchange(ref _switchCancellation, operation)?.Cancel();
        _ = SwitchAsync(packagePath, generation, operation);
    }

    private async Task SwitchAsync(string? packagePath, long generation, CancellationTokenSource operation)
    {
        try
        {
            await _switchGate.WaitAsync(operation.Token).ConfigureAwait(true);
            try
            {
                if (string.IsNullOrWhiteSpace(packagePath))
                {
                    _hasPresentedPackage = false;
                    if (generation == Volatile.Read(ref _generation))
                    {
                        SetValue(IsLoadingPropertyKey, false);
                        SetValue(IsReadyPropertyKey, false);
                        SetValue(StatusTextPropertyKey, string.Empty);
                    }
                    return;
                }

                SetValue(IsLoadingPropertyKey, true);
                SetValue(StatusTextPropertyKey, LocalizationManager.Get("RendererStarting"));
                var resident = GetCurrentSession();
                if (resident is { IsAlive: true, SupportsResidentPackageLoad: true, SupportsResidentHostAttach: true })
                {
                    try
                    {
                        using var loadTimeout = CancellationTokenSource.CreateLinkedTokenSource(operation.Token);
                        loadTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                        var load = await resident.LoadPackageAsync(packagePath, generation, loadTimeout.Token).ConfigureAwait(true);
                        if (!string.Equals(load.Backend, RequiredRendererBackend, StringComparison.Ordinal))
                        {
                            throw new InvalidDataException($"The resident preview renderer reported unsupported backend '{load.Backend}'.");
                        }
                        operation.Token.ThrowIfCancellationRequested();
                        if (generation != Volatile.Read(ref _generation))
                        {
                            throw new OperationCanceledException(operation.Token);
                        }
                        var visibleHost = _hostHandle;
                        if (visibleHost == IntPtr.Zero)
                        {
                            throw new OperationCanceledException(operation.Token);
                        }
                        await resident.AttachToHostAsync(visibleHost, activate: true, loadTimeout.Token).ConfigureAwait(true);
                        resident.TryResizeAttachedRenderer(visibleHost);
                        SetValue(IsReadyPropertyKey, true);
                        SetValue(IsLoadingPropertyKey, false);
                        SetValue(StatusTextPropertyKey, LocalizationManager.Get("RendererReady"));
                        _hasPresentedPackage = true;
                        BeginCameraInputUpdate();
                        return;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        // The old scene remains live while a fresh-process fallback starts.
                        SetValue(StatusTextPropertyKey, LocalizationManager.Get("RendererStarting"));
                        await DiagnosticLog.WriteAsync(
                            "renderer-resident-fallback",
                            exception.ToString(),
                            CancellationToken.None).ConfigureAwait(true);
                    }
                }
                var session = await ModelRendererSession.StartAsync(
                    packagePath,
                    _hostHandle,
                    status => SetStatusIfCurrent(generation, status),
                    operation.Token).ConfigureAwait(true);
                lock (_sessionGate)
                {
                    _startingSession = session;
                }
                try
                {
                    using var readyTimeout = CancellationTokenSource.CreateLinkedTokenSource(operation.Token);
                    readyTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                    var backend = await session.Ready.WaitAsync(readyTimeout.Token).ConfigureAwait(true);
                    if (!string.Equals(backend, RequiredRendererBackend, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException($"The preview renderer reported unsupported backend '{backend}'.");
                    }
                    operation.Token.ThrowIfCancellationRequested();
                    if (generation != Volatile.Read(ref _generation))
                    {
                        throw new OperationCanceledException(operation.Token);
                    }
                    ModelRendererSession? prior;
                    lock (_sessionGate)
                    {
                        prior = _currentSession;
                        _currentSession = session;
                        _startingSession = null;
                    }
                    session.TryResizeAttachedRenderer(_hostHandle);
                    SetValue(IsReadyPropertyKey, true);
                    SetValue(IsLoadingPropertyKey, false);
                    SetValue(StatusTextPropertyKey, LocalizationManager.Get("RendererReady"));
                    _hasPresentedPackage = true;
                    BeginCameraInputUpdate();
                    if (prior is not null)
                    {
                        await prior.ShutdownAsync().ConfigureAwait(true);
                    }
                }
                catch
                {
                    lock (_sessionGate)
                    {
                        if (ReferenceEquals(_startingSession, session))
                        {
                            _startingSession = null;
                        }
                    }
                    await session.ShutdownAsync().ConfigureAwait(true);
                    throw;
                }
            }
            finally
            {
                _switchGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // A newer preview selection or application shutdown owns the host.
        }
        catch (Exception exception)
        {
            if (generation == Volatile.Read(ref _generation) && Volatile.Read(ref _shutdown) == 0)
            {
                var prior = GetCurrentSession();
                SetValue(IsReadyPropertyKey, _hasPresentedPackage && prior is { IsAlive: true });
                SetValue(IsLoadingPropertyKey, false);
                SetValue(StatusTextPropertyKey, LocalizationManager.Format("RendererFailed", exception.Message));
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref _switchCancellation, null, operation);
            operation.Dispose();
        }
    }

    private void SetStatusIfCurrent(long generation, string status)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => SetStatusIfCurrent(generation, status));
            return;
        }
        if (generation == Volatile.Read(ref _generation) && Volatile.Read(ref _shutdown) == 0)
        {
            SetValue(StatusTextPropertyKey, status);
        }
    }

    private ModelRendererSession? GetCurrentSession()
    {
        lock (_sessionGate)
        {
            return _currentSession;
        }
    }

    private IntPtr EnsureWarmupHost()
    {
        if (_warmupHostHandle != IntPtr.Zero && IsWindow(_warmupHostHandle))
        {
            return _warmupHostHandle;
        }
        _warmupHostHandle = CreateWindowExW(
            WsExToolWindow | WsExNoActivate,
            "STATIC",
            string.Empty,
            WsPopup | WsVisible | WsClipChildren | WsClipSiblings,
            -32000,
            -32000,
            640,
            480,
            IntPtr.Zero,
            IntPtr.Zero,
            GetModuleHandleW(null),
            IntPtr.Zero);
        if (_warmupHostHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Could not create the hidden renderer warmup host (Win32 {Marshal.GetLastWin32Error()}).");
        }
        return _warmupHostHandle;
    }

    private void DestroyWarmupHost()
    {
        var warmupHost = _warmupHostHandle;
        _warmupHostHandle = IntPtr.Zero;
        if (warmupHost != IntPtr.Zero)
        {
            _ = DestroyWindow(warmupHost);
        }
    }

    private void DetachResidentFromVisibleHost(IntPtr visibleHost)
    {
        _hasPresentedPackage = false;
        Interlocked.Increment(ref _generation);
        Interlocked.Exchange(ref _switchCancellation, null)?.Cancel();
        SetValue(IsLoadingPropertyKey, false);
        SetValue(IsReadyPropertyKey, false);
        SetValue(StatusTextPropertyKey, string.Empty);

        IntPtr warmupHost;
        try
        {
            warmupHost = EnsureWarmupHost();
        }
        catch
        {
            DisposeSessionsImmediately();
            return;
        }

        lock (_sessionGate)
        {
            if (_currentSession is { IsAlive: true } current
                && current.IsAttachedTo(visibleHost))
            {
                if (current.TryAttachToHostImmediately(warmupHost))
                {
                    _ = ConfirmHiddenHostAttachmentAsync(current, warmupHost);
                }
                else
                {
                    current.DisposeImmediately();
                    _currentSession = null;
                }
            }

            if (_startingSession is { } starting
                && !ReferenceEquals(starting, _currentSession)
                && starting.IsAttachedTo(visibleHost))
            {
                starting.DisposeImmediately();
                _startingSession = null;
            }
        }
    }

    private async Task ConfirmHiddenHostAttachmentAsync(ModelRendererSession session, IntPtr warmupHost)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_warmupCancellation.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await session.AttachToHostAsync(warmupHost, activate: false, timeout.Token).ConfigureAwait(true);
        }
        catch
        {
            // The synchronous reparent already protects the resident process from HWND teardown.
        }
    }

    private void DisposeSessionsImmediately()
    {
        Interlocked.Increment(ref _generation);
        _warmupCancellation.Cancel();
        Interlocked.Exchange(ref _switchCancellation, null)?.Cancel();
        lock (_sessionGate)
        {
            _startingSession?.DisposeImmediately();
            if (!ReferenceEquals(_currentSession, _startingSession))
            {
                _currentSession?.DisposeImmediately();
            }
            _startingSession = null;
            _currentSession = null;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        int extendedStyle,
        string className,
        string windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr child, IntPtr parent);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr child);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InvalidateRect(IntPtr window, IntPtr rect, [MarshalAs(UnmanagedType.Bool)] bool erase);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FillRect(IntPtr deviceContext, ref NativeRect rect, IntPtr brush);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(int color);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private sealed record PreviewCameraInput(
        double OrbitSensitivity,
        double PanSensitivity,
        bool InvertOrbitX,
        bool InvertOrbitY,
        bool InvertPanX,
        bool InvertPanY,
        string BackgroundColor);

    private sealed class ModelRendererSession : IDisposable
    {
        private readonly Process _process;
        private readonly WorkerJob _job;
        private readonly string _runtimeRoot;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly BoundedTextTail _stderr = new(64 * 1024);
        private readonly BoundedTextTail _stdout = new(64 * 1024);
        private readonly Task _stdoutTask;
        private readonly Task _stderrTask;
        private readonly Task _exitTask;
        private readonly TaskCompletionSource<string> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<long, TaskCompletionSource<ResidentPackageLoadResult>> _packageLoads = new();
        private readonly ConcurrentDictionary<long, TaskCompletionSource<long>> _hostAttachments = new();
        private readonly ConcurrentDictionary<long, TaskCompletionSource<bool>> _cameraInputUpdates = new();
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private long _packageLoadRequestId;
        private long _hostAttachRequestId;
        private long _cameraInputRequestId;
        private long _rendererWindowHandle;
        private long _attachedHostHandle;
        private string _residentSessionId;
        private volatile bool _supportsResidentPackageLoad;
        private volatile bool _supportsResidentHostAttach;
        private volatile bool _supportsPreviewCameraInput;
        private int _disposed;

        private ModelRendererSession(
            Process process,
            WorkerJob job,
            string runtimeRoot,
            IntPtr parentHandle,
            string residentSessionId,
            Action<string> status)
        {
            _process = process;
            _job = job;
            _runtimeRoot = runtimeRoot;
            _attachedHostHandle = parentHandle.ToInt64();
            _residentSessionId = residentSessionId;
            _stdoutTask = ReadProtocolAsync(process.StandardOutput, status, _lifetime.Token);
            _stderrTask = DrainAsync(process.StandardError, _stderr, _lifetime.Token);
            _exitTask = ObserveExitAsync();
        }

        public Task<string> Ready => _ready.Task;
        public bool IsAlive => Volatile.Read(ref _disposed) == 0 && !_process.HasExited;
        public bool SupportsResidentPackageLoad => _supportsResidentPackageLoad;
        public bool SupportsResidentHostAttach => _supportsResidentHostAttach;
        public bool SupportsPreviewCameraInput => _supportsPreviewCameraInput;

        public bool IsAttachedTo(IntPtr hostHandle) =>
            hostHandle != IntPtr.Zero
            && Interlocked.Read(ref _attachedHostHandle) == hostHandle.ToInt64();

        public bool TryAttachToHostImmediately(IntPtr parentHandle)
        {
            if (parentHandle == IntPtr.Zero || !IsWindow(parentHandle))
            {
                return false;
            }
            var rendererHandle = new IntPtr(Interlocked.Read(ref _rendererWindowHandle));
            if (rendererHandle == IntPtr.Zero || !IsWindow(rendererHandle))
            {
                return false;
            }
            _ = SetParent(rendererHandle, parentHandle);
            if (GetParent(rendererHandle) != parentHandle
                || !GetClientRect(parentHandle, out var rect))
            {
                return false;
            }
            var width = Math.Max(1, rect.Right - rect.Left);
            var height = Math.Max(1, rect.Bottom - rect.Top);
            if (!SetWindowPos(
                    rendererHandle,
                    IntPtr.Zero,
                    0,
                    0,
                    width,
                    height,
                    SwpNoZOrder | SwpNoActivate | SwpFrameChanged | SwpShowWindow))
            {
                return false;
            }
            Interlocked.Exchange(ref _attachedHostHandle, parentHandle.ToInt64());
            return true;
        }

        /// <summary>
        /// Sizes the renderer from the host's own client rect, which is the single figure
        /// the renderer's reconciliation timer also reads.
        /// </summary>
        public bool TryResizeAttachedRenderer(IntPtr parentHandle)
        {
            if (parentHandle == IntPtr.Zero
                || !IsWindow(parentHandle)
                || Interlocked.Read(ref _attachedHostHandle) != parentHandle.ToInt64())
            {
                return false;
            }
            var rendererHandle = new IntPtr(Interlocked.Read(ref _rendererWindowHandle));
            if (rendererHandle == IntPtr.Zero
                || !IsWindow(rendererHandle)
                || GetParent(rendererHandle) != parentHandle
                || !GetClientRect(parentHandle, out var rect))
            {
                return false;
            }
            var width = Math.Max(1, rect.Right - rect.Left);
            var height = Math.Max(1, rect.Bottom - rect.Top);
            return SetWindowPos(
                rendererHandle,
                IntPtr.Zero,
                0,
                0,
                width,
                height,
                SwpNoZOrder | SwpNoActivate | SwpFrameChanged | SwpShowWindow);
        }

        public async Task AttachToHostAsync(
            IntPtr parentHandle,
            bool activate,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (!_supportsResidentHostAttach)
            {
                throw new NotSupportedException("The active renderer does not support resident host attachment.");
            }
            if (parentHandle == IntPtr.Zero || !IsWindow(parentHandle))
            {
                throw new InvalidOperationException("The requested preview host window is unavailable.");
            }

            var requestId = Interlocked.Increment(ref _hostAttachRequestId);
            var completion = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_hostAttachments.TryAdd(requestId, completion))
            {
                throw new InvalidOperationException("Could not register the resident host attachment request.");
            }
            try
            {
                var message = JsonSerializer.Serialize(new
                {
                    @event = "host_attach_request",
                    request_id = requestId,
                    parent_hwnd = parentHandle.ToInt64(),
                    activate,
                });
                await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                    await _process.StandardInput.WriteLineAsync(message).WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _writeGate.Release();
                }
                var attachedHandle = await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (attachedHandle != parentHandle.ToInt64())
                {
                    throw new InvalidDataException("The resident renderer acknowledged the wrong host window.");
                }
                Interlocked.Exchange(ref _attachedHostHandle, attachedHandle);
            }
            finally
            {
                _hostAttachments.TryRemove(requestId, out _);
            }
        }

        public async Task<ResidentPackageLoadResult> LoadPackageAsync(
            string packagePath,
            long generation,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (!_supportsResidentPackageLoad)
            {
                throw new NotSupportedException("The active renderer does not support resident package loading.");
            }
            var package = ValidatePackage(packagePath);
            var requestId = Interlocked.Increment(ref _packageLoadRequestId);
            var completion = new TaskCompletionSource<ResidentPackageLoadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_packageLoads.TryAdd(requestId, completion))
            {
                throw new InvalidOperationException("Could not register the resident package load request.");
            }
            try
            {
                var message = JsonSerializer.Serialize(new
                {
                    @event = "package_load_request",
                    request_id = requestId,
                    generation,
                    package_path = package,
                });
                await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                    await _process.StandardInput.WriteLineAsync(message).WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _writeGate.Release();
                }
                var result = await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(result.SessionId))
                {
                    throw new InvalidDataException("The resident preview package acknowledgement has no session identity.");
                }
                _residentSessionId = result.SessionId;
                return result;
            }
            finally
            {
                _packageLoads.TryRemove(requestId, out _);
            }
        }

        public async Task ApplyCameraInputAsync(
            PreviewCameraInput input,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (!_supportsPreviewCameraInput)
            {
                throw new NotSupportedException("The active renderer does not support live preview camera input settings.");
            }
            var sessionId = _residentSessionId;
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                throw new InvalidDataException("The active preview package has no resident session identity.");
            }

            var requestId = Interlocked.Increment(ref _cameraInputRequestId);
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_cameraInputUpdates.TryAdd(requestId, completion))
            {
                throw new InvalidOperationException("Could not register the preview camera input request.");
            }
            try
            {
                var message = JsonSerializer.Serialize(new
                {
                    @event = "presentation_state_update",
                    session_id = sessionId,
                    request_id = requestId,
                    base_revision = 0,
                    process_generation = ArchivePreviewProcessGeneration,
                    protocol_version = 2,
                    presentation_generation = requestId,
                    display = new
                    {
                        quality = new
                        {
                            orbit_sensitivity = input.OrbitSensitivity,
                            pan_sensitivity = input.PanSensitivity,
                            invert_orbit_x = input.InvertOrbitX,
                            invert_orbit_y = input.InvertOrbitY,
                            invert_pan_x = input.InvertPanX,
                            invert_pan_y = input.InvertPanY,
                            // An empty string leaves the renderer's own viewport background in place.
                            d3d11_background_color = input.BackgroundColor,
                        },
                    },
                });
                await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                    await _process.StandardInput.WriteLineAsync(message).WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _writeGate.Release();
                }
                await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _cameraInputUpdates.TryRemove(requestId, out _);
            }
        }

        public static Task<ModelRendererSession> StartAsync(
            string packagePath,
            IntPtr parentHandle,
            Action<string> status,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var package = ValidatePackage(packagePath);
            var residentSessionId = ReadPreviewSessionId(package);
            var manifest = Path.Combine(package, "manifest.json");
            var metadata = Path.Combine(package, "mesh.cdmeta.json");
            var renderer = ResolveRendererPath();
            var runtimeRoot = CreateRuntimeRoot();
            var output = Path.Combine(runtimeRoot, "output");
            Directory.CreateDirectory(output);
            var startInfo = new ProcessStartInfo
            {
                FileName = renderer,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(renderer) ?? AppContext.BaseDirectory,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };
            foreach (var argument in new[]
            {
                "--input-package", package,
                "--mesh", manifest,
                "--metadata", metadata,
                "--status", Path.Combine(runtimeRoot, "status.json"),
                "--output", output,
                "--edit-operations", Path.Combine(runtimeRoot, "edit_operations.json"),
                "--evaluation", Path.Combine(runtimeRoot, "evaluation.md"),
                "--embedded",
                "--simple-preview",
                "--parent-hwnd", parentHandle.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture),
            })
            {
                startInfo.ArgumentList.Add(argument);
            }
            startInfo.Environment["DOTNET_EnableDiagnostics"] = "0";

            Process? process = null;
            WorkerJob? job = null;
            try
            {
                process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("The .NET/Vortice previewer could not be started.");
                process.StandardInput.AutoFlush = true;
                job = WorkerJob.Create();
                job.Add(process);
                return Task.FromResult(new ModelRendererSession(
                    process,
                    job,
                    runtimeRoot,
                    parentHandle,
                    residentSessionId,
                    status));
            }
            catch
            {
                job?.Dispose();
                try
                {
                    if (process is { HasExited: false })
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // Preserve the renderer launch failure.
                }
                process?.Dispose();
                DeleteRuntimeRoot(runtimeRoot);
                throw;
            }
        }

        public async Task ShutdownAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            try
            {
                if (!_process.HasExited)
                {
                    await _process.StandardInput.WriteLineAsync("{\"event\":\"close_request\"}").ConfigureAwait(false);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
                    await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // The job-object fence below performs the bounded forced stop.
            }
            catch (IOException)
            {
                // The renderer has already disconnected.
            }
            finally
            {
                DisposeImmediatelyCore();
                await ObserveTasksAsync().ConfigureAwait(false);
            }
        }

        public void DisposeImmediately()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                DisposeImmediatelyCore();
                _ = ObserveTasksAsync();
            }
        }

        public void Dispose() => DisposeImmediately();

        private async Task ReadProtocolAsync(StreamReader reader, Action<string> status, CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (line is null)
                    {
                        break;
                    }
                    _stdout.Append(line + Environment.NewLine);
                    if (Encoding.UTF8.GetByteCount(line) > 1024 * 1024)
                    {
                        throw new InvalidDataException("The .NET previewer emitted an oversized protocol message.");
                    }
                    try
                    {
                        using var message = JsonDocument.Parse(line);
                        var root = message.RootElement;
                        var eventName = JsonString(root, "event").Trim().ToLowerInvariant();
                        switch (eventName)
                        {
                            case "protocol_ready":
                                _supportsResidentPackageLoad = HasCapability(root, "resident_package_load_v1");
                                _supportsResidentHostAttach = HasCapability(root, "resident_host_attach_v1");
                                if (root.TryGetProperty("window_handle", out var windowHandle)
                                    && windowHandle.TryGetInt64(out var parsedWindowHandle)
                                    && parsedWindowHandle > 0)
                                {
                                    Interlocked.Exchange(ref _rendererWindowHandle, parsedWindowHandle);
                                }
                                status(LocalizationManager.Get("RendererLoading"));
                                break;
                            case "ready":
                                _supportsPreviewCameraInput = HasCapability(root, "resident_presentation_state_v1");
                                var backend = root.TryGetProperty("renderer", out var renderer)
                                    ? JsonString(renderer, "backend")
                                    : string.Empty;
                                _ready.TrySetResult(backend);
                                break;
                            case "error":
                                _ready.TrySetException(new InvalidDataException(
                                    JsonString(root, "message", LocalizationManager.Get("RendererUnknownError"))));
                                break;
                            case "package_load_applied":
                                CompletePackageLoad(root, failed: false);
                                break;
                            case "package_load_failed":
                                CompletePackageLoad(root, failed: true);
                                break;
                            case "host_attach_applied":
                                CompleteHostAttachment(root, failed: false);
                                break;
                            case "host_attach_failed":
                                CompleteHostAttachment(root, failed: true);
                                break;
                            case "presentation_state_update_ack":
                                CompleteCameraInputUpdate(root);
                                break;
                        }
                    }
                    catch (JsonException)
                    {
                        // Retain non-protocol diagnostics in the bounded stdout tail.
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Expected during preview replacement or application shutdown.
            }
            catch (Exception exception)
            {
                _ready.TrySetException(exception);
                FailPendingPackageLoads(exception);
                FailPendingHostAttachments(exception);
                FailPendingCameraInputUpdates(exception);
            }
        }

        private async Task ObserveExitAsync()
        {
            try
            {
                await _process.WaitForExitAsync(_lifetime.Token).ConfigureAwait(false);
                if (!_ready.Task.IsCompleted)
                {
                    var detail = _stderr.ToString().Trim();
                    _ready.TrySetException(new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                        ? $"The .NET previewer exited before its first frame (code {_process.ExitCode})."
                        : detail));
                }
                FailPendingPackageLoads(new InvalidOperationException(
                    $"The .NET previewer exited during resident package loading (code {_process.ExitCode})."));
                FailPendingHostAttachments(new InvalidOperationException(
                    $"The .NET previewer exited during host attachment (code {_process.ExitCode})."));
                FailPendingCameraInputUpdates(new InvalidOperationException(
                    $"The .NET previewer exited during a camera input update (code {_process.ExitCode})."));
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                // Expected during owned teardown.
            }
        }

        private void DisposeImmediatelyCore()
        {
            _lifetime.Cancel();
            FailPendingPackageLoads(new OperationCanceledException("The resident preview renderer is shutting down."));
            FailPendingHostAttachments(new OperationCanceledException("The resident preview renderer is shutting down."));
            FailPendingCameraInputUpdates(new OperationCanceledException("The resident preview renderer is shutting down."));
            _job.Dispose();
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Job disposal is the primary process-tree teardown.
            }
        }

        private async Task ObserveTasksAsync()
        {
            foreach (var task in new[] { _stdoutTask, _stderrTask, _exitTask })
            {
                try
                {
                    await task.ConfigureAwait(false);
                }
                catch
                {
                    // Bounded protocol and stderr diagnostics own the useful failure detail.
                }
            }
            if (_ready.Task.IsFaulted)
            {
                _ = _ready.Task.Exception;
            }
            _lifetime.Dispose();
            _writeGate.Dispose();
            _process.Dispose();
            DeleteRuntimeRoot(_runtimeRoot);
        }

        private static async Task DrainAsync(StreamReader reader, BoundedTextTail tail, CancellationToken cancellationToken)
        {
            var buffer = new char[4096];
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (count == 0)
                    {
                        break;
                    }
                    tail.Append(new string(buffer, 0, count));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Expected during owned teardown.
            }
        }

        private static string ResolveRendererPath()
        {
            var overridePath = Environment.GetEnvironmentVariable("CDMW_ARCHIVE_LITE_DOTNET_PREVIEW_PATH");
            if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            {
                return Path.GetFullPath(overridePath);
            }
            var packaged = Path.Combine(AppContext.BaseDirectory, "renderer", "cdmw-mesh-dotnet-editor.exe");
            if (File.Exists(packaged))
            {
                return packaged;
            }
            for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
            {
                foreach (var configuration in new[] { "Release", "Debug" })
                {
                    var candidate = Path.Combine(
                        current.FullName,
                        "tools",
                        "dotnet_mesh_editor_experiment",
                        "bin",
                        configuration,
                        "net8.0-windows",
                        "cdmw-mesh-dotnet-editor.exe");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
            throw new FileNotFoundException(
                "cdmw-mesh-dotnet-editor.exe was not found. Rebuild the Archive Lite portable package or set CDMW_ARCHIVE_LITE_DOTNET_PREVIEW_PATH.");
        }

        private static string CreateRuntimeRoot()
        {
            var dataRoot = Path.Combine(AppDataPaths.Cache, "preview", "runtime");
            Directory.CreateDirectory(dataRoot);
            var runtime = Path.Combine(dataRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(runtime);
            return runtime;
        }

        private static void DeleteRuntimeRoot(string runtimeRoot)
        {
            try
            {
                var root = Path.Combine(AppDataPaths.Cache, "preview", "runtime");
                var resolvedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var resolvedRuntime = Path.GetFullPath(runtimeRoot);
                if (resolvedRuntime.StartsWith(resolvedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && Directory.Exists(resolvedRuntime))
                {
                    Directory.Delete(resolvedRuntime, recursive: true);
                }
            }
            catch
            {
                // Bounded cache maintenance can remove a stale runtime folder later.
            }
        }

        private static string JsonString(JsonElement element, string name, string fallback = "")
        {
            return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? fallback
                : fallback;
        }

        private void CompletePackageLoad(JsonElement root, bool failed)
        {
            var requestId = root.TryGetProperty("request_id", out var request)
                && request.TryGetInt64(out var parsed)
                ? parsed
                : 0;
            if (requestId <= 0 || !_packageLoads.TryGetValue(requestId, out var completion))
            {
                return;
            }
            if (failed)
            {
                completion.TrySetException(new InvalidDataException(
                    JsonString(root, "message", "Resident package load failed.")));
                return;
            }
            var backend = root.TryGetProperty("renderer", out var renderer)
                ? JsonString(renderer, "backend")
                : string.Empty;
            var sceneLoadCount = root.TryGetProperty("resident_scene_load_count", out var count)
                && count.TryGetInt64(out var parsedCount)
                ? parsedCount
                : 0;
            completion.TrySetResult(new ResidentPackageLoadResult(
                backend,
                sceneLoadCount,
                JsonString(root, "session_id").Trim()));
        }

        private void FailPendingPackageLoads(Exception exception)
        {
            foreach (var pending in _packageLoads.Values)
            {
                pending.TrySetException(exception);
            }
        }

        private void CompleteHostAttachment(JsonElement root, bool failed)
        {
            var requestId = root.TryGetProperty("request_id", out var request)
                && request.TryGetInt64(out var parsed)
                ? parsed
                : 0;
            if (requestId <= 0 || !_hostAttachments.TryGetValue(requestId, out var completion))
            {
                return;
            }
            if (failed)
            {
                completion.TrySetException(new InvalidDataException(
                    JsonString(root, "message", "Resident host attachment failed.")));
                return;
            }
            var parentHandle = root.TryGetProperty("parent_hwnd", out var parent)
                && parent.TryGetInt64(out var parsedParent)
                ? parsedParent
                : 0;
            completion.TrySetResult(parentHandle);
        }

        private void FailPendingHostAttachments(Exception exception)
        {
            foreach (var pending in _hostAttachments.Values)
            {
                pending.TrySetException(exception);
            }
        }

        private void CompleteCameraInputUpdate(JsonElement root)
        {
            var requestId = root.TryGetProperty("request_id", out var request)
                && request.TryGetInt64(out var parsed)
                ? parsed
                : 0;
            if (requestId <= 0 || !_cameraInputUpdates.TryGetValue(requestId, out var completion))
            {
                return;
            }
            if (!string.Equals(JsonString(root, "status"), "applied", StringComparison.OrdinalIgnoreCase))
            {
                completion.TrySetException(new InvalidDataException(
                    $"The renderer rejected the camera input update: {JsonString(root, "reason", "unknown reason")}."));
                return;
            }
            completion.TrySetResult(true);
        }

        private void FailPendingCameraInputUpdates(Exception exception)
        {
            foreach (var pending in _cameraInputUpdates.Values)
            {
                pending.TrySetException(exception);
            }
        }

        private static bool HasCapability(JsonElement root, string capability)
        {
            return root.TryGetProperty("capabilities", out var capabilities)
                && capabilities.ValueKind == JsonValueKind.Array
                && capabilities.EnumerateArray().Any(value =>
                    value.ValueKind == JsonValueKind.String
                    && string.Equals(value.GetString(), capability, StringComparison.Ordinal));
        }

        private static string ReadPreviewSessionId(string packagePath)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(packagePath, "dotnet_scene.json")));
            var sessionId = JsonString(document.RootElement, "session_id").Trim();
            if (sessionId.Length == 0)
            {
                throw new InvalidDataException("The read-only .NET preview scene has no session identity.");
            }
            return sessionId;
        }

        private static string ValidatePackage(string packagePath)
        {
            var package = Path.GetFullPath(packagePath);
            if (!Directory.Exists(package)
                || !File.Exists(Path.Combine(package, "manifest.json"))
                || !File.Exists(Path.Combine(package, "mesh.cdmeta.json"))
                || !File.Exists(Path.Combine(package, "net_materials.json"))
                || !File.Exists(Path.Combine(package, "dotnet_scene.json")))
            {
                throw new InvalidDataException("The read-only .NET preview package is incomplete.");
            }
            return package;
        }

        public sealed record ResidentPackageLoadResult(
            string Backend,
            long ResidentSceneLoadCount,
            string SessionId);
    }
}
