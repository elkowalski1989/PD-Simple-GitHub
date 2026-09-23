using System.Collections.Immutable;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Geometry;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;
using PD.PcbTools;

internal static class ScalableCorridorChecks
{
    public static int Run()
    {
        int checks = 0;
        checks += CheckSmallFixtureEquivalence();
        checks += CheckBatchingAndObjectKinds();
        checks += CheckIdentifierIndependenceAndNegativeControl();
        checks += CheckFailureAndPartialSemantics();
        checks += CheckKnownViaMetadataIsAdvisory();
        checks += CheckDetailedContourEvidence();
        checks += CheckBatchScenesAreReleased();
        checks += CheckScalablePairPolicies();
        checks += CheckBatchArrivalOrder();
        checks += CheckPlanningTileOrder();
        checks += CheckBatchPartitions();
        checks += CheckSourceTraversalOrdering();
        checks += CheckSourceTraversalValidation();
        checks += CheckCrossCaptureScanTraversal();
        return checks;
    }

    private static int CheckSourceTraversalOrdering()
    {
        int checks = 0;
        foreach (string prefix in new[] { "ORIGINAL/", "RENAMED/" })
        {
            DesignScene fixture = Fixture(prefix + "PAIR", 0, prefix);
            ImmutableArray<CopperObject> copper =
            [
                Via("UNRELATED", 1_000, 0, prefix + "via:remote"),
                .. fixture.Data.Copper.Select(item => item.Via is { } via
                    ? item with { Via = via with { BackdrillStatus = "unknown" } }
                    : item),
                Via(prefix + "PAIR_P", -40, 0, prefix + "via:second-p"),
                Via(prefix + "PAIR_N", 40, 0, prefix + "via:second-n"),
                Segment(prefix + "CLOCK_OTHER", 5, -20, 20, 0, prefix + "trace:second"),
                Segment(null, 10, -20, 20, 0, prefix + "trace:disconnected"),
                Shape(prefix + "SHAPE", -5, -2, 5, 2, 0, prefix + "shape:unknown") with
                {
                    FillOutOfDate = null,
                },
            ];
            DesignScene full = Rebuild(fixture, data: fixture.Data with
            {
                Copper = copper,
                Nets =
                [
                    .. fixture.Data.Nets,
                    new(new(prefix + "net:remote"), "UNRELATED", 1),
                    new(new(prefix + "net:second"), prefix + "CLOCK_OTHER", 1),
                    new(new(prefix + "net:shape"), prefix + "SHAPE", 1),
                ],
            });
            CorridorSourceTraversal traversal = SourceTraversal();
            ScalableCorridorScan original = Complete(Plan(full, sourceTraversal: traversal), full);
            ScalableCorridorScan compacted = Complete(
                Plan(full, sourceTraversal: traversal, compactCapture: true), full, compactCapture: true);
            Require(original.SourceTraversal?.CanCompareSourceOrdinalsWith(compacted.SourceTraversal) == true &&
                    original.PairCount == compacted.PairCount &&
                    original.CorridorCount == compacted.CorridorCount &&
                    original.Findings.Select(item => Comparable(item.Finding))
                        .SequenceEqual(compacted.Findings.Select(item => Comparable(item.Finding))) &&
                    original.CoverageWarnings.SequenceEqual(compacted.CoverageWarnings),
                "Compacted emitted ordinals changed physical source finding or warning order.");
            Require(original.Findings.Count >= 3 && original.CoverageWarnings.Count >= 3 &&
                    original.Findings.Any(item => item.Witnesses.Aggressor.NetName is null) &&
                    original.Findings.Select(item => item.Witnesses.AggressorIdentity.SourceTraversalOrdinal)
                        .SequenceEqual(compacted.Findings.Select(item => item.Witnesses.AggressorIdentity.SourceTraversalOrdinal)) &&
                    !original.Findings.Select(item => item.Witnesses.AggressorIdentity.CaptureOrdinal)
                        .SequenceEqual(compacted.Findings.Select(item => item.Witnesses.AggressorIdentity.CaptureOrdinal)),
                "The source-order control did not exercise sparse ordinals, warnings, and all-net aggressors.");
            foreach (ScalableCorridorFinding finding in compacted.Findings)
            {
                DesignScene witnesses = compacted.CreateSourceWitnessScene(finding);
                Require(finding.Finding.PositiveViaIndex == 0 && finding.Finding.NegativeViaIndex == 1 &&
                        finding.Finding.AggressorIndex == 2 &&
                        witnesses.Data.Copper[finding.Finding.PositiveViaIndex].Id == finding.Witnesses.PositiveVia.Id &&
                        witnesses.Data.Copper[finding.Finding.NegativeViaIndex].Id == finding.Witnesses.NegativeVia.Id &&
                        witnesses.Data.Copper[finding.Finding.AggressorIndex].Id == finding.Witnesses.Aggressor.Id,
                    "A physical source ordinal escaped into a local three-object witness index.");
            }
            DesignScene clear = Fixture(prefix + "CLEAR", 1_000, prefix + "clear/");
            Require(Complete(Plan(clear, sourceTraversal: traversal), clear).Findings.Count == 0,
                "Source ordering created a finding for the changed-identifier clear control.");
            checks += 4;
        }
        return checks;
    }

    private static int CheckSourceTraversalValidation()
    {
        DesignScene full = Fixture("PROVENANCE", 0);
        CorridorSourceTraversal traversal = SourceTraversal();
        ScalableCorridorPlan plan = Plan(full, sourceTraversal: traversal);
        CorridorReplayBatch batch = plan.Batches.Single();
        CorridorReplayScene valid = WithSourceTraversal(Replay(full, batch), traversal, compactCapture: false);
        foreach (long? invalidOrdinal in new long?[] { null, -1, traversal.RootCount })
        {
            CorridorReplayScene invalid = valid with
            {
                ObjectIdentities = valid.ObjectIdentities.Select((identity, index) => index == 2
                    ? identity with { SourceTraversalOrdinal = invalidOrdinal }
                    : identity).ToArray(),
            };
            RequireThrows<InvalidDataException>(() => plan.BeginScan().AcceptBatch(batch, invalid),
                "An incomplete or out-of-range source ordinal was admitted.");
        }
        CorridorReplayScene duplicateOrdinal = valid with
        {
            ObjectIdentities = valid.ObjectIdentities.Select((identity, index) => index == 2
                ? identity with { SourceTraversalOrdinal = valid.ObjectIdentities[0].SourceTraversalOrdinal }
                : identity).ToArray(),
        };
        RequireThrows<InvalidDataException>(() => plan.BeginScan().AcceptBatch(batch, duplicateOrdinal),
            "Two source identities claimed the same physical-root ordinal.");
        CorridorReplayScene changedPlanningIdentity = valid with
        {
            ObjectIdentities = valid.ObjectIdentities.Select((identity, index) => index == 0
                ? identity with { SourceTraversalOrdinal = 900 }
                : identity).ToArray(),
        };
        RequireThrows<InvalidDataException>(() => plan.BeginScan().AcceptBatch(batch, changedPlanningIdentity),
            "A planning identity changed its physical-root ordinal during scanning.");
        ScalableCorridorPlan legacyPlan = Plan(full);
        RequireThrows<InvalidDataException>(() => legacyPlan.BeginScan().AcceptBatch(legacyPlan.Batches.Single(), valid with
        {
            SourceTraversal = null,
        }), "An undeclared source ordinal was treated as legacy evidence.");
        RequireThrows<InvalidDataException>(() => plan.BeginScan().AcceptBatch(batch, valid with
        {
            SourceTraversal = traversal with { ObservationToken = "another-observation" },
        }), "A replay from another observation entered a source-ordered plan.");
        RequireThrows<InvalidDataException>(() => Plan(full, sourceTraversal: traversal with { Version = 2 }),
            "An unsupported source-order version was admitted.");
        RequireThrows<InvalidDataException>(() => ScalableCorridorAnalyzer.BeginPlanning(
            full, new(0, null, false), new() { MaximumRetainedSourceIdentities = 1 },
            sourceTraversal: traversal).AcceptReplay(new(
                Rebuild(full, data: full.Data with { Copper = full.Data.Copper.Take(2).ToImmutableArray() }),
                valid.ObjectIdentities.Take(2).ToArray()) { SourceTraversal = traversal }),
            "Source validation exceeded its configured scalar-identity memory bound.");
        var frozen = new CorridorFrozenSource(new string('a', 32), new string('b', 64), 123, 10_000, true);
        CorridorSourceTraversal authenticated = traversal with { FrozenSource = frozen };
        CorridorSourceTraversal second = authenticated with { ObservationToken = "second-capture" };
        Require(authenticated.CanCompareSourceOrdinalsWith(second),
            "Verified projections of the same frozen ordering universe were not comparable.");
        foreach (CorridorSourceTraversal incomparable in new[]
        {
            traversal with { ObservationToken = "another-observation" },
            authenticated with { SessionId = "another-session" },
            authenticated with { BoardGeneration = 2 },
            authenticated with { RootCount = traversal.RootCount + 1 },
            authenticated with { Version = 2 },
            authenticated with { OrderingUniverse = "filtered-roots" },
            second with { FrozenSource = frozen with { SourceOrderVerified = false } },
            second with { FrozenSource = frozen with { InputSha256 = new string('c', 64) } },
        })
        {
            Require(!authenticated.CanCompareSourceOrdinalsWith(incomparable),
                "Incompatible observations were certified by matching local sort or source identities.");
        }
        return 18;
    }

