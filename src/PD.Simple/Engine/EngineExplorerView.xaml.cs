using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;
using Microsoft.Win32;

namespace PD.Simple.Engine;

public partial class EngineExplorerView : UserControl
{
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };
    private BridgeSession? _session;
    private LiveDesignScene? _live;
    private DesignScene? _scene;
    private CancellationTokenSource? _operation;
    private bool _busy;

    public EngineExplorerView()
    {
        InitializeComponent();
        Unloaded += (_, _) => _operation?.Cancel();
        UpdateActions();
    }

    public BridgeSession? Session
    {
        get => _session;
        set
        {
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
            UpdateActions();
        }
    }

    private void Session_StateChanged(object? sender, SimpleSessionState state) => Dispatcher.BeginInvoke(() =>
    {
        if (_live is { IsCurrent: false })
        {
            StatusText.Text = "The live document changed. This scene is now historical; highlight and zoom are disabled until you acquire again.";
        }
        UpdateActions();
    });

    private void AcquireMetadata_Click(object sender, RoutedEventArgs e) =>
        StartAcquire(SceneQuery.Metadata, "Reading lightweight Engine metadata…");

    private void AcquireBoard_Click(object sender, RoutedEventArgs e) =>
        StartAcquire(SceneQuery.CompleteBoard(includeContours: false), "Capturing one coherent Engine board scene…");

    private void StartAcquire(SceneQuery query, string status) => Run(async token =>
    {
        BridgeSession session = _session ?? throw new InvalidOperationException("Connect PD Simple to an Allegro board first.");
        StatusText.Text = status;
        LiveDesignScene live = await session.ReadEngineSceneAsync(query, token);
        Adopt(live.Scene, live);
        StatusText.Text = $"LIVE | {live.Scene.Document.Name} | captured {live.Scene.Identity.CapturedAt:O}";
    });

    private void OpenScene_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Allegro scene (*.allegroscene)|*.allegroscene",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            Run(async token =>
            {
                DesignScene scene = await BridgeSession.OpenEngineSceneAsync(dialog.FileName, token);
                Adopt(scene, null);
                StatusText.Text = $"OFFLINE | {scene.Document.Name} | captured {scene.Identity.CapturedAt:O}. Native actions are disabled.";
            });
        }
    }

    private void SaveScene_Click(object sender, RoutedEventArgs e)
    {
        if (_scene is not { } scene)
        {
            return;
        }
        var dialog = new SaveFileDialog
        {
            Filter = "Allegro scene (*.allegroscene)|*.allegroscene",
            DefaultExt = ".allegroscene",
            AddExtension = true,
            FileName = "captured-scene.allegroscene",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            Run(async token =>
            {
                await BridgeSession.SaveEngineSceneAsync(dialog.FileName, scene, overwrite: true, token);
                StatusText.Text = "Saved immutable Engine scene data. The file contains no live edit authority.";
            });
        }
    }

    private void Adopt(DesignScene scene, LiveDesignScene? live)
    {
        _scene = scene;
        _live = live;
        SceneView.SetScene(scene);
        Objects.SelectedItem = null;
        Details.Text = "Select an object. Relationships below are local to this captured scene and never trigger hidden native reads.";
        ApplyFilter();
        UpdateActions();
    }

    private void Family_Changed(object sender, SelectionChangedEventArgs e) => ApplyFilter();
    private void Search_Changed(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (Objects is null || Family is null || Search is null || _scene is not { } scene ||
            Family.SelectedItem is not ComboBoxItem { Tag: string tag })
        {
            return;
        }

        (DataFamily family, IEnumerable<Entry> entries) = tag switch
        {
            "components" => (DataFamily.Components, scene.Data.Components.Select(item =>
                new Entry(item.Refdes, item, scene.ReferenceTo(item.Id)))),
            "nets" => (DataFamily.Nets, scene.Data.Nets.Select(item =>
                new Entry(item.Name, item, scene.ReferenceTo(item.Id)))),
            "pairs" => (DataFamily.Connectivity, scene.Data.DifferentialPairs.Select(item =>
                new Entry(item.Name, item, scene.ReferenceTo(item.Id)))),
            "layers" => (DataFamily.Layers, scene.Data.Layers.Select(item =>
                new Entry(item.Id.Value, item, null))),
            "pins" => (DataFamily.Pins, scene.Data.Pins.Select(item =>
                new Entry(item.ComponentRefdes + "." + item.Number, item, scene.ReferenceTo(item.Id)))),
            _ => throw new InvalidOperationException("Unknown Engine object family.")
        };

        FamilyCoverage coverage = scene.Coverage[family];
        Entry[] filtered = entries
            .Where(item => item.Name.Contains(Search.Text ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Objects.ItemsSource = filtered;
        CoverageText.Text = $"{filtered.Length} captured | {coverage.Availability} | {coverage.Completeness}";
        if (!coverage.Reasons.IsDefaultOrEmpty)
        {
            CoverageText.Text += "\n" + string.Join(" ", coverage.Reasons);
        }
        UpdateActions();
    }

    private void Object_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_scene is not { } scene || Objects.SelectedItem is not Entry entry)
        {
            Details.Text = "Select an object.";
            UpdateActions();
            return;
        }

        var text = new StringBuilder(JsonSerializer.Serialize(entry.Value, entry.Value.GetType(), _json));
        switch (entry.Value)
        {
            case ComponentObject component:
                if (scene.Coverage[DataFamily.Pins].Availability == DataAvailability.Available)
                {
                    text.AppendLine("\nPins:");
                    foreach (PinObject pin in scene.Relations.PinsOf(component.Refdes))
                    {
                        text.AppendLine($"  {pin.Number}: {pin.NetName ?? "Disconnected"}");
                    }
                }
                break;
            case NetObject net when scene.Coverage[DataFamily.Connectivity].Availability == DataAvailability.Available:
                foreach (XnetObject xnet in scene.Relations.XnetsOf(net.Name))
                {
                    text.AppendLine($"\nXnet {xnet.Name}: {string.Join(", ", xnet.PhysicalNets)}");
                }
                foreach (DifferentialPairObject pair in scene.Relations.PairsOf(net.Name))
                {
                    text.AppendLine($"Declared pair: {pair.Name} ({pair.SideAXnet} / {pair.SideBXnet})");
                }
                break;
            case DifferentialPairObject pair:
                text.AppendLine("\nSide A/B is preserved exactly and does not imply positive/negative polarity.");
                if (scene.Coverage[DataFamily.Connectivity].Availability == DataAvailability.Available)
                {
                    text.AppendLine("Physical nets: " + string.Join(", ", scene.Relations.PhysicalNetsOf(pair)));
                }
                break;
        }
        Details.Text = text.ToString();
        if (entry.Reference is { } reference)
        {
            SceneView.Select([reference]);
        }
        UpdateActions();
    }

    private void Highlight_Click(object sender, RoutedEventArgs e) => Run(async token =>
    {
        (LiveDesignScene live, SceneObjectReference reference) = RequireLiveTarget();
        await (_session ?? throw new InvalidOperationException()).EngineHighlightAsync(live, reference, token);
        StatusText.Text = "Highlighted the current semantic Engine object in Allegro.";
    });

    private void Zoom_Click(object sender, RoutedEventArgs e) => Run(async token =>
    {
        (LiveDesignScene live, SceneObjectReference reference) = RequireLiveTarget();
        await (_session ?? throw new InvalidOperationException()).EngineZoomAsync(live, reference, token);
        StatusText.Text = "Zoomed Allegro to the current semantic Engine object.";
    });

    private (LiveDesignScene Live, SceneObjectReference Reference) RequireLiveTarget()
    {
        if (_live is not { IsCurrent: true } live || Objects.SelectedItem is not Entry { Reference: { } reference })
        {
            throw new InvalidOperationException("Select a supported object from a current live Engine scene.");
        }
        return (live, reference);
    }

    private void Run(Func<CancellationToken, Task> work)
    {
        if (_busy)
        {
            return;
        }
        _operation?.Dispose();
        _operation = new CancellationTokenSource();
        _ = RunCore(work, _operation.Token);
    }

    private async Task RunCore(Func<CancellationToken, Task> work, CancellationToken token)
    {
        _busy = true;
        UpdateActions();
        try
        {
            await work(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            StatusText.Text = "Engine operation wait cancelled. No native cancellation or rollback is implied.";
        }
        catch (Exception error)
        {
            StatusText.Text = "Engine operation unavailable: " + error.Message;
        }
        finally
        {
            _busy = false;
            UpdateActions();
        }
    }

    private void UpdateActions()
    {
        if (AcquireMetadataButton is null)
        {
            return;
        }
        bool connected = _session?.HasLiveNativeSession == true;
        AcquireMetadataButton.IsEnabled = !_busy && connected;
        AcquireBoardButton.IsEnabled = !_busy && connected;
        SaveSceneButton.IsEnabled = !_busy && _scene is not null;
        bool liveTarget = !_busy && _live is { IsCurrent: true } && Objects.SelectedItem is Entry { Reference: not null };
        HighlightButton.IsEnabled = liveTarget;
        ZoomButton.IsEnabled = liveTarget;
    }

    private sealed record Entry(string Name, object Value, SceneObjectReference? Reference);
}
