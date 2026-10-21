using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using GarageGame.Native.Look;
using GarageGame.Native.Room;

namespace GarageGame.Tests.Look;

/// <summary>
/// The seasons on the land (L10, LandSeason): the moment's four season weights are the grade's own cross-fade (one season alone at its
/// middle and through its hold, two between, summing to one and moving one way); the land's state is the four seasons' numbers blended
/// by them, summer is the land as drawn and 15 April is spring held; the land shader's globals are declared, typed, read and set, and
/// the light never reads them; the habit of each plant set; the frost's hour; the room's live oak shifts gently and the juniper not at
/// all; and in the look, the clock pins (--look-date) and an island's world clock drive the land's season the way they drive the sky,
/// while the key, the ambient, the sky and the grade stay exactly what the moment makes them and the test room wears no material that
/// reads the season. A frame's pixels need a GPU: the four-season sheet (docs/look/SEASONS.md) is their record.
/// </summary>
public partial class LookPresetTest
{
    private static readonly string[] SeasonOrder = LookTuning.SeasonNames;

    private async Task CheckSeasons(StylePreset preset, RoomData room)
    {
        CheckSeasonWeights(preset);
        CheckLandSeasonState(preset);
        CheckSeasonColours();
        CheckSeasonShaders();
        CheckSeasonHabits();
        CheckSeasonLightUntouched(preset);
        await CheckSeasonsInTheLook(preset, room);
    }

    /// <summary>The weights: one season alone around its middle, two cross-faded between, the grade's own blend, summing to one.</summary>
    private void CheckSeasonWeights(StylePreset preset)
    {
        var seasons = preset.Tuning.Seasons;
        for (var i = 0; i < 4; i++)
        {
            var centre = seasons.CentreDays[i];
            var w = LookClock.WeightsAt(preset, centre);
            Check(w == SeasonWeights.Only(SeasonOrder[i]) && w.Of(SeasonOrder[i]) == 1f,
                $"day {centre}, the middle of {SeasonOrder[i]}, is {SeasonOrder[i]} alone ({w})");
        }
        int unbalanced = 0, crowded = 0, outOfRange = 0, reversed = 0, offGrade = 0, offLook = 0;
        var previous = LookClock.WeightsAt(preset, 365);
        for (var day = 1; day <= 365; day++)
        {
            var w = LookClock.WeightsAt(preset, day);
            var values = new[] { w.Winter, w.Spring, w.Summer, w.Autumn };
            if (Mathf.Abs(w.Sum - 1f) > 1e-6f) unbalanced++;
            if (values.Count(v => v > 0f) > 2) crowded++;
            if (values.Any(v => v is < 0f or > 1f)) outOfRange++;
            // Between two centres the next season only grows and the last only fades.
            var (from, to, _, _) = LookClock.Cross(preset, day);
            if (from != to && day > 1 && (w.Of(to) < previous.Of(to) - 1e-6f || w.Of(from) > previous.Of(from) + 1e-6f) && LookClock.Cross(preset, day - 1).From == from) reversed++;
            // The grade and the season's look are these weights over the four seasons' numbers: the weights are the grade's own.
            var moment = LookClock.At(preset, 12f, day);
            var saturation = SeasonOrder.Sum(s => w.Of(s) * preset.SeasonGrades[s].Saturation);
            var sun = SeasonOrder.Sum(s => w.Of(s) * seasons.Looks[s].SunEnergy);
            if (Mathf.Abs(moment.SeasonSaturation - saturation) > 1e-5f) offGrade++;
            if (Mathf.Abs(moment.Look.SunEnergy - sun) > 1e-5f || moment.Weights != w) offLook++;
            previous = w;
        }
        Check(unbalanced == 0 && crowded == 0 && outOfRange == 0, $"every day of the year the weights sum to one, each between 0 and 1, at most two seasons at once ({unbalanced} unbalanced, {crowded} crowded, {outOfRange} out of range)");
        Check(reversed == 0, $"between two seasons' middles the weights move one way: the next season grows, the last fades ({reversed} days reversed)");
        Check(offGrade == 0 && offLook == 0, $"the weights are the grade's and the season look's own cross-fade ({offGrade} days off the grade's saturation, {offLook} off the look's sun)");
        // Held: the whole of each season's hold is that season alone, and the review moment (17:00 on 15 April) is spring.
        var spring = seasons.CentreDays[1];
        var held = Enumerable.Range(spring - 8, 17).All(d => LookClock.WeightsAt(preset, d) == SeasonWeights.Only("spring"));
        var review = LookClock.At(preset, 17f, new DateTime(2026, 4, 15).DayOfYear);
        Check(held && review.Weights == SeasonWeights.Only("spring") && review.Season == "spring",
            $"spring is held around its middle and the review moment, 17:00 on 15 April, is spring alone ({review.Weights})");
        var midway = (seasons.CentreDays[0] + seasons.CentreDays[1]) / 2;
        var between = LookClock.WeightsAt(preset, midway);
        Check(Mathf.Abs(between.Winter - 0.5f) < 0.06f && Mathf.Abs(between.Spring - 0.5f) < 0.06f, $"halfway from winter's middle to spring's the two share the day ({between})");
        // The hemisphere turns the year round, and a preset without seasons is summer: the land as drawn.
        var south = PresetWith(("\"hemisphere\": \"north\"", "\"hemisphere\": \"south\""));
        Check(LookClock.WeightsAt(south, seasons.CentreDays[0]) == SeasonWeights.Only("summer"), "south of the equator the middle of January is summer");
        var none = PresetWith(("\"seasons\": {\n    \"enabled\": true", "\"seasons\": {\n    \"enabled\": false"));
        Check(!none.SeasonsEnabled && LookClock.At(none, 12f, seasons.CentreDays[0]).Weights == SeasonWeights.SummerOnly, "a preset without seasons is summer alone");
        CheckThrows(() => SeasonWeights.Only("monsoon").Of("monsoon"), "monsoon", "a season the look does not have is refused, naming it");
    }