    private static int CheckCrossCaptureScanTraversal()
    {
        DesignScene fixture = Fixture("CROSS", 0, "cross-");
        ImmutableArray<CopperObject> copper =
        [
            Via("UNRELATED", 1_000, 0, "cross-via:remote"),
            .. fixture.Data.Copper.Select(item => item.Via is { } via
                ? item with { Via = via with { BackdrillStatus = "unknown" } }
                : item),
            Via("CROSS_P", -40, 0, "cross-via:second-p"),
            Via("CROSS_N", 40, 0, "cross-via:second-n"),
            Segment("CROSS_CLOCK", 5, -20, 20, 0, "cross-trace:second"),
            Segment(null, 10, -20, 20, 0, "cross-trace:disconnected"),
            Shape("CROSS_SHAPE", -5, -2, 5, 2, 0, "cross-shape:unknown") with
            {
                FillOutOfDate = null,
            },
        ];
        DesignScene full = Rebuild(fixture, data: fixture.Data with
        {
            Copper = copper,
            Nets =
            [
                .. fixture.Data.Nets,
                new(new("cross-net:remote"), "UNRELATED", 1),
                new(new("cross-net:second"), "CROSS_CLOCK", 1),
                new(new("cross-net:shape"), "CROSS_SHAPE", 1),
            ],
        });
        var frozen = new CorridorFrozenSource(new string('a', 32), new string('b', 64), 123, 20_000, true);
        CorridorSourceTraversal planningTraversal = SourceTraversal() with
        {
            ObservationToken = "planning-capture",
            FrozenSource = frozen,
        };
        CorridorSourceTraversal scanTraversal = planningTraversal with
        {
            ObservationToken = "scan-capture",
        };
        Require(planningTraversal.CanCompareSourceOrdinalsWith(scanTraversal),
            "Verified projections of the same frozen source were not comparable.");
        ScalableCorridorPlan plan = Plan(full, sourceTraversal: planningTraversal);
        ScalableCorridorScan baseline = Complete(plan, full);
        Require(baseline.Findings.Count >= 3 && baseline.CoverageWarnings.Count >= 3,
            "The cross-capture control did not exercise multiple findings and warnings.");
        ScalableCorridorScanSession session = plan.BeginScan(scanTraversal);
        foreach (CorridorReplayBatch batch in plan.Batches)
        {
            CorridorReplayScene replay = WithScanLocalIdentities(
                WithSourceTraversal(Replay(full, batch), scanTraversal, compactCapture: false));
            session.AcceptBatch(batch, replay);
        }
        ScalableCorridorScan cross = session.Complete();
        Require(cross.Findings.Select(item => Comparable(item.Finding))
                .SequenceEqual(baseline.Findings.Select(item => Comparable(item.Finding))),
            "A second capture of the same frozen source changed ordered findings.");
        Require(cross.CoverageWarnings.SequenceEqual(baseline.CoverageWarnings),
            "A second capture of the same frozen source changed coverage warnings.");
        Require(cross.BlockingCoverageWarnings.SequenceEqual(baseline.BlockingCoverageWarnings),
            "A second capture of the same frozen source changed blocking coverage.");
        Require(cross.SourceTraversal == scanTraversal && cross.SourceTraversal != plan.SourceTraversal,
            "The scan did not preserve its actual second-capture traversal token.");
        Require(cross.Findings.Count == baseline.Findings.Count &&
                cross.Findings.Zip(baseline.Findings).All(pair =>
                    pair.First.Witnesses.AggressorIdentity.SourceIdentity ==
                        pair.Second.Witnesses.AggressorIdentity.SourceIdentity &&
                    pair.First.Witnesses.AggressorIdentity.Kind ==
                        pair.Second.Witnesses.AggressorIdentity.Kind &&
                    pair.First.Witnesses.AggressorIdentity.SourceTraversalOrdinal ==
                        pair.Second.Witnesses.AggressorIdentity.SourceTraversalOrdinal &&
                    pair.First.Witnesses.AggressorIdentity.Id !=
                        pair.Second.Witnesses.AggressorIdentity.Id &&
                    pair.First.Witnesses.AggressorIdentity.CaptureOrdinal !=
                        pair.Second.Witnesses.AggressorIdentity.CaptureOrdinal),
            "The cross-capture control did not change local IDs and ordinals while preserving stable roots.");
        CorridorReplayBatch firstBatch = plan.Batches[0];
        CorridorReplayScene validCross = WithScanLocalIdentities(
            WithSourceTraversal(Replay(full, firstBatch), scanTraversal, compactCapture: false));
        RequireThrows<InvalidDataException>(() => plan.BeginScan(scanTraversal with
        {
            FrozenSource = frozen with { InputSha256 = new string('c', 64) },
        }), "A scan with a different frozen input was admitted.");
        RequireThrows<InvalidDataException>(() => plan.BeginScan(scanTraversal with
        {
            SessionId = "another-session",
        }), "A scan from another session was admitted.");
        RequireThrows<InvalidDataException>(() => plan.BeginScan(scanTraversal with
        {
            BoardGeneration = planningTraversal.BoardGeneration + 1,
        }), "A scan from another board generation was admitted.");
        RequireThrows<InvalidDataException>(() => plan.BeginScan(scanTraversal with
        {
            RootCount = planningTraversal.RootCount + 1,
        }), "A scan with a different root universe was admitted.");
        RequireThrows<InvalidDataException>(() => plan.BeginScan(scanTraversal with
        {
            FrozenSource = frozen with { SourceOrderVerified = false },
        }), "A scan without verified source order was admitted.");
        RequireThrows<InvalidDataException>(() => plan.BeginScan(planningTraversal with
        {
            FrozenSource = frozen with { InputSha256 = new string('d', 64) },
        }), "A scan with the same token but a different frozen input was admitted.");
        int viaIndex = validCross.Scene.Data.Copper
            .Select((item, index) => (item, index))
            .First(entry => entry.item.Kind == CopperKind.Via).index;
        CorridorReplayScene changedOrdinal = validCross with
        {
            ObjectIdentities = validCross.ObjectIdentities.Select((identity, index) => index == viaIndex
                ? identity with { SourceTraversalOrdinal = 900 }
                : identity).ToArray(),
        };
        RequireThrows<InvalidDataException>(() => plan.BeginScan(scanTraversal).AcceptBatch(firstBatch, changedOrdinal),
            "A planning root changed its physical-root ordinal in the second capture.");
        CopperObject[] changedKindCopper = validCross.Scene.Data.Copper.ToArray();
        CorridorSourceIdentity[] changedKindIdentities = validCross.ObjectIdentities.ToArray();
        changedKindCopper[viaIndex] = changedKindCopper[viaIndex] with
        {
            Kind = CopperKind.Shape,
            Layer = new LayerId("ETCH/S03"),
            Via = null,
        };
        changedKindIdentities[viaIndex] = changedKindIdentities[viaIndex] with { Kind = CopperKind.Shape };
        CorridorReplayScene changedKind = validCross with
        {
            Scene = Rebuild(validCross.Scene, data: validCross.Scene.Data with
            {
                Copper = changedKindCopper.ToImmutableArray(),
            }),
            ObjectIdentities = changedKindIdentities,
        };
        RequireThrows<InvalidDataException>(() => plan.BeginScan(scanTraversal).AcceptBatch(firstBatch, changedKind),
            "A planning root changed its kind in the second capture.");
        long firstOrdinal = validCross.ObjectIdentities[0].SourceTraversalOrdinal!.Value;
        CorridorReplayScene duplicateOrdinal = validCross with
        {
            ObjectIdentities = validCross.ObjectIdentities.Select((identity, index) => index == 1
                ? identity with { SourceTraversalOrdinal = firstOrdinal }
                : identity).ToArray(),
        };
        RequireThrows<InvalidDataException>(() => plan.BeginScan(scanTraversal).AcceptBatch(firstBatch, duplicateOrdinal),
            "Two second-capture sources claimed the same physical-root ordinal.");
        RequireThrows<InvalidDataException>(() => plan.BeginScan().AcceptBatch(firstBatch, validCross),
            "The default scan admitted a replay from a different capture token.");
        CopperObject overlapTarget = full.Data.Copper.First(item =>
            item.Kind == CopperKind.Via &&
            item.NetName is not null &&
            (item.NetName.EndsWith("_P", StringComparison.Ordinal) ||
                item.NetName.EndsWith("_N", StringComparison.Ordinal)) &&
            (item.Bounds.Minimum.X + item.Bounds.Maximum.X) / 2 > firstBatch.Region.Minimum.X &&
            (item.Bounds.Minimum.X + item.Bounds.Maximum.X) / 2 < firstBatch.Region.Maximum.X);
        decimal overlapMiddle = (overlapTarget.Bounds.Minimum.X + overlapTarget.Bounds.Maximum.X) / 2;
        DesignBounds overlapFirst = new(firstBatch.Region.Minimum, new(overlapMiddle, firstBatch.Region.Maximum.Y));
        DesignBounds overlapSecond = new(new(overlapMiddle, firstBatch.Region.Minimum.Y), firstBatch.Region.Maximum);
        string overlapSource = "source/" + overlapTarget.Id.Value;
        ScalableCorridorScanSession overlapSession = plan.BeginScan(scanTraversal);
        overlapSession.SplitBatchRegion(firstBatch, firstBatch.Region, overlapFirst, overlapSecond);
        CorridorReplayScene overlapFirstReplay = WithScanLocalIdentities(
            WithSourceTraversal(Replay(full, firstBatch with { Region = overlapFirst }), scanTraversal, compactCapture: false));
        CorridorReplayScene overlapSecondReplay = WithScanLocalIdentities(
            WithSourceTraversal(Replay(full, firstBatch with { Region = overlapSecond }), scanTraversal, compactCapture: false));
        int overlapFirstIndex = overlapFirstReplay.ObjectIdentities
            .Select((identity, index) => (identity, index))
            .First(entry => entry.identity.SourceIdentity == overlapSource).index;
        int overlapSecondIndex = overlapSecondReplay.ObjectIdentities
            .Select((identity, index) => (identity, index))
            .First(entry => entry.identity.SourceIdentity == overlapSource).index;
        Require(overlapFirstReplay.ObjectIdentities[overlapFirstIndex].Kind ==
                overlapSecondReplay.ObjectIdentities[overlapSecondIndex].Kind &&
                overlapFirstReplay.ObjectIdentities[overlapFirstIndex].SourceTraversalOrdinal ==
                overlapSecondReplay.ObjectIdentities[overlapSecondIndex].SourceTraversalOrdinal,
            "The B-local overlap control did not preserve stable roots across partitions.");
        overlapSession.AcceptBatch(firstBatch, overlapFirstReplay);
        CorridorSourceIdentity[] inconsistentIdentities = overlapSecondReplay.ObjectIdentities.ToArray();
        inconsistentIdentities[overlapSecondIndex] = inconsistentIdentities[overlapSecondIndex] with
        {
            CaptureOrdinal = inconsistentIdentities[overlapSecondIndex].CaptureOrdinal + 1,
        };
        CorridorReplayScene inconsistentSecond = overlapSecondReplay with
        {
            ObjectIdentities = inconsistentIdentities,
        };
        RequireThrows<InvalidDataException>(() => overlapSession.AcceptBatch(firstBatch, inconsistentSecond),
            "A B root present in planning changed its capture-local ordinal across B replays.");
        return 19;
    }

