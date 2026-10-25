using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Look;
using GarageGame.Native.Look.Charm;
using GarageGame.Native.Room;

namespace GarageGame.Tests.Look;

/// <summary>
/// Run 2, L11: the hamlet's charm (docs/look/CHARM.md). The shaders take exactly what the look sets; the survey reads the kit's
/// token from an object's category (else its display name); a synthetic open-land hamlet (three cottages, a fence, a drying line, a
/// lantern and a crate on the test room's floor) gets smoke at each chimney rising downwind, a glowing pane on each window, bunting
/// and a washing line between the right eave corners, a glow on the lantern and lights along the fence, the same on every run and
/// the same whether the ground comes from rays or from the floor's own height; nothing passes near the spawn, over the sea, too low
/// over the ground (a path, a bed or a meadow) or through another object's box; LampsOn brings the night kinds in and takes them
/// away; the pin and a room take it all away; it costs little and allocates nothing in steady state; and on the garage island,
/// when the fixture names it, the hamlet is found and dressed within budget.
/// </summary>
public partial class LookPresetTest
{
    private static readonly Vector3 CottageSize = new(0.4292f, 0.4292f, 0.3552f);

    private async Task CheckCharm(StylePreset preset)
    {
        CheckCharmShaders();
        CheckCharmTokens();
        var room = RoomData.Load(CharmRoom("charm_hamlet"));
        await CheckCharmHamlet(preset, room);
        CheckCharmRules(room);
        await CheckCharmPinAndRooms(preset, room);
        CheckCharmCost(room, "the synthetic hamlet", ticks: 600);
        await CheckRealCharm();
    }

    // ---------- the shaders and the cloth ----------

    /// <summary>Every uniform CharmLook sets is declared and every declared uniform is set; the night lights add light and light nothing; the cloth is under the fish's 200 triangles.</summary>
    private void CheckCharmShaders()
    {
        foreach (var (path, names, materials) in new (string, string[], ShaderMaterial[])[]
                 {
                     (CharmLook.SmokeShaderPath, CharmLook.SmokeParameterNames, new[] { CharmLook.SmokeMaterial() }),
                     (CharmLook.ClothShaderPath, CharmLook.ClothParameterNames, new[] { CharmLook.ClothMaterial(false, CharmLook.WindAxis()), CharmLook.ClothMaterial(true, CharmLook.WindAxis()) }),
                     (CharmLook.GlowShaderPath, CharmLook.GlowParameterNames, Enum.GetValues<CharmLook.GlowKind>().Select(CharmLook.GlowMaterial).ToArray()),
                 })
        {
            var declared = GD.Load<Shader>(path).GetShaderUniformList().Select(u => u.AsGodotDictionary()["name"].AsString()).ToHashSet();
            var set = names.ToHashSet();
            Check(declared.Count >= 4 && set.Except(declared).Count() == 0, $"every parameter CharmLook gives {path} is one of its uniforms: not declared " + string.Join(",", set.Except(declared)));
            Check(declared.Except(set).Count() == 0, $"every uniform of {path} is set by CharmLook: unset " + string.Join(",", declared.Except(set)));
            var missing = materials.SelectMany(m => names.Where(n => m.GetShaderParameter(n).VariantType == Variant.Type.Nil).Select(n => $"{m.ResourceName}.{n}")).ToArray();
            Check(missing.Length == 0, $"each of its materials has every one of them set: missing " + string.Join(",", missing));
        }
        string Code(string path) => Regex.Replace(GD.Load<Shader>(path).Code, @"//[^\n]*", "");
        var glow = Code(CharmLook.GlowShaderPath);
        Check(Regex.IsMatch(glow, @"render_mode[^;]*\bblend_add\b") && Regex.IsMatch(glow, @"render_mode[^;]*\bunshaded\b") && Regex.IsMatch(glow, @"render_mode[^;]*\bfog_disabled\b")
              && Regex.IsMatch(glow, @"abs\(\s*PROJECTION_MATRIX\[1\]\[1\]\s*\)") && glow.Contains("fog_density", StringComparison.Ordinal) && Regex.IsMatch(glow, @"\*\s*lit\b"),
            "the night lights are light added to the scene, faded by lit, never smaller than their pixels (the projection's size, never its sign), and hidden by the look's own haze");
        var cloth = Code(CharmLook.ClothShaderPath);
        var smoke = Code(CharmLook.SmokeShaderPath);
        Check(cloth.Contains("wind_dir", StringComparison.Ordinal) && cloth.Contains("TIME", StringComparison.Ordinal) && !Regex.IsMatch(cloth, @"render_mode[^;]*\bunshaded\b")
              && Regex.IsMatch(smoke, @"render_mode[^;]*\bblend_mix\b") && Regex.IsMatch(smoke, @"render_mode[^;]*\bshadows_disabled\b"),
            "the cloth sways with the wind in its shader and is lit by the scene; the smoke is a soft lit veil that casts no shadow");
        var flag = CharmMeshes.Triangles(CharmMeshes.Flag());
        var garment = CharmMeshes.Triangles(CharmMeshes.Garment());
        Check(flag is > 4 and < 200 && garment is > 4 and < 200, $"a flag ({flag} triangles) and a garment ({garment}) are each under the fish's 200, string and all");
        var axis = CharmLook.WindAxis();
        var meadow = LandscapeLook.Roles["meadow"].Axis;
        Check(Mathf.IsZeroApprox(axis.Y) && Mathf.IsEqualApprox(axis.Length(), 1f) && axis.Dot(new Vector3(meadow.X, 0f, meadow.Z).Normalized()) > 0.999f,
            $"the charm's wind is the meadow's own axis, level ({axis})");
    }

