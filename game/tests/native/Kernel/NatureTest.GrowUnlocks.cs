using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Room;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// Grow's three unlocks (docs/game/MECHANICS.md decisions 76 and 104 to 107, the packet P-Grow), in the test room with plants and
/// fixtures of its own (a cliff for ivy, two ledges with a gap for the bridge): the gate (each kind refused permission_denied before its
/// count, in the kernel's words, whoever asks, and listed locked), seedless growth (sprouts deterministic, byte-exact on reload, growing
/// in phases with the island clock, undone, capped, burning), replanting (a burnt tree back at half size, climbed again, growing in
/// phases), the bridge (a small avatar crosses a 1.2 m deck and falls when it burns; its collision one-sided over the void alone) and
/// wall ivy (a climb on it drains a quarter of the bar, full again once it burns; one strip a ledge), every kind priced as any Grow,
/// and no Gubble cast moving the player.
/// </summary>
public partial class NatureTest
{
    /// <summary>Bare soil clear of the tufts, the tree and the room's objects (the doorstop, the table), within the Gubble's reach from its spawn.</summary>
    private static readonly Vector3 BareAt = new(-0.9f, 0.01f, 0.0f);

    private JsonObject GrowCast(string principal, Vector3 at, double radius, Vector3? end = null, bool preview = false)
    {
        var args = Cast("grow", at, radius, 1);
        if (end is { } to) args["end_m"] = new JsonArray(to.X, to.Y, to.Z);
        return Send(Command(NextId("unlock"), "nature.cast", args, preview: preview), principal);
    }

    private static string? Kind(JsonObject result) => result["data"]?["kind"]?.GetValue<string>();
    private static int Grown(JsonObject result) => result["data"]?["grown_plants"]?.GetValue<int>() ?? -1;
    private SproutView? SproutsShown => _world!.GetNode("Room").GetNodeOrNull<SproutView>(SproutView.ViewName);

