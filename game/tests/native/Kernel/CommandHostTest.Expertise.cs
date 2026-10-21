using Godot;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Room;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// The team's expertise store for several abilities (M11; docs/magic/README.md): Glow's, Bubbles' (the Bubble's carry, boat and lift
/// for now) and Fire's (five ids a later Fire packet records), each against its own fixed list, the founder's Glow-only store
/// unchanged byte for byte, the sum across abilities for the pips and the pace lines, and the one hook for the Bubble's events.
/// Nothing records a stream of bubbles, and no Bubbles or Fire hint exists yet.
/// </summary>
public partial class CommandHostTest
{
    /// <summary>The founder's real store (18 casts, four Glow expertise, one hint delivered): version 3, Glow only.</summary>
    private const string FoundersStore =
        "{\"schema\":\"enfractal.glow_progress\",\"version\":3,\"team_id\":\"local\",\"abilities\":{\"glow\":{\"successful_casts\":18," +
        "\"discoveries\":[\"lantern\",\"torch\",\"bioluminesce\",\"constellation\"],\"delivered_hints\":[\"bioluminesce_water\"]}}," +
        "\"touch\":\"plain\",\"sound_volume\":0.35,\"muted\":false}\n";

    private void TestExpertise()
    {
        TestExpertiseStore();
        TestExpertiseProgress();
        TestFoundersStoreSurvives();
        TestExpertiseHook();
        _host.ConfigureGlowProgress(new GrowthTestStore());
        _host.LoadRules(RoomWorld.DefaultRulesId, ShippedRules.Version);
    }

    private static ExpertiseRecord Full(ExpertiseList list) => ExpertiseRecord.Restore(list, 1_000_000, list.Order, list.HintOrder);

    private static GlowProgress FullStore() => GlowProgress.Restore(1_000_000, GlowProgress.DiscoveryOrder, GlowProgress.HintOrder, "chime", 0.35, true,
        new[] { Full(ExpertiseLists.BubblesList), Full(ExpertiseLists.FireList), Full(ExpertiseLists.GrowList) });

