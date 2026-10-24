using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using GarageGame.Native.Look.Fauna;
using GarageGame.Native.Room;

namespace GarageGame.Native.Look;

/// <summary>
/// The endless sea (Run 2, the founder's playtest: "the kids want to be able to swim forever"). The room's own meshes stop:
/// the generated sea surface reaches 60 m out and the backdrop's sea floor about 50 m. The open sea carries on from wherever the
/// camera is, at the same cost everywhere:
/// <list type="bullet">
/// <item>a surface that follows the camera, wearing the room's own sea water with its marks in world space (so they stay put
/// while the surface moves), leaving out the rectangle the room's sea mesh already paints, and turning into the sky's own
/// horizon colour toward the camera's far plane, so the sea runs to the horizon with no edge;</item>
/// <item>one sea floor: the backdrop's floor and an open-sea bed beyond it out to 1.5 km, both drawn at RoomSea.OpenSeaBedAt, the
/// bed the swimmers and divers touch (Lane P), so a diver never sees the bed in one place and touches it in another, and the water
/// past the room's meshes always has a bed to be water over (the depth the water shader reads);</item>
/// <item>no distant islands (the founder, the Glow playtest, 9 October: the sea wraps round to home, and the horizon is only sea and
/// sky): the generator's islands in the backdrop are laid flat on the bed with the rest of its floor;</item>
/// <item>a sea mist past the island: from the island's edge out to SeamM it closes in (the environment's depth fog, in the sky's own
/// horizon colour), so the island fades into it and is wholly hidden, in every view, from SeamM out. Lane P's wrap (a swimmer past
/// the seam comes back from the other side) goes beyond SeamM, so no view shows the jump.</item>
/// </list>
/// Visual only: it changes no room data, collision or protection. Swimming past the meshes is Lane P's (RoomSea answers there).
/// </summary>
public partial class OpenSea : Node3D
{
    /// <summary>Half the size of the follow surface, metres: well past every play camera's far plane (ViewDistances.MaxFarM, 250 m).</summary>
    public const float ReachM = 1.6f * ViewDistances.MaxFarM;
    /// <summary>How far out the open-sea bed reaches from the island, metres: past Lane P's far net (1 km) and a camera's far plane.</summary>
    public const float BedReachM = 1500f;
    /// <summary>How many sides the open-sea bed has when the room has no backdrop floor to continue.</summary>
    public const int BedSides = 128;
    /// <summary>What of the backdrop was an island: ground reaching higher than this under the sea's level takes the deep floor's colour when it is laid on the bed.</summary>
    public const float IslandCutM = 0.3f;
    /// <summary>A backdrop mesh is one that reaches this far past the room's sea (the generator's sea floor and distant islands, not the home island).</summary>
    public const float BackdropReachM = 20f;

    // ---------- the sea mist and the seam ----------

    /// <summary>
    /// The swim speed the seam is measured in: the small avatar's walk (0.32 m/s) times its swim factor (0.6). A minute's swim is about 11.5 m; the seam is
    /// 16.5 m out.
    /// </summary>
    public const float SwimMps = 0.192f;
    /// <summary>
    /// How far any play camera stands from the swimmer: F2 zoomed out to RoomHud.ShoulderFarM (8 m; the shelved F4's arm is 2.7 m, F3's at most
    /// 2.4 m, plus the framing's shift toward the Gubble), and half a metre to spare.
    /// </summary>
    public const float CameraReachM = RoomHud.ShoulderFarM + 0.5f;
    /// <summary>At full mist (from SeamM out), nothing farther than this from the camera shows: wholly the mist (the sky's horizon colour).</summary>
    public const float MistFullM = 8f;
    /// <summary>At full mist, the nearest the mist begins, metres from the camera: the swimmer's own water stays clear.</summary>
    public const float MistNearM = 2f;
    /// <summary>
    /// How far past the island's reach (its reef or its room's corners, whichever is farther) the seam lies: a camera there stands at
    /// least MistFullM from every part of the island, so the island is wholly hidden in every view. 11.5 m is about a minute's swim.
    /// </summary>
    public const float SeamPastIslandM = CameraReachM + MistFullM;
    /// <summary>With no mist (at the island's edge and inside it), the depth fog starts at the camera's far plane times this and is whole at FogClearEnd times it: nothing within the far plane is touched.</summary>
    public const float FogClearStart = 1.0f, FogClearEnd = 1.5f;
    /// <summary>A water surface within this of the sea's level is the sea's (a pond at another level keeps its own veil).</summary>
    public const float SeaLevelToleranceM = 0.003f;
    /// <summary>Where, as fractions of the camera's far plane, the sea starts and finishes turning into the horizon.</summary>
    public const float HorizonFadeStart = 0.55f, HorizonFadeEnd = 0.97f;
    /// <summary>The open bed's colour when there is no backdrop floor to continue: a dark moss, of which deep water lets a tenth through.</summary>
    public static readonly Color BedColor = new(0.30f, 0.34f, 0.28f);