    /// <summary>A solid box on the world layer (a cliff, a ledge), its bottom on the floor: what the body climbs and stands on.</summary>
    private StaticBody3D Block(string name, Vector3 low, Vector3 high)
    {
        var body = new StaticBody3D { Name = name, CollisionLayer = RoomBuilder.WorldLayer, CollisionMask = 0, Position = (low + high) * 0.5f };
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = high - low } });
        _world!.AddChild(body);
        return body;
    }

    /// <summary>Where the player and the Gubble stand now, to prove a cast moved neither.</summary>
    private (Vector3 Player, Vector3 Gubble) Bodies() => (_player.GlobalPosition, _companion.GlobalPosition);

    private async Task TestGrowUnlocks()
    {
        await TestGrowGate();
        await TestSprouts();
        await TestSproutsFill();
        await TestReplant();
        await TestBridge();
        await TestIvy();
    }

    /// <summary>Item 1: the gate. Each kind refused permission_denied before its count, before the price and whoever asks; locked in the list.</summary>
    private async Task TestGrowGate()
    {
        await NewWorld(SavePath("unlocks-gate"));
        Block("Cliff", new Vector3(-1.9f, 0, 0.1f), new Vector3(-1.3f, 0.4f, 1.0f));
        await Frames(2);
        var revision = _host.Revision;
        var pips = _host.Vitals.Pips;
        var bare = GrowCast(Player, BareAt, 0.3);
        var gubble = GrowCast(Companion, BareAt, 0.3);
        Check(Code(bare) == "permission_denied" && Message(bare) == CommandHost.SproutsLocked && Code(gubble) == "permission_denied" && Message(gubble) == CommandHost.SproutsLocked,
            "seedless growth before two Grow expertise: Grow on bare fertile soil is refused permission_denied in the kernel's words, whoever asks: " + Message(bare));
        var drag = GrowCast(Player, BareAt, 0.3, end: BareAt + new Vector3(5, 0, 0));
        Check(Code(drag) == "permission_denied" && Message(drag) == CommandHost.BridgeLocked && drag["error"]?["field_path"]?.GetValue<string>() == "$.args.end_m",
            "a drag before three is refused permission_denied for the bridge, ahead of every other check (its end lies outside the island): " + Canonical(drag["error"]));
        var wall = GrowCast(Player, new Vector3(-1.3f, 0.2f, 0.3f), 0.1);
        Check(Code(wall) == "permission_denied" && Message(wall) == CommandHost.IvyLocked, "Grow at a wall before five is refused permission_denied for ivy: " + Message(wall));
        _host.Nature.Plants[Tuft(3)] = new NaturePlant { Id = Tuft(3), Burn = 1 };
        var burnt = GrowCast(Player, new Vector3(TuftsAt[3].X, 0.01f, TuftsAt[3].Y), 0.04);
        _host.Nature.Plants.Remove(Tuft(3));
        Check(Code(burnt) == "permission_denied" && Message(burnt) == CommandHost.ReplantLocked, "Grow on a burnt plant before two is refused permission_denied for replanting: " + Message(burnt));
        Check(_host.Revision == revision && _host.Vitals.Pips == pips && _host.Nature.Sprouts.Count == 0 && _host.Nature.Bridges.Count == 0 && _host.Nature.Ivy.Count == 0,
            "every refusal comes before the price: no revision, no pip, nothing grown");
        var plain = GrowAt(NearGubble, 0.15);
        Check(Ok(plain) && Kind(plain) == null && Grown(plain) == 3, "plain Grow still grows the island's own plants before any unlock, with no kind: " + Canonical(plain["error"] ?? plain["data"]));
        var all = new[] { GrowKindWords.Sprouts, GrowKindWords.Replant, GrowKindWords.Bridge, GrowKindWords.Ivy };
        var lists = new List<IReadOnlyList<string>>();
        foreach (var count in new[] { 0, 1, 2, 3, 4, 5 })
        {
            _host.LearnGrowForTests(count);
            lists.Add(_host.GrowLockedKinds().ToList());
        }
        Check(lists[0].SequenceEqual(all) && lists[1].SequenceEqual(all) && lists[2].SequenceEqual(new[] { GrowKindWords.Bridge, GrowKindWords.Ivy }) &&
              lists[3].SequenceEqual(new[] { GrowKindWords.Ivy }) && lists[4].SequenceEqual(new[] { GrowKindWords.Ivy }) && lists[5].Count == 0 && _host.GrowExpertiseCount == 5,
            "the locked kinds by the team's Grow expertise count: all four at 0 and 1, the bridge and ivy at 2, ivy at 3 and 4, none at 5: " +
            string.Join(" | ", lists.Select(l => string.Join(",", l))));
        Check(_host.GrowLockedWords(GrowKindWords.Ivy) == null && Code(GrowCast(Player, new Vector3(-1.3f, 0.2f, 0.3f), 0.1)) != "permission_denied",
            "at five every kind is open, the lock words gone");
        await FreeWorld();
    }

    /// <summary>Item 2: seedless growth (decision 104).</summary>
    private async Task TestSprouts()
    {
        var sownAt = _now;
        await NewWorld(SavePath("unlocks-sprouts"));
        _host.LearnGrowForTests(2);
        var revision = _host.Revision;
        var preview = GrowCast(Player, BareAt, 0.5, preview: true);
        Check(Ok(preview) && Kind(preview) == GrowKindWords.Sprouts && Grown(preview) == GrowKindLaws.SproutsPerCast && _host.Nature.Sprouts.Count == 0 && _host.Revision == revision,
            "a preview on bare fertile soil says sprouts, and how many, changing nothing: " + Canonical(preview["error"] ?? preview["data"]));
        _now = sownAt;
        var pips = _host.Vitals.Pips;
        var cast = GrowCast(Player, BareAt, 0.5);
        Check(pips - _host.Vitals.Pips == 4, $"sprouts are priced as any Grow: the player's order 4 pips ({pips} to {_host.Vitals.Pips})");
        var sprouts = _host.Nature.Sprouts.ToList();
        var apart = sprouts.SelectMany((a, i) => sprouts.Skip(i + 1).Select(b => CampLaws.Flat(a.PositionM, b.PositionM))).DefaultIfEmpty(9).Min();
        Check(Ok(cast) && Kind(cast) == GrowKindWords.Sprouts && Grown(cast) == 12 && sprouts.Count == 12 && apart >= GrowKindLaws.SproutApartM &&
              sprouts.All(s => CampLaws.Flat(s.PositionM, BareAt) <= 0.5f && s.Factor == GrowKindLaws.SeedlingStart && s.Template.StartsWith("plant:grass-", StringComparison.Ordinal)),
            $"Grow on bare fertile soil makes 12 sprouts in its circle, at least {apart:0.000} m apart, each copying the nearest grass at half size: " + Canonical(cast["error"] ?? cast["data"]));
        Check(SproutsShown?.Count == 12 && SproutsShown!.Drawn.All(d => Math.Abs(d.Place.Basis.Scale.Y - 0.5f * _room.Copies!.Sets.First(s => s.Name == "grass").Scale(0).Y) < 0.6f),
            "the sprouts are drawn at once, at half their copy's size");
        var first = Canonical(NatureBlock.Pack(_host.Nature)["sprouts"]);
        // Deterministic: the same cast in the same place, in a second island, makes the same sprouts.
        await NewWorld(SavePath("unlocks-sprouts-twin"));
        _host.LearnGrowForTests(2);
        _now = sownAt;
        GrowCast(Player, BareAt, 0.5);
        Check(Canonical(NatureBlock.Pack(_host.Nature)["sprouts"]) == first, "the same cast in the same place makes the same sprouts: their spots, their copies, their sowing");
        // Byte-exact on reload, and the phases advance with the island clock: a reload continues them.
        await NewWorld(SavePath("unlocks-sprouts"));
        var saved = Canonical(NatureBlock.Pack(_host.Nature));
        Check(_host.Nature.Sprouts.Count == 12 && saved.Contains(first, StringComparison.Ordinal) && SproutsShown?.Count == 12, "the sprouts are saved with the island and come back on load, drawn");
        // A save is untrusted input: the strict check refuses a forged sprout or seedling.
        var block = NatureBlock.Pack(_host.Nature);
        var forgeries = new (string Label, Action<JsonObject> Edit)[]
        {
            ("a sprout copying a tree", b => b["sprouts"]![0]!["copy"] = Tree),
            ("a sprout of a set the room lacks", b => b["sprouts"]![0]!["copy"] = "plant:fern-0"),
            ("a sprout with a saved size", b => b["sprouts"]![0]!["factor"] = 0.5),
            ("a sprout outside the island", b => b["sprouts"]![0]!["position_m"] = new JsonArray(9, 0, 0)),
            ("a sprout burnt down", b => b["sprouts"]![0]!["burn"] = 1),
            ("a sowing that is no whole second", b => b["sprouts"]![0]!["sown_s"] = 1.5),
            ("an empty list of sprouts", b => b["sprouts"] = new JsonArray()),
            ("a seedling with a saved size", b => b["plants"] = new JsonObject { [Tuft(0)] = new JsonObject { ["sown_s"] = 5, ["factor"] = 1.25 } }),
            ("a seedling burnt down", b => b["plants"] = new JsonObject { [Tuft(0)] = new JsonObject { ["sown_s"] = 5, ["burn"] = 1 } }),
        };
        Check(NatureBlock.Problem(block, _host.Revision, _room) == null, "the block with sprouts passes the strict check");
        foreach (var (label, edit) in forgeries)
            Check(NatureBlock.Problem(Edited(block, edit), _host.Revision, _room) != null, "the strict check refuses a block with " + label);
        var phases = new List<float> { _host.Nature.Sprouts[0].Factor };
        for (var hour = 1; hour <= 4; hour++)
        {
            _now = sownAt + TimeSpan.FromHours(hour) + TimeSpan.FromSeconds(1);
            Steps(1);
            phases.Add(_host.Nature.Sprouts[0].Factor);
            if (hour == 1)
            {
                await NewWorld(SavePath("unlocks-sprouts"));
                Check(Canonical(NatureBlock.Pack(_host.Nature)["sprouts"]) == first && _host.Nature.Sprouts.All(s => Math.Abs(s.Factor - 2f / 3) < 1e-4f),
                    "an hour on, a reload finds the sprouts' same bytes and the sprouts in their second phase: the clock decides them, the save never does");
            }
        }
        Check(phases.Zip(new[] { 0.5f, 2f / 3, 5f / 6, 1f, 1f }).All(p => Math.Abs(p.First - p.Second) < 1e-4f) && SproutsShown!.Drawn.All(d => d.Place.Basis.Scale.Y > 0.6f),
            "a sprout grows in three phases, one an in-game hour, from half its size to its natural size, and stays there: " + string.Join(", ", phases.Select(f => f.ToString("0.000"))));
        await FreeWorld();
    }
}

