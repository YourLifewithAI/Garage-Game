using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace GarageGame.Native.Kernel;

/// <summary>
/// One entry of the manifest's extensions.x_firepits as read: its number (from 1, the order the generator listed it in, which is
/// the firepit's id: firepit:1, firepit:2, ...), and its spot, or why the entry does not read (it is then left out and the
/// kernel's own spread fills its place).
/// </summary>
public sealed record ManifestFirepit(int Number, Vector3? PositionM, string? Problem);

/// <summary>
/// The generator's firepits (decision 74; the generator's real firepits, Lane C): the landscape generator chooses the island's
/// firepits (pipeline/landscape/generator/firepits.py: spread evenly, one per 250 m2 of standable dry land, 4 m apart, 1 m from
/// any Fractal, 3 m from the starting campfire, on ground a first-run player reaches where it can, one near the hamlet) and the
/// exporter writes them as the manifest's x_firepits (contracts/room-manifest.schema.json). Read here from the room's pinned
/// manifest as x_fractals is (CommandHostFractals): a block that does not check is not read at all (logged; the kernel spreads
/// the firepits itself, CampLaws.SpreadFirepits, as for a manifest without one), and each entry is checked on the island once
/// the room has physics (IslandFirepits): a spot of three finite numbers inside the island, on or within FirepitGroundM of
/// standable ground (StandableGround: not water, not a steep face, not a room object or a creation), FirepitFractalClearM
/// from every Fractal and SameCampM from the starting campfire and every firepit kept before it; an entry that fails is left
/// out with a log line, and the spread fills its place among the ones kept. An island save's lit firepits keep their saved
/// spots over both (CommandHostCamps.ResolveSavedPits).
/// </summary>
public partial class CommandHost
{
    /// <summary>A manifest's x_firepits lists at most this many (the schema's maxItems, CampLaws.MaxFirepits).</summary>
    public const int MaxManifestFirepits = CampLaws.MaxFirepits;
    private IReadOnlyList<ManifestFirepit>? _manifestFirepits;
    private bool _manifestFirepitsRead;
    private IReadOnlyList<Vector3>? _firepitLandForTests;
    /// <summary>Where the island's firepits came from, for the camp's log line: the manifest, or the kernel's spread.</summary>
    private string _campSource = "the kernel's spread";

    /// <summary>The manifest's entries the island left out when its firepits were last placed, by number, and why (each also logged).</summary>
    internal IReadOnlyList<(int Number, string Why)> FirepitsLeftOut { get; private set; } = Array.Empty<(int, string)>();

    /// <summary>The manifest's x_firepits as read (each entry, its spot or its problem), or null: the manifest carries none or its block does not check.</summary>
    internal IReadOnlyList<ManifestFirepit>? ManifestFirepits
    {
        get
        {
            if (!_manifestFirepitsRead)
            {
                _manifestFirepits = LoadManifestFirepits();
                _manifestFirepitsRead = true;
            }
            return _manifestFirepits;
        }
    }

    /// <summary>
    /// Test seam: the island's land the spread probes (in place of FirepitLand's rays), so a room with no terrain (the test room) has
    /// land to spread firepits over. Setting it spreads the firepits again.
    /// </summary>
    internal IReadOnlyList<Vector3>? FirepitLandForTests
    {
        get => _firepitLandForTests;
        set
        {
            _firepitLandForTests = value;
            FirepitsForTests = _firepitsForTests;
        }
    }

    /// <summary>The manifest's x_firepits, read from the room's pinned manifest (its own bytes, never a file changed since); null for none.</summary>
    private IReadOnlyList<ManifestFirepit>? LoadManifestFirepits()
    {
        if (Room == null || string.IsNullOrEmpty(Room.Directory)) return null;
        var path = Room.Directory + "/room.json";
        if (!Godot.FileAccess.FileExists(path)) return null;
        var bytes = Godot.FileAccess.GetFileAsBytes(path);
        if (bytes.Length == 0 || CanonicalJson.Sha256Hex(bytes) != Room.ManifestSha256) return NoManifestFirepits("its manifest changed since the room was loaded");
        try
        {
            using var document = CanonicalJson.Parse(bytes);
            if (!document.RootElement.TryGetProperty("extensions", out var extensions) || extensions.ValueKind != JsonValueKind.Object ||
                !extensions.TryGetProperty("x_firepits", out var block)) return null;
            return ParseFirepits(block, Room.Bounds, out var problem) ?? NoManifestFirepits(problem);
        }
        catch (CanonicalJsonException) { return NoManifestFirepits("its manifest is not strict JSON"); }
    }

    private static IReadOnlyList<ManifestFirepit>? NoManifestFirepits(string why)
    {
        GD.Print($"CAMP this island's x_firepits were not read ({why}): the kernel spreads its firepits itself");
        return null;
    }

