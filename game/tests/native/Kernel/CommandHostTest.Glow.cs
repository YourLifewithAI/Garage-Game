using Godot;
using System;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Look;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// Glow, the Gubble's first ability (docs/runs/RUN-2-GLOW.md section 2): the island's rules pack the game ships
/// (RoomWorld.DefaultRulesVersion, storybook_wild v4 or v5: the same abilities, Grow (v4's Bloom read as Grow) and Fire beside the effects; ShippedRules), loaded fail-closed;
/// capabilities.list from it; effect.start for glow on the Gubble (self) and at a spot (point), with its bounds, who may
/// cast it, its targets, the caps, preview, replay and expiry; effect.stop by id and all; PlayerEffect for the HUD; the
/// look hook (a real LookDirector shows each glow drawn and released) and the aura colour.
/// </summary>
public partial class CommandHostTest
{
    private static readonly string ShippedPack = IslandRules.PathFor(RoomWorld.DefaultRulesId, ShippedRules.Version);

    private async Task TestGlow()
    {
        TestRulesLoader();
        await MoveCompanion(CompanionSpawn, "to its spawn for the glows");
        _player.TryTeleportTo(_room.SpawnFor("player").PositionM + Vector3.Up * 0.008f);
        await Frames(3);
        TestNoRules();
        _host.LoadRules(RoomWorld.DefaultRulesId, ShippedRules.Version);
        Check(_host.Rules?.RulesId == "storybook_wild" && _host.RulesNotice.Length == 0, "the host loads the shipped rules pack");
        TestCapabilitiesFromRules();
        TestGlowWithNoLook();
        var look = new LookDirector { Name = "GlowTestLook" };
        AddChild(look);
        _host.LookHook = look;
        await Frames(1);
        TestGlowRefusals();
        await TestGlowSight();
        TestGlowCasters();
        await TestGlowLifecycle(look);
        TestGlowCaps(look);
        TestGlowTiers();
        await TestGlowUnload(look);
        TestAura(look);
        await TestGlowGrowth(look);
        TestExpertise();
        _host.UseRules(null, "The glow checks are over.");
        _host.LookHook = null;
        look.QueueFree();
        await Frames(2);
    }

    private static string PackText() => System.IO.File.ReadAllText(ProjectSettings.GlobalizePath(ShippedPack));

    /// <summary>The shipped pack with one text replacement, as bytes.</summary>
    private static byte[] Variant(string from, string to)
    {
        var text = PackText();
        if (!text.Contains(from, StringComparison.Ordinal)) throw new InvalidOperationException("the shipped pack no longer holds " + from);
        return Encoding.UTF8.GetBytes(text.Replace(from, to, StringComparison.Ordinal));
    }

    private static bool Refused(byte[] pack, string? expectedId = null, int? expectedVersion = null)
    {
        try
        {
            IslandRules.Parse(pack, expectedId, expectedVersion);
            return false;
        }
        catch (IslandRulesException) { return true; }
    }

    private static IslandRules Rules(byte[] pack) => IslandRules.Parse(pack);