    /// <summary>
    /// The living shore (ShoreWash): where the sea meets the land the water draws back and washes in. The numbers, the field of each
    /// place's distance to the coast the land shader reads. The wash runs on the engine's own time in the shaders (nothing is set on any
    /// material each frame); a capture can hold it at a chosen second (HoldShoreAt).
    /// </summary>
    public ShoreLook Shore { get; private set; } = ShoreLook.Default;
    public ShoreWash.Field? ShoreField { get; private set; }
    /// <summary>The colour the wash over the sand is painted in: the sea's own shallow colour.</summary>
    public Color ShoreFilmColour { get; private set; } = ShoreWash.DefaultFilmColour;
    /// <summary>The second the wash is held at (a capture), or null while it runs on the engine's time.</summary>
    public float? ShoreHeldAtS { get; private set; }
    /// <summary>The land materials the shore is on (the terrain's, which share the island's marks), for the clock and the tests.</summary>
    public IReadOnlyCollection<ShaderMaterial> ShoreLandMaterials => _shoreLand;
    private readonly HashSet<ShaderMaterial> _shoreLand = new();

    public RoomSea Sea { get; private set; } = null!;
    /// <summary>The surface that follows the camera (null when the room's own sea mesh could not be found).</summary>
    public MeshInstance3D? Surface { get; private set; }
    /// <summary>The open-sea bed past the backdrop's floor, out to BedReachM: static, drawn at RoomSea.OpenSeaBedAt.</summary>
    public MeshInstance3D Bed { get; private set; } = null!;
    /// <summary>The backdrop meshes whose floor now lies at RoomSea.OpenSeaBedAt (their islands laid flat with it).</summary>
    public IReadOnlyList<MeshInstance3D> Floors => _floors;
    /// <summary>The centre of the room's sea (the generated sea's and the backdrop's centre), in XZ.</summary>
    public Vector2 Home { get; private set; }
    /// <summary>How far the home waters reach from Home: the reef's farthest point.</summary>
    public float HomeReachM { get; private set; }
    /// <summary>The rectangle (x0, z0, x1, z1) the room's own sea mesh covers, which the follow surface leaves to it.</summary>
    public Vector4 Hole { get; private set; }
    /// <summary>The island's centre the seam is measured from: the open-sea floor's centre (RoomSea.OpenSeaCentreM), the same for the look and the bodies.</summary>
    public Vector2 SeamCentre { get; private set; }
    /// <summary>How far the island reaches from SeamCentre: its reef or its room's corners, whichever is farther.</summary>
    public float IslandReachM { get; private set; }
    /// <summary>
    /// The seam: from this distance from SeamCentre out, the mist wholly hides the island in every view (F1 to F4, observe, above and
    /// under water). Lane P's wrap goes beyond it. On the island garage it is about 17.6 m, about a minute's swim from the reef.
    /// </summary>
    public float SeamM => IslandReachM + SeamPastIslandM;
    /// <summary>How thick the mist is now, 0 (none: at the island) to 1 (full: from SeamM out), from the swimmer's distance.</summary>
    public float Mist { get; private set; }
    /// <summary>
    /// Whether the land has an aerial haze (ViewDistances: open land, the look sets it): with no mist the fog is the haze, and the
    /// mist closes in from it as a swimmer swims out. Without it the fog is off until there is mist.
    /// </summary>
    public bool Haze { get; set; }
    /// <summary>The fog drawn now (the haze, the mist or both).</summary>
    public DepthFog Fog { get; private set; }
    /// <summary>The environment the mist is drawn in (the look's own); without one the open sea draws no mist.</summary>
    public Godot.Environment? Atmosphere { get; set; }
    /// <summary>The camera the sea follows: the one a review or preview framed, else the viewport's current camera.</summary>
    public Camera3D? Camera { get; set; }
    /// <summary>Why the open sea is missing a part (empty when it has them all).</summary>
    public string Note { get; private set; } = "";

