using Godot;
using Godot.Collections;

namespace GarageGame.Native;

/// <summary>A narrow Variant boundary for existing GDScript callers.</summary>
[GlobalClass]
public partial class NativeWorldContract : RefCounted
{
    public Dictionary GetDefaultProfile()
    {
        var profile = WorldScaleProfile.SmallPlayer;
        return new Dictionary
        {
            ["schema_version"] = WorldScaleProfile.SchemaVersion,
            ["meters_per_world_unit"] = WorldScaleProfile.MetersPerWorldUnit,
            ["height_m"] = profile.HeightMeters,
            ["radius_m"] = profile.RadiusMeters,
            ["eye_height_m"] = profile.EyeHeightMeters,
            ["interaction_reach_m"] = profile.InteractionReachMeters,
        };
    }

    public bool ValidateProfile(Variant candidate)
    {
        if (candidate.VariantType != Variant.Type.Dictionary)
            return false;

        var values = candidate.AsGodotDictionary();
        if (values.Count != 6 ||
            !values.TryGetValue("schema_version", out var schema) ||
            schema.VariantType != Variant.Type.Int || schema.AsInt64() != WorldScaleProfile.SchemaVersion ||
            !TryReadNumber(values, "meters_per_world_unit", out var units) || units != WorldScaleProfile.MetersPerWorldUnit ||
            !TryReadNumber(values, "height_m", out var height) ||
            !TryReadNumber(values, "radius_m", out var radius) ||
            !TryReadNumber(values, "eye_height_m", out var eye) ||
            !TryReadNumber(values, "interaction_reach_m", out var reach))
            return false;

        return new WorldScaleProfile(height, radius, eye, reach).IsValid;
    }

    private static bool TryReadNumber(Dictionary values, string key, out double number)
    {
        number = 0.0;
        if (!values.TryGetValue(key, out var value) ||
            value.VariantType is not (Variant.Type.Int or Variant.Type.Float))
            return false;
        number = value.AsDouble();
        return double.IsFinite(number);
    }
}