    private void TestRulesLoader()
    {
        var shipped = IslandRules.Load(RoomWorld.DefaultRulesId, ShippedRules.Version);
        var glow = shipped.Ability("glow");
        var bubbles = shipped.Ability("bubbles");
        var fireworks = shipped.Ability("fireworks");
        var bloom = shipped.Ability("grow");
        var fire = shipped.Ability("fire");
        Check(shipped.RulesVersion == ShippedRules.Version && shipped.RulesVersion >= 4 && bubbles is { Category: "float", Primitive: "particles.float", ReachM: 2.0, AreaRadiusDefaultM: 0.5, AreaRadiusMaxM: 1.0, DurationDefaultS: 20, DurationMaxS: 60, MaxActive: 2 } &&
              fireworks is { Category: "burst", Primitive: "particles.burst", ReachM: 4.0, AreaRadiusDefaultM: 1.0, AreaRadiusMaxM: 2.0, DurationDefaultS: 4, DurationMaxS: 6, MaxActive: 3 } &&
              fireworks.Params["intensity"] == new AbilityParam(0.3, 1.0, 0.8) && bubbles.Params["intensity"] == new AbilityParam(0.2, 1.0, 0.6),
            "the game reads storybook_wild v4 or later: Bubbles (particles.float) and Fireworks (particles.burst) with their bounds, beside Glow");
        Check(bloom is { Category: "growth", Primitive: "growth.grow", DisplayName: "Grow", Tier: "auto_undo", ReachM: 2.0, AreaRadiusDefaultM: 0.5, AreaRadiusMaxM: 1.0, DurationDefaultS: 1, DurationMaxS: 600, MaxActive: 1 } &&
              fire is { Category: "fire", Primitive: "element.ignite", Tier: "preview_commit", ReachM: 2.0, AreaRadiusDefaultM: 1.5, AreaRadiusMaxM: 1.5, DurationDefaultS: 30, DurationMaxS: 60, MaxActive: 1 } &&
              bloom.Params["intensity"] == new AbilityParam(0.1, 0.5, 0.25) && fire.Params["intensity"] == new AbilityParam(0.2, 1.0, 0.6) &&
              bloom.CastBy.SetEquals(new[] { "player", "companion" }) && fire.CastBy.SetEquals(new[] { "player", "companion" }),
            "and the nature abilities: Grow (growth.grow, a quarter of a plant's size a cast, a 0.5 m circle to 1 m) and Fire (element.ignite, previewed before it commits) with their bounds and intensity");
        var v4 = IslandRules.Load("storybook_wild", 4).Ability("grow");
        Check(v4 is { Category: "growth", Primitive: "growth.seed", DisplayName: "Grow" } && IslandRules.Load("storybook_wild", 4).Ability("bloom") == null,
            "the pinned v4 stays byte-exact, and its Bloom (growth.seed) is read as Grow (decision 52)");
        Check(shipped.MagicWord == "magic" && shipped.Abilities.Count == 5 && glow != null && glow.Category == "light" && glow.Primitive == "light.emit" &&
            glow.CastBy.SetEquals(new[] { "player", "companion" }) && glow.Targets.SetEquals(new[] { "self", "point" }) && glow.Tier == "auto" && glow.ReachM == 2.0 &&
            glow.Params["intensity"] == new AbilityParam(0.2, 1.0, 0.6) && glow.AreaRadiusDefaultM == 0.6 && glow.AreaRadiusMaxM == 1.5 &&
            glow.DurationDefaultS == 300 && glow.DurationMaxS == 600 && glow.MaxActive == 5 && shipped.Sha256.Length == 64,
            "the rules loader reads the shipped storybook_wild pack: Glow and its bounds");
        // The schema's rules, each refused on its own.
        var schemaCases = new (string Label, byte[] Pack)[]
        {
            ("an unknown top-level field", Variant("\"magic_word\": \"magic\",", "\"magic_word\": \"magic\", \"cheats\": true,")),
            ("an unknown ability field", Variant("\"max_active\": 5", "\"max_active\": 5, \"owner\": \"companion:local\"")),
            ("a missing required field", Variant("\"tier\": \"auto\",", "")),
            ("a number given as a string", Variant("\"reach_m\": 2.0", "\"reach_m\": \"2.0\"")),
            ("a fractional max_active", Variant("\"max_active\": 5", "\"max_active\": 2.5")),
            ("an unknown tier", Variant("\"tier\": \"auto\"", "\"tier\": \"always\"")),
            ("an unknown status", Variant("\"status\": \"draft\"", "\"status\": \"live\"")),
            ("an unknown principal in cast_by", Variant("\"cast_by\": [\"player\", \"companion\"]", "\"cast_by\": [\"player\", \"guest\"]")),
            ("a repeated target", Variant("\"targets\": [\"self\", \"point\"]", "\"targets\": [\"self\", \"self\"]")),
            ("no targets", Variant("\"targets\": [\"self\", \"point\"]", "\"targets\": []")),
            ("an unknown primitive", Variant("\"primitive\": \"light.emit\"", "\"primitive\": \"fire.emit\"")),
            ("a capability that is not a token", Variant("\"capability\": \"glow\"", "\"capability\": \"Glow!\"")),
            ("a reserved param name", Variant("\"params\": { \"intensity\"", "\"params\": { \"owner\": { \"min\": 0, \"max\": 1, \"default\": 0 }, \"intensity\"")),
            ("a hidden character in a display name", Variant("\"display_name\": \"Glow\"", "\"display_name\": \"Gl\\u200bow\"")),
            ("a bidirectional override in the description", Variant("\"description\": \"Storybook nature magic", "\"description\": \"Storybook \\u202enature magic")),
            ("an empty magic word", Variant("\"magic_word\": \"magic\"", "\"magic_word\": \"\"")),
            ("an extension key without x_", Variant("\"extensions\": {}", "\"extensions\": { \"look\": 1 }")),
            ("a duplicate key", Variant("\"reach_m\": 2.0,", "\"reach_m\": 2.0, \"reach_m\": 4.0,")),
            ("a trailing comma", Variant("\"extensions\": {}", "\"extensions\": {},")),
            ("a wrong schema id", Variant("\"schema\": \"enfractal.island_rules\"", "\"schema\": \"enfractal.style_preset\"")),
            ("a reach over the schema's 10 m", Variant("\"reach_m\": 2.0", "\"reach_m\": 12.0")),
        };
        foreach (var (label, pack) in schemaCases) Check(Refused(pack), "the rules loader refuses " + label);
        // light.emit's outer limits (the schema's if/then) and the validator's extras.
        var limitCases = new (string Label, byte[] Pack)[]
        {
            ("an intensity above light.emit's 2.0", Variant("\"max\": 1.0, \"default\": 0.6", "\"max\": 2.5, \"default\": 0.6")),
            ("an intensity below light.emit's 0.05", Variant("\"min\": 0.2,", "\"min\": 0.01,")),
            ("a param light.emit does not take", Variant("\"params\": { \"intensity\"", "\"params\": { \"speed\": { \"min\": 0, \"max\": 1, \"default\": 0 }, \"intensity\"")),
            ("light.emit without intensity", Variant("\"params\": { \"intensity\": { \"min\": 0.2, \"max\": 1.0, \"default\": 0.6 } }", "\"params\": {}")),
            ("a category other than light for light.emit", Variant("\"category\": \"light\"", "\"category\": \"growth\"")),
            ("a reach over light.emit's 5 m", Variant("\"reach_m\": 2.0", "\"reach_m\": 6.0")),
            ("a radius over light.emit's 3 m", Variant("\"area_radius_max_m\": 1.5", "\"area_radius_max_m\": 4.0")),
            ("max_active over light.emit's 8", Variant("\"max_active\": 5", "\"max_active\": 9")),
            ("a min above its max", Variant("\"min\": 0.2, \"max\": 1.0, \"default\": 0.6", "\"min\": 0.9, \"max\": 0.5, \"default\": 0.6")),
            ("a default outside its range", Variant("\"default\": 0.6", "\"default\": 1.2")),
            ("a default radius above its maximum", Variant("\"area_radius_default_m\": 0.6", "\"area_radius_default_m\": 1.8")),
            ("a default duration above its maximum", Variant("\"duration_max_s\": 600", "\"duration_max_s\": 200")),
            ("a repeated capability", Variant("\"abilities\": [", "\"abilities\": [" + GlowAbility("glow", "light") + ",")),
            ("a repeated category", Variant("\"abilities\": [", "\"abilities\": [" + GlowAbility("shine", "light") + ",")),
        };
        foreach (var (label, pack) in limitCases) Check(Refused(pack), "the rules loader refuses " + label);
        Check(Refused(Encoding.UTF8.GetBytes(PackText()), "storybook_tame", ShippedRules.Version) && Refused(Encoding.UTF8.GetBytes(PackText()), "storybook_wild", 1) &&
            !Refused(Encoding.UTF8.GetBytes(PackText()), "storybook_wild", ShippedRules.Version), "a pack must name the id and version its path names");
        Check(!Refused(Variant("\"duration_default_s\": 300", "\"duration_default_s\": 600")) && !Refused(Variant("\"display_name\": \"Glow\"", "\"display_name\": \"Glow ✨\"")),
            "a default at its maximum and an emoji in a name are fine");
        var missing = false;
        try { IslandRules.Load("no_such_rules", 1); }
        catch (IslandRulesException) { missing = true; }
        Check(missing, "a pack that is not there is refused, not thrown past the loader");
    }

