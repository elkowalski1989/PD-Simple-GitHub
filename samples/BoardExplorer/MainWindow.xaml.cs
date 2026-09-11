using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using CircuitHub.AllegroBridge;
using CircuitHub.AllegroBridge.Windows;

namespace BoardExplorer;

/// <summary>A package-only consumer. No native database IDs, SKILL, or private SDK access.</summary>
public partial class MainWindow : Window
{
    private readonly CancellationTokenSource _lifetime = new();
    private AllegroDesktopAttachment? _attachment;
    private AllegroPcbSession? _pcb;
    private AllegroPcbCatalog? _catalog;
    private bool _busy;
    private bool _closed;
    private bool _closeReady;
    private Task _pending = Task.CompletedTask;

    public MainWindow()
    {
        InitializeComponent();
        UpdateActions();
        Closing += async (_, args) =>
        {
            if (_closeReady)
            {
                return;
            }
            args.Cancel = true;
            if (_closed)
            {
                return;
            }
            _closed = true;
            _lifetime.Cancel();
            try
            {
                await _pending;
                if (_attachment is not null)
                {
                    _attachment.Session.ContextChanged -= ContextChanged;
                    await _attachment.DisposeAsync();
                }
            }
            finally
            {
                _lifetime.Dispose();
                _closeReady = true;
                Close();
            }
        };
    }

    private void Discover_Click(object sender, RoutedEventArgs args) => Start(async () =>
    {
        var candidates = await Task.Run(() => AllegroDesktop.DiscoverRunningInstances(
            cancellationToken: _lifetime.Token), _lifetime.Token);
        Boards.ItemsSource = candidates.Select(candidate => new BoardChoice(candidate)).ToArray();
        Status.Text = candidates.Count == 0 ? "No compatible running board was discovered. Start the matching Bridge in Allegro first."
            : "Select the intended board. Entries can be stale; Connect verifies the exact resident and window.";
    });

    private void Connect_Click(object sender, RoutedEventArgs args) => Start(async () =>
    {
        if (Boards.SelectedItem is not BoardChoice selected || !selected.Candidate.CanAttemptAttach)
        {
            throw new InvalidOperationException("Select a board with a compatible Bridge resident.");
        }
        AllegroDesktopAttachment? next = await AllegroDesktop.AttachAsync(selected.Candidate,
            cancellationToken: _lifetime.Token);
        try
        {
            var pcb = await next.Session.OpenPcbAsync(_lifetime.Token);
            var catalog = (await pcb.ReadCatalogAsync(new(), _lifetime.Token)).RequireCatalog();
            _lifetime.Token.ThrowIfCancellationRequested();
            var previous = _attachment;
            if (previous is not null)
            {
                previous.Session.ContextChanged -= ContextChanged;
            }
            _attachment = next;
            _pcb = pcb;
            _catalog = catalog;
            next.Session.ContextChanged += ContextChanged;
            next = null;
            ApplyFilter();
            if (previous is not null)
            {
                await previous.DisposeAsync();
            }
        }
        finally
        {
            if (next is not null)
            {
                await next.DisposeAsync();
            }
        }
    });

    private void Refresh_Click(object sender, RoutedEventArgs args) => Start(async () =>
    {
        _catalog = (await (await CurrentPcbAsync()).ReadCatalogAsync(new(), _lifetime.Token)).RequireCatalog();
        ApplyFilter();
    });

    private async Task<AllegroPcbSession> CurrentPcbAsync()
    {
        var session = _attachment?.Session ?? throw new InvalidOperationException("Connect to a board first.");
        if (!session.Context.IsConnected || !session.Context.IsCompatible)
        {
            throw new InvalidOperationException("The Bridge is no longer connected. Choose the intended board again.");
        }
        if (_pcb is null || !_pcb.IsCurrent)
        {
            _pcb = await session.OpenPcbAsync(_lifetime.Token);
        }
        return _pcb;
    }

    private void ContextChanged(object? sender, AllegroBoardContext context) => Dispatcher.BeginInvoke(() =>
    {
        if (_closed || !ReferenceEquals(sender, _attachment?.Session))
        {
            return;
        }
        if (_catalog is { } catalog && (!context.IsConnected || !context.IsCompatible ||
            context.Binding.SessionId != catalog.SessionId || context.Binding.BoardGeneration != catalog.BoardGeneration))
        {
            _catalog = null;
            Objects.ItemsSource = null;
            Details.Text = "This catalog is invalidated. Refresh data or reconnect to the intended board.";
            Status.Text = Details.Text;
            UpdateActions();
        }
    });