    /// <summary>The kit's token is read from the asset's category as the exporter writes it, else from the display name; anything else is no hamlet thing.</summary>
    private void CheckCharmTokens()
    {
        var cases = new (string Category, string? Display, HamletKind? Want)[]
        {
            ("cottage", "Cottage 2", HamletKind.Cottage), ("tower", "Tower 9", HamletKind.Tower), ("fence", "Fence 12", HamletKind.Fence),
            ("lantern", "Lantern 16", HamletKind.Lantern), ("drying_line", "Drying line 15", HamletKind.DryingLine), ("woodpile", "Log", HamletKind.Woodpile),
            ("crate", "Apple crate", HamletKind.Crate), ("footbridge", "Footbridge 3", HamletKind.Footbridge),
            ("rock", "Stone", null), ("bean bag", null, null),
            // An authored object whose category is its own kind: its display name, the token with spaces and a number, decides.
            ("decor", "Drying line 4", HamletKind.DryingLine), ("decor", "Lantern", HamletKind.Lantern), ("decor", "Cottage garden 2", null),
        };
        var wrong = cases.Where(c => HamletSurvey.KindOf(c.Category, c.Display) != c.Want).Select(c => $"{c.Category}/{c.Display}").ToArray();
        Check(wrong.Length == 0, "the survey reads the generator's prototype token from the asset's category (the exporter's), else from the display name (\"Drying line 15\"): wrong " + string.Join(", ", wrong));
    }

    // ---------- the synthetic hamlet ----------

    /// <summary>
    /// The test room opened to the sky (no walls, no ceiling, no window: open land) with the kit's things on its floor as boxes at the
    /// kit's sizes: three cottages, a fence, a drying line, a lantern beside the first door and a crate; extra adds more.
    /// </summary>
    private static string CharmRoom(string id, IEnumerable<(string Id, string Asset, Vector3 At, float YawDeg)>? extra = null)
    {
        var assets = new (string Id, string Display, string Category, string Group, Vector3 Size)[]
        {
            ("kit_cottage", "Cottage", "cottage", "structure", CottageSize),
            ("kit_fence", "Fence", "fence", "structure", new Vector3(0.3f, 0.132f, 0.04f)),
            ("kit_drying_line", "Drying line", "drying_line", "decor", new Vector3(0.36f, 0.2f, 0.03f)),
            ("kit_lantern", "Lantern", "lantern", "lighting", new Vector3(0.04f, 0.075f, 0.04f)),
            ("kit_crate", "Crate", "crate", "container", new Vector3(0.08f, 0.07f, 0.07f)),
            ("kit_pole", "Pole", "pole", "decor", new Vector3(0.06f, 0.5f, 0.06f)),
        };
        var things = new List<(string Id, string Asset, Vector3 At, float YawDeg)>
        {
            ("obj:cottage_a", "kit_cottage", new Vector3(-0.9f, 0f, -0.6f), 0f),
            ("obj:cottage_b", "kit_cottage", new Vector3(-0.2f, 0f, -0.65f), 0f),
            ("obj:cottage_c", "kit_cottage", new Vector3(0.55f, 0f, -0.5f), 30f),
            ("obj:fence_1", "kit_fence", new Vector3(-0.5f, 0f, 0.05f), 0f),
            ("obj:drying_1", "kit_drying_line", new Vector3(1.0f, 0f, 0.4f), 90f),
            ("obj:lantern_1", "kit_lantern", new Vector3(-0.62f, 0f, -0.86f), 0f),
            ("obj:crate_1", "kit_crate", new Vector3(1.4f, 0f, -0.3f), 15f),
        };
        if (extra != null) things.AddRange(extra);
        var files = assets.Select(a => ($"objects/{a.Id}/asset.json", new UTF8Encoding(false).GetBytes(new JsonObject
        {
            ["schema"] = "garage_game.asset", ["version"] = 1, ["asset_id"] = a.Id, ["display_name"] = a.Display, ["category"] = a.Category, ["category_group"] = a.Group,
            ["tier"] = "proxy",
            ["provenance"] = new JsonObject { ["kind"] = "placeholder", ["license"] = "Original EnFractal placeholder; CC0-1.0", ["created_utc"] = "2026-10-15T00:00:00Z", ["notes"] = "L11's synthetic hamlet: a box at a kit thing's size." },
            ["pivot"] = "bottom_center", ["dimensions_m"] = new JsonArray(a.Size.X, a.Size.Y, a.Size.Z),
            ["geometry"] = new JsonObject { ["kind"] = "primitive", ["primitive"] = "box" }, ["collision"] = new JsonObject { ["kind"] = "primitive" },
            ["physics"] = new JsonObject { ["movable"] = false, ["mass_kg"] = 100.0 },
            ["materials"] = new JsonArray(new JsonObject { ["slot"] = "body", ["role"] = "wood", ["base_color"] = "#a27665" }),
            ["affordances"] = new JsonArray("walkable_top"),
            ["review"] = new JsonObject { ["status"] = "approved", ["reviewer"] = "integrator", ["notes"] = "Test fixture only." },
            ["files"] = new JsonArray(),
        }.ToJsonString() + "\n"))).ToArray();
        return LookFixtureRooms.Write(id, room =>
        {
            var parts = room["shell"]!["parts"]!.AsArray();
            foreach (var part in parts.Where(p => p!["role"]!.GetValue<string>() is "wall" or "ceiling").ToArray()) parts.Remove(part);
            room["shell"]!["openings"] = new JsonArray();
            var hints = LookFixtureRooms.Hints(room);
            foreach (var hint in hints.Where(h => h!["kind"]!.GetValue<string>() == "window").ToArray()) hints.Remove(hint);
            var objects = new JsonArray();
            var n = 0;
            foreach (var (thingId, asset, at, yaw) in things)
            {
                var half = Mathf.DegToRad(yaw) * 0.5f;
                var display = assets.First(a => a.Id == asset).Display + " " + (++n).ToString(CultureInfo.InvariantCulture);
                objects.Add(new JsonObject
                {
                    ["id"] = thingId, ["asset"] = $"objects/{asset}/asset.json", ["display_name"] = display,
                    ["transform"] = new JsonObject { ["position_m"] = new JsonArray(at.X, at.Y, at.Z), ["rotation"] = new JsonArray(0.0, Math.Sin(half), 0.0, Math.Cos(half)) },
                    ["support"] = new JsonObject { ["kind"] = "shell", ["target_id"] = "shell:floor" },
                });
            }
            room["objects"] = objects;
        }, files);
    }