    private readonly List<MeshInstance3D> _floors = new();
    private Color _horizon = new(0.75f, 0.85f, 0.93f);
    private float _horizonEnergy = 1f;
    private Func<Node3D?>? _swimmer;
    /// <summary>
    /// The view under the sea (Run 2, for diving): the pond life's veil shader over the whole screen, in the sea's colours, with the
    /// light from above, shown while the camera is under the sea (CameraUnderSea).
    /// </summary>
    public MeshInstance3D Veil { get; private set; } = null!;
    /// <summary>Whether the camera the sea follows is under the sea now: below its level and off the island (outside the coast).</summary>
    public bool CameraUnderSea { get; private set; }
    /// <summary>The sea seen from inside: its murk colour and distance (metres that hide two thirds), the tint at the eye, and the light from above.</summary>
    public static readonly Color MurkColor = new(0.11f, 0.27f, 0.34f);
    public const float MurkM = 0.55f, NearTint = 0.1f, UpLight = 1.4f, DimPerM = 0.8f;

    /// <summary>The fish in the sea's deeper water near the swimmer (null until the look names the swimmer).</summary>
    public SeaLife? Life { get; private set; }

    /// <summary>The sea's fish follow this swimmer (the player; never the Gubble): the look hands it over once the room is dressed.</summary>
    public void SetSwimmer(Func<Node3D?> swimmer)
    {
        _swimmer = swimmer;
        if (Life != null) return;
        Life = SeaLife.Create(Sea, swimmer);
        AddChild(Life);
    }
    private readonly List<ShaderMaterial> _water = new();
    private float _far = -1f;

    /// <summary>
    /// Build the open sea for a dressed room with a sea. waterMeshes are the room's water meshes as the look dressed them (the
    /// sea's surface is the one reaching past the room); the backdrop is the dressed land mesh reaching BackdropReachM past it.
    /// </summary>
    public static OpenSea Build(RoomSea sea, Aabb roomBounds, IEnumerable<MeshInstance3D> waterMeshes, IEnumerable<MeshInstance3D> landMeshes)
    {
        var open = new OpenSea { Name = "OpenSea", Sea = sea };
        var roomRect = new Rect2(roomBounds.Position.X, roomBounds.Position.Z, roomBounds.Size.X, roomBounds.Size.Z);
        // The room's sea surface: the water mesh that reaches well past the room (ponds and brooks stay inside it).
        var seaMesh = waterMeshes.Where(m => m.Mesh != null)
            .Select(m => (Mesh: m, Box: m.GlobalTransform * m.GetAabb()))
            .Where(m => m.Box.Size.X > roomRect.Size.X + 2f * BackdropReachM || m.Box.Size.Z > roomRect.Size.Y + 2f * BackdropReachM)
            .OrderByDescending(m => m.Box.Size.X * m.Box.Size.Z).FirstOrDefault();
        var centre = roomRect.GetCenter();
        if (seaMesh.Mesh != null)
        {
            var box = seaMesh.Box;
            open.Hole = new Vector4(box.Position.X, box.Position.Z, box.End.X, box.End.Z);
            centre = new Vector2(box.GetCenter().X, box.GetCenter().Z);
        }
        open.Home = centre;
        open.HomeReachM = sea.Reef.Length > 0 ? sea.Reef.Max(p => p.DistanceTo(centre)) : roomRect.Size.Length() * 0.5f;
        open.SeamCentre = sea.OpenSeaCentreM;
        open.IslandReachM = IslandReach(sea, roomBounds);

        // The backdrop: its floor, islands and all, laid on the open bed.
        var backdrop = landMeshes.Where(m => m.Mesh != null && m.Mesh.GetSurfaceCount() > 0)
            .Select(m => (Mesh: m, Box: m.GlobalTransform * m.GetAabb()))
            .Where(m => m.Box.Position.X < roomRect.Position.X - BackdropReachM || m.Box.End.X > roomRect.End.X + BackdropReachM)
            .Select(m => m.Mesh).ToArray();
        if (seaMesh.Mesh?.GetSurfaceOverrideMaterial(0) is ShaderMaterial water && water.HasMeta("water"))
        {
            open._water.Add(water);
            var surface = (ShaderMaterial)water.Duplicate();
            surface.ResourceName = "open sea";
            surface.SetShaderParameter("world_pattern", true);
            surface.SetShaderParameter("use_hole", true);
            surface.SetShaderParameter("hole", open.Hole);
            open._water.Add(surface);
            open.Surface = new MeshInstance3D
            {
                Name = "OpenSeaSurface", Mesh = Quad(ReachM, OpenWaterColour(seaMesh.Mesh, roomRect)), MaterialOverride = surface,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, GIMode = GeometryInstance3D.GIModeEnum.Disabled,
                Position = new Vector3(centre.X, sea.LevelM, centre.Y),
            };
            open.Surface.SetMeta(LookDirector.DressedMeta, true);
            open.Surface.SetMeta(LookDirector.WaterMeta, true);
            open.AddChild(open.Surface);
        }
        else open.Note = "the room's sea surface was not found among its water, so the open sea has a bed but no surface";

        // The floor laid on the bed the bodies touch (the generator's distant islands flat with it), then the open bed carrying on from its rim.
        Vector3[] rim = Array.Empty<Vector3>();
        Color[] rimColours = Array.Empty<Color>();
        Material? floorMaterial = null;
        // The sea's own look on its water (the lagoon light, the open sea deep, foam on the reef and the beaches).
        foreach (var material in open._water)
        {
            material.SetShaderParameter("use_sea", true);
            material.SetShaderParameter("sea_level", sea.LevelM);
        }
        // The living shore: the sea's water draws its edge back and in, the land paints the wash and the damp band (ShoreWash).
        var shoreClock = System.Diagnostics.Stopwatch.StartNew();
        if (open._water.Count > 0)
        {
            open.ShoreField = ShoreWash.BuildField(sea, open.Shore);
            if (seaMesh.Mesh?.GetSurfaceOverrideMaterial(0) is ShaderMaterial seaWater) open.ShoreFilmColour = open.FilmColourOf(seaWater, seaMesh.Mesh, roomRect);
            open.ApplyShore();
            foreach (var mesh in landMeshes) open.AdoptLand(mesh);
            GD.Print(FormattableString.Invariant($"LOOK: the living shore: its field {open.ShoreField?.Width}x{open.ShoreField?.Depth} and {open._shoreLand.Count} land materials in {shoreClock.Elapsed.TotalMilliseconds:0} ms"));
        }
        foreach (var mesh in backdrop)
        {
            // A backdrop the look cannot lay flat (more than one surface) is hidden rather than left standing on the horizon.
            if (mesh.Mesh.GetSurfaceCount() != 1)
            {
                mesh.Visible = false;
                open.Note = (open.Note.Length > 0 ? open.Note + "; " : "") + $"a backdrop mesh ({mesh.Name}) is not one surface, so the look cannot lay it on the bed; it is hidden";
                continue;
            }
            var (meshRim, meshColours) = open.LayFloor(mesh);
            if (meshRim.Length > rim.Length) (rim, rimColours, floorMaterial) = (meshRim, meshColours, mesh.GetSurfaceOverrideMaterial(0));
        }
        open.Bed = new MeshInstance3D
        {
            Name = "OpenSeaBed", Mesh = open.BedMesh(rim, rimColours), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            GIMode = GeometryInstance3D.GIModeEnum.Disabled,
        };
        if (floorMaterial != null) open.Bed.SetSurfaceOverrideMaterial(0, floorMaterial);
        else open.Bed.MaterialOverride = new StandardMaterial3D { AlbedoColor = BedColor, Roughness = 1f, MetallicSpecular = 0.2f, VertexColorUseAsAlbedo = true, ResourceName = "open sea bed" };
        open.Bed.SetMeta(LookDirector.DressedMeta, true);
        open.AddChild(open.Bed);
        open.BuildVeil();
        return open;
    }

