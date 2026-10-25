namespace GarageGame.Native.Kernel;

/// <summary>What the Esc menu's Save answered: whether everything waiting was written, and the words for the player.</summary>
public readonly record struct SaveNowOutcome(bool Saved, string Words);

/// <summary>
/// The Esc menu's Save (the second playtest round's P2, decision 81): the one entry point the menu needed that did not exist. The island is
/// saved as it is played (every durable command writes it at once, Trust and the team file are written at once), and three things wait for
/// a throttle: the nature block with its weather clock, the team block (journal, map, open tasks) and the player's vitals. This writes
/// whatever of those waits, now, by the same steps the room's exit takes (SaveNatureOnExit, SaveTeam, FlushPlayerVitals): no new save
/// path, nothing written that the throttle would not have written a moment later. A held island writes nothing (its own words say why).
/// </summary>
public partial class CommandHost
{
    public const string SavedNowWords = "Saved.";
    public const string NotAllSavedWords = "Not everything could be saved just now; the game keeps trying as you play.";

    /// <summary>Write now what waits for a throttle (nature, the team block, the vitals). Never while the island is held.</summary>
    public SaveNowOutcome SaveNow()
    {
        if (IslandHeld) return new(false, TeamNotice);
        SaveNatureOnExit();
        if (_teamDirty) SaveTeam();
        FlushPlayerVitals();
        var waiting = _natureDirty || _teamDirty || (_vitalsDirty && !_playerVitalsUnavailable);
        return waiting ? new(false, NotAllSavedWords) : new(true, SavedNowWords);
    }
}
