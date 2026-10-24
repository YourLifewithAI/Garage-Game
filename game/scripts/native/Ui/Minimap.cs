using System;
using System.Diagnostics;
using Godot;
using GarageGame.Native.Kernel;
using GarageGame.Native.Look;
using GarageGame.Native.Room;

namespace GarageGame.Native.Ui;

/// <summary>
/// The minimap in the HUD's top-right corner, from the first minute (docs/game/MECHANICS.md section 6, "The minimap and the
/// overview"): the journal's map, drawn from what either avatar has seen. It is read from the same discovered map the journal
/// reads (DiscoveredMapData), nothing else: no ground, no thing, no height that no avatar has seen is ever drawn, and blank panel
/// is still unexplored. Like the journal's map it lies as the room does from above, -Z at the top, with a compass for a room
/// whose manifest says which way it faces. Ink on the player's own level is darker than other levels. The two avatars are marked
/// where they are: the player an arrow pointing the way the body faces, the Gubble a diamond in its own colour.
///
/// Cost: the host's discovered map is read and painted into a small texture at most once a second, and then only when an avatar
/// moved (or every few seconds while they stand, for sight that is still filling in); a frame draws that texture and two marks.
/// </summary>
public partial class Minimap : Control
{
    /// <summary>The map's picture, in pixels (square), and the panel's padding round it.</summary>
    public const int ViewPx = 168;
    public const float PadPx = 8f;
    /// <summary>The panel in the corner stays inside the corner the focus tag keeps clear (Sandbox.SandboxControls.FocusWords).</summary>
    public const float PanelPx = ViewPx + 2 * PadPx;
    /// <summary>The map is read again no oftener than this, and then only after an avatar moved this far (m)...</summary>
    public const double RebuildEveryS = 1.0;
    public const float MovedM = 0.04f;
    /// <summary>... or this long (seconds) after the last read, standing still: sight is still filling in round a body at rest.</summary>
    public const double RebuildStillS = 6.0;
    /// <summary>The picture shows at least this much of the world (metres across), so a first minute is not a postage stamp.</summary>
    public const float MinSideM = 1.0f;
    private const float MarginRatio = 1.06f;

    private static readonly Color Panel = new(0.075f, 0.13f, 0.13f, 0.86f);
    private static readonly Color Rim = new(0.55f, 0.66f, 0.62f, 0.9f);
    private static readonly Color OwnInk = new(0.58f, 0.76f, 0.44f, 0.95f);
    private static readonly Color OtherInk = new(0.46f, 0.6f, 0.42f, 0.42f);
    private static readonly Color ThingInk = new(0.86f, 0.7f, 0.5f, 0.85f);
    private static readonly Color PlayerInk = new("f6ecd4");

    private static readonly uint OwnWord = Word(OwnInk);
    private static readonly uint OtherWord = Word(OtherInk);

    private static uint Word(Color colour) =>
        (uint)Math.Round(colour.R * 255) | (uint)Math.Round(colour.G * 255) << 8 | (uint)Math.Round(colour.B * 255) << 16 | (uint)Math.Round(colour.A * 255) << 24;

    private readonly DiscoveredMapData _data = new();
    private byte[] _cover = new byte[ViewPx * ViewPx];
    private byte[] _pixels = new byte[ViewPx * ViewPx * 4];
    private Image? _image;
    private ImageTexture? _texture;
    private Vector2 _min;
    private float _side = MinSideM;
    private float _scale = ViewPx / MinSideM;
    private Vector2 _player, _companion, _builtPlayer, _builtCompanion;
    private float _yaw, _height;
    private Color _aura = new("a18cc3");
    private double _sinceBuild = double.MaxValue;
    private RoomData? _siteOf;
    private RoomSite? _site;
    private Vector2 _drawnPlayer = new(float.NaN, 0), _drawnCompanion;
    private float _drawnYaw;
    private bool _dirty = true;
    private bool _paintNext;