    /// <summary>
    /// How far the island reaches from the open sea's centre (RoomSea.OpenSeaCentreM): the farthest of its reef and its room's corners,
    /// so every part of the island, its shore, its things and its reef foam, lies within it. Room data only: Lane P's wrap can ask
    /// it without a look.
    /// </summary>
    public static float IslandReach(RoomSea sea, Aabb roomBounds)
    {
        var centre = sea.OpenSeaCentreM;
        var reach = sea.Reef.Length > 0 ? sea.Reef.Max(p => p.DistanceTo(centre)) : 0f;
        foreach (var x in new[] { roomBounds.Position.X, roomBounds.End.X })
            foreach (var z in new[] { roomBounds.Position.Z, roomBounds.End.Z })
                reach = Mathf.Max(reach, new Vector2(x, z).DistanceTo(centre));
        return reach;
    }

    /// <summary>The seam for a room's data: from this distance from RoomSea.OpenSeaCentreM out, the mist wholly hides the island in every view.</summary>
    public static float SeamFor(RoomSea sea, Aabb roomBounds) => IslandReach(sea, roomBounds) + SeamPastIslandM;

    /// <summary>How thick the mist is for a swimmer at a distance from the island's centre: none to the island's reach, full from the seam, smooth between.</summary>
    public static float MistFor(float distanceM, float islandReachM)
    {
        var t = Mathf.Clamp((distanceM - islandReachM) / SeamPastIslandM, 0f, 1f);
        return float.IsFinite(t) ? t * t * (3f - 2f * t) : 1f;
    }

