using Godot;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Look;
using GarageGame.Native.Room;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// Glow's growth (docs/magic/README.md "Growth and touches"): the progression state and its store, the discoveries and
/// hints the host awards only for real casts, the earned light allowance under the shipped pack and an older one, and the
/// Glow commands that learn from success without awarding anything for a refused or replayed cast.
/// </summary>
public partial class CommandHostTest
{
    /// <summary>Host observations earn progress only after a real cast and a successful save.</summary>
    private async Task TestGlowGrowth(LookDirector look)
    {
        TestGlowProgressState();
        TestGlowProgressStore();
        TestGlowProgressCommands(look);
        TestGlowEarnedLimits(look);
        TestGlowVariantsAndTouches(look);
        await TestGlowAudioLifecycle();
        await TestGlowObservedGeometry(look);
        _host.ConfigureGlowProgress(new GrowthTestStore());
        _host.LoadRules(RoomWorld.DefaultRulesId, ShippedRules.Version);
    }

    private void TestGlowProgressState()
    {
        var fresh = GlowProgress.Fresh;
        var lantern = fresh.AfterCast(true);
        var repeated = lantern.AfterCast(true);
        Check(fresh.SuccessfulCasts == 0 && fresh.Discoveries.Count == 0 && fresh.ActiveLightAllowance == 3 &&
            lantern.SuccessfulCasts == 1 && lantern.HasDiscovery("lantern") && repeated.SuccessfulCasts == 2 &&
            repeated.Discoveries.SequenceEqual(new[] { "lantern" }) && repeated.ActiveLightAllowance == 3,
            "Glow starts at three lights; a real Lantern observation records one discovery without changing the previous state or farming duplicates");
        var four = GlowProgress.Restore(2, new[] { "torch", "lantern" }, Array.Empty<string>());
        var five = GrowthComplete();
        Check(four.ActiveLightAllowance == 4 && four.Discoveries.SequenceEqual(new[] { "lantern", "torch" }) && five.ActiveLightAllowance == 5,
            "two distinct restored discoveries allow four lights, and all five allow five");
        var points = fresh;
        for (var i = 0; i < GlowProgress.HintAfterCasts - 1; i++) points = points.AfterCast(false);
        Check(points.Discoveries.Count == 0 && points.DeliveredHints.Count == 0, "ordinary point Glow neither invents a future discovery nor nudges early");
        points = points.AfterCast(false);
        Check(points.SuccessfulCasts == 10 && points.DeliveredHints.SequenceEqual(new[] { GlowProgress.LanternHint }) &&
            points.AfterCast(false).DeliveredHints.Count == 1 && points.AfterCast(true).HasDiscovery("lantern") &&
            GlowProgress.Restore(9, new[] { "lantern" }, Array.Empty<string>()).AfterCast(false).DeliveredHints.SequenceEqual(new[] { "torch_high_place" }),
            "ten successful uses nudge one missing discovery once, and later discovery remains valid");
        Check(GrowthRefused(() => GlowProgress.Restore(-1, Array.Empty<string>(), Array.Empty<string>())) &&
            GrowthRefused(() => GlowProgress.Restore(GlowProgress.MaxSuccessfulCasts + 1, Array.Empty<string>(), Array.Empty<string>())) &&
            GrowthRefused(() => GlowProgress.Restore(0, new[] { "lantern" }, Array.Empty<string>())) &&
            GrowthRefused(() => GlowProgress.Restore(2, new[] { "lantern", "lantern" }, Array.Empty<string>())) &&
            GrowthRefused(() => GlowProgress.Restore(2, new[] { "make-me-powerful" }, Array.Empty<string>())) &&
            GrowthRefused(() => GlowProgress.Restore(9, Array.Empty<string>(), new[] { GlowProgress.LanternHint })),
            "progress restoration refuses impossible counts, duplicate or unknown discoveries, and premature hints");
        Check(GlowProgress.Restore(GlowProgress.MaxSuccessfulCasts, new[] { "lantern" }, Array.Empty<string>()).AfterCast(true).SuccessfulCasts == GlowProgress.MaxSuccessfulCasts,
            "the bounded use counter saturates without wrapping or resetting progress");
    }