    private static CorridorSourceTraversal SourceTraversal() => new(
        1, "all_board_copper_roots_v1", "source-order-session", 1, "source-observation", 10_000);

    private static CorridorReplayScene WithSourceTraversal(
        CorridorReplayScene replay,
        CorridorSourceTraversal traversal,
        bool compactCapture) => replay with
    {
        SourceTraversal = traversal,
        ObjectIdentities = replay.ObjectIdentities.Select(identity => identity with
        {
            CaptureOrdinal = compactCapture ? identity.CaptureOrdinal : identity.CaptureOrdinal * 100 + 7,
            SourceTraversalOrdinal = identity.CaptureOrdinal * 10 + 3,
        }).ToArray(),
    };

    private static CorridorReplayScene WithScanLocalIdentities(CorridorReplayScene replay)
    {
        CopperObject[] copper = replay.Scene.Data.Copper
            .Select(item => item with { Id = new SceneObjectId(item.Id.Value + "/scan-local") })
            .ToArray();
        CorridorSourceIdentity[] identities = replay.ObjectIdentities
            .Select((identity, index) => identity with
            {
                Id = copper[index].Id,
                CaptureOrdinal = identity.CaptureOrdinal + 50_000,
            })
            .ToArray();
        DesignScene scene = Rebuild(replay.Scene, data: replay.Scene.Data with
        {
            Copper = copper.ToImmutableArray(),
        });
        return replay with
        {
            Scene = scene,
            ObjectIdentities = identities,
        };
    }

