using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using GarageGame.Native.Characters;

namespace GarageGame.Tests.Characters;

/// <summary>
/// K1's suite (docs/runs/RUN-2-CHARACTERS.md, acceptance item 1, the rig's part): the library loads the five shipped
/// characters and refuses malformed folders with reasons; each rig matches its character.json bone for bone; the
/// role table, the chains, the look-ats, the leg IK and the motion profiles are what the geometry implies; the pose
/// API orders its layers, locks rigid parts and hashes deterministically; the load fits its 150 ms budget.
/// Headless, --fixed-fps 60. Run: res://tests/native_characters.tscn (add "-- --skip-budget" where wall time means nothing).
/// </summary>
public partial class CharacterRigTest : Node3D
{
    private static readonly string[] Shipped = { "cloudpuff", "gubble", "potato_man", "stickbear", "tomato_man" };
    private int _checks;
    private int _failures;
    private CharacterLibrary _library = null!;
    private double _loadMs;
    private double _buildMs;
    private double _cpuMs;
    private double _staticMb;
    private double _managedMb;
    private double _warmMs;

    public override async void _Ready()
    {
        try
        {
            TestLoadBudget();
            TestRoleGrammar();
            foreach (var id in Shipped) TestShippedCharacter(id);
            TestRoleTable();
            TestChainsLooksAndLegs();
            TestProfiles();
            TestFreshCopies();
            TestMatchesGodotGltf();
            TestMalformedFolders();
            TestPoseBuffer();
            await TestRestInTheTree();
            await TestDeterministicHashes();
            await TestSpringFrequency();
            await TestSpringsOnThePhysicsTick();
            await TestLegIk();
            await TestLookAt();
            GD.Print(FormattableString.Invariant(
                $"CHARACTERS_LOAD: five characters read, validated and rigged in {_loadMs + _buildMs:0.0} ms (load {_loadMs:0.0}, build {_buildMs:0.0}) after the engine's boot-time first uses ({_warmMs:0.0} ms), {_cpuMs:0.0} ms of processor time; static memory +{_staticMb:0.00} MB, managed +{_managedMb:0.00} MB"));
            GD.Print($"NATIVE_CHARACTERS: {_checks - _failures}/{_checks} checks passed; rig and library: five characters, roles, chains, look-at, leg IK, motion profiles, refusals, pose hashes; not the gaits, the look or the feel");
            GetTree().Quit(_failures == 0 ? 0 : 1);
        }
        catch (Exception exception)
        {
            GD.PushError("Native characters test exception: " + exception);
            GetTree().Quit(1);
        }
    }

    private void Check(bool condition, string label)
    {
        _checks++;
        if (condition) return;
        _failures++;
        GD.Print("FAIL " + label);
    }

    private Task Frames(int count)
    {
        var source = new TaskCompletionSource();
        var remaining = count;
        void Tick()
        {
            if (--remaining > 0) return;
            GetTree().ProcessFrame -= Tick;
            source.SetResult();
        }
        GetTree().ProcessFrame += Tick;
        return source.Task;
    }

    private CharacterBody Build(string id) => _library.Build(id) ?? throw new InvalidOperationException($"{id} did not build");

