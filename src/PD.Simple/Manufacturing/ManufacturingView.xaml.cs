using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Manufacturing;
using PD.PcbTools.Manufacturing;

namespace PD.Simple.Manufacturing;

/// <summary>
/// Manufacturing task page. Opens while disconnected with setup guidance;
/// every action is gated by source, plan, result and approval state with a
/// visible reason. PD owns navigation and policy; Engine owns planning,
/// staging, validation and promotion primitives.
/// </summary>
public partial class ManufacturingView : UserControl
{
    private readonly ManufacturingPageModel _model = new();
    private readonly List<(CheckBox Box, Ipc2581Content Flag)> _contentBoxes = [];
    private BridgeSession? _session;
    private CancellationTokenSource? _runCancellation;

    private ArtworkJobPlan? _artworkPlan;
    private OdbPlusPlusJobPlan? _odbPlan;
    private Ipc2581JobPlan? _ipcPlan;

    public ManufacturingView()
    {
        InitializeComponent();
        MfgArtworkFormatCombo.ItemsSource = Enum.GetNames<ArtworkGerberFormat>();
        MfgArtworkFormatCombo.SelectedIndex = 0;
        MfgArtworkUnitsCombo.ItemsSource = Enum.GetNames<ArtworkCoordinateUnits>();
        MfgArtworkUnitsCombo.SelectedIndex = 0;
        MfgOdbPadflashCombo.ItemsSource = Enum.GetNames<OdbPlusPlusPadflashHandling>();
        MfgOdbPadflashCombo.SelectedIndex = 0;
        MfgOdbOutlineCombo.ItemsSource = Enum.GetNames<OdbPlusPlusComponentOutlineSource>();
        MfgOdbOutlineCombo.SelectedIndex = 0;
        MfgIpcRevisionCombo.ItemsSource = Enum.GetNames<Ipc2581Revision>();
        MfgIpcRevisionCombo.SelectedIndex = 1;
        MfgIpcUnitsCombo.ItemsSource = Enum.GetNames<Ipc2581Units>();
        MfgIpcUnitsCombo.SelectedIndex = 1;
        (string Label, Ipc2581Content Flag)[] contents =
        [
            ("Device descriptions", Ipc2581Content.DeviceDescriptions),
            ("Bill of materials", Ipc2581Content.BillOfMaterials),
            ("Layer stackup", Ipc2581Content.LayerStackup),
            ("Regular drill", Ipc2581Content.RegularDrillLayers),
            ("Backdrill", Ipc2581Content.BackdrillLayers),
            ("Netlist", Ipc2581Content.LogicalAndPhysicalNetlist),
            ("Packages", Ipc2581Content.ComponentPackages),
            ("Land patterns", Ipc2581Content.DeviceLandPatterns),
            ("Assembly", Ipc2581Content.ComponentAssembly),
            ("Documentation", Ipc2581Content.DocumentationLayers),
            ("Outer copper", Ipc2581Content.OuterCopperLayers),
            ("Inner copper", Ipc2581Content.InnerCopperLayers),
            ("Misc fab", Ipc2581Content.MiscellaneousFabricationLayers),
            ("Mask/paste/legend", Ipc2581Content.SolderMaskPasteAndLegendLayers),
            ("Padstacks", Ipc2581Content.PadstackDefinitions),
            ("Cavities", Ipc2581Content.Cavities),
            ("Vector text", Ipc2581Content.VectorText),
            ("Board profile (Rev C)", Ipc2581Content.BoardProfile),
        ];
        foreach (var (label, flag) in contents)
        {
            var box = new CheckBox
            {
                Content = label,
                Tag = (int)flag,
            };
            box.SetValue(AutomationProperties.AutomationIdProperty, "MfgContent_" + label.Replace(" ", string.Empty));
            _contentBoxes.Add((box, flag));
            MfgContentPanel.Children.Add(box);
        }
        _contentBoxes.First(item => item.Flag == Ipc2581Content.LayerStackup).Box.IsChecked = true;
        _contentBoxes.First(item => item.Flag == Ipc2581Content.OuterCopperLayers).Box.IsChecked = true;
        Unloaded += (_, _) => _runCancellation?.Cancel();
        RefreshActionStates("Attach a session and build a plan to enable generation.");
    }

    public IManufacturingExportRunner Runner { get; set; } = new UnqualifiedManufacturingRunner();