    private static int CheckBatchPartitions()
    {
        DesignScene full = Fixture("PARTITIONED", 0);
        ViaPadMeasurementSelection selection = ViaPadMeasurementSelection.MatchingNetNameSuffixes("_p", "_n", "_DIFF");
        full = new(full.Identity, full.Document, full.Query with { ViaPadMeasurements = selection },
            full.Coverage, full.Data);
        ScalableCorridorPlan plan = Plan(full);
        CorridorReplayBatch batch = plan.Batches.Single();
        Require(batch.CreateQuery().ViaPadMeasurements == selection,
            "Batch query construction did not preserve the exact capture pad-selection policy.");
        Require(!batch.CreateQuery().Families.Contains(DataFamily.Nets),
            "A bounded aggressor replay redundantly requested the global net catalog.");
        decimal middle = (batch.Region.Minimum.X + batch.Region.Maximum.X) / 2;
        DesignBounds first = new(batch.Region.Minimum, new(middle, batch.Region.Maximum.Y));
        DesignBounds second = new(new(middle, batch.Region.Minimum.Y), batch.Region.Maximum);
        ScalableCorridorScanSession session = plan.BeginScan();
        RequireThrows<ArgumentException>(() => session.SplitBatchRegion(batch, batch.Region,
            first with { Maximum = new(middle - 1, first.Maximum.Y) }, second),
            "A partition gap was admitted as complete corridor coverage.");
        session.SplitBatchRegion(batch, batch.Region, first, second);
        session.AcceptBatch(batch, Replay(full, batch with { Region = second }));
        RequireThrows<InvalidOperationException>(() => session.Complete(),
            "A batch with an unread child partition published a partial result.");
        session.AcceptBatch(batch, Replay(full, batch with { Region = first }));
        ScalableCorridorScan result = session.Complete();
        Require(result.Findings.Select(item => Comparable(item.Finding))
                .SequenceEqual(CorridorAnalyzer.Analyze(full, new(0, null, false)).Findings.Select(Comparable)),
            "Reversed child replay order changed boundary-object deduplication or findings.");
        ScalableCorridorScanSession partial = plan.BeginScan();
        partial.SplitBatchRegion(batch, batch.Region, first, second);
        CorridorReplayScene incomplete = Replay(full, batch with { Region = first });
        partial.AcceptBatch(batch, incomplete with
        {
            Scene = Rebuild(incomplete.Scene, coverage: Coverage(incomplete.Scene.Query, false, "bounded_out")),
        });
        partial.AcceptBatch(batch, Replay(full, batch with { Region = second }));
        Require(!partial.Complete().HasCompleteInputs,
            "A complete partition tree concealed incomplete child replay coverage.");
        return 6;
    }

    private static int CheckPlanningTileOrder()
    {
        DesignScene fixture = Fixture("TILED", 0);
        DesignScene full = Rebuild(fixture, data: fixture.Data with
        {
            Copper = fixture.Data.Copper
                .Add(Via("TILED_P", 60, 3, "via:last-positive"))
                .Add(Via("UNRELATED", 20, 4, "via:unrelated")),
            Nets = fixture.Data.Nets.Add(new(new("net:unrelated"), "UNRELATED", 1)),
        });
        DesignScene metadata = Rebuild(full, data: full.Data with { Copper = [] });
        ScalableCorridorPlanningSession planning = ScalableCorridorAnalyzer.BeginPlanning(
            metadata, new(0, null, false));
        var replays = new List<CorridorReplayScene>();
        for (int index = full.Data.Copper.Length - 1; index >= 0; index--)
        {
            CopperObject item = full.Data.Copper[index];
            if (item.Kind != CopperKind.Via)
            {
                continue;
            }
            DesignScene scene = Rebuild(full, data: full.Data with
            {
                Copper = [item],
                CopperScope = new([CopperKind.Via]),
            });
            var replay = new CorridorReplayScene(scene,
                [new(item.Id, index, "source/" + item.Id.Value, item.Kind)]);
            planning.AcceptReplay(replay);
            planning.AcceptReplay(replay);
            replays.Add(replay);
        }
        Require(planning.RetainedSubjectViaCount == 3,
            "Planning retained unrelated vias or duplicated overlapping tile identities.");
        CorridorReplayScene changedIdentity = replays[1] with
        {
            ObjectIdentities = [replays[1].ObjectIdentities[0] with { CaptureOrdinal = 999 }],
        };
        RequireThrows<InvalidDataException>(() => planning.AcceptReplay(changedIdentity),
            "A repeated planning identity accepted a changed capture ordinal.");
        ScalableCorridorScan scan = Complete(planning.Complete(), full);
        CorridorScan direct = CorridorAnalyzer.Analyze(full, new(0, null, false));
        Require(scan.CorridorCount == direct.CorridorCount &&
                scan.Findings.Select(item => Comparable(item.Finding))
                    .SequenceEqual(direct.Findings.Select(Comparable)),
            "Reversed planning tile arrival changed the global greedy pairing result.");
        Require(scan.PlanningScene.Data.Copper.Length == 0,
            "Tiled planning rebuilt an unbounded Engine copper scene.");
        RequireThrows<InvalidOperationException>(() => planning.AcceptReplay(replays[0]),
            "A completed planning session accepted more copper.");
        return 5;
    }

    private static int CheckSmallFixtureEquivalence()
    {
        DesignScene full = Fixture("PAIR", 0);
        CorridorScan legacy = CorridorAnalyzer.Analyze(full, new(0, null, false));
        ScalableCorridorPlan plan = Plan(full);
        ScalableCorridorScan scalable = Complete(plan, full);

        Require(plan.PairCount == legacy.PairCount &&
                plan.CorridorCount == legacy.CorridorCount,
            "Global via planning changed pair or corridor counts.");
        Require(scalable.Findings.Select(item => Comparable(item.Finding))
                .SequenceEqual(legacy.Findings.Select(Comparable)),
            "The scalable small fixture changed finding ordering or semantics.");
        ScalableCorridorFinding finding = scalable.Findings.Single();
        DesignScene witness = scalable.CreateSourceWitnessScene(finding);
        Require(witness.Data.Copper.Length == 3 &&
                finding.Finding.PositiveViaIndex == 0 &&
                finding.Finding.NegativeViaIndex == 1 &&
                finding.Finding.AggressorIndex == 2 &&
                witness.Data.Copper[0].Id == finding.Witnesses.PositiveVia.Id &&
                witness.Data.Copper[1].Id == finding.Witnesses.NegativeVia.Id &&
                witness.Data.Copper[2].Id == finding.Witnesses.Aggressor.Id,
            "The scan did not retain a bounded three-object source witness scene.");
        Require(!witness.Coverage[DataFamily.Copper].IsComplete &&
                !witness.Coverage[DataFamily.Nets].IsComplete &&
                witness.Coverage[DataFamily.Copper].Reasons.Contains("selected_witness_subset") &&
                witness.Data.CopperScope is { } witnessScope &&
                witnessScope.Details.All(item => !item.IsComplete) &&
                witness.Coverage[DataFamily.Layers] == full.Coverage[DataFamily.Layers],
            "A selected three-object witness fabricated complete copper or net coverage.");
        SceneQuery query = CorridorNavigation.CreateQuery(scalable, finding);
        Require(query.Region is { } ticketScope &&
                witness.Document.Bounds.Contains(ticketScope),
            "The selected witness scene cannot admit the padded ticket viewport.");
        DesignScene fresh = FreshRegion(full, query);
        var document = new WorkspaceDocumentIdentity(
            "scalable-session", 1, 2, 1234, "fixture.brd", "PD_V25");
        EngineWitnessMatch matched = CorridorNavigation.MatchWitnesses(
            scalable,
            finding,
            fresh,
            document,
            document);
        Require(matched.MatchedCount == 3,
            "Scalable findings could not use existing fresh Engine witness matching.");
        return 5;
    }