    private static string GlowAbility(string capability, string category) =>
        $"{{ \"capability\": \"{capability}\", \"category\": \"{category}\", \"primitive\": \"light.emit\", \"display_name\": \"Shine\", \"cast_by\": [\"player\"], \"tier\": \"auto\", " +
        "\"targets\": [\"point\"], \"reach_m\": 1.0, \"params\": { \"intensity\": { \"min\": 0.2, \"max\": 1.0, \"default\": 0.6 } }, \"area_radius_default_m\": 0.5, " +
        "\"area_radius_max_m\": 1.0, \"duration_default_s\": 60, \"duration_max_s\": 120, \"max_active\": 1 }";

    /// <summary>A broken or missing pack: no abilities, clear refusals, and stops still apply.</summary>
    private void TestNoRules()
    {
        _host.LoadRules("no_such_rules", 1);
        Check(_host.Rules == null && _host.RulesNotice.Contains("did not load", StringComparison.Ordinal), "a pack that fails to load leaves no rules and says why");
        var refused = Send(Glow("glow-none-1", SelfArgs()), Player);
        Check(Code(refused) == "unsupported_capability" && refused["error"]!["field_path"]!.GetValue<string>() == "$.args.capability", "with no rules, effect.start is refused: unsupported_capability");
        Check(Code(_host.PlayerEffect("glow")) == "unsupported_capability", "with no rules, the HUD's glow is refused too, without a crash");
        var listed = Query("capabilities.list", new JsonObject(), Companion);
        Check(Ok(listed) && listed["data"]!["items"]!.AsArray().Count == 0, "with no rules, capabilities.list is empty");
        Check(Ok(Send(Command("glow-none-stop", "effect.stop", new JsonObject { ["effect"] = "all" }), Companion)), "with no rules, effect.stop still applies");
        _host.UseRules(Rules(Encoding.UTF8.GetBytes(PackText())));
        _host.LoadRules("storybook_wild", 9);
        Check(_host.Rules == null && Code(Send(Glow("glow-none-2", SelfArgs()), Companion)) == "unsupported_capability", "a pack version that is not there replaces the old rules with none");
    }

