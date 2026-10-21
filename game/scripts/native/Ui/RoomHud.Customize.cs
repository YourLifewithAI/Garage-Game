using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace GarageGame.Native;

/// <summary>
/// The player's character on the body. The Customize panel ("Your two avatars": the row of the plain figure and the drawn
/// characters, the player's colour, the Gubble's name and colour, Save appearance) moved out of the HUD into the opening menu's "New
/// team" panel (Ui/Menu/AvatarPicker.cs and MainMenu.NewTeam.cs; docs/game/MECHANICS.md section 3), where the team file keeps the choice.
/// What stays here is the seam to the character library: the composition root supplies the shipped characters (RoomHud.CharacterChoices),
/// and the body wears the one the team file names (RoomHud.UseTeam), or the one an older machine profile saved (a fixture without a
/// team). Only a character the library offers is worn, never the family's Gubble; anything else is the plain figure.
/// </summary>
public partial class RoomHud
{
    /// <summary>The profile's key for the character an older profile saved: its id, or "" for the plain figure. Read at the start; the team file's costume replaces it.</summary>
    public const string CharacterProfileKey = "player_character";

    /// <summary>
    /// The seam the composition root fills from the character library: the drawn characters the body may wear (their ids, names and
    /// thumbnails, CharacterChoice). Set it before the HUD enters the tree, so the saved choice is restored when the HUD loads the
    /// profile. Null, or nothing set: only the plain figure. The row leaves out the Gubble, the plain figure's own id, ids that are not tokens and repeats.
    /// </summary>
    public Func<IReadOnlyList<CharacterChoice>?>? CharacterChoices
    {
        get => _characterChoices;
        set { _characterChoices = value; _rowChoices = null; }
    }

    private Func<IReadOnlyList<CharacterChoice>?>? _characterChoices;
    private IReadOnlyList<CharacterChoice>? _rowChoices;

    /// <summary>The characters the body may wear, in order, the plain figure first (CharacterChoice.RowOf).</summary>
    public IReadOnlyList<CharacterChoice> CharacterRow => _rowChoices ??= CharacterChoice.RowOf(CharacterChoices?.Invoke());

    /// <summary>The character the body wears: its CharacterId when the row has it, else the plain figure ("").</summary>
    public string ChosenCharacterId => RowHas(Player.CharacterId) ? Player.CharacterId : CharacterChoice.PrototypeId;

    /// <summary>A drawn character is worn (not the plain figure).</summary>
    public bool DrawnCharacterChosen => ChosenCharacterId.Length > 0;

    private bool RowHas(string id) => CharacterRow.Any(choice => choice.Id == id);

    /// <summary>Wear a character by its id ("" for the plain figure). An id the row does not show is refused.</summary>
    public bool ChooseCharacter(string id) => id != null && RowHas(id) && Player.SetCharacterId(id);

    /// <summary>
    /// The profile's character, loaded: a character the row shows is worn (through Player.SetCharacterId); anything else (a
    /// missing key, not a string, not a token, the Gubble, a character the library does not have) is the plain figure.
    /// </summary>
    private void RestoreCharacter(Variant saved)
    {
        var id = saved.VariantType == Variant.Type.String ? saved.AsString() : CharacterChoice.PrototypeId;
        Player.SetCharacterId(RowHas(id) ? id : CharacterChoice.PrototypeId);
    }
}