    private static int CheckBatchingAndObjectKinds()
    {
        DesignScene first = Fixture("ONE", 0);
        ImmutableArray<CopperObject> copper =
        [
            .. first.Data.Copper,
            Via("TWO_P", 40, 30, "via:two-p"),
            Via("TWO_N", 140, 31, "via:two-n"),
            Segment("LONG_TRACE", 90, -2_000, 2_000, 32, "trace:long"),
            Via("SPANNING_VIA", 25, 33, "via:spanning"),
            // First corridor broad-phase minimum is exactly -61 mil; this
            // shape touches that boundary without crossing it.
            Shape("SHAPE_SIGNAL", -70, -8, -61, 8, 34, "shape:edge"),
            Segment(null, 0, -20, 20, 35, "trace:disconnected"),
        ];
        SceneData data = first.Data with
        {
            Nets =
            [
                .. first.Data.Nets,
                new(new("net:two-p"), "TWO_P", 1),
                new(new("net:two-n"), "TWO_N", 1),
                new(new("net:long"), "LONG_TRACE", 1),
                new(new("net:spanning"), "SPANNING_VIA", 1),
                new(new("net:shape"), "SHAPE_SIGNAL", 1),
            ],
            Copper = copper,
        };
        DesignScene full = Rebuild(first, data: data);
        ScalableCorridorPlan plan = Plan(full);
        Require(plan.CorridorCount == 2 && plan.Batches.Count == 1,
            "Overlapping same-layer corridors were not grouped deterministically.");
        CorridorReplayBatch batch = plan.Batches.Single();
        Require(batch.Region.Contains(new DesignPoint(batch.Region.Maximum.X, 0)),
            "Batch bounds lost their inclusive maximum edge.");

        ScalableCorridorScan scan = Complete(plan, full);
        string[] kinds = scan.Findings.Select(item => item.Finding.ObjectType).ToArray();
        Require(kinds.Contains("cline_segment") && kinds.Contains("via") && kinds.Contains("shape"),
            "Long traces, spanning vias, or edge-touching shapes were lost.");
        Require(scan.Findings.Any(item => item.Finding.AggressorNet == "(unassigned)" &&
                item.Witnesses.Aggressor.NetName is null &&
                item.Witnesses.Aggressor.Id.Value == "trace:disconnected"),
            "Disconnected crossing copper was dropped or its missing net assignment was invented.");
        Require(scan.Findings.Select(item => item.Witnesses.AggressorIdentity.SourceIdentity)
                .Distinct(StringComparer.Ordinal).Count() == scan.Findings.Count,
            "Overlapping replays defeated global stable-identity seen semantics.");
        CorridorScan direct = CorridorAnalyzer.Analyze(full, new(0, null, false));
        Require(scan.Findings.Select(item => Comparable(item.Finding))
                .SequenceEqual(direct.Findings.Select(Comparable)),
            "Incremental candidate reduction changed global pair ownership or finding order.\n" +
                string.Join("\n", scan.Findings.Select(item => "scalable: " + Comparable(item.Finding))) +
                "\n" + string.Join("\n", direct.Findings.Select(item => "direct: " + Comparable(item))));
        return 6;
    }

    private static int CheckBatchScenesAreReleased()
    {
        DesignScene full = Fixture("RETENTION", 0);
        ScalableCorridorPlan plan = Plan(full);
        ScalableCorridorScanSession session = plan.BeginScan();
        WeakReference<DesignScene>[] replayScenes = plan.Batches
            .Select(batch => AcceptWithoutRetainingScene(session, full, batch))
            .ToArray();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Require(replayScenes.All(reference => !reference.TryGetTarget(out _)),
            "The scan session retained a complete batch scene after admission.");
        Require(session.Complete().Findings.Count == 1,
            "Releasing replay scenes discarded the retained finding witnesses.");
        GC.KeepAlive(session);
        return 2;
    }