    private void TestCapabilitiesFromRules()
    {
        var listed = Query("capabilities.list", new JsonObject(), Companion);
        var items = listed["data"]!["items"]!.AsArray();
        var glow = items.FirstOrDefault(i => i!["capability"]!.GetValue<string>() == "glow")?.AsObject();
        Check(Ok(listed) && items.Select(i => i!["capability"]!.GetValue<string>()).SequenceEqual(new[] { "bubbles", "fire", "fireworks", "glow", "grow" }) &&
              items.Select(i => i!["category"]!.GetValue<string>()).SequenceEqual(new[] { "float", "fire", "burst", "light", "growth" }),
            "capabilities.list answers every ability of the pack, by capability: bubbles (float), fire (fire), fireworks (burst), glow (light), grow (growth)");
        Check(items.Where(i => i!["capability"]!.GetValue<string>() is "grow" or "fire").All(i => i!["command_op"]!.GetValue<string>() == "nature.cast"),
            "Grow and Fire are cast through nature.cast");
        Check(Ok(listed) && items.Count == 5 && glow != null && glow["capability"]!.GetValue<string>() == "glow" && glow["category"]!.GetValue<string>() == "light" &&
            glow["params"]!["intensity"]!["min"]!.GetValue<double>() == 0.2 && glow["params"]!["intensity"]!["max"]!.GetValue<double>() == 1.0 &&
            glow["params"]!["intensity"]!.AsObject().Count == 2 && glow["area_radius_max_m"]!.GetValue<double>() == 1.5 && glow["duration_max_s"]!.GetValue<double>() == 600 &&
            glow.Count == 8 && glow["command_op"]!.GetValue<string>() == "effect.start" && glow["max_active"]!.GetValue<int>() == _host.EffectiveEffectLimit("glow") &&
            glow["reach_m"]!.GetValue<double>() == 2.0 && listed["data"]!["next_cursor"] == null,
            "capabilities.list answers the pack's Glow bounds, command path and current earned limit");
        Check(Query("capabilities.list", new JsonObject { ["category"] = "light" }, Companion)["data"]!["items"]!.AsArray().Count == 1 &&
            Query("capabilities.list", new JsonObject { ["category"] = "air" }, Companion)["data"]!["items"]!.AsArray().Count == 0, "capabilities.list filters by category");
        var paged = Query("capabilities.list", new JsonObject { ["limit"] = 1, ["cursor"] = "3" }, Player);
        var last = Query("capabilities.list", new JsonObject { ["limit"] = 1, ["cursor"] = "4" }, Player);
        Check(Ok(paged) && paged["data"]!["items"]!.AsArray().Single()!["capability"]!.GetValue<string>() == "glow" && paged["data"]!["next_cursor"]!.GetValue<string>() == "4" &&
            Ok(last) && last["data"]!["items"]!.AsArray().Single()!["capability"]!.GetValue<string>() == "grow" && last["data"]!["next_cursor"] == null &&
            Code(Query("capabilities.list", new JsonObject { ["cursor"] = "6" }, Player)) == "invalid_args" &&
            Code(Query("capabilities.list", new JsonObject { ["cursor"] = "+0" }, Player)) == "invalid_args", "capabilities.list honours its limit and refuses a cursor it did not give");
    }

    /// <summary>With no look attached (fixtures), a glow still starts and ends on the host's side.</summary>
    private void TestGlowWithNoLook()
    {
        var started = Send(Glow("glow-nolook", SelfArgs()), Player);
        var id = Effect(started);
        Check(Ok(started) && id.StartsWith("effect:", StringComparison.Ordinal) && _host.ActiveEffectIds.Contains(id), "with no look attached a glow starts: " + Code(started));
        var stopped = Send(Command("glow-nolook-stop", "effect.stop", new JsonObject { ["effect"] = id }), Player);
        Check(Ok(stopped) && stopped["data"]!["effects_stopped"]!.GetValue<int>() == 1 && _host.ActiveEffectIds.Count == 0, "and stops by its id");
    }

    private void TestGlowRefusals()
    {
        Check(Code(Send(Glow("glow-r1", SelfArgs(capability: "grow")), Companion)) == "unsupported_capability", "an ability the island lacks (v2 has no growth) is unsupported_capability");
        var unknown = Send(Glow("glow-r2", SelfArgs(new JsonObject { ["speed"] = 1 })), Companion);
        Check(Code(unknown) == "invalid_args" && unknown["error"]!["field_path"]!.GetValue<string>() == "$.args.params.speed", "a param the ability does not take is invalid_args");
        Check(Code(Send(Glow("glow-r3", SelfArgs(new JsonObject { ["owner"] = "player:local" })), Companion, contractValid: false)) == "request_invalid", "a reserved param name is refused");
        var bright = Send(Glow("glow-r4", SelfArgs(new JsonObject { ["intensity"] = 1.5 })), Companion);
        Check(Code(bright) == "invalid_args" && bright["error"]!["allowed"]!.AsArray().Select(v => v!.GetValue<double>()).SequenceEqual(new[] { 0.2, 1.0 }),
            "an intensity outside the ability's 0.2 to 1.0 is invalid_args, with the range");
        Check(Code(Send(Glow("glow-r5", SelfArgs(new JsonObject { ["intensity"] = "bright" })), Companion)) == "invalid_args", "a param that is not a number is invalid_args");
        var wide = Send(Glow("glow-r6", SelfArgs(radius: 2.0)), Companion);
        Check(Code(wide) == "invalid_args" && wide["error"]!["field_path"]!.GetValue<string>() == "$.args.area.radius_m", "a radius over the ability's 1.5 m is invalid_args");
        Check(Code(Send(Glow("glow-r7", SelfArgs(radius: 12.0)), Companion, contractValid: false)) == "request_invalid", "a radius over the contract's 10 m is request_invalid");
        Check(Code(Send(Glow("glow-r8", SelfArgs(duration: 700)), Companion, contractValid: false)) == "request_invalid", "a duration over the contract's 600 s is request_invalid");
        _host.UseRules(Rules(Variant("\"duration_max_s\": 600", "\"duration_max_s\": 400")));
        var longer = Send(Glow("glow-r9", SelfArgs(duration: 450)), Companion);
        Check(Code(longer) == "invalid_args" && longer["error"]!["field_path"]!.GetValue<string>() == "$.args.duration_s", "a duration over the ability's maximum is invalid_args");
        _host.UseRules(Rules(Variant("\"targets\": [\"self\", \"point\"]", "\"targets\": [\"point\"]")));
        Check(Code(Send(Glow("glow-r10", SelfArgs()), Companion)) == "invalid_args", "an ability without self cannot be cast on the Gubble");
        _host.UseRules(Rules(Variant("\"targets\": [\"self\", \"point\"]", "\"targets\": [\"self\"]")));
        Check(Code(Send(Glow("glow-r11", PointArgs(InTheOpen)), Companion)) == "invalid_args", "an ability without point cannot be cast at a spot");
        _host.LoadRules(RoomWorld.DefaultRulesId, ShippedRules.Version);
        // Targets: the companion's own avatar only, from anyone.
        var rug = SelfArgs();
        rug["targets"] = new JsonArray("obj:rug");
        Check(Code(Send(Glow("glow-r12", rug), Companion)) == "permission_denied", "the companion may not cast on another entity it sees");
        var player = SelfArgs();
        player["targets"] = new JsonArray(CommandHost.PlayerAvatar);
        Check(Code(Send(Glow("glow-r13", player), Companion)) == "permission_denied", "the companion may not cast on the player");
        var both = SelfArgs();
        both["targets"] = new JsonArray(CommandHost.CompanionAvatarId, "obj:rug");
        Check(Code(Send(Glow("glow-r14", both), Companion)) == "permission_denied", "the companion may not add another target to its own avatar");
        Check(Code(Send(Glow("glow-r15", rug), Player)) == "invalid_args" && Code(Send(Glow("glow-r16", player), Player)) == "invalid_args", "the player may not cast it on another entity either");
        var far = Send(Glow("glow-r17", PointArgs(new Vector3(-1.5f, 0, -1.0f))), Companion);
        Check(Code(far) == "out_of_bounds" && far["error"]!["retryable"]!.GetValue<bool>() && far["error"]!["allowed"]!.GetValue<double>() == 2.0, "a spot beyond the Gubble's 2 m reach is out_of_bounds");
        Check(Code(Send(Glow("glow-r18", PointArgs(new Vector3(0.6f, 0, 1.6f))), Player)) == "out_of_bounds", "a spot outside the room is out_of_bounds");
        Check(_host.ActiveEffectIds.Count == 0, "no refusal started anything");
    }