    /// <summary>The grammar on names alone: head nouns, plurals, qualifiers, sides, compounds and the effect fallback.</summary>
    private void TestRoleGrammar()
    {
        (string Name, PartKind Kind, EffectMotion Effect, PartRole Role)[] cases =
        {
            ("eye_glint_screen_left", PartKind.Solid, EffectMotion.None, PartRole.Glint),
            ("digit_pad_left_0", PartKind.Solid, EffectMotion.None, PartRole.Digit),
            ("horn_bands_left", PartKind.Solid, EffectMotion.None, PartRole.Mark),
            ("hat_band", PartKind.Solid, EffectMotion.None, PartRole.HatBand),
            ("arch_red", PartKind.Solid, EffectMotion.None, PartRole.Arch),
            ("stripes_screen_right", PartKind.Solid, EffectMotion.None, PartRole.Mark),
            ("cheeks", PartKind.Solid, EffectMotion.None, PartRole.Cheek),
            ("hairline", PartKind.Solid, EffectMotion.None, PartRole.Hair),
            ("eyebrow_screen_left", PartKind.Solid, EffectMotion.None, PartRole.Brow),
            ("tail_1", PartKind.Solid, EffectMotion.None, PartRole.Tail),
            ("wing_screen_left", PartKind.Solid, EffectMotion.None, PartRole.Decor),
            ("wisp", PartKind.Effect, EffectMotion.Flicker, PartRole.Flame),
            ("wisp", PartKind.Effect, EffectMotion.Pulse, PartRole.Aura),
            ("wisp", PartKind.Effect, EffectMotion.Orbit, PartRole.Sparkle),
            ("torch_flame", PartKind.Effect, EffectMotion.Flicker, PartRole.Flame),
        };
        foreach (var (name, kind, effect, role) in cases)
            Check(PartRoles.Infer(name, kind, effect) == role, $"grammar: {name} ({kind}, {effect}) is {PartRoles.Token(role)}, got {PartRoles.Token(PartRoles.Infer(name, kind, effect))}");
        Check(PartRoles.SideFromName("arm_screen_left") == BodySide.Right && PartRoles.SideFromName("claw_right_3") == BodySide.Left && PartRoles.SideFromName("mouth") == null,
            "grammar: screen_left (and a bare left) is the character's right, +X; no side word is no side");
        var tokens = Enum.GetValues<PartRole>().Select(PartRoles.Token).ToArray();
        Check(tokens.Distinct().Count() == tokens.Length && tokens.All(t => PartRoles.TryParse(t, out var r) && PartRoles.Token(r) == t) &&
              PartRoles.Token(PartRole.HatBand) == "hat_band" && !PartRoles.TryParse("Hat", out _),
            "grammar: every role has one snake_case rig.json token that parses back");
    }

    /// <summary>
    /// What the game has already done at boot before it loads characters (the room's load parses JSON, lists and reads
    /// files, builds meshes): the engine's and the runtime's own first uses, none of the character code. Without it,
    /// a fresh process also pays those first uses inside the window (measured 133 to 145 ms on the founder's machine).
    /// </summary>
    private double WarmEngineLikeBoot()
    {
        var clock = Stopwatch.StartNew();
        using (var json = JsonDocument.Parse("{\"room\": [1, 2.5, \"x\", null, true]}")) _ = json.RootElement.GetProperty("room").GetArrayLength();
        using (var directory = DirAccess.Open("res://")) _ = directory?.GetDirectories().Length;
        using (var file = Godot.FileAccess.Open("res://project.godot", Godot.FileAccess.ModeFlags.Read)) _ = file?.GetBuffer((long)file.GetLength()).Length;
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = new[] { Vector3.Zero, Vector3.Up, Vector3.Right };
        arrays[(int)Mesh.ArrayType.Normal] = new[] { Vector3.Back, Vector3.Back, Vector3.Back };
        arrays[(int)Mesh.ArrayType.Index] = new[] { 0, 1, 2 };
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetMaterial(0, new StandardMaterial3D { AlbedoColor = new Color(0.5f, 0.5f, 0.5f) });
        var node = new Node3D { Name = "Warm" };
        node.AddChild(new MeshInstance3D { Name = "Mesh", Mesh = mesh, Transform = Transform3D.Identity });
        node.Free();
        return clock.Elapsed.TotalMilliseconds;
    }