    private static int CheckBatchArrivalOrder()
    {
        DesignScene original = Fixture("MULTILAYER", 0);
        LayerId extraLayer = new("ETCH/S04");
        ImmutableArray<CopperObject> copper = original.Data.Copper
            .Add(Via("OTHER_SIGNAL", 25, 3, "via:multi-layer"))
            .Select(item => item.Via is { } via
                ? item with
                {
                    Via = via with
                    {
                        OriginalLayers = via.OriginalLayers.Add(extraLayer),
                        ActiveLayers = via.ActiveLayers.Add(extraLayer),
                    },
                }
                : item)
            .ToImmutableArray();
        DesignScene full = Rebuild(original, data: original.Data with
        {
            Nets = original.Data.Nets.Add(new(new("net:other-signal"), "OTHER_SIGNAL", 1)),
            Layers = original.Data.Layers.Add(new(extraLayer, 3, false)),
            Copper = copper,
        });
        ScalableCorridorPlan plan = Plan(full);
        Require(plan.Batches.Count == 2, "The multilayer fixture did not exercise two replay batches.");
        ScalableCorridorScanSession reversed = plan.BeginScan();
        foreach (CorridorReplayBatch batch in plan.Batches.Reverse())
        {
            reversed.AcceptBatch(batch, Replay(full, batch));
        }
        ScalableCorridorScan reversedScan = reversed.Complete();
        CorridorScan direct = CorridorAnalyzer.Analyze(full, new(0, null, false));
        Require(reversedScan.Findings.Select(item => Comparable(item.Finding))
                .SequenceEqual(direct.Findings.Select(Comparable)),
            "Batch arrival order changed multilayer findings or global deduplication.");
        return 2;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference<DesignScene> AcceptWithoutRetainingScene(
        ScalableCorridorScanSession session,
        DesignScene full,
        CorridorReplayBatch batch)
    {
        CorridorReplayScene replay = Replay(full, batch);
        session.AcceptBatch(batch, replay);
        return new(replay.Scene);
    }

    private static int CheckScalablePairPolicies()
    {
        DesignScene suffix = Fixture("DECLARED", 0);
        SceneQuery query = suffix.Query with
        {
            Families = suffix.Query.Families.Add(DataFamily.Connectivity),
        };
        DesignScene declared = new(
            suffix.Identity,
            suffix.Document,
            query,
            Coverage(query, true),
            suffix.Data with
            {
                Xnets =
                [
                    new(new("x:positive"), "X_POSITIVE", ["DECLARED_P"]),
                    new(new("x:negative"), "X_NEGATIVE", ["DECLARED_N"]),
                ],
                DifferentialPairs =
                [new(new("pair:declared"), "DECLARATION_NAME", "X_POSITIVE", "X_NEGATIVE")],
            });
        var options = new CorridorOptions(0, null, false, CorridorPairPolicy.DeclaredPairs);
        ScalableCorridorScan scan = Complete(Plan(declared, options), declared);
        CorridorScan direct = CorridorAnalyzer.Analyze(declared, options);
        Require(scan.Findings.Select(item => Comparable(item.Finding))
                .SequenceEqual(direct.Findings.Select(Comparable)) &&
                scan.Findings.Single().Finding.PairName == "DECLARATION_NAME",
            "Scalable declared-pair mode silently used suffix discovery.");
        DesignScene absent = Rebuild(declared, data: declared.Data with { DifferentialPairs = [] });
        Require(Complete(Plan(absent, options), absent).PairCount == 0 &&
                Complete(Plan(absent), absent).PairCount == 1,
            "Declared mode fell back to suffix pairs when declarations were absent.");
        DesignScene incomplete = Rebuild(declared, data: declared.Data with
        {
            DifferentialPairs = [],
            Xnets =
            [new(new("x:orphan"), "X_ORPHAN", ["DECLARED_P"], "MISSING_DECLARATION")],
        });
        ScalableCorridorScan incompleteScan = Complete(Plan(incomplete, options), incomplete);
        Require(incompleteScan.PairCount == 0 && incompleteScan.CoverageWarnings.Count > 0,
            "Incomplete declared pair metadata was silently accepted as clear.");
        DesignScene ambiguous = Rebuild(declared, data: declared.Data with
        {
            Xnets =
            [
                new(new("x:positive"), "X_POSITIVE", ["DECLARED_P", "CLOCK_TEST"]),
                new(new("x:negative"), "X_NEGATIVE", ["DECLARED_N"]),
            ],
        });
        ScalableCorridorScan ambiguousScan = Complete(Plan(ambiguous, options), ambiguous);
        Require(ambiguousScan.PairCount == 0 && ambiguousScan.CoverageWarnings.Count > 0,
            "Ambiguous declared polarity was silently replaced by suffix pairing.");
        return 4;
    }

    private static int CheckIdentifierIndependenceAndNegativeControl()
    {
        ScalableCorridorScan baseline = Complete(Plan(Fixture("BASE", 0)), Fixture("BASE", 0));
        DesignScene renamed = Fixture("RENAMED_BUS_77", 0, "renamed-");
        ScalableCorridorScan renamedScan = Complete(Plan(renamed), renamed);
        Require(baseline.Findings.Count == renamedScan.Findings.Count &&
                baseline.Findings.Zip(renamedScan.Findings).All(pair =>
                    Equals(Geometry(pair.First.Finding), Geometry(pair.Second.Finding))),
            "Changed identifiers changed equivalent geometric results.");

        DesignScene moved = Fixture("MOVED", 500);
        ScalableCorridorScan movedScan = Complete(Plan(moved), moved);
        Require(movedScan.Findings.Count == 0,
            "A deliberately moved nonintersecting obstacle remained a finding.");
        return 2;
    }

    private static int CheckFailureAndPartialSemantics()
    {
        DesignScene full = Fixture("FAILURE", 0);
        ScalableCorridorPlan plan = Plan(full);
        ScalableCorridorScanSession missing = plan.BeginScan();
        RequireThrows<InvalidOperationException>(() => missing.Complete(),
            "A missing replay batch produced a scan.");

        ScalableCorridorScanSession cancelled = plan.BeginScan();
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            RequireThrows<OperationCanceledException>(
                () => cancelled.AcceptBatch(
                    plan.Batches[0], Replay(full, plan.Batches[0]), cancellation.Token),
                "Cancelled batch admission continued.");
        }

        ScalableCorridorScanSession partial = plan.BeginScan();
        foreach (CorridorReplayBatch batch in plan.Batches)
        {
            CorridorReplayScene replay = Replay(full, batch);
            CoverageReport coverage = Coverage(replay.Scene.Query, false, "bounded_out");
            partial.AcceptBatch(batch, replay with
            {
                Scene = Rebuild(replay.Scene, coverage: coverage),
            });
        }
        ScalableCorridorScan partialScan = partial.Complete();
        Require(!partialScan.HasCompleteInputs &&
                partialScan.BlockingCoverageWarnings.Count > 0 &&
                partialScan.CoverageWarnings.Any(message =>
                    message.Contains("No clear conclusion", StringComparison.Ordinal)),
            "Incomplete replay coverage was presented as clear.");
        return 4;
    }

    private static int CheckKnownViaMetadataIsAdvisory()
    {
        DesignScene full = Fixture("VIA_METADATA", 0);
        ImmutableArray<CopperObject> vias = full.Data.Copper
            .Where(item => item.Kind == CopperKind.Via)
            .ToImmutableArray();
        CoverageReport planningCoverage = Coverage(
            full.Query,
            false,
            "via:active_via_layers",
            "via:backdrill");
        DesignScene planning = Rebuild(full, coverage: planningCoverage, data: full.Data with
        {
            Copper = vias,
            CopperScope = ViaMetadataAdvisoryScope([CopperKind.Via]),
        });
        var ordinals = full.Data.Copper.Select((item, index) => (item.Id, index))
            .ToDictionary(item => item.Id, item => item.index);
        CorridorSourceIdentity[] identities = vias.Select(item => new CorridorSourceIdentity(
            item.Id,
            ordinals[item.Id],
            "source/" + item.Id.Value,
            item.Kind)).ToArray();
        ScalableCorridorPlan plan = ScalableCorridorAnalyzer.CreatePlan(
            new(planning, identities),
            new(0, null, false));
        Require(plan.PlanningWarnings.Any(message =>
                    message.Contains("via:active_via_layers", StringComparison.Ordinal)) &&
                plan.PlanningBlockingWarnings.Count == 0,
            "Known global via metadata gaps were blocked instead of classified for conservative review.");

        ScalableCorridorScanSession session = plan.BeginScan();
        foreach (CorridorReplayBatch batch in plan.Batches)
        {
            CorridorReplayScene replay = Replay(full, batch);
            DesignScene advisory = Rebuild(
                replay.Scene,
                coverage: Coverage(
                    replay.Scene.Query,
                    false,
                    "via:active_via_layers",
                    "via:backdrill"),
                data: replay.Scene.Data with
                {
                    CopperScope = ViaMetadataAdvisoryScope(
                        [CopperKind.Trace, CopperKind.Via, CopperKind.Shape]),
                });
            session.AcceptBatch(batch, replay with { Scene = advisory });
        }
        ScalableCorridorScan scan = session.Complete();
        Require(scan.HasCompleteInputs && scan.BlockingCoverageWarnings.Count == 0,
            "Known via metadata gaps became blocking even though every original via layer was screened.");
        Require(scan.CoverageWarnings.Any(message =>
                message.Contains("original via layers are screened conservatively", StringComparison.Ordinal)),
            "The conservative via fallback did not remain visible as a review warning.");
        Require(scan.Findings.Count == 1,
            "Advisory via metadata gaps suppressed the otherwise complete corridor result.");
        return 4;
    }

