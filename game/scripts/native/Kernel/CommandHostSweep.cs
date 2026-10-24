using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;

namespace GarageGame.Native.Kernel;

/// <summary>
/// The host's sight sweep, spread over physics ticks (S12). On the 10x island one sweep casts some 1,300 rays at about 20 µs each,
/// which in one tick made physics peaks of 30 ms and more. Now a sweep is the same work in the same order, done in steps:
/// - it begins as before (the entities, the levels, the things looked at in turn, the team's lock owners and the room revision,
///   read once), and each avatar's eye and body are taken as they are then, so every ray is cast from where the eye was when the
///   sweep began, as the one-tick sweep did;
/// - then, avatar by avatar, as before: its sight of each thing looked at (Perceive), what it remembers out of sight checked
///   (Remember's first half), what it sees remembered afresh and the bound kept (its second half), and the space it discovers;
/// - each tick does steps until it has used DefaultSweepTickBudgetMs, unless it is behind its share of the sweep, so the sweep always
///   ends before the next is due; a sweep due every tick (tests, a tiny room) is done whole in its tick, as before.
/// In a world that does not change during the sweep, its results are the one-tick sweep's.
/// </summary>
public partial class CommandHost
{
    /// <summary>How long a physics tick works on the sweep before leaving the rest to the next tick (while on schedule).</summary>
    public const double DefaultSweepTickBudgetMs = 2.0;

    /// <summary>SweepTickBudgetMs for this host; a test sets 0 to see a sweep spread over all its ticks.</summary>
    internal double SweepTickBudgetMs { get; set; } = DefaultSweepTickBudgetMs;

    /// <summary>The sweep under way, or null between sweeps.</summary>
    private SweepRun? _sweep;

    /// <summary>Sweeps finished since the host started (a test's and the probe's count).</summary>
    internal int SweepsDone { get; private set; }

    /// <summary>Whether a sweep is under way, spread over ticks.</summary>
    internal bool SweepUnderWay => _sweep != null;

    private enum SweepStage { Perceive, Recall, Fresh, Candidates, Discover, Done }

    /// <summary>A thing a sweep looks at, read from its summary once.</summary>
    private readonly record struct SweepTarget(string Id, string Kind, string? HeldBy, Aabb Bounds);

    /// <summary>One avatar's part of a sweep, from where its eye was when the sweep began.</summary>
    private sealed class SweepPass
    {
        public required SmallPlayerController Body { get; init; }
        public required string Avatar { get; init; }
        public required Vector3 Eye { get; init; }
        public required Rid Exclude { get; init; }
        public SweepStage Stage = SweepStage.Perceive;
        public int Next;
        public readonly HashSet<string> Sight = new(StringComparer.Ordinal);
        public List<string>? Remembered;
        public readonly List<DiscoverCandidate> Candidates = new();
    }

    private sealed class SweepRun
    {
        public required List<JsonObject> Entities { get; init; }
        public required Dictionary<string, JsonObject> ById { get; init; }
        public required List<SweepTarget> Looked { get; init; }
        public required HashSet<string> Considered { get; init; }
        public required Dictionary<string, string> Owners { get; init; }
        public required PerceptionMemory Memory { get; init; }
        public required int Revision { get; init; }
        public required List<SweepPass> Passes { get; init; }
        /// <summary>The canonical text of each summary without its place, made once a sweep for SameAsSeen.</summary>
        public readonly Dictionary<JsonObject, string> UnplacedTexts = new(ReferenceEqualityComparer.Instance);
        public int Pass;
        /// <summary>The work left, in rays at most (15 a box, one a cell), and the ticks left to do it in.</summary>
        public double Estimate;
        public int TicksLeft;
    }

