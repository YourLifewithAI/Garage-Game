using System;
using System.Collections.Generic;
using System.Text;

namespace GarageGame.Native;

/// <summary>
/// The data-format ids (contracts/README.md, "Schema ids"; the Python table is contracts/schema_ids.py, GDScript's
/// scripts/schema_ids.gd, and contracts/tests/test_schema_ids.py checks the three agree). Every document names its kind
/// in its schema field: the game writes garage_game.&lt;kind&gt;; files written before the rename (decision C, 24 October
/// 2026) say enfractal.&lt;kind&gt;, and pinned files (rooms, presets, rules packs, the characters) keep that spelling for
/// as long as they exist, so a reader accepts both for good. Writers write the constants below; readers ask
/// <see cref="IsSchema"/> and never compare the string themselves. A mutable file read with the old id takes the new one
/// the next time it is saved; a creation source keeps the spelling it was written with (its hash pins it).
/// </summary>
public static class SchemaIds
{
    public const string Command = "garage_game.command";
    public const string Query = "garage_game.query";
    public const string Result = "garage_game.result";
    public const string Creation = "garage_game.creation";
    public const string Room = "garage_game.room";
    public const string Asset = "garage_game.asset";
    public const string Style = "garage_game.style";
    public const string RoomState = "garage_game.room_state";
    public const string IslandRules = "garage_game.island_rules";
    public const string Team = "garage_game.team";
    public const string PlayerModes = "garage_game.player_modes";
    public const string GlowProgress = "garage_game.glow_progress";
    public const string PlaytestManifest = "garage_game.playtest_manifest";
    public const string LastGame = "garage_game.last_game";
    public const string CarriedBuilds = "garage_game.carried_builds";
    public const string CompanionSession = "garage_game.companion_session";
    public const string CompanionLink = "garage_game.companion_link";
    public const string CreationWorld = "garage_game.creation-world";
    public const string WishQueue = "garage_game.wish_queue";
    public const string LegacyMigration = "garage_game.legacy_migration";
    public const string CharacterDraft = "garage_game.character.draft";
    public const string CharacterRig = "garage_game.character.rig";
    public const string CharacterSheets = "garage_game.character_sheets";

    /// <summary>kind: (the id written, the id written before the rename). The alignment test reads these lines.</summary>
    private static readonly Dictionary<string, (string Id, string OldId)> Table = new(StringComparer.Ordinal)
    {
        ["command"] = (Command, "enfractal.command"),
        ["query"] = (Query, "enfractal.query"),
        ["result"] = (Result, "enfractal.result"),
        ["creation"] = (Creation, "enfractal.creation"),
        ["room"] = (Room, "enfractal.room"),
        ["asset"] = (Asset, "enfractal.asset"),
        ["style"] = (Style, "enfractal.style"),
        ["room_state"] = (RoomState, "enfractal.room_state"),
        ["island_rules"] = (IslandRules, "enfractal.island_rules"),
        ["team"] = (Team, "enfractal.team"),
        ["player_modes"] = (PlayerModes, "enfractal.player_modes"),
        ["glow_progress"] = (GlowProgress, "enfractal.glow_progress"),
        ["playtest_manifest"] = (PlaytestManifest, "enfractal.playtest_manifest"),
        ["last_game"] = (LastGame, "enfractal.last_game"),
        ["carried_builds"] = (CarriedBuilds, "enfractal.carried_builds"),
        ["companion_session"] = (CompanionSession, "enfractal.companion_session"),
        ["companion_link"] = (CompanionLink, "enfractal.companion_link"),
        ["creation-world"] = (CreationWorld, "enfractal.creation-world"),
        ["wish_queue"] = (WishQueue, "enfractal.wish_queue"),
        ["legacy_migration"] = (LegacyMigration, "enfractal.legacy_migration"),
        ["character.draft"] = (CharacterDraft, "enfractal.character.draft"),
        ["character.rig"] = (CharacterRig, "enfractal.character.rig"),
        ["character_sheets"] = (CharacterSheets, "enfractal.character_sheets"),
    };

    /// <summary>A character GLB node's extras key, as the converter writes it now and as it wrote it before.</summary>
    public static readonly string[] GlbPartKeys = { "garage_game_part", "enfractal_part" };
    /// <summary>A character GLB material's extras key, as the converter writes it now and as it wrote it before.</summary>
    public static readonly string[] GlbMaterialKeys = { "garage_game_material", "enfractal_material" };

    /// <summary>The id a writer writes for kind.</summary>
    public static string Of(string kind) => Entry(kind).Id;

    /// <summary>Whether value names kind in either spelling. A kind the table does not have is a mistake in the reader: it throws.</summary>
    public static bool IsSchema(string? value, string kind)
    {
        var (id, old) = Entry(kind);
        return value != null && (string.Equals(value, id, StringComparison.Ordinal) || string.Equals(value, old, StringComparison.Ordinal));
    }

    /// <summary>Whether value is an id in the spelling written before the rename.</summary>
    public static bool IsOld(string? value)
    {
        foreach (var (_, old) in Table.Values)
            if (string.Equals(value, old, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// Whether the bytes a store read differ from the bytes it would write now only in the spelling of kind's id (the old one where
    /// the new is written, compact JSON). A store that skips a save that changes nothing skips this one too, so a file the player
    /// did not touch keeps its bytes; it takes the new id when something in it changes.
    /// </summary>
    public static bool OnlyTheIdDiffers(ReadOnlySpan<byte> earlier, byte[] now, string kind)
    {
        var (id, old) = Entry(kind);
        var written = Encoding.UTF8.GetString(now);
        var needle = $"\"schema\":\"{id}\"";
        var at = written.IndexOf(needle, StringComparison.Ordinal);
        return at >= 0 && earlier.SequenceEqual(Encoding.UTF8.GetBytes(string.Concat(written.AsSpan(0, at), $"\"schema\":\"{old}\"", written.AsSpan(at + needle.Length))));
    }

    /// <summary>Both spellings of kind, the new first, for a refusal's words.</summary>
    public static string Both(string kind) => $"{Entry(kind).Id} (or {Entry(kind).OldId})";

    private static (string Id, string OldId) Entry(string kind) =>
        Table.TryGetValue(kind, out var entry) ? entry : throw new ArgumentException($"no schema kind {kind}", nameof(kind));
}
