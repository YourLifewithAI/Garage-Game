using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace GarageGame.Tests.Look;

/// <summary>
/// The probe's "--parts" (S12): what the per-frame and periodic jobs of the HUD, the command host and the bodies cost one at a
/// time, called directly on the booted island: the bytes each call allocates, the Godot wrappers it leaves for the .NET finalizer
/// (FrameGarbage), and its longest time. Then the frame as a whole with no collection allowed (GC.TryStartNoGCRegion), which gives
/// the bytes and the finalizer's wrappers a frame exactly.
/// </summary>
public partial class IslandFrameProbe
{
    private async Task MeasureParts()
    {
        var hud = _world.GetNodeOrNull<GarageGame.Native.RoomHud>("RoomHud")!;
        var host = GarageGame.Native.Kernel.CommandHost.Of(_world)!;
        var ids = host.Room.Shell.Select(p => p.Id).Concat(host.Room.Objects.Select(o => o.Id)).ToList();
        var parts = new List<(string Name, Action Call)>
        {
            ("RoomHud._Process", () => hud._Process(1 / 60.0)),
            ("RoomHud.RefreshKeyText", Private(hud, "RefreshKeyText")),
            ("RoomHud.UpdateFocus (a query)", Private(hud, "UpdateFocus", 1.0)),
            ("RoomHud.UpdateAskTag (a query)", Private(hud, "UpdateAskTag", 1.0)),
            ("RoomHud.UpdateChains", Private(hud, "UpdateChains")),
            ("RoomHud.UpdateDusk", Private(hud, "UpdateDusk", 1 / 60.0)),
            ("RoomHud.TickMagicPanel", Private(hud, "TickMagicPanel", 1 / 60.0)),
            ("RoomHud.TickGubbleView", Private(hud, "TickGubbleView")),
            ("RoomHud.ClockText", () => hud.ClockText()),
            ("CommandHost._Process", () => host._Process(1 / 60.0)),
            ("CommandHost.Entities", () => host.Entities()),
            ("the authority's snapshot (1 call)", () => { using var snapshot = host.Authority.Call(SnapshotName, GarageGame.Native.Kernel.CommandHost.PlayerPrincipal); }),
            ("CommandHost.EntityRevision (every room entity)", () => { foreach (var id in ids) host.EntityRevision(id); }),
            ("CommandHost.PlayerFocus", () => host.PlayerFocus()),
            ("CommandHost.SightSweep (a whole sweep, in one call)", WholeSweep(host)),
            ("CommandHost.AdvanceNature (a step)", () => host.AdvanceNature(GarageGame.Native.Kernel.NatureLaws.StepS)),
            ("CommandHost.AdvanceNature (a tick)", () => host.AdvanceNature(1 / 60.0)),
            ("the pond survey (PondSurvey.Survey, once a boot)", () => GarageGame.Native.Look.Fauna.PondSurvey.Survey(_world.Built, GarageGame.Native.Look.Fauna.PondSurvey.RayProbe(_world.GetWorld3D().DirectSpaceState))),
            ("Player._PhysicsProcess", () => _world.Player._PhysicsProcess(1 / 60.0)),
            ("Player.SampleWater", Private(_world.Player, "SampleWater", 1 / 60f)),
            ("Companion._PhysicsProcess", () => _world.Companion._PhysicsProcess(1 / 60.0)),
            ("Companion._Process", () => _world.Companion._Process(1 / 60.0)),
        };
        // The look's own per-frame jobs (the first recorded play: what a frame of the sea, its fish and the director costs).
        if (_world.Look is { } look)
        {
            parts.Add(("LookDirector._Process", () => look._Process(1 / 60.0)));
            if (look.OpenSea is { } sea)
            {
                parts.Add(("OpenSea._Process (the sea's follow, the mist, the shore's clock)", () => sea._Process(1 / 60.0)));
                if (sea.Life is { } fish) parts.Add(("SeaLife.Advance (the sea's fish)", () => fish.Advance(1 / 60f)));
            }
            if (_world.GetChildren().OfType<GarageGame.Native.Look.Fauna.PondLife>().FirstOrDefault() is { } ponds)
                parts.Add(("PondLife.Advance (the ponds' fish)", () => ponds.Advance(1 / 60f)));
        }
        foreach (var (name, call) in parts)
        {
            call();
            var casts = SightRays();
            var cost = FrameGarbage.Measure(call, 12);
            casts = SightRays() - casts;
            if (casts > 0) GD.Print($"ISLAND_PROBE part {name}: {casts / 12.0:0} sight rays a call");
            GD.Print(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"ISLAND_PROBE part {name}: {cost.Bytes / 1024.0:0.0} KB, {cost.Wrappers:0.#} Godot wrappers left for the finalizer, longest {cost.LongestMs:0.00} ms a call"));
            await Frame();
        }
        // The whole frame, process and physics, with no collection allowed: its bytes and its wrappers exactly.
        const int frames = 120;
        var window = await FrameGarbage.MeasureFrames(frames, Frame);
        GD.Print(window is { } w
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"ISLAND_PROBE a frame, all threads, no collection in {frames} frames: {w.Bytes / 1024.0 / frames:0.0} KB allocated, {w.Wrappers / (double)frames:0.0} Godot wrappers left for the finalizer")
            : "ISLAND_PROBE a frame: the no-collection window could not be held (a collection came anyway)");
        // Then without each of the world's nodes in turn: what each one's frames allocate and leave for the finalizer.
        var nodes = _world.GetChildren().Concat(_world.Look.GetChildren()).OfType<Node>().Where(n => n != _world.Built && n.Name != "Environment").ToList();
        foreach (var node in nodes)
        {
            var mode = node.ProcessMode;
            node.ProcessMode = ProcessModeEnum.Disabled;
            for (var i = 0; i < 5; i++) await Frame();
            var without = await FrameGarbage.MeasureFrames(frames / 2, Frame);
            node.ProcessMode = mode;
            for (var i = 0; i < 5; i++) await Frame();
            if (without is { } o)
                GD.Print(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"ISLAND_PROBE a frame without {node.Name}: {o.Bytes / 1024.0 / (frames / 2):0.0} KB allocated, {o.Wrappers / (double)(frames / 2):0.0} Godot wrappers left"));
        }
    }

    private static readonly StringName SnapshotName = "snapshot";

    /// <summary>The host's sight rays cast so far (SightRay.Casts, S12), by reflection; 0 before S12.</summary>
    private static long SightRays() =>
        typeof(GarageGame.Native.Kernel.CommandHost).Assembly.GetType("GarageGame.Native.Kernel.SightRay")?.GetProperty("Casts", BindingFlags.Static | BindingFlags.Public)?.GetValue(null) is long casts ? casts : 0;

    /// <summary>A sweep begun and finished at once: all its work, as the sweep did in one tick before S12 spread it.</summary>
    private static Action WholeSweep(GarageGame.Native.Kernel.CommandHost host)
    {
        var sweep = Private(host, "SightSweep", host.TeamSightIntervalS);
        // By reflection, so the probe measures commits from before S12 too (a sweep was then done in its tick).
        var finish = typeof(GarageGame.Native.Kernel.CommandHost).GetMethod("FinishSweep", BindingFlags.Instance | BindingFlags.NonPublic);
        return () =>
        {
            sweep();
            finish?.Invoke(host, null);
        };
    }

    private static Action Private(object target, string method, params object[] arguments)
    {
        var info = target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                   ?? throw new MissingMethodException(target.GetType().Name, method);
        return () => info.Invoke(target, arguments);
    }
}