    /// <summary>A spot must be in sight of either avatar now (the team's sight), and the companion's own eyes when the team's sight is off.</summary>
    private async Task TestGlowSight()
    {
        await MoveCompanion(BehindTheBox, "behind the box, where the box hides the wedge spot");
        var hidden = Send(Glow("glow-s1", PointArgs(WedgeSpot)), Companion);
        Check(Code(hidden) == "target_not_found" && hidden["error"]!["field_path"]!.GetValue<string>() == "$.args.area.center_m", "a spot the companion cannot see is target_not_found");
        var players = Send(Glow("glow-s2", PointArgs(WedgeSpot)), Player);
        Check(Ok(players), "the player's own eyes count for the player's glow at that spot: " + Code(players));
        _host.SharedSight = true;
        var shared = Send(Glow("glow-s3", PointArgs(WedgeSpot)), Companion);
        _host.SharedSight = false;
        Check(Ok(shared), "with the team's sight, the companion may light a spot only the player sees: " + Code(shared));
        Check(Ok(Send(Command("glow-s-stop", "effect.stop", new JsonObject { ["effect"] = "all" }), Player)) && _host.ActiveEffectIds.Count == 0, "the player's effect.stop all ends them");
        await MoveCompanion(CompanionSpawn, "back to its spawn");
    }

    private void TestGlowCasters()
    {
        _host.UseRules(Rules(Variant("\"cast_by\": [\"player\", \"companion\"]", "\"cast_by\": [\"player\"]")));
        var denied = Send(Glow("glow-c1", SelfArgs()), Companion);
        Check(Code(denied) == "permission_denied" && denied["error"]!["field_path"]!.GetValue<string>() == "$.args.capability", "cast_by without the companion: the companion's glow is permission_denied");
        Check(Ok(Send(Glow("glow-c2", SelfArgs()), Player)), "and the player's is cast");
        _host.UseRules(Rules(Variant("\"cast_by\": [\"player\", \"companion\"]", "\"cast_by\": [\"companion\"]")));
        Check(Code(Send(Glow("glow-c3", SelfArgs()), Player)) == "permission_denied" && _host.ActiveEffectIds.Count == 0, "cast_by without the player: the player's is permission_denied (and new rules ended the old glow)");
        Check(Ok(Send(Glow("glow-c4", SelfArgs()), Companion)), "and the companion's is cast");
        _host.LoadRules(RoomWorld.DefaultRulesId, ShippedRules.Version);
    }

