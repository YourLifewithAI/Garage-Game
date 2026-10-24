using Godot;
using System;

namespace GarageGame.Native.Look;

/// <summary>
/// The preset's post.grain and post.vignette, and the focus pass, as a Forward+ compositor effect, so every camera in
/// the world gets them, including offscreen review targets. It runs after transparent geometry, on the linear colour
/// buffer before tone mapping (and so before depth of field blurs it). The grain is fixed in screen space, like the
/// tooth of paper, rather than animated film noise, so anti-aliasing keeps it instead of averaging it away. Without a GPU
/// rendering device (headless tests, Compatibility) it never runs.
///
/// The focus pass is colour contrast as a way to focus (the founder, 7 October): it reads the depth buffer, works out
/// how far each pixel is outside the depth-of-field band the look gave the camera (SetFocus), and there lifts or drains
/// colour and lifts highlights, so the figures in focus carry the colour and the blurred distance melts into calm,
/// creamy discs.
///
/// Lifetime (review M8). RenderingDevice.FreeRid may only be called on the render thread. The callback holds C# wrappers of
/// the viewport's render buffers; left to the garbage collector, a wrapper that outlives its viewport is the last reference
/// to the buffers, so they (and the textures they own) were destroyed on the finalizer thread: "free_rid can only be called
/// from the render thread" for every texture, then an access violation at a room reload or at exit (reproduced with
/// tools/look/capture-look.ps1 -Probe free-viewport against the commit before the fix). The callback now drops its
/// reference itself, on the render thread, and nothing is freed from a destructor. Release, called from the main thread by
/// the look director when it leaves the tree, hands the shader's free to the render thread too, and an effect that is
/// simply dropped leaves its two GPU objects to the rendering device, which frees them at shutdown.
/// </summary>
public partial class LookPostEffect : CompositorEffect
{
    /// <summary>The compute shader. Its Params block must match PushConstantNames, which the look test checks.</summary>
    public const string ComputeSource = @"
#version 450
#extension GL_EXT_samplerless_texture_functions : require
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;
layout(rgba16f, set = 0, binding = 0) uniform image2D color_image;
layout(set = 0, binding = 1) uniform texture2D depth_texture;
layout(push_constant, std430) uniform Params {
	vec2 size;
	float grain;
	float vignette;
	float seed;
	float vignette_start;
	float vignette_end;
	float grain_fine;
	float grain_soft;
	float grain_soft_px;
	float near_distance;
	float near_transition;
	float far_distance;
	float far_transition;
	float depth_a;
	float depth_b;
	float focus_saturation;
	float defocus_saturation;
	float defocus_darken;
	float bokeh_threshold;
	float bokeh_gain;
	float focus_active;
	float pad0;
	float pad1;
} params;

float hash12(vec2 p) {
	vec3 p3 = fract(vec3(p.xyx) * 0.1031);
	p3 += dot(p3, p3.yzx + 33.33);
	return fract((p3.x + p3.y) * p3.z);
}

void main() {
	ivec2 pixel = ivec2(gl_GlobalInvocationID.xy);
	if (pixel.x >= int(params.size.x) || pixel.y >= int(params.size.y)) return;
	vec4 color = imageLoad(color_image, pixel);
	if (params.focus_active > 0.5) {
		// Distance from the camera along its view axis: the depth buffer holds clip depth, and the camera's projection
		// maps view distance d to depth_a + depth_b / d (depth 0, the sky, is infinitely far).
		float raw = texelFetch(depth_texture, pixel, 0).r;
		float distance = raw <= 1e-7 ? 1e6 : params.depth_b / (raw + params.depth_a);
		float defocus = 0.0;
		if (params.far_distance > 0.0) defocus = max(defocus, clamp((distance - params.far_distance) / params.far_transition, 0.0, 1.0));
		if (params.near_distance > 0.0) defocus = max(defocus, clamp((params.near_distance - distance) / params.near_transition, 0.0, 1.0));
		float luma = dot(color.rgb, vec3(0.2126, 0.7152, 0.0722));
		color.rgb = max(mix(vec3(luma), color.rgb, mix(params.focus_saturation, params.defocus_saturation, defocus)), vec3(0.0));
		float highlight = smoothstep(params.bokeh_threshold, params.bokeh_threshold * 2.0 + 0.5, luma);
		color.rgb *= (1.0 + params.bokeh_gain * highlight * defocus) * (1.0 - params.defocus_darken * defocus);
	}
	vec2 uv = (vec2(pixel) + 0.5) / params.size;
	vec2 centred = (uv - 0.5) * vec2(params.size.x / params.size.y, 1.0);
	float edge = smoothstep(params.vignette_start, params.vignette_end, length(centred));
	float vignette = 1.0 - params.vignette * edge;
	// Two scales of grain: a fine tooth and a softer mottle, multiplicative so shadows stay clean.
	float fine = hash12(vec2(pixel) + params.seed) - 0.5;
	float soft = hash12(floor(vec2(pixel) / max(params.grain_soft_px, 1.0)) + params.seed * 1.7) - 0.5;
	float grain = 1.0 + params.grain * (fine * params.grain_fine + soft * params.grain_soft);
	color.rgb *= vignette * grain;
	imageStore(color_image, pixel, color);
}
";

