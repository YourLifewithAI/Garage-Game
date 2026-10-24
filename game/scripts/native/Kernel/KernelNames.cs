using Godot;

namespace GarageGame.Native.Kernel;

/// <summary>
/// The GDScript kernel's method and property names (the creation authority, the invention runtime), each a StringName made once
/// for the game (S12). A string passed where Godot wants a StringName makes a new one for every call: a Godot value left to the
/// .NET finalizer, which frees it later on its own thread while the main thread goes on using the engine.
/// </summary>
internal static class KernelNames
{
    public static readonly StringName
        AdoptEnvelope = "adopt_envelope",
        Assemblies = "assemblies",
        Authority = "authority",
        CommandSink = "command_sink",
        CompactedReceipt = "compacted_receipt",
        Configure = "configure",
        EntityRevision = "entity_revision",
        ExecuteActivation = "execute_activation",
        ExportEnvelope = "export_envelope",
        IsLocked = "is_locked",
        IsReady = "is_ready",
        MayChange = "may_change",
        Nature = "nature",
        NatureCheck = "nature_check",
        NatureDisabledCreations = "nature_disabled_creations",
        Notice = "notice",
        ObjectPoseCheck = "object_pose_check",
        ObjectPoseSink = "object_pose_sink",
        PermissionRevision = "permission_revision",
        Preflight = "preflight",
        Preset = "preset",
        ReceiptCount = "receipt_count",
        ReceiptFor = "receipt_for",
        RefreshNow = "refresh_now",
        Revision = "revision",
        SavePath = "save_path",
        SetNature = "set_nature",
        SetTeam = "set_team",
        Snapshot = "snapshot",
        StopEffects = "stop_effects",
        Submit = "submit",
        Team = "team",
        TeamCheck = "team_check";

    /// <summary>The room's nodes' metadata names (RoomBuilder), made once.</summary>
    public static readonly StringName EntityIdMeta = "entity_id", MaterialRoleMeta = "material_role";

    /// <summary>A ray answer's keys (DirectSpaceState.IntersectRay), as Variants made once.</summary>
    public static readonly Variant HitPosition = "position", HitNormal = "normal", HitCollider = "collider";

    /// <summary>
    /// No exclusions, for a ray that leaves nothing out: PhysicsRayQueryParameters3D.Create, given none, makes an empty Godot array
    /// of its own every call, left to the finalizer. Never added to.
    /// </summary>
    public static readonly Godot.Collections.Array<Rid> NoRids = new();

    /// <summary>The principals as call arguments, made once.</summary>
    public static readonly Variant PlayerArgument = CommandHost.PlayerPrincipal, CompanionArgument = CommandHost.CompanionPrincipal;
}