    private async Task TestGlowLifecycle(LookDirector look)
    {
        // Preview: what would start, nothing started.
        var preview = Glow("glow-l1", PointArgs(InTheOpen, new JsonObject { ["intensity"] = 0.9 }));
        preview["preview"] = true;
        var previewed = Send(preview, Companion);
        Check(Ok(previewed) && previewed["preview"]!.GetValue<bool>() && previewed["created"] == null && previewed["data"]!["target"]!.GetValue<string>() == "point" &&
            previewed["data"]!["params"]!["intensity"]!.GetValue<double>() == 0.9 && _host.ActiveEffectIds.Count == 0 && look.GlowCount == 0, "a preview says what would start and starts nothing");
        // Self: the light follows the Gubble; omitted params take the defaults.
        var self = Glow("glow-l2", SelfArgs(new JsonObject()));
        var started = Send(self, Companion);
        var id = Effect(started);
        Check(Ok(started) && started["transient"]!.GetValue<bool>() && started["created"]!.AsArray().Count == 1 && started["data"]!["target"]!.GetValue<string>() == "self" &&
            started["data"]!["params"]!["intensity"]!.GetValue<double>() == 0.6 && System.Text.RegularExpressions.Regex.IsMatch(id, @"\Aeffect:[a-z2-7]{26}\z"),
            "the companion's own glow: a transient receipt, an opaque effect id and the default intensity: " + Code(started));
        Check(look.HasGlow(id) && look.GlowLight(id) is { } light && Mathf.IsEqualApprox(light.OmniRange, 0.5f) && Mathf.IsEqualApprox(light.LightEnergy, GlowLook.Energy(0.6f)),
            "the look draws it: StartGlow with the radius and intensity");
        await Frames(2);
        Check(look.GlowRoot(id) is { } root && root.GlobalPosition.DistanceTo(_companion.GlobalPosition) < 0.2f, "the self glow follows the Gubble");
        var replay = Send(self, Companion);
        Check(Ok(replay) && replay["replayed"]!.GetValue<bool>() && Effect(replay) == id && _host.ActiveEffectIds.Count == 1 && look.GlowCount == 1, "a replay answers the same receipt and starts no second glow");
        var changed = Glow("glow-l2", SelfArgs(new JsonObject { ["intensity"] = 0.3 }));
        Check(Code(Send(changed, Companion)) == "action_id_conflict", "the same action id with other content is action_id_conflict");
        var lookup = Query("receipt.lookup", new JsonObject { ["action_id"] = "glow-l2" }, Companion);
        Check(lookup["data"]!["found"]!.GetValue<bool>() && lookup["data"]!["receipt"]!["created"]![0]!.GetValue<string>() == id, "receipt.lookup finds the glow's transient receipt");
        // Point: a wisp at the spot.
        var point = Send(Glow("glow-l3", PointArgs(InTheOpen, new JsonObject { ["intensity"] = 1.0 }, radius: 1.5, duration: 600)), Companion);
        var wisp = Effect(point);
        Check(Ok(point) && point["data"]!["target"]!.GetValue<string>() == "point" && look.HasGlow(wisp) && look.GlowRoot(wisp)!.GlobalPosition.DistanceTo(InTheOpen) < 0.1f,
            "a point glow at a spot in reach and in sight: a wisp there: " + Code(point));
        // effect.stop by id: the companion stops its own; an unknown id or another's stops nothing and still applies.
        var playerGlow = Effect(_host.PlayerEffect("glow", InTheOpen + new Vector3(0.1f, 0, 0)));
        var notMine = Send(Command("glow-l4", "effect.stop", new JsonObject { ["effect"] = playerGlow }), Companion);
        Check(Ok(notMine) && notMine["data"]!["effects_stopped"]!.GetValue<int>() == 0 && look.HasGlow(playerGlow), "the companion's stop of the player's glow stops nothing and still applies");
        var unknown = Send(Command("glow-l5", "effect.stop", new JsonObject { ["effect"] = "effect:" + new string('a', 26) }), Companion);
        Check(Ok(unknown) && unknown["data"]!["effects_stopped"]!.GetValue<int>() == 0, "a stop of an unknown effect applies and stops nothing");
        var byId = Send(Command("glow-l6", "effect.stop", new JsonObject { ["effect"] = wisp }), Companion);
        Check(Ok(byId) && byId["affected"]![0]!.GetValue<string>() == wisp && byId["data"]!["effects_stopped"]!.GetValue<int>() == 1 && !look.HasGlow(wisp) && look.HasGlow(id),
            "effect.stop by id ends that glow and releases its light, and only it");
        var again = Send(Command("glow-l6", "effect.stop", new JsonObject { ["effect"] = wisp }), Companion);
        Check(Ok(again) && !again["replayed"]!.GetValue<bool>(), "a stop under a used action id applies again and never conflicts");
        var mineAll = Send(Command("glow-l7", "effect.stop", new JsonObject { ["effect"] = "all" }), Companion);
        Check(Ok(mineAll) && !look.HasGlow(id) && look.HasGlow(playerGlow) && _host.ActiveEffectIds.SequenceEqual(new[] { playerGlow }), "the companion's effect.stop all ends only its own glows");
        var all = Send(Command("glow-l8", "effect.stop", new JsonObject { ["effect"] = "all" }), Player);
        Check(Ok(all) && look.GlowCount == 0 && _host.ActiveEffectIds.Count == 0, "the player's effect.stop all ends every glow");
        // PlayerEffect: the HUD's glow on the Gubble, with the pack's defaults.
        var hud = _host.PlayerEffect("glow");
        Check(Ok(hud) && hud["principal"]!.GetValue<string>() == Player && hud["data"]!["target"]!.GetValue<string>() == "self" && hud["data"]!["area"]!["radius_m"]!.GetValue<double>() == 0.6 &&
            hud["data"]!["duration_s"]!.GetValue<double>() == 300 && look.HasGlow(Effect(hud)), "PlayerEffect casts the Gubble's own glow with the pack's defaults");
        var hudPoint = _host.PlayerEffect("glow", InTheOpen);
        Check(Ok(hudPoint) && hudPoint["data"]!["target"]!.GetValue<string>() == "point" && look.HasGlow(Effect(hudPoint)), "PlayerEffect with a point lights that spot");
        Check(Code(_host.PlayerEffect("grow")) == "unsupported_capability", "PlayerEffect of an ability the island lacks answers why");
        // Fireworks go to the look's own call, never StartGlow; Bubbles is one bubble at a spot (Kernel/Bubble), no stream the look blows.
        var onTheGubble = _host.PlayerEffect("bubbles");
        var blown = _host.PlayerEffect("bubbles", InTheOpen);
        var bubble = blown["data"]?["bubble"]?.GetValue<string>() ?? "";
        var burst = Effect(_host.PlayerEffect("fireworks", InTheOpen));
        Check(Code(onTheGubble) == "invalid_args" && Ok(blown) && blown["data"]?["kind"]?.GetValue<string>() == "walk_in" && _host.ActiveBubbles.Count == 1 &&
              look.KindOf(bubble) == LookDirector.EffectKind.None && !look.HasGlow(bubble) && look.KindOf(burst) == LookDirector.EffectKind.Fireworks && !look.HasGlow(burst) &&
              look.EffectCount == 3,
            "PlayerEffect of bubbles is one bubble at a spot (none on the Gubble, no stream of small bubbles); fireworks at a spot start the look's fireworks, not a glow");
        Check(BubbleLaws.CarryDurationDefaultS == 8f && blown["data"]?["duration_s"]?.GetValue<double>() == 8 &&
              blown["data"]?["expires_utc"]?.GetValue<string>() == KernelJson.Utc(_now + TimeSpan.FromSeconds(8)),
            "the Bubbles key's bubble lasts 8 seconds (decision 95; it was 20), not the pack's duration_default_s: " + Canonical(blown["data"]));
        // goal.stop of the Gubble ends its effects too, of every kind (the contract: an actor's goals and its effects), and pops its bubble.
        var goalStop = _host.PlayerGoal("stop");
        Check(Ok(goalStop) && goalStop["data"]!["effects_stopped"]!.GetValue<int>() == 4 && look.EffectCount == 0 && _host.ActiveEffectIds.Count == 0 && _host.ActiveBubbles.Count == 0,
            "the player's goal.stop of the Gubble ends its effects of every kind and pops the bubble: glows, the bubble and fireworks");
        _companion.Follow();
        // Expiry: an effect ends at its duration, and its light with it.
        var brief = Effect(Send(Glow("glow-l9", SelfArgs(duration: 2)), Companion));
        var longer = Effect(Send(Glow("glow-l10", PointArgs(InTheOpen, duration: 30)), Companion));
        _now += TimeSpan.FromSeconds(3);
        await Frames(2);
        Check(!look.HasGlow(brief) && look.HasGlow(longer) && _host.ActiveEffectIds.SequenceEqual(new[] { longer }), "an effect ends at its duration and the look releases it");
        _now += TimeSpan.FromSeconds(30);
        await Frames(2);
        Check(look.GlowCount == 0 && _host.ActiveEffectIds.Count == 0, "and so does the longer one, at its own");
    }

