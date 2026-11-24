using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Godot;
using FileAccess = System.IO.FileAccess;

namespace GarageGame.Native.Saves;

/// <summary>What asking for a team's play lease gave (PlayLease.Ask).</summary>
public enum PlayLeaseAnswer
{
    /// <summary>The hold was given.</summary>
    Taken,
    /// <summary>Someone else holds the team in a way that refuses this hold: a room that is up, or a put-away at work.</summary>
    Held,
    /// <summary>
    /// The lease file could not be opened at all (a folder that cannot be written, a folder standing where the file belongs):
    /// nobody is known to hold the team, and waiting would not help.
    /// </summary>
    CannotOpen,
}

/// <summary>
/// The team's play lease (Run 4, MENU-2; docs/game/MECHANICS.md section 3, "Never under a running room"): one empty file,
/// <c>teams/&lt;team_id&gt;/in_play.lock</c>, held open by whoever must not have the team's saves moved under it.
/// <list type="bullet">
/// <item><b>A room that is up holds it to play</b> (TeamBoot.Prepare takes it before the host reads a save; PlayLeaseKeeper lets it
/// go once the host has written what it writes as it leaves). Rooms share it: two windows on one team play as they did before.</item>
/// <item><b>New game's put-away holds it alone</b> (GameSetAside.PutAway): it is refused while any room of the team is up, and a
/// room that starts meanwhile waits for it. So a game is never put away under a room that still writes as it leaves, and a
/// room never opens a game that is half moved.</item>
/// <item><b>A crash frees it.</b> The hold is an open file handle and nothing else: the operating system closes it when the
/// process ends, however it ends. Nothing is written in the file, and the game never deletes it.</item>
/// <item><b>Held is not the same as cannot be opened.</b> Only a sharing refusal means someone holds the team. Every hold opens
/// the file to read, never to write, so a lease file that came back read-only (a restored backup, a sync tool) is held and let
/// go like any other. A file that cannot be opened at all answers CannotOpen at once: no wait, and never "still open".</item>
/// </list>
/// A lease is the menu's and the boot's alone: not a command, nothing a request carries reaches it, and the companion surface
/// never sees it. On Windows the sharing rules are the file system's; on Linux they are .NET's advisory locks, which every
/// instance of this game honours (not yet run there).
/// </summary>
public sealed class PlayLease : IDisposable
{
    public const string FileName = "in_play.lock";
    /// <summary>What the player reads when New game finds the team's game still open (the family's to reword).</summary>
    public const string StillOpen = "That game is still open. Close it, then try again.";
    /// <summary>What the player reads when a room starts while the team's game is being put away.</summary>
    public const string BeingPutAway = "This team's game is being put away. Try again in a moment.";
    /// <summary>
    /// How long a start waits for a room that is leaving (after "Back to the menu" the old window still writes for a moment), and
    /// how long a room's boot waits for a put-away at work. Provisional: the menu page's 5 s.
    /// </summary>
    public static readonly TimeSpan LeavingWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(40);

    private FileStream? _file;

    public string TeamId { get; }
    /// <summary>True for the put-away's hold (nobody else holds the team), false for a room's (shared with other rooms).</summary>
    public bool Alone { get; }
    public bool Held => _file != null;

    private PlayLease(string team, bool alone, FileStream file)
    {
        TeamId = team;
        Alone = alone;
        _file = file;
    }

    public static string PathFor(TeamLayout layout, string team) => Path.Combine(layout.TeamFolder(team), FileName);

    /// <summary>A room's hold, shared with other rooms of the team; null while a put-away holds the team alone (or the file cannot be opened: Ask tells which).</summary>
    public static PlayLease? TryPlay(TeamLayout layout, string team) => Take(layout, team, alone: false, out var lease) == PlayLeaseAnswer.Taken ? lease : null;

    /// <summary>The put-away's hold; null while a room of the team is up, or another put-away is at work (or the file cannot be opened: Ask tells which).</summary>
    public static PlayLease? TryAlone(TeamLayout layout, string team) => Take(layout, team, alone: true, out var lease) == PlayLeaseAnswer.Taken ? lease : null;