    public void Attach(BridgeSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        RefreshFromSession();
    }

    public void RefreshFromSession()
    {
        try
        {
            if (_session is null)
            {
                _model.SetConnected(false);
                MfgSourceStatusText.Text = "No session attached.";
            }
            else
            {
                _model.SetConnected(_session.HasReadySession);
                _model.RefreshSource(() => _session.Workspace.Manufacturing.CaptureSource());
                MfgSourceStatusText.Text = _session.HasReadySession
                    ? _model.SourceStatus.Detail + $" Design: {_session.State.Design}."
                    : "Session is not ready. " + _model.SourceStatus.Detail;
            }
        }
        catch (Exception exception)
        {
            MfgSourceStatusText.Text = "Source refresh failed: " + exception.Message;
        }
        RefreshActionStates(null);
    }

    private enum ActiveFormat
    {
        Artwork,
        Odb,
        Ipc
    }

    private ActiveFormat CurrentFormat =>
        MfgFormatOdb.IsChecked == true ? ActiveFormat.Odb :
        MfgFormatIpc.IsChecked == true ? ActiveFormat.Ipc : ActiveFormat.Artwork;

    private void MfgFormat_Checked(object sender, RoutedEventArgs args)
    {
        if (MfgArtworkPanel is null)
        {
            return;
        }
        MfgArtworkPanel.Visibility = CurrentFormat == ActiveFormat.Artwork ? Visibility.Visible : Visibility.Collapsed;
        MfgOdbPanel.Visibility = CurrentFormat == ActiveFormat.Odb ? Visibility.Visible : Visibility.Collapsed;
        MfgIpcPanel.Visibility = CurrentFormat == ActiveFormat.Ipc ? Visibility.Visible : Visibility.Collapsed;
        ClearPlans();
        RefreshActionStates("Format changed; build a plan to enable generation.");
    }

    private void MfgRefreshSourceButton_Click(object sender, RoutedEventArgs args) => RefreshFromSession();

    private void ClearPlans()
    {
        _artworkPlan = null;
        _odbPlan = null;
        _ipcPlan = null;
        MfgManifestList.ItemsSource = null;
    }

