using System;
using Godot;

namespace GarageGame.Native.Saves;

/// <summary>
/// The machine's settings file (user://single_player/room/avatar_profile_v1.cfg, section [profile]; RoomHud.ProfilePath): the
/// switches that belong to the machine, never to a team (docs/game/MECHANICS.md section 2, decided), stay here as today, and the
/// teams packet settles where each key lives:
/// <list type="bullet">
/// <item>"Let my AI see through the Gubble's eyes": <c>ai_eyes</c> (CommandHost.EyesProfileKey), default on. It lives here, with the
/// machine, not in a team file: it is a grown-up's consent for the AI on this computer, whichever team plays, and the companion
/// surface never sees the team folder anyway.</item>
/// <item>"Let the Gubble use fire": <c>gubble_fire</c> (CommandHost.FireProfileKey), default on.</item>
/// <item>"Play the intro": <c>play_intro</c> (RoomHud.PlayIntroProfileKey), default on.</item>
/// <item>The numbers on the meters: <c>meter_numbers</c> (RoomHud.NumbersProfileKey, M2), default off.</item>
/// <item>Full screen: <c>fullscreen</c> (RoomHud.FullscreenProfileKey, decision 66 of 16 October), default off. The full-screen key writes it;
/// every launch then opens in full screen until the key is pressed again, and "--fullscreen" (run-room.ps1 -Fullscreen) forces one launch.
/// Headless runs, boot checks and the tools' own scenes (the look's captures) never read it: only the game's own boot does.</item>
/// </list>
/// The same file still keeps the HUD's appearance (the colours, the Gubble's name, the character worn), which belongs to the team:
/// the first team is seeded from it (SeedTeam), and the menu (M7) moves the Customize panel onto the team file. Nothing here is
/// written by the teams packet.
/// </summary>
public static class MachineSettings
{
    public const string Section = "profile";
    public const string LetMyAiSeeKey = Kernel.CommandHost.EyesProfileKey;
    public const string GubbleFireKey = Kernel.CommandHost.FireProfileKey;
    public const string PlayIntroKey = RoomHud.PlayIntroProfileKey;
    public const string MeterNumbersKey = RoomHud.NumbersProfileKey;
    public const string FullscreenKey = RoomHud.FullscreenProfileKey;
    /// <summary>The switches, in the order the design lists them, the window's after the others.</summary>
    public static readonly string[] SwitchKeys = { LetMyAiSeeKey, GubbleFireKey, PlayIntroKey, MeterNumbersKey, FullscreenKey };

    /// <summary>The HUD's appearance palette (RoomHud's colours, by the index the settings file keeps), for the Gubble's colour in a seeded team.</summary>
    public static readonly string[] AppearancePalette = { "#d28f63", "#65b9b0", "#d7b765", "#a18cc3", "#75965c" };
    /// <summary>The Gubble's colour when the settings name none (the HUD's default companion colour, index 1).</summary>
    public const int DefaultCompanionColour = 1;

    /// <summary>
    /// The first team, made from what this machine's settings say about the player today (the single-player profile the saves
    /// before teams belonged to): the character worn (the prototype figure when none), the Gubble's name (Wisp read as the Gubble,
    /// as the HUD does) and colour. The player's display name is "Player" (the save's own word for the player's avatar) until
    /// the menu names the team. A settings file that does not read gives the defaults; it is never written.
    /// </summary>
    public static TeamFile SeedTeam(string settingsPath, string teamId, DateTime createdUtc)
    {
        var costume = TeamFile.PrototypeCostume;
        var name = CompanionAvatar.DefaultName;
        var colour = DefaultCompanionColour;
        using var config = new ConfigFile();
        if (System.IO.File.Exists(settingsPath) && config.Load(settingsPath) == Error.Ok &&
            config.GetValue(Section, "version", 0) is { VariantType: Variant.Type.Int } version && version.AsInt32() == 1)
        {
            if (config.GetValue(Section, RoomHud.CharacterProfileKey, "") is { VariantType: Variant.Type.String } worn && SmallPlayerController.IsCharacterToken(worn.AsString()) &&
                worn.AsString() != CharacterChoice.GubbleId)
                costume = worn.AsString();
            if (config.GetValue(Section, "companion_name", "") is { VariantType: Variant.Type.String } saved && saved.AsString().Length > 0)
                name = Kernel.KernelText.DisplayText(CompanionAvatar.SavedName(saved.AsString()), TeamFile.MaxNameLength);
            if (config.GetValue(Section, "companion_color", DefaultCompanionColour) is { VariantType: Variant.Type.Int } index)
                colour = Mathf.PosMod(index.AsInt32(), AppearancePalette.Length);
        }
        try { return TeamFile.Create(teamId, createdUtc, "Player", costume, name.Length > 0 ? name : CompanionAvatar.DefaultName, AppearancePalette[colour]); }
        catch (TeamFileException) { return TeamFile.Create(teamId, createdUtc, "Player", TeamFile.PrototypeCostume, CompanionAvatar.DefaultName, AppearancePalette[DefaultCompanionColour]); }
    }
}
