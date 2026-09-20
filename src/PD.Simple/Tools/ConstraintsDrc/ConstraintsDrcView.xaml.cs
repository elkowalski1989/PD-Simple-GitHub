using System.Windows;
using System.Windows.Controls;
using CircuitHub.AllegroBridge.Engine.Live;

namespace PD.Simple.Tools.ConstraintsDrc;

/// <summary>
/// Thin WPF host for the Constraints/DRC task. All policy, gating, and
/// Engine access live in <see cref="ConstraintsDrcViewModel"/>; this view
/// only attaches the shared Engine session, forwards button clicks, and
/// owns the view-model lifetime. It never creates or disposes an Engine
/// session.
/// </summary>
public partial class ConstraintsDrcView : UserControl, IDisposable
{
    private ConstraintsDrcViewModel? _model;
    private bool _disposed;

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

        var model = new ConstraintsDrcViewModel(session);
        _model = model;
        DataContext = model;
    }

    private async void RefreshConstraints_Click(object sender, RoutedEventArgs e)
    {
        if (_model is not null)
        {
            await _model.RefreshConstraintSnapshotAsync().ConfigureAwait(true);
        }
    }

    private void ObserveConstraints_Click(object sender, RoutedEventArgs e) =>
        _model?.ObserveConstraintSnapshot();

    private async void ReadMarkers_Click(object sender, RoutedEventArgs e)
    {
        if (_model is not null)
        {
            await _model.ReadDrcMarkersAsync().ConfigureAwait(true);
        }
    }

    private async void RunDrc_Click(object sender, RoutedEventArgs e)
    {
        if (_model is not null)
        {
            await _model.RunDrcAsync().ConfigureAwait(true);
        }
    }

    private async void ReadEffective_Click(object sender, RoutedEventArgs e)
    {
        if (_model is null)
        {
            return;
        }
        if (_model.SelectedValue is null)
        {
            return;
        }
        if (!Enum.TryParse<EngineConstraintScalarKind>(_model.QueryKindText, ignoreCase: true, out EngineConstraintScalarKind kind))
        {
            return;
        }
        if (!Enum.TryParse<EngineConstraintUnit>(_model.QueryUnitText, ignoreCase: true, out EngineConstraintUnit unit))
        {
            return;
        }
        EngineConstraintQuery query =
            ConstraintsDrcViewModel.BuildEffectiveQuery(_model.SelectedValue, kind, unit);
        await _model.ReadEffectiveAsync(query).ConfigureAwait(true);
    }

    private async void EditConstraint_Click(object sender, RoutedEventArgs e)
    {
        if (_model is null)
        {
            return;
        }
        if (_model.HasPreparedEdit)
        {
            await _model.ExecuteEditAsync().ConfigureAwait(true);
            return;
        }
        if (_model.SelectedValue is null)
        {
            return;
        }
        if (!Enum.TryParse<EngineConstraintScalarKind>(_model.QueryKindText, ignoreCase: true, out EngineConstraintScalarKind kind))
        {
            return;
        }
        if (!Enum.TryParse<EngineConstraintUnit>(_model.QueryUnitText, ignoreCase: true, out EngineConstraintUnit unit))
        {
            return;
        }
        if (!Enum.TryParse<EngineConstraintChangeKind>(_model.EditChangeKindText, ignoreCase: true, out EngineConstraintChangeKind changeKind))
        {
            return;
        }
        EngineConstraintQuery query =
            ConstraintsDrcViewModel.BuildEffectiveQuery(_model.SelectedValue, kind, unit);
        EngineConstraintChange change = changeKind == EngineConstraintChangeKind.SetValue
            ? ConstraintsDrcViewModel.BuildChange(
                changeKind,
                ConstraintsDrcViewModel.BuildScalar(kind, unit, _model.NewValueText),
                _model.SelectedValue.SetName)
            : ConstraintsDrcViewModel.BuildChange(changeKind, constraintSet: _model.SelectedValue.SetName);
        await _model.PrepareEditAsync(query, change).ConfigureAwait(true);
    }

    private async void RecoverEdit_Click(object sender, RoutedEventArgs e)
    {
        if (_model is not null)
        {
            await _model.RecoverEditAsync().ConfigureAwait(true);
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
}
