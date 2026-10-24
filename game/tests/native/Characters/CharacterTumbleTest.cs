using Godot;
using System;
using System.Linq;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Characters;
using GarageGame.Native.Room;

namespace GarageGame.Tests.Characters;

/// <summary>
/// K7's suite: the tumble on a big fall (docs/runs/RUN-2-CHARACTERS.md, "The tumble (K7)" and acceptance item 2), on real
/// physics with real SmallPlayerControllers wearing the four player characters (CharacterVisual.Create, CharacterMotion.Install
/// and SetBodyVisual, as the room does: the gaits and contact run under the tumble). Headless, --fixed-fps 60.
/// Run: res://tests/native_character_tumble.tscn ("-- --spike" also runs the physical-bones spike and prints its numbers;
/// "-- --skip-budget" drops the wall-clock frame-cost check where wall time means nothing).
/// </summary>
public partial class CharacterTumbleTest : Node3D
{
    /// <summary>The characters a player may wear (the Gubble floats and never tumbles).</summary>
    private static readonly string[] PlayerCharacters = { "cloudpuff", "potato_man", "stickbear", "tomato_man" };
    /// <summary>A body moving slower than this everywhere has settled (m/s).</summary>
    private const float SettledMps = 0.02f;
    private const float Dt = 1f / 60;

    private int _checks;
    private int _failures;
    private int _serial;
    private CharacterLibrary _library = null!;

    public override async void _Ready()
    {
        try
        {
            var args = OS.GetCmdlineUserArgs();
            _library = CharacterLibrary.Load(CharacterLibrary.ShippedRoot);
            // While iterating: "-- --tumbles=20" runs fewer seeded tumbles per character and ground.
            foreach (var arg in args)
                if (arg.StartsWith("--tumbles=") && int.TryParse(arg["--tumbles=".Length..], out var tumbles)) SettleTumbles = Math.Clamp(tumbles, 1, 200);
            if (args.Contains("--spike")) await RunSpike();
            if (!args.Contains("--spike-only"))
            {
                if (args.Contains("--settle-only")) await TestSettling();
                else
                {
                    TestTierTable();
                    await TestInstall();
                    await TestTiersOnRealFalls();
                    await TestCatFeetTiers();
                    await TestDeepWaterAndGlide();
                    await TestFloatingNeverTumbles();
                    await TestControl();
                    await TestKnockout();
                    await TestHazardEntryPoint();
                    await TestCapsuleUntouched();
                    await TestSettling();
                    await TestFrameCost(args.Contains("--skip-budget"));
                }
            }
            GD.Print($"NATIVE_CHARACTER_TUMBLE: {_checks - _failures}/{_checks} checks passed; the tumble: tiers from FallDamage, control held and released, 200 seeded tumbles per character on flat, slope and step, tether, get-up, dazed stars, the capsule untouched, cost; not the feel");
            GetTree().Quit(_failures == 0 ? 0 : 1);
        }
        catch (Exception exception)
        {
            GD.PushError("Native character tumble test exception: " + exception);
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
            GetTree().PhysicsFrame -= Tick;
            source.SetResult();
        }
        GetTree().PhysicsFrame += Tick;
        return source.Task;
    }

    private CharacterDefinition Definition(string id) =>
        _library.TryGet(id, out var definition) ? definition : throw new InvalidOperationException($"{id} is not in the library");

    /// <summary>
    /// A body wearing a character dressed as the room dresses the player (RoomWorld.WearCharacter: CharacterMotion.Install, the
    /// gaits, contact with the land and the tumble, then SetBodyVisual). A floating body also gets the tumble layer the room never
    /// gives it, so the suite can check that the layer itself never tumbles one.
    /// </summary>
    private sealed record Wearer(SmallPlayerController Body, CharacterVisual Visual, CharacterTumble Tumble);

    private Wearer Wear(string id, Vector3 position, float yaw = 0, bool floats = false)
    {
        var body = new SmallPlayerController { Name = $"Wearer{_serial++}_{id}", ReadKeyboard = false, Position = position, Rotation = new Vector3(0, yaw, 0), Floats = floats };
        var visual = CharacterVisual.Create(Definition(id));
        CharacterMotion.Install(visual, body);
        if (floats) TumbleInstaller.Install(visual, body);
        body.SetBodyVisual(visual);
        AddChild(body);
        return new Wearer(body, visual, CharacterTumble.Find(visual)!);
    }

    private static Godot.Collections.Dictionary Preset(string id, int revision) =>
        GD.Load<GDScript>(SmallPlayerController.WorldPhysicsScript).Call("preset", id, revision).AsGodotDictionary();

    /// <summary>A flat floor 4 m square with its top at <paramref name="origin"/>.</summary>
    private StaticBody3D Floor(Vector3 origin, float size = 4)
    {
        var floor = new StaticBody3D { Name = $"Floor{_serial++}", Position = origin + Vector3.Down * 0.05f, CollisionLayer = RoomBuilder.WorldLayer, CollisionMask = 0 };
        floor.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(size, 0.1f, size) } });
        AddChild(floor);
        return floor;
    }

    private static string Inv(FormattableString text) => FormattableString.Invariant(text);
}
