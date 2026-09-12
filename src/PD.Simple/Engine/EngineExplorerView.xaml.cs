using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using CircuitHub.AllegroBridge.Engine.Interactions;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;
using CircuitHub.AllegroBridge.Wpf;

namespace PD.Simple.Engine;

/// <summary>
/// Hosts the reusable Engine workbench when the matching WPF package is present.
/// Older checked-in previews fall back to a small read-only Engine surface rather
/// than making the flagship application fail at XAML compile time.
/// </summary>
public partial class EngineExplorerView : UserControl, IAsyncDisposable
{
    private readonly EventHandler _workbenchStateChanged;
    private readonly EventHandler<string> _workbenchStatusChanged;
    private BridgeSession? _session;
    private object? _workbench;
    private FrameworkElement? _workbenchElement;
    private TextBlock? _fallbackStatus;
    private Button? _fallbackAcquire;
    private bool _fallbackBusy;
    private bool _disposed;

    public EngineExplorerView()
    {
        InitializeComponent();
        _workbenchStateChanged = (_, _) =>
        {
            RefreshState();
            StateChanged?.Invoke(this, EventArgs.Empty);
        };
        _workbenchStatusChanged = (_, _) =>
        {
            RefreshState();
            StateChanged?.Invoke(this, EventArgs.Empty);
        };

        if (!TryCreateSharedWorkbench())
        {
            CreateCompatibilitySurface();
        }
        RefreshState();
    }

