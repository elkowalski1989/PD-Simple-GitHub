using PD.ToolsAChecks;

int checks = 0;
checks += await CrossingWorkspaceChecks.RunAsync();
checks += await InspectorWorkspaceChecks.RunAsync();
checks += await CapturedSceneChecks.RunAsync();
checks += await ArtifactChecks.RunAsync();
checks += ViewContractChecks.Run();
checks += NativeGateFixtures.Run(out int notExecuted);

Console.WriteLine(
    $"PASS: {checks} Lane A workspace checks (T01 crossing review, T02 geometry inspector, " +
    $"T08 captured scenes, artifacts, view contracts, fixture definitions). " +
    $"NOT_EXECUTED: {notExecuted} native gates (need the coordinator-scheduled licensed Allegro slot).");
return 0;