    private static CharmPlan PlanOn(RoomData room, Func<Vector2, float?>? ground = null, IReadOnlyList<SpawnPoint>? spawns = null, float? sea = null, string? roomId = null) =>
        CharmPlan.Make(roomId ?? room.RoomId, HamletSurvey.Of(room), spawns ?? room.Spawns, new List<CharmPlan.Tree>(), ground ?? (_ => 0f), sea, CharmLook.Tuning.Accents, CharmLook.Tuning.Seed);

    private static string CharmDigest(CharmPlan plan)
    {
        static string V(Vector3 v) => string.Create(CultureInfo.InvariantCulture, $"({v.X:0.00000},{v.Y:0.00000},{v.Z:0.00000})");
        static string C(Color c) => string.Create(CultureInfo.InvariantCulture, $"#{c.R:0.0000}{c.G:0.0000}{c.B:0.0000}");
        return string.Join("|", new[]
        {
            string.Join(";", plan.Chimneys.Select(c => c.CottageId + V(c.TopM) + c.Phase.ToString("0.00000", CultureInfo.InvariantCulture))),
            string.Join(";", plan.Windows.Select(w => w.CottageId + V(w.CentreM) + w.Phase.ToString("0.00000", CultureInfo.InvariantCulture))),
            string.Join(";", plan.Lanterns.Select(l => l.LanternId + V(l.CentreM))),
            string.Join(";", plan.Strings.Select(s => $"{s.FromId}>{s.ToId}{V(s.FromM)}{V(s.ToM)}{s.Load}")),
            string.Join(";", plan.Flags.Concat(plan.Garments).Select(c => V(c.TopM) + C(c.Colour) + c.Phase.ToString("0.00000", CultureInfo.InvariantCulture))),
            string.Join(";", plan.Bulbs.Select(b => V(b.AtM) + C(b.Tint))),
        });
    }