    /// <summary>The push-constant block in the order the compute shader declares it: twenty-four floats, 96 bytes (std430).</summary>
    public static readonly string[] PushConstantNames =
    {
        "size.x", "size.y", "grain", "vignette", "seed", "vignette_start", "vignette_end", "grain_fine", "grain_soft", "grain_soft_px",
        "near_distance", "near_transition", "far_distance", "far_transition", "depth_a", "depth_b",
        "focus_saturation", "defocus_saturation", "defocus_darken", "bokeh_threshold", "bokeh_gain", "focus_active", "pad0", "pad1",
    };

    /// <summary>The grain pattern's fixed offset: the same tooth every frame, so anti-aliasing keeps it.</summary>
    public const float Seed = 17.0f;

    public float Grain { get; set; }
    public float Vignette { get; set; }
    /// <summary>Vignette range, grain mix and the focus pass's strengths (x_look_post).</summary>
    public PostTuning Tuning { get; set; } = LookTuning.Default.Post;
    /// <summary>Whether the shader compiled and the effect has run at least once (for reports and tests).</summary>
    public bool Ran { get; private set; }
    public string Error { get; private set; } = "";
    /// <summary>The depth-of-field band the focus pass works from, as the look last gave it to a camera; null leaves the focus pass off.</summary>
    public DepthOfField? Focus { get; private set; }

    private RenderingDevice? _device;
    private Rid _shader;
    private Rid _pipeline;
    private Projection _projection = Projection.Identity;
    private bool _haveProjection;
    // Written by the main thread (SetFocus) and read by the render thread: one reference each, replaced whole.
    private DepthOfField? _focus;

    public LookPostEffect()
    {
        EffectCallbackType = EffectCallbackTypeEnum.PostTransparent;
    }

    /// <summary>Tell the effect the camera's projection without a render (tests and previews); a real render callback sets it from the camera itself.</summary>
    public void UseProjection(Projection projection)
    {
        _projection = projection;
        _haveProjection = true;
    }

    /// <summary>The band the camera now in use is blurred outside of; the focus pass draws colour in and out of the same band.</summary>
    public void SetFocus(DepthOfField? band)
    {
        _focus = band;
        Focus = band;
    }

    private bool EnsurePipeline()
    {
        if (_pipeline.IsValid) return true;
        if (Error.Length > 0) return false;
        _device = RenderingServer.GetRenderingDevice();
        if (_device == null) return false;
        var source = new RDShaderSource { Language = RenderingDevice.ShaderLanguage.Glsl, SourceCompute = ComputeSource };
        var spirv = _device.ShaderCompileSpirVFromSource(source);
        if (!string.IsNullOrEmpty(spirv.CompileErrorCompute))
        {
            Error = spirv.CompileErrorCompute;
            GD.PushError("LOOK: post effect shader failed to compile: " + Error);
            return false;
        }
        _shader = _device.ShaderCreateFromSpirV(spirv);
        if (!_shader.IsValid) { Error = "shader could not be created"; return false; }
        _pipeline = _device.ComputePipelineCreate(_shader);
        return _pipeline.IsValid;
    }