    private static int CheckDetailedContourEvidence()
    {
        DesignScene traceFixture = Fixture("DETAIL", 0);
        CopperObject scalarShape = Shape(
            "CLOCK_TEST",
            -8,
            -24,
            8,
            24,
            2,
            "shape:detailed");
        DesignScene scalar = Rebuild(traceFixture, data: traceFixture.Data with
        {
            Copper = [traceFixture.Data.Copper[0], traceFixture.Data.Copper[1], scalarShape],
        });
        CorridorScan scan = CorridorAnalyzer.Analyze(scalar, new(0, null, false));
        CorridorFinding finding = scan.Findings.Single();
        SceneQuery query = CorridorNavigation.CreateQuery(scan, finding);
        WorkspaceDocumentIdentity document = new(
            "detail-session",
            3,
            4,
            4321,
            "fixture.brd",
            "PD_V25");
        Guid operationId = Guid.NewGuid();

        CopperObject oneHoleShape = WithDetailedShapeGeometry(scalarShape, 1);
        DesignScene oneHoleFresh = FreshRegion(
            Rebuild(scalar, data: scalar.Data with
            {
                Copper = [scalar.Data.Copper[0], scalar.Data.Copper[1], oneHoleShape],
                CopperScope = DetailedCopperScope(),
            }),
            query);
        CorridorNavigationEvidence oneHole = CorridorNavigation.MatchWitnessesWithEvidence(
            scan,
            finding,
            oneHoleFresh,
            document,
            document,
            operationId,
            new(10, 20, 30));
        Require(oneHole.Witnesses.VerificationScope ==
                EngineWitnessVerificationScope.SelectedCapturedFields &&
                oneHole.DetailedEvidence.Status == CorridorDetailedEvidenceStatus.Complete &&
                oneHole.DetailedEvidence.ResolvedRegionCount == 2 &&
                oneHole.DetailedEvidence.HoleCount == 1 &&
                ReferenceEquals(oneHole.DetailedEvidence.FreshScene, oneHoleFresh) &&
                oneHole.DetailedEvidence.NativeOperationId == operationId,
            "Complete fresh shape topology, observation identity, or operation correlation was not retained.");

        CopperObject twoHoleShape = WithDetailedShapeGeometry(scalarShape, 2);
        DesignScene twoHoleFresh = FreshRegion(
            Rebuild(scalar, data: scalar.Data with
            {
                Copper = [scalar.Data.Copper[0], scalar.Data.Copper[1], twoHoleShape],
                CopperScope = DetailedCopperScope(),
            }),
            query);
        CorridorNavigationEvidence twoHoles = CorridorNavigation.MatchWitnessesWithEvidence(
            scan,
            finding,
            twoHoleFresh,
            document,
            document,
            Guid.NewGuid());
        Require(twoHoles.Witnesses.MatchedCount == 3 &&
                twoHoles.DetailedEvidence.HoleCount == 2,
            "Selected-field witness matching stopped navigation or lost the actual fresh hole topology.");

        DesignScene missingContours = FreshRegion(
            Rebuild(scalar, data: scalar.Data with
            {
                Copper = [scalar.Data.Copper[0], scalar.Data.Copper[1], scalarShape],
                CopperScope = DetailedCopperScope(),
            }),
            query);
        CorridorNavigationEvidence incomplete = CorridorNavigation.MatchWitnessesWithEvidence(
            scan,
            finding,
            missingContours,
            document,
            document,
            Guid.NewGuid());
        Require(incomplete.DetailedEvidence.Status == CorridorDetailedEvidenceStatus.Incomplete &&
                !incomplete.DetailedEvidence.HasCompleteShapeContours &&
                incomplete.DetailedEvidence.Diagnostics.Any(message =>
                    message.Contains("No current resolved-copper contour", StringComparison.Ordinal)),
            "Missing current contours were presented as complete or clear evidence.");
        return 3;
    }

    private static CopperObject WithDetailedShapeGeometry(
        CopperObject scalar,
        int holeCount)
    {
        var layer = new LayerId("ETCH/S03");
        CurveLoopGeometry shell = RectangleLoop(new(
            new(-8, -24),
            new(8, 24)));
        ImmutableArray<CurveLoopGeometry> holes = Enumerable.Range(0, holeCount)
            .Select(index => new CurveLoopGeometry(
                [new CircleGeometry(new(-2 + index * 4, 0), new Length(1))]))
            .ToImmutableArray();
        var primary = new RegionGeometry(shell, holes);
        var nestedIsland = new RegionGeometry(
            RectangleLoop(new(new(-1, -1), new(1, 1))),
            []);
        var topology = new RegionSetGeometry([primary, nestedIsland]);
        var source = new NativeGeometrySource(
            "allegro",
            "qualified-contour",
            "family-token",
            "shape:detailed",
            []);
        var record = new GeometryRecord(
            GeometryRole.ResolvedCopper,
            topology,
            GeometryOrigin.NativeReadback,
            GeometryFidelity.NativeContour,
            GeometryValidity.Current,
            new Length(0.001m),
            source,
            []);
        return scalar with
        {
            Surfaces = [new(layer, "shape", topology, GeometryFidelity.NativeContour, [])],
            Geometry = new([new(layer, record)], []),
        };
    }

    private static CurveLoopGeometry RectangleLoop(DesignBounds bounds)
    {
        DesignPoint lowerLeft = bounds.Minimum;
        DesignPoint lowerRight = new(bounds.Maximum.X, bounds.Minimum.Y);
        DesignPoint upperRight = bounds.Maximum;
        DesignPoint upperLeft = new(bounds.Minimum.X, bounds.Maximum.Y);
        return new([
            new LineGeometry(lowerLeft, lowerRight),
            new LineGeometry(lowerRight, upperRight),
            new LineGeometry(upperRight, upperLeft),
            new LineGeometry(upperLeft, lowerLeft),
        ]);
    }

    private static CopperReadScope DetailedCopperScope() => new(
        Enum.GetValues<CopperKind>().ToImmutableArray(),
        Enum.GetValues<CopperKind>().Select(kind => new CopperKindCoverage(
            kind,
            DataAvailability.Available,
            DataCompleteness.CompleteForRequestedScope,
            GeometryFidelity.NativeContour,
            [])).ToImmutableArray());

    private static CopperReadScope ViaMetadataAdvisoryScope(
        ImmutableArray<CopperKind> requestedKinds) => new(
        requestedKinds.Where(kind => kind != CopperKind.Via).ToImmutableArray(),
        Enum.GetValues<CopperKind>().Select(kind =>
        {
            if (!requestedKinds.Contains(kind))
            {
                return new CopperKindCoverage(
                    kind,
                    DataAvailability.NotRequested,
                    DataCompleteness.Partial,
                    GeometryFidelity.Unknown,
                    []);
            }
            return kind == CopperKind.Via
                ? new CopperKindCoverage(
                    kind,
                    DataAvailability.Available,
                    DataCompleteness.Partial,
                    GeometryFidelity.BoundsOnly,
                    ["via:active_via_layers", "via:backdrill"])
                : new CopperKindCoverage(
                    kind,
                    DataAvailability.Available,
                    DataCompleteness.CompleteForRequestedScope,
                    GeometryFidelity.BoundsOnly,
                    []);
        }).ToImmutableArray());

    private static ScalableCorridorPlan Plan(
        DesignScene full,
        CorridorOptions? options = null,
        CorridorSourceTraversal? sourceTraversal = null,
        bool compactCapture = false)
    {
        ImmutableArray<CopperObject> vias = full.Data.Copper
            .Where(item => item.Kind == CopperKind.Via)
            .ToImmutableArray();
        DesignScene planning = Rebuild(full, data: full.Data with
        {
            Copper = vias,
            CopperScope = new([CopperKind.Via]),
        });
        var ordinals = full.Data.Copper.Select((item, index) => (item.Id, index))
            .ToDictionary(item => item.Id, item => item.index);
        CorridorSourceIdentity[] identities = vias.Select(item => new CorridorSourceIdentity(
            item.Id,
            ordinals[item.Id],
            "source/" + item.Id.Value,
            item.Kind)).ToArray();
        var replay = new CorridorReplayScene(planning, identities);
        if (sourceTraversal is not null)
        {
            replay = WithSourceTraversal(replay, sourceTraversal, compactCapture);
        }
        return ScalableCorridorAnalyzer.CreatePlan(replay, options ?? new(0, null, false));
    }

