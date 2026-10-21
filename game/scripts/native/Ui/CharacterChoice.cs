using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;

namespace GarageGame.Native;

/// <summary>
/// One drawn character the player may choose in the Customize panel (the characters round, K6; docs/runs/RUN-2-CHARACTERS.md):
/// its id (its folder's, a token), the name it shows (its character.json's "name") and its thumbnail (its three_quarter.png,
/// LoadThumbnail), which may be null. The composition root fills the HUD's list from the character library
/// (RoomHud.CharacterChoices). The prototype figure is the HUD's own and the family's Gubble is never the player's, so neither
/// belongs in the list; the HUD leaves them out if they come.
/// </summary>
public sealed record CharacterChoice(string Id, string DisplayName, Texture2D? Thumbnail = null)
{
    /// <summary>
    /// The row as shown, from what the library supplies: the plain figure first, then each supplied character in order, leaving
    /// out the Gubble, the plain figure's own id, ids that are not tokens and repeats (the first stays); names are cleaned
    /// (ShownName). Used by the opening menu's picker (AvatarPicker) and by the HUD to know which costumes a body may wear.
    /// </summary>
    public static IReadOnlyList<CharacterChoice> RowOf(IReadOnlyList<CharacterChoice>? supplied)
    {
        var row = new List<CharacterChoice> { new(PrototypeId, PrototypeName) };
        var seen = new HashSet<string>(StringComparer.Ordinal) { PrototypeId, GubbleId };
        foreach (var choice in supplied ?? Array.Empty<CharacterChoice>())
        {
            if (choice == null || !SmallPlayerController.IsCharacterToken(choice.Id) || !seen.Add(choice.Id)) continue;
            row.Add(choice with { DisplayName = ShownName(choice.DisplayName, choice.Id) });
        }
        return row;
    }