public partial class NatureTest
{
    /// <summary>Item 2, continued: later casts fill the spots left, undo, the island's most, sprouts burning, and the Gubble's sprouts moving no one.</summary>
    private async Task TestSproutsFill()
    {
        await NewWorld(SavePath("unlocks-sprouts-fill"));
        _host.LearnGrowForTests(2);
        // The Gubble's sprouts round the player (its own idea, from a full pool): nothing collides, nothing moves.
        Check(_player.TryTeleportTo(new Vector3(-1.4f, 0.003f, 0.6f)), "(the player stands on bare soil)");
        await Frames(10);
        var (player, _) = Bodies();
        var pips = _host.Vitals.Pips;
        var gubble = GrowCast(Companion, new Vector3(-1.4f, 0.01f, 0.6f), 0.3);
        var paid = pips - _host.Vitals.Pips;
        await Frames(10);
        Check(Ok(gubble) && Kind(gubble) == GrowKindWords.Sprouts && paid == 2 && _player.GlobalPosition.DistanceTo(player) < 0.001f,
            $"the Gubble's sprouts round the player cost its own idea's 2 pips ({pips} to {pips - paid}) and leave the player where they were (moved {_player.GlobalPosition.DistanceTo(player) * 1000:0.0} mm): " +
            Canonical(gubble["error"] ?? gubble["data"]));
        Check(Ok(_host.PlayerNatureUndo()), "(the Gubble's sprouts are taken back)");
        var casts = new List<int>();
        JsonObject last = new();
        for (var i = 0; i < 6; i++)
        {
            last = GrowCast(Player, BareAt, 0.5);
            if (!Ok(last)) break;
            casts.Add(Grown(last));
        }
        var all = _host.Nature.Sprouts.ToList();
        var apart = all.SelectMany((a, i) => all.Skip(i + 1).Select(b => CampLaws.Flat(a.PositionM, b.PositionM))).DefaultIfEmpty(9).Min();
        Check(casts.Count >= 2 && casts[0] == 12 && casts.Sum() == all.Count && Code(last) == "invalid_args" && Message(last) == CommandHost.SproutsGroundFull && apart >= GrowKindLaws.SproutApartM,
            $"each later cast fills only the spots left ({string.Join(" + ", casts)} sprouts, at least {apart:0.000} m apart), then the circle is full: " + Message(last));
        var undo = _host.PlayerNatureUndo();
        Check(Ok(undo) && _host.Nature.Sprouts.Count == all.Count - casts[^1] && SproutsShown?.Count == _host.Nature.Sprouts.Count, "Creative's undo takes the last cast's sprouts back, drawing and all");
        var fill = _host.Nature.Sprouts.Count;
        while (_host.Nature.Sprouts.Count < NatureState.MaxSprouts)
            _host.Nature.Sprouts.Add(new NatureSprout { Template = all[0].Template, PositionM = new Vector3(1.9f, 0, 1.4f), SownS = 0 });
        var capped = GrowCast(Player, new Vector3(-1.4f, 0.01f, 0.6f), 0.3);
        _host.Nature.Sprouts.RemoveRange(fill, NatureState.MaxSprouts - fill);
        Check(Code(capped) == "invalid_args" && (Message(capped) ?? "").Contains($"{NatureState.MaxSprouts} sprouts", StringComparison.Ordinal),
            $"the island keeps at most {NatureState.MaxSprouts} sprouts: " + Message(capped));
        // Sprouts burn as plants: gone, leaving ash, in the fire's undo step.
        var before = _host.Nature.Sprouts.Count(s => CampLaws.Flat(s.PositionM, BareAt) <= 0.5f);
        var lit = Fire(Player, BareAt, 0.6, 30, out _);
        for (var step = 0; step < 120 && _host.Nature.Fire != null; step++) Steps(1);
        var after = _host.Nature.Sprouts.Count(s => CampLaws.Flat(s.PositionM, BareAt) <= 0.5f);
        var ash = _host.Nature.Cells.Values.Count(c => c.Ash > 0 && CampLaws.Flat(c.Position, BareAt) <= 0.5f);
        Check(Ok(lit) && after < before && ash > 0 && _host.Nature.Sprouts.All(s => s.Burn < 1) && SproutsShown?.Count == _host.Nature.Sprouts.Count,
            $"a fire among the sprouts burns them as plants: {before - after} of {before} gone, ash left ({ash} cells)");
        var back = _host.PlayerNatureUndo();
        Check(Ok(back) && _host.Nature.Sprouts.Count(s => CampLaws.Flat(s.PositionM, BareAt) <= 0.5f) == before, "the fire's one undo step in Creative brings the burnt sprouts back");
        await FreeWorld();
    }