    private void MfgBuildPlanButton_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            ClearPlans();
            if (!_model.CanPlan(out string planReason))
            {
                MfgPlanStatusText.Text = planReason;
                RefreshActionStates(planReason);
                return;
            }
            if (!TryReadCommon(out string approvedRoot, out string destination, out bool replace, out string error))
            {
                MfgPlanStatusText.Text = error;
                RefreshActionStates(error);
                return;
            }
            switch (CurrentFormat)
            {
                case ActiveFormat.Artwork:
                    BuildArtworkPlan(destination, replace);
                    break;
                case ActiveFormat.Odb:
                    BuildOdbPlan(destination, replace);
                    break;
                default:
                    BuildIpcPlan(destination, replace);
                    break;
            }
        }
        catch (Exception exception)
        {
            MfgPlanStatusText.Text = "Plan failed: " + exception.Message;
            RefreshActionStates(MfgPlanStatusText.Text);
        }
    }

    private bool TryReadCommon(out string approvedRoot, out string destination, out bool replace, out string error)
    {
        approvedRoot = MfgApprovedRootText.Text.Trim();
        destination = MfgDestinationText.Text.Trim();
        replace = MfgReplaceCheck.IsChecked == true;
        error = string.Empty;
        if (approvedRoot.Length == 0 || !Directory.Exists(approvedRoot))
        {
            error = "Choose an existing approved root directory first.";
            return false;
        }
        if (destination.Length == 0)
        {
            error = "Enter a relative destination directory.";
            return false;
        }
        return true;
    }

    private void BuildArtworkPlan(string destination, bool replace)
    {
        var films = new List<ManufacturingFilmInput>();
        foreach (string rawLine in MfgFilmsText.Text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }
            string[] cells = line.Split('|');
            if (cells.Length != 5)
            {
                FailPlan("Each film line needs 5 cells: name | layers | artifact | polarity | mirror.");
                return;
            }
            string polarity = cells[3].Trim();
            bool negative = polarity is "-" or "negative" or "Negative" or "NEGATIVE";
            if (!negative && polarity is not ("+" or "positive" or "Positive" or "POSITIVE"))
            {
                FailPlan($"Film '{cells[0].Trim()}' has unknown polarity '{polarity}'.");
                return;
            }
            if (!bool.TryParse(cells[4].Trim(), out bool mirror))
            {
                FailPlan($"Film '{cells[0].Trim()}' has unknown mirror flag '{cells[4].Trim()}'.");
                return;
            }
            films.Add(new(cells[0].Trim(), cells[1].Trim(), cells[2].Trim(), negative, mirror));
        }
        if (films.Count == 0)
        {
            FailPlan("Enter at least one film.");
            return;
        }
        if (!Enum.TryParse<ArtworkGerberFormat>(MfgArtworkFormatCombo.SelectedItem as string, out ArtworkGerberFormat format) ||
            !Enum.TryParse<ArtworkCoordinateUnits>(MfgArtworkUnitsCombo.SelectedItem as string, out ArtworkCoordinateUnits units))
        {
            FailPlan("Choose a Gerber format and units.");
            return;
        }
        decimal? offset = ParseOptionalDecimal(MfgOutlineOffsetText.Text, "outline offset", out string offsetError);
        if (offsetError.Length != 0)
        {
            FailPlan(offsetError);
            return;
        }
        int? aperture = ParseOptionalInt(MfgMinApertureText.Text, "minimum aperture", out string apertureError);
        if (apertureError.Length != 0)
        {
            FailPlan(apertureError);
            return;
        }
        var options = new ArtworkOptions(format, units, MfgSuppressFillCheck.IsChecked == true,
            MfgVectorPadCheck.IsChecked == true, offset, aperture);
        var (plan, planError) = _model.TryBuildArtwork(films, options, destination, replace);
        if (plan is null)
        {
            FailPlan(planError ?? "The Artwork plan was rejected.");
            return;
        }
        if (MfgArtParamText.Text.Trim().Length == 0)
        {
            FailPlan("Paste the caller-owned art_param.txt contents; the native exporter requires it.");
            return;
        }
        _artworkPlan = plan;
        MfgPlanStatusText.Text = $"Artwork plan {plan.JobId:N} with {plan.Films.Length} film(s) ready.";
        MfgManifestList.ItemsSource = ManufacturingPageModel.PreviewArtworkManifest(plan);
        RefreshActionStates(null);
    }

    private void BuildOdbPlan(string destination, bool replace)
    {
        if (!Enum.TryParse<OdbPlusPlusPadflashHandling>(MfgOdbPadflashCombo.SelectedItem as string, out OdbPlusPlusPadflashHandling padflash) ||
            !Enum.TryParse<OdbPlusPlusComponentOutlineSource>(MfgOdbOutlineCombo.SelectedItem as string, out OdbPlusPlusComponentOutlineSource outline))
        {
            FailPlan("Choose ODB++ padflash and outline options.");
            return;
        }
        string thermalFile = MfgOdbThermalFileText.Text.Trim();
        string thermalName = MfgOdbThermalNameText.Text.Trim();
        var options = new OdbPlusPlusOptions(
            OdbPlusPlusOutputMode.Directory, null, null, padflash, outline,
            thermalFile.Length == 0 ? null : new(thermalFile),
            thermalName.Length == 0 ? null : thermalName,
            false, true);
        var (plan, planError) = _model.TryBuildOdbPlusPlus(
            MfgOdbStepText.Text.Trim(), MfgOdbLayersText.Text, options, destination, replace);
        if (plan is null)
        {
            FailPlan(planError ?? "The ODB++ plan was rejected.");
            return;
        }
        _odbPlan = plan;
        MfgPlanStatusText.Text = $"ODB++ plan {plan.JobId:N} for step '{plan.StepName}' ready.";
        MfgManifestList.ItemsSource = ManufacturingPageModel.PreviewOdbManifest(plan);
        RefreshActionStates(null);
    }

    private void BuildIpcPlan(string destination, bool replace)
    {
        if (!Enum.TryParse<Ipc2581Revision>(MfgIpcRevisionCombo.SelectedItem as string, out Ipc2581Revision revision) ||
            !Enum.TryParse<Ipc2581Units>(MfgIpcUnitsCombo.SelectedItem as string, out Ipc2581Units units))
        {
            FailPlan("Choose an IPC-2581 revision and units.");
            return;
        }
        Ipc2581Content content = Ipc2581Content.None;
        foreach (var (box, flag) in _contentBoxes)
        {
            if (box.IsChecked == true)
            {
                content |= flag;
            }
        }
        string propertyPath = MfgIpcPropertyPathText.Text.Trim();
        string propertyText = MfgIpcPropertyText.Text;
        string mappingPath = MfgIpcMappingPathText.Text.Trim();
        string mappingText = MfgIpcMappingText.Text;
        if ((propertyPath.Length == 0) != (propertyText.Trim().Length == 0))
        {
            FailPlan("Property configuration needs both a staging path and its text, or neither.");
            return;
        }
        if ((mappingPath.Length == 0) != (mappingText.Trim().Length == 0))
        {
            FailPlan("Layer mapping needs both a staging path and its text, or neither.");
            return;
        }
        var options = new Ipc2581Options(
            revision, units, content,
            propertyPath.Length == 0 ? null : new(propertyPath),
            mappingPath.Length == 0 ? null : new(mappingPath));
        var (plan, planError) = _model.TryBuildIpc2581(
            MfgIpcLayersText.Text, MfgIpcArtifactText.Text.Trim(), options, destination, replace);
        if (plan is null)
        {
            FailPlan(planError ?? "The IPC-2581 plan was rejected.");
            return;
        }
        _ipcPlan = plan;
        MfgPlanStatusText.Text = $"IPC-2581 plan {plan.JobId:N} ({revision}) ready.";
        MfgManifestList.ItemsSource = ManufacturingPageModel.PreviewIpcManifest(plan);
        RefreshActionStates(null);
    }

    private void FailPlan(string error)
    {
        ClearPlans();
        MfgPlanStatusText.Text = error;
        RefreshActionStates(error);
    }

    private static decimal? ParseOptionalDecimal(string text, string role, out string error)
    {
        error = string.Empty;
        text = text.Trim();
        if (text.Length == 0)
        {
            return null;
        }
        if (decimal.TryParse(text, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out decimal value) && value >= 0)
        {
            return value;
        }
        error = $"The {role} must be a non-negative number.";
        return null;
    }

    private static int? ParseOptionalInt(string text, string role, out string error)
    {
        error = string.Empty;
        text = text.Trim();
        if (text.Length == 0)
        {
            return null;
        }
        if (int.TryParse(text, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int value) && value >= 0)
        {
            return value;
        }
        error = $"The {role} must be a non-negative integer.";
        return null;
    }

    private async void MfgRunButton_Click(object sender, RoutedEventArgs args)
    {
        _runCancellation?.Cancel();
        _runCancellation = new CancellationTokenSource();
        try
        {
            if (!TryReadRunContext(out ManufacturingRunnerContext context, out string contextError))
            {
                MfgProgressText.Text = contextError;
                return;
            }
            MfgRunButton.IsEnabled = false;
            MfgCancelButton.IsEnabled = true;
            MfgProgressText.Text = "Generating into isolated staging…";
            MfgResultText.Text = string.Empty;
            switch (CurrentFormat)
            {
                case ActiveFormat.Artwork when _artworkPlan is not null:
                    await RunOnBackgroundAsync(token =>
                        _model.RunArtworkAsync(_artworkPlan, context, Runner, token)).ConfigureAwait(true);
                    ShowArtworkResult();
                    break;
                case ActiveFormat.Odb when _odbPlan is not null:
                    await RunOnBackgroundAsync(token =>
                        _model.RunOdbPlusPlusAsync(_odbPlan, context, Runner, token)).ConfigureAwait(true);
                    ShowOdbResult();
                    break;
                case ActiveFormat.Ipc when _ipcPlan is not null:
                    await RunOnBackgroundAsync(token =>
                        _model.RunIpc2581Async(_ipcPlan, context, Runner, token)).ConfigureAwait(true);
                    ShowIpcResult();
                    break;
                default:
                    MfgProgressText.Text = "Build a plan for the selected format first.";
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            MfgProgressText.Text = "Cancelled. Staging is retained for inspection; release it or run again.";
        }
        catch (Exception exception)
        {
            MfgProgressText.Text = "Generation failed: " + exception.Message;
        }
        finally
        {
            MfgCancelButton.IsEnabled = false;
            RefreshActionStates(null);
        }
    }

    private Task<T> RunOnBackgroundAsync<T>(Func<CancellationToken, Task<T>> run)
    {
        CancellationTokenSource cancellation = _runCancellation ?? new();
        return Task.Run(() => run(cancellation.Token), cancellation.Token);
    }

    private bool TryReadRunContext(out ManufacturingRunnerContext context, out string error)
    {
        context = null!;
        error = string.Empty;
        string approvedRoot = MfgApprovedRootText.Text.Trim();
        if (approvedRoot.Length == 0 || !Directory.Exists(approvedRoot))
        {
            error = "Choose an existing approved root directory first.";
            return false;
        }
        if (!double.TryParse(MfgTimeoutText.Text.Trim(), System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out double minutes) ||
            minutes < 1 || minutes > 360)
        {
            error = "Timeout must be 1 to 360 minutes.";
            return false;
        }
        context = new(
            approvedRoot, null, TimeSpan.FromMinutes(minutes),
            NullIfBlank(MfgArtParamText.Text), NullIfBlank(MfgArtAperText.Text),
            NullIfBlank(MfgIpcPropertyText.Text), NullIfBlank(MfgIpcMappingText.Text));
        return true;
    }

    private static string? NullIfBlank(string value) =>
        value.Trim().Length == 0 ? null : value;

    private void ShowArtworkResult()
    {
        if (_model.LastArtwork is null)
        {
            return;
        }
        MfgProgressText.Text = "Generation finished.";
        MfgResultText.Text = ManufacturingPageModel.SummarizeArtwork(_model.LastArtwork.Result);
        DescribeStaging(_model.LastArtwork.Result.JobId, _model.LastArtwork.Result.State, _model.LastArtwork.Staging);
    }

    private void ShowOdbResult()
    {
        if (_model.LastOdbPlusPlus is null)
        {
            return;
        }
        MfgProgressText.Text = "Generation finished.";
        MfgResultText.Text = ManufacturingPageModel.SummarizeOdb(_model.LastOdbPlusPlus.Result);
        MfgPromotionText.Text = _model.LastOdbPlusPlus.Result.State == ManufacturingOutputState.NoGo
            ? "No staging is produced for an unobservable native operation; resolve the headless contract first."
            : string.Empty;
    }

    private void ShowIpcResult()
    {
        if (_model.LastIpc2581 is null)
        {
            return;
        }
        MfgProgressText.Text = "Generation finished.";
        MfgResultText.Text = ManufacturingPageModel.SummarizeIpc(_model.LastIpc2581.Result);
        DescribeStaging(_model.LastIpc2581.Result.JobId, _model.LastIpc2581.Result.State, _model.LastIpc2581.Staging);
    }

    private void DescribeStaging(Guid jobId, ManufacturingOutputState state, ManufacturingStagingArea? staging)
    {
        if (!_model.CanPromote(jobId, state, staging is not null, out string reason))
        {
            MfgPromotionText.Text = staging is null
                ? reason + " Staging was released or never created."
                : reason + " Staging is retained for inspection; release it when done.";
            return;
        }
        string[] files = ListStagedFiles(staging!);
        MfgPromotionText.Text = $"Staged {files.Length} file(s); approve promotion to replace or create the destination. " +
            string.Join(", ", files.Take(12)) + (files.Length > 12 ? $" (+{files.Length - 12} more)" : string.Empty);
    }

    private static string[] ListStagedFiles(ManufacturingStagingArea staging)
    {
        try
        {
            return Directory.EnumerateFiles(staging.StageDirectory, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(staging.StageDirectory, path).Replace(Path.DirectorySeparatorChar, '/'))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Take(10000)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void MfgCancelButton_Click(object sender, RoutedEventArgs args) => _runCancellation?.Cancel();

    private void MfgPromoteButton_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            (Guid jobId, ManufacturingOutputState state, ManufacturingStagingArea? staging) = CurrentExecution();
            if (staging is null)
            {
                MfgPromotionText.Text = "No staged output is held for promotion.";
                return;
            }
            string destination = _model.Promote(jobId, state, staging);
            MfgPromotionText.Text = "Promoted to " + destination;
            RefreshActionStates(null);
        }
        catch (Exception exception)
        {
            MfgPromotionText.Text = "Promotion refused: " + exception.Message;
        }
    }

    private void MfgReleaseButton_Click(object sender, RoutedEventArgs args)
    {
        _model.ReleaseStaging();
        MfgPromotionText.Text = "Staging released without promotion.";
        RefreshActionStates(null);
    }

    private void MfgSaveManifestButton_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            (Guid jobId, ManufacturingOutputState state, ManufacturingStagingArea? _) = CurrentExecution();
            string summary = CurrentFormat switch
            {
                ActiveFormat.Artwork when _model.LastArtwork is not null =>
                    ManufacturingPageModel.SummarizeArtwork(_model.LastArtwork.Result),
                ActiveFormat.Odb when _model.LastOdbPlusPlus is not null =>
                    ManufacturingPageModel.SummarizeOdb(_model.LastOdbPlusPlus.Result),
                ActiveFormat.Ipc when _model.LastIpc2581 is not null =>
                    ManufacturingPageModel.SummarizeIpc(_model.LastIpc2581.Result),
                _ => "No result for the selected format.",
            };
            var report = new
            {
                tool = "Manufacturing/" + CurrentFormat,
                jobId = jobId.ToString("N"),
                at = DateTimeOffset.UtcNow,
                source = _model.SourceStatus.Capture?.SourceSha256,
                snapshotRevision = _model.SourceStatus.Capture?.SnapshotRevision,
                plan = MfgPlanStatusText.Text,
                result = summary,
                promotion = MfgPromotionText.Text,
            };
            string directory = MfgApprovedRootText.Text.Trim();
            if (directory.Length == 0 || !Directory.Exists(directory))
            {
                MfgPromotionText.Text = "Choose an existing approved root to save the manifest.";
                return;
            }
            string path = Path.Combine(directory, $"manufacturing-manifest-{jobId:N}.json");
            if (File.Exists(path))
            {
                MfgPromotionText.Text = "Manifest already saved: " + path;
                return;
            }
            File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            MfgPromotionText.Text = "Manifest saved: " + path;
        }
        catch (Exception exception)
        {
            MfgPromotionText.Text = "Manifest save failed: " + exception.Message;
        }
    }

    private (Guid JobId, ManufacturingOutputState State, ManufacturingStagingArea? Staging) CurrentExecution() =>
        CurrentFormat switch
        {
            ActiveFormat.Artwork when _model.LastArtwork is not null =>
                (_model.LastArtwork.Result.JobId, _model.LastArtwork.Result.State, _model.LastArtwork.Staging),
            ActiveFormat.Odb when _model.LastOdbPlusPlus is not null =>
                (_model.LastOdbPlusPlus.Result.JobId, _model.LastOdbPlusPlus.Result.State, null),
            ActiveFormat.Ipc when _model.LastIpc2581 is not null =>
                (_model.LastIpc2581.Result.JobId, _model.LastIpc2581.Result.State, _model.LastIpc2581.Staging),
            _ => (Guid.Empty, ManufacturingOutputState.NoGo, null),
        };

    private void RefreshActionStates(string? reason)
    {
        bool hasPlan = (_artworkPlan, _odbPlan, _ipcPlan) switch
        {
            (not null, _, _) when CurrentFormat == ActiveFormat.Artwork => true,
            (_, not null, _) when CurrentFormat == ActiveFormat.Odb => true,
            (_, _, not null) when CurrentFormat == ActiveFormat.Ipc => true,
            _ => false,
        };
        bool running = _runCancellation is not null && MfgCancelButton.IsEnabled;
        MfgRunButton.IsEnabled = hasPlan && !running;
        (Guid jobId, ManufacturingOutputState state, ManufacturingStagingArea? staging) = CurrentExecution();
        bool hasResult = jobId != Guid.Empty;
        MfgPromoteButton.IsEnabled = hasResult && _model.CanPromote(jobId, state, staging is not null, out _);
        MfgReleaseButton.IsEnabled = staging is not null;
        MfgSaveManifestButton.IsEnabled = hasResult;
        if (reason is not null)
        {
            MfgActionReasonText.Text = reason;
        }
        else if (!hasPlan)
        {
            MfgActionReasonText.Text = "Build a plan to enable generation.";
        }
        else if (!MfgPromoteButton.IsEnabled && hasResult)
        {
            _ = _model.CanPromote(jobId, state, staging is not null, out string promoteReason);
            MfgActionReasonText.Text = promoteReason;
        }
        else
        {
            MfgActionReasonText.Text = "Plan ready. Generation runs the bound exporter into isolated staging.";
        }
    }
}