    /// <summary>The land's state: the season's numbers blended by the weights; summer is nothing; the frost carries the hour.</summary>
    private void CheckLandSeasonState(StylePreset preset)
    {
        // An afternoon sun: the frost at its daytime share.
        foreach (var season in SeasonOrder)
        {
            var l = LandSeason.Tuning.Looks[season];
            var state = LandSeason.At(SeasonWeights.Only(season), 20f, 250f);
            Check(state.Leaf == new Vector4(l.Fresh, l.Turn, l.Pale, l.Blossom) && state.Ground == new Vector4(l.Straw, l.Frost * LandSeason.Tuning.FrostByDay, l.Wet, l.Snow)
                && state.Tone == new Vector4(l.MeadowGreen, l.EvergreenDark, LandSeason.Tuning.TurnStagger, LandSeason.Tuning.TurnSpan) && state.RoomOakSaturation == l.RoomOakSaturation,
                $"{season} alone gives {season}'s own numbers to the land");
        }
        var summer = LandSeason.At(SeasonWeights.Only("summer"), 60f, 180f);
        Check(summer.Leaf == Vector4.Zero && summer.Ground == Vector4.Zero && summer.Tone.X == 0f && summer.Tone.Y == 0f && summer.RoomOakSaturation == 1f,
            "summer changes nothing: the land and the room's trees as they are drawn");
        var looks = LandSeason.Tuning.Looks;
        var spring = LandSeason.At(LookClock.At(preset, 17f, new DateTime(2026, 4, 15).DayOfYear));
        Check(spring.Weights == SeasonWeights.Only("spring") && spring.Leaf.X == looks["spring"].Fresh && spring.Leaf.W == looks["spring"].Blossom
            && spring.Leaf.Y == 0f && spring.Leaf.Z == 0f && spring.Ground.W == 0f && spring.Ground.X == 0f && spring.Ground.Y == 0f,
            "at the review moment the land is spring held: fresh leaves in blossom, no turn, no pale, no straw, no frost, no snow");
        var day = (preset.Tuning.Seasons.CentreDays[2] + preset.Tuning.Seasons.CentreDays[3]) / 2;
        var w = LookClock.WeightsAt(preset, day);
        var blended = LandSeason.At(w, 20f, 250f);
        Check(Mathf.Abs(blended.Leaf.Y - w.Autumn * looks["autumn"].Turn) < 1e-6f && Mathf.Abs(blended.Ground.X - w.Autumn * looks["autumn"].Straw) < 1e-6f
            && Mathf.Abs(blended.RoomOakSaturation - (w.Summer * looks["summer"].RoomOakSaturation + w.Autumn * looks["autumn"].RoomOakSaturation)) < 1e-6f,
            $"between summer and autumn (day {day}) the land is the two seasons' numbers in the weights' measure (turn {blended.Leaf.Y:0.00})");
        Check(SeasonOrder.All(s => looks[s] is { Fresh: >= 0f and <= 1f, Turn: >= 0f and <= 1f, Pale: >= 0f and <= 1f, Blossom: >= 0f and <= 1f, Snow: >= 0f and <= 1f, Straw: >= 0f and <= 1f,
            Frost: >= 0f and <= 1f, Wet: >= 0f and < 0.5f, MeadowGreen: >= 0f and <= 1f, EvergreenDark: >= 0f and <= 1f, RoomOakSaturation: > 0.5f and < 1.5f })
            && LandSeason.Tuning.TurnStagger + LandSeason.Tuning.TurnSpan <= 1f && LandSeason.Tuning.TurnSpan > 0f
            && LandSeason.Tuning.BlossomTrees > 0f && LandSeason.Tuning.BlossomTrees < 1f && LandSeason.Tuning.BlossomDabs > 0f && LandSeason.Tuning.BlossomDabs <= 1f,
            "every season's number is an amount from 0 to 1 (soil darkens by less than half, the oak's saturation stays near its own), every tree has turned when autumn's turn is whole, and some of the broadleaves flower");

        // The frost's hour: all of it at night and at dawn, melting through the morning, its daytime share in the afternoon, back by dusk.
        float F(float elevation, float bearing) => LandSeason.FrostTime(elevation, bearing);
        var byDay = LandSeason.Tuning.FrostByDay;
        Check(F(-20f, 10f) == 1f && F(0f, 100f) == 1f && F(-7f, 290f) == 1f && Mathf.Abs(F(30f, 140f) - byDay) < 1e-6f && Mathf.Abs(F(20f, 230f) - byDay) < 1e-6f
            && F(10f, 110f) < 1f && F(10f, 110f) > byDay,
            "frost is whole at night and dawn, melts as the morning sun climbs, keeps its daytime share in the afternoon and is back after dusk");
        // Read as the look reads its clock, every ClockUpdateSeconds: no step of the frost is more than a hundredth of it.
        var jumps = 0f;
        var winterDay = preset.Tuning.Seasons.CentreDays[0];
        var steps = (int)(24 * 3600 / LookDirector.ClockUpdateSeconds);
        var last = LandSeason.At(LookClock.At(preset, 0f, winterDay)).FrostTime;
        for (var step = 1; step <= steps; step++)
        {
            var now = LandSeason.At(LookClock.At(preset, step * 24f / steps, winterDay)).FrostTime;
            jumps = Mathf.Max(jumps, Mathf.Abs(now - last));
            last = now;
        }
        Check(jumps < 0.01f, $"through a winter day the frost changes smoothly: at the look's {LookDirector.ClockUpdateSeconds:0} s updates its largest step is {jumps:0.0000}");
    }