    /// <summary>Item 3: replanting (decision 105). A burnt tree comes back at half size, climbed again, growing through its phases.</summary>
    private async Task TestReplant()
    {
        var sownAt = _now;
        await NewWorld(SavePath("unlocks-replant"));
        await Frames(2);
        _host.LearnGrowForTests(2);
        _companion.GlobalPosition = TreeAt + new Vector3(-0.6f, 0.01f, 0.3f);
        Fire(Player, TreeAt + new Vector3(-0.1f, 0.01f, 0), 0.3, 30, out _);
        for (var step = 0; step < 80 && _host.Nature.Fire != null; step++) Steps(1);
        await Frames(2);
        Check(_host.Nature.Plants.GetValueOrDefault(Tree) is { Burnt: true } && float.IsNaN(HitDown(TreeAt.X, TreeAt.Z, RoomBuilder.HiddenLayer)), "(the tree burns down first)");
        // Nobody stands where it would grow back: the trunk would push them.
        Check(_player.TryTeleportTo(TreeAt + new Vector3(0.02f, 0.003f, 0)), "(the player stands where the trunk stood)");
        await Frames(5);
        var standing = GrowCast(Player, TreeAt + new Vector3(-0.2f, 0.01f, 0), 0.5);
        Check(Code(standing) == "target_busy" && Message(standing) == CommandHost.ReplantStandingWords && _host.Nature.Plants[Tree].Burnt,
            "a tree is not replanted while someone stands where its trunk would grow: " + Message(standing));
        Check(_player.TryTeleportTo(TreeAt + new Vector3(-0.16f, 0.003f, 0)), "(the player steps back from the stump)");
        await Frames(10);
        var (player, _) = Bodies();
        var pips = _host.Vitals.Pips;
        _now = sownAt;
        var replant = GrowCast(Companion, TreeAt + new Vector3(-0.2f, 0.01f, 0), 0.5);
        await Frames(10);
        var record = _host.Nature.Plants.GetValueOrDefault(Tree);
        var perch = HitDown(TreeAt.X, TreeAt.Z, RoomBuilder.HiddenLayer);
        Check(Ok(replant) && Kind(replant) == GrowKindWords.Replant && Grown(replant) == 1 && record is { Burn: 0, SownS: not null } && Math.Abs(record.Factor - 0.5f) < 1e-4f &&
              Copies.FactorOf(Tree) == 0.5f && Math.Abs(perch - 0.47f) < 0.01f && TrunkAt(0.15f) && !TrunkAt(0.3f),
            $"Grow on burnt ground returns the burnt tree as a seedling at half size, its trunk and perch with it (perch at {perch:0.000} m): " + Canonical(replant["error"] ?? replant["data"]));
        Check(pips - _host.Vitals.Pips == 2 && _player.GlobalPosition.DistanceTo(player) < 0.001f && !_host.Nature.Cells.Values.Any(c => c.Ash > 0 && CampLaws.Flat(c.Position, TreeAt) <= 0.5f),
            $"the Gubble's replanting costs its 2 pips, greens the ash there and leaves the player where they were (moved {_player.GlobalPosition.DistanceTo(player) * 1000:0.0} mm)");
        // It climbs again: the real body walks into the young trunk, grabs it and climbs.
        _player.Rotation = new Vector3(0, Mathf.Atan2(-1, 0), 0);
        var grabs = _player.Grabs;
        var highest = _player.GlobalPosition.Y;
        _player.SetControlInput(new Vector2(0, 1));
        for (var frame = 0; frame < 900 && _player.PullOvers == 0; frame++)
        {
            await Frames(1);
            highest = Math.Max(highest, _player.GlobalPosition.Y);
        }
        _player.SetControlInput(Vector2.Zero);
        GD.Print(System.FormattableString.Invariant($"GROW_REPLANT the half-size tree: grabbed {_player.Grabs - grabs}, climbed to {highest:0.000} m (perch {perch:0.000} m), pulled over {_player.PullOvers}"));
        Check(_player.Grabs > grabs && highest > 0.2f, $"the replanted tree is climbed again: grabbed, and up to {highest:0.000} m (its perch at {perch:0.000} m)");
        Check(_player.TryTeleportTo(_room.SpawnFor("player").PositionM), "(the player steps back to the spawn)");
        // Saved as a sowing, never a size; the phases on the island clock bring it to its natural size, and a reload continues them.
        var saved = NatureBlock.Pack(_host.Nature)["plants"]?[Tree];
        Check(saved?["sown_s"] != null && saved["factor"] == null && saved["burn"] == null, "the seedling is saved as when it was sown, with no size: " + Canonical(saved));
        _now = sownAt + TimeSpan.FromHours(2) + TimeSpan.FromSeconds(1);
        await NewWorld(SavePath("unlocks-replant"));
        await Frames(2);
        Check(Math.Abs(Factor(Tree) - 5f / 6) < 1e-4f && Math.Abs(Copies.FactorOf(Tree) - 5f / 6) < 1e-4f, $"two hours on, a reload finds the tree in its third phase ({Factor(Tree):0.000})");
        _now = sownAt + TimeSpan.FromHours(3) + TimeSpan.FromSeconds(1);
        Steps(1);
        await Frames(2);
        Check(Factor(Tree) == 1 && Copies.FactorOf(Tree) == 1 && Math.Abs(HitDown(TreeAt.X, TreeAt.Z, RoomBuilder.HiddenLayer) - 0.94f) < 0.01f,
            "after the third phase it stands at its natural size, perch and all");
        _host.LearnGrowForTests(5);
        var grown = GrowCast(Player, TreeAt + new Vector3(-0.2f, 0.01f, 0), 0.5);
        Check(Ok(grown) && Kind(grown) == null && Factor(Tree) > 1 && _host.Nature.Plants[Tree].SownS == null, "a replanted tree at its natural size grows on with Grow, a plant like any other again");
        // One plant a cast: of two burnt tufts, the one nearest the cast.
        foreach (var index in new[] { 3, 4 }) _host.Nature.Plants[Tuft(index)] = new NaturePlant { Id = Tuft(index), Burn = 1 };
        var nearest = GrowCast(Player, new Vector3(0.73f, 0.01f, 0.95f), 0.1);
        Check(Ok(nearest) && _host.Nature.Plants[Tuft(4)] is { Burn: 0, SownS: not null } && _host.Nature.Plants[Tuft(3)].Burnt,
            "replanting returns one plant: the nearest burnt one to the cast (a second stays burnt)");
        await FreeWorld();
    }
}

