using Godot;
using System;
using System.Threading.Tasks;

namespace GarageGame.Native.Look;

/// <summary>One still picture an eye took: the encoded bytes and what they are.</summary>
public sealed record EyePicture(byte[] Bytes, string MediaType, int WidthPx, int HeightPx);

/// <summary>
/// An eye in the shared world: a camera in its own SubViewport, from any pose, sharing the room's world. It draws either one
/// still picture on demand (<see cref="Capture"/>: the Gubble's eyes for the connected AI, the kernel's view.look) or, while a
/// player looks through it, every frame (<see cref="StartLive"/>: the HUD's view through the Gubble's eyes, the founder's
/// 13 October icon); otherwise it draws nothing. It knows nothing of either use.
///
/// What a camera sees is a per-viewer choice made with visibility layers (VisualInstance3D.Layers, which layers a thing is
/// on) and the camera's cull mask (which layers it draws), never by hiding nodes for everyone. Today: layer 1 is the world
/// and everything that has not chosen a layer (the room, what anyone carries, effects); layer 19 is the player's own body;
/// layer 20 is the Gubble's own body. Each body's own eye leaves out its own layer (PlayerEyeCullMask, GubbleEyeCullMask), so
/// the player's first-person view never shows the inside of its head while the Gubble still sees the player; every other
/// camera keeps Godot's default mask and draws both bodies. The layers are named in project.godot ([layer_names]).
/// Per-viewer rules (who may see a Gubble) come later: docs/ideas/GUBBLE-EYES-AND-STEALTH.md.
///
/// Headless (no renderer) nothing is drawn: <see cref="CanRender"/> is false and <see cref="Capture"/> answers null.
/// </summary>
public partial class EyeView : Node
{
    /// <summary>Layer 1: the world and everything that has not chosen a layer.</summary>
    public const uint WorldLayer = 1u << 0;
    /// <summary>Layer 19, "player_body": the player's own body (its meshes), which only the player's own eye leaves out.</summary>
    public const uint PlayerBodyLayer = 1u << 18;
    /// <summary>Layer 20, "gubble_body": the Gubble's own body (its meshes, its name tag, its pointing and status cues).</summary>
    public const uint GubbleBodyLayer = 1u << 19;
    /// <summary>All twenty render layers (Godot's default camera cull mask).</summary>
    public const uint AllLayers = (1u << 20) - 1;
    /// <summary>
    /// The Gubble's eye sees the world, the player and what the Gubble holds, but not its own body. A deliberate per-viewer
    /// choice: later rules (a Gubble seen only by its steward, Gubbles spotting Gubbles, a Gubble disguised or invisible to
    /// all but its steward) become layers and masks like this one, not nodes hidden from everyone.
    /// </summary>
    public const uint GubbleEyeCullMask = AllLayers & ~GubbleBodyLayer;
    /// <summary>The player's own eye (the first-person view, F1) sees everything but the player's own body.</summary>
    public const uint PlayerEyeCullMask = AllLayers & ~PlayerBodyLayer;

    /// <summary>
    /// An eye's turn on a body: the body's facing on the floor (forward), turned yawDeg to the right (clockwise seen from above)
    /// and tilted pitchDeg up, without turning the body. The AI's look and the player's view through the Gubble's eyes share it.
    /// </summary>
    public static Basis LookBasis(Vector3 forward, double yawDeg, double pitchDeg)
    {
        var bodyYaw = Mathf.Atan2(-forward.X, -forward.Z);
        // Positive yaw turns right, which is a negative turn about Godot's +Y.
        return new Basis(Vector3.Up, bodyYaw - Mathf.DegToRad((float)yawDeg)) * new Basis(Vector3.Right, Mathf.DegToRad((float)pitchDeg));
    }

    /// <summary>A picture is at most this many pixels on its long edge, and at least MinEdgePx on its short one.</summary>
    public const int MaxEdgePx = 512;
    public const int MinEdgePx = 16;
    /// <summary>The scene is drawn at this multiple of the picture's size and scaled down (smooth edges without any history).</summary>
    public const int Supersample = 2;
    /// <summary>JPEG qualities tried in turn until the picture fits its byte cap; then the picture halves and tries again.</summary>
    public static readonly float[] JpegQualities = { 0.8f, 0.68f, 0.55f, 0.42f, 0.3f };
    /// <summary>A picture whose frame is not drawn within this long is given up (the window may be minimized: Godot then draws nothing).</summary>
    public const double FrameTimeoutS = 2.0;
    /// <summary>Frames waited after asking the viewport to draw once, before the picture is read back.</summary>
    public const int FramesToWait = 2;

    private SubViewport? _viewport;
    private Camera3D? _camera;

