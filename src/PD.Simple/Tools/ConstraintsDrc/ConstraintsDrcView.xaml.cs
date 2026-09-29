using System.Windows;
using System.Windows.Controls;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Wpf.Engine;

namespace PD.Simple.Tools.ConstraintsDrc;

/// <summary>
/// Thin WPF host for the Constraints/DRC task. All policy, gating, and
/// Engine access live in <see cref="ConstraintsDrcViewModel"/>; this view
/// only attaches the shared Engine session, forwards button clicks, and
/// owns the view-model lifetime. It never creates or disposes an Engine
/// session.
/// </summary>
public partial class ConstraintsDrcView : UserControl, IDisposable, IAsyncDisposable
{
    private ConstraintsDrcViewModel? _model;
    private bool _disposed;
    private EngineWorkspaceRecoveryCoordinator? _recovery;
    private EngineWorkspaceRecoveryEntry? _recoveryEntry;
    public Func<EngineWorkspaceRecoveryCoordinator?>? RecoveryProvider { get; set; }
    public bool CanClose => _model is null || (!_model.IsBusy &&
        _model.PendingMutation is not { IsTerminal: false } &&
        _recoveryEntry?.Snapshot.Category is not (EngineWorkspaceRecoveryCategory.Running or EngineWorkspaceRecoveryCategory.RecoveryRunning));

    public ConstraintsDrcView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Attaches the application's one shared Engine session. May be called
    /// while disconnected: the page opens in offline setup mode and live
    /// actions stay gated until the session is ready.
    /// </summary>
    public void AttachSession(AllegroEngineSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_model is not null)
        {
            throw new InvalidOperationException(
                "The Constraints/DRC view is already attached to an Engine session.");
        }

        var model = new ConstraintsDrcViewModel(session, action =>
            Dispatcher.CheckAccess() ? action() : Dispatcher.InvokeAsync(action).Task.Unwrap());
        _model = model;
        model.MutationOwnerRetained = (operation, observeResult) =>
        {
            _recovery = RecoveryProvider?.Invoke();
            if (_recovery is not null)
            {
                _recoveryEntry = _recovery.TrackConstraint(operation, observeResult);
                model.SharedRecovery = async token =>
                {
                    await _recoveryEntry.CheckResultAsync(token);
                    await _recoveryEntry.RecoverAsync(token);
                };
            }
        };
        model.MutationOwnerSettled = async () =>
        {
            if (_recoveryEntry is not null)
            {
                await _recoveryEntry.CheckResultAsync();
            }
        };
        model.MutationOwnerReleasing = _ =>
        {
            if (_recoveryEntry is not null)
            {
                _recovery!.ReleaseTracking(_recoveryEntry);
                _recoveryEntry = null;
                model.SharedRecovery = null;
            }
        };
        DataContext = model;
    }

    private async void RefreshConstraints_Click(object sender, RoutedEventArgs e)
    {
        if (_model is not null)
        {
            await RunAsync(() => _model.RefreshConstraintSnapshotAsync());
        }
    }

    private void ObserveConstraints_Click(object sender, RoutedEventArgs e) =>
        _model?.ObserveConstraintSnapshot();

    private async void ReadMarkers_Click(object sender, RoutedEventArgs e)
    {
        if (_model is not null)
        {
            await RunAsync(() => _model.ReadDrcMarkersAsync());
        }
    }

    private async void RunDrc_Click(object sender, RoutedEventArgs e)
    {
        if (_model is not null)
        {
            await RunAsync(() => _model.RunDrcAsync());
        }
    }

    private async void ReadEffective_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            if (_model is not null && _model.TryBuildEffectiveInput(out EngineConstraintQuery? query))
            {
                await _model.ReadEffectiveAsync(query);
            }
        });
    }

    private async void EditConstraint_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            if (_model is null)
            {
                return;
            }
            if (_model.HasPreparedEdit)
            {
                await _model.ExecuteEditAsync();
                return;
            }
            if (_model.TryBuildEditInput(out EngineConstraintQuery? query, out EngineConstraintChange? change))
            {
                await _model.PrepareEditAsync(query, change);
            }
        });
    }

    private async void RecoverEdit_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(() => _model?.RecoverEditAsync() ?? Task.CompletedTask);
    }

    private async Task RunAsync(Func<Task> action)
    {
        ConstraintsDrcViewModel? owner = _model;
        Action<Exception>? reportFailure = owner?.CaptureOperationFailureReporter();
        try
        {
            Task operation = action();
            reportFailure = owner?.CaptureOperationFailureReporter();
            await operation;
        }
        catch (Exception error)
        {
            reportFailure?.Invoke(error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _model?.CancelPending();

    private void MarkReviewed_Click(object sender, RoutedEventArgs e) =>
        _model?.MarkSelectedReviewed(ReviewNoteBox.Text);

    private void CopyExport_Click(object sender, RoutedEventArgs e)
    {
        if (_model is not null && _model.ExportText.Length > 0)
        {
            Clipboard.SetText(_model.ExportText);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _model?.Dispose();
        _model = null;
        DataContext = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_model is not null)
        {
            if (!CanClose)
            {
                throw new InvalidOperationException("The constraint operation or its recovery must reach a terminal result before closing.");
            }
            await _model.ReleaseRetainedOperationAsync();
        }
        Dispose();
    }
}