    private void Filter_Changed(object sender, SelectionChangedEventArgs args) => ApplyFilter();
    private void Search_Changed(object sender, TextChangedEventArgs args) => ApplyFilter();

    private void ApplyFilter()
    {
        if (Objects is null || Search is null || _catalog is not { } catalog)
        {
            return;
        }
        (AllegroPcbReadStatus state, IEnumerable<Entry> entries) = Family.SelectedIndex switch
        {
            0 => (catalog.Components.Status, catalog.Components.Items.Select(value => new Entry(value.Refdes, value,
                new AllegroPcbDisplayTarget.Component(value.Refdes)))),
            1 => (catalog.Nets.Status, catalog.Nets.Items.Select(value => new Entry(value.Name, value,
                new AllegroPcbDisplayTarget.Net(value.Name)))),
            2 => (catalog.DifferentialPairs.Status, catalog.DifferentialPairs.Items.Select(value => new Entry(value.Name, value,
                new AllegroPcbDisplayTarget.DifferentialPair(value.Name)))),
            _ => (catalog.Layers.Status, catalog.Layers.Items.Select(value => new Entry(value.Name, value, null)))
        };
        Entry[] visible = entries.Where(item => item.Name.Contains(Search.Text, StringComparison.OrdinalIgnoreCase)).ToArray();
        Objects.ItemsSource = state == AllegroPcbReadStatus.Available ? visible : Array.Empty<Entry>();
        Details.Text = state == AllegroPcbReadStatus.Available ? "Select an object to inspect its captured metadata."
            : $"This collection is {state}. That does not mean there are no objects.";
        Status.Text = $"{catalog.Design} | {state} | {visible.Length} matching objects. Captured metadata is not live edit authority.";
        UpdateActions();
    }