public partial class NatureTest
{
    /// <summary>The bridge's two ledges, 0.2 m high with a 1.1 m gap between them (x from -1.2 to -0.1), and the deck's line over them.</summary>
    private static readonly Rect2 LedgeA = new(-1.9f, -0.45f, 0.7f, 0.3f), LedgeB = new(-0.1f, -0.45f, 0.7f, 0.3f);
    private const float LedgeY = 0.2f, DeckZ = -0.3f;
    private static readonly Vector3 NearLip = new(-1.25f, LedgeY, DeckZ), FarLip = new(-0.05f, LedgeY, DeckZ);

    /// <summary>Soil everywhere, at the ledges' height over them: the ground the bridge's fire burns on.</summary>
    private static NatureCell? Ledges(int x, int z)
    {
        var cell = Soil(x, z)!;
        var at = new Vector2((x + 0.5f) * NatureState.CellM, (z + 0.5f) * NatureState.CellM);
        if (LedgeA.HasPoint(at) || LedgeB.HasPoint(at)) cell.Y = LedgeY;
        return cell;
    }

    private BridgeView? BridgesShown => _world!.GetNode("Room").GetNodeOrNull<BridgeView>(BridgeView.ViewName);

    /// <summary>The first hit straight down from just over the deck at (x, z), any layer a body stands on: its height and what it hit.</summary>
    private (float Y, string Entity) DownOnto(float x, float z, bool up = false)
    {
        var from = new Vector3(x, up ? 0.05f : LedgeY + 0.05f, z);
        var to = new Vector3(x, up ? LedgeY + 0.05f : -0.1f, z);
        using var query = PhysicsRayQueryParameters3D.Create(from, to, RoomBuilder.BodyMask);
        query.HitBackFaces = true;
        var hit = GetViewport().World3D.DirectSpaceState.IntersectRay(query);
        if (hit.Count == 0) return (float.NaN, "");
        var collider = hit["collider"].AsGodotObject() as Node;
        return (hit["position"].AsVector3().Y, collider != null && collider.HasMeta(KernelNames.EntityIdMeta) ? collider.GetMeta(KernelNames.EntityIdMeta).AsString() : collider?.Name ?? "");
    }