    private void TestLoadBudget()
    {
        _warmMs = WarmEngineLikeBoot();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var managedBefore = GC.GetTotalMemory(true);
        var staticBefore = OS.GetStaticMemoryUsage();
        var process = System.Diagnostics.Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var clock = Stopwatch.StartNew();
        _library = CharacterLibrary.Load(CharacterLibrary.ShippedRoot);
        _loadMs = clock.Elapsed.TotalMilliseconds;
        clock.Restart();
        var bodies = Shipped.Select(id => _library.Build(id)).ToList();
        _buildMs = clock.Elapsed.TotalMilliseconds;
        process.Refresh();
        _cpuMs = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
        _staticMb = (OS.GetStaticMemoryUsage() - staticBefore) / 1048576.0;
        _managedMb = (GC.GetTotalMemory(false) - managedBefore) / 1048576.0;
        Check(_library.Entries.Count == 5 && _library.Characters.Select(c => c.Id).SequenceEqual(Shipped),
            "the library finds the five shipped characters, sorted: " + string.Join(", ", _library.Entries.Select(e => e.Id + (e.IsLoaded ? "" : " REFUSED " + e.RefusalReason))));
        Check(bodies.All(b => b != null), "all five build");
        // The budget is the founder's machine's; "-- --skip-budget" keeps a slow cloud container from failing on wall time.
        if (!OS.GetCmdlineUserArgs().Contains("--skip-budget"))
        {
            // Judged on the processor time the load used, which equals the wall time on an idle machine but does not grow while other
            // programs take the processor (four agents' Godot runs failed the wall-clock check on 13 October); both are printed.
            Check(_cpuMs < 150, FormattableString.Invariant($"all five read, validated and rigged within 150 ms of processor time ({_cpuMs:0.0} ms; {_loadMs + _buildMs:0.0} ms wall)"));
            Check(_staticMb + _managedMb < 25, FormattableString.Invariant($"the library and five bodies take under 25 MB ({_staticMb + _managedMb:0.00} MB)"));
        }
        foreach (var body in bodies) body?.Free();
    }