    /// <summary>True while a picture is being taken; a second request then gets null rather than waiting.</summary>
    public bool Busy { get; private set; }
    /// <summary>Pictures taken (for tests and the console).</summary>
    public int Taken { get; private set; }
    /// <summary>True while a player looks through this eye: it draws every frame, and takes no still pictures.</summary>
    public bool Live { get; private set; }
    /// <summary>The eye's viewport and camera (null until first used), for the screen that shows them and for tests.</summary>
    public SubViewport? EyeViewport => _viewport;
    public Camera3D? EyeCamera => _camera;

    /// <summary>Whether this run draws anything at all: false headless (the dummy renderer), as in the test suites.</summary>
    public static bool CanRender
    {
        get
        {
            if (DisplayServer.GetName() == "headless") return false;
            var driver = RenderingServer.GetCurrentRenderingDriverName();
            return driver.Length > 0 && driver != "dummy";
        }
    }

    public override void _Ready() => ProcessMode = ProcessModeEnum.Always;

    /// <summary>
    /// Put a body's own drawn parts on a layer (the Gubble's: GubbleBodyLayer), so a camera can leave them out by its cull mask
    /// while every other camera still draws them. Only geometry under the body: lights keep their layers, and what the body
    /// carries is a room object elsewhere in the tree, so it stays on the world layer. Returns how many changed.
    /// </summary>
    public static int MarkBody(Node body, uint layer)
    {
        var marked = 0;
        foreach (var node in body.FindChildren("*", nameof(GeometryInstance3D), true, false))
            if (node is GeometryInstance3D geometry && geometry.Layers != layer)
            {
                geometry.Layers = layer;
                marked++;
            }
        return marked;
    }