    private void TestGlowCaps(LookDirector look)
    {
        _host.ConfigureGlowProgress(new GrowthTestStore());
        var ids = Enumerable.Range(0, 3).Select(i => Effect(Send(Glow($"glow-cap-{i}", PointArgs(InTheOpen + new Vector3(0.05f * i, 0, 0))), Companion))).ToList();
        Check(ids.All(i => i.Length > 0) && look.GlowCount == 3, "three glows run at once (the initial learned allowance)");
        var fourth = Send(Glow("glow-cap-3", SelfArgs()), Player);
        Check(Code(fourth) == "budget_exceeded" && fourth["error"]!["allowed"]!.GetValue<int>() == 3 && look.GlowCount == 3, "a fourth is budget_exceeded, from the player too");
        Check(Ok(Send(Command("glow-cap-stop", "effect.stop", new JsonObject { ["effect"] = "all" }), Player)), "they stop");
        _host.EffectLimit = 2;
        Effect(Send(Glow("glow-cap-4", SelfArgs()), Companion));
        Effect(Send(Glow("glow-cap-5", PointArgs(InTheOpen)), Companion));
        var capped = Send(Glow("glow-cap-6", PointArgs(InTheOpen)), Player);
        Check(Code(capped) == "budget_exceeded" && capped["error"]!["allowed"]!.GetValue<int>() == 2 && _host.ActiveEffectIds.Count == 2, "past the engine's cap on effects per room, budget_exceeded");
        _host.EffectLimit = CommandHost.MaxActiveEffects;
        Check(CommandHost.MaxActiveEffects == 8 && CommandHost.MaxActiveEffects <= GlowLook.MaxGlows, "the engine's cap is 8, no more than the look draws");
        Send(Command("glow-cap-stop-2", "effect.stop", new JsonObject { ["effect"] = "all" }), Player);
        // The look refuses (it already draws its most): the command is refused and nothing is left half-started.
        for (var i = 0; i < GlowLook.MaxGlows; i++) look.StartGlow("other-" + i, null, InTheOpen, 0.3f, 0.5f);
        var preview = Glow("glow-cap-7", PointArgs(InTheOpen));
        preview["preview"] = true;
        Check(Code(Send(preview, Companion)) == "budget_exceeded", "a preview tells when the look cannot show another light");
        var refused = Send(Glow("glow-cap-8", PointArgs(InTheOpen)), Companion);
        Check(Code(refused) == "budget_exceeded" && refused["error"]!["retryable"]!.GetValue<bool>() && _host.ActiveEffectIds.Count == 0 && look.GlowCount == GlowLook.MaxGlows,
            "when the look's StartGlow refuses, the command is refused and nothing starts");
        Check(Code(Query("receipt.lookup", new JsonObject { ["action_id"] = "glow-cap-8" }, Companion)) == null &&
            !Query("receipt.lookup", new JsonObject { ["action_id"] = "glow-cap-8" }, Companion)["data"]!["found"]!.GetValue<bool>(), "and keeps no receipt");
        look.StopAllGlows();
    }