    /// <summary>
    /// The hamlet dressed: smoke at each chimney, a pane on each window, bunting and washing between the right corners, the lantern's
    /// glow, the fence's lights; the drawing matches the plan, the rays' plan matches the floor's, and LampsOn brings the night in.
    /// </summary>
    private async Task CheckCharmHamlet(StylePreset preset, RoomData room)
    {
        var survey = HamletSurvey.Of(room);
        Check(survey.Count(HamletKind.Cottage) == 3 && survey.Count(HamletKind.Fence) == 1 && survey.Count(HamletKind.DryingLine) == 1 && survey.Count(HamletKind.Lantern) == 1
              && survey.Count(HamletKind.Crate) == 1 && survey.Boxes.Count == room.Objects.Count && survey.Things.Select(t => t.Id).SequenceEqual(survey.Things.Select(t => t.Id).Order(StringComparer.Ordinal)),
            $"the survey finds the synthetic hamlet's three cottages, its fence, drying line, lantern and crate (by their categories), in id order, and every object's box");

        var plan = PlanOn(room);
        var cottages = survey.OfKind(HamletKind.Cottage).ToList();
        var wind = CharmLook.WindAxis();

        // The chimneys and their smoke.
        var onTop = plan.Chimneys.Count == 3 && plan.Chimneys.Zip(cottages).All(p =>
        {
            var local = p.Second.Turn.Inverse() * (p.First.TopM - p.Second.PositionM);
            return p.First.CottageId == p.Second.Id && Mathf.IsEqualApprox(local.Y, p.Second.SizeM.Y, 1e-4f) && local.X > 0.2f * p.Second.SizeM.X && local.Z > 0.1f * p.Second.SizeM.Z
                   && local.X < 0.5f * p.Second.SizeM.X && local.Z < 0.5f * p.Second.SizeM.Z;
        });
        Check(onTop, "each cottage has one chimney, its top on the cottage's roofline at the back right, where the kit's stone chimney stands");
        var rising = true;
        var downwind = true;
        var fromTop = true;
        foreach (var chimney in plan.Chimneys)
        {
            // The first puff from the moment it leaves the chimney (its life starts where the stream's cycle says), over its life.
            var start = (double)((1f - chimney.Phase % 1f) % 1f) * CharmLook.Tuning.SmokeLifeS + 0.02;
            var born = CharmLook.Puff(chimney, 0, start, wind, 0f);
            fromTop &= born.At.DistanceTo(chimney.TopM) < 0.012f && born.Opacity < 0.05f;
            var last = born.At;
            for (var t = 0.5; t < CharmLook.Tuning.SmokeLifeS * 0.95; t += 0.5)
            {
                var puff = CharmLook.Puff(chimney, 0, start + t, wind, 0f);
                rising &= puff.At.Y > last.Y;
                downwind &= (puff.At - chimney.TopM).Dot(wind) >= (last - chimney.TopM).Dot(wind) - 1e-4f;
                last = puff.At;
            }
        }
        var day = CharmLook.Puff(plan.Chimneys[0], 3, 2.0, wind, 0f);
        var night = CharmLook.Puff(plan.Chimneys[0], 3, 2.0, wind, 1f);
        Check(fromTop && rising && downwind && CharmLook.Tuning.SmokePuffs < 20 && night.Opacity > day.Opacity * 1.5f && night.At.IsEqualApprox(day.At),
            $"smoke leaves each chimney's top, rises through its life and leans ever further downwind; {CharmLook.Tuning.SmokePuffs} puffs a chimney (under 20), thicker once the lamps are on ({day.Opacity:0.##} by day, {night.Opacity:0.##} at night)");

        // The windows.
        var panes = plan.Windows.Count == 6 && plan.Windows.All(w =>
        {
            var cottage = cottages.First(c => c.Id == w.CottageId);
            var local = cottage.Turn.Inverse() * (w.CentreM - cottage.PositionM);
            return Mathf.IsEqualApprox(local.Z, -0.5f * cottage.SizeM.Z - CharmLook.Tuning.PaneOffsetM, 2e-3f) && Mathf.Abs(local.X) < 0.4f * cottage.SizeM.X
                   && local.Y > 0.25f * cottage.SizeM.Y && local.Y < 0.5f * cottage.SizeM.Y && w.Frame.Z.Dot(cottage.DoorSide) > 0.999f;
        }) && plan.Windows.GroupBy(w => w.CottageId).All(g => g.Count() == 2);
        Check(panes, "two glowing panes a cottage, on its door side just off the wall, beside the door at the kit windows' height, facing out");

        // The strings: bunting and washing between the right eave corners, the drying line untouched.
        bool AtCorner(Vector3 p, string id) => cottages.First(c => c.Id == id) is var c && CharmLook.Tuning.EaveCorners.Any(f => c.At(f).DistanceTo(p) < 1e-4f);
        var bunting = plan.Strings.Where(s => s.Load == StringLoad.Bunting).ToList();
        var washing = plan.Strings.Where(s => s.Load == StringLoad.Laundry).ToList();
        var pairs = plan.Strings.Select(s => string.Join("+", new[] { s.FromId, s.ToId }.Order(StringComparer.Ordinal))).ToHashSet();
        Check(plan.Strings.Count == 2 && bunting.Count == 1 && washing.Count == 1 && pairs.SetEquals(new[] { "obj:cottage_a+obj:cottage_b", "obj:cottage_b+obj:cottage_c" })
              && plan.Strings.All(s => AtCorner(s.FromM, s.FromId) && AtCorner(s.ToM, s.ToId)),
            $"the hamlet's nearest neighbours are strung eave corner to eave corner, one with bunting and one with washing ({string.Join(", ", plan.Strings.Select(s => $"{s.Load} {s.FromId} to {s.ToId}, {s.LengthM:0.##} m"))})");
        var nearest = plan.Strings.All(s =>
        {
            var a = cottages.First(c => c.Id == s.FromId);
            var b = cottages.First(c => c.Id == s.ToId);
            var best = CharmLook.Tuning.EaveCorners.SelectMany(f => CharmLook.Tuning.EaveCorners.Select(g => a.At(f).DistanceTo(b.At(g)))).Min();
            return Mathf.IsEqualApprox(s.LengthM, best, 1e-4f);
        });
        var flags = plan.Flags.All(f => plan.Strings[f.SpanIndex].Load == StringLoad.Bunting && f.TopM.DistanceTo(plan.Strings[f.SpanIndex].At(f.S)) < 1e-4f
                                        && CharmLook.Tuning.Accents.Any(a => a.SrgbToLinear().IsEqualApprox(f.Colour)))
                    && plan.Flags.Count == Math.Max(2, (int)Math.Floor((bunting[0].LengthM - CharmLook.Tuning.FlagWidthM) / CharmLook.Tuning.FlagSpacingM) + 1)
                    && plan.Flags.Select(f => f.Colour).Distinct().Count() >= Math.Min(plan.Flags.Count, CharmLook.Tuning.Accents.Length) - 1;
        var garments = plan.Garments.OrderBy(g => g.S).ToList();
        var line = washing[0];
        var laundry = garments.Count >= 2 && garments.All(g => plan.Strings[g.SpanIndex].Load == StringLoad.Laundry && g.TopM.DistanceTo(line.At(g.S)) < 1e-4f
                                                               && g.S * line.LengthM - g.WidthM * 0.5f >= CharmLook.Tuning.LaundryEndsM - 1e-4f && (1f - g.S) * line.LengthM - g.WidthM * 0.5f >= CharmLook.Tuning.LaundryEndsM - 1e-4f)
                      && garments.Zip(garments.Skip(1)).All(p => (p.Second.S - p.First.S) * line.LengthM - 0.5f * (p.First.WidthM + p.Second.WidthM) >= CharmLook.Tuning.LaundryGapM - 1e-4f);
        Check(nearest && flags && laundry,
            $"each string runs between its two cottages' nearest corners; {plan.Flags.Count} flags in the palette's accents hang along the bunting; {garments.Count} garments hang on the washing line, apart and clear of its ends");
        Check(plan.Strings.All(s => s.FromId != "obj:drying_1" && s.ToId != "obj:drying_1") && plan.Notes.Any(n => n.Contains("drying line", StringComparison.Ordinal)),
            "nothing is hung on the generator's drying line (its washing is in its own mesh), and the plan says so");

        // The lights: the lantern's glow, the fence's top, between the flags.
        var lantern = survey.OfKind(HamletKind.Lantern).Single();
        var fence = survey.OfKind(HamletKind.Fence).Single();
        var fenceBulbs = plan.Bulbs.Where(b => b.AtM.DistanceTo(fence.PositionM) < 0.3f).ToList();
        var onFence = fenceBulbs.All(b =>
        {
            var local = fence.Turn.Inverse() * (b.AtM - fence.PositionM);
            return Mathf.Abs(local.X) <= 0.46f * fence.SizeM.X && Mathf.Abs(local.Z) < 1e-3f && local.Y > fence.SizeM.Y - CharmLook.Tuning.FenceSagM - 1e-3f && local.Y < fence.SizeM.Y + 0.01f;
        });
        Check(plan.Lanterns.Count == 1 && plan.Lanterns[0].CentreM.DistanceTo(lantern.At(CharmLook.Tuning.LanternCloth)) < 1e-5f && plan.Lanterns[0].SizeM > lantern.SizeM.X,
            "the lantern glows from the middle of its cloth, wider than the lantern");
        Check(fenceBulbs.Count >= 8 && onFence && plan.Bulbs.Count == fenceBulbs.Count + plan.Flags.Count - 1,
            $"fairy lights run along the fence's top from post to post ({fenceBulbs.Count}) and between the bunting's flags ({plan.Flags.Count - 1})");

        // Where nothing goes: the spawn, too low over the ground, another object's box.
        CheckCharmClear(plan, survey, room, _ => 0f, "the synthetic hamlet");

        // The same plan on every run, from the room's id and the seed: the twin of this room under another id varies.
        var again = PlanOn(room);
        var twin = PlanOn(room, roomId: "charm_hamlet_twin");
        Check(CharmDigest(again) == CharmDigest(plan) && CharmDigest(twin) != CharmDigest(plan)
              && twin.Chimneys.Select(c => c.TopM).SequenceEqual(plan.Chimneys.Select(c => c.TopM)) && twin.Strings.Count == plan.Strings.Count,
            "the same room gives the same charm on every run (every place, colour and phase), and the same hamlet under another id the same places with its own colours and phases");

        // In a world: the node plans from rays down on its first physics frame, exactly as from the floor's height, and draws it.
        var (holder, look) = NewDirector(preset, room, "CharmLookHolder");
        var built = RoomBuilder.Build(room);
        holder.AddChild(built);
        await Frames(2);
        var charm = CharmLook.Create(room, built, look);
        charm.Running = false;
        holder.AddChild(charm);
        await Frames(3);
        var drawnPlan = charm.Plan;
        var floorPlan = CharmPlan.Make(room.RoomId, survey, room.Spawns, new List<CharmPlan.Tree>(), _ => 0f, null, look.Preset.Accents, CharmLook.Tuning.Seed);
        Check(charm.Enabled && charm.Planned && drawnPlan != null && CharmDigest(drawnPlan) == CharmDigest(floorPlan),
            $"in a world the charm plans itself on its first physics frame from rays down to the ground, and gets the plan the floor's own height gives ({charm.Summary})");
        if (drawnPlan == null) { holder.QueueFree(); await Frames(2); return; }
        var stringPieces = (charm.Bunting?.Multimesh.InstanceCount ?? 0) - drawnPlan.Flags.Count;
        Check(charm.Smoke?.Multimesh.InstanceCount == 3 * CharmLook.Tuning.SmokePuffs && charm.Windows?.Multimesh.InstanceCount == 6 && charm.Lanterns?.Multimesh.InstanceCount == 1
              && charm.FairyLights?.Multimesh.InstanceCount == drawnPlan.Bulbs.Count && stringPieces >= 2 && charm.Laundry?.Multimesh.InstanceCount > drawnPlan.Garments.Count
              && charm.GetChildren().OfType<MultiMeshInstance3D>().Count() == 6,
            $"one MultiMesh a kind, six in all: {charm.Smoke?.Multimesh.InstanceCount} puffs, 6 panes, 1 glow, {drawnPlan.Bulbs.Count} bulbs, the flags with {stringPieces} bare ends of string, the washing on its line");
        Check(charm.FindChildren("*", "Light3D", true, false).Count == 0 && charm.FindChildren("*", "CollisionObject3D", true, false).Count == 0
              && charm.Pools.All(p => p.CastShadow == GeometryInstance3D.ShadowCastingSetting.Off && p.Layers == 1u && p.VisibilityRangeEnd == CharmLook.Tuning.FarM),
            "no light, no collider, no shadow: scenery on the world layer every view draws, to the charm's far distance");
        var flagRow = charm.Instance(charm.Bunting!, 0);
        var flag0 = drawnPlan.Flags[0];
        Check(flagRow.Transform.Origin.IsEqualApprox(flag0.TopM) && Mathf.IsEqualApprox(flagRow.Transform.Basis.X.Length(), CharmLook.Tuning.FlagWidthM, 1e-5f)
              && Mathf.IsEqualApprox(flagRow.Custom.G, flag0.S, 1e-5f) && flagRow.Custom.A == CharmMeshes.ClothAndString,
            "the bunting's first instance is its first flag, drawn where the plan hangs it at its width, with its place on the span for the sway");

        // The lamps: the night kinds come in with LampsOn and go with it; by day they are not drawn at all (the look's lamps pinned
        // off first: unpinned they follow the machine's own clock).
        look.SetLamps(false);
        charm.Advance(CharmLook.Tuning.LitFadeS + 0.05f);
        var (windows, lanterns, bulbs, smoke) = (charm.Windows!, charm.Lanterns!, charm.FairyLights!, charm.Smoke!);
        var dayHidden = charm.Lit == 0f && !windows.Visible && !lanterns.Visible && !bulbs.Visible && smoke.Visible;
        look.SetLamps(true);
        charm.Advance(CharmLook.Tuning.LitFadeS * 0.5f);
        var half = charm.Lit;
        var halfShown = windows.Visible && Mathf.IsEqualApprox(((ShaderMaterial)windows.MaterialOverride).GetShaderParameter("lit").AsSingle(), half);
        charm.Advance(CharmLook.Tuning.LitFadeS);
        var full = charm.Lit == 1f && ((ShaderMaterial)bulbs.MaterialOverride).GetShaderParameter("lit").AsSingle() == 1f;
        var nightAlpha = charm.Instance(smoke, 5).Colour.A;
        look.SetLamps(false);
        charm.Advance(CharmLook.Tuning.LitFadeS + 0.05f);
        Check(look.LampsOn == false && dayHidden && Mathf.IsEqualApprox(half, 0.5f, 0.02f) && halfShown && full && charm.Lit == 0f && !windows.Visible && !bulbs.Visible,
            $"by day the windows, the lantern's glow and the fairy lights are not drawn; LampsOn brings them in over {CharmLook.Tuning.LitFadeS} s (half way at {half:0.##}) and they go when the lamps go out");
        Check(nightAlpha > 0f, "the smoke stays drawn through the night");
        charm.LampsPinned = true;
        charm.Advance(CharmLook.Tuning.LitFadeS + 0.05f);
        Check(charm.Lit == 1f && look.LampsOn == false, "a test can pin the charm's lamps without the look's");
        charm.LampsPinned = null;
        charm.Advance(CharmLook.Tuning.LitFadeS + 0.05f);

        // Its cost in the world, the lamps steady: nothing allocated.
        for (var i = 0; i < 30; i++) charm.Advance(1f / 60f);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 300; i++) charm.Advance(1f / 60f);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Check(allocated == 0, $"the charm allocates nothing in steady state ({allocated} bytes over 300 frames)");
        holder.QueueFree();
        await Frames(3);
    }

    /// <summary>Nothing of a plan is near a spawn, too low over the ground, over the sea or inside another object's box.</summary>
    private void CheckCharmClear(CharmPlan plan, HamletSurvey survey, RoomData room, Func<Vector2, float?> ground, string label)
    {
        var problems = new List<string>();
        foreach (var span in plan.Strings)
        {
            var flatFrom = new Vector2(span.FromM.X, span.FromM.Z);
            var flatTo = new Vector2(span.ToM.X, span.ToM.Z);
            foreach (var spawn in room.Spawns)
            {
                var p = new Vector2(spawn.PositionM.X, spawn.PositionM.Z);
                if (Geometry2D.GetClosestPointToSegment(p, flatFrom, flatTo).DistanceTo(p) < CharmLook.Tuning.SpawnClearM) problems.Add($"{span.FromId}>{span.ToId} by {spawn.Id}");
            }
            for (var k = 0; k <= 20; k++)
            {
                var at = span.At(k / 20f);
                var under = ground(new Vector2(at.X, at.Z)) ?? 0f;
                if (at.Y - CharmLook.Tuning.ClothDropMaxM - under < CharmLook.Tuning.HeadroomM - 1e-4f) problems.Add($"{span.FromId}>{span.ToId} low at {k}");
                if (room.Sea is { } sea && under < sea.LevelM) problems.Add($"{span.FromId}>{span.ToId} over the sea");
            }
        }
        IEnumerable<ObjectBox> Others(params string[] own) => survey.Boxes.Where(b => !own.Contains(b.Id));
        foreach (var cloth in plan.Flags.Concat(plan.Garments))
        {
            var span = plan.Strings[cloth.SpanIndex];
            var bottom = cloth.TopM + cloth.Frame.Y * -cloth.DropM;
            foreach (var box in Others(span.FromId, span.ToId))
                if (box.Holds(cloth.TopM) || box.Holds(bottom)) problems.Add($"cloth in {box.Id}");
        }
        foreach (var bulb in plan.Bulbs)
            foreach (var box in survey.Boxes.Where(b => b.Holds(bulb.AtM, 0.002f)))
                if (survey.Things.FirstOrDefault(t => t.Id == box.Id) is not { Kind: HamletKind.Fence } && !plan.Strings.Any(s => s.FromId == box.Id || s.ToId == box.Id))
                    problems.Add($"bulb in {box.Id}");
        foreach (var w in plan.Windows)
            foreach (var box in survey.Boxes.Where(b => b.Holds(w.CentreM)))
                problems.Add($"pane of {w.CottageId} in {box.Id}");
        foreach (var c in plan.Chimneys)
            foreach (var box in Others(c.CottageId).Where(b => b.Holds(c.TopM)))
                problems.Add($"chimney of {c.CottageId} in {box.Id}");
        Check(problems.Count == 0, $"{label}: no string near a spawn, under {CharmLook.Tuning.HeadroomM * 100f:0} cm of headroom or over the sea, and no piece inside another object's box: " + string.Join(", ", problems.Take(8)));
    }

    /// <summary>The rules, each on the hamlet: a string that would pass the spawn, hang over the sea, hang too low or cross another object is left out, and the plan says why.</summary>
    private void CheckCharmRules(RoomData room)
    {
        static bool Joins(Span s, string a, string b) => (s.FromId == a && s.ToId == b) || (s.FromId == b && s.ToId == a);
        static bool Strung(CharmPlan plan, string a, string b) => plan.Strings.Any(s => Joins(s, a, b));
        var plain = PlanOn(room);
        var ab = plain.Strings.First(s => Joins(s, "obj:cottage_a", "obj:cottage_b"));
        var middle = ab.At(0.5f);

        var spawned = PlanOn(room, spawns: room.Spawns.Append(new SpawnPoint("spawn_under", "any", new Vector3(middle.X, 0f, middle.Z), 0f)).ToList());
        Check(!Strung(spawned, "obj:cottage_a", "obj:cottage_b") && spawned.Notes.Any(n => n.Contains("spawn_under", StringComparison.Ordinal)),
            "a string that would pass over a spawn is left out, and the plan says which spawn: " + string.Join("; ", spawned.Notes.Where(n => n.Contains("spawn", StringComparison.Ordinal))));

        var sea = PlanOn(room, sea: 0.05f);
        Check(sea.Strings.Count == 0 && sea.Flags.Count == 0 && sea.Garments.Count == 0 && sea.Notes.Count(n => n.Contains("over the sea", StringComparison.Ordinal)) >= 2
              && sea.Chimneys.Count == 3 && sea.Windows.Count == 6,
            "with the ground under the sea's level no string hangs (nothing over the water), while the smoke and the windows stay");

        var bump = PlanOn(room, ground: p => p.DistanceTo(new Vector2(middle.X, middle.Z)) < 0.08f ? 0.12f : 0f);
        Check(!Strung(bump, "obj:cottage_a", "obj:cottage_b") && bump.Notes.Any(n => n.Contains("over the ground", StringComparison.Ordinal)),
            $"a string whose washing would hang under {CharmLook.Tuning.HeadroomM * 100f:0} cm over the ground (a path, a bed or a meadow) is left out: " + string.Join("; ", bump.Notes.Where(n => n.Contains("ground", StringComparison.Ordinal))));

        var poled = RoomData.Load(CharmRoom("charm_hamlet_pole", new[] { ("obj:pole", "kit_pole", new Vector3(middle.X, 0f, middle.Z), 0f) }));
        var crossed = PlanOn(poled);
        Check(!Strung(crossed, "obj:cottage_a", "obj:cottage_b") && crossed.Notes.Any(n => n.Contains("cross obj:pole", StringComparison.Ordinal)),
            "a string that would cross another object's box (a pole under it) is left out: " + string.Join("; ", crossed.Notes.Where(n => n.Contains("pole", StringComparison.Ordinal))));
        CheckCharmClear(crossed, HamletSurvey.Of(poled), poled, _ => 0f, "the hamlet with a pole");
    }

    /// <summary>The pin takes it all away, a room never has it, and a room with no hamlet draws nothing.</summary>
    private async Task CheckCharmPinAndRooms(StylePreset preset, RoomData hamlet)
    {
        Check(CharmLook.Pin(new[] { CharmLook.PinArgument + "off" }, null) == false && CharmLook.Pin(Array.Empty<string>(), "OFF ") == false
              && CharmLook.Pin(new[] { CharmLook.PinArgument + "on" }, "off") == true && CharmLook.Pin(Array.Empty<string>(), null) == null
              && CharmLook.Pin(new[] { CharmLook.PinArgument + "maybe" }, null) == null,
            "a capture pins the charm off by --look-charm=off (first) or GARAGE_GAME_LOOK_CHARM=off; on, or nothing, leaves the default");
        var holder = new Node3D { Name = "CharmPinHolder" };
        AddChild(holder);
        var built = RoomBuilder.Build(hamlet);
        holder.AddChild(built);
        var before = System.Environment.GetEnvironmentVariable(CharmLook.PinVariable);
        System.Environment.SetEnvironmentVariable(CharmLook.PinVariable, "off");
        var pinned = CharmLook.Create(hamlet, built, null);
        System.Environment.SetEnvironmentVariable(CharmLook.PinVariable, before);
        holder.AddChild(pinned);
        var testRoom = RoomData.Load(RoomWorld.DefaultRoom);
        var indoor = CharmLook.Create(testRoom, built, null);
        holder.AddChild(indoor);
        await Frames(3);
        pinned.PlanNow(_ => 0f);
        indoor.PlanNow(_ => 0f);
        Check(!pinned.Enabled && pinned.Plan == null && !pinned.Pools.Any() && pinned.Summary.Contains("pinned off", StringComparison.Ordinal),
            "pinned off, the hamlet gets no charm at all: no plan, no pools (" + pinned.Summary + ")");
        Check(!indoor.Enabled && indoor.Plan == null && !indoor.Pools.Any() && !LandscapeLook.IsOpenLand(testRoom),
            "a room (walls and a ceiling) never has the hamlet's charm: " + indoor.Summary);
        var bare = RoomData.Load(LookFixtureRooms.Write("charm_bare", room =>
        {
            var parts = room["shell"]!["parts"]!.AsArray();
            foreach (var part in parts.Where(p => p!["role"]!.GetValue<string>() is "wall" or "ceiling").ToArray()) parts.Remove(part);
            room["shell"]!["openings"] = new JsonArray();
            foreach (var hint in LookFixtureRooms.Hints(room).Where(h => h!["kind"]!.GetValue<string>() == "window").ToArray()) LookFixtureRooms.Hints(room).Remove(hint);
        }));
        var none = CharmLook.Create(bare, built, null);
        holder.AddChild(none);
        await Frames(2);
        none.PlanNow(_ => 0f);
        Check(none.Enabled && none.Plan != null && !none.Pools.Any() && none.Summary.Contains("no hamlet", StringComparison.Ordinal),
            "open land with no hamlet (its things are a table, a box, a book, a rug and a doorstop) draws nothing: " + none.Summary);
        holder.QueueFree();
        await Frames(2);
    }

    /// <summary>The charm's cost a frame on the CPU (the smoke written, the lamps and the haze followed) and its allocations, with the lamps steady.</summary>
    private void CheckCharmCost(RoomData room, string label, int ticks, Func<Vector2, float?>? ground = null)
    {
        var holder = new Node3D { Name = "CharmCostHolder" };
        AddChild(holder);
        var charm = CharmLook.Create(room, holder, null);
        charm.Running = false;
        holder.AddChild(charm);
        charm.PlanNow(ground ?? (_ => null));
        for (var i = 0; i < 60; i++) charm.Advance(1f / 60f);
        var clock = Stopwatch.StartNew();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < ticks; i++) charm.Advance(1f / 60f);
        var perTick = clock.Elapsed.TotalMilliseconds * 1000.0 / ticks;
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        var puffs = charm.Smoke?.Multimesh.InstanceCount ?? 0;
        GD.Print($"LOOK_INFO: the charm on {label} ({charm.Summary}): {perTick:0.0} us a frame headless for {puffs} puffs, {allocated} bytes allocated over {ticks} frames");
        Check(perTick < 300.0 && allocated == 0, $"on {label} the charm costs {perTick:0.0} us a frame on the CPU (every kind together; the plan's budget 0.3 ms) and allocates nothing in steady state ({allocated} bytes)");
        holder.QueueFree();
    }