    /// <summary>
    /// The hold, waited for at most <paramref name="upTo"/> (asked again every few hundredths of a second; the caller's thread
    /// waits, so keep it short). Null when it is still refused then, or cannot be opened. A wait of zero asks once.
    /// </summary>
    public static PlayLease? Wait(TeamLayout layout, string team, bool alone, TimeSpan upTo) => Ask(layout, team, alone, upTo, out var lease) == PlayLeaseAnswer.Taken ? lease : null;

    /// <summary>
    /// The hold, and why not when it is not given. While someone holds the team it is asked for again, for at most
    /// <paramref name="upTo"/>; a lease file that cannot be opened answers at once, since waiting changes nothing.
    /// </summary>
    public static PlayLeaseAnswer Ask(TeamLayout layout, string team, bool alone, TimeSpan upTo, out PlayLease? lease)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var answer = Take(layout, team, alone, out lease);
            if (answer != PlayLeaseAnswer.Held || clock.Elapsed >= upTo) return answer;
            Thread.Sleep(Poll);
        }
    }

    /// <summary>
    /// Whether anyone holds the team now: a room that is up (or still leaving), or a put-away at work. For the menu's buttons,
    /// which wait for a leaving room before they start anything. Asking makes nothing: a team with no folder is not in use, and
    /// neither is one whose lease file merely cannot be opened (nobody holds it; New game then says what is wrong).
    /// </summary>
    public static bool InUse(TeamLayout layout, string team)
    {
        var path = PathFor(layout, team);
        if (!File.Exists(path)) return false;
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (Exception error) when (FileError(error)) { return SomeoneHolds(error); }
    }

    private static PlayLeaseAnswer Take(TeamLayout layout, string team, bool alone, out PlayLease? lease)
    {
        lease = null;
        var path = PathFor(layout, team);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Alone: no other handle may be open at all. To play: other rooms may hold it to play too, and none may hold it alone.
            // Both open the file to read only: nothing is ever written in it, and a read-only lease file is held like any other.
            var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Read, alone ? FileShare.None : FileShare.Read);
            lease = new PlayLease(team, alone, file);
            return PlayLeaseAnswer.Taken;
        }
        catch (Exception error) when (FileError(error)) { return SomeoneHolds(error) ? PlayLeaseAnswer.Held : PlayLeaseAnswer.CannotOpen; }
    }

    /// <summary>Lets the team go. Safe to call twice.</summary>
    public void Dispose()
    {
        var file = Interlocked.Exchange(ref _file, null);
        file?.Dispose();
    }

    private static bool FileError(Exception error) => error is IOException or UnauthorizedAccessException or System.Security.SecurityException;

    /// <summary>
    /// Whether an open was refused because someone else has the file open (a sharing refusal), and not for any other reason. On
    /// Windows that is the system's own answer (ERROR_SHARING_VIOLATION 32, ERROR_LOCK_VIOLATION 33). Elsewhere .NET's advisory
    /// lock refuses with a plain IOException, and what is denied, missing or too long comes as its own kind.
    /// </summary>
    private static bool SomeoneHolds(Exception error) =>
        error is IOException and not (FileNotFoundException or DirectoryNotFoundException or PathTooLongException or DriveNotFoundException) &&
        (!OperatingSystem.IsWindows() || (error.HResult & 0xFFFF) is 32 or 33);
}

/// <summary>
/// Keeps a room's play lease for as long as the room is in the tree. TeamBoot.Prepare adds it to the room before the command
/// host is attached, and a node's children leave the tree last one first: so the host has written its island, its vitals and
/// its Trust (CommandHost._ExitTree) before this lets the team go. A game that quits or crashes frees the lease with its process.
/// </summary>
public partial class PlayLeaseKeeper : Node
{
    public PlayLease? Lease { get; set; }

    public override void _ExitTree()
    {
        Lease?.Dispose();
        Lease = null;
    }
}
