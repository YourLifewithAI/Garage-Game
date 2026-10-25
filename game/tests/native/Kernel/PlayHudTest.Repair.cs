using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Sandbox;
using GarageGame.Native.Ui;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// The repair's icon (MECHANICS.md decision 131), in the inventory room with the real host and HUD. The Gubble builds a one-part stone shed, fire
/// scorches it, the player gathers the room's pebble (a stone), and the Gubble's repair goes through without asking: the HUD puts a card over the
/// shed with a stone's mark and the kernel's words ("1 stone"), where the host said (the shed's top, on the screen), and it fades and goes in about
/// three seconds. Then, from the host's word as the HUD gets it (the seam): two kinds with their marks ("2 logs, 1 stone"), the cap of three
/// cards, a second repair of the same thing replacing its card, a repair that spent nothing showing none, the cards standing aside for the
/// journal, a thing behind the view, the place by numbers at both window sizes and the cost against the HUD's budget; and the words of the camp's
/// Inventory page and the Magic panel's Repair section, which say how the Gubble pays and nothing of asking.
/// </summary>
public partial class PlayHudTest
{
    private static readonly Vector3 InvShedAt = new(-0.95f, 0, -0.1f);

    private JsonObject InvSend(string op, JsonObject args, string principal) =>
        _ih.HandleObject(CanonicalJson.Text(new JsonObject
        {
            ["schema"] = "garage_game.command", ["version"] = 1, ["action_id"] = "inv-" + op.Replace('.', '-') + "-" + Guid.NewGuid().ToString("N")[..8], ["room_id"] = _ih.Room.RoomId,
            ["op"] = op, ["args"] = args,
        }), principal);

    /// <summary>A shed of one stone part (its repair quote is one stone), placed by the principal.</summary>
    private string InvBuildStoneShed(Vector3 at, string principal)
    {
        var source = new JsonObject
        {
            ["schema"] = "garage_game.creation", ["version"] = 1, ["name"] = "Hut", ["seed"] = 1, ["mount"] = "ground",
            ["parts"] = new JsonArray(new JsonObject
            {
                ["id"] = "a", ["shape"] = "box", ["position_m"] = new JsonArray(0, 0.06, 0), ["rotation_deg"] = new JsonArray(0, 0, 0), ["size_m"] = new JsonArray(0.12, 0.12, 0.12), ["material"] = "stone",
            }),
            ["nodes"] = new JsonArray(new JsonObject { ["id"] = "use", ["op"] = "interact", ["part_id"] = "a", ["params"] = new JsonObject() }),
            ["edges"] = new JsonArray(),
        };
        var made = InvSend("creation.place", new JsonObject { ["source"] = source, ["placement"] = new JsonObject { ["position_m"] = new JsonArray(at.X, 0, at.Z) } }, principal);
        Check(made["ok"]?.GetValue<bool>() == true, "(the hut is built: " + CanonicalJson.Text(made["error"] ?? new JsonObject()) + ")");
        return made["created"]?[0]?.GetValue<string>() ?? "";
    }

