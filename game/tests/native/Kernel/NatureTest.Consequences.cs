using Godot;
using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Room;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// The founder's decision of 13 October: "Let things like fire or other actions that might hurt a player, hurt the player.
/// This becomes feedback for the AI." In Challenge and Live the Gubble's fire hurts the player like any fire, with no hold;
/// the Gubble's next result carries a fixed-format notice, and the journal keeps a consequence fact in the game's words.
/// Guardrails instead of holds: one fire of the Gubble's own at a time and a wait after it, the player's Stop puts it out,
/// the player's setting "Let the Gubble use fire", a grace after recovery and a recovery point out of the fire. The preview
/// names the avatars in the area as the caster perceives them. Undo is Creative's alone, with no back door.
/// </summary>
public partial class NatureTest
{
    private async Task TestConsequences()
    {
        await TestCompanionFireHurtsPlayer();
        await TestCreativeNeverHurts();
        await TestCompanionFireBudget();
        await TestGubbleFireSetting();
        await TestRecoveryGrace();
        await TestPreviewAvatars();
        await TestUndoOnlyInCreative();
    }

    private Vector3 PlayerSpot() => new(_player.GlobalPosition.X, 0.01f, _player.GlobalPosition.Z);

    /// <summary>Held at 12af51c (approval_required); now it burns, hurts, notices and journals.</summary>
    private async Task TestCompanionFireHurtsPlayer()
    {
        await NewWorld(SavePath("consequence"), challenge: true);
        var before = _host.Vitals.Health;
        var lit = Fire(Companion, PlayerSpot(), 0.25, 6, out var preview);
        Check(Ok(preview) && Ok(lit), "in Challenge the Gubble's fire that reaches the player is lit with no hold: " + Code(lit));
        Check(preview["data"]?["avatars_in_area"]?.AsArray().Any(a => a!.GetValue<string>() == CommandHost.PlayerAvatar) == true && preview["data"]?["requires_approval"]?.GetValue<bool>() == false,
            "its preview told the Gubble the player stands in the area: " + Canonical(preview["data"]?["avatars_in_area"]));
        Steps(2);
        var after = _host.Vitals.Health;
        Check(after < before, $"and it hurts the player ({before} to {after})");
        var status = Send(Query("player.status", new JsonObject()), Companion);
        var notice = status["notices"]?.AsArray().FirstOrDefault();
        Check(Ok(status) && notice?["kind"]?.GetValue<string>() == "fire_hurt_player" && notice["health_before"]?.GetValue<double>() == before &&
            notice["health_after"]?.GetValue<double>() == after && notice["fire_revision"]?.GetValue<int>() == lit["revision"]?.GetValue<int>() &&
            notice["recovered"]?.GetValue<bool>() == false && notice.AsObject().Count == 6,
            "the Gubble's next result carries the fixed-format notice: what happened, health before and after, which fire: " + Canonical(status["notices"]));
        var again = Send(Query("player.status", new JsonObject()), Companion);
        Check(Ok(again) && again["notices"] == null && Send(Query("player.status", new JsonObject()), Player)["notices"] == null, "a notice is delivered once, and only to the Gubble");
        // One step today (6 health a step); two under a pack in quarters (a quarter a second, NatureTest.Energy).
        Steps(2);
        var next = Send(Command(NextId("look"), "goal.set", new JsonObject { ["actor"] = CommandHost.CompanionAvatarId, ["goal"] = "stay" }), Companion);
        Check(next["notices"]?[0]?["health_before"]?.GetValue<double>() == after, "more harm from the same fire after it was read is a new notice, on any result: " + Canonical(next["notices"]));
        var journal = Send(Query("journal.read", new JsonObject { ["kind"] = "consequence" }), Companion);
        // Since M8 the lessons hold Trust's words too ("Trust fell: ..."): the fire's own facts are the ones in its words.
        var facts = new JsonArray(journal["data"]?["entries"]?.AsArray().Where(e => e?["line"]?.GetValue<string>().StartsWith("The Gubble's fire", StringComparison.Ordinal) == true)
            .Select(e => e!.DeepClone()).ToArray() ?? Array.Empty<JsonNode?>());
        var fact = facts.FirstOrDefault();
        Check(Ok(journal) && facts.Count == 1 && fact?["line"]?.GetValue<string>().StartsWith("The Gubble's fire burned you", StringComparison.Ordinal) == true &&
            fact["actor"]?.GetValue<string>() == Companion && fact["kind"]?.GetValue<string>() == "consequence" && fact["pin_m"] != null,
            "the journal keeps one consequence fact for the fire, in the game's own words, read back by kind: " + Canonical(journal["data"]));
    }

    /// <summary>Creative never hurts, whoever lit the fire, and so there is nothing to tell.</summary>
    private async Task TestCreativeNeverHurts()
    {
        await NewWorld(SavePath("creative-fire"));
        var lit = Fire(Companion, PlayerSpot(), 0.25, 6, out _);
        Steps(6);
        var status = Send(Query("player.status", new JsonObject()), Companion);
        Check(Ok(lit) && _host.Vitals.Health == 100 && status["notices"] == null && _host.UnreadNotices.Count == 0, "in Creative the Gubble's fire on the player hurts no one and sends no notice");
    }

