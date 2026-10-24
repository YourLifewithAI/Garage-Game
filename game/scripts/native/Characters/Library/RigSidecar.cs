using System;
using System.Collections.Generic;
using System.Text.Json;

namespace GarageGame.Native.Characters;

/// <summary>
/// The named multipliers a character's rig.json may set on its measured motion profile (all 1 by default).
/// Geometry cannot know a cloud should drift down from each hop, or a tomato should be springier: these can.
/// Each name is the rig.json key; see docs/characters/RIG-API.md for what each one scales.
/// </summary>
public sealed class ProfileMultipliers
{
    public static readonly ProfileMultipliers Default = new();

    /// <summary>The overall bounce: hop height, squash, bob and lean together (the testing key scales this further).</summary>
    public float Bounce { get; init; } = 1;
    public float Squash { get; init; } = 1;
    public float Stride { get; init; } = 1;
    public float Settle { get; init; } = 1;
    /// <summary>Every spring chain's frequency.</summary>
    public float Springiness { get; init; } = 1;
    /// <summary>Every spring chain's damping ratio.</summary>
    public float SpringDamping { get; init; } = 1;
    public float HopHeight { get; init; } = 1;
    /// <summary>Floaty drift: a cloud drifting down from each hop, a float's idle wander.</summary>
    public float Drift { get; init; } = 1;
    public float Lean { get; init; } = 1;
    /// <summary>A waddle's roll, a float's bob.</summary>
    public float Sway { get; init; } = 1;
    public float ArmSwing { get; init; } = 1;

    /// <summary>The rig.json key of every multiplier, in a fixed order, with its getter.</summary>
    public static readonly (string Key, Func<ProfileMultipliers, float> Get)[] Fields =
    {
        ("bounce", m => m.Bounce), ("squash", m => m.Squash), ("stride", m => m.Stride),
        ("settle", m => m.Settle), ("springiness", m => m.Springiness), ("spring_damping", m => m.SpringDamping),
        ("hop_height", m => m.HopHeight), ("drift", m => m.Drift), ("lean", m => m.Lean), ("sway", m => m.Sway),
        ("arm_swing", m => m.ArmSwing),
    };

    internal static ProfileMultipliers Parse(JsonElement element, string path)
    {
        var keys = new string[Fields.Length];
        for (var i = 0; i < Fields.Length; i++) keys[i] = Fields[i].Key;
        StrictJson.Keys(element, path, System.Array.Empty<string>(), keys);
        float Read(string key) => element.TryGetProperty(key, out var value) ? (float)StrictJson.Number(value, $"{path}.{key}", 0, key == "bounce" ? 3 : 4) : 1f;
        return new ProfileMultipliers
        {
            Bounce = Read("bounce"), Squash = Read("squash"), Stride = Read("stride"),
            Settle = Read("settle"), Springiness = Read("springiness"), SpringDamping = Read("spring_damping"),
            HopHeight = Read("hop_height"), Drift = Read("drift"), Lean = Read("lean"), Sway = Read("sway"), ArmSwing = Read("arm_swing"),
        };
    }
}

/// <summary>A rig.json entry for one spring chain, keyed by the chain's root part. Unset values keep the inferred ones.</summary>
public sealed class ChainOverride
{
    /// <summary>false removes an inferred chain; true makes a chain at a part the inference skipped.</summary>
    public bool? Enabled { get; init; }
    /// <summary>Multiplies this chain's frequency (after the profile's springiness).</summary>
    public float Springiness { get; init; } = 1;
    /// <summary>Multiplies this chain's damping ratio (after the profile's spring_damping).</summary>
    public float Damping { get; init; } = 1;
    /// <summary>The engine's spring gravity, a constant sag speed in m/s (0 keeps the drawn pose at rest).</summary>
    public float? GravityMps { get; init; }
    public float? RadiusM { get; init; }
    public int? MaxBones { get; init; }
    /// <summary>Whether the chain collides with the body's capsule.</summary>
    public bool? Collide { get; init; }
}

/// <summary>
/// The optional hand-written sidecar <c>rig.json</c> beside a character.json (schema <c>enfractal.character.rig</c>,
/// version 1). It may rename roles, mark parts rigid, set chain numbers and scale the motion profile. Strict: an
/// unknown key, an unknown part or role, or a number out of range refuses the whole character with a reason.
/// </summary>
public sealed class RigSidecar
{
    public const string Schema = "enfractal.character.rig";
    public const int Version = 1;
    public const int MaxBytes = 64 * 1024;

    public static readonly RigSidecar Empty = new();

    public string Notes { get; private init; } = "";
    public IReadOnlyDictionary<string, PartRole> Roles { get; private init; } = new Dictionary<string, PartRole>();
    public IReadOnlyCollection<string> Rigid { get; private init; } = System.Array.Empty<string>();
    public IReadOnlyDictionary<string, ChainOverride> Chains { get; private init; } = new Dictionary<string, ChainOverride>();
    public ProfileMultipliers Profile { get; private init; } = ProfileMultipliers.Default;

