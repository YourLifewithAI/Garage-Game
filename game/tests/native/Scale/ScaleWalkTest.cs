using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Navigation;
using GarageGame.Native.Room;

namespace GarageGame.Tests.Scale;

/// <summary>
/// A long walk across the 10x island (the scale suite, S10): from the player's spawn to the farthest land a route reaches, the
/// player runs as a player steers, facing the route's next corner and holding forward, the route asked again every half second
/// from where the body is. Measured: the route's length and the straight line, the distance run, the simulated and the real time,
/// the speed, the ticks (median, 95th percentile, worst, how many held a garbage collection), the longest the body stood stuck,
/// recoveries, swimming and climbing, and the route searches' cost on the main thread.
/// </summary>
public sealed class ScaleWalkTest
{
    /// <summary>A goal this close (planar) counts as reached.</summary>
    public const float ArriveM = 0.3f;
    private readonly ScaleSuiteTest _test;
    private readonly RoomWorld _world;
    private readonly RoomNavigation _navigation;

    public ScaleWalkTest(ScaleSuiteTest test, RoomWorld world, RoomNavigation navigation) => (_test, _world, _navigation) = (test, world, navigation);

    public sealed class Result
    {
        public RoomNavigation.Route Route = RoomNavigation.Route.None;
        public Vector3 From, Goal;
        public bool Arrived;
        public float EndGapM, RunM;
        public int Ticks, CollectionTicks, Recoveries, SwimTicks, ClimbTicks, Replans, Candidates;
        public double RealS, MedianTickMs, P95TickMs, WorstTickMs, LongestStuckS, ChooseMs, WorstReplanMs;

        public string Line() => ScaleSuiteTest.Invariant(
            $"arrived={(Arrived ? "yes" : "no")} route_m={Route.LengthM:0.0} straight_m={ScaleSuiteTest.PlanarDistance(From, Goal):0.0} run_m={RunM:0.0} end_gap_m={EndGapM:0.00} ") +
            ScaleSuiteTest.Invariant($"sim_s={Ticks / 60.0:0.0} real_s={RealS:0.0} speed_mps={RunM / Math.Max(1.0 / 60, Ticks / 60.0):0.00} ") +
            ScaleSuiteTest.Invariant($"tick_ms_median={MedianTickMs:0.0} tick_ms_p95={P95TickMs:0.0} tick_ms_worst={WorstTickMs:0.0} gc_ticks={CollectionTicks} ") +
            ScaleSuiteTest.Invariant($"stuck_s_longest={LongestStuckS:0.0} recoveries={Recoveries} swim_s={SwimTicks / 60.0:0.0} climb_s={ClimbTicks / 60.0:0.0} ") +
            ScaleSuiteTest.Invariant($"replans={Replans} replan_ms_worst={WorstReplanMs:0.0} goal_search_ms={ChooseMs:0} goal_candidates={Candidates} from={Text(From)} goal={Text(Goal)}");
    }