    public BridgeSession? Session
    {
        get => _session;
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (ReferenceEquals(_session, value))
            {
                return;
            }
            if (_session is not null)
            {
                _session.StateChanged -= Session_StateChanged;
            }
            _session = value;
            if (_session is not null)
            {
                _session.StateChanged += Session_StateChanged;
            }
            RefreshState();
        }
    }

    public bool IsBusy => _workbench is null ? _fallbackBusy : ReadBoolean("IsBusy");
    public bool HasUnresolvedEdit => _workbench is not null && ReadBoolean("HasUnresolvedEdit");
    public bool CanClose => _workbench is null || ReadBoolean("CanClose", fallback: true);
    public bool CanSwitchNativeSession => CanClose && !HasUnresolvedEdit;
    public bool CanStartNativeMutation => !IsBusy && CanSwitchNativeSession;
    public string? NativeSessionRetentionReason => !CanClose
        ? "A native Engine edit has been dispatched and its terminal result is still being tracked."
        : HasUnresolvedEdit
            ? "The current board has an uncertain or guarded-recovery Engine edit outcome. Review or recover it before switching boards or starting another native mutation."
            : null;
    public string StatusMessage => _workbench is null
        ? _fallbackStatus?.Text ?? "Engine explorer is ready."
        : ReadString("StatusMessage") ?? "Engine workbench is ready.";

    public event EventHandler? StateChanged;

    private bool TryCreateSharedWorkbench()
    {
        Type? workbenchType = Type.GetType(
            "CircuitHub.AllegroBridge.Wpf.Engine.EngineWorkbenchView, CircuitHub.AllegroBridge.Wpf",
            throwOnError: false);
        if (workbenchType is null || !typeof(FrameworkElement).IsAssignableFrom(workbenchType))
        {
            return false;
        }

        object instance = Activator.CreateInstance(workbenchType)
            ?? throw new InvalidOperationException("The shared Engine workbench could not be created.");
        _workbench = instance;
        _workbenchElement = (FrameworkElement)instance;
        WorkbenchHost.Content = _workbenchElement;

        SetProperty("WorkspaceProvider", new Func<CancellationToken, ValueTask<AllegroWorkspace>>(ProvideWorkspaceAsync));
        SetProperty("ReviewCaptureProvider", new Func<LiveDesignScene, AnnotationScene, CancellationToken, ValueTask<AllegroReviewFrame>>(ProvideReviewCaptureAsync));
        Subscribe("BusyChanged", _workbenchStateChanged);
        Subscribe("SceneChanged", _workbenchStateChanged);
        Subscribe("StatusChanged", _workbenchStatusChanged);
        return true;
    }

    private void CreateCompatibilitySurface()
    {
        var title = new TextBlock
        {
            Text = "Allegro Engine",
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8),
        };
        var explanation = new TextBlock
        {
            Text = "This checkout uses an older Bridge WPF preview that does not contain the shared Engine Workbench. The application remains usable and can verify the selected board through the Engine. A matching Bridge rebuild enables the full reusable Workbench automatically.",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 760,
            Margin = new Thickness(0, 0, 0, 18),
        };
        _fallbackAcquire = new Button
        {
            Content = "Acquire board metadata",
            MinWidth = 180,
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(0, 0, 0, 16),
        };
        _fallbackAcquire.Click += FallbackAcquire_Click;
        _fallbackStatus = new TextBlock
        {
            Text = "Connect to the intended Allegro board, then acquire metadata.",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 760,
        };
        var panel = new StackPanel
        {
            Margin = new Thickness(28),
            Children = { title, explanation, _fallbackAcquire, _fallbackStatus },
        };
        WorkbenchHost.Content = panel;
    }

    private async void FallbackAcquire_Click(object sender, RoutedEventArgs e)
    {
        if (_fallbackBusy || _disposed)
        {
            return;
        }
        _fallbackBusy = true;
        RefreshState();
        StateChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            BridgeSession session = _session
                ?? throw new InvalidOperationException("Connect PD Simple to an Allegro board first.");
            _fallbackStatus!.Text = "Reading current board metadata through the Engine…";
            LiveDesignScene captured = await session.ReadEngineSceneAsync(SceneQuery.Metadata);
            captured.RequireCurrent();
            _fallbackStatus.Text = $"Current Engine capture: {captured.Scene.Document.Name}. Captured {captured.Scene.Identity.CapturedAt:yyyy-MM-dd HH:mm:ss}. No board edit was performed.";
        }
        catch (Exception error)
        {
            if (_fallbackStatus is not null)
            {
                _fallbackStatus.Text = "Engine metadata unavailable: " + error.Message;
            }
        }
        finally
        {
            _fallbackBusy = false;
            RefreshState();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private ValueTask<AllegroWorkspace> ProvideWorkspaceAsync(CancellationToken token) =>
        (_session ?? throw new InvalidOperationException("Connect PD Simple to an Allegro board first."))
            .GetEngineWorkspaceAsync(token);

    private ValueTask<AllegroReviewFrame> ProvideReviewCaptureAsync(
        LiveDesignScene scene,
        AnnotationScene annotations,
        CancellationToken token) =>
        (_session ?? throw new InvalidOperationException("Connect PD Simple to an Allegro board first."))
            .CaptureEngineReviewAsync(scene, annotations, token);

    private void Session_StateChanged(object? sender, SimpleSessionState state) =>
        Dispatcher.BeginInvoke(RefreshState);

    private void RefreshState()
    {
        if (_disposed)
        {
            return;
        }
        if (_workbench is not null)
        {
            SetProperty("LiveAvailable", _session?.HasLiveNativeSession == true);
            SetProperty("HostBusy", _session?.IsBusy == true);
            Invoke("RefreshHostState");
        }
        else if (_fallbackAcquire is not null)
        {
            _fallbackAcquire.IsEnabled = !_fallbackBusy && _session?.HasLiveNativeSession == true;
        }
    }

    private bool ReadBoolean(string name, bool fallback = false) =>
        _workbench?.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)?.GetValue(_workbench) as bool? ?? fallback;

    private string? ReadString(string name) =>
        _workbench?.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)?.GetValue(_workbench) as string;

    private void SetProperty(string name, object? value)
    {
        if (_workbench is null)
        {
            return;
        }
        PropertyInfo? property = _workbench.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        if (property?.CanWrite == true && (value is null || property.PropertyType.IsInstanceOfType(value)))
        {
            property.SetValue(_workbench, value);
        }
    }

    private void Subscribe(string name, Delegate handler)
    {
        if (_workbench is null)
        {
            return;
        }
        EventInfo? eventInfo = _workbench.GetType().GetEvent(name, BindingFlags.Instance | BindingFlags.Public);
        if (eventInfo?.EventHandlerType is { } eventType && eventType.IsInstanceOfType(handler))
        {
            eventInfo.AddEventHandler(_workbench, handler);
        }
    }

    private void Invoke(string name)
    {
        _workbench?.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public, Type.EmptyTypes)
            ?.Invoke(_workbench, null);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        if (_session is not null)
        {
            _session.StateChanged -= Session_StateChanged;
        }
        _session = null;
        if (_fallbackAcquire is not null)
        {
            _fallbackAcquire.Click -= FallbackAcquire_Click;
        }
        if (_workbench is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else if (_workbench is IDisposable disposable)
        {
            disposable.Dispose();
        }
        _disposed = true;
    }
}