    /// <summary>Item 4: the bridge (decision 106).</summary>
    private async Task TestBridge()
    {
        await NewWorld(SavePath("unlocks-bridge"));
        Block("LedgeA", new Vector3(LedgeA.Position.X, 0, LedgeA.Position.Y), new Vector3(LedgeA.End.X, LedgeY, LedgeA.End.Y));
        Block("LedgeB", new Vector3(LedgeB.Position.X, 0, LedgeB.Position.Y), new Vector3(LedgeB.End.X, LedgeY, LedgeB.End.Y));
        _host.NatureSampleForTests = Ledges;
        _host.LearnGrowForTests(3);
        Check(_companion.TryTeleportTo(new Vector3(-0.65f, 0.01f, 0.1f)) && _player.TryTeleportTo(new Vector3(-1.34f, LedgeY + 0.003f, DeckZ)), "(the Gubble by the gap, the player on the near ledge)");
        await Frames(10);
        // The drag's refusals, each before the price.
        var tooLong = GrowCast(Player, NearLip + new Vector3(-0.05f, 0, 0), 0.1, FarLip);
        var tooShort = GrowCast(Player, NearLip, 0.1, NearLip + new Vector3(0.3f, 0, 0));
        var noLip = GrowCast(Player, NearLip, 0.1, new Vector3(-0.5f, LedgeY, DeckZ));
        var noGap = GrowCast(Player, new Vector3(-1.85f, LedgeY, DeckZ), 0.1, NearLip);
        Check(Code(tooLong) == "invalid_args" && Message(tooLong) == "A bridge of plants is at most 1.2 m long." && Code(tooShort) == "invalid_args" &&
              Code(noLip) == "invalid_args" && Message(noLip) == CommandHost.BridgeNoLip && Code(noGap) == "invalid_args" && Message(noGap) == CommandHost.BridgeNoGap,
            $"a bridge is 0.4 to 1.2 m and never longer, lip to lip on ground, over a gap: {Message(tooLong)} | {Message(tooShort)} | {Message(noLip)} | {Message(noGap)}");
        var shim = Block("Shim", new Vector3(-0.1f, LedgeY, -0.45f), new Vector3(0.05f, LedgeY + 0.03f, -0.15f));
        await Frames(2);
        var tilted = GrowCast(Player, NearLip, 0.1, FarLip);
        shim.Free();
        var pillar = Block("Pillar", new Vector3(-0.7f, 0, DeckZ - 0.02f), new Vector3(-0.66f, 0.3f, DeckZ + 0.02f));
        await Frames(2);
        var blocked = GrowCast(Player, NearLip, 0.1, FarLip);
        pillar.Free();
        var island = Block("Island", new Vector3(-0.7f, 0, -0.45f), new Vector3(-0.6f, 0.12f, -0.15f));
        await Frames(2);
        var twoGaps = GrowCast(Player, NearLip, 0.1, FarLip);
        island.Free();
        await Frames(2);
        Check(Code(tilted) == "invalid_args" && Message(tilted) == CommandHost.BridgeNotLevel && Code(blocked) == "invalid_args" && Message(blocked) == CommandHost.BridgeInTheWay &&
              Code(twoGaps) == "invalid_args" && Message(twoGaps) == CommandHost.BridgeTwoGaps && _host.Nature.Bridges.Count == 0,
            $"lips level within 2 cm, nothing in the way, one true void: {Message(tilted)} | {Message(blocked)} | {Message(twoGaps)}");
        // The Gubble's bridge, the player at the lip: one-sided collision over the void alone, and no one moved.
        var (player, _) = Bodies();
        var pips = _host.Vitals.Pips;
        var made = GrowCast(Companion, NearLip, 0.1, FarLip);
        await Frames(10);
        var bridge = _host.Nature.Bridges.FirstOrDefault();
        Check(Ok(made) && Kind(made) == GrowKindWords.Bridge && Grown(made) == 1 && bridge != null && Math.Abs(bridge.LengthM - 1.2f) < 1e-3f &&
              Math.Abs(bridge.SpanFromM - 0.05f) < 0.006f && Math.Abs(bridge.SpanToM - 1.15f) < 0.006f && BridgesShown?.Count == 1 && pips - _host.Vitals.Pips == 2,
            $"the Gubble grows a 1.2 m deck from lip to lip, collided over the 1.1 m void alone (span {bridge?.SpanFromM:0.000} to {bridge?.SpanToM:0.000} m), for its 2 pips: " +
            Canonical(made["error"] ?? made["data"]));
        var middle = DownOnto(-0.65f, DeckZ);
        var lip = DownOnto(-1.22f, DeckZ);
        var under = DownOnto(-0.65f, DeckZ, up: true);
        var shape = BridgesShown!.GetChild(0).GetChild<CollisionShape3D>(0).Shape as ConcavePolygonShape3D;
        Check(Math.Abs(middle.Y - LedgeY) < 1e-3f && middle.Entity == "bridge:1" && lip.Entity != "bridge:1" && float.IsNaN(under.Y) && shape is { BackfaceCollision: false } &&
              _player.GlobalPosition.DistanceTo(player) < 0.001f,
            $"over the void the deck is solid from above ({middle.Entity} at {middle.Y:0.000} m); at the lip the ledge is (\"{lip.Entity}\"); from below nothing ({under.Entity}); " +
            $"and the player at the lip was not moved ({_player.GlobalPosition.DistanceTo(player) * 1000:0.0} mm)");
        // An avatar in the gap where its collision would be: no bridge then.
        Check(Ok(_host.PlayerNatureUndo()) && _host.Nature.Bridges.Count == 0 && BridgesShown?.Count == 0, "Creative's undo takes the bridge back");
        _player.SetPhysicsProcess(false);
        _player.GlobalPosition = new Vector3(-0.65f, LedgeY - 0.05f, DeckZ);
        var busy = GrowCast(Player, NearLip, 0.1, FarLip);
        _player.GlobalPosition = new Vector3(-1.5f, LedgeY + 0.003f, DeckZ);
        _player.SetPhysicsProcess(true);
        Check(Code(busy) == "target_busy" && Message(busy) == CommandHost.BridgeAvatar && _host.Nature.Bridges.Count == 0,
            "no bridge grows while a body is in the gap where its collision would be: " + Message(busy));
        // A small avatar crosses the 1.2 m bridge.
        pips = _host.Vitals.Pips;
        var again = GrowCast(Player, NearLip, 0.1, FarLip);
        var paid = pips - _host.Vitals.Pips;
        await Frames(4);
        _player.Rotation = new Vector3(0, Mathf.Atan2(-1, 0), 0);
        var lowest = float.PositiveInfinity;
        _player.SetControlInput(new Vector2(0, 1));
        for (var frame = 0; frame < 900 && _player.GlobalPosition.X < 0.05f; frame++)
        {
            await Frames(1);
            if (_player.GlobalPosition.X > -1.15f && _player.GlobalPosition.X < -0.15f) lowest = Math.Min(lowest, _player.GlobalPosition.Y);
        }
        _player.SetControlInput(Vector2.Zero);
        GD.Print(System.FormattableString.Invariant($"GROW_BRIDGE crossed the 1.2 m deck: lowest {lowest:0.000} m over the void, reached x {_player.GlobalPosition.X:0.000}"));
        Check(Ok(again) && paid == 4 && _player.GlobalPosition.X >= 0.05f && lowest > LedgeY - 0.01f && lowest < LedgeY + 0.02f,
            $"a small avatar walks across the 1.2 m bridge over the gap without dropping (lowest {lowest:0.000} m over the void; reached x {_player.GlobalPosition.X:0.000})");
        // Another one there, or past the island's most, is refused; saved and back on load.
        var twin = GrowCast(Player, NearLip + new Vector3(0, 0, 0.05f), 0.1, FarLip + new Vector3(0, 0, 0.05f));
        Check(Code(twin) == "invalid_args" && Message(twin) == CommandHost.BridgeHere, "a second deck beside the first is refused: " + Message(twin));
        await NewWorld(SavePath("unlocks-bridge"));
        Block("LedgeA", new Vector3(LedgeA.Position.X, 0, LedgeA.Position.Y), new Vector3(LedgeA.End.X, LedgeY, LedgeA.End.Y));
        Block("LedgeB", new Vector3(LedgeB.Position.X, 0, LedgeB.Position.Y), new Vector3(LedgeB.End.X, LedgeY, LedgeB.End.Y));
        _host.NatureSampleForTests = Ledges;
        _host.LearnGrowForTests(3);
        await Frames(4);
        Check(_host.Nature.Bridges.Count == 1 && BridgesShown?.Count == 1 && DownOnto(-0.65f, DeckZ).Entity == "bridge:1", "the bridge is saved with the island, drawn and collided again on load");
        var block = NatureBlock.Pack(_host.Nature);
        var forgeries = new (string Label, Action<JsonObject> Edit)[]
        {
            ("a bridge longer than 1.2 m", b => b["bridges"]![0]!["from_m"] = new JsonArray(-1.4, LedgeY, DeckZ)),
            ("a bridge that is not level", b => b["bridges"]![0]!["to_m"] = new JsonArray(-0.05, LedgeY + 0.05, DeckZ)),
            ("a span past its deck", b => b["bridges"]![0]!["span_m"] = new JsonArray(0.05, 1.5)),
            ("a bridge id of another spelling", b => b["bridges"]![0]!["id"] = "bridge:01"),
            ("a bridge with a field it does not have", b => b["bridges"]![0]!["owner"] = "companion:local"),
        };
        Check(NatureBlock.Problem(block, _host.Revision, _room) == null, "the block with a bridge passes the strict check");
        foreach (var (label, edit) in forgeries)
            Check(NatureBlock.Problem(Edited(block, edit), _host.Revision, _room) != null, "the strict check refuses a block with " + label);
        // It burns as a plant, caught at a lip, and whoever is on it falls.
        Check(_companion.TryTeleportTo(new Vector3(-0.65f, 0.01f, 0.1f)) && _player.TryTeleportTo(new Vector3(-0.65f, LedgeY + 0.003f, DeckZ)), "(the player stands in the middle of the deck)");
        await Frames(20);
        var stood = _player.GlobalPosition.Y;
        var fire = Fire(Player, new Vector3(-1.4f, LedgeY, DeckZ), 0.3, 30, out _);
        var steps = 0;
        for (; steps < 240 && _host.Nature.Bridges.Count > 0; steps++) Steps(1);
        await Frames(60);
        for (var step = 0; step < 240 && _host.Nature.Fire != null; step++) Steps(1);
        Check(Ok(fire) && _host.Nature.Bridges.Count == 0 && BridgesShown?.Count == 0 && Math.Abs(stood - LedgeY) < 0.01f && _player.GlobalPosition.Y < 0.05f,
            $"a fire at a lip burns the bridge down like any plant in {steps * NatureLaws.StepS:0.0} s, and the player on it falls (from {stood:0.000} m to {_player.GlobalPosition.Y:0.000} m)");
        for (var i = _host.Nature.Bridges.Count; i < NatureState.MaxBridges; i++)
            _host.Nature.Bridges.Add(new NatureBridge { Id = "bridge:" + (i + 1), FromM = new Vector3(1.0f, 0, -1.0f + 0.2f * i), ToM = new Vector3(1.5f, 0, -1.0f + 0.2f * i), SpanFromM = 0.1f, SpanToM = 0.4f });
        var most = GrowCast(Player, NearLip, 0.1, FarLip);
        Check(Code(most) == "invalid_args" && (Message(most) ?? "").Contains($"{NatureState.MaxBridges} bridges", StringComparison.Ordinal), $"the island keeps at most {NatureState.MaxBridges} bridges: " + Message(most));
        _host.Nature.Bridges.Clear();
        await FreeWorld();
    }