    private static ScalableCorridorScan Complete(
        ScalableCorridorPlan plan,
        DesignScene full,
        bool compactCapture = false)
    {
        ScalableCorridorScanSession session = plan.BeginScan();
        foreach (CorridorReplayBatch batch in plan.Batches)
        {
            CorridorReplayScene replay = Replay(full, batch);
            if (plan.SourceTraversal is { } traversal)
            {
                replay = WithSourceTraversal(replay, traversal, compactCapture);
            }
            session.AcceptBatch(batch, replay);
        }
        return session.Complete();
    }

    private static CorridorReplayScene Replay(
        DesignScene full,
        CorridorReplayBatch batch)
    {
        SceneQuery query = batch.CreateQuery();
        ImmutableArray<CopperObject> copper = full.Data.Copper
            .Where(item => item.Bounds.Intersects(batch.Region))
            .ToImmutableArray();
        var ordinals = full.Data.Copper.Select((item, index) => (item.Id, index))
            .ToDictionary(item => item.Id, item => item.index);
        CorridorSourceIdentity[] identities = copper.Select(item => new CorridorSourceIdentity(
            item.Id,
            ordinals[item.Id],
            "source/" + item.Id.Value,
            item.Kind)).ToArray();
        DesignScene scene = new(
            new(Guid.NewGuid(), DateTimeOffset.UtcNow,
                new("test-engine", "1", false, "bounded-replay")),
            full.Document with { Bounds = batch.Region },
            query,
            Coverage(query, true),
            new SceneData
            {
                Nets = query.Families.Contains(DataFamily.Nets) ? full.Data.Nets : [],
                Layers = full.Data.Layers,
                Copper = copper,
                CopperScope = new([CopperKind.Trace, CopperKind.Via, CopperKind.Shape]),
            });
        return new(scene, identities);
    }

    private static DesignScene Fixture(
        string pair,
        double aggressorOffset,
        string idPrefix = "")
    {
        SceneQuery query = CorridorAnalyzer.CreateSceneQuery();
        ImmutableArray<LayerObject> layers =
        [
            new(new("ETCH/TOP"), 0, false),
            new(new("ETCH/S03"), 1, false),
            new(new("ETCH/BOTTOM"), 2, false),
        ];
        var data = new SceneData
        {
            Nets =
            [
                new(new(idPrefix + "net:p"), pair + "_P", 1),
                new(new(idPrefix + "net:n"), pair + "_N", 1),
                new(new(idPrefix + "net:a"), idPrefix + "CLOCK_TEST", 1),
            ],
            Layers = layers,
            Modules = [],
            Copper =
            [
                Via(pair + "_P", -50, 0, idPrefix + "via:p"),
                Via(pair + "_N", 50, 1, idPrefix + "via:n"),
                Segment(idPrefix + "CLOCK_TEST", 0,
                    aggressorOffset == 0 ? -20 : aggressorOffset,
                    aggressorOffset == 0 ? 20 : aggressorOffset,
                    2,
                    idPrefix + "trace:a"),
            ],
            CopperScope = new([CopperKind.Trace, CopperKind.Via, CopperKind.Shape]),
        };
        return new(
            new(Guid.NewGuid(), DateTimeOffset.UtcNow,
                new("test-engine", "1", false, "fixture")),
            new(DocumentKind.PcbBoard, "fixture.brd", "mils", 2,
                new(new(-3_000, -3_000), new(3_000, 3_000))),
            query,
            Coverage(query, true),
            data);
    }

    private static CopperObject Via(
        string? net,
        decimal x,
        int ordinal,
        string id)
    {
        DesignPoint position = new(x, 0);
        ImmutableArray<LayerId> layers =
            [new("ETCH/TOP"), new("ETCH/S03"), new("ETCH/BOTTOM")];
        ImmutableArray<ViaPadMeasurement> pads =
        [
            new(new("ETCH/S03"), new("ETCH/S03"), "regular", "CIRCLE", new(12), new(12)),
            new(new("ETCH/S03"), new("ETCH/S03"), "antipad", "CIRCLE", new(20), new(20)),
        ];
        var via = new ViaSpan(
            "PAD_SAMPLE",
            position,
            layers,
            layers,
            "not_started",
            true,
            new(true, null, null, null, null, null, pads));
        return new(new(id), CopperKind.Via, net, null,
            new(new(x - 6, -6), new(x + 6, 6)), null, null, via, [], null, []);
    }

    private static CopperObject Segment(
        string? net,
        decimal x,
        double startY,
        double endY,
        int ordinal,
        string id)
    {
        DesignPoint first = new(x, checked((decimal)startY));
        DesignPoint second = new(x, checked((decimal)endY));
        DesignBounds bounds = new(
            new(Math.Min(first.X, second.X) - 2, Math.Min(first.Y, second.Y) - 2),
            new(Math.Max(first.X, second.X) + 2, Math.Max(first.Y, second.Y) + 2));
        return new(new(id), CopperKind.Trace, net, new("ETCH/S03"), bounds,
            new LineGeometry(first, second), new Length(4), null, [], null, []);
    }

    private static CopperObject Shape(
        string net,
        decimal minimumX,
        decimal minimumY,
        decimal maximumX,
        decimal maximumY,
        int ordinal,
        string id) =>
        new(new(id), CopperKind.Shape, net, new("ETCH/S03"),
            new(new(minimumX, minimumY), new(maximumX, maximumY)),
            null, null, null, [], false, []);

    private static DesignScene Rebuild(
        DesignScene source,
        CoverageReport? coverage = null,
        SceneData? data = null) =>
        new(source.Identity, source.Document, source.Query,
            coverage ?? source.Coverage, data ?? source.Data);

    private static DesignScene FreshRegion(DesignScene source, SceneQuery query)
    {
        DesignBounds bounds = query.Region ?? throw new InvalidOperationException(
            "Fresh corridor query has no region.");
        return new(
            new(Guid.NewGuid(), DateTimeOffset.UtcNow,
                new("test-engine", "1", false, "fresh-scalable-region")),
            source.Document with { Bounds = bounds },
            query,
            Coverage(query, true),
            new SceneData
            {
                Layers = source.Data.Layers,
                Copper = source.Data.Copper,
                CopperScope = source.Data.CopperScope,
            });
    }

    private static CoverageReport Coverage(
        SceneQuery query,
        bool complete,
        params string[] reasons) =>
        new(query.Families.Select(family => new FamilyCoverage(
            family,
            DataAvailability.Available,
            complete || family != DataFamily.Copper
                ? DataCompleteness.CompleteForRequestedScope
                : DataCompleteness.Partial,
            Reasons: family == DataFamily.Copper ? reasons.ToImmutableArray() : [])));

    private static object Comparable(CorridorFinding finding) => new
    {
        finding.Id,
        finding.PairName,
        finding.AggressorNet,
        finding.ObjectType,
        finding.Layer,
        finding.Category,
        finding.Risk,
        finding.P,
        finding.N,
        finding.Intrusion,
        finding.DistanceMils,
        finding.HalfWidthMils,
        finding.HalfLengthMils,
        finding.WidthSourceLayer,
    };

    private static object Geometry(CorridorFinding finding) => new
    {
        finding.ObjectType,
        finding.Layer,
        finding.Risk,
        finding.P,
        finding.N,
        finding.Intrusion,
        finding.DistanceMils,
        finding.HalfWidthMils,
        finding.HalfLengthMils,
    };

    private static void RequireThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Scalable corridor check failed: " + message);
        }
    }
}