    /// <summary>The colours: autumn unmistakable, spring fresh, winter gentle and pale, conifers green; the summer references are the harness's own.</summary>
    private void CheckSeasonColours()
    {
        var harness = System.IO.Path.GetFullPath(System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "../pipeline/landscape/harness/common.py"));
        var text = System.IO.File.Exists(harness) ? System.IO.File.ReadAllText(harness) : "";
        if (text.Length == 0) GD.Print("LOOK_INFO: harness palette not found beside the project; the season references are not compared with it");
        Vector3 Palette(string role)
        {
            var match = Regex.Match(text, $@"'{role}':\s*\(([-.\d]+),\s*([-.\d]+),\s*([-.\d]+)\)");
            Check(match.Success, $"the harness palette names {role}");
            return match.Success ? new Vector3(float.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), float.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture)) : Vector3.Zero;
        }
        if (text.Length > 0)
            Check(LandSeason.Tuning.FoliageReference == Palette("foliage") && LandSeason.Tuning.MeadowReference == Palette("meadow") && LandSeason.Tuning.SnowColor == Palette("snow"),
                "the summer the season's colours are measured against is the harness palette's foliage and meadow, and a dusting of snow is its snow");
        static float Luma(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;
        static float Chroma(Vector3 c) => Mathf.Max(c.X, Mathf.Max(c.Y, c.Z)) - Mathf.Min(c.X, Mathf.Min(c.Y, c.Z));
        var leaf = LandSeason.Tuning.FoliageReference;
        var gold = LandSeason.Tuning.Gold;
        var rust = LandSeason.Tuning.Rust;
        Check(gold.X > gold.Y && gold.Y > gold.Z && Luma(gold) > 1.2f * Luma(leaf) && rust.X > 2.5f * rust.Y && rust.Y > rust.Z,
            "autumn is unmistakable: its gold is warmer and brighter than the summer leaf, its rust red-brown");
        var fresh = LandSeason.Tuning.Fresh;
        Check(fresh.Y > fresh.X && fresh.X / fresh.Y > leaf.X / leaf.Y && Luma(fresh) > Luma(leaf), "spring leaves are a fresher, lighter yellow-green than summer's");
        var pale = LandSeason.Tuning.Pale;
        Check(Chroma(pale) < 0.5f * Chroma(leaf) && Luma(pale) >= Luma(leaf) * 0.95f && pale.Y >= pale.X && pale.Y >= pale.Z, "winter leaves are pale and thin in colour, still a little green");
        var evergreen = LandSeason.Tuning.Evergreen;
        Check(evergreen.Y > evergreen.X && evergreen.Y > evergreen.Z && Luma(evergreen) < Luma(leaf) && Luma(evergreen) > 0.7f * Luma(leaf),
            "a conifer stays green in winter, a touch darker");
        var meadow = LandSeason.Tuning.MeadowReference;
        Check(LandSeason.Tuning.MeadowFresh.Y / LandSeason.Tuning.MeadowFresh.X > meadow.Y / meadow.X && LandSeason.Tuning.MeadowStraw.X > LandSeason.Tuning.MeadowStraw.Z * 2f
            && LandSeason.Tuning.Straw.X > LandSeason.Tuning.Straw.Z * 2f, "the meadow is greenest in spring and its straw, like the grass's, is a warm pale yellow");
        var all = new[] { fresh, gold, rust, pale, evergreen, LandSeason.Tuning.Straw, LandSeason.Tuning.MeadowStraw, LandSeason.Tuning.MeadowFresh, LandSeason.Tuning.Blossom, LandSeason.Tuning.Frost, LandSeason.Tuning.SnowColor };
        Check(all.All(c => c.X is > 0f and <= 1f && c.Y is > 0f and <= 1f && c.Z is > 0f and <= 1f) && Luma(LandSeason.Tuning.Blossom) > 0.6f && Luma(LandSeason.Tuning.Frost) > 0.6f,
            "every season colour is a real albedo, blossom and frost pale");
    }

    /// <summary>The land shader's globals and the foliage's saturation: declared, typed, read, and set; nothing in the light reads them.</summary>
    private void CheckSeasonShaders()
    {
        var code = Regex.Replace(Regex.Replace(GD.Load<Shader>(LandscapeLook.ShaderPath).Code, @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");
        var declared = Regex.Matches(code, @"^\s*global\s+uniform\s+(\w+)\s+(season_\w+)\s*;", RegexOptions.Multiline).Select(m => (Name: m.Groups[2].Value, Type: m.Groups[1].Value)).ToArray();
        string TypeOf(RenderingServer.GlobalShaderParameterType type) => type switch
        {
            RenderingServer.GlobalShaderParameterType.Vec4 => "vec4", RenderingServer.GlobalShaderParameterType.Vec3 => "vec3", _ => type.ToString(),
        };
        var expected = LandSeason.Globals.Select(g => (g.Name, Type: TypeOf(g.Type))).ToArray();
        Check(declared.SequenceEqual(expected), "the land shader declares every season global LandSeason registers, with its type, and no other: "
            + string.Join(",", declared.Select(d => d.Type + " " + d.Name)));
        var body = Regex.Replace(code, @"^\s*(?:(?:instance|global)\s+)?uniform\s+[^;]*;", "", RegexOptions.Multiline);
        var unread = expected.Where(g => !Regex.IsMatch(body, $@"\b{g.Name}\b")).Select(g => g.Name).ToArray();
        Check(unread.Length == 0, "the land shader reads every season global: unread " + string.Join(",", unread));
        Check(Regex.IsMatch(code, $@"^\s*instance\s+uniform\s+float\s+{LandSeason.HabitParameter}\s*=\s*0\.0\s*;", RegexOptions.Multiline),
            "a plant's habit is a per-instance number of the land shader, a broadleaf's by default");
        var light = body[body.IndexOf("void light()", StringComparison.Ordinal)..];
        Check(!light.Contains("season_", StringComparison.Ordinal), "nothing in the land's light reads the season: it is albedo only");
        // The habits as the shader reads them: 1 a conifer's (its winter colour, never a turn), 2 grass's (straw), anything else a broadleaf's.
        var leaf = body[body.IndexOf("vec3 season_foliage(", StringComparison.Ordinal)..];
        leaf = leaf[..leaf.IndexOf("\nvoid ", StringComparison.Ordinal)];
        var evergreen = Regex.Match(leaf, @"if \(habit == (\d)\) \{([^}]*)\}");
        var grass = Regex.Match(leaf, @"else if \(habit == (\d)\) \{([^}]*)\}");
        Check(evergreen.Success && evergreen.Groups[1].Value == ((int)LandSeason.Habit.Evergreen).ToString() && evergreen.Groups[2].Value.Contains("season_evergreen")
            && !evergreen.Groups[2].Value.Contains("season_gold") && grass.Success && grass.Groups[1].Value == ((int)LandSeason.Habit.Grass).ToString() && grass.Groups[2].Value.Contains("season_straw"),
            "the shader's habits are LandSeason's: a conifer only darkens, grass goes to straw");

        // Every global set, to the value the state gives.
        var state = LandSeason.At(SeasonWeights.Between("autumn", "winter", 0.4f), -3f, 100f);
        LandSeason.Apply(state);
        var values = LandSeason.AppliedValues;
        Check(LandSeason.Globals.All(g => values.ContainsKey(g.Name)) && values.Count == LandSeason.Globals.Count, "every season global is set when the land's season is applied");
        Check(values["season_leaf"].AsVector4() == state.Leaf && values["season_ground"].AsVector4() == state.Ground && values["season_tone"].AsVector4() == state.Tone
            && values["season_gold"].AsVector3() == LandSeason.Ratio(LandSeason.Tuning.Gold, LandSeason.Tuning.FoliageReference)
            && values["season_meadow_straw"].AsVector3() == LandSeason.Ratio(LandSeason.Tuning.MeadowStraw, LandSeason.Tuning.MeadowReference)
            && values["season_snow"].AsVector3() == LandSeason.Tuning.SnowColor && values["season_bloom"].AsVector4() == new Vector4(LandSeason.Tuning.BlossomTrees, LandSeason.Tuning.BlossomDabs, 0f, 0f)
            && LandSeason.Applied == state,
            "the globals carry the state, the season colours over their summer, the snow as it is");
        Check(LandSeason.RegisteredHere == !LandSeason.Globals.All(g => ProjectSettings.HasSetting("shader_globals/" + g.Name)),
            $"the globals are registered at run time, project.godot untouched (registered here: {LandSeason.RegisteredHere})");

        // The room's own trees: the live oak's saturation follows the season, the juniper and the bunchgrass keep their own.
        var foliage = Regex.Replace(GD.Load<Shader>("res://shaders/painterly_foliage.gdshader").Code, @"//[^\n]*", "");
        Check(Regex.IsMatch(foliage, @"uniform\s+float\s+season_saturation[^;]*=\s*1\.0\s*;") && Regex.IsMatch(foliage, @"mix\([^;]*,\s*color,\s*season_saturation\)"),
            "the room trees' leaf shader takes a saturation, 1 as painted");
        var trees = GD.Load<GDScript>(LandSeason.RoomTreesScript);
        var oak = (ShaderMaterial)trees.Call("foliage_material", "oak");
        var juniper = (ShaderMaterial)trees.Call("foliage_material", "juniper");
        var grassCover = (ShaderMaterial)trees.Call("groundcover_material");
        LandSeason.Apply(LandSeason.At(SeasonWeights.Only("winter"), 20f, 250f));
        var winterOak = oak.GetShaderParameter("season_saturation").AsSingle();
        LandSeason.Apply(LandSeason.At(SeasonWeights.Only("spring"), 20f, 250f));
        var springOak = oak.GetShaderParameter("season_saturation").AsSingle();
        Check(Mathf.IsEqualApprox(winterOak, LandSeason.Tuning.Looks["winter"].RoomOakSaturation) && Mathf.IsEqualApprox(springOak, LandSeason.Tuning.Looks["spring"].RoomOakSaturation)
            && winterOak < 1f && springOak > 1f && Mathf.Abs(winterOak - 1f) <= 0.2f && Mathf.Abs(springOak - 1f) <= 0.2f,
            $"the room's live oak shifts gently: a little less saturated in winter ({winterOak:0.00}), a little more in spring ({springOak:0.00})");
        Check(juniper.GetShaderParameter("season_saturation").AsSingle() == 1f && grassCover.GetShaderParameter("season_saturation").VariantType == Variant.Type.Nil,
            "the Ashe juniper does not change, and the bunchgrass keeps the shader's own");
        var later = (ShaderMaterial)trees.Call("foliage_material", "live_oak");
        Check(Mathf.IsEqualApprox(later.GetShaderParameter("season_saturation").AsSingle(), springOak), "oak leaves made after the season was set take it too");
        LandSeason.Apply(LandSeason.At(SeasonWeights.Only("summer"), 20f, 250f));
        Check(oak.GetShaderParameter("season_saturation").AsSingle() == 1f, "in summer the oak is as painted");
    }

    /// <summary>Which way each plant set's leaves go: conifers evergreen, grass and herbs to straw, broadleaves and shrubs turn.</summary>
    private void CheckSeasonHabits()
    {
        Check(LandSeason.HabitFor("conifer") == LandSeason.Habit.Evergreen && LandSeason.HabitFor("grass") == LandSeason.Habit.Grass && LandSeason.HabitFor("herb") == LandSeason.Habit.Grass
            && new[] { "broadleaf", "shrub", "none", "" }.All(f => LandSeason.HabitFor(f) == LandSeason.Habit.Deciduous),
            "a conifer is evergreen, grass and the low herbs go to straw, and a broadleaf, a shrub or anything else turns");
        Check((int)LandSeason.Habit.Deciduous == 0, "a broadleaf's habit is the shader's default, so foliage nobody marks turns");
    }

    /// <summary>
    /// The light never reads the land's season: at every minute of four days, one per season, the grade and the sky the moment makes are
    /// the same whatever the weights say.
    /// </summary>
    private void CheckSeasonLightUntouched(StylePreset preset)
    {
        var differ = 0;
        var seen = 0;
        foreach (var centre in preset.Tuning.Seasons.CentreDays)
            for (var minute = 0; minute < 24 * 60; minute++)
            {
                var moment = LookClock.At(preset, minute / 60f, centre);
                var other = moment with { Weights = SeasonWeights.Only(SeasonOrder[(Array.IndexOf(SeasonOrder, moment.Season) + 2) % 4]) };
                seen++;
                if (!Equals(GradeParams.For(preset, moment), GradeParams.For(preset, other)) || LookSky.At(preset, moment, null) != LookSky.At(preset, other, null)) differ++;
            }
        Check(seen == 4 * 1440 && differ == 0, $"the grade and the sky read nothing of the land's season: {seen} minutes of four days, {differ} differ");
    }

    /// <summary>What a look lights the room with: the key, the ambient, the sky's own parameters, the lamps.</summary>
    private static string Light(LookDirector look)
    {
        var sky = look.Environment.Sky?.SkyMaterial as ShaderMaterial;
        var skyText = sky == null ? "" : string.Join(";", sky.Shader.GetShaderUniformList().Select(u => u.AsGodotDictionary()["name"].AsString()).Order()
            .Select(n => (Name: n, Value: sky.GetShaderParameter(n))).Where(p => p.Value.VariantType != Variant.Type.Object).Select(p => p.Name + "=" + p.Value));
        return string.Join("|", look.Key.LightEnergy, look.Key.LightColor, look.Key.RotationDegrees, look.Key.ShadowBlur, look.Environment.AmbientLightColor,
            look.Environment.AmbientLightEnergy, look.LampsOn, look.Moment.KeyEnergy, look.Moment.KeyColor, skyText);
    }

    /// <summary>In the look: the clock, its pins and an island's world clock drive the land's season; the light stays the moment's own.</summary>
    private async Task CheckSeasonsInTheLook(StylePreset preset, RoomData room)
    {
        var savedClock = OS.GetEnvironment("ENFRACTAL_LOOK_CLOCK");
        var savedDate = OS.GetEnvironment("ENFRACTAL_LOOK_DATE");
        var savedUtc = LookClock.UtcClock;
        OS.UnsetEnvironment("ENFRACTAL_LOOK_CLOCK");
        OS.UnsetEnvironment("ENFRACTAL_LOOK_DATE");
        try
        {
            var (holder, look) = NewDirector(preset, room, "SeasonsHolder");
            var (twinHolder, twin) = NewDirector(preset, room, "SeasonsTwinHolder");
            await Frames(2);
            for (var i = 0; i < 4; i++)
            {
                var centre = preset.Tuning.Seasons.CentreDays[i];
                look.SetClock(17f, centre);
                var applied = LandSeason.Applied;
                Check(applied is { } a && a.Weights == SeasonWeights.Only(SeasonOrder[i]) && a == LandSeason.At(look.Moment),
                    $"the look sets the land to {SeasonOrder[i]} at {SeasonOrder[i]}'s middle (day {centre})");
                // The same moment in a twin, then the land's season set to something else entirely: the light is the moment's, untouched.
                twin.SetClock(17f, centre);
                var lut = twin.Environment.AdjustmentColorCorrection;
                var key = Light(twin);
                LandSeason.Apply(LandSeason.At(SeasonWeights.Only(SeasonOrder[(i + 2) % 4]), -10f, 0f));
                // Each look keeps its own grade textures, so the twin's grade is compared with itself and the two looks' with the moment's.
                Check(key == Light(twin) && key == Light(look) && ReferenceEquals(lut, twin.Environment.AdjustmentColorCorrection) && twin.GradeCurrent && look.GradeCurrent
                    && Equals(GradeParams.For(twin.Preset, twin.Moment, twin.LampsOn), GradeParams.For(look.Preset, look.Moment, look.LampsOn)),
                    $"at {SeasonOrder[i]}'s middle the sun, the ambient, the sky, the grade and the lamps are the moment's whatever the land's season");
            }
            twinHolder.QueueFree();
            // The test room, built and dressed, wears no material that reads the season: its frame is today's in every season.
            var built = RoomBuilder.Build(room);
            holder.AddChild(built);
            await Frames(3);
            var meshes = built.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>().ToArray();
            var readers = meshes
                .SelectMany(m => Enumerable.Range(0, m.Mesh?.GetSurfaceCount() ?? 0).Select(m.GetActiveMaterial).Append(m.MaterialOverride))
                .OfType<ShaderMaterial>().Count(s => s.Shader?.ResourcePath is LandscapeLook.ShaderPath or "res://shaders/painterly_foliage.gdshader");
            Check(meshes.Length >= room.Shell.Count + room.Objects.Count && meshes.All(m => m.HasMeta(LookDirector.DressedMeta)) && readers == 0,
                $"the test room ({meshes.Length} meshes, dressed) wears no material that reads the season ({readers}): its pixels are today's in every season");
            look.ReleaseClock();
            Check(LandSeason.Applied == LandSeason.At(look.Moment), "following the clock again, the land follows it too");
            holder.QueueFree();

            // The pins: --look-date (here its environment form) sets the land's season as it sets the sky's.
            foreach (var (date, season) in new[] { ("2026-01-15", "winter"), ("2026-04-15", "spring"), ("2026-07-15", "summer"), ("2026-10-15", "autumn") })
            {
                OS.SetEnvironment("ENFRACTAL_LOOK_DATE", date);
                var (pinnedHolder, pinned) = NewDirector(preset, room, "SeasonPinHolder");
                Check(pinned.Moment.Season == season && LandSeason.Applied is { } p && p.Weights == SeasonWeights.Only(season) && p == LandSeason.At(pinned.Moment),
                    $"ENFRACTAL_LOOK_DATE={date} gives the land {season} ({pinned.ClockNote})");
                pinnedHolder.QueueFree();
            }
            OS.UnsetEnvironment("ENFRACTAL_LOOK_DATE");

            // An island's own clock: four game days a real day, so a game year passes in about 91 real days and its seasons with it.
            var instant = new DateTime(2026, 1, 15, 3, 0, 0, DateTimeKind.Utc);
            LookClock.UtcClock = () => instant;
            var (worldHolder, world) = NewDirector(preset, room, "SeasonWorldClockHolder");
            Check(world.SetWorldClockFrom(JsonNode.Parse("""{"zone_id":"Etc/UTC","day_scale":4,"seasons_per_year":4,"phase_h":0,"hemisphere":"north"}""")).Length == 0,
                "the look takes a Live clock record");
            var first = LandSeason.Applied!.Value;
            var firstDay = world.Moment.DayOfYear;
            Check(first.Weights == LookClock.WeightsAt(world.Preset, LookClock.Now(world.Preset, world.WorldClock).DayOfYear) && first == LandSeason.At(world.Moment),
                $"the island's clock sets the land's season by the day it reads (day {firstDay}, {world.Moment.Season})");
            instant = instant.AddDays(45.6);
            world._Process(11.0);
            var second = LandSeason.Applied!.Value;
            Check(world.Moment.DayOfYear != firstDay && second == LandSeason.At(world.Moment) && second.Weights != first.Weights
                && second.Weights == LookClock.WeightsAt(world.Preset, world.Moment.DayOfYear),
                $"and as its year passes in play the land's season goes with it (day {world.Moment.DayOfYear}, {world.Moment.Season}, from {firstDay})");
            worldHolder.QueueFree();
        }
        finally
        {
            LookClock.UtcClock = savedUtc;
            if (savedClock.Length > 0) OS.SetEnvironment("ENFRACTAL_LOOK_CLOCK", savedClock); else OS.UnsetEnvironment("ENFRACTAL_LOOK_CLOCK");
            if (savedDate.Length > 0) OS.SetEnvironment("ENFRACTAL_LOOK_DATE", savedDate); else OS.UnsetEnvironment("ENFRACTAL_LOOK_DATE");
        }
        await Frames(1);
    }
}
