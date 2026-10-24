using Godot;
using Godot.Collections;
using GarageGame.Native.Look;
using GarageGame.Native.Room;

namespace GarageGame.Native;

/// <summary>Runs unchanged in editor and exported release; never opens a user save.</summary>
public partial class NativeContractProbe : Node
{
    /// <summary>A neutral sample creation, inline because the export leaves out <c>tests/*</c>: a Use trigger turning a rotor,
    /// which drives a wind and then a light (the same graph as <c>tests/fixtures/creations/fixture_use_rotor_wind.json</c>).</summary>
    private const string SampleCreation = @"{""schema"":""garage_game.creation"",""version"":1,""name"":""Fixture use rotor wind"",""seed"":5,""mount"":""ground"",""parts"":[{""id"":""base"",""shape"":""cylinder"",""position_m"":[0,0.1,0],""rotation_deg"":[0,0,0],""size_m"":[1.2,0.2,1.2],""material"":""stone""},{""id"":""post"",""shape"":""cylinder"",""position_m"":[0,0.6,0],""rotation_deg"":[0,0,0],""size_m"":[0.25,0.8,0.25],""material"":""wood""},{""id"":""rotor"",""shape"":""rotor"",""position_m"":[0,1.1,0],""rotation_deg"":[0,0,0],""size_m"":[1.2,0.12,1.2],""material"":""copper""}],""nodes"":[{""id"":""use"",""op"":""interact"",""part_id"":""base"",""params"":{}},{""id"":""turn"",""op"":""spin"",""part_id"":""rotor"",""params"":{""speed_rpm"":60,""duration_s"":2}},{""id"":""lift"",""op"":""wind"",""part_id"":""rotor"",""params"":{""direction"":[0,1,0],""acceleration_mps2"":3,""radius_m"":2,""duration_s"":2}},{""id"":""glow"",""op"":""light"",""part_id"":""post"",""params"":{""intensity"":0.8,""duration_s"":2}}],""edges"":[{""from"":""use"",""to"":""turn""},{""from"":""turn"",""to"":""lift""},{""from"":""lift"",""to"":""glow""}]}";

    public override void _Ready()
    {
        try
        {
            using var contract = new NativeWorldContract();
            var profile = contract.GetDefaultProfile();
            if (!contract.ValidateProfile(profile) || profile["height_m"].AsDouble() != 0.10 ||
                profile["meters_per_world_unit"].AsDouble() != 1.0 || profile["schema_version"].AsInt32() != 1)
                throw new System.InvalidOperationException("Native metric profile failed");
            using var adapter = new LegacyCreationCompiler();
            var source = Json.ParseString(SampleCreation);
            var accepted = adapter.Compile(source);
            var rejected = adapter.Compile(new Dictionary { ["schema"] = "untrusted" });
            using var original = (RefCounted)GD.Load<GDScript>("res://scripts/creation_compiler.gd").New().AsGodotObject();
            var direct = original.Call("compile", source).AsGodotDictionary();
            var directRejected = original.Call("compile", new Dictionary { ["schema"] = "untrusted" }).AsGodotDictionary();
            if (!accepted["ok"].AsBool() || rejected["ok"].AsBool() ||
                original.Call("canonical_json", accepted).AsString() != original.Call("canonical_json", direct).AsString() ||
                original.Call("canonical_json", rejected).AsString() != original.Call("canonical_json", directRejected).AsString())
                throw new System.InvalidOperationException("C# compiler adapter diverged from existing rules");
            // Shipped data must load with every pinned hash intact, exactly as the game will read it.
            var room = RoomData.Load(RoomWorld.DefaultRoom);
            var style = StylePreset.Resolve(RoomWorld.DefaultStyleId, RoomWorld.DefaultStyleVersion);
            if (room.Objects.Count == 0 || room.Shell.Count == 0 || style.PresetVersion < 1)
                throw new System.InvalidOperationException("Shipped room or style preset is empty");
            GD.Print($"Native release probe passed: compiled C#, 0.10 m profile, shared GDScript compiler, valid/invalid inputs, identical artifacts, room {room.RoomId} and style {style.PresetId}@{style.PresetVersion} hashes verified");
            GetTree().Quit(0);
        }
        catch (System.Exception error)
        {
            GD.PushError(error.Message);
            GetTree().Quit(1);
        }
    }
}