    /// <summary>Bone for part, rest for pivot, against an independent read of the shipped character.json.</summary>
    private void TestShippedCharacter(string id)
    {
        var json = JsonDocument.Parse(Godot.FileAccess.GetFileAsBytes($"res://characters/{id}/character.json")).RootElement;
        var parts = json.GetProperty("parts").EnumerateArray().ToArray();
        var body = Build(id);
        var definition = body.Definition;
        Check(definition.Parts.Count == parts.Length && body.BoneCount == parts.Length + 1 && body.Skeleton.GetBoneCount() == parts.Length + 1,
            $"{id}: {parts.Length} parts give {parts.Length + 1} bones (the root and one per part); got {body.Skeleton.GetBoneCount()}");
        Check(body.BoneCount <= CharacterFolder.MaxBones, $"{id}: within the 64-bone budget");
        Check(body.Skeleton.GetBoneName(0) == "character_root" && body.Skeleton.GetBoneParent(0) == -1 && body.Skeleton.GetBoneRest(0).Origin == Vector3.Zero,
            $"{id}: the root bone sits at root_pivot_m, on the ground");
        var worst = 0.0;
        var named = true;
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            var bone = i + 1;
            named &= body.Skeleton.GetBoneName(bone) == part.GetProperty("name").GetString();
            var parentName = part.GetProperty("parent").ValueKind == JsonValueKind.Null ? "character_root" : part.GetProperty("parent").GetString();
            named &= body.Skeleton.GetBoneName(body.Skeleton.GetBoneParent(bone)) == parentName;
            var local = part.GetProperty("local_pivot_m").EnumerateArray().Select(v => v.GetDouble()).ToArray();
            var pivot = part.GetProperty("pivot_m").EnumerateArray().Select(v => v.GetDouble()).ToArray();
            var rest = body.Skeleton.GetBoneRest(bone);
            var global = body.Skeleton.GetBoneGlobalRest(bone);
            named &= rest.Basis.IsEqualApprox(Basis.Identity);
            for (var axis = 0; axis < 3; axis++)
            {
                worst = Math.Max(worst, Math.Abs(rest.Origin[axis] - local[axis]));
                worst = Math.Max(worst, Math.Abs(global.Origin[axis] - pivot[axis]));
            }
        }
        Check(named, $"{id}: every bone is named and parented as its part, with no rest rotation");
        Check(worst <= 1e-6, FormattableString.Invariant($"{id}: rest pivots match character.json within 1e-6 (worst {worst:E2})"));
        Check(Math.Abs(body.RestBounds.Position.Y) <= 1e-5f && Math.Abs(body.RestBounds.End.Y - 0.10f) <= 1e-5f && Math.Abs(body.LowestPointY()) <= 1e-5f,
            FormattableString.Invariant($"{id}: the rig stands 0.10 m tall with its feet at y=0 ({body.RestBounds.Position.Y:0.000000} to {body.RestBounds.End.Y:0.000000})"));
        var meshes = Enumerable.Range(0, body.BoneCount).Count(b => body.PartMesh(b) != null);
        var expectedMeshes = parts.Count(p => definition.Parts[definition.FindPart(p.GetProperty("name").GetString()!)].Mesh != null);
        Check(meshes == expectedMeshes && meshes == definition.Parts.Count(p => p.Mesh != null) &&
              definition.Parts.Sum(p => p.Triangles) == json.GetProperty("triangles").GetInt32(),
            $"{id}: every part's mesh rides its bone, and the triangles add up to character.json's {json.GetProperty("triangles").GetInt32()}");
        Check(definition.GlbSha256.Length == 64 && definition.HasSidecar, $"{id}: the GLB is hashed and the sidecar read");
        body.Free();
    }

    /// <summary>The roles each shipped character gets (side: c centre, r the character's right +X, l its left).</summary>
    private static readonly Dictionary<string, string> RoleTable = new()
    {
        ["gubble"] = "body:body:c eye_screen_left:eye:r eye_screen_right:eye:l mouth:mouth:c arm_screen_right:arm:l hand_screen_right:hand:l " +
            "aura:aura:c floating_sparkle_0:sparkle:c floating_sparkle_1:sparkle:c floating_sparkle_2:sparkle:c floating_sparkle_3:sparkle:c",
        ["potato_man"] = "body:body:c hair:hair:c hairline:hair:c hat:hat:c hat_band:hat_band:c eye_screen_left:eye:r eye_screen_right:eye:l nose:nose:c " +
            "nostrils:mark:c cheeks:cheek:c mouth:mouth:c arm_screen_left:arm:r hand_screen_left:hand:r arm_screen_right:arm:l hand_screen_right:hand:l " +
            "shoe_screen_left:shoe:r straps_screen_left:mark:r shoe_screen_right:shoe:l straps_screen_right:mark:l",
        ["tomato_man"] = "body:body:c calyx:calyx:c hat:hat:c hat_band:hat_band:c eye_screen_left:eye:r eye_screen_right:eye:l mouth:mouth:c " +
            "arm_screen_left:arm:r hand_screen_left:hand:r wand:wand:r wand_sparkle_0:sparkle:r wand_sparkle_1:sparkle:r wand_sparkle_2:sparkle:r " +
            "arm_screen_right:arm:l hand_screen_right:hand:l foot_screen_left:foot:r stripes_screen_left:mark:r foot_screen_right:foot:l stripes_screen_right:mark:l " +
            "floating_sparkle_0:sparkle:c floating_sparkle_1:sparkle:c floating_sparkle_2:sparkle:c floating_sparkle_3:sparkle:c floating_sparkle_4:sparkle:c",
        ["cloudpuff"] = "body:body:c arch_red:arch:c arch_orange:arch:c arch_yellow:arch:c arch_green:arch:c arch_blue:arch:c arch_indigo:arch:c arch_violet:arch:c " +
            "arm_screen_left:arm:r hand_screen_left:hand:r arm_screen_right:arm:l hand_screen_right:hand:l leg_screen_left:leg:r foot_screen_left:foot:r " +
            "leg_screen_right:leg:l foot_screen_right:foot:l eye_screen_left:eye:r pupil_screen_left:pupil:r lid_screen_left:lid:r eye_screen_right:eye:l " +
            "pupil_screen_right:pupil:l lid_screen_right:lid:l brow_screen_left:brow:r brow_screen_right:brow:l nose:nose:c mouth:mouth:c " +
            "cheek_screen_left:cheek:r cheek_screen_right:cheek:l",
        ["stickbear"] = "body:body:c head:head:c horn_screen_left:horn:r horn_screen_right:horn:l horn_bands_left:mark:r horn_bands_right:mark:l " +
            "arm_screen_left:arm:r arm_screen_right:arm:l paw_screen_left:paw:r paw_screen_right:paw:l heart_screen_left:heart:r heart_screen_right:heart:l " +
            "digit_pad_left_0:digit:r digit_pad_left_1:digit:r digit_pad_left_2:digit:r digit_pad_right_0:digit:l digit_pad_right_1:digit:l digit_pad_right_2:digit:l " +
            "digit_pad_right_3:digit:l claw_left_0:digit:r claw_left_1:digit:r claw_left_2:digit:r claw_left_3:digit:r claw_left_4:digit:r claw_right_0:digit:l " +
            "claw_right_1:digit:l claw_right_2:digit:l claw_right_3:digit:l claw_right_4:digit:l leg_screen_left:leg:r leg_screen_right:leg:l " +
            "foot_screen_left:foot:r foot_screen_right:foot:l eye_screen_left:eye:r eye_glint_screen_left:glint:r eye_screen_right:eye:l " +
            "eye_glint_screen_right:glint:l mouth:mouth:c",
    };

    private void TestRoleTable()
    {
        foreach (var id in Shipped)
        {
            var body = Build(id);
            var expected = RoleTable[id].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var actual = body.Bones.Skip(1).Select(b => $"{b.Name}:{PartRoles.Token(b.Role)}:{b.Side switch { BodySide.Right => "r", BodySide.Left => "l", _ => "c" }}").ToArray();
            var mismatches = expected.Zip(actual).Where(p => p.First != p.Second).Select(p => $"{p.First} got {p.Second}").ToArray();
            Check(expected.Length == actual.Length && mismatches.Length == 0, $"{id}: the role table ({string.Join("; ", mismatches.Take(4))})");
            Check(body.Bones.All(b => b.IsRigid == (b.Role == PartRole.Decor && b.Part >= 0)), $"{id}: only décor is rigid (rig.json marks nothing rigid here)");
            var sidesAgree = body.Bones.Where(b => b.Side != BodySide.Center && b.Part >= 0 && Math.Abs(b.RestModel.X - body.Bones[body.BodyBone].RestModel.X) > 0.003f)
                .All(b => (b.Side == BodySide.Right) == (b.RestModel.X > body.Bones[body.BodyBone].RestModel.X));
            Check(sidesAgree, $"{id}: every sided part off the midline sits on its side (the character's right is +X)");
            body.Free();
        }
    }

    /// <summary>Which chains, look-ats and leg IKs each character gets, inside the budgets.</summary>
    private void TestChainsLooksAndLegs()
    {
        var expectations = new Dictionary<string, (string Chains, string Look, string Legs)>
        {
            ["gubble"] = ("arm_screen_right>hand_screen_right floating_sparkle_1 floating_sparkle_3", "body eye_screen_left eye_screen_right", ""),
            ["potato_man"] = ("hat hair>hairline arm_screen_left>hand_screen_left arm_screen_right>hand_screen_right", "body eye_screen_left eye_screen_right", ""),
            ["tomato_man"] = ("hat foot_screen_left foot_screen_right wand", "body eye_screen_left eye_screen_right", ""),
            ["cloudpuff"] = ("leg_screen_left>foot_screen_left leg_screen_right>foot_screen_right", "body eye_screen_left eye_screen_right", "leg_screen_left>foot_screen_left leg_screen_right>foot_screen_right"),
            ["stickbear"] = ("horn_screen_left horn_screen_right", "head eye_screen_left eye_screen_right", "leg_screen_left>foot_screen_left leg_screen_right>foot_screen_right"),
        };
        foreach (var id in Shipped)
        {
            var body = Build(id);
            var chains = string.Join(" ", body.Secondary.Chains.Select(c => string.Join(">", c.Bones.Select(b => body.Bones[b].Name))));
            var looks = new List<string>();
            if (body.Look.Head != null) looks.Add(body.Bones[body.Look.Head.Bone].Name);
            looks.AddRange(body.Look.Eyes.Select(e => body.Bones[e.Bone].Name));
            var legs = string.Join(" ", body.Legs.Select(l => body.Bones[l.HipBone].Name + ">" + body.Bones[l.FootBone].Name));
            var (expectedChains, expectedLook, expectedLegs) = expectations[id];
            Check(chains == expectedChains, $"{id}: spring chains are [{expectedChains}], got [{chains}]");
            Check(string.Join(" ", looks) == expectedLook, $"{id}: look-ats are [{expectedLook}], got [{string.Join(" ", looks)}]");
            Check(legs == expectedLegs, $"{id}: leg IK chains are [{expectedLegs}], got [{legs}]");
            Check(body.Secondary.Chains.Count <= 6 && body.Secondary.Chains.All(c => c.Bones.Length <= 6) && body.Legs.Count <= 2 && body.Look.Count <= 3,
                $"{id}: within the budgets (6 chains of 6 bones, 2 IK, 3 look-ats)");
            Check((body.LegSolver == null || !body.LegSolver.Active) && (body.Look.Head == null || !body.Look.Head.Modifier.Active) && body.Look.Eyes.All(e => !e.Modifier.Active),
                $"{id}: leg IK and look-ats are built inactive (the layers turn them on)");
            Check(body.Secondary.Chains.All(c => c.FrequencyHz is >= SecondaryMotion.MinFrequencyHz and <= SecondaryMotion.MaxFrequencyHz && c.DampingRatio > 0 && c.LengthM > 0.002f),
                $"{id}: every chain has a sane frequency, damping and length");
            var collider = body.Secondary.BodyCollider;
            Check(body.Secondary.Chains.Count == 0 || (collider != null && collider.BoneName == body.Bones[body.BodyBone].Name && collider.Radius > 0.003f),
                $"{id}: the chains share a capsule inside the body");
            body.Free();
        }
    }

    private void TestProfiles()
    {
        var stickbear = Build("stickbear");
        var cloud = Build("cloudpuff");
        var potato = Build("potato_man");
        var tomato = Build("tomato_man");
        var gubble = Build("gubble");
        var s = stickbear.Profile;
        var c = cloud.Profile;
        Check(Math.Abs(s.HipHeightM - 0.043f) < 0.001f && s.LegCount == 2, FormattableString.Invariant($"Stickbear's hips stand about 0.043 m high ({s.HipHeightM:0.0000} m)"));
        Check(Math.Abs(s.NaturalCadenceHz - 1.44f) < 0.02f, FormattableString.Invariant($"Stickbear's legs swing at about 1.44 Hz ({s.NaturalCadenceHz:0.000} Hz)"));
        Check(Math.Abs(c.HipHeightM - 0.030f) < 0.001f && c.LegCount == 2, FormattableString.Invariant($"Cloudpuff's hips stand about 0.030 m high ({c.HipHeightM:0.0000} m)"));
        Check(Math.Abs(c.NaturalCadenceHz - 1.72f) < 0.02f, FormattableString.Invariant($"Cloudpuff's legs swing at about 1.72 Hz ({c.NaturalCadenceHz:0.000} Hz)"));
        Check(Math.Abs(s.NaturalCadenceHz - MotionProfile.PendulumHz(s.HipHeightM)) < 1e-5f &&
              Math.Abs(MotionProfile.PendulumHz(0.043f) - Mathf.Sqrt(3.5f / 0.043f) / Mathf.Tau) < 1e-6f, "the cadence is a pendulum's in the game's 3.5 m/s² gravity");
        Check(s.WalkStrideM > c.WalkStrideM && Math.Abs(s.WalkStrideM * s.NaturalCadenceHz - MotionProfile.WalkSpeedMps) < 1e-4f,
            "longer legs take fewer, longer strides at the shared walk speed");
        Check(s.StrideAt(MotionProfile.RunSpeedMps) > s.StrideAt(MotionProfile.WalkSpeedMps) && s.CadenceAt(MotionProfile.RunSpeedMps) > s.CadenceAt(MotionProfile.WalkSpeedMps),
            "a run lengthens the stride and quickens the cadence");
        Check(!potato.Profile.HasLegs && !tomato.Profile.HasLegs && !gubble.Profile.HasLegs && potato.Profile.FootCount == 2 && tomato.Profile.FootCount == 2,
            "Potato Man, Tomato Man and the Gubble have no legs; the first two step on their shoes and feet");
        Check(potato.Profile.LegLengthM == potato.Profile.FootLengthM && potato.Profile.NaturalCadenceHz > s.NaturalCadenceHz,
            "a legless body steps by its footprint, and patters faster than Stickbear");
        Check(s.Gait == MotionKind.Stride && c.Gait == MotionKind.Hop && potato.Profile.Gait == MotionKind.Waddle && gubble.Profile.Gait == MotionKind.Float,
            "each gait is its motion hint");
        var round = new[] { potato, cloud, gubble, tomato };
        Check(round.All(b => b.Profile.Roundness > 0.7f && b.Profile.SquashAmount > 2 * s.SquashAmount) && s.Roundness < 0.3f,
            FormattableString.Invariant($"full, round bodies squash more than flat, lanky Stickbear (roundness {s.Roundness:0.00}, squash {s.SquashAmount:0.000} against {potato.Profile.SquashAmount:0.000})"));
        Check(round.All(b => b.Profile.SettleTimeS > s.SettleTimeS) && round.All(b => b.Profile.VolumeM3 > 4 * s.VolumeM3),
            "bigger, fuller bodies settle slower");
        Check(Math.Abs(s.WidthM - 0.082f) < 0.002f && Math.Abs(s.DepthM - 0.019f) < 0.002f && Math.Abs(tomato.Profile.WidthM - 0.147f) < 0.002f && Math.Abs(c.WidthM - 0.110f) < 0.002f,
            "the measured widths and depths match the plan's table (Stickbear 8.2 x 1.9, Tomato 14.7 wide, Cloudpuff 11.0)");
        // rig.json scales the measurements by name.
        Check(Math.Abs(s.Squash - s.SquashAmount * 0.7f) < 1e-6f && Math.Abs(tomato.Profile.Bounce - 1.1f) < 1e-6f && Math.Abs(c.Multipliers.Drift - 1.6f) < 1e-6f,
            "rig.json's named multipliers reach the profile (Stickbear's little squash, Tomato's bounce, Cloudpuff's drift)");
        var cloudLeg = cloud.Secondary.Chains[0];
        Check(Math.Abs(cloudLeg.FrequencyHz - 0.7f * 2.3f * cloudLeg.PendulumHz) < 1e-4f && c.HangingParts.Count == 2 &&
              Math.Abs(c.HangingParts[0].FrequencyHz - cloudLeg.FrequencyHz) < 1e-6f,
            "a hanging part's spring is its pendulum's frequency times the springiness (Cloudpuff's leg springs, the profile's 0.7 and the chain's 2.3)");
        var hat = potato.Secondary.Chains.First(ch => ch.Kind == ChainKind.Hat);
        var hair = potato.Secondary.Chains.First(ch => ch.Kind == ChainKind.Hair);
        Check(Math.Abs(hat.FrequencyHz - 2.2f * hat.PendulumHz) < 1e-4f && hair.Bones.Length == 2 && hair.DampingRatio < SecondaryMotion.BaseDampingRatio,
            "a chain's own rig.json numbers apply (Potato Man's lagging hat and hair)");
        var before = tomato.Profile.Squash;
        tomato.Profile.BounceScale = 1.5f;
        Check(Math.Abs(tomato.Profile.Squash - 1.5f * before) < 1e-6f && Math.Abs(tomato.Profile.Bounce - 1.65f) < 1e-5f, "the testing key's bounce scale multiplies the bounce");
        tomato.Profile.BounceScale = float.NaN;
        Check(tomato.Profile.BounceScale == 1, "a non-finite bounce scale falls back to 1");
        var described = s.Describe(stickbear.Bones);
        Check(described.Contains("cadence 1.44 Hz") && described.Contains("chain horn_screen_left"), "the profile prints its values: " + described);
        foreach (var body in new[] { gubble, potato, tomato, cloud, stickbear })
            GD.Print($"CHARACTERS_PROFILE {body.CharacterId}: {body.Profile.Describe(body.Bones)}");
        foreach (var body in new[] { stickbear, cloud, potato, tomato, gubble }) body.Free();
    }

    /// <summary>game/characters must be a fresh copy of assets/characters (GLB bytes; JSON by value, never by text).</summary>
    private void TestFreshCopies()
    {
        var assets = System.IO.Path.GetFullPath(System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "..", "assets", "characters"));
        if (!System.IO.Directory.Exists(assets))
        {
            GD.Print("SKIP the freshness check: assets/characters is not beside this project");
            return;
        }
        foreach (var id in Shipped)
        {
            var game = ProjectSettings.GlobalizePath($"res://characters/{id}");
            var glbSame = System.IO.File.ReadAllBytes(System.IO.Path.Combine(assets, id, "character.glb")).AsSpan()
                .SequenceEqual(System.IO.File.ReadAllBytes(System.IO.Path.Combine(game, "character.glb")));
            var jsonSame = true;
            foreach (var name in new[] { "character.json", "rig.json" })
            {
                var source = System.IO.Path.Combine(assets, id, name);
                var copy = System.IO.Path.Combine(game, name);
                jsonSame &= System.IO.File.Exists(source) == System.IO.File.Exists(copy) &&
                    (!System.IO.File.Exists(source) || JsonSemantic(System.IO.File.ReadAllBytes(source)) == JsonSemantic(System.IO.File.ReadAllBytes(copy)));
            }
            var keep = System.IO.File.ReadAllText(System.IO.Path.Combine(game, "character.glb.import")).Replace("\r\n", "\n") == "[remap]\n\nimporter=\"keep\"\n";
            Check(glbSame && jsonSame && keep, $"{id}: game/characters is a fresh copy (rerun tools/characters/sync_characters.py), its GLB kept from the importer");
        }
    }

    /// <summary>
    /// The library builds meshes straight from the GLB; they must be what Godot's own glTF reader makes of the same
    /// bytes (the look lane may build against the raw GLBs): the same vertices, triangles in Godot's winding, normals
    /// and base colours.
    /// </summary>
    private void TestMatchesGodotGltf()
    {
        foreach (var id in new[] { "gubble", "stickbear" })
        {
            var document = new GltfDocument();
            var state = new GltfState();
            var error = document.AppendFromBuffer(Godot.FileAccess.GetFileAsBytes($"res://characters/{id}/character.glb"), "", state);
            var scene = (Node3D)document.GenerateScene(state);
            var godotMeshes = new Dictionary<string, Mesh>();
            void Collect(Node node)
            {
                if (node is MeshInstance3D instance) godotMeshes[instance.Name] = instance.Mesh;
                foreach (var child in node.GetChildren()) Collect(child);
            }
            Collect(scene);
            _library.TryGet(id, out var definition);
            float worstVertex = 0, worstNormal = 0, worstColour = 0;
            var sameTriangles = error == Error.Ok;
            foreach (var part in definition.Parts.Where(p => p.Mesh != null))
            {
                var ours = part.Mesh!;
                if (!godotMeshes.TryGetValue(part.Name, out var theirs) || theirs.GetSurfaceCount() != ours.GetSurfaceCount()) { sameTriangles = false; continue; }
                for (var surface = 0; surface < ours.GetSurfaceCount(); surface++)
                {
                    var a = ours.SurfaceGetArrays(surface);
                    var b = theirs.SurfaceGetArrays(surface);
                    var va = a[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                    var vb = b[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                    var na = a[(int)Mesh.ArrayType.Normal].AsVector3Array();
                    var nb = b[(int)Mesh.ArrayType.Normal].AsVector3Array();
                    var ia = a[(int)Mesh.ArrayType.Index].AsInt32Array();
                    var ib = b[(int)Mesh.ArrayType.Index].AsInt32Array();
                    sameTriangles &= va.Length == vb.Length && ia.SequenceEqual(ib);
                    for (var i = 0; i < Math.Min(va.Length, vb.Length); i++)
                    {
                        worstVertex = Mathf.Max(worstVertex, (va[i] - vb[i]).Length());
                        worstNormal = Mathf.Max(worstNormal, (na[i] - nb[i]).Length());
                    }
                    var ca = ((StandardMaterial3D)ours.SurfaceGetMaterial(surface)).AlbedoColor;
                    var cb = ((BaseMaterial3D)theirs.SurfaceGetMaterial(surface)).AlbedoColor;
                    worstColour = Mathf.Max(worstColour, Mathf.Max(Mathf.Abs(ca.R - cb.R), Mathf.Max(Mathf.Abs(ca.G - cb.G), Mathf.Abs(ca.B - cb.B))));
                }
            }
            scene.Free();
            Check(sameTriangles, $"{id}: every part's triangles are Godot's glTF reader's, index for index (its winding)");
            Check(worstVertex < 2e-5f && worstNormal < 0.02f && worstColour < 1e-5f,
                FormattableString.Invariant($"{id}: vertices, normals and base colours match Godot's glTF reader (worst {worstVertex:E1} m, {worstNormal:0.000}, {worstColour:E1})"));
        }
    }

    private static string JsonSemantic(byte[] bytes) => JsonSerializer.Serialize(JsonDocument.Parse(bytes).RootElement);
}