    /// <summary>The climb up a face from its foot, as the real body climbs it: the bar it paid between 0.1 m and 0.3 m up, and whether it pulled over at the top.</summary>
    private async Task<(float Paid, bool Top)> ClimbFace(Vector3 foot, float yaw)
    {
        Check(_player.TryTeleportTo(foot), "(the player stands at the foot of the face)");
        _player.Rotation = new Vector3(0, yaw, 0);
        await Frames(30);
        // A full bar first: standable ground refills it.
        for (var frame = 0; frame < 900 && _host.Vitals.Stamina < _host.Vitals.MaxStamina; frame++) await Frames(1);
        float? at10 = null, at30 = null;
        var pulls = _player.PullOvers;
        _player.SetControlInput(new Vector2(0, 1));
        for (var frame = 0; frame < 900 && _player.PullOvers == pulls; frame++)
        {
            await Frames(1);
            if (_player.IsClimbing && _player.GlobalPosition.Y >= foot.Y + 0.1f) at10 ??= _host.Vitals.Stamina;
            if (_player.IsClimbing && _player.GlobalPosition.Y >= foot.Y + 0.3f) at30 ??= _host.Vitals.Stamina;
        }
        _player.SetControlInput(Vector2.Zero);
        await Frames(10);
        return (at10 is { } a && at30 is { } b ? a - b : float.NaN, _player.PullOvers > pulls);
    }