    /// <summary>
    /// The shipped characters as choices (the Gubble left out; each refusal logged), with their thumbnails from the converter's renders
    /// beside the source models (assets/characters/&lt;id&gt;/three_quarter.png): a build without them draws a named swatch instead.
    /// </summary>
    public static IReadOnlyList<CharacterChoice> FromLibrary(GarageGame.Native.Characters.CharacterLibrary library)
    {
        foreach (var entry in library.Entries)
            if (!entry.IsLoaded) GD.PushWarning($"CHARACTERS: {entry.Id} refused: {entry.RefusalReason}");
        var renders = System.IO.Path.GetFullPath(System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "..", "assets", "characters"));
        return library.Characters.Where(c => c.Id != GubbleId)
            .Select(c => new CharacterChoice(c.Id, c.DisplayName, LoadThumbnail(System.IO.Path.Combine(renders, c.Id)))).ToList();
    }

    /// <summary>The prototype figure's id: none (SmallPlayerController.CharacterId is "" for it).</summary>
    public const string PrototypeId = "";
    /// <summary>The prototype figure's name in the row.</summary>
    public const string PrototypeName = "Plain figure";
    /// <summary>The family's Gubble (its folder's id): the companion's body for every player (decided), never in the player's row.</summary>
    public const string GubbleId = "gubble";
    /// <summary>The longest name the row shows; a longer one is cut.</summary>
    public const int MaxNameLength = 24;
    /// <summary>The thumbnail's file in a character's folder (the converter writes it).</summary>
    public const string ThumbnailFile = "three_quarter.png";
    /// <summary>The thumbnail's size: the 640 px render is scaled down once, so the row never shrinks a big texture every frame.</summary>
    public const int ThumbnailPx = 128;

    /// <summary>The largest thumbnail file read (the converter's renders are about 0.4 MB).</summary>
    public const int MaxThumbnailBytes = 4 * 1024 * 1024;
    /// <summary>The largest thumbnail decoded, pixels a side (the converter renders 640 px): a bigger PNG is never decoded.</summary>
    public const int MaxThumbnailSidePx = 2048;

    /// <summary>
    /// A character folder's thumbnail (its ThumbnailFile, the folder an absolute path), scaled to fit ThumbnailPx. Null when
    /// the folder has none, when the file is over MaxThumbnailBytes, when its PNG header says it is more than MaxThumbnailSidePx a
    /// side (read before decoding), or when it does not read as an image: the row then draws a swatch.
    /// </summary>
    public static Texture2D? LoadThumbnail(string folder, int sizePx = ThumbnailPx)
    {
        if (string.IsNullOrEmpty(folder) || sizePx < 8) return null;
        var path = folder.TrimEnd('/', '\\') + "/" + ThumbnailFile;
        if (!Godot.FileAccess.FileExists(path)) return null;
        byte[] bytes;
        using (var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read))
        {
            if (file == null) return null;
            var length = file.GetLength();
            if (length == 0 || length > MaxThumbnailBytes) return null;
            bytes = file.GetBuffer((long)length);
            if ((ulong)bytes.Length != length) return null;
        }
        if (!PngSize(bytes, out var width, out var height) || width > MaxThumbnailSidePx || height > MaxThumbnailSidePx) return null;
        var image = new Image();
        if (image.LoadPngFromBuffer(bytes) != Error.Ok || image.IsEmpty()) return null;
        var scale = Mathf.Min(1f, (float)sizePx / Mathf.Max(image.GetWidth(), image.GetHeight()));
        if (scale < 1f)
            image.Resize(Mathf.Max(1, Mathf.RoundToInt(image.GetWidth() * scale)), Mathf.Max(1, Mathf.RoundToInt(image.GetHeight() * scale)), Image.Interpolation.Lanczos);
        return ImageTexture.CreateFromImage(image);
    }

    /// <summary>
    /// A PNG's width and height from its header (the signature, then the IHDR chunk, which must come first), without decoding
    /// it; false when the bytes do not start as a PNG.
    /// </summary>
    public static bool PngSize(System.ReadOnlySpan<byte> png, out int width, out int height)
    {
        width = height = 0;
        System.ReadOnlySpan<byte> signature = stackalloc byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        if (png.Length < 33 || !System.MemoryExtensions.SequenceEqual(png[..8], signature) || System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png[8..]) != 13 ||
            !System.MemoryExtensions.SequenceEqual(png.Slice(12, 4), "IHDR"u8)) return false;
        var w = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png[16..]);
        var h = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png[20..]);
        if (w == 0 || h == 0 || w > int.MaxValue || h > int.MaxValue) return false;
        width = (int)w;
        height = (int)h;
        return true;
    }

    /// <summary>
    /// The name as the row shows it: control characters and runs of spaces folded to one space, trimmed, at most MaxNameLength;
    /// the id when nothing is left. A name is the drawing's data, never anything more.
    /// </summary>
    public static string ShownName(string? name, string id)
    {
        var shown = new StringBuilder();
        foreach (var c in name ?? "")
        {
            var space = char.IsControl(c) || char.IsWhiteSpace(c);
            if (space && (shown.Length == 0 || shown[^1] == ' ')) continue;
            shown.Append(space ? ' ' : c);
        }
        var text = shown.ToString().TrimEnd();
        if (text.Length > MaxNameLength) text = text[..MaxNameLength].TrimEnd();
        return text.Length > 0 ? text : id;
    }

    /// <summary>
    /// The swatch drawn for a choice without a thumbnail: the prototype figure (a body, a head and two eyes) in its colour for
    /// the prototype, a rounded tile in the character's own steady colour for a drawn one.
    /// </summary>
    public static ImageTexture Swatch(string id, Color prototypeColor)
    {
        const int size = 64;
        var image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        image.Fill(new Color(0, 0, 0, 0));
        if (id.Length == 0)
        {
            var ink = prototypeColor.Darkened(0.55f);
            for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    // A capsule body under a round head, as the figure drawn in code (SmallPlayerController.BuildVisual).
                    var body = CapsuleDistance(p, new Vector2(32, 34), new Vector2(32, 52), 11);
                    var head = p.DistanceTo(new Vector2(32, 18)) - 11;
                    var eye = Mathf.Min(p.DistanceTo(new Vector2(28, 17)), p.DistanceTo(new Vector2(36, 17))) - 1.8f;
                    var inside = Mathf.Min(body, head);
                    if (eye <= 0) image.SetPixel(x, y, ink);
                    else if (inside <= -1.2f) image.SetPixel(x, y, prototypeColor);
                    else if (inside <= 0) image.SetPixel(x, y, ink);
                }
        }
        else
        {
            var tile = Color.FromHsv(SteadyHue(id), 0.42f, 0.86f);
            var edge = tile.Darkened(0.35f);
            for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    var inside = RoundedSquareDistance(p, new Vector2(32, 32), 27, 9);
                    if (inside <= -1.5f) image.SetPixel(x, y, tile);
                    else if (inside <= 0) image.SetPixel(x, y, edge);
                }
        }
        return ImageTexture.CreateFromImage(image);
    }

    /// <summary>The same hue for the same id on every run and machine (FNV-1a; string.GetHashCode changes per process).</summary>
    public static float SteadyHue(string id)
    {
        var hash = 2166136261u;
        foreach (var c in id)
        {
            hash ^= c;
            hash *= 16777619u;
        }
        return hash % 360u / 360f;
    }

    private static float CapsuleDistance(Vector2 p, Vector2 a, Vector2 b, float radius)
    {
        var ab = b - a;
        var t = Mathf.Clamp((p - a).Dot(ab) / ab.LengthSquared(), 0, 1);
        return p.DistanceTo(a + ab * t) - radius;
    }

    private static float RoundedSquareDistance(Vector2 p, Vector2 centre, float half, float corner)
    {
        var q = (p - centre).Abs() - new Vector2(half - corner, half - corner);
        return new Vector2(Mathf.Max(q.X, 0), Mathf.Max(q.Y, 0)).Length() + Mathf.Min(Mathf.Max(q.X, q.Y), 0) - corner;
    }
}