    /// <summary>
    /// The depth fog for a mist (0 to 1) under a camera's far plane: where it begins and where it is whole, metres from the camera.
    /// With no mist it starts at the far plane (nothing drawn is touched); at full mist it runs from MistNearM to MistFullM. Between,
    /// both close in geometrically, so the island fades steadily as the swimmer swims out.
    /// </summary>
    public static (float Begin, float End) MistFog(float mist, float far)
    {
        mist = Mathf.Clamp(mist, 0f, 1f);
        far = Mathf.Max(far, MistFullM * 2f);
        float Toward(float from, float to) => from * Mathf.Pow(to / from, mist);
        return (Toward(far * FogClearStart, MistNearM), Toward(far * FogClearEnd, MistFullM));
    }

    /// <summary>A flat square, half-size reach, facing up, in one colour (the water shader takes the colour as its baked colour).</summary>
    private static ArrayMesh Quad(float reach, Color colour)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = new[] { new Vector3(-reach, 0, -reach), new Vector3(reach, 0, -reach), new Vector3(reach, 0, reach), new Vector3(-reach, 0, reach) };
        arrays[(int)Mesh.ArrayType.Normal] = new[] { Vector3.Up, Vector3.Up, Vector3.Up, Vector3.Up };
        arrays[(int)Mesh.ArrayType.Color] = new[] { colour, colour, colour, colour };
        arrays[(int)Mesh.ArrayType.Index] = new[] { 0, 1, 2, 0, 2, 3 };
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.CustomAabb = new Aabb(new Vector3(-reach, -0.01f, -reach), new Vector3(2f * reach, 0.02f, 2f * reach));
        return mesh;
    }

    /// <summary>
    /// The sea's colour in its shallows, as the water shader paints it there (its colour times the baked tint of the sea inside the room
    /// times the shallow wash), for the wash over the sand to match the water it comes from.
    /// </summary>
    private Color FilmColourOf(ShaderMaterial water, MeshInstance3D sea, Rect2 room)
    {
        var baseLinear = water.GetShaderParameter("base_color").AsColor().SrgbToLinear();
        var shallow = water.GetShaderParameter("shallow_tint").AsVector3();
        var near = NearWaterColour(sea.Mesh, sea.GlobalTransform, room);
        return new Color(
            Mathf.Clamp(baseLinear.R * near.R * shallow.X, 0f, 1f), Mathf.Clamp(baseLinear.G * near.G * shallow.Y, 0f, 1f), Mathf.Clamp(baseLinear.B * near.B * shallow.Z, 0f, 1f)).LinearToSrgb();
    }

    /// <summary>The mean baked colour of the sea surface inside the room (the island's own water).</summary>
    private static Color NearWaterColour(Mesh mesh, Transform3D world, Rect2 room)
    {
        var arrays = mesh.SurfaceGetArrays(0);
        if (arrays[(int)Mesh.ArrayType.Color].VariantType != Variant.Type.PackedColorArray) return new Color(1f, 1f, 1f);
        var colours = arrays[(int)Mesh.ArrayType.Color].AsColorArray();
        var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        float r = 0, g = 0, b = 0;
        var count = 0;
        for (var i = 0; i < vertices.Length; i++)
        {
            var at = world * vertices[i];
            if (!room.HasPoint(new Vector2(at.X, at.Z))) continue;
            r += colours[i].R; g += colours[i].G; b += colours[i].B;
            count++;
        }
        return count == 0 ? new Color(1f, 1f, 1f) : new Color(r / count, g / count, b / count);
    }

    /// <summary>Turn the shore on for the sea's water and every land material adopted (the numbers, the field, the clock).</summary>
    public void ApplyShore()
    {
        var water = ShoreWash.WaterParameters(Shore, true, ShoreHeldAtS ?? ShoreWash.FreeRunning, ShoreField);
        foreach (var material in _water)
            foreach (var (name, value) in water) material.SetShaderParameter(name, value);
        // The same numbers on every land material (the terrain has one for each tile's bake colour: hundreds): made once.
        _landParameters = ShoreWash.LandParameters(Shore, true, ShoreHeldAtS ?? ShoreWash.FreeRunning, Sea.LevelM, ShoreFilmColour, SeaLook.Default.FoamColor, ShoreField);
        foreach (var material in _shoreLand) ApplyShoreTo(material);
    }

    private (StringName Name, Variant Value)[] _landParameters = Array.Empty<(StringName, Variant)>();

    private void ApplyShoreTo(ShaderMaterial material)
    {
        foreach (var (name, value) in _landParameters) material.SetShaderParameter(name, value);
    }

    /// <summary>
    /// Put the land materials of a dressed mesh on the shore (the look calls it for every land mesh it dresses once the sea is built, so
    /// ground that arrives later wears it too). Water is not land; a mesh that wears no landscape material is left alone.
    /// </summary>
    public void AdoptLand(MeshInstance3D mesh)
    {
        if (mesh.Mesh == null || mesh.HasMeta(LookDirector.WaterMeta)) return;
        for (var surface = 0; surface < mesh.Mesh.GetSurfaceCount(); surface++)
            if (mesh.GetSurfaceOverrideMaterial(surface) is ShaderMaterial material && material.HasMeta("landscape") && !material.HasMeta("water") && _shoreLand.Add(material))
                ApplyShoreTo(material);
    }

    /// <summary>The wash is held at a second of the shore's clock and stays there (a capture shows it up or drawn back, the same in a before and an after).</summary>
    public void HoldShoreAt(float seconds)
    {
        ShoreHeldAtS = Math.Max(0f, seconds);
        PushHold();
    }

    /// <summary>The wash runs on the engine's time again.</summary>
    public void ReleaseShore()
    {
        ShoreHeldAtS = null;
        PushHold();
    }

    private void PushHold()
    {
        var hold = ShoreHeldAtS ?? ShoreWash.FreeRunning;
        foreach (var material in _water) material.SetShaderParameter("shore_hold_s", hold);
        foreach (var material in _shoreLand) material.SetShaderParameter("shore_hold_s", hold);
    }

    public override void _ExitTree()
    {
        // The materials are shared across rooms (the library caches them): leave them as a room without a sea expects them.
        foreach (var material in _water) material.SetShaderParameter("shore_on", false);
        foreach (var material in _shoreLand) material.SetShaderParameter("shore_on", false);
        _shoreLand.Clear();
    }

    /// <summary>The baked colour of the room's open sea: the mean vertex colour of the sea surface outside the room.</summary>
    private static Color OpenWaterColour(MeshInstance3D sea, Rect2 room)
    {
        var arrays = sea.Mesh.SurfaceGetArrays(0);
        if (arrays[(int)Mesh.ArrayType.Color].VariantType != Variant.Type.PackedColorArray) return new Color(1f, 1f, 1f);
        var colours = arrays[(int)Mesh.ArrayType.Color].AsColorArray();
        var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var outside = room.Grow(BackdropReachM);
        float r = 0, g = 0, b = 0;
        var count = 0;
        for (var i = 0; i < vertices.Length; i++)
        {
            var at = sea.GlobalTransform * vertices[i];
            if (outside.HasPoint(new Vector2(at.X, at.Z))) continue;
            r += colours[i].R; g += colours[i].G; b += colours[i].B;
            count++;
        }
        return count == 0 ? new Color(1f, 1f, 1f) : new Color(r / count, g / count, b / count);
    }

    /// <summary>
    /// Lay a backdrop's floor on the bed the bodies touch: every vertex at RoomSea.OpenSeaBedAt under it, facing up, and where a
    /// distant island stood its ground takes the deep floor's colour (the horizon is only sea and sky). Returns the floor's rim (its outermost ring of vertices, by
    /// angle round Home) and their colours, where the open bed carries on.
    /// </summary>
    private (Vector3[] Rim, Color[] Colours) LayFloor(MeshInstance3D backdrop)
    {
        var mesh = backdrop.Mesh;
        if (mesh.GetSurfaceCount() != 1) return (Array.Empty<Vector3>(), Array.Empty<Color>());
        var arrays = mesh.SurfaceGetArrays(0);
        var world = backdrop.GlobalTransform;
        var local = world.AffineInverse();
        var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var colours = arrays[(int)Mesh.ArrayType.Color].VariantType == Variant.Type.PackedColorArray ? arrays[(int)Mesh.ArrayType.Color].AsColorArray() : null;
        var cut = Sea.LevelM - IslandCutM;
        var rimRadius = vertices.Select(v => world * v).Max(v => new Vector2(v.X, v.Z).DistanceTo(Home));
        var deep = colours == null ? new Color(1f, 1f, 1f) : Mean(vertices.Select((v, i) => (At: world * v, i))
            .Where(v => new Vector2(v.At.X, v.At.Z).DistanceTo(Home) > rimRadius - 1e-3f).Select(v => colours[v.i]));
        var rim = new List<(float Angle, Vector3 At, Color Colour)>();
        for (var i = 0; i < vertices.Length; i++)
        {
            var at = world * vertices[i];
            var flat = new Vector2(at.X, at.Z);
            if (colours != null && at.Y > cut) colours[i] = deep;
            at.Y = Sea.OpenSeaBedAt(flat);
            vertices[i] = local * at;
            if (flat.DistanceTo(Home) > rimRadius - 1e-3f) rim.Add((Mathf.Atan2(flat.Y - Home.Y, flat.X - Home.X), at, colours?[i] ?? deep));
        }
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = Enumerable.Repeat(local.Basis * Vector3.Up, vertices.Length).Select(n => n.Normalized()).ToArray();
        arrays[(int)Mesh.ArrayType.Tangent] = default;
        if (colours != null) arrays[(int)Mesh.ArrayType.Color] = colours;
        var floor = new ArrayMesh();
        floor.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        var material = backdrop.GetSurfaceOverrideMaterial(0);
        backdrop.Mesh = floor;
        if (material != null) backdrop.SetSurfaceOverrideMaterial(0, material);
        _floors.Add(backdrop);
        var ordered = rim.OrderBy(r => r.Angle).ToArray();
        return (ordered.Select(r => r.At).ToArray(), ordered.Select(r => r.Colour).ToArray());
    }

    private static Color Mean(IEnumerable<Color> colours)
    {
        float r = 0, g = 0, b = 0;
        var count = 0;
        foreach (var c in colours) { r += c.R; g += c.G; b += c.B; count++; }
        return count == 0 ? new Color(1f, 1f, 1f) : new Color(r / count, g / count, b / count);
    }

    /// <summary>
    /// The open-sea bed: rings from the floor's rim (its very vertices, so the two meet without a seam) out to BedReachM, every vertex
    /// at RoomSea.OpenSeaBedAt, in the rim's colours. Without a backdrop floor, a disc round Home.
    /// </summary>
    private ArrayMesh BedMesh(Vector3[] rim, Color[] rimColours)
    {
        if (rim.Length < 3)
        {
            rim = Enumerable.Range(0, BedSides).Select(k => Mathf.Tau * k / BedSides).Select(a => new Vector3(Home.X + Mathf.Cos(a) * 0.01f, 0f, Home.Y + Mathf.Sin(a) * 0.01f)).ToArray();
            rimColours = Enumerable.Repeat(new Color(1f, 1f, 1f), rim.Length).ToArray();
        }
        var directions = rim.Select(r => (new Vector2(r.X, r.Z) - Home).Normalized()).ToArray();
        var radius = rim.Average(r => new Vector2(r.X, r.Z).DistanceTo(Home));
        var radii = new List<float>();
        for (var r = radius * 1.08f + 0.5f; ; r = r * 1.3f + 1f)
        {
            radii.Add(Mathf.Min(r, BedReachM));
            if (r >= BedReachM) break;
        }
        var count = rim.Length;
        var positions = new List<Vector3>(rim.Select(r => new Vector3(r.X, Sea.OpenSeaBedAt(new Vector2(r.X, r.Z)), r.Z)));
        foreach (var r in radii)
            foreach (var d in directions)
            {
                var flat = Home + d * r;
                positions.Add(new Vector3(flat.X, Sea.OpenSeaBedAt(flat), flat.Y));
            }
        var indices = new List<int>();
        for (var ring = 0; ring < radii.Count; ring++)
            for (var k = 0; k < count; k++)
            {
                int a = ring * count + k, b = ring * count + (k + 1) % count, c = a + count, d = b + count;
                indices.AddRange(new[] { a, b, d, a, d, c });
            }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = positions.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = Enumerable.Repeat(Vector3.Up, positions.Count).ToArray();
        arrays[(int)Mesh.ArrayType.Color] = Enumerable.Range(0, positions.Count).Select(i => rimColours[i % count]).ToArray();
        // Wound clockwise seen from above (Godot's front face), whichever way the rim runs.
        var probe = (positions[1] - positions[0]).Cross(positions[count] - positions[0]);
        arrays[(int)Mesh.ArrayType.Index] = (probe.Y < 0f ? indices : FlipWinding(indices)).ToArray();
        var bed = new ArrayMesh();
        bed.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return bed;
    }

    private static List<int> FlipWinding(List<int> indices)
    {
        var flipped = new List<int>(indices.Count);
        for (var i = 0; i < indices.Count; i += 3) flipped.AddRange(new[] { indices[i], indices[i + 2], indices[i + 1] });
        return flipped;
    }

    private void BuildVeil()
    {
        var material = new ShaderMaterial { Shader = GD.Load<Shader>(PondLife.VeilShaderPath), ResourceName = "under the sea", RenderPriority = 100 };
        material.SetShaderParameter("pond_count", 1);
        material.SetShaderParameter("pond_box", Enumerable.Range(0, 8).Select(i => i == 0 ? new Vector4(-1e6f, -1e6f, 1e6f, 1e6f) : Vector4.Zero).ToArray());
        material.SetShaderParameter("pond_level", Enumerable.Range(0, 8).Select(i => i == 0 ? Sea.LevelM : -1e9f).ToArray());
        material.SetShaderParameter("murk_color", MurkColor);
        material.SetShaderParameter("murk_m", MurkM);
        material.SetShaderParameter("near_tint", NearTint);
        material.SetShaderParameter("up_light", UpLight);
        material.SetShaderParameter("dim_per_m", DimPerM);
        Veil = new MeshInstance3D
        {
            Name = "UnderTheSea", Mesh = new QuadMesh { Size = new Vector2(2f, 2f) }, MaterialOverride = material, Visible = false,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, GIMode = GeometryInstance3D.GIModeEnum.Disabled, ExtraCullMargin = 16384f,
        };
        Veil.SetMeta(LookDirector.DressedMeta, true);
        AddChild(Veil);
    }

    /// <summary>
    /// Whether a camera at a point is under the sea, asked the way the bodies ask (Codex's reviews: the veil had shown under solid ground
    /// and below the bed, and never in sea water inside the simplified coast): the water column at the point from RoomWater.At, else
    /// RoomSea.OpenWater, the eye under its surface, and that surface the sea's (a pond at another level keeps its own veil). Lane P's
    /// fixes to those queries (submerged ceilings) reach the view with no change here.
    /// </summary>
    public bool UnderSea(Vector3 eye)
    {
        // An eye above the sea's level is under no sea surface: no rays (the look asks every frame, and the queries' answers are garbage).
        if (!eye.IsFinite() || eye.Y >= Sea.LevelM + SeaLevelToleranceM) return false;
        if (!IsInsideTree() || GetWorld3D()?.DirectSpaceState is not { } space) return false;
        var column = RoomWater.At(space, eye, RoomWater.SearchM, 0f);
        if (!column.Wet) column = Sea.OpenWater(space, eye, RoomWater.SearchM, 0f);
        return column.Wet && eye.Y < column.SurfaceY && Mathf.Abs(column.SurfaceY - Sea.LevelM) < SeaLevelToleranceM;
    }

    /// <summary>The sky's horizon (its colour and brightness, as the sky shader draws it) for the far sea and the mist to turn into.</summary>
    public void SetHorizon(Color horizon, float brightness)
    {
        _horizon = horizon;
        _horizonEnergy = brightness;
        if (Atmosphere != null)
        {
            Atmosphere.FogLightColor = horizon;
            Atmosphere.FogLightEnergy = brightness;
        }
        foreach (var material in _water)
        {
            material.SetShaderParameter("horizon_color", horizon);
            material.SetShaderParameter("horizon_energy", brightness);
        }
    }

    private void SetFar(float far)
    {
        if (Mathf.IsEqualApprox(far, _far)) return;
        _far = far;
        foreach (var material in _water)
        {
            material.SetShaderParameter("horizon_fade_start_m", far * HorizonFadeStart);
            material.SetShaderParameter("horizon_fade_end_m", far * HorizonFadeEnd);
        }
    }

    /// <summary>Follow a camera now (the look calls this every frame from _Process; tests call it directly).</summary>
    public void Follow(Camera3D camera)
    {
        var at = camera.GlobalPosition;
        if (Surface != null) Surface.GlobalPosition = new Vector3(at.X, Sea.LevelM, at.Z);
        SetFar(camera.Far);
        CameraUnderSea = UnderSea(at);
        Veil.Visible = CameraUnderSea;
        if (CameraUnderSea && Veil.MaterialOverride is ShaderMaterial veil && GetWorld3D()?.Environment is { } environment)
        {
            var ambient = environment.AmbientLightColor * environment.AmbientLightEnergy;
            veil.SetShaderParameter("light_level", Mathf.Clamp(ambient.Luminance * 1.2f + 0.08f, 0.04f, 1.5f));
        }
        // The mist thickens with the swimmer's distance from the island (a stable reference: the player's body, the same whichever
        // view looks at it), so switching views never changes it; a view without a swimmer goes by the camera.
        var body = _swimmer?.Invoke();
        var swimmer = body != null && IsInstanceValid(body) && body.IsInsideTree() ? body.GlobalPosition : at;
        ApplyMist(MistFor(new Vector2(swimmer.X, swimmer.Z).DistanceTo(SeamCentre), IslandReachM), camera.Far);
    }

    /// <summary>
    /// Draw the mist (and the land's haze, ViewDistances.Fog): the environment's depth fog in the sky's horizon colour, off the sky;
    /// off entirely while there is neither.
    /// </summary>
    private void ApplyMist(float mist, float far)
    {
        Mist = mist;
        Fog = ViewDistances.Fog(mist, far, Haze);
        if (Atmosphere != null) ViewDistances.Apply(Atmosphere, Fog, _horizon, _horizonEnergy);
    }

    public override void _Process(double delta)
    {
        var camera = Camera != null && IsInstanceValid(Camera) && Camera.IsInsideTree() ? Camera : GetViewport()?.GetCamera3D();
        if (camera != null) Follow(camera);
    }
}
