using System;
using Godot;
using GarageGame.Native.Saves;

namespace GarageGame.Native;

/// <summary>
/// The team's looks on the bodies (docs/game/MECHANICS.md sections 2 and 3): the team file the menu wrote names the costume the player
/// wears and the Gubble's name and colour, and the game's boot hands it here once the host has read it (RoomWorld). It replaces what
/// the machine's appearance profile gave the HUD at the start (the profile still seeds a team the menu never made, and holds the plain
/// figure's colour). A colour that is not one of the five (a hand-edited file) leaves the Gubble's as it was; a costume the library
/// does not offer is the plain figure.
/// </summary>
public partial class RoomHud
{
    public void UseTeam(TeamFile team)
    {
        var colour = Array.FindIndex(MachineSettings.AppearancePalette, hex => string.Equals(hex, team.GubbleColour, StringComparison.OrdinalIgnoreCase));
        if (colour >= 0)
        {
            _companionColor = colour;
            Companion.SetAppearance(Palette[colour]);
            ApplyAura();
        }
        Companion.SetDisplayName(CompanionAvatar.SavedName(team.GubbleName));
        _rowChoices = null;
        var costume = team.Costume == TeamFile.PrototypeCostume ? CharacterChoice.PrototypeId : team.Costume;
        Player.SetCharacterId(RowHas(costume) ? costume : CharacterChoice.PrototypeId);
    }
}