    /// <summary>
    /// The host's sight sweep: what each avatar's eyes see goes into the team's map, entities and space. Each sweep is
    /// bounded: at most SweepEntitiesPerAvatar things are looked at per avatar, in turn, and at most DiscoverRaysPerSweep
    /// cells, nearest first among those not tried lately. Its work is spread over the ticks before the next sweep is due.
    /// </summary>
    private void SightSweep(double delta)
    {
        if (TeamSightIntervalS <= 0 || Authority == null || !Authority.Call(KernelNames.IsReady).AsBool())
        {
            _sweep = null;
            return;
        }
        _sightClock += delta;
        _saveClock += delta;
        if (_sightClock >= TeamSightIntervalS)
        {
            _sightClock = 0;
            // A sweep still under way when the next is due (the interval was shortened) is finished first.
            if (_sweep != null) ContinueSweep(whole: true);
            _sweep = BeginSweep(delta);
        }
        if (_sweep != null) ContinueSweep(whole: false);
        if (_teamDirty && _saveClock >= TeamSaveIntervalS) SaveTeamOrBoth();
    }

    /// <summary>Test seam: finish the sweep under way now, in this tick.</summary>
    internal void FinishSweep()
    {
        if (_sweep != null) ContinueSweep(whole: true);
    }

    private SweepRun BeginSweep(double delta)
    {
        _sweepCount++;
        var entities = Entities();
        RefreshLevels(entities);
        var things = entities.Where(e => e["kind"]!.GetValue<string>() is "object" or "creation").ToList();
        var turn = things.Count <= SweepEntitiesPerAvatar ? things
            : Enumerable.Range(0, SweepEntitiesPerAvatar).Select(i => things[(_sweepEntityCursor + i) % things.Count]).ToList();
        _sweepEntityCursor = things.Count == 0 ? 0 : (_sweepEntityCursor + SweepEntitiesPerAvatar) % things.Count;
        var looked = entities.Where(e => e["kind"]!.GetValue<string>() is "avatar" or "shell").Concat(turn)
            .Select(e => new SweepTarget(e["id"]!.GetValue<string>(), e["kind"]!.GetValue<string>(), e["held_by"]?.GetValue<string>(), BoundsOf(e))).ToList();
        var considered = new HashSet<string>(looked.Select(t => t.Id), StringComparer.Ordinal);
        // What the map remembers that is no longer in the room is looked for too (its place, seen empty, drops it), in turn.
        var live = new HashSet<string>(entities.Select(e => e["id"]!.GetValue<string>()), StringComparer.Ordinal);
        var memory = MemoryOf(CompanionPrincipal);
        var gone = memory.Order.Where(id => !live.Contains(id)).ToList();
        if (gone.Count > 0)
        {
            foreach (var id in Enumerable.Range(0, Math.Min(SweepEntitiesPerAvatar, gone.Count)).Select(i => gone[(_sweepGoneCursor + i) % gone.Count])) considered.Add(id);
            _sweepGoneCursor = (_sweepGoneCursor + SweepEntitiesPerAvatar) % gone.Count;
        }
        var passes = new List<SweepPass>(2);
        foreach (var (body, avatar) in new (SmallPlayerController?, string)[] { (Player, PlayerAvatar), (Companion, CompanionAvatarId) })
        {
            if (body == null || !IsInstanceValid(body) || !body.IsInsideTree()) continue;
            passes.Add(new SweepPass { Body = body, Avatar = avatar, Eye = body.EyeCamera.GlobalPosition, Exclude = body.GetRid() });
        }
        var run = new SweepRun
        {
            Entities = entities, ById = entities.ToDictionary(e => e["id"]!.GetValue<string>(), StringComparer.Ordinal), Looked = looked,
            Considered = considered, Owners = LockOwners(), Memory = memory, Revision = Revision, Passes = passes,
        };
        var remembered = memory.Order.Count(considered.Contains);
        run.Estimate = passes.Count * (looked.Count * SampleOffsets.Length + remembered * SampleOffsets.Length + DiscoverRaysPerSweep + 1);
        // The ticks before the next sweep is due, less two to spare: at 60 ticks a second and a quarter-second interval, 13.
        run.TicksLeft = Math.Max(1, (int)Math.Floor(TeamSightIntervalS / Math.Max(delta, 1e-6) + 1e-9) - 2);
        return run;
    }