    /// <summary>The garage island, when the fixture names it: its hamlet found by category and dressed (no ground asked: the anchors' footing), within budget.</summary>
    private async Task CheckRealCharm()
    {
        var directory = FindLandscapeFixture();
        if (directory == null) { GD.Print("LOOK_INFO: no garage landscape: the hamlet's charm is looked at with -- --landscape=DIR"); return; }
        var land = RoomData.Load(directory);
        var survey = HamletSurvey.Of(land);
        var cottages = land.Objects.Count(o => o.Asset.Category == "cottage");
        var plan = CharmPlan.Make(land.RoomId, survey, land.Spawns, CharmLook.Trees(land, survey.Things), _ => null, land.Sea?.LevelM, CharmLook.Tuning.Accents, CharmLook.Tuning.Seed);
        Check(cottages > 0 && survey.Count(HamletKind.Cottage) == cottages && survey.Count(HamletKind.Lantern) == land.Objects.Count(o => o.Asset.Category == "lantern")
              && plan.Chimneys.Count == cottages && plan.Windows.Count == 2 * cottages && plan.Lanterns.Count == survey.Count(HamletKind.Lantern) && (cottages < 2 || plan.Strings.Count >= 1),
            $"the garage island's hamlet ({land.RoomId}): {cottages} cottages found by their category, smoke from every chimney, two windows each, a glow on each lantern, strung where they stand close");
        CheckCharmClear(plan, survey, land, _ => null, "the garage island");
        CheckCharmCost(land, "the garage island", ticks: 600);
        await Frames(1);
    }
}