    public async Task<Result> Run()
    {
        var player = _world.Player;
        var result = new Result { From = player.GlobalPosition };
        var chose = Stopwatch.StartNew();
        (result.Goal, result.Route, result.Candidates) = FarthestReachable(result.From);
        result.ChooseMs = chose.Elapsed.TotalMilliseconds;
        if (!result.Route.Reaches) return result;

        // Run as a player steers (LandscapePlayTest.PlayerWalk): face the next corner, forward, sprint; ask the route again often.
        var limit = (int)Math.Min(30 * 60 * 60, result.Route.LengthM / player.RunSpeedMps * 60 * 3 + 30 * 60);
        var route = result.Route;
        var index = 1;
        var age = 0;
        var ticks = new List<double>(limit);
        var watch = new Stopwatch();
        var real = Stopwatch.StartNew();
        var recoveries = player.Recoveries;
        var last = player.GlobalPosition;
        int stuck = 0, longestStuck = 0;
        for (var i = 0; i < limit; i++)
        {
            var here = player.GlobalPosition;
            var remaining = ScaleSuiteTest.PlanarDistance(result.Goal, here);
            if (remaining <= ArriveM) { result.Arrived = true; break; }
            if (++age >= 30 || route.IsEmpty)
            {
                var asked = Stopwatch.GetTimestamp();
                var again = _navigation.FindRoute(here, result.Goal, ArriveM * 0.5f);
                result.WorstReplanMs = Math.Max(result.WorstReplanMs, Stopwatch.GetElapsedTime(asked).TotalMilliseconds);
                result.Replans++;
                if (!again.IsEmpty) (route, index) = (again, 1);
                age = 0;
            }
            while (index < route.Points.Length - 1 && ScaleSuiteTest.PlanarDistance(route.Points[index], here) < 0.05f) index++;
            var corner = route.IsEmpty || index >= route.Points.Length ? result.Goal : route.Points[index];
            var to = corner - here;
            if (new Vector2(to.X, to.Z).Length() > 0.001f) player.Rotation = new Vector3(0, Mathf.Atan2(-to.X, -to.Z), 0);
            player.SetControlInput(new Vector2(0, 1), sprint: remaining > 0.5f);
            var collections = GC.CollectionCount(0);
            watch.Restart();
            await _test.Frames(1);
            ticks.Add(watch.Elapsed.TotalMilliseconds);
            if (GC.CollectionCount(0) != collections) result.CollectionTicks++;
            var now = player.GlobalPosition;
            result.RunM += ScaleSuiteTest.PlanarDistance(now, last);
            stuck = ScaleSuiteTest.PlanarDistance(now, last) < 0.002f ? stuck + 1 : 0;
            longestStuck = Math.Max(longestStuck, stuck);
            last = now;
            if (player.IsSwimming) result.SwimTicks++;
            if (player.IsClimbing) result.ClimbTicks++;
        }
        player.SetControlInput(Vector2.Zero);
        result.RealS = real.Elapsed.TotalSeconds;
        result.Ticks = ticks.Count;
        result.EndGapM = ScaleSuiteTest.PlanarDistance(result.Goal, player.GlobalPosition);
        result.Recoveries = player.Recoveries - recoveries;
        result.LongestStuckS = longestStuck / 60.0;
        if (ticks.Count > 0)
        {
            ticks.Sort();
            result.MedianTickMs = ticks[ticks.Count / 2];
            result.P95TickMs = ticks[Math.Min(ticks.Count - 1, (int)(ticks.Count * 0.95))];
            result.WorstTickMs = ticks[^1];
        }
        await _test.Frames(10);
        return result;
    }

    /// <summary>
    /// The land on a grid of about 50 by 50 over the island, kept where the walkable mesh is, farthest from the start first: the
    /// first a route from the start reaches is the goal (at most 80 tried).
    /// </summary>
    private (Vector3 Goal, RoomNavigation.Route Route, int Tried) FarthestReachable(Vector3 from)
    {
        var bounds = _world.Room.Sea?.LandBounds(_world.Room.Bounds) ?? _world.Room.Bounds;
        var step = Mathf.Max(0.1f, Mathf.Max(bounds.Size.X, bounds.Size.Z) / 50f);
        var space = _test.GetWorld3D().DirectSpaceState;
        var land = new List<Vector3>();
        for (var x = bounds.Position.X + step * 0.5f; x < bounds.End.X; x += step)
            for (var z = bounds.Position.Z + step * 0.5f; z < bounds.End.Z; z += step)
            {
                var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(new Vector3(x, bounds.End.Y + 1, z), new Vector3(x, bounds.Position.Y - 1, z), RoomBuilder.WorldLayer));
                if (hit.Count == 0) continue;
                var point = hit["position"].AsVector3();
                var snapped = _navigation.ClosestPoint(point);
                if (ScaleSuiteTest.PlanarDistance(snapped, point) <= _navigation.AgentRadiusM + 0.01f && Mathf.Abs(snapped.Y - point.Y) <= 0.03f) land.Add(snapped);
            }
        var start = _navigation.ClosestPoint(from);
        var tried = 0;
        foreach (var goal in land.OrderByDescending(p => ScaleSuiteTest.PlanarDistance(p, from)).Take(80))
        {
            tried++;
            var route = _navigation.FindRoute(start, goal, ArriveM * 0.5f);
            if (route.Reaches) return (goal, route, tried);
        }
        return (from, RoomNavigation.Route.None, tried);
    }

    private static string Text(Vector3 v) => ScaleSuiteTest.Invariant($"({v.X:0.00},{v.Y:0.00},{v.Z:0.00})");
}