    /// <summary>
    /// Draw one picture from an eye pose (camera looking along its -Z) with a vertical field of view, drawing only cullMask's
    /// layers, then encode it within maxBytes (JPEG, falling quality, then smaller). Null when this run cannot render, a picture
    /// is already being taken, no frame was drawn within FrameTimeoutS, or nothing fits maxBytes.
    /// </summary>
    public async Task<EyePicture?> Capture(Transform3D eye, float verticalFovDeg, uint cullMask, Vector2I size, float nearM, float farM, int maxBytes)
    {
        if (!CanRender || !IsInsideTree() || Busy || Live || !eye.IsFinite()) return null;
        Busy = true;
        try
        {
            var (viewport, camera) = Rig(size * Supersample);
            viewport.UseTaa = false;
            camera.Transform = eye.Orthonormalized();
            camera.Fov = Mathf.Clamp(verticalFovDeg, 1f, 179f);
            camera.Near = nearM;
            camera.Far = farM;
            camera.CullMask = cullMask;
            viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
            if (!await FramesDrawn(FramesToWait)) return null;
            var image = viewport.GetTexture()?.GetImage();
            if (image == null || image.IsEmpty()) return null;
            image.Resize(size.X, size.Y, Image.Interpolation.Lanczos);
            Taken++;
            return Encode(image, maxBytes);
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>
    /// The encoding step on its own (CPU only, so tests run it headless): fit the long edge within maxEdgePx, then JPEG at each of
    /// JpegQualities until it fits maxBytes, halving the picture while nothing fits. Null when even the smallest does not.
    /// </summary>
    public static EyePicture? Encode(Image source, int maxBytes, int maxEdgePx = MaxEdgePx)
    {
        if (source.IsEmpty() || maxBytes <= 0) return null;
        var image = (Image)source.Duplicate();
        if (image.IsCompressed() && image.Decompress() != Error.Ok) return null;
        image.Convert(Image.Format.Rgb8);
        var longEdge = Math.Max(image.GetWidth(), image.GetHeight());
        if (longEdge > maxEdgePx)
        {
            var scale = (double)maxEdgePx / longEdge;
            image.Resize(Math.Max(1, (int)Math.Round(image.GetWidth() * scale)), Math.Max(1, (int)Math.Round(image.GetHeight() * scale)), Image.Interpolation.Lanczos);
        }
        while (Math.Min(image.GetWidth(), image.GetHeight()) >= MinEdgePx)
        {
            foreach (var quality in JpegQualities)
            {
                var bytes = image.SaveJpgToBuffer(quality);
                if (bytes.Length > 0 && bytes.Length <= maxBytes) return new EyePicture(bytes, "image/jpeg", image.GetWidth(), image.GetHeight());
            }
            image.Resize(Math.Max(1, image.GetWidth() / 2), Math.Max(1, image.GetHeight() / 2), Image.Interpolation.Lanczos);
        }
        return null;
    }

    /// <summary>
    /// Start drawing every frame at this size (pixels), drawing only cullMask's layers, for a screen that shows <see cref="EyeViewport"/>'s
    /// texture. Place the eye each frame with <see cref="PlaceLive"/>; <see cref="StopLive"/> makes it idle again. The rig is made
    /// headless too (it draws nothing there), so its state can be tested.
    /// </summary>
    public void StartLive(Vector2I size, uint cullMask)
    {
        if (!IsInsideTree() || Busy) return;
        var (viewport, camera) = Rig(LiveSize(size));
        // Drawn every frame, the view can use the window's temporal anti-aliasing and 3D scaling, as the game's own view does.
        var window = GetViewport();
        viewport.UseTaa = window.UseTaa;
        viewport.Scaling3DMode = window.Scaling3DMode;
        viewport.Scaling3DScale = window.Scaling3DScale;
        camera.CullMask = cullMask;
        viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        Live = true;
    }

    /// <summary>The live eye's pose this frame (a camera looking along its -Z), its vertical field of view and its clip planes.</summary>
    public void PlaceLive(Transform3D eye, float verticalFovDeg, float nearM, float farM)
    {
        if (!Live || _camera == null || !IsInstanceValid(_camera) || !eye.IsFinite()) return;
        _camera.Transform = eye.Orthonormalized();
        _camera.Fov = Mathf.Clamp(verticalFovDeg, 1f, 179f);
        _camera.Near = nearM;
        _camera.Far = farM;
    }

    /// <summary>Follow the screen's size while live (a resized window).</summary>
    public void ResizeLive(Vector2I size)
    {
        if (Live && _viewport != null && IsInstanceValid(_viewport) && _viewport.Size != LiveSize(size)) _viewport.Size = LiveSize(size);
    }

    /// <summary>Stop drawing: the eye is idle again until the next picture or view.</summary>
    public void StopLive()
    {
        Live = false;
        if (_viewport != null && IsInstanceValid(_viewport)) _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
    }

    private static Vector2I LiveSize(Vector2I size) => new(Math.Clamp(size.X, MinEdgePx, 8192), Math.Clamp(size.Y, MinEdgePx, 8192));

    /// <summary>The viewport and its camera, made once and kept: idle until asked for a picture (UpdateMode.Once) or a live view.</summary>
    private (SubViewport Viewport, Camera3D Camera) Rig(Vector2I size)
    {
        if (_viewport == null || !IsInstanceValid(_viewport) || _camera == null || !IsInstanceValid(_camera))
        {
            // The window's 3D quality, so the picture looks like the game; no temporal anti-aliasing (one frame has no history).
            var window = GetViewport();
            // No world of its own: the viewport draws its parent's World3D, the room the player is in.
            _viewport = new SubViewport
            {
                Name = "EyeViewport", RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
                RenderTargetClearMode = SubViewport.ClearMode.Always, TransparentBg = false, HandleInputLocally = false,
                Msaa3D = window.Msaa3D, ScreenSpaceAA = window.ScreenSpaceAA, UseTaa = false, UseDebanding = window.UseDebanding,
                PositionalShadowAtlasSize = window.PositionalShadowAtlasSize, AudioListenerEnable3D = false,
            };
            for (var quadrant = 0; quadrant < 4; quadrant++)
                _viewport.SetPositionalShadowAtlasQuadrantSubdiv(quadrant, window.GetPositionalShadowAtlasQuadrantSubdiv(quadrant));
            AddChild(_viewport);
            // Placed directly each time it draws, so never interpolated between physics ticks.
            _camera = new Camera3D { Name = "EyeCamera", Current = true, PhysicsInterpolationMode = PhysicsInterpolationModeEnum.Off };
            _viewport.AddChild(_camera);
        }
        if (_viewport.Size != size) _viewport.Size = size;
        return (_viewport, _camera);
    }

    /// <summary>Wait for the renderer to finish `count` frames, or give up after FrameTimeoutS (false).</summary>
    private async Task<bool> FramesDrawn(int count)
    {
        var deadline = Time.GetTicksMsec() + (ulong)(FrameTimeoutS * 1000);
        for (var frame = 0; frame < count; frame++)
        {
            var drawn = new TaskCompletionSource<bool>();
            var onDrawn = Callable.From(() => drawn.TrySetResult(true));
            RenderingServer.Singleton.Connect(RenderingServer.SignalName.FramePostDraw, onDrawn, (uint)ConnectFlags.OneShot);
            var left = Math.Max(0.05, (deadline - Math.Min(deadline, Time.GetTicksMsec())) / 1000.0);
            var timer = GetTree().CreateTimer(left, processAlways: true, processInPhysics: false, ignoreTimeScale: true);
            timer.Timeout += () => drawn.TrySetResult(false);
            var ok = await drawn.Task;
            if (RenderingServer.Singleton.IsConnected(RenderingServer.SignalName.FramePostDraw, onDrawn))
                RenderingServer.Singleton.Disconnect(RenderingServer.SignalName.FramePostDraw, onDrawn);
            if (!ok || !IsInsideTree()) return false;
        }
        return true;
    }
}