    public bool IsRigid(string part)
    {
        foreach (var name in Rigid)
            if (string.Equals(name, part, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Parses and checks a sidecar against the character's part names.</summary>
    public static RigSidecar Parse(byte[] bytes, IReadOnlyCollection<string> partNames)
    {
        const string label = "rig.json";
        using var document = StrictJson.Parse(bytes, label, MaxBytes);
        var root = document.RootElement;
        StrictJson.Keys(root, label, new[] { "schema", "version" }, new[] { "notes", "roles", "rigid", "chains", "profile" });
        if (StrictJson.String(root.GetProperty("schema"), label + ".schema", 64) != Schema)
            throw new CharacterLoadException($"{label}.schema must be \"{Schema}\"");
        if (StrictJson.Integer(root.GetProperty("version"), label + ".version", 0, 1000) != Version)
            throw new CharacterLoadException($"{label}.version must be {Version}");
        var notes = root.TryGetProperty("notes", out var notesValue) ? StrictJson.String(notesValue, label + ".notes", 2000) : "";

        void ExpectPart(string name, string path)
        {
            foreach (var part in partNames)
                if (string.Equals(part, name, StringComparison.Ordinal)) return;
            throw new CharacterLoadException($"{path} names \"{StrictJson.Clip(name)}\", which is not a part of this character");
        }

        var roles = new Dictionary<string, PartRole>(StringComparer.Ordinal);
        if (root.TryGetProperty("roles", out var roleMap))
        {
            StrictJson.ExpectObject(roleMap, label + ".roles");
            foreach (var entry in roleMap.EnumerateObject())
            {
                var path = $"{label}.roles.{StrictJson.Clip(entry.Name)}";
                ExpectPart(entry.Name, path);
                var token = StrictJson.String(entry.Value, path, 32);
                if (!PartRoles.TryParse(token, out var role)) throw new CharacterLoadException($"{path} is \"{StrictJson.Clip(token)}\", not a known role");
                roles[entry.Name] = role;
            }
        }

        var rigid = new List<string>();
        if (root.TryGetProperty("rigid", out var rigidList))
        {
            StrictJson.Array(rigidList, label + ".rigid", 0, 128);
            var i = 0;
            foreach (var item in rigidList.EnumerateArray())
            {
                var path = $"{label}.rigid[{i++}]";
                var name = StrictJson.String(item, path, 64);
                ExpectPart(name, path);
                if (rigid.Contains(name)) throw new CharacterLoadException($"{path} repeats \"{name}\"");
                rigid.Add(name);
            }
        }

        var chains = new Dictionary<string, ChainOverride>(StringComparer.Ordinal);
        if (root.TryGetProperty("chains", out var chainMap))
        {
            StrictJson.ExpectObject(chainMap, label + ".chains");
            foreach (var entry in chainMap.EnumerateObject())
            {
                var path = $"{label}.chains.{StrictJson.Clip(entry.Name)}";
                ExpectPart(entry.Name, path);
                var value = entry.Value;
                StrictJson.Keys(value, path, System.Array.Empty<string>(),
                    new[] { "enabled", "springiness", "damping", "gravity_mps", "radius_m", "max_bones", "collide" });
                var chain = new ChainOverride
                {
                    Enabled = value.TryGetProperty("enabled", out var e) ? StrictJson.Bool(e, path + ".enabled") : null,
                    Springiness = value.TryGetProperty("springiness", out var s) ? (float)StrictJson.Number(s, path + ".springiness", 0.25, 4) : 1f,
                    Damping = value.TryGetProperty("damping", out var d) ? (float)StrictJson.Number(d, path + ".damping", 0.1, 5) : 1f,
                    GravityMps = value.TryGetProperty("gravity_mps", out var g) ? (float)StrictJson.Number(g, path + ".gravity_mps", 0, 1) : null,
                    RadiusM = value.TryGetProperty("radius_m", out var r) ? (float)StrictJson.Number(r, path + ".radius_m", 0.0005, 0.01) : null,
                    MaxBones = value.TryGetProperty("max_bones", out var b) ? StrictJson.Integer(b, path + ".max_bones", 1, 6) : null,
                    Collide = value.TryGetProperty("collide", out var c) ? StrictJson.Bool(c, path + ".collide") : null,
                };
                if (chain.Enabled == true && rigid.Contains(entry.Name))
                    throw new CharacterLoadException($"{path} enables a chain on a part rig.json marks rigid");
                chains[entry.Name] = chain;
            }
        }

        var profile = root.TryGetProperty("profile", out var profileValue) ? ProfileMultipliers.Parse(profileValue, label + ".profile") : ProfileMultipliers.Default;
        return new RigSidecar { Notes = notes, Roles = roles, Rigid = rigid, Chains = chains, Profile = profile };
    }
}