    /// <summary>A keyed_yes ability waits for the player's click when the companion asks; the player's own runs at once.</summary>
    private void TestGlowTiers()
    {
        _host.UseRules(Rules(Variant("\"tier\": \"auto\"", "\"tier\": \"keyed_yes\"")));
        var held = Send(Glow("glow-t1", SelfArgs()), Companion);
        var request = held["approval_needed"]?["request_id"]?.GetValue<string>() ?? "";
        Check(Code(held) == "approval_required" && request.Length > 0 && _host.ActiveEffectIds.Count == 0, "a keyed_yes glow from the companion is held for the player");
        var approved = _host.Approve(request);
        Check(Ok(approved) && approved["approved_by"]?.GetValue<string>() == Player && approved["principal"]!.GetValue<string>() == Companion && _host.ActiveEffectIds.Count == 1,
            "the player's click starts it, under the companion with approved_by");
        Check(Code(Send(Glow("glow-t2", SelfArgs(new JsonObject { ["intensity"] = 1.4 })), Companion)) == "invalid_args", "a held ability is checked before it is held");
        Check(Ok(Send(Glow("glow-t3", PointArgs(InTheOpen)), Player)), "the player's own keyed_yes glow runs at once");
        _host.LoadRules(RoomWorld.DefaultRulesId, ShippedRules.Version);
        Check(_host.ActiveEffectIds.Count == 0, "new rules end the glows started under the old");
    }

    /// <summary>A host that unloads ends its glows (the look's StopAllGlows).</summary>
    private async Task TestGlowUnload(LookDirector look)
    {
        var host = CommandHost.Create(this, _room, _player, _companion, null, $"{TestRoot}/glow/{_room.ManifestSha256[..16]}/inventions.json");
        host.Clock = () => _now;
        await Frames(2);
        host.LookHook = look;
        host.LoadRules(RoomWorld.DefaultRulesId, ShippedRules.Version);
        var started = host.PlayerEffect("glow");
        Check(Ok(started) && look.GlowCount == 1, "a second host lights a glow: " + Code(started));
        host.QueueFree();
        await Frames(2);
        Check(look.GlowCount == 0, "the host unloading ends every glow");
    }

    private void TestAura(LookDirector look)
    {
        var blue = new Color(0.2f, 0.4f, 1.0f);
        _companion.SetAuraColor(blue);
        Check(_companion.AuraColor == blue && _companion.HasMeta(LookDirector.AuraColorMeta) && _companion.GetMeta(LookDirector.AuraColorMeta).AsColor() == blue, "SetAuraColor sets the aura and the look's meta");
        var id = Effect(_host.PlayerEffect("glow"));
        Check(look.GlowLight(id) is { } light && light.LightColor.IsEqualApprox(GlowLook.LightColor(blue)), "the glow takes the aura colour");
        _companion.SetAuraColor(null);
        Check(_companion.AuraColor == null && !_companion.HasMeta(LookDirector.AuraColorMeta), "SetAuraColor(null) clears the aura and its meta");
        Send(Command("glow-aura-stop", "effect.stop", new JsonObject { ["effect"] = "all" }), Player);
    }

    private JsonObject Glow(string actionId, JsonObject args) => Command(actionId, "effect.start", (JsonObject)args.DeepClone());

    private static JsonObject SelfArgs(JsonObject? parameters = null, string capability = "glow", double radius = 0.5, double duration = 60)
    {
        var args = PointArgs(Vector3.Zero, parameters, capability, radius, duration);
        args["targets"] = new JsonArray(CommandHost.CompanionAvatarId);
        return args;
    }

    private static JsonObject PointArgs(Vector3 centre, JsonObject? parameters = null, string capability = "glow", double radius = 0.5, double duration = 60) => new()
    {
        ["capability"] = capability, ["params"] = parameters ?? new JsonObject { ["intensity"] = 0.6 },
        ["area"] = new JsonObject { ["center_m"] = new JsonArray(centre.X, centre.Y, centre.Z), ["radius_m"] = radius }, ["duration_s"] = duration,
    };

    private static string Effect(JsonObject result) => result["created"]?[0]?.GetValue<string>() ?? "";
}