    /// <summary>One fire of the Gubble's own at a time, the player's Stop puts it out, a wait after it; the player is never slowed.</summary>
    private async Task TestCompanionFireBudget()
    {
        await NewWorld(SavePath("budget"));
        var lit = Fire(Companion, new Vector3(0.9f, 0.01f, 1.2f), 0.2, 10, out _);
        Check(Ok(lit) && _host.Nature.Fire?.Principal == Companion, "the Gubble lights a fire of its own");
        var second = Send(Command(NextId("second"), "nature.cast", Cast("fire", NearGubble, 0.15, 6), preview: true), Companion);
        Check(Code(second) == "target_busy", "one fire of its own at a time: " + Code(second));
        var stop = _host.PlayerGoal("stop");
        Check(Ok(stop) && stop["data"]?["fires_put_out"]?.GetValue<int>() == 1 && _host.Nature.Fire == null && !_host.Nature.Cells.Values.Any(c => c.BurningS > 0),
            "the player's Stop (X) puts the Gubble's fire out at once: " + Canonical(stop["data"]));
        var soon = Send(Command(NextId("soon"), "nature.cast", Cast("fire", NearGubble, 0.15, 6), preview: true), Companion);
        Check(Code(soon) == "rate_limited" && soon["error"]?["retryable"]?.GetValue<bool>() == true, "the Gubble waits after its fire goes out: " + soon["error"]?["message"]);
        Steps((int)(NatureLaws.CompanionFireCooldownS / NatureLaws.StepS) + 1);
        var later = Fire(Companion, NearGubble, 0.15, 6, out _);
        Check(Ok(later) && _host.Nature.Fire?.Principal == Companion, "after the wait the Gubble may light another: " + Code(later));
        var mine = Fire(Player, new Vector3(1.35f, 0.01f, 1.2f), 0.15, 4, out _);
        Check(Ok(mine) && _host.Nature.Fire?.Principal == Player, "the player's own Fire is never kept waiting by the Gubble's: it puts that one out: " + Code(mine));
        var busy = Send(Command(NextId("busy"), "nature.cast", Cast("fire", new Vector3(0.9f, 0.01f, 0.3f), 0.15, 6), preview: true), Companion);
        Check(Code(busy) is "target_busy" or "rate_limited", "and the Gubble waits again: " + Code(busy));
    }

    /// <summary>The player's own setting: off, the Gubble's Fire is refused with a clear code; only the player's control changes it.</summary>
    private async Task TestGubbleFireSetting()
    {
        await NewWorld(SavePath("fire-setting"));
        _host.SetGubbleFire(false);
        var refused = Send(Command(NextId("off"), "nature.cast", Cast("fire", NearGubble, 0.15, 6), preview: true), Companion);
        var mine = Send(Command(NextId("mine"), "nature.cast", Cast("fire", NearGubble, 0.15, 6), preview: true), Player);
        Check(Code(refused) == "permission_denied" && refused["error"]?["message"]?.GetValue<string>() == CommandHost.FireOffMessage && Ok(mine),
            "with \"Let the Gubble use fire\" off the Gubble's Fire is refused with permission_denied; the player's own is not");
        var bloom = Send(Command(NextId("bloom"), "nature.cast", Cast("grow", NearGubble, 0.15, 1)), Companion);
        Check(Ok(bloom), "its other magic is untouched");
        _host.SetGubbleFire(true);
    }

    /// <summary>After an injury recovery the player is safe from fire for a moment, and never recovers inside a burning area.</summary>
    private async Task TestRecoveryGrace()
    {
        await NewWorld(SavePath("grace"), challenge: true);
        // Leave the player one burn from a knockout: today's 6 health a step, or one quarter when the pack counts hearts in quarters.
        var oneBurn = _host.BodyRules.InQuarters ? (float)_host.BodyRules.HealthPerQuarter : NatureLaws.PlayerBurnDamage;
        _host.ApplyPlayerDamage(_host.Vitals.MaxHealth - oneBurn, PlayerDamageSource.Fall);
        var lit = Fire(Companion, PlayerSpot(), 0.3, 20, out _);
        Steps(2);
        var at = _player.GlobalPosition;
        bool BurningNear() => _host.Nature.Cells.Values.Any(c => c.BurningS > 0 && new Vector2(c.Position.X - at.X, c.Position.Z - at.Z).Length() <= NatureLaws.RecoverySafeM);
        Check(Ok(lit) && _host.Vitals.Recoveries == 1 && _host.Vitals.Health == PlayerVitals.MaximumHealth / 2 && !BurningNear(),
            "the Gubble's fire put the player down; they recover, and the recovery point is not inside a burning area");
        var graced = _host.ApplyPlayerDamage(NatureLaws.PlayerBurnDamage, PlayerDamageSource.Fire);
        Steps(6);
        Check(graced == null && _host.Vitals.Health == PlayerVitals.MaximumHealth / 2 && !BurningNear(), "during the grace fire cannot hurt them, and the ground there does not catch again");
        var notice = Send(Query("player.status", new JsonObject()), Companion)["notices"]?[0];
        var fact = Send(Query("journal.read", new JsonObject { ["kind"] = "consequence" }), Companion)["data"]?["entries"]?.AsArray()
            .FirstOrDefault(e => e?["line"]?.GetValue<string>().StartsWith("The Gubble's fire", StringComparison.Ordinal) == true);
        Check(notice?["recovered"]?.GetValue<bool>() == true && notice["health_after"]?.GetValue<double>() == 0 &&
            fact?["line"]?.GetValue<string>().EndsWith("until you had to recover.", StringComparison.Ordinal) == true,
            "the Gubble's notice and the journal say the player had to recover: " + Canonical(notice) + " " + fact?["line"]);
        Steps(6);
        await Frames(70);
        var hurt = _host.ApplyPlayerDamage(NatureLaws.PlayerBurnDamage, PlayerDamageSource.Fire);
        Check(hurt is { Recovered: false } && _host.Vitals.Health < PlayerVitals.MaximumHealth / 2, "after the grace fire hurts again");
    }