    /// <summary>Steps of the sweep under way: this tick's share (whole: all of what is left).</summary>
    private void ContinueSweep(bool whole)
    {
        var run = _sweep!;
        // The team's map was replaced (a test cleared it, a load): this sweep's reading of it is stale, and the next sweep starts afresh.
        if (!_memory.TryGetValue(TeamKey, out var memory) || memory != run.Memory)
        {
            _sweep = null;
            return;
        }
        whole |= run.TicksLeft <= 1;
        var share = whole ? double.MaxValue : run.Estimate / run.TicksLeft;
        var clock = Stopwatch.StartNew();
        double done = 0;
        while (run.Pass < run.Passes.Count)
        {
            if (!whole && done >= share && clock.Elapsed.TotalMilliseconds >= SweepTickBudgetMs) break;
            var step = SweepStep(run, run.Passes[run.Pass]);
            done += step;
            run.Estimate = Math.Max(0, run.Estimate - step);
        }
        run.TicksLeft--;
        if (run.Pass < run.Passes.Count) return;
        _sweep = null;
        _teamDirty = true;
        SweepsDone++;
    }

    /// <summary>One step of an avatar's pass; returns the work it stood for (rays at most).</summary>
    private double SweepStep(SweepRun run, SweepPass pass)
    {
        if (!IsInstanceValid(pass.Body) || !pass.Body.IsInsideTree()) pass.Stage = SweepStage.Done;
        switch (pass.Stage)
        {
            case SweepStage.Perceive:
            {
                // Perceive: its own avatar, the shell it stands in and whatever it holds are always perceived.
                if (pass.Next >= run.Looked.Count)
                {
                    pass.Stage = SweepStage.Recall;
                    pass.Next = 0;
                    return 0;
                }
                var target = run.Looked[pass.Next++];
                if (target.Id == pass.Avatar || target.Kind == "shell" || target.HeldBy == pass.Avatar)
                {
                    pass.Sight.Add(target.Id);
                    return 0;
                }
                if (SeesBoxFrom(pass.Body, pass.Eye, pass.Exclude, target.Bounds)) pass.Sight.Add(target.Id);
                return SampleOffsets.Length;
            }
            case SweepStage.Recall:
            {
                if (pass.Remembered == null)
                {
                    if (PerceptionMemoryLimit <= 0)
                    {
                        // Memory off: Remember forgets everything and remembers nothing.
                        run.Memory.Clear();
                        pass.Stage = SweepStage.Candidates;
                        return 0;
                    }
                    pass.Remembered = run.Memory.Order.ToList();
                    pass.Next = 0;
                }
                if (pass.Next >= pass.Remembered.Count)
                {
                    pass.Stage = SweepStage.Fresh;
                    return 0;
                }
                var id = pass.Remembered[pass.Next++];
                // A sweep looks at some things each time: what it did not look at is neither changed nor gone as far as it knows.
                if (pass.Sight.Contains(id) || !run.Considered.Contains(id)) return 0;
                CheckRemembered(run.Memory, id, run.ById, run.Owners, pass.Body, pass.Eye, pass.Exclude, run.UnplacedTexts);
                return SampleOffsets.Length;
            }
            case SweepStage.Fresh:
                // The sweep's summaries are its own, never handed out: the map keeps them without copying.
                AddFresh(run.Memory, run.Entities, pass.Sight, pass.Avatar, pass.Eye, run.Owners, run.Revision, copy: false);
                EvictPastTheBound(run.Memory, pass.Sight);
                pass.Stage = SweepStage.Candidates;
                return 1;
            case SweepStage.Candidates:
                SelectDiscoverCandidates(pass.Eye, pass.Candidates);
                pass.Next = 0;
                pass.Stage = SweepStage.Discover;
                return 1;
            case SweepStage.Discover:
                if (pass.Next >= pass.Candidates.Count || pass.Next >= DiscoverRaysPerSweep)
                {
                    pass.Candidates.Clear();
                    pass.Stage = SweepStage.Done;
                    return 0;
                }
                DiscoverCell(pass.Body, pass.Eye, pass.Exclude, pass.Candidates[pass.Next++]);
                return 1;
            default:
                run.Pass++;
                return 0;
        }
    }
}
