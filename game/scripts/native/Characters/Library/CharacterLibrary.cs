using System;
using System.Collections.Generic;
using System.Diagnostics;
using Godot;

namespace GarageGame.Native.Characters;

/// <summary>One folder under a library root: loaded, or refused with a reason.</summary>
public sealed class CharacterEntry
{
    public required string Id { get; init; }
    public required string Folder { get; init; }
    /// <summary>The validated character, or null when the folder was refused.</summary>
    public CharacterDefinition? Definition { get; init; }
    /// <summary>Why the folder was refused (for logs and the HUD); null when it loaded.</summary>
    public string? RefusalReason { get; init; }
    public bool IsLoaded => Definition != null;
}

/// <summary>
/// Every character folder under one root, read and validated once. The shipped five live in
/// <see cref="ShippedRoot"/> (res://characters, copied from assets/characters by tools/characters/sync_characters.py);
/// a player's own will live in <see cref="PlayerRoot"/>; tests pass any absolute path. A refused folder never stops
/// the others, and asking for it (or for an unknown id) gives null: the caller keeps the prototype body.
/// </summary>
public sealed class CharacterLibrary
{
    public const string ShippedRoot = "res://characters";
    public const string PlayerRoot = "user://characters";
    /// <summary>A root holding more folders than this is refused past the limit (a player's folder is untrusted).</summary>
    public const int MaxFolders = 256;

    private readonly List<CharacterEntry> _entries;

    private CharacterLibrary(string root, List<CharacterEntry> entries, double milliseconds)
    {
        Root = root;
        _entries = entries;
        LoadMilliseconds = milliseconds;
    }

    public string Root { get; }
    /// <summary>Every folder found, sorted by id (ordinal), loaded or refused.</summary>
    public IReadOnlyList<CharacterEntry> Entries => _entries;
    /// <summary>Wall time of <see cref="Load"/>: listing, reading, validating and building the meshes.</summary>
    public double LoadMilliseconds { get; }

    /// <summary>The loaded characters, sorted by id.</summary>
    public IEnumerable<CharacterDefinition> Characters
    {
        get
        {
            foreach (var entry in _entries)
                if (entry.Definition != null) yield return entry.Definition;
        }
    }

    /// <summary>
    /// Reads every folder under <paramref name="root"/>, in id order (the same folders always give the same library).
    /// A missing root is an empty library, not an error. Call it on the main thread: it makes meshes and materials.
    /// </summary>
    public static CharacterLibrary Load(string root)
    {
        var clock = Stopwatch.StartNew();
        root = root.Replace('\\', '/').TrimEnd('/');
        var names = new List<string>();
        using (var directory = DirAccess.Open(root))
        {
            if (directory != null)
            {
                directory.IncludeHidden = false;
                foreach (var name in directory.GetDirectories())
                    if (!name.StartsWith('.')) names.Add(name);
            }
        }
        names.Sort(StringComparer.Ordinal);
        var entries = new List<CharacterEntry>(names.Count);
        foreach (var name in names)
        {
            var folder = root + "/" + name;
            entries.Add(entries.Count < MaxFolders
                ? LoadFolder(folder, name)
                : new CharacterEntry { Id = name, Folder = folder, RefusalReason = $"more than {MaxFolders} character folders" });
        }
        return new CharacterLibrary(root, entries, clock.Elapsed.TotalMilliseconds);
    }

    /// <summary>Reads one folder; never throws for a bad folder (the entry carries the reason).</summary>
    public static CharacterEntry LoadFolder(string folder, string id)
    {
        try
        {
            return new CharacterEntry { Id = id, Folder = folder, Definition = CharacterFolder.Read(folder, id) };
        }
        catch (CharacterLoadException refused)
        {
            return new CharacterEntry { Id = id, Folder = folder, RefusalReason = refused.Message };
        }
        catch (Exception unexpected) when (unexpected is not OutOfMemoryException)
        {
            // A player's folder must never stop the game; an unforeseen failure is a refusal too, named for the log.
            return new CharacterEntry { Id = id, Folder = folder, RefusalReason = $"could not be read ({unexpected.GetType().Name}: {unexpected.Message})" };
        }
    }

    public CharacterEntry? Entry(string? id)
    {
        if (id == null) return null;
        foreach (var entry in _entries)
            if (string.Equals(entry.Id, id, StringComparison.Ordinal)) return entry;
        return null;
    }

    public bool TryGet(string? id, out CharacterDefinition definition)
    {
        definition = Entry(id)?.Definition!;
        return definition != null;
    }

    /// <summary>
    /// A new body for <paramref name="id"/>, or null when the id is null, unknown or refused: the caller keeps the
    /// prototype figure. The reason for a refusal is in <see cref="Entry"/>.
    /// </summary>
    public CharacterBody? Build(string? id)
    {
        return TryGet(id, out var definition) ? CharacterRig.Build(definition) : null;
    }
}