    /// <summary>The Fire preview's avatars, filtered by what the caster perceives: an avatar the Gubble cannot see is never named.</summary>
    private async Task TestPreviewAvatars()
    {
        await NewWorld(SavePath("avatars"), challenge: true);
        _host.SharedSight = false;
        var wall = new StaticBody3D { Name = "AvatarWall", Position = new Vector3(0.22f, 0.15f, 0.6f), CollisionLayer = RoomBuilder.WorldLayer, CollisionMask = 0 };
        wall.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(0.02f, 0.3f, 0.3f) } });
        _world!.AddChild(wall);
        await Frames(2);
        var args = Cast("fire", new Vector3(0.3f, 0.01f, 0.35f), 0.5, 6);
        var gubble = Send(Command(NextId("avatars-gubble"), "nature.cast", (JsonObject)args.DeepClone(), preview: true), Companion);
        var player = Send(Command(NextId("avatars-player"), "nature.cast", (JsonObject)args.DeepClone(), preview: true), Player);
        static string Names(JsonObject result) => Canonical(result["data"]?["avatars_in_area"]);
        Check(Ok(gubble) && Ok(player) && Names(player) == "[\"avatar:player\",\"avatar:companion\"]" && Names(gubble) == "[\"avatar:companion\"]",
            "the player's preview names both avatars in the area; the Gubble's names only what it sees: " + Names(gubble) + " / " + Names(player));
        _host.SharedSight = true;
    }

    /// <summary>
    /// Undo is Creative's alone (the founder, 13 October): refused in Challenge for the player and the Gubble alike; a change
    /// recorded in Challenge, or a Creative fire that burned on in Challenge, stays undone-proof after switching back.
    /// </summary>
    private async Task TestUndoOnlyInCreative()
    {
        await NewWorld(SavePath("undo-modes"), challenge: true);
        var bloom = Send(Command(NextId("challenge-bloom"), "nature.cast", Cast("grow", NearGubble, 0.15, 1)), Player);
        JsonObject Undo(string principal) =>
            Send(Command(NextId("undo"), "room.undo", new JsonObject { ["to_revision"] = Math.Max(0, _host.Revision - 1) }, expectedRevision: _host.Revision), principal);
        var mine = Undo(Player);
        var gubble = Undo(Companion);
        Check(Ok(bloom) && Code(mine) == "unsupported_capability" && Code(gubble) == "unsupported_capability" && _host.Nature.Plants.Count > 0 && !_host.NatureCanUndo,
            "in Challenge there is no undo, for the player or the Gubble: " + mine["error"]?["message"]);
        Check(Code(Send(Command(NextId("undo-build"), "room.undo", new JsonObject { ["to_revision"] = 0 }, expectedRevision: _host.Revision), Player)) == "unsupported_capability",
            "whatever it names");
        Check(Ok(_host.PlayerMode("creative")), "the player switches to Creative");
        var after = Undo(Player);
        Check(Code(after) == "unsupported_capability" && _host.Nature.Plants.Count > 0 && !_host.NatureCanUndo,
            "a change made in Challenge cannot be undone after switching to Creative: " + after["error"]?["message"]);
        var fire = Fire(Player, new Vector3(0.9f, 0.01f, 1.2f), 0.2, 10, out _);
        Check(Ok(fire) && _host.NatureCanUndo, "a fire lit in Creative can be undone there");
        Check(Ok(_host.PlayerMode("challenge")), "the player switches to Challenge while it burns");
        Steps(2);
        Check(Ok(_host.PlayerMode("creative")) && !_host.NatureCanUndo && Code(Undo(Player)) == "unsupported_capability",
            "a Creative fire that burned on in Challenge can no longer be undone: no back door");
    }
}