    /// <summary>How many times the map was read and painted (the suite holds the cadence).</summary>
    public int Builds { get; private set; }
    /// <summary>The last read in stages, in milliseconds: the host's export, the parse, the fit and paint (the cost probe reads it).</summary>
    public double[] StageMs { get; } = new double[3];
    /// <summary>What the last read and paint took, in milliseconds.</summary>
    public double LastBuildMs { get; private set; }
    /// <summary>Whether the map shows a compass: only for a room whose manifest declares its site.</summary>
    public bool ShowsCompass => _site is { Declared: true };
    /// <summary>Strips of discovered ground the picture was painted from, and things last seen.</summary>
    public int Strips => _data.Land.Count;
    public int Things => _data.Objects.Count;
    /// <summary>Pixels of the picture that hold ink.</summary>
    public int InkPixels { get; private set; }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        CustomMinimumSize = new Vector2(PanelPx, PanelPx);
        Size = CustomMinimumSize;
    }

    /// <summary>Whether a pixel of the picture (0 to ViewPx) holds ink: ground an avatar has seen.</summary>
    public bool InkAt(int x, int y) => x >= 0 && y >= 0 && x < ViewPx && y < ViewPx && _cover[y * ViewPx + x] != 0;

    /// <summary>The map coordinates (x, z in metres) of a pixel's middle, for the suite to hold each inked pixel against the host's map.</summary>
    public Vector2 MapOf(int x, int y) => _min + new Vector2(x + 0.5f, y + 0.5f) / _scale;

    /// <summary>The picture's pixel (0 to ViewPx) for a point of the map (x, z in metres): marks outside it are held at its edge.</summary>
    public Vector2 PixelOf(Vector2 mapXz) => (mapXz - _min) * _scale;

    /// <summary>The metres one pixel of the picture stands for.</summary>
    public float MetresPerPixel => 1f / _scale;

    public bool NeedsRead(Vector2 player, Vector2 companion) => _sinceBuild >= RebuildEveryS &&
        (_sinceBuild >= RebuildStillS || player.DistanceTo(_builtPlayer) > MovedM || companion.DistanceTo(_builtCompanion) > MovedM || Outside(player) || Outside(companion));

    private bool Outside(Vector2 mapXz)
    {
        var at = PixelOf(mapXz);
        return at.X < 0 || at.Y < 0 || at.X >= ViewPx || at.Y >= ViewPx;
    }

    /// <summary>
    /// Each frame: the avatars where they are (metres, the player's heading in radians about +Y), the Gubble's colour, and time
    /// passing. The first call reads the map at once; later reads follow the cadence above.
    /// </summary>
    public void Tick(CommandHost host, Vector3 player, float yaw, Vector3 companion, Color aura, double delta)
    {
        _sinceBuild += delta;
        _player = new Vector2(player.X, player.Z);
        _companion = new Vector2(companion.X, companion.Z);
        _height = player.Y;
        if (aura != _aura) { _aura = aura; _dirty = true; }
        // A read is two frames' work, so no frame carries all of it: the host's map is fetched and parsed in one, painted in the next.
        if (_paintNext) { _paintNext = false; Paint(); }
        else if (_texture == null || NeedsRead(_player, _companion))
        {
            Fetch(host);
            _paintNext = true;
        }
        var at = PixelOf(_player);
        var near = PixelOf(_companion);
        if (_dirty || !float.IsFinite(_drawnPlayer.X) || at.DistanceTo(_drawnPlayer) >= 0.5f || near.DistanceTo(_drawnCompanion) >= 0.5f || Mathf.Abs(Mathf.AngleDifference(yaw, _drawnYaw)) > 0.07f)
        {
            (_drawnPlayer, _drawnCompanion, _drawnYaw, _dirty) = (at, near, yaw, false);
            _yaw = yaw;
            QueueRedraw();
        }
    }

    /// <summary>Read the host's discovered map and paint the picture again, at once (the suite's way to force a read; play reads it in two frames).</summary>
    public void Read(CommandHost host)
    {
        Fetch(host);
        _paintNext = false;
        Paint();
    }

    /// <summary>The first half of a read: the host's discovered map, parsed, and the picture's window fitted to it.</summary>
    private void Fetch(CommandHost host)
    {
        var watch = Stopwatch.StartNew();
        var exported = host.ExportDiscovered();
        StageMs[0] = watch.Elapsed.TotalMilliseconds;
        _data.Read(exported);
        StageMs[1] = watch.Elapsed.TotalMilliseconds - StageMs[0];
        if (!ReferenceEquals(_siteOf, host.Room))
        {
            _siteOf = host.Room;
            _site = RoomSite.For(host.Room, out _);
        }
        Fit();
        _builtPlayer = _player;
        _builtCompanion = _companion;
        _sinceBuild = 0;
        LastBuildMs = watch.Elapsed.TotalMilliseconds;
    }

    /// <summary>The picture's window on the world: a square holding every discovered span and thing and both avatars, a little over, as large as the journal's map would draw it.</summary>
    private void Fit()
    {
        var box = new Rect2(_player - Vector2.One * 0.2f, Vector2.One * 0.4f).Expand(_companion);
        foreach (var land in _data.Land) box = box.Merge(land.Area);
        foreach (var thing in _data.Objects) box = box.Merge(thing.Area);
        _side = Math.Max(MinSideM, Math.Max(box.Size.X, box.Size.Y) * MarginRatio);
        _min = box.GetCenter() - Vector2.One * (_side * 0.5f);
        _scale = ViewPx / _side;
    }

    /// <summary>Each strip of discovered ground becomes the pixels it touches (a strip thinner than a pixel still marks one); the player's own level is inked darker.</summary>
    private void Paint()
    {
        var watch = Stopwatch.StartNew();
        Array.Clear(_cover);
        // The picture is written as the strips are laid: each pixel is its colour as a 32-bit word (red, green, blue and alpha, as the image holds them).
        var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(_pixels);
        words.Clear();
        var ink = 0;
        foreach (var (area, height) in _data.Land)
        {
            var own = Math.Abs(height - _height) < JournalMap.OwnLevelM;
            var value = (byte)(own ? 2 : 1);
            var word = own ? OwnWord : OtherWord;
            var x0 = Math.Clamp((int)Math.Floor((area.Position.X - _min.X) * _scale), 0, ViewPx - 1);
            var x1 = Math.Clamp((int)Math.Ceiling((area.End.X - _min.X) * _scale), x0 + 1, ViewPx);
            var y0 = Math.Clamp((int)Math.Floor((area.Position.Y - _min.Y) * _scale), 0, ViewPx - 1);
            var y1 = Math.Clamp((int)Math.Ceiling((area.End.Y - _min.Y) * _scale), y0 + 1, ViewPx);
            for (var y = y0; y < y1; y++)
            {
                var row = y * ViewPx;
                for (var x = x0; x < x1; x++)
                {
                    var level = _cover[row + x];
                    if (level >= value) continue;
                    if (level == 0) ink++;
                    _cover[row + x] = value;
                    words[row + x] = word;
                }
            }
        }
        InkPixels = ink;
        if (_image == null)
        {
            _image = Image.CreateFromData(ViewPx, ViewPx, false, Image.Format.Rgba8, _pixels);
            _texture = ImageTexture.CreateFromImage(_image);
        }
        else
        {
            _image.SetData(ViewPx, ViewPx, false, Image.Format.Rgba8, _pixels);
            _texture!.Update(_image);
        }
        StageMs[2] = watch.Elapsed.TotalMilliseconds;
        LastBuildMs += StageMs[2];
        _dirty = true;
        Builds++;
    }

    public override void _Draw()
    {
        var began = MeterCost.Begin();
        DrawMap();
        MeterCost.End(began, MeterCost.Piece.Map);
    }

    private void DrawMap()
    {
        DrawRect(new Rect2(0, 0, PanelPx, PanelPx), Panel);
        DrawRect(new Rect2(0.75f, 0.75f, PanelPx - 1.5f, PanelPx - 1.5f), Rim, false, 1.5f);
        var origin = new Vector2(PadPx, PadPx);
        if (_texture != null) DrawTexture(_texture, origin);
        // The things last seen: small boxes where they were (never their names, the map is for the eye).
        foreach (var (area, _) in _data.Objects)
        {
            var low = origin + PixelOf(area.Position);
            var size = new Vector2(Math.Max(2f, area.Size.X * _scale), Math.Max(2f, area.Size.Y * _scale));
            DrawRect(new Rect2(low, size), ThingInk, false, 1f);
        }
        // The Gubble, a diamond in its colour; the player, an arrow the way the body faces. Held at the picture's edge if outside it.
        var g = origin + Held(PixelOf(_companion));
        DrawColoredPolygon(new[] { g + Vector2.Up * 5.5f, g + Vector2.Right * 5.5f, g + Vector2.Down * 5.5f, g + Vector2.Left * 5.5f }, new Color(_aura, 1f).Lightened(0.1f));
        DrawPolyline(new[] { g + Vector2.Up * 5.5f, g + Vector2.Right * 5.5f, g + Vector2.Down * 5.5f, g + Vector2.Left * 5.5f, g + Vector2.Up * 5.5f }, new Color(0.04f, 0.07f, 0.07f, 0.9f), 1.3f, true);
        var p = origin + Held(PixelOf(_player));
        var forward = new Vector2(-Mathf.Sin(_yaw), -Mathf.Cos(_yaw));
        var side = new Vector2(-forward.Y, forward.X);
        var arrow = new[] { p + forward * 7f, p - forward * 4f + side * 4.5f, p - forward * 1.5f, p - forward * 4f - side * 4.5f };
        DrawColoredPolygon(arrow, PlayerInk);
        DrawPolyline(new[] { arrow[0], arrow[1], arrow[2], arrow[3], arrow[0] }, new Color(0.04f, 0.07f, 0.07f, 0.9f), 1.3f, true);
        if (!ShowsCompass) return;
        // The compass in the picture's top-right corner: a needle to the room's north and an N at its tip, as the journal's map has it.
        var north = JournalMap.NorthOnPage(_site!.NegZBearingDeg);
        var centre = new Vector2(PanelPx - 24, 28);
        DrawLine(centre - north * 8, centre + north * 8, PlayerInk, 1.4f, true);
        DrawCircle(centre + north * 8, 2f, PlayerInk);
        DrawString(GetThemeDefaultFont(), centre + north * 16 + new Vector2(-4, 5), "N", fontSize: 12, modulate: PlayerInk);
    }

    private static Vector2 Held(Vector2 pixel) => new(Mathf.Clamp(pixel.X, 3, ViewPx - 3), Mathf.Clamp(pixel.Y, 3, ViewPx - 3));
}