    private void TestGlowProgressStore()
    {
        var path = ProjectSettings.GlobalizePath($"{TestRoot}/growth/{Guid.NewGuid():N}/progress.json");
        var store = new LocalGlowProgressStore(path);
        Check(store.Load().SuccessfulCasts == 0 && !File.Exists(path), "a missing progression profile loads fresh without writing a save");
        var state = GlowProgress.Fresh;
        for (var i = 0; i < 10; i++) state = state.AfterCast(false);
        state = state.AfterCast(true);
        store.Save(state);
        var bytes = File.ReadAllBytes(path);
        var loaded = new LocalGlowProgressStore(path).Load();
        Check(loaded.SuccessfulCasts == 11 && loaded.HasDiscovery("lantern") && loaded.DeliveredHints.SequenceEqual(new[] { GlowProgress.LanternHint }) &&
            bytes[0] == (byte)'{' && bytes[^1] == (byte)'\n' && !bytes.Contains((byte)'\r'),
            "the local profile roundtrips uses, discoveries and delivered hints as UTF-8 without BOM and with LF");
        var validText = Encoding.UTF8.GetString(bytes);
        var malformed = new[]
        {
            "{", validText.Replace("\"version\":3", "\"version\":99", StringComparison.Ordinal),
            validText.Replace("\"lantern\"", "\"wish-granted\"", StringComparison.Ordinal),
            validText.Replace("\"successful_casts\":11", "\"successful_casts\":1000001", StringComparison.Ordinal),
            validText.Replace("\"successful_casts\":11", "\"successful_casts\":11,\"successful_casts\":11", StringComparison.Ordinal),
            validText.Replace("\"schema\":", "\"unexpected\":true,\"schema\":", StringComparison.Ordinal),
        };
        Check(malformed.All(text => GrowthRefused(() => LocalGlowProgressStore.Parse(Encoding.UTF8.GetBytes(text)))) &&
            GrowthRefused(() => LocalGlowProgressStore.Parse(new byte[LocalGlowProgressStore.MaxSaveBytes + 1])) &&
            GrowthRefused(() => LocalGlowProgressStore.Parse(new byte[] { (byte)'{', 0xff, (byte)'}' })),
            "progress saves reject malformed JSON, newer versions, unknown IDs, excessive counts, duplicate or extra fields and oversized input");
        // Versions 1 and 2 (one file per mode, before teams) still read, for the teams packet's migration: Glow's record at the top.
        var legacy = JsonNode.Parse(validText)!["abilities"]!["glow"]!.DeepClone().AsObject();
        legacy["schema"] = "enfractal.glow_progress"; legacy["version"] = 1;
        var migrated = LocalGlowProgressStore.Parse(Encoding.UTF8.GetBytes(legacy.ToJsonString()));
        var configured = migrated.WithConfiguration("frost", 0.25, true);
        var roundtrip = LocalGlowProgressStore.Parse(LocalGlowProgressStore.Serialize(configured));
        Check(migrated.HasDiscovery("lantern") && migrated.SelectedTouch == "plain" && roundtrip.SelectedTouch == "frost" &&
            roundtrip.SoundMuted && Math.Abs(roundtrip.SoundVolume - 0.25) < 0.0001,
            "v1 growth profiles migrate without losing discoveries, and v2 retains touch and sound preferences");
        using (var lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, System.IO.FileAccess.ReadWrite, FileShare.None))
            Check(GrowthRefused(() => store.Save(state.AfterCast(false))) && File.ReadAllBytes(path).SequenceEqual(bytes),
                "an active profile writer lease prevents a competing write without damaging the saved progress");
        store.Save(state.AfterCast(false));
        Check(new LocalGlowProgressStore(path).Load().SuccessfulCasts == 12,
            "saving recovers after the other writer releases its profile lease");
        var changed = new LocalGlowProgressStore(path);
        changed.Load();
        store.Save(state.AfterCast(false).AfterCast(false));
        var latest = File.ReadAllBytes(path);
        Check(GrowthRefused(() => changed.Save(state)) && changed.IsReadOnly && File.ReadAllBytes(path).SequenceEqual(latest),
            "a stale store cannot overwrite newer progression saved by another session");
        File.WriteAllText(path, "{\"version\":999}", new UTF8Encoding(false));
        var corruptBytes = File.ReadAllBytes(path);
        var corrupt = new LocalGlowProgressStore(path);
        Check(GrowthRefused(() => corrupt.Load()) && corrupt.IsReadOnly && GrowthRefused(() => corrupt.Save(GlowProgress.Fresh)) &&
            File.ReadAllBytes(path).SequenceEqual(corruptBytes), "a corrupt or unsupported profile is kept byte-for-byte and is never replaced with fresh progress");
    }

    private void TestGlowProgressCommands(LookDirector look)
    {
        _host.LoadRules(RoomWorld.DefaultRulesId, ShippedRules.Version);
        var store = new GrowthTestStore();
        _host.ConfigureGlowProgress(store);
        var preview = Glow("growth-preview", SelfArgs());
        preview["preview"] = true;
        Check(Ok(Send(preview, Companion)) && _host.GlowProgress.SuccessfulCasts == 0 && store.Saves == 0 && look.GlowCount == 0,
            "previewing Lantern neither casts, learns nor saves progression");
        var refused = Send(Glow("growth-invalid", SelfArgs(new JsonObject { ["intensity"] = 1.4 })), Player);
        Check(Code(refused) == "invalid_args" && store.Saves == 0 && _host.GlowProgress.Discoveries.Count == 0,
            "a refused Glow command earns no progress");
        for (var i = 0; i < GlowLook.MaxGlows; i++) look.StartGlow("growth-other-" + i, null, InTheOpen, 0.3f, 0.5f);
        var lookRefused = Send(Glow("growth-look-full", SelfArgs()), Companion);
        Check(Code(lookRefused) == "budget_exceeded" && _host.ActiveEffectIds.Count == 0 && store.Saves == 0 && _host.GlowProgress.SuccessfulCasts == 0,
            "a renderer refusal cannot award a discovery for an effect it never showed");
        look.StopAllGlows();
        var firstCommand = Glow("growth-player-lantern", SelfArgs());
        var first = Send(firstCommand, Player);
        var replay = Send(firstCommand, Player);
        Check(Ok(first) && Ok(replay) && replay["replayed"]!.GetValue<bool>() && _host.GlowProgress.SuccessfulCasts == 1 &&
            _host.GlowProgress.HasDiscovery("lantern") && store.Saves == 1 && look.GlowCount == 1,
            "the player's successful Lantern learns and saves once, and replay never earns twice");
        var companion = Send(Glow("growth-companion-lantern", SelfArgs()), Companion);
        Check(Ok(companion) && _host.GlowProgress.SuccessfulCasts == 2 && _host.GlowProgress.Discoveries.Count == 1 && store.Saves == 2,
            "a connected companion's successful cast advances the same team progress without a second Lantern discovery");
        GrowthStop("after-lanterns");
        Check(Ok(Send(Glow("growth-point", PointArgs(InTheOpen)), Companion)) && _host.GlowProgress.SuccessfulCasts == 3 &&
            _host.GlowProgress.Discoveries.SequenceEqual(new[] { "lantern" }), "a point cast counts as practice without awarding an unimplemented Glow behaviour");
        Check(Ok(Send(Glow("growth-bubbles", PointArgs(InTheOpen, capability: "bubbles")), Player)) && _host.GlowProgress.SuccessfulCasts == 3 && store.Saves == 3,
            "a successful Bubbles cast does not count as Glow practice");
        Send(Command("growth-bubbles-pop", "effect.stop", new JsonObject { ["effect"] = "all" }), Player);
        _host.UseRules(Rules(Variant("\"tier\": \"auto\"", "\"tier\": \"keyed_yes\"")));
        var held = Send(Glow("growth-held", SelfArgs()), Companion);
        var approval = held["approval_needed"]?["request_id"]?.GetValue<string>() ?? "";
        Check(Code(held) == "approval_required" && _host.GlowProgress.SuccessfulCasts == 3 && store.Saves == 3 && look.GlowCount == 0,
            "a held Glow request earns nothing before the player approves it");
        var approved = _host.Approve(approval);
        Check(Ok(approved) && _host.GlowProgress.SuccessfulCasts == 4 && store.Saves == 4,
            "an approved Glow request earns progress when it actually starts");
        _host.LoadRules(RoomWorld.DefaultRulesId, ShippedRules.Version);
        var saveFailure = new GrowthTestStore { FailSave = true };
        _host.ConfigureGlowProgress(saveFailure);
        var retryCommand = Glow("growth-save-retry", SelfArgs());
        var failed = Send(retryCommand, Player);
        var receipt = Query("receipt.lookup", new JsonObject { ["action_id"] = "growth-save-retry" }, Player);
        Check(Code(failed) == "not_ready" && _host.GlowProgress.SuccessfulCasts == 0 && _host.ActiveEffectIds.Count == 0 && look.GlowCount == 0 &&
            !receipt["data"]!["found"]!.GetValue<bool>() && _host.GlowProgressNotice.Length > 0,
            "a progression save failure rolls back the staged light, progress and receipt, and reports the problem");
        saveFailure.FailSave = false;
        Check(Ok(Send(retryCommand, Player)) && _host.GlowProgress.SuccessfulCasts == 1 && look.GlowCount == 1 && _host.GlowProgressNotice.Length == 0,
            "retrying the same command after storage recovers starts and earns exactly once");
        GrowthStop("after-retry");
        _host.ConfigureGlowProgress(new GrowthTestStore { FailLoad = true });
        Check(_host.GlowProgressNotice.Length > 0 && Code(Send(Glow("growth-load-failed", SelfArgs()), Player)) == "not_ready" &&
            _host.ActiveEffectIds.Count == 0 && look.GlowCount == 0,
            "a profile that cannot load produces a clear notice and cannot be overwritten by a cast");
    }

    private void TestGlowEarnedLimits(LookDirector look)
    {
        // Restored fixtures exercise the future thresholds; no live command awards these unimplemented variants.
        var levels = new[]
        {
            GlowProgress.Fresh,
            GlowProgress.Restore(2, new[] { "lantern", "torch" }, Array.Empty<string>()),
            GrowthComplete(),
        };
        foreach (var level in levels)
        {
            _host.LoadRules(RoomWorld.DefaultRulesId, ShippedRules.Version);
            _host.ConfigureGlowProgress(new GrowthTestStore(level));
            var limit = level.ActiveLightAllowance;
            Check(_host.EffectiveEffectLimit("glow") == limit, $"the host exposes the earned {limit}-light allowance under v4");
            for (var i = 0; i < limit; i++)
                Check(Ok(Send(Glow($"growth-limit-{limit}-{i}", SelfArgs()), i % 2 == 0 ? Player : Companion)), $"earned allowance {limit} admits light {i + 1}");
            var over = Send(Glow($"growth-limit-{limit}-over", SelfArgs()), Companion);
            Check(Code(over) == "budget_exceeded" && over["error"]!["allowed"]!.GetValue<int>() == limit && look.GlowCount == limit &&
                _host.GlowProgress.SuccessfulCasts == level.SuccessfulCasts + limit,
                $"the earned {limit}-light cap applies across both principals and excess attempts earn nothing");
        }
        _host.LoadRules("storybook_wild", 2);
        Check(_host.EffectiveEffectLimit("glow") == 3, "a fully learned Gubble still obeys the older v2 island's three-light rule");
        for (var i = 0; i < 3; i++) Send(Glow($"growth-v2-{i}", SelfArgs()), Player);
        Check(Code(Send(Glow("growth-v2-over", SelfArgs()), Companion)) == "budget_exceeded" && look.GlowCount == 3,
            "progression never raises the pinned v2 island limit");
        _host.LoadRules(RoomWorld.DefaultRulesId, ShippedRules.Version);
        for (var i = 0; i < 5; i++) Send(Glow($"growth-global-glow-{i}", SelfArgs()), Player);
        // One bubble at a time (Kernel/Bubble): it counts toward the room's cap, though the look draws it as no effect.
        Send(Glow("growth-global-bubbles", PointArgs(InTheOpen, capability: "bubbles")), Player);
        for (var i = 0; i < 2; i++) Send(Glow($"growth-global-fireworks-{i}", SelfArgs(capability: "fireworks", duration: 4)), Player);
        var ninth = Send(Glow("growth-global-over", SelfArgs(capability: "fireworks", duration: 4)), Companion);
        Check(_host.ActiveEffectIds.Count == 7 && _host.ActiveBubbles.Count == 1 && look.EffectCount == 7 && Code(ninth) == "budget_exceeded" &&
              ninth["error"]!["allowed"]!.GetValue<int>() == 8 && ninth["error"]!["actual"]!.GetValue<int>() == 8,
            "five earned lights share the unchanged eight-effect room cap with the Bubble and Fireworks, the bubble counted");
        GrowthStop("finished");
    }

    private void GrowthStop(string suffix) => Send(Command("growth-stop-" + suffix, "effect.stop", new JsonObject { ["effect"] = "all" }), Player);

    private async Task TestGlowObservedGeometry(LookDirector look)
    {
        GrowthStop("before-geometry");
        _host.LoadRules(RoomWorld.DefaultRulesId, ShippedRules.Version);
        _host.ConfigureGlowProgress(new GrowthTestStore());
        var original = _companion.GlobalPosition;
        var support = new StaticBody3D { Name = "GrowthSupportFixture", CollisionLayer = RoomBuilder.WorldLayer, CollisionMask = 0,
            Position = InTheOpen + Vector3.Up * 0.35f };
        support.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(0.3f, 0.05f, 0.3f) } });
        AddChild(support);
        var waterPoint = InTheOpen + new Vector3(0.42f, 0.07f, 0);
        var water = RoomWater.CreateCollider(new[]
        {
            ((Shape3D)new BoxShape3D { Size = new Vector3(0.3f, 0.001f, 0.3f) }, Transform3D.Identity),
        });
        water.Position = waterPoint;
        AddChild(water);
        var ceiling = GetNode<StaticBody3D>("Room/Shell/ceiling");
        var ceilingLayer = ceiling.CollisionLayer;
        try
        {
            await Frames(3);
            var top = InTheOpen + Vector3.Up * 0.375f;
            Check(_companion.TryTeleportTo(top + Vector3.Up * 0.008f), "the Glow geometry fixture puts its Gubble above a real elevated support");
            await Frames(2);
            var torch = Send(Glow("growth-real-torch", PointArgs(top + Vector3.Right * 0.07f)), Player);
            Check(Ok(torch) && torch["data"]?["variant"]?.GetValue<string>() == "torch" && _host.GlowProgress.HasDiscovery("torch") &&
                torch["data"]!["params"]!["intensity"]!.GetValue<double>() > 0.6 && look.GlowVariant(Effect(torch)) == "torch",
                "a physical high support actually earns Torch and a brighter bounded light");
            GrowthStop("after-real-torch");
            _companion.TryTeleportTo(InTheOpen + Vector3.Left * 0.35f + Vector3.Up * 0.008f);
            await Frames(3);
            var bio = Send(Glow("growth-real-water", PointArgs(waterPoint)), Player);
            Check(Ok(bio) && bio["data"]?["variant"]?.GetValue<string>() == "bioluminesce" &&
                _host.GlowProgress.HasDiscovery("bioluminesce") && look.GlowVariant(Effect(bio)) == "bioluminesce",
                "a real query-only water surface earns Bioluminesce through the same cast command");
            GrowthStop("after-real-water");
            var skyArgs = PointArgs(waterPoint); skyArgs["variant"] = "skylight";
            Check(Code(Send(Glow("growth-covered-sky", skyArgs.DeepClone().AsObject()), Player)) == "invalid_args" &&
                !_host.GlowProgress.HasDiscovery("skylight"), "a roof blocks a requested Skylight and its discovery");
            ceiling.CollisionLayer = 0;
            await Frames(2);
            var sky = Send(Glow("growth-real-sky", skyArgs), Player);
            Check(Ok(sky) && _host.GlowProgress.HasDiscovery("skylight") && look.GlowVariant(Effect(sky)) == "skylight",
                "the same place with a real open sky earns its high light");
            GrowthStop("after-real-sky");
            Send(Glow("growth-geometry-lantern", SelfArgs()), Player);
            GrowthStop("after-geometry-lantern");
            for (var index = 0; index < 3; index++)
            {
                var args = PointArgs(InTheOpen + new Vector3(index * 0.05f, 0, -0.3f)); args["variant"] = "plain";
                Send(Glow("growth-geometry-stars-" + index, args), Player);
            }
            Check(_host.GlowProgress.Discoveries.Count == 5 && _host.GlowProgress.ActiveLightAllowance == 5 && look.GlowConnectionCount == 3,
                "all five discoveries and the fifth light can be earned from live host-verified casts without restoring a learned fixture");
        }
        finally
        {
            GrowthStop("geometry-finished");
            ceiling.CollisionLayer = ceilingLayer;
            support.Free(); water.Free();
            _companion.TryTeleportTo(original);
        }
        await Frames(3);
    }

    private void TestGlowVariantsAndTouches(LookDirector look)
    {
        _host.LoadRules(RoomWorld.DefaultRulesId, ShippedRules.Version);
        var store = new GrowthTestStore();
        _host.ConfigureGlowProgress(store);
        Check(Code(Send(Command("growth-unknown-settings", "glow.configure", new JsonObject { ["cheat"] = true }), Player, contractValid: false)) == "field_unknown" &&
            Code(Send(Command("growth-missing-adjust-id", "effect.adjust", new JsonObject { ["connections"] = new JsonArray() }), Player, contractValid: false)) == "request_invalid" &&
            Code(Send(Command("growth-empty-settings", "glow.configure", new JsonObject()), Player, contractValid: false)) == "request_invalid",
            "specialized Glow commands refuse unknown settings, missing effect IDs and empty changes before touching state");
        for (var index = 0; index < 3; index++)
        {
            var repeated = PointArgs(InTheOpen); repeated["variant"] = "plain";
            Send(Glow("growth-same-point-" + index, repeated), Player);
        }
        Check(!_host.GlowProgress.HasDiscovery("constellation") && look.GlowConnectionCount == 0,
            "repeating Glow at one point cannot farm a constellation discovery");
        GrowthStop("same-point");
        var ids = new string[3];
        for (var index = 0; index < 3; index++)
        {
            var args = PointArgs(InTheOpen + Vector3.Right * (index * 0.05f)); args["variant"] = "plain";
            ids[index] = Effect(Send(Glow("growth-constellation-" + index, args), Player));
        }
        Check(ids.All(id => id.Length > 0) && _host.GlowProgress.HasDiscovery("constellation") &&
            _host.GlowProgress.Discoveries.Count == 1 && look.GlowConnectionCount == 3,
            "three nearby real point lights discover a connected constellation without needing the fifth light");
        var adjust = new JsonObject { ["effect"] = ids[0], ["center_m"] = KernelJson.Vector(InTheOpen + Vector3.Right * 0.02f) };
        Check(Code(Send(Command("growth-adjust-locked", "effect.adjust", adjust), Player)) == "permission_denied",
            "moving constellation points waits for the learned fifth light");
        GrowthStop("constellation");
        Check(Ok(Send(Glow("growth-lantern-after-stars", SelfArgs()), Player)) && _host.GlowProgress.ActiveLightAllowance == 4,
            "Lantern and the basic constellation can actually unlock the fourth light through play");
        GrowthStop("fourth-earned");
        var badWater = PointArgs(InTheOpen); badWater["variant"] = "bioluminesce";
        var badTorch = SelfArgs(); badTorch["variant"] = "torch";
        Check(Code(Send(Glow("growth-dry-water", badWater), Player)) == "invalid_args" &&
            Code(Send(Glow("growth-body-torch", badTorch), Player)) == "invalid_args" &&
            !_host.GlowProgress.HasDiscovery("bioluminesce") && !_host.GlowProgress.HasDiscovery("torch"),
            "requesting a name cannot invent water or a supported Torch, and rejected variants earn nothing");

        var configure = Command("growth-touch-ember", "glow.configure", new JsonObject { ["touch"] = "ember", ["sound_volume"] = 0.2, ["muted"] = true });
        var casts = _host.GlowProgress.SuccessfulCasts;
        Check(Ok(Send(configure, Player)) && Ok(Send(configure, Player)) && _host.GlowProgress.SelectedTouch == "ember" &&
            _host.GlowProgress.SoundMuted && _host.GlowProgress.SuccessfulCasts == casts,
            "the player can save cosmetic touch and sound choices without earning a cast, and replay stays harmless");
        Check(Code(Send(Command("growth-ai-touch", "glow.configure", new JsonObject { ["touch"] = "frost" }), Companion)) == "permission_denied",
            "the connected AI cannot change the player's saved touch or audio preferences");
        var touched = _host.PlayerEffect("glow");
        Check(Ok(touched) && look.GlowTouch(Effect(touched)) == "ember" && look.GlowVariant(Effect(touched)) == "lantern",
            "manual Glow carries the saved cosmetic touch into its real Lantern drawing");
        GrowthStop("touch");
        // Glow's "Light the bubbles" lit the stream of small bubbles, which the founder's redesign removed (decision 55): with only the
        // one bubble now, it finds no stream to light and is refused, until the founder decides whether it lights the Bubble.
        var bubbles = Send(Glow("growth-lit-bubble-source", PointArgs(InTheOpen, capability: "bubbles")), Player);
        var bubbleGlowArgs = SelfArgs(); bubbleGlowArgs["variant"] = "glowing_bubbles";
        var litBubbles = Send(Glow("growth-lit-bubbles", bubbleGlowArgs), Player);
        Check(Ok(bubbles) && Code(litBubbles) == "target_not_found" && litBubbles["error"]?["field_path"]?.GetValue<string>() == "$.args.variant" && look.EffectCount == 0,
            "with the stream of small bubbles gone, Glow's glowing_bubbles finds none to light and is refused: " + Code(litBubbles));
        Send(Command("growth-stop-lit-bubble-source", "effect.stop", new JsonObject { ["effect"] = "all" }), Player);
        Check(_host.ActiveEffectIds.Count == 0 && _host.ActiveBubbles.Count == 0 && look.EffectCount == 0, "and the bubble pops with the player's stop of everything");
        _host.ConfigureGlowProgress(new GrowthTestStore(GrowthComplete()));
        for (var index = 0; index < 3; index++)
        {
            var args = PointArgs(InTheOpen + Vector3.Right * (index * 0.05f)); args["variant"] = "plain";
            ids[index] = Effect(Send(Glow("growth-edit-point-" + index, args), Player));
        }
        var old = _host.ActiveGlowPoints.First(point => point.Id == ids[0]).Center;
        var target = old + new Vector3(0.025f, 0.01f, 0.01f);
        var editArgs = new JsonObject { ["effect"] = ids[0], ["center_m"] = KernelJson.Vector(target), ["connections"] = new JsonArray() };
        var preview = Command("growth-adjust-preview", "effect.adjust", editArgs.DeepClone().AsObject()); preview["preview"] = true;
        Check(Ok(Send(preview, Player)) && _host.ActiveGlowPoints.First(point => point.Id == ids[0]).Center == old && look.GlowConnectionCount == 3,
            "previewing a learned constellation edit leaves the points and links untouched");
        Check(Code(Send(Command("growth-adjust-other-caster", "effect.adjust", editArgs.DeepClone().AsObject()), Companion)) == "permission_denied",
            "an AI cannot move or relink a point cast by someone else");
        var edited = Send(Command("growth-adjust-real", "effect.adjust", editArgs), Player);
        Check(Ok(edited) && _host.ActiveGlowPoints.First(point => point.Id == ids[0]).Center == target && look.GlowConnectionCount == 1 &&
            look.GlowRoot(ids[0])!.GlobalPosition.DistanceTo(target) < 0.1f,
            "a learned point moves through the host and its two links can be removed without changing the other link");
        GrowthStop("edited");
        Check(look.GlowConnectionCount == 0, "stopping the effects removes every constellation tendril");

        // The look's variation contract is checked without a GPU; host geometry tests cover acceptance separately.
        Check(look.StartGlowVariant("growth-water-look", null, InTheOpen, 0.8f, 0.6f, "bioluminesce", "plain", null, 0, true) &&
            look.GlowRoot("growth-water-look")!.GetNode<Godot.MeshInstance3D>("Core").Visible == false &&
            look.GlowRoot("growth-water-look")!.GetNodeOrNull<MultiMeshInstance3D>("GlowTouch") != null,
            "Bioluminesce draws a water field and hides the ordinary floating orb");
        look._Process(1.0);
        Check(!look.GlowRoot("growth-water-look")!.GetNode<MeshInstance3D>("Core").Visible &&
            !look.GlowRoot("growth-water-look")!.GetNode<MeshInstance3D>("Halo").Visible,
            "the water field stays a field after its arrival spark lands");
        Check(look.StartGlowVariant("growth-star-look", null, InTheOpen, 2f, 0.6f, "skylight", "frost", null, 0, true) &&
            look.GlowRoot("growth-star-look")!.GlobalPosition.Y > InTheOpen.Y + 1 && look.GlowLight("growth-star-look")!.LightEnergy < GlowLook.Energy(0.6f),
            "Skylight is a high, faint real light with a distinct cosmetic touch");
        look.StopAllGlows();
    }

    private async Task TestGlowAudioLifecycle()
    {
        var look = new LookDirector { Name = "GrowthAudioLifecycle" };
        ulong activePlaybackId = 0, queuedPlaybackId = 0, voiceId = 0;
        AddChild(look);
        try
        {
            foreach (var id in new[] { "growth-audio-first", "growth-audio-second" })
            {
                look.StartGlowVariant(id, null, InTheOpen, 0.8f, 0.6f, "plain", "chime", null, 0.35f, false);
                look.PlayGlowSound(id);
            }
            var second = look.GlowRoot("growth-audio-second")!.GetNode<AudioStreamPlayer3D>("GlowChime");
            var sharedVoice = second.Stream;
            voiceId = sharedVoice.GetInstanceId();
            await Frames(2);
            using (var playback = second.GetStreamPlayback()) activePlaybackId = playback.GetInstanceId();
            look.StopGlow("growth-audio-first");
            Check(IsInstanceValid(sharedVoice) && second.Stream == sharedVoice && second.Playing,
                "stopping one Glow preserves the cached chime while another Glow is still playing it");

            look.StartGlowVariant("growth-audio-queued", null, InTheOpen, 0.8f, 0.6f, "plain", "chime", null, 0.35f, false);
            look.PlayGlowSound("growth-audio-queued");
            var queued = look.GlowRoot("growth-audio-queued")!.GetNode<AudioStreamPlayer3D>("GlowChime");
            using (var playback = queued.GetStreamPlayback()) queuedPlaybackId = playback.GetInstanceId();
            RemoveChild(look);
            Check(second.Stream == null && queued.Stream == null && !second.HasStreamPlayback() && !queued.HasStreamPlayback() &&
                !IsInstanceValid(sharedVoice),
                "Look teardown releases active and queued chime playback before disposing the shared generated sound");
        }
        finally
        {
            if (look.GetParent() != null) RemoveChild(look);
            look.Free();
        }
        var audioDeadline = Time.GetTicksMsec() + 200;
        while ((GrowthAudioResourceAlive(activePlaybackId) || GrowthAudioResourceAlive(queuedPlaybackId) || GrowthAudioResourceAlive(voiceId)) &&
            Time.GetTicksMsec() < audioDeadline)
        {
            await Task.Delay(5);
            await Frames(1);
        }
        Check(!GrowthAudioResourceAlive(activePlaybackId) && !GrowthAudioResourceAlive(queuedPlaybackId) && !GrowthAudioResourceAlive(voiceId),
            "stopped active and queued chimes release their native playback and voice resources without waiting for the tone to finish");
    }

    private static bool GrowthAudioResourceAlive(ulong id)
    {
        // IsInstanceIdValid creates a managed wrapper too, but does not dispose
        // it. Release this observation's reference so it cannot retain the sound.
        using var resource = InstanceFromId(id);
        return resource != null;
    }

    private static GlowProgress GrowthComplete() => GlowProgress.Restore(5,
        new[] { "lantern", "torch", "bioluminesce", "constellation", "skylight" }, Array.Empty<string>());

    private static bool GrowthRefused(Action action)
    {
        try { action(); return false; }
        catch (GlowProgressException) { return true; }
    }

    private sealed class GrowthTestStore : IGlowProgressStore
    {
        private GlowProgress _state;
        public int Saves { get; private set; }
        public bool FailSave { get; set; }
        public bool FailLoad { get; set; }

        public GrowthTestStore(GlowProgress? state = null) => _state = state ?? GlowProgress.Fresh;
        public GlowProgress Load() => FailLoad ? throw new GlowProgressException("The test profile could not load.") : _state;
        public void Save(GlowProgress state)
        {
            if (FailSave) throw new GlowProgressException("The test profile could not save.");
            _state = state;
            Saves++;
        }
    }
}
