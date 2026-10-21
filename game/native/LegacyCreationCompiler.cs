using Godot;
using Godot.Collections;

namespace GarageGame.Native;

/// <summary>Interop only. The existing compiler remains the source of rules and hashes.</summary>
[GlobalClass]
public partial class LegacyCreationCompiler : RefCounted
{
    public Dictionary Compile(Variant source)
    {
        var script = GD.Load<GDScript>("res://scripts/creation_compiler.gd");
        using var compiler = (RefCounted)script.New().AsGodotObject();
        return compiler.Call("compile", source).AsGodotDictionary();
    }
}
