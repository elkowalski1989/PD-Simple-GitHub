using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;
using PD.PcbTools;

// Measurement adapter, not an alternative production acquisition provider.
// No UI automation, board save, native mutation, hot-patch or process termination.
var json = new JsonSerializerOptions { WriteIndented = true };
Dictionary<string,string> options = Parse(args);
string Required(string key) => options.TryGetValue(key,out var value) && value.Length > 0
    ? value : throw new ArgumentException("Missing " + key);
string receiptPath = Required("--receipt");
string runId = Required("--run-id");
string condition = Required("--condition");
string mode = Required("--mode");
if (mode is not ("offline" or "live")) throw new ArgumentException("Mode must be offline or live.");
if (mode == "live" && !OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Live acquisition requires the authorized Windows/Allegro environment.");
string input = Path.GetFullPath(Required("--input"));
string inputHash = HashFile(input);
string inventoryPath = Path.GetFullPath(Required("--inventory"));
if (new FileInfo(inventoryPath).Length > 1_048_576) throw new ArgumentException("Artifact inventory too large.");
Dictionary<string,Artifact> inventory = JsonSerializer.Deserialize<Dictionary<string,Artifact>>(File.ReadAllText(inventoryPath),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new ArgumentException("No artifact inventory.");
if (inventory.Count is < 1 or > 512) throw new ArgumentException("Inventory must contain 1..512 files.");
var artifacts = VerifyInventory(inventory);
string assemblyPath = Assembly.GetExecutingAssembly().Location;
if (!artifacts.Values.Contains(HashFile(assemblyPath), StringComparer.Ordinal))
    throw new ArgumentException("Include this adapter assembly in the artifact inventory.");
int iterations = options.TryGetValue("--iterations",out var repeat) ? int.Parse(repeat,CultureInfo.InvariantCulture) : 1;
if (iterations is < 1 or > 100) throw new ArgumentOutOfRangeException("--iterations");
double margin = options.TryGetValue("--margin",out var amount) ? double.Parse(amount,CultureInfo.InvariantCulture) : 0;
string navigation = options.GetValueOrDefault("--navigation", "none");
if (navigation is not ("none" or "browse" or "strict") || (mode=="offline" && navigation!="none"))
    throw new ArgumentException("Navigation must be none/browse/strict; offline permits none only.");
int selectionCount = options.TryGetValue("--selection-count",out var selects) ? int.Parse(selects,CultureInfo.InvariantCulture) : 12;
if (selectionCount is < 1 or > 100) throw new ArgumentOutOfRangeException("--selection-count");
var corridorOptions = new CorridorOptions(margin,options.GetValueOrDefault("--module"),
    options.GetValueOrDefault("--include-unused","false") == "true");
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_,e)=> { e.Cancel=true; cancellation.Cancel(); };
var samples = new List<object>();
var resultHashes = new List<string>();
var coverageHashes = new List<string>();
var captureIds = new HashSet<Guid>();
var total = Stopwatch.StartNew();
AllegroEngineSession? session = null;
string status="failed", detail="";
try
{
    if (mode == "live")
    {
        var resolution = AllegroEngineDiscovery.ResolveLaunchTarget(["--bridge-dir",Required("--bridge-dir")]);
        var target = resolution.Target ?? throw new InvalidOperationException(string.Join("; ",resolution.Diagnostics.Select(x=>x.Message)));
        session = AllegroEngineSession.Create(new EngineSessionOptions { RequiredCapabilities=navigation=="none" ? [EngineCapabilities.SceneRead] : [EngineCapabilities.SceneRead,EngineCapabilities.Display] });
        await session.ConnectAsync(target,cancellation.Token);
    }
    for (int iteration=0; iteration<iterations; iteration++)
    {
        cancellation.Token.ThrowIfCancellationRequested();
        if (HashFile(input)!=inputHash) throw new InvalidOperationException("Saved input changed; stop without pooling samples.");
        VerifyInventory(inventory);
        object memoryBefore = SnapshotProcess(Environment.ProcessId);
        var phases = new List<object>();
        var iterationTimer = Stopwatch.StartNew();
        var timer = Stopwatch.StartNew();
        LiveDesignScene? live = null;
        DesignScene scene;
        if (session is not null)
        {
            live = await session.Workspace.ReadAsync(CorridorAnalyzer.CreateSceneQuery(corridorOptions.ModuleName),cancellation.Token);
            live.RequireCurrent();
            scene = live.Scene;
            if (!captureIds.Add(scene.Identity.CaptureId)) throw new InvalidOperationException("Run reused a live capture identity.");
        }
        else scene = await SceneArchive.LoadAsync(input,cancellation.Token);
        phases.Add(Phase("acquisition_or_archive_load",timer.Elapsed.TotalMilliseconds));
        if (live?.AcquisitionTiming is { } timing)
        {
            phases.Add(DurationPhase("native_command_round_trip",timing.NativeCommandRoundTrip));
            phases.Add(DurationPhase("transfer_and_seal",timing.SnapshotTransferAndSeal));
            phases.Add(DurationPhase("native_release",timing.NativeRelease));
            phases.Add(DurationPhase("replay_and_conversion",timing.SnapshotReplayAndConversion));
            phases.Add(DurationPhase("scene_construction",timing.SceneConstruction));
            phases.Add(DurationPhase("snapshot_disposal",timing.SnapshotDisposal));
        }
        timer.Restart();
        CorridorScan scan = await Task.Run(()=>CorridorAnalyzer.Analyze(scene,corridorOptions,cancellation.Token),cancellation.Token);
        phases.Add(Phase("analysis",timer.Elapsed.TotalMilliseconds));
        live?.RequireCurrent();
        // Deliberately preserve finding order and captured ordinals. New semantic
        // order is a different algorithm, not an equivalent optimization.
        string resultHash = Hash(new { CorridorAnalyzer.Algorithm,scan.Options,scan.PairCount,scan.CorridorCount,scan.Findings });
        string coverageHash = Hash(new { scan.CoverageWarnings,Families=Enum.GetValues<DataFamily>().Select(f=>new {
            Family=f.ToString(), Availability=scene.Coverage[f].Availability.ToString(),
            Completeness=scene.Coverage[f].Completeness.ToString(),scene.Coverage[f].Reasons }) });
        resultHashes.Add(resultHash); coverageHashes.Add(coverageHash);
        timer.Restart();
        var report = new { scan.PairCount,scan.CorridorCount,scan.Findings,scan.CoverageWarnings };
        string reportPath=receiptPath+$".sample-{iteration:D3}.result.json";
        await WriteNewJson(reportPath,report,cancellation.Token);
        phases.Add(Phase("benchmark_report_write",timer.Elapsed.TotalMilliseconds));
        var navigationSamples = new List<object>();
        if (session is not null && live is not null && navigation!="none")
        {
            if (scan.Findings.Count==0) throw new InvalidOperationException("Navigation campaign has no findings; cannot claim navigation qualification.");
            for (int pick=0;pick<selectionCount;pick++)
            {
                live.RequireCurrent();
                CorridorFinding finding=scan.Findings[pick%Math.Min(scan.Findings.Count,12)];
                SceneQuery regionQuery=CorridorNavigation.CreateQuery(scan,finding);
                DesignBounds scope=regionQuery.Region ?? throw new InvalidDataException("No navigation scope.");
                var navTimer=Stopwatch.StartNew();
                EngineViewport viewport;
                EngineRegionTiming? nativeTiming=null;
                double? regionMs=null, matchMs=null;
                if(navigation=="browse")
                {
                    EngineNavigationTicket ticket=session.Workspace.Display.AdmitTicket(scan.Scene,
                        [finding.PositiveViaIndex,finding.NegativeViaIndex,finding.AggressorIndex],live.Document,regionQuery.Layers,scope);
                    viewport=await session.Workspace.Display.ZoomTicketAsync(ticket,scope,new(finding.Layer),cancellation.Token);
                }
                else
                {
                    var phase=Stopwatch.StartNew();
                    LiveRegionScene region=await session.Workspace.ReadRegionAsync(regionQuery,cancellation.Token);
                    regionMs=phase.Elapsed.TotalMilliseconds; phase.Restart();
                    EngineWitnessMatch witnesses=CorridorNavigation.MatchFreshWitnesses(scan,finding,region,live.Document);
                    matchMs=phase.Elapsed.TotalMilliseconds;
                    viewport=await session.Workspace.Display.ZoomWitnessesAsync(region,region.Scene.Document.Bounds,new(finding.Layer),witnesses,cancellation.Token);
                    nativeTiming=region.Timing;
                }
                if(viewport.Document!=live.Document) throw new InvalidDataException("Navigation changed document.");
                navigationSamples.Add(new {pick,finding_id=finding.Id,mode=navigation,wall_ms=navTimer.Elapsed.TotalMilliseconds,
                    region_read_count=navigation=="browse"?0:1,region_ms=regionMs,match_ms=matchMs,nativeTiming,
                    viewport.RequestedBounds,viewport.ActualBounds,
                    native_process=live.Document.ProcessId is int nativePid?SnapshotProcess(nativePid):null,
                    verification=navigation=="browse"?"scalar-witness-navigation":"fresh-region-selected-field-match",
                    visible_ui_completion="not_measured",crossing_analysis_rerun=false});
            }
        }
        iterationTimer.Stop();
        samples.Add(new {
            iteration,status="passed",wall_ms=iterationTimer.Elapsed.TotalMilliseconds,navigation=navigationSamples, capture_id=scene.Identity.CaptureId,document=session?.State.Document,
            phases,resources=live?.AcquisitionResources,
            equivalence=new {semantics=CorridorAnalyzer.Algorithm,result_sha256=resultHash,coverage_sha256=coverageHash},
            own_process_before=memoryBefore,own_process_after=SnapshotProcess(Environment.ProcessId),
            native_process=session?.State.Document?.ProcessId is int pid ? SnapshotProcess(pid) : null,
            managed_gc=new { heap_bytes=GC.GetTotalMemory(false),allocated_bytes=GC.GetTotalAllocatedBytes(),
                gen0=GC.CollectionCount(0),gen1=GC.CollectionCount(1),gen2=GC.CollectionCount(2) },
            report_sha256=HashFile(reportPath),
            qualification="Saved input hash is not proof of unsaved live geometry. Native busy intervals and actual visible UI latency are not measured by this console adapter."
        });
        if (HashFile(input)!=inputHash) throw new InvalidOperationException("Saved input changed during acquisition/analysis.");
        VerifyInventory(inventory);
    }
    if (resultHashes.Distinct().Count()!=1 || coverageHashes.Distinct().Count()!=1)
        throw new InvalidOperationException("Repeated run result/coverage drift; inspect individual samples instead of averaging.");
    status="passed";
}
catch (OperationCanceledException)
{
    status="cancelled";
    detail="Managed wait cancelled. Native cancellation and quiescence are not inferred. Inspect Engine/native state before another run.";
}
catch(Exception error) { detail=error.ToString(); }
finally
{
    if (session is not null)
    {
        try { await session.DisposeAsync(); }
        catch(Exception error) { status="failed"; detail+="\nSession disposal: "+error; }
    }
    total.Stop();
    var receipt = new {
        schema="allegro.acquisition-benchmark/v1",run_id=runId,condition,
        workload="corridor/acquire-analyze/"+navigation+"/iterations-"+iterations,kind=mode,
        input_scope=mode=="live" ? "current-live-capture/saved-source-hash-only" : "immutable-scene-archive",
        input_sha256=inputHash,algorithm=CorridorAnalyzer.Algorithm,artifacts,status,wall_ms=total.Elapsed.TotalMilliseconds,
        phases=Array.Empty<object>(),samples,
        equivalence=status=="passed" ? new {semantics=CorridorAnalyzer.Algorithm,
            result_sha256=Hash(resultHashes),coverage_sha256=Hash(coverageHashes)} : null,
        detail,os_cache_state="not_controlled",forced_gc=false,
        production_ui_visibility="not_measured",process_after_session_disposal=SnapshotProcess(Environment.ProcessId),native_mutation="not_requested",
        qualification="A passed adapter proves these calls completed and repeated fingerprints agree; it is not native UI acceptance or a cross-provider equivalence verdict."
    };
    await WriteNew(receiptPath,JsonSerializer.Serialize(receipt,json),CancellationToken.None);
}
return status=="passed" ? 0 : 2;

static object Phase(string name,double elapsed)=>new {name,kind="wall",value_ms=elapsed,resolution_ms=1000d/Stopwatch.Frequency};
static object DurationPhase(string name,TimeSpan? elapsed)=>new {name,kind="wall",value_ms=elapsed?.TotalMilliseconds,resolution_ms=1000d/Stopwatch.Frequency};
static string Hash(object value)
{
    using var hash=SHA256.Create();
    using(var stream=new CryptoStream(Stream.Null,hash,CryptoStreamMode.Write,true))
    { JsonSerializer.Serialize(stream,value,value.GetType()); stream.FlushFinalBlock(); }
    return Convert.ToHexString(hash.Hash!).ToLowerInvariant();
}
static string HashFile(string path) { using var stream=File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
static Dictionary<string,string> VerifyInventory(Dictionary<string,Artifact> inventory)
{
    var result=new Dictionary<string,string>(StringComparer.Ordinal);
    foreach(var pair in inventory)
    {
        if (pair.Value is null || !Path.IsPathFullyQualified(pair.Value.Path)) throw new ArgumentException("Absolute artifact paths are required.");
        string actual=HashFile(pair.Value.Path);
        if (actual!=pair.Value.Sha256) throw new InvalidOperationException("Artifact identity differs: "+pair.Key);
        result.Add(pair.Key,actual);
    }
    return result;
}
static object SnapshotProcess(int pid)
{
    try { using var p=Process.GetProcessById(pid); p.Refresh(); return new {pid,working_set=p.WorkingSet64,private_bytes=p.PrivateMemorySize64,process_cpu_ms=p.TotalProcessorTime.TotalMilliseconds,available=true}; }
    catch(Exception e) when(e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
    { return new {pid,available=false,reason=e.Message}; }
}
static async Task WriteNewJson(string path,object content,CancellationToken token)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    await using var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);
    await JsonSerializer.SerializeAsync(stream,content,content.GetType(),cancellationToken:token);
    await stream.FlushAsync(token);
}
static async Task WriteNew(string path,string content,CancellationToken token)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    await using var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);
    byte[] bytes=System.Text.Encoding.UTF8.GetBytes(content);
    await stream.WriteAsync(bytes,token); await stream.FlushAsync(token);
}
static Dictionary<string,string> Parse(string[] args)
{
    string[] valid=["--receipt","--run-id","--condition","--mode","--input","--inventory","--iterations","--margin","--module","--include-unused","--bridge-dir","--navigation","--selection-count"];
    if (args.Length%2!=0) throw new ArgumentException("Supply --name value pairs.");
    var options=new Dictionary<string,string>(StringComparer.Ordinal);
    for(int i=0;i<args.Length;i+=2)
        if(!valid.Contains(args[i]) || !options.TryAdd(args[i],args[i+1])) throw new ArgumentException("Unknown or duplicate option: "+args[i]);
    return options;
}
internal sealed record Artifact(string Path,string Sha256);