    private async Task InvRepairIcon()
    {
        var host = _ih;
        var hud = _ihud;
        var world = _iw;
        FreshBodies(host);
        Check(host.PlayerMode("challenge")["ok"]?.GetValue<bool>() == true && host.Mode == "challenge", "(Challenge, where a repair spends)");
        await Frames(8);
        // The pockets hold a stone: the room's second pebble (Creative used the first), carried and gathered with E.
        if (host.HeldBy(CommandHost.PlayerAvatar) != null) { host.PlayerHands(); await Frames(10); }
        await InvStand(new Vector3(-0.75f, 0, 0.38f));
        var grabbed = InvSend("entity.grab", new JsonObject { ["target"] = "obj:pebble_2", ["actor"] = CommandHost.PlayerAvatar }, CommandHost.PlayerPrincipal);
        Check(grabbed["ok"]?.GetValue<bool>() == true && host.HeldBy(CommandHost.PlayerAvatar) == "obj:pebble_2", "the player takes the second pebble in hand: " + CanonicalJson.Text(grabbed["error"] ?? new JsonObject()));
        await InvStand(InvAway);
        await Frames(40);
        await InvKey(Key.E, 8);
        Check(InvUnits("stone") == 1, "and gathers it: a stone in the pockets");
        // The Gubble builds a hut where it can see, and fire scorches it.
        Check(world.Companion.TryTeleportTo(InvShedAt + new Vector3(0.1f, 0.01f, -0.35f)), "(the Gubble floats near the hut's spot)");
        world.Companion.Rotation = new Vector3(0, Mathf.Pi, 0);
        await Frames(6);
        var hut = InvBuildStoneShed(InvShedAt, CommandHost.CompanionPrincipal);
        host.ScorchForTests(hut);
        // The player stands facing the hut, so its card is in front of the view.
        await InvFace(InvShedAt + new Vector3(0.05f, 0, 0.55f), InvShedAt);
        hud.SetViewMode(0);
        await Frames(10);
        var seen = new List<RepairSpentView>();
        host.RepairSpent += seen.Add;
        var revision = host.Revision;
        var repaired = InvSend("nature.repair", new JsonObject { ["target"] = hut }, CommandHost.CompanionPrincipal);
        Check(repaired["ok"]?.GetValue<bool>() == true && repaired["approved_by"] == null && host.PendingApprovals.Count == 0 && host.Revision == revision + 1 && InvUnits("stone") == 0 &&
              CanonicalJson.Text(repaired["data"]?["spent"] ?? new JsonObject()) == CanonicalJson.Text(new JsonObject { ["stone"] = 1 }),
            "the Gubble's repair spends the stone without asking: " + CanonicalJson.Text(repaired));
        Check(seen.Count == 1 && seen[0].Thing == hut && seen[0].Spent.Count == 1 && seen[0].Spent[0].Words == "1 stone", "and the host raises RepairSpent once, for the hut: 1 stone");
        await Frames(3);
        var icon = hud.RepairIcons.SingleOrDefault();
        Check(icon != null && icon.Thing == hut && icon.Words == "1 stone" && icon.Marks.Count == 1 && icon.Marks[0].KindMaterial == "stone" && icon.Labels.Single().Text == "1 stone" && icon.Visible,
            $"the HUD puts a card over the hut: \"{icon?.Words}\" with a stone's mark");
        if (icon == null) return;
        var camera = hud.GetViewport().GetCamera3D();
        var screen = hud.GetViewport().GetVisibleRect().Size;
        var anchor = camera.UnprojectPosition(icon.At);
        var rect = new Rect2(icon.Position, icon.Size);
        Check(!camera.IsPositionBehind(icon.At) && icon.At.Y > 0.1f && rect.End.Y <= anchor.Y && Mathf.Abs(rect.GetCenter().X - anchor.X) < 2 && icon.GetGlobalRect().Encloses(icon.Marks[0].GetGlobalRect()) &&
              new Rect2(Vector2.Zero, screen).Encloses(rect) && icon.MouseFilter == Control.MouseFilterEnum.Ignore && icon.FindChildren("*", "Control", true, false).OfType<Control>().All(c => c.MouseFilter == Control.MouseFilterEnum.Ignore),
            $"it stands over the hut's top as the host gave it (the card {rect.Position.X:0},{rect.Position.Y:0} {rect.Size.X:0}x{rect.Size.Y:0}, its foot above the point at y {anchor.Y:0}), on the screen, and takes no mouse");
        Check(icon.Marks[0].Size == new Vector2(MaterialMark.SizePx, MaterialMark.SizePx) && icon.Marks[0].Draws >= 1 && icon.Labels[0].GetThemeFontSize("font_size") == RepairIcon.FontSize,
            $"its stone mark is {MaterialMark.SizePx:0} px and was drawn ({icon.Marks[0].Draws} time), its words {RepairIcon.FontSize} px");
        // Fading after about three seconds, as a notice does.
        await Frames(100);
        var mid = (icon.Age, icon.Modulate.A, icon.Position.Y);
        await Frames(60);
        var late = (icon.Age, icon.Modulate.A, icon.Position.Y);
        Check(mid.Item2 > 0.99f && late.Item2 < 1f && late.Item2 > 0 && late.Item3 < mid.Item3 && late.Item1 > RepairIcon.HoldS && late.Item1 < RepairIcon.LifeS,
            $"it holds for {RepairIcon.HoldS:0.0} s, rising a little ({mid.Item3:0} to {late.Item3:0}), then fades: opacity {mid.Item2:0.00} at {mid.Item1:0.0} s, {late.Item2:0.00} at {late.Item1:0.0} s");
        await Frames(40);
        Check(hud.RepairIcons.Count == 0 && !IsInstanceValid(icon), "and it is gone in about three seconds (" + RepairIcon.LifeS + " s)");

        await InvRepairIconCases();
        FreshBodies(host);
        host.PlayerMode("creative");
        await Frames(8);
    }