    /// <summary>
    /// x_firepits checked as contracts/room-manifest.schema.json has it: an object with items only, at most MaxManifestFirepits (else
    /// null and the block's problem). Each item an object with position_m only, three finite numbers inside the room's bounds; an item
    /// that is not is kept in the list with its problem (dropped on the island, its place filled), so the others keep their numbers.
    /// </summary>
    internal static IReadOnlyList<ManifestFirepit>? ParseFirepits(JsonElement block, Aabb bounds, out string problem)
    {
        problem = "";
        if (block.ValueKind != JsonValueKind.Object || block.EnumerateObject().Any(p => p.Name != "items") || !block.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            problem = "it is not an object of items";
            return null;
        }
        if (items.GetArrayLength() > MaxManifestFirepits)
        {
            problem = $"it lists {items.GetArrayLength()} firepits, more than {MaxManifestFirepits}";
            return null;
        }
        var list = new List<ManifestFirepit>();
        var number = 0;
        foreach (var item in items.EnumerateArray())
        {
            number++;
            if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Any(p => p.Name != "position_m") || !item.TryGetProperty("position_m", out var at) ||
                at.ValueKind != JsonValueKind.Array || at.GetArrayLength() != 3 || at.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.Number))
            {
                list.Add(new ManifestFirepit(number, null, "not a spot of three numbers"));
                continue;
            }
            var position = new Vector3((float)at[0].GetDouble(), (float)at[1].GetDouble(), (float)at[2].GetDouble());
            list.Add(!position.IsFinite() ? new ManifestFirepit(number, null, "not a finite spot") :
                !bounds.Grow(0.001f).HasPoint(position) ? new ManifestFirepit(number, null, "outside the island") : new ManifestFirepit(number, position, null));
        }
        return list;
    }

    /// <summary>
    /// The island's firepits from the manifest's entries, by number (once the room has physics; `land` and `candidates` are the
    /// island's, FirepitLand): each entry that reads, stands on the
    /// island's standable ground (FirepitGround) and keeps FirepitFractalClearM from every Fractal and SameCampM from the starting
    /// campfire and every firepit kept before it, at its ground; the places of the others filled by the spread (CampLaws.SpreadFirepits,
    /// the kept ones fixed sites), each taking a left-out number in order. A place the land cannot fill stays empty (its number unused).
    /// </summary>
    private List<(int Number, Vector3 Spot)> IslandFirepits(IReadOnlyList<ManifestFirepit> listed, Vector3 start, IReadOnlyList<Vector3> land, IReadOnlyList<Vector3> candidates)
    {
        var fractals = AllFractals.Select(f => f.PositionM).ToList();
        var kept = new List<(int Number, Vector3 Spot)>();
        var left = new List<int>();
        var leftOut = new List<(int, string)>();
        foreach (var entry in listed)
        {
            string? why = entry.Problem;
            Vector3 ground = default;
            if (why == null)
            {
                if (FirepitGround(entry.PositionM!.Value) is not { } at) why = "not on the island's standable ground";
                else if (fractals.Any(f => CampLaws.Flat(at, f) < CampLaws.FirepitFractalClearM)) why = $"within {CampLaws.FirepitFractalClearM:0.#} m of a Fractal";
                else if (CampLaws.Flat(at, start) < CampLaws.SameCampM || kept.Any(k => CampLaws.Flat(at, k.Spot) < CampLaws.SameCampM)) why = "on another campfire's spot";
                else ground = at;
            }
            if (why != null)
            {
                GD.Print($"CAMP x_firepits entry {entry.Number} is left out ({why}): the kernel's spread fills its place");
                left.Add(entry.Number);
                leftOut.Add((entry.Number, why));
                continue;
            }
            kept.Add((entry.Number, ground));
        }
        if (left.Count > 0)
        {
            var clear = candidates.Where(c => fractals.All(f => CampLaws.Flat(c, f) >= CampLaws.FirepitFractalClearM)).ToList();
            var fill = CampLaws.SpreadFirepits(start, land, clear, left.Count, kept.Select(k => k.Spot).ToList());
            for (var i = 0; i < fill.Count; i++) kept.Add((left[i], fill[i]));
            if (fill.Count < left.Count) GD.Print($"CAMP the land fills {fill.Count} of the {left.Count} places left out");
        }
        kept.Sort((a, b) => a.Number.CompareTo(b.Number));
        FirepitsLeftOut = leftOut;
        return kept;
    }

    /// <summary>
    /// The standable ground at a spot (StandableGround from FirepitProbeM above it): the first thing under it, when it is walkable
    /// ground with no water over it within CampLaws.FirepitGroundM of the spot's height; null otherwise (mid-air, in water, under a
    /// ledge or a canopy, on a steep face, an object or a creation).
    /// </summary>
    private Vector3? FirepitGround(Vector3 at)
    {
        if (!at.IsFinite() || StandableGround(at + Vector3.Up * CampLaws.FirepitProbeM, CampLaws.FirepitProbeM + CampLaws.FirepitGroundM) is not { } ground) return null;
        return Math.Abs(ground.Y - at.Y) <= CampLaws.FirepitGroundM ? ground : null;
    }
}