    /// <summary>Item 5: wall ivy (decision 107), in Challenge so the bar drains.</summary>
    private async Task TestIvy()
    {
        await NewWorld(SavePath("unlocks-ivy"), challenge: true);
        Block("Cliff", new Vector3(-1.9f, 0, 0.1f), new Vector3(-1.3f, 0.4f, 1.0f));
        Block("Step", new Vector3(1.3f, 0, -0.5f), new Vector3(1.9f, 0.3f, 0.0f));
        _host.LearnGrowForTests(5);
        Check(_companion.TryTeleportTo(new Vector3(-0.9f, 0.01f, 0.55f)) && _player.TryTeleportTo(new Vector3(-1.0f, 0.003f, 0.3f)), "(the Gubble and the player by the cliff)");
        await Frames(10);
        var noRoot = GrowCast(Player, new Vector3(-0.9f, 0.6f, -0.6f), 0.1);
        Check(Code(noRoot) == "invalid_args" && Message(noRoot) == CommandHost.IvyNoRoot, "ivy needs standable ground within 0.4 m under the aim (the table's side, 0.6 m up): " + Message(noRoot));
        var pips = _host.Vitals.Pips;
        var cast = GrowCast(Player, new Vector3(-1.3f, 0.2f, 0.3f), 0.1);
        var strip = _host.Nature.Ivy.FirstOrDefault();
        var shown = _world!.GetNode("Room").GetNodeOrNull<IvyView>(IvyView.ViewName);
        Check(Ok(cast) && Kind(cast) == GrowKindWords.Ivy && Grown(cast) == 1 && pips - _host.Vitals.Pips == 4 && strip != null && Math.Abs(strip.BaseM.Y) < 0.005f &&
              strip.NormalM.DistanceTo(Vector3.Right) < 0.01f && strip.LedgeM is { } ledge && Math.Abs(ledge.Y - 0.4f) < 0.005f && shown?.Count == 1 && shown.Factor == GrowKindLaws.IvyClimbFactor,
            $"Grow at the cliff roots ivy on the floor at its foot, reaching its 0.4 m ledge, priced as any Grow: {Canonical(cast["error"] ?? cast["data"])} base {strip?.BaseM} ledge {strip?.LedgeM}");
        var near = GrowCast(Player, new Vector3(-1.3f, 0.2f, 0.45f), 0.1);
        var sameLedge = GrowCast(Player, new Vector3(-1.3f, 0.2f, 0.85f), 0.1);
        Check(Code(near) == "invalid_args" && Message(near) == CommandHost.IvyHere && Code(sameLedge) == "invalid_args" && Message(sameLedge) == CommandHost.IvySameLedge,
            $"one ivy a ledge: no strip within 0.3 m of another's foot ({Message(near)}), none to the same ledge 0.55 m along ({Message(sameLedge)})");
        // Another ledge takes its own: the Gubble's, by the step, moving no one.
        Check(_companion.TryTeleportTo(new Vector3(0.9f, 0.01f, -0.25f)) && _player.TryTeleportTo(new Vector3(1.25f, 0.003f, -0.25f)), "(by the step, the player at its foot)");
        await Frames(10);
        var (player, _) = Bodies();
        pips = _host.Vitals.Pips;
        var other = GrowCast(Companion, new Vector3(1.3f, 0.15f, -0.25f), 0.1);
        await Frames(10);
        Check(Ok(other) && _host.Nature.Ivy.Count == 2 && _host.Nature.Ivy[1].LedgeM is { } low && Math.Abs(low.Y - 0.3f) < 0.005f && pips - _host.Vitals.Pips == 2 &&
              _player.GlobalPosition.DistanceTo(player) < 0.001f,
            $"another ledge takes its own strip: the Gubble's at the 0.3 m step, its 2 pips, the player at its foot not moved ({_player.GlobalPosition.DistanceTo(player) * 1000:0.0} mm): " + Message(other));
        // The climb: on the strip the bar drains at a quarter of a bare face's rate; full again once the strip has burnt.
        var bare = await ClimbFace(new Vector3(-1.27f, 0.003f, 0.75f), Mathf.Pi / 2);
        var onIvy = await ClimbFace(new Vector3(-1.27f, 0.003f, 0.3f), Mathf.Pi / 2);
        GD.Print(System.FormattableString.Invariant($"GROW_IVY the bar paid from 0.1 m to 0.3 m up the 0.4 m cliff: bare {bare.Paid:0.0000} m, on ivy {onIvy.Paid:0.0000} m (ratio {onIvy.Paid / bare.Paid:0.000})"));
        Check(bare.Top && onIvy.Top && bare.Paid > 0.05f && Math.Abs(onIvy.Paid / bare.Paid - GrowKindLaws.IvyClimbFactor) < 0.03f,
            $"the real body climbs the cliff on the ivy for a quarter of the bar it pays on the bare face beside it: {onIvy.Paid:0.0000} m against {bare.Paid:0.0000} m over 0.2 m of rise");
        Check(_player.TryTeleportTo(new Vector3(-0.9f, 0.003f, -0.2f)) && _companion.TryTeleportTo(new Vector3(-0.9f, 0.01f, 0.55f)), "(the player steps away from the fire to come; the Gubble by the cliff)");
        await Frames(10);
        var fire = Fire(Player, new Vector3(-1.25f, 0.01f, 0.3f), 0.15, 30, out _);
        var steps = 0;
        for (; steps < 240 && _host.Nature.Ivy.Count > 1; steps++) Steps(1);
        await Frames(2);
        var burnt = await ClimbFace(new Vector3(-1.27f, 0.003f, 0.3f), Mathf.Pi / 2);
        Check(Ok(fire) && _host.Nature.Ivy.Count == 1 && shown?.Count == 1 && burnt.Top && Math.Abs(burnt.Paid / bare.Paid - 1) < 0.05f,
            $"a fire at its foot burns the strip as a plant in {steps * NatureLaws.StepS:0.0} s, and the climb there pays the full price again ({burnt.Paid:0.0000} m against {bare.Paid:0.0000} m; " +
            $"top {burnt.Top}, strips {_host.Nature.Ivy.Count}, drawn {shown?.Count}): " + Canonical(fire["error"]));
        await NewWorld(SavePath("unlocks-ivy"), challenge: true);
        await Frames(2);
        Check(_host.Nature.Ivy.Count == 1 && _world!.GetNode("Room").GetNodeOrNull<IvyView>(IvyView.ViewName)?.Count == 1, "the ivy is saved with the island and drawn again on load");
        var block = NatureBlock.Pack(_host.Nature);
        var forgeries = new (string Label, Action<JsonObject> Edit)[]
        {
            ("ivy on a face too gentle for it", b => b["ivy"]![0]!["normal"] = new JsonArray(0.6, 0.8, 0)),
            ("ivy whose normal is no unit", b => b["ivy"]![0]!["normal"] = new JsonArray(2, 0, 0)),
            ("ivy past the island's most", b => b["ivy"] = new JsonArray(Enumerable.Range(1, NatureState.MaxIvy + 1).Select(i => (JsonNode?)new JsonObject
                { ["id"] = "ivy:" + i, ["base_m"] = new JsonArray(0, 0, 0), ["normal"] = new JsonArray(1, 0, 0) }).ToArray())),
            ("two strips of one id", b => b["ivy"]!.AsArray().Add(b["ivy"]![0]!.DeepClone())),
        };
        Check(NatureBlock.Problem(block, _host.Revision, _room) == null, "the block with ivy passes the strict check");
        foreach (var (label, edit) in forgeries)
            Check(NatureBlock.Problem(Edited(block, edit), _host.Revision, _room) != null, "the strict check refuses a block with " + label);
        await FreeWorld();
    }
}