    private void Object_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (Objects.SelectedItem is Entry item)
        {
            var text = new StringBuilder(JsonSerializer.Serialize(item.Value, item.Value.GetType(), new JsonSerializerOptions { WriteIndented = true }));
            if (item.Value is AllegroPcbNetInput net && _catalog is { } catalog)
            {
                text.AppendLine("\nDeclared relationships:");
                if (catalog.Xnets.Status == AllegroPcbReadStatus.Available && catalog.DifferentialPairs.Status == AllegroPcbReadStatus.Available)
                {
                    foreach (var xnet in catalog.GetXnetsForNet(net.Name))
                    {
                        text.AppendLine($"Xnet {xnet.Name}: {string.Join(", ", xnet.PhysicalNets)}");
                    }
                    foreach (var pair in catalog.GetDifferentialPairsForNet(net.Name))
                    {
                        text.AppendLine($"{pair.Name}: {pair.SideAXnet} / {pair.SideBXnet}");
                    }
                }
                else
                {
                    text.AppendLine("Connectivity metadata is unavailable or was not requested.");
                }
            }
            if (item.Value is AllegroPcbDifferentialPair selectedPair)
            {
                text.AppendLine("\nSide A/B does not establish positive/negative polarity.");
                if (_catalog?.Xnets.Status == AllegroPcbReadStatus.Available)
                {
                    foreach (var xnet in _catalog.Xnets.Items.Where(value =>
                        value.Name == selectedPair.SideAXnet || value.Name == selectedPair.SideBXnet))
                    {
                        text.AppendLine($"Xnet {xnet.Name}: {string.Join(", ", xnet.PhysicalNets)}");
                    }
                }
            }
            Details.Text = text.ToString();
        }
        UpdateActions();
    }

    private void Pins_Click(object sender, RoutedEventArgs args) => Start(async () =>
    {
        if (Objects.SelectedItem is not Entry { Value: AllegroPcbComponent component } item)
        {
            return;
        }
        var detail = (await (await CurrentPcbAsync()).InspectComponentAsync(component.Refdes, _lifetime.Token)).RequireCatalog();
        if (!ReferenceEquals(item, Objects.SelectedItem))
        {
            return;
        }
        var text = new StringBuilder($"{component.Refdes}: {detail.Pins.Status}\n");
        if (detail.Pins.Status == AllegroPcbReadStatus.Available)
        {
            foreach (var pin in detail.Pins.Items)
            {
                text.AppendLine($"{pin.Number}: {pin.Net ?? "Disconnected"}");
                if (pin.Net is not null && detail.Xnets.Status == AllegroPcbReadStatus.Available &&
                    detail.DifferentialPairs.Status == AllegroPcbReadStatus.Available)
                {
                    foreach (var pair in detail.GetDifferentialPairsForNet(pin.Net))
                    {
                        text.AppendLine($"  Declared pair {pair.Name}: {pair.SideAXnet} / {pair.SideBXnet}");
                    }
                }
            }
        }
        Details.Text = text.ToString();
    });

    private void Highlight_Click(object sender, RoutedEventArgs args) => Start(() => DisplayAsync(false));
    private void Zoom_Click(object sender, RoutedEventArgs args) => Start(() => DisplayAsync(true));
    private async Task DisplayAsync(bool zoom)
    {
        var target = (Objects.SelectedItem as Entry)?.Target ?? throw new InvalidOperationException("Choose a supported object.");
        var pcb = await CurrentPcbAsync();
        var receipt = zoom ? await pcb.ZoomAsync(target, _lifetime.Token) : await pcb.HighlightAsync(target, _lifetime.Token);
        ShowReceipt(receipt);
    }

    private void Clear_Click(object sender, RoutedEventArgs args) => Start(async () =>
        ShowReceipt(await (await CurrentPcbAsync()).ClearHighlightAsync(_lifetime.Token)));

    private void ShowReceipt(AllegroOperationReceipt receipt)
    {
        Status.Text = $"{receipt.State}: {receipt.Message}";
        if (receipt.State != AllegroOperationState.Complete)
        {
            throw new InvalidOperationException(Status.Text + " No action was replayed.");
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs args)
    {
        if (Objects.SelectedItem is not Entry item)
        {
            return;
        }
        string name = JsonSerializer.Serialize(item.Name);
        string operation = item.Value switch
        {
            AllegroPcbComponent => $"var read = await pcb.InspectComponentAsync({name}, cancellationToken);\nvar component = read.RequireCatalog().FindComponent({name});",
            AllegroPcbNetInput => $"var read = await pcb.ReadNetsAsync(cancellationToken);\nvar net = read.RequireCatalog().FindNet({name});",
            AllegroPcbDifferentialPair => $"var read = await pcb.ReadConnectivityAsync(cancellationToken);\nvar pair = read.RequireCatalog().DifferentialPairs.RequireAvailable().Single(p => p.Name == {name});",
            _ => $"var read = await pcb.ReadLayersAsync(cancellationToken);\nvar layer = read.RequireCatalog().Layers.RequireAvailable().Single(l => l.Name == {name});"
        };
        try
        {
            Clipboard.SetText("using CircuitHub.AllegroBridge;\n\n// session is an explicitly connected SDK session.\nvar pcb = await session.OpenPcbAsync(cancellationToken);\n" + operation);
            Status.Text = "Copied the public C# query. No private SDK or PD-Simple code is required.";
        }
        catch (System.Runtime.InteropServices.COMException error)
        {
            Details.Text = operation;
            Status.Text = "The clipboard is busy. The code is shown in the details pane. " + error.Message;
        }
    }

    private void Start(Func<Task> action)
    {
        if (_busy || _closed)
        {
            return;
        }
        _busy = true;
        UpdateActions();
        _pending = RunAsync(action);
    }

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception error)
        {
            if (!_closed)
            {
                Status.Text = error is OperationCanceledException
                    ? "The wait ended. A dispatched display action was not cancelled or replayed." : error.Message;
            }
        }
        finally
        {
            _busy = false;
            if (!_closed)
            {
                UpdateActions();
            }
        }
    }

    private void UpdateActions()
    {
        if (ConnectButton is null)
        {
            return;
        }
        bool idle = !_busy && !_closed;
        bool connected = idle && _attachment?.Session.Context.IsConnected == true;
        ConnectButton.IsEnabled = DiscoverButton.IsEnabled = Boards.IsEnabled = idle;
        RefreshButton.IsEnabled = ClearButton.IsEnabled = connected;
        Family.IsEnabled = Search.IsEnabled = Objects.IsEnabled = idle;
        PinsButton.IsEnabled = connected && Objects.SelectedItem is Entry { Value: AllegroPcbComponent };
        HighlightButton.IsEnabled = ZoomButton.IsEnabled = connected && Objects.SelectedItem is Entry { Target: not null };
        CopyButton.IsEnabled = idle && Objects.SelectedItem is Entry;
    }

    private sealed record Entry(string Name, object Value, AllegroPcbDisplayTarget? Target);
    private sealed record BoardChoice(AllegroDesktopCandidate Candidate)
    {
        public string Label => $"{Candidate.Design ?? "Unknown board"} | process {Candidate.ProcessId} | {Candidate.UnavailableReason ?? "Verify on connection"}";
    }
}