    public override void _RenderCallback(int effectCallbackType, RenderData renderData)
    {
        if (effectCallbackType != (int)EffectCallbackTypeEnum.PostTransparent || (Grain <= 0f && Vignette <= 0f && !FocusPassWanted)) return;
        if (!EnsurePipeline() || _device == null) return;
        var sceneBuffers = renderData.GetRenderSceneBuffers();
        try
        {
            if (sceneBuffers is not RenderSceneBuffersRD buffers) return;
            var size = buffers.GetInternalSize();
            if (size.X == 0 || size.Y == 0) return;
            if (renderData.GetRenderSceneData() is { } sceneData)
            {
                _projection = sceneData.GetCamProjection();
                _haveProjection = true;
            }
            var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(PushConstants(size).AsSpan()).ToArray();
            var groupsX = (uint)((size.X - 1) / 8 + 1);
            var groupsY = (uint)((size.Y - 1) / 8 + 1);
            for (uint view = 0; view < buffers.GetViewCount(); view++)
            {
                var colour = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 0 };
                colour.AddId(buffers.GetColorLayer(view));
                var depth = new RDUniform { UniformType = RenderingDevice.UniformType.Texture, Binding = 1 };
                depth.AddId(buffers.GetDepthLayer(view));
                var set = UniformSetCacheRD.GetCache(_shader, 0, new Godot.Collections.Array<RDUniform> { colour, depth });
                var list = _device.ComputeListBegin();
                _device.ComputeListBindComputePipeline(list, _pipeline);
                _device.ComputeListBindUniformSet(list, set, 0);
                _device.ComputeListSetPushConstant(list, bytes, (uint)bytes.Length);
                _device.ComputeListDispatch(list, groupsX, groupsY, 1);
                _device.ComputeListEnd();
            }
            Ran = true;
        }
        finally
        {
            // The render buffers belong to the viewport, but this wrapper holds a reference to them. Left to the garbage
            // collector, a wrapper that outlives its viewport is the last reference, and the render buffers (and the textures
            // they own) are then destroyed on the finalizer thread, where freeing GPU objects is not allowed (review M8).
            // Dropping the reference here, on the render thread, leaves the viewport to free them where it should.
            sceneBuffers?.Dispose();
        }
    }

    /// <summary>Whether the focus pass changes anything at all under this tuning (it is off at its neutral values).</summary>
    private bool FocusPassWanted => _focus != null && FocusPassChanges(Tuning);

    /// <summary>True when the tuning's focus pass would change a pixel: any saturation other than 1, any darkening, or any highlight gain.</summary>
    public static bool FocusPassChanges(PostTuning tuning) =>
        !Mathf.IsEqualApprox(tuning.FocusSaturation, 1f) || !Mathf.IsEqualApprox(tuning.DefocusSaturation, 1f) || tuning.DefocusDarken > 0f || tuning.BokehGain > 0f;

    /// <summary>The values the effect pushes to the shader for a target of this size, in PushConstantNames order.</summary>
    public float[] PushConstants(Vector2I size)
    {
        var band = _focus;
        var active = band != null && _haveProjection && FocusPassChanges(Tuning);
        return new[]
        {
            size.X, size.Y, Grain, Vignette, Seed, Tuning.VignetteStart, Tuning.VignetteEnd,
            Tuning.GrainFine, Tuning.GrainSoft, Tuning.GrainSoftPx,
            band is { NearEnabled: true } n ? n.NearDistance : -1f, band is { NearEnabled: true } nt ? nt.NearTransition : 1f,
            band is { FarEnabled: true } f ? f.FarDistance : -1f, band is { FarEnabled: true } ft ? ft.FarTransition : 1f,
            _projection.Z.Z, _projection.W.Z,
            Tuning.FocusSaturation, Tuning.DefocusSaturation, Tuning.DefocusDarken, Tuning.BokehThreshold, Tuning.BokehGain,
            active ? 1f : 0f, 0f, 0f,
        };
    }

    /// <summary>
    /// What the shader multiplies one pixel's colour by for the vignette and the grain, computed on the CPU from the same
    /// push constants with the same arithmetic (a mirror of main() in ComputeSource, for tests and reports).
    /// </summary>
    public static float Factor(float[] constants, Vector2I pixel)
    {
        float P(string name) => constants[Array.IndexOf(PushConstantNames, name)];
        var size = new Vector2(P("size.x"), P("size.y"));
        var uv = (new Vector2(pixel.X, pixel.Y) + new Vector2(0.5f, 0.5f)) / size;
        var centred = (uv - new Vector2(0.5f, 0.5f)) * new Vector2(size.X / size.Y, 1f);
        var edge = SmoothStep(P("vignette_start"), P("vignette_end"), centred.Length());
        var vignette = 1f - P("vignette") * edge;
        var fine = Hash12(new Vector2(pixel.X, pixel.Y) + new Vector2(P("seed"), P("seed"))) - 0.5f;
        var softPx = Mathf.Max(P("grain_soft_px"), 1f);
        var soft = Hash12(new Vector2(Mathf.Floor(pixel.X / softPx), Mathf.Floor(pixel.Y / softPx)) + new Vector2(P("seed") * 1.7f, P("seed") * 1.7f)) - 0.5f;
        var grain = 1f + P("grain") * (fine * P("grain_fine") + soft * P("grain_soft"));
        return vignette * grain;
    }

    /// <summary>
    /// How far a pixel at a view distance is outside the depth-of-field band, 0 (in focus) to 1 (fully blurred): a mirror of
    /// the shader's defocus, from the same push constants.
    /// </summary>
    public static float Defocus(float[] constants, float distance)
    {
        float P(string name) => constants[Array.IndexOf(PushConstantNames, name)];
        var defocus = 0f;
        if (P("far_distance") > 0f) defocus = Mathf.Max(defocus, Mathf.Clamp((distance - P("far_distance")) / P("far_transition"), 0f, 1f));
        if (P("near_distance") > 0f) defocus = Mathf.Max(defocus, Mathf.Clamp((P("near_distance") - distance) / P("near_transition"), 0f, 1f));
        return defocus;
    }

    /// <summary>The focus pass on one linear colour, for a given defocus (0 to 1): a CPU mirror of the shader, for tests.</summary>
    public static Color FocusColor(float[] constants, Color color, float defocus)
    {
        float P(string name) => constants[Array.IndexOf(PushConstantNames, name)];
        var luma = 0.2126f * color.R + 0.7152f * color.G + 0.0722f * color.B;
        var saturation = Mathf.Lerp(P("focus_saturation"), P("defocus_saturation"), defocus);
        var c = new Vector3(luma, luma, luma).Lerp(new Vector3(color.R, color.G, color.B), saturation);
        c = new Vector3(Mathf.Max(c.X, 0f), Mathf.Max(c.Y, 0f), Mathf.Max(c.Z, 0f));
        var threshold = P("bokeh_threshold");
        var highlight = SmoothStep(threshold, threshold * 2f + 0.5f, luma);
        c *= (1f + P("bokeh_gain") * highlight * defocus) * (1f - P("defocus_darken") * defocus);
        return new Color(c.X, c.Y, c.Z, color.A);
    }

    private static float Fract(float x) => x - Mathf.Floor(x);

    private static float SmoothStep(float from, float to, float x)
    {
        var t = Mathf.Clamp((x - from) / (to - from), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static float Hash12(Vector2 p)
    {
        var p3 = new Vector3(Fract(p.X * 0.1031f), Fract(p.Y * 0.1031f), Fract(p.X * 0.1031f));
        var d = p3.Dot(new Vector3(p3.Y, p3.Z, p3.X) + new Vector3(33.33f, 33.33f, 33.33f));
        p3 += new Vector3(d, d, d);
        return Fract((p3.X + p3.Y) * p3.Z);
    }

    /// <summary>
    /// Free the GPU objects on the render thread. The look director calls this from the main thread when it leaves the tree;
    /// safe to call twice, and from any thread but the finalizer's. A render callback after this builds the pipeline again,
    /// so releasing never leaves the effect unusable.
    /// </summary>
    public void Release()
    {
        if (_device == null || !_shader.IsValid) return;
        RenderingServer.CallOnRenderThread(Callable.From(ReleaseOnRenderThread));
    }

    private void ReleaseOnRenderThread()
    {
        // Freeing the shader also frees the pipeline made from it and the cached uniform sets that use it.
        if (_device != null && _shader.IsValid) _device.FreeRid(_shader);
        _shader = default;
        _pipeline = default;
    }
}
