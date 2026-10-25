using System.Text.Json.Nodes;
using Godot;

namespace GarageGame.Native;

/// <summary>
/// The gather key (decisions 26 and 116 of docs/game/MECHANICS.md; the mechanics round's packet M1 built the command): the key sends
/// the player's item.gather for the loose log or stone the hands hold, through the host's one command path like every other key,
/// never by editing the room. The host decides and answers; the notice says what it answered and then what the pockets hold of logs
/// and stones now (P-Inventory-2: the materials wallet is retired, a gathered log or stone is a unit of its stack).
/// </summary>
public partial class RoomHud
{
    /// <summary>Gather what the hands hold into the pockets: E does it when the hands hold a loose log or stone (RoomHud.Interact.cs).</summary>
    public void Gather()
    {
        if (Host is not { } host) { _noticeText = "The command host is not attached; the gather key is off."; return; }
        var result = host.PlayerGather();
        _noticeText = GatherWords(result, host);
    }

    private static string GatherWords(JsonObject result, Kernel.CommandHost host) =>
        result["ok"]?.GetValue<bool>() == true
            ? "Gathered. In your pockets: " + Ui.InventoryPages.MaterialStacks(host, chestToo: false)
            : result["error"]?["message"]?.GetValue<string>() ?? "That could not be gathered.";
}