    /// <summary>The cards from the host's word as the HUD gets it: kinds and marks, the cap, replacing, nothing spent, standing aside, behind the view, the place by numbers, the cost, the words.</summary>
    private async Task InvRepairIconCases()
    {
        var host = _ih;
        var hud = _ihud;
        var camera = hud.GetViewport().GetCamera3D();
        var screen = hud.GetViewport().GetVisibleRect().Size;
        var front = camera.GlobalPosition - camera.GlobalBasis.Z * 0.5f;
        RepairSpentView View(string thing, Vector3 at, params (string Kind, int Count, string Words)[] spent) =>
            new(thing, at, spent.Select(s => new SpentUnits(s.Kind, s.Count, s.Words)).ToList());
        // Two kinds, in the pack's order: "2 logs, 1 stone" with a log's and a stone's marks.
        hud.RepairSpentForTests(View("obj:hut-a", front, ("log", 2, "2 logs"), ("stone", 1, "1 stone")));
        await Frames(3);
        var two = hud.RepairIcons.SingleOrDefault();
        Check(two != null && two.Words == "2 logs, 1 stone" && two.Marks.Select(m => m.KindMaterial).SequenceEqual(new[] { "wood", "stone" }) && two.Labels.Select(l => l.Text).SequenceEqual(new[] { "2 logs,", "1 stone" }),
            $"two kinds read \"2 logs, 1 stone\" with the log's mark (wood) and the stone's: {two?.Words}");
        // A kind no material is made of gets a plain dot; words are shown as plain text, however long.
        hud.RepairSpentForTests(View("obj:hut-b", front, ("seed", 1, "1 seed " + new string('x', 80))));
        await Frames(3);
        var dot = hud.RepairIcons.Last();
        Check(dot.Marks.Single().KindMaterial == "" && dot.Words.Length <= 40 && dot.Words.StartsWith("1 seed", StringComparison.Ordinal),
            $"a kind of no material gets a plain dot, and its words are clipped as display text (\"{dot.Words.Length}\" characters)");
        // A repair that spent nothing shows none.
        hud.RepairSpentForTests(new RepairSpentView("obj:hut-c", front, new List<SpentUnits>()));
        await Frames(3);
        Check(hud.RepairIcons.Count == 2, "a repair that spent nothing shows no card");
        // The same thing repaired again: its card is replaced, the others stay.
        var olderAge = hud.RepairIcons[0].Age;
        hud.RepairSpentForTests(View("obj:hut-a", front, ("log", 1, "1 log")));
        await Frames(3);
        Check(hud.RepairIcons.Count == 2 && hud.RepairIcons.Count(i => i.Thing == "obj:hut-a") == 1 && hud.RepairIcons.Last().Words == "1 log" && hud.RepairIcons.Last().Age < olderAge,
            "a second repair of the same thing takes its card's place, newest last: " + string.Join(" | ", hud.RepairIcons.Select(i => i.Thing + ": " + i.Words)));
        // The cap: three at once, the oldest goes.
        hud.RepairSpentForTests(View("obj:hut-d", front, ("stone", 1, "1 stone")));
        hud.RepairSpentForTests(View("obj:hut-e", front, ("log", 1, "1 log")));
        await Frames(3);
        Check(hud.RepairIcons.Count == RoomHud.RepairIconsMax && hud.RepairIcons.Select(i => i.Thing).SequenceEqual(new[] { "obj:hut-a", "obj:hut-d", "obj:hut-e" }),
            "at most three cards stand, the oldest giving way to the newest: " + string.Join(", ", hud.RepairIcons.Select(i => i.Thing)));
        // Standing aside for the journal, back when it closes (the notice does the same).
        hud.OpenJournal();
        await Frames(JournalMoveFrames);
        var aside = hud.RepairIcons.All(i => !i.Visible);
        hud.CloseJournal();
        await Frames(JournalMoveFrames);
        Check(aside && hud.RepairIcons.Count > 0 && hud.RepairIcons.All(i => i.Visible), "the cards stand aside while the journal fills the screen and are back when it closes");
        // A thing behind the view puts the card in the lower middle of the screen, as the focus tag's rule is.
        await Frames(200);
        hud.RepairSpentForTests(View("obj:hut-f", camera.GlobalPosition + camera.GlobalBasis.Z * 1.0f, ("stone", 1, "1 stone")));
        await Frames(3);
        var behind = hud.RepairIcons.Single(i => i.Thing == "obj:hut-f");
        var behindRect = new Rect2(behind.Position, behind.Size);
        Check(camera.IsPositionBehind(behind.At) && Mathf.Abs(behindRect.GetCenter().X - screen.X * 0.5f) < 2 && behindRect.Position.Y > screen.Y * 0.6f && behindRect.End.Y <= RoomHud.BarClearTop(screen),
            $"a thing behind the view puts its card in the lower middle of the screen, above the bar's caption ({behindRect.Position.X:0},{behindRect.Position.Y:0})");
        await Frames(220);
        Check(hud.RepairIcons.Count == 0, "and every card is gone after its three seconds");

        // The place by numbers, at both window sizes: centred over the point with its foot above it, kept on the screen, above the bar's caption and out of the minimap's corner.
        foreach (var size in new[] { new Vector2(1280, 720), new Vector2(1152, 648) })
        {
            var card = new Vector2(150, 34);
            var middle = RoomHud.RepairIconPlace(size * 0.5f, card, size);
            var low = RoomHud.RepairIconPlace(new Vector2(size.X * 0.5f, size.Y - 5), card, size);
            var corner = RoomHud.RepairIconPlace(new Vector2(size.X - 20, 30), card, size);
            var edge = RoomHud.RepairIconPlace(new Vector2(-300, 400), card, size);
            var risen = RoomHud.RepairIconPlace(size * 0.5f, card, size, RepairIcon.RisePx);
            var tallest = RoomHud.BarClearTop(size);
            var minimap = new Rect2(size.X - SandboxControls.FocusWords.MinimapWidthPx, 0, SandboxControls.FocusWords.MinimapWidthPx, SandboxControls.FocusWords.MinimapHeightPx);
            Check(Mathf.Abs(middle.X + card.X * 0.5f - size.X * 0.5f) <= 1 && Mathf.Abs(middle.Y + card.Y + RoomHud.RepairIconAbovePx - size.Y * 0.5f) <= 1 && Mathf.Abs(risen.Y - (middle.Y - RepairIcon.RisePx)) <= 1 &&
                  low.Y + card.Y <= tallest + 0.01f && edge.X == RoomHud.RepairIconMarginPx && !new Rect2(corner, card).Intersects(minimap) &&
                  new[] { middle, low, corner, edge, risen }.All(p => new Rect2(Vector2.Zero, size).Grow(-RoomHud.RepairIconMarginPx + 0.5f).Encloses(new Rect2(p, card))),
                $"at {size.X:0}x{size.Y:0} a card is centred over its point with its foot {RoomHud.RepairIconAbovePx:0} px above it ({middle.X:0},{middle.Y:0}), rises {RepairIcon.RisePx:0} px, stays on the screen, ends above the bar's caption ({low.Y + card.Y:0} of {tallest:0}) and clear of the minimap");
        }
        var life = RepairIcon.LifeS;
        Check(RepairIcon.AlphaAt(0) == 1 && RepairIcon.AlphaAt(RepairIcon.HoldS) == 1 && RepairIcon.AlphaAt(RepairIcon.HoldS + RepairIcon.FadeS * 0.5) is > 0.45f and < 0.55f && RepairIcon.AlphaAt(RepairIcon.LifeS) == 0 &&
              life is > 2.5 and < 3.5 && RepairIcon.RiseAt(0) == 0 && Mathf.IsEqualApprox(RepairIcon.RiseAt(RepairIcon.LifeS), RepairIcon.RisePx),
            $"a card holds {RepairIcon.HoldS:0.0} s, fades over {RepairIcon.FadeS:0.0} s ({RepairIcon.LifeS:0.0} in all) and rises {RepairIcon.RisePx:0} px");

        // The cost: three cards standing, frame after frame, against the HUD's budget; and the frames with none cost no more than a comparison.
        PromptCost.Reset();
        PromptCost.Measuring = true;
        await Frames(120);
        var none = (PromptCost.PieceMeanMs(PromptCost.Piece.Repair), PromptCost.AllocatedBytes);
        PromptCost.Reset();
        for (var i = 0; i < 3; i++) hud.RepairSpentForTests(View("obj:hut-" + i, front + new Vector3(0.05f * i, 0, 0), ("log", 2, "2 logs"), ("stone", 1, "1 stone")));
        await Frames(150);
        var standing = (PromptCost.PieceMeanMs(PromptCost.Piece.Repair), PromptCost.MeanMs, PromptCost.Frames, hud.RepairIcons.Count);
        PromptCost.Measuring = false;
        GD.Print($"PLAY_HUD_COST: the repair icons, ms a frame: none standing {none.Item1:0.0000} ({none.Item2} bytes), three standing {standing.Item1:0.0000}, the pass's whole {standing.Item2:0.0000} over {standing.Item3} frames");
        Check(none.Item1 < 0.02 && standing.Item1 < 0.1 && standing.Item2 < 0.5,
            $"the icons cost {none.Item1:0.0000} ms a frame while none stand and {standing.Item1:0.0000} ms with three standing, against the HUD's budget of 0.5 ms");
        await Frames(200);

        // The words: how the Gubble pays, in the camp's Inventory page and the Magic panel's Repair section, and nothing of asking.
        var camp = string.Join("\n", InventoryPages.CampLines(host).Select(l => l.Text));
        var magic = hud.FindChild("GubbleRepairWords", true, false) as Label;
        var banned = new[] { "approv", "ask", "permission", "confirm", "your yes", "your OK", "waits" };
        Check(camp.Contains(InventoryPages.GubbleRepairWords, StringComparison.Ordinal) && magic != null && magic.Text == InventoryPages.GubbleRepairWords &&
              !banned.Any(b => InventoryPages.GubbleRepairWords.Contains(b, StringComparison.OrdinalIgnoreCase)) && InventoryPages.GubbleRepairWords.Contains("card over the thing", StringComparison.Ordinal),
            "the camp's Inventory page and the Magic panel's Repair section say how the Gubble pays and that a card over the thing shows it, and nothing of approval: " + InventoryPages.GubbleRepairWords);
    }
}