    /// <summary>The store holds several abilities, each against its own fixed list, and stays under its size cap with every one full.</summary>
    private void TestExpertiseStore()
    {
        var full = FullStore();
        var bytes = LocalGlowProgressStore.Serialize(full, "local");
        var back = LocalGlowProgressStore.Parse(bytes);
        Check(bytes.Length <= LocalGlowProgressStore.MaxSaveBytes && back.Same(full) && back.ExpertiseCount == 18 && back.Others.Keys.SequenceEqual(new[] { "bubbles", "fire", "grow" }),
            $"every ability full ({bytes.Length} of {LocalGlowProgressStore.MaxSaveBytes} bytes, 1000000 casts each) saves under the cap and reads back whole");
        var text = Encoding.UTF8.GetString(bytes);
        Check(text.IndexOf("\"glow\":", StringComparison.Ordinal) < text.IndexOf("\"bubbles\":", StringComparison.Ordinal) &&
              text.IndexOf("\"bubbles\":", StringComparison.Ordinal) < text.IndexOf("\"fire\":", StringComparison.Ordinal) &&
              text.IndexOf("\"fire\":", StringComparison.Ordinal) < text.IndexOf("\"grow\":", StringComparison.Ordinal) && !text.Contains('\r') && bytes[^1] == (byte)'\n',
            "the abilities are written in the catalog's order (Glow first), compact, with one final LF");
        Check(ExpertiseLists.BubblesList.Order.SequenceEqual(new[] { "bubble_carry", "bubble_boat", "bubble_lift" }) &&
              ExpertiseLists.FireList.Order.SequenceEqual(new[] { "first_fire", "campfire", "fireline", "snuff", "will_o_wisp" }) &&
              ExpertiseLists.BubblesList.HintOrder.Count == 0 && ExpertiseLists.FireList.HintOrder.Count == 5 &&
              ExpertiseLists.GrowList.Order.SequenceEqual(new[] { "tall_plant", "thicket", "green_ash", "rain_garden", "waterpad" }) && ExpertiseLists.GrowList.HintOrder.Count == 5,
            "Bubbles accepts the carry, the boat and the lift, with no hint; Fire accepts first_fire, campfire, fireline, snuff and will_o_wisp, each with a hint (its list is final, decisions 77 to 80); Grow accepts its five (decision 75), each with a hint");
        var some = GlowProgress.Fresh.WithExpertise("bubbles", ExpertiseLists.BubbleCarry);
        var someBytes = LocalGlowProgressStore.Serialize(some, "local");
        Check(LocalGlowProgressStore.Parse(someBytes).Same(some) && !Encoding.UTF8.GetString(someBytes).Contains("\"fire\"", StringComparison.Ordinal),
            "an ability with nothing recorded is not written");

        string Replace(string from, string to) => text.Replace(from, to, StringComparison.Ordinal);
        string Parsed(string document) { try { LocalGlowProgressStore.Parse(Encoding.UTF8.GetBytes(document)); return ""; } catch (GlowProgressException error) { return error.Message; } }
        const string BubblesCarry = "\"bubbles\":{\"successful_casts\":1000000,\"discoveries\":[\"bubble_carry\",";
        var refusals = new (string Label, string Document)[]
        {
            ("an ability the code does not know", Replace("\"fire\":", "\"comet\":")),
            ("Fire's ids under Grow", Replace("\"fire\":", "\"grow\":")),
            ("the replaced Waterside", Replace("\"waterpad\"]", "\"waterside\"]")),
            ("another ability's id under Bubbles", Replace(BubblesCarry, "\"bubbles\":{\"successful_casts\":1000000,\"discoveries\":[\"campfire\",")),
            ("the dropped stream's Bubble trail", Replace(BubblesCarry, "\"bubbles\":{\"successful_casts\":1000000,\"discoveries\":[\"bubble_trail\",")),
            ("a Fire id of the replaced list", Replace("\"first_fire\"", "\"firebreak\"")),
            ("a duplicated expertise", Replace("\"discoveries\":[\"first_fire\",\"campfire\"", "\"discoveries\":[\"first_fire\",\"first_fire\"")),
            ("a hint for Bubbles, which has none until its list is final", Replace("\"delivered_hints\":[]},\"fire\"", "\"delivered_hints\":[\"bubble_carry_ride\"]},\"fire\"")),
            // Fire's list is final (decisions 77 to 80): its hints are its own five, and no other.
            ("a hint Fire's list does not hold", Replace("\"first_fire_rest\"", "\"campfire_small\"")),
            ("no abilities at all", Replace(text[text.IndexOf("\"abilities\":", StringComparison.Ordinal)..(text.IndexOf("\"touch\":", StringComparison.Ordinal))], "\"abilities\":{},")),
            ("an extra field on an ability", Replace("\"fire\":{\"successful_casts\"", "\"fire\":{\"power\":9,\"successful_casts\"")),
            ("a count past the bound", Replace("\"fire\":{\"successful_casts\":1000000", "\"fire\":{\"successful_casts\":1000001")),
        };
        foreach (var (label, document) in refusals)
            Check(Parsed(document).Length > 0, "the store refuses " + label);
        var carry = "{\"schema\":\"enfractal.glow_progress\",\"version\":3,\"team_id\":\"local\",\"abilities\":{\"bubbles\":{\"successful_casts\":0," +
            "\"discoveries\":[\"bubble_carry\"],\"delivered_hints\":[]}},\"touch\":\"plain\",\"sound_volume\":0.35,\"muted\":false}\n";
        var carried = LocalGlowProgressStore.Parse(Encoding.UTF8.GetBytes(carry));
        Check(carried.Has("bubbles", "bubble_carry") && carried.SuccessfulCasts == 0 && carried.ExpertiseCount == 1,
            "the Bubble's events need no cast behind them, and a store may hold only another ability's record (Glow starts fresh)");

        // The contract's own example (several abilities in one version) reads, and its invalid expertise documents are refused.
        var examples = ProjectSettings.GlobalizePath("res://") + "../contracts/examples/";
        var example = LocalGlowProgressStore.ParseDocument(File.ReadAllBytes(examples + "teams/example_team/expertise.json"));
        var invalid = Directory.GetFiles(examples + "teams_invalid", "expertise_*.json").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        Check(example is { Version: 3, TeamId: "example_team" } && example.Progress.Discoveries.SequenceEqual(new[] { "lantern", "torch" }) &&
              example.Progress.Of("bubbles")!.Discoveries.SequenceEqual(new[] { "bubble_carry" }) && example.Progress.Of("fire")!.Discoveries.SequenceEqual(new[] { "first_fire" }) &&
              example.Progress.ExpertiseCount == 4 && invalid.Length >= 1 && invalid.All(f => GrowthRefused(() => LocalGlowProgressStore.Parse(File.ReadAllBytes(f)))),
            $"the contract's expertise example (Glow, Bubbles and Fire in one version 3) reads, and its {invalid.Length} invalid expertise documents are refused");

        // A known ability never locks the store; an unknown one does, whole, and keeps the file.
        var path = ProjectSettings.GlobalizePath($"{TestRoot}/expertise/{Guid.NewGuid():N}/progress.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        var known = new LocalGlowProgressStore(path);
        Check(known.Load().Same(full) && !known.IsReadOnly, "a store holding every known ability loads and stays writable");
        File.WriteAllText(path, Replace("\"fire\":", "\"comet\":"), new UTF8Encoding(false));
        var kept = File.ReadAllBytes(path);
        var unknown = new LocalGlowProgressStore(path);
        Check(GrowthRefused(() => unknown.Load()) && unknown.IsReadOnly && GrowthRefused(() => unknown.Save(full)) && File.ReadAllBytes(path).SequenceEqual(kept),
            "an ability the code does not know locks the store read-only and the file is kept byte for byte");
    }

    /// <summary>The progress rules: once per expertise, in the list's order, nothing counted or hinted, and the sum for the pips.</summary>
    private void TestExpertiseProgress()
    {
        var lift = GlowProgress.Fresh.WithExpertise("bubbles", ExpertiseLists.BubbleLift);
        var both = lift.WithExpertise("bubbles", ExpertiseLists.BubbleCarry);
        Check(both.Of("bubbles")!.Discoveries.SequenceEqual(new[] { "bubble_carry", "bubble_lift" }) && both.Of("bubbles")!.SuccessfulCasts == 0 &&
              ReferenceEquals(both.WithExpertise("bubbles", ExpertiseLists.BubbleLift), both) && both.SuccessfulCasts == 0 && both.Discoveries.Count == 0 && both.ExpertiseCount == 2,
            "an expertise is learned once however often it happens again, in the list's order, with no cast of its own, and Glow's record is untouched");
        var fire = GlowProgress.Fresh;
        foreach (var id in ExpertiseLists.FireOrder) fire = fire.WithExpertise("fire", id);
        Check(fire.Of("fire")!.Discoveries.SequenceEqual(ExpertiseLists.FireOrder) && fire.Of("fire")!.DeliveredHints.Count == 0 && fire.Of("bubbles")!.IsEmpty && fire.Others.Count == 1,
            "all five of Fire's ids are accepted, with no hint, and Bubbles' record is its own");
        Check(GrowthRefused(() => GlowProgress.Fresh.WithExpertise("bubbles", "campfire")) && GrowthRefused(() => GlowProgress.Fresh.WithExpertise("fire", "bubble_carry")) &&
              GrowthRefused(() => GlowProgress.Fresh.WithExpertise("bubbles", "bubble_trail")) && GrowthRefused(() => GlowProgress.Fresh.WithExpertise("bubbles", "pond_bubbles")) &&
              GrowthRefused(() => GlowProgress.Fresh.WithExpertise("fire", "night_fire")) && GrowthRefused(() => GlowProgress.Fresh.WithExpertise("fire", "firebreak")) &&
              GrowthRefused(() => GlowProgress.Fresh.WithExpertise("comet", "bubble_carry")) && GrowthRefused(() => GlowProgress.Fresh.WithExpertise("grow", "waterside")) &&
              GrowthRefused(() => GlowProgress.Fresh.WithExpertise("glow", "lantern")) && GrowthRefused(() => GlowProgress.Fresh.WithExpertise("bubbles", "")),
            "an id from another list, the dropped ones (Grow's Waterside among them), an unknown ability and Glow's own (its casts are AfterObservedCast's) are refused");
        // Grow's casts (decision 75, the shared rules): counted, each expertise once, a hint at the 10th to 50th cast for the first not learned.
        var grow = GlowProgress.Fresh;
        for (var cast = 0; cast < 9; cast++) grow = grow.AfterAbilityCast("grow", Array.Empty<string>());
        var nine = grow.Of("grow")!.DeliveredHints.Count;
        grow = grow.AfterAbilityCast("grow", Array.Empty<string>());
        var tenth = grow.Of("grow")!.DeliveredHints.ToArray();
        grow = grow.AfterAbilityCast("grow", new[] { ExpertiseLists.TallPlant, ExpertiseLists.TallPlant });
        for (var cast = 0; cast < 9; cast++) grow = grow.AfterAbilityCast("grow", new[] { ExpertiseLists.Waterpad });
        Check(nine == 0 && tenth.SequenceEqual(new[] { "tall_plant_one" }) && grow.Of("grow")!.SuccessfulCasts == 20 &&
              grow.Of("grow")!.Discoveries.SequenceEqual(new[] { ExpertiseLists.TallPlant, ExpertiseLists.Waterpad }) &&
              grow.Of("grow")!.DeliveredHints.SequenceEqual(new[] { "tall_plant_one", "thicket_together" }) && grow.ExpertiseCount == 2 &&
              GrowthRefused(() => GlowProgress.Fresh.AfterAbilityCast("grow", new[] { "waterside" })) && GrowthRefused(() => GlowProgress.Fresh.AfterAbilityCast("glow", Array.Empty<string>())),
            "Grow's casts are counted, each expertise learned once in the list's order, the 10th cast hints Tall plant and the 20th the next not learned (Thicket); an id off the list is refused");
        Check(GrowthRefused(() => ExpertiseRecord.Restore(ExpertiseLists.FireList, 9, Array.Empty<string>(), new[] { "campfire_small" })) &&
              GrowthRefused(() => ExpertiseRecord.Restore(ExpertiseLists.BubblesList, 0, new[] { "bubble_carry", "bubble_carry" }, Array.Empty<string>())) &&
              GrowthRefused(() => GlowProgress.Restore(0, Array.Empty<string>(), Array.Empty<string>(), "plain", 0.35, false,
                  new[] { ExpertiseRecord.Empty(ExpertiseLists.FireList).WithLearned("campfire"), ExpertiseRecord.Empty(ExpertiseLists.FireList).WithLearned("snuff") })),
            "a restored record refuses a hint nothing defines, a duplicate expertise and an ability twice");

        var all = GlowProgress.Restore(5, GlowProgress.DiscoveryOrder, Array.Empty<string>(), "plain", 0.35, false,
            new[] { ExpertiseRecord.Restore(ExpertiseLists.BubblesList, 0, ExpertiseLists.BubblesOrder, Array.Empty<string>()), ExpertiseRecord.Restore(ExpertiseLists.FireList, 0, ExpertiseLists.FireOrder, Array.Empty<string>()),
                ExpertiseRecord.Restore(ExpertiseLists.GrowList, 50, ExpertiseLists.GrowOrder, Array.Empty<string>()) });
        Check(all.ExpertiseCount == 18 && GlowProgress.Fresh.ExpertiseCount == 0 && GlowProgress.Restore(5, GlowProgress.DiscoveryOrder, Array.Empty<string>()).ExpertiseCount == 5,
            "the expertise of every ability add up: Glow's five, Bubbles' three for now, Fire's five and Grow's five");
        _host.LoadRules(RoomWorld.DefaultRulesId, ShippedRules.Version);
        int Pips(GlowProgress progress) { _host.ConfigureGlowProgress(new GrowthTestStore(progress)); return _host.BodyRules.MaxPips; }
        var glowFive = GlowProgress.Restore(5, GlowProgress.DiscoveryOrder, Array.Empty<string>());
        Check(_host.BodyRules.Pips != null && Pips(GlowProgress.Fresh) == 8 && Pips(glowFive) == 9 && Pips(both) == 8 && Pips(fire) == 9 && Pips(all) == 11 && _host.ExpertiseCount() == 18,
            "the pips' maximum is 8, then one per five across every ability: Glow's five reach 9, Fire's five alone 9, and all eighteen 11");
    }

    /// <summary>The founder's real store loads byte for byte as today: its expertise and pips kept, rewritten unchanged until something new is learned.</summary>
    private void TestFoundersStoreSurvives()
    {
        var original = Encoding.UTF8.GetBytes(FoundersStore);
        var path = ProjectSettings.GlobalizePath($"{TestRoot}/expertise/{Guid.NewGuid():N}/progress.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, original);
        _host.LoadRules(RoomWorld.DefaultRulesId, ShippedRules.Version);
        var store = new LocalGlowProgressStore(path);
        _host.ConfigureGlowProgress(store);
        var loaded = _host.GlowProgress;
        Check(loaded.SuccessfulCasts == 18 && loaded.Discoveries.SequenceEqual(new[] { "lantern", "torch", "bioluminesce", "constellation" }) &&
              loaded.DeliveredHints.SequenceEqual(new[] { "bioluminesce_water" }) && loaded.Others.Count == 0 && loaded.ExpertiseCount == 4 && _host.ExpertiseCount() == 4 &&
              _host.BodyRules.MaxPips == 8 && loaded.ActiveLightAllowance == 4 && loaded.SelectedTouch == "plain" && loaded.SoundVolume == 0.35 && !loaded.SoundMuted,
            "the founder's Glow-only store loads: its four expertise, its hint, its four lights and its pips (8, the fifth expertise still to come)");
        Check(LocalGlowProgressStore.Serialize(loaded, "local").SequenceEqual(original), "and it serialises to the very same bytes");
        store.Save(loaded);
        store.Save(loaded.WithConfiguration("plain", 0.35, false));
        Check(File.ReadAllBytes(path).SequenceEqual(original), "saved back unchanged (even through the settings path) it is still the same file, byte for byte");
        // Something new: the Glow part stays exactly as it was, and the new ability joins after it.
        var learned = _host.RecordExpertise(ExpertiseLists.Bubbles, ExpertiseLists.BubbleCarry);
        var after = File.ReadAllText(path);
        var glowPart = FoundersStore[..FoundersStore.IndexOf("}}", StringComparison.Ordinal)];
        Check(learned && after.StartsWith(glowPart + "}", StringComparison.Ordinal) &&
              after.Contains(",\"bubbles\":{\"successful_casts\":0,\"discoveries\":[\"bubble_carry\"],\"delivered_hints\":[]}", StringComparison.Ordinal) &&
              _host.ExpertiseCount() == 5 && _host.BodyRules.MaxPips == 9,
            "only when it learns something new does the file change: Glow's record is as it was, Bubbles joins it, and the fifth expertise raises the pips to 9: " + after.Trim());
        Check(new LocalGlowProgressStore(path).Load().Same(_host.GlowProgress), "and the new file reads back as the host holds it");
    }

    /// <summary>
    /// The plain Bubble teaches nothing and saves nothing (Bubble trail and Pond bubbles were dropped with the stream, the founder, 15
    /// October): a bubble to walk into at a spot is no expertise, and there is no Bubble on the Gubble itself any more (decision 55).
    /// </summary>
    private void TestBubblesStreamTeachesNothing()
    {
        GrowthStop("before-bubbles");
        _host.LoadRules(RoomWorld.DefaultRulesId, ShippedRules.Version);
        var store = new GrowthTestStore();
        _host.ConfigureGlowProgress(store);
        var self = Send(Glow("exp-stream-self", SelfArgs(capability: "bubbles")), Player);
        var point = Send(Glow("exp-stream-point", PointArgs(InTheOpen, capability: "bubbles")), Companion);
        Check(Code(self) == "invalid_args" && Ok(point) && point["data"]?["kind"]?.GetValue<string>() == "walk_in" && _host.GlowProgress.Others.Count == 0 &&
              _host.ExpertiseCount() == 0 && store.Saves == 0,
            "the plain Bubble at a spot is no expertise (nothing is learned, counted or saved), and none is cast on the Gubble itself");
        GrowthStop("after-bubbles");
    }

    /// <summary>The Bubble's events (packet M9) go through one method, the same save path: learned once, a pace line, the pips follow.</summary>
    private void TestExpertiseHook()
    {
        TestBubblesStreamTeachesNothing();
        _host.LoadRules(RoomWorld.DefaultRulesId, ShippedRules.Version);
        // The team already holds four of Glow's, so the next expertise of any ability is the fifth, and the pool grows a pip.
        var store = new GrowthTestStore(GlowProgress.Restore(18, new[] { "lantern", "torch", "bioluminesce", "constellation" }, new[] { "bioluminesce_water" }));
        _host.ConfigureGlowProgress(store);
        var before = _host.BodyRules.MaxPips;
        Check(_host.RecordExpertise(ExpertiseLists.Bubbles, ExpertiseLists.BubbleCarry) && _host.GlowProgress.Has("bubbles", "bubble_carry") && store.Saves == 1 &&
              _host.GlowProgress.Of("bubbles")!.SuccessfulCasts == 0 && _host.ExpertiseCount() == 5 && _host.BodyRules.MaxPips == before + 1,
            "the hook records the Bubble's carry for the team, saved, with no cast of its own; as the fifth expertise it raises the pool by a pip");
        var lines = Query("journal.read", new JsonObject { ["kind"] = "found" }, Player)["data"]?["entries"]?.AsArray().Select(e => e?["line"]?.GetValue<string>()).ToList();
        Check(lines?.Contains("The Gubble learned something new (5 expertise).") == true && lines.Contains($"The Gubble's pool grew to {before + 1} pips."),
            "its pace line, and the pool's, are in the journal: " + string.Join(" | ", lines ?? new()));
        Check(!_host.RecordExpertise(ExpertiseLists.Bubbles, ExpertiseLists.BubbleCarry) && store.Saves == 1, "a second carry teaches nothing and saves nothing");
        Check(!_host.RecordExpertise(ExpertiseLists.Bubbles, "make-me-powerful") && !_host.RecordExpertise("comet", "bubble_boat") && !_host.RecordExpertise(ExpertiseLists.Glow, "lantern") &&
              !_host.RecordExpertise(ExpertiseLists.Bubbles, ExpertiseLists.Campfire) && !_host.RecordExpertise(ExpertiseLists.Bubbles, "bubble_trail") &&
              !_host.RecordExpertise(ExpertiseLists.Bubbles, "pond_bubbles") && store.Saves == 1 && _host.ExpertiseCount() == 5,
            "a name from nowhere, another ability's, the dropped Bubble trail and Pond bubbles, an ability that is not there and Glow's own record nothing");
        foreach (var id in new[] { ExpertiseLists.BubbleBoat, ExpertiseLists.BubbleLift }) Check(_host.RecordExpertise(ExpertiseLists.Bubbles, id), "the Bubble's " + id + " is learned too");
        Check(_host.ExpertiseCount() == 7 && _host.GlowProgress.Of("bubbles")!.Discoveries.SequenceEqual(ExpertiseLists.BubblesOrder) && _host.GlowProgress.Of("bubbles")!.DeliveredHints.Count == 0,
            "all three of Bubbles' for now are learned, in the list's order, with no hint among them");
        // Learned is seen across abilities: a Fire id counts in the same total, with its own pace line.
        Check(_host.RecordExpertise(ExpertiseLists.Fire, ExpertiseLists.FirstFire) && _host.ExpertiseCount() == 8 &&
              Query("journal.read", new JsonObject { ["kind"] = "found" }, Player)["data"]!["entries"]!.AsArray().Any(e => e!["line"]!.GetValue<string>() == "The Gubble learned something new (8 expertise)."),
            "a Fire id is learned through the same hook and counted in the same total, with its pace line");
        var failing = new GrowthTestStore { FailSave = true };
        _host.ConfigureGlowProgress(failing);
        Check(!_host.RecordExpertise(ExpertiseLists.Bubbles, ExpertiseLists.BubbleBoat) && _host.ExpertiseCount() == 0 && _host.GlowProgressNotice.Length > 0,
            "an event whose journal cannot be saved is not kept, never throws, and says so");
        failing.FailSave = false;
        Check(_host.RecordExpertise(ExpertiseLists.Bubbles, ExpertiseLists.BubbleBoat) && _host.GlowProgressNotice.Length == 0, "and it is learned when it happens again");
        _host.ConfigureGlowProgress(new GrowthTestStore { FailLoad = true });
        Check(!_host.RecordExpertise(ExpertiseLists.Bubbles, ExpertiseLists.BubbleLift) && _host.GlowProgressNotice.Contains("could not be loaded", StringComparison.Ordinal),
            "a store that could not be loaded is waiting for recovery: nothing is written to it, and its notice stays");
    }
}
