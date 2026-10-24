using System.Text.Json.Nodes;

namespace GarageGame.Native.Kernel;

/// <summary>
/// The journal book's two doors into the host (Ui/JournalPanel.cs): the player's note, written through journal.note like the
/// companion's, and the player's read-only queries (player.status, journal.read). Nothing here bypasses the command path.
/// </summary>
public partial class CommandHost
{
    /// <summary>The player's own words on the journal's page, as a journal.note command from the player.</summary>
    public JsonObject PlayerJournalNote(string text) => PlayerCommand("journal.note", new JsonObject { ["text"] = text });

    /// <summary>A read-only query as the player (player.status, journal.read and the other query ops).</summary>
    public JsonObject PlayerQuery(string op, JsonObject? args = null) => HandleObject(CanonicalJson.Text(new JsonObject
    {
        ["schema"] = SchemaIds.Query, ["version"] = 1, ["query_id"] = $"hud-query-{_session}-{++_counter}",
        ["room_id"] = Room.RoomId, ["op"] = op, ["args"] = args ?? new JsonObject(),
    }), PlayerPrincipal);
}
