"""Pose backends. Today: MapAnything (Meta, 2025/2026) with the Apache-2.0 weights.

The backend turns a list of photos into, per photo, a camera-to-world pose, intrinsics and a
metric point map in the camera frame. Everything heavy (torch, the model) is imported lazily so
C1 and the unit tests never need the GPU stack.
"""

from __future__ import annotations

import contextlib
import math
import shutil
import subprocess
import sys
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

import numpy as np
from PIL import Image

from ..exif import digitally_zoomed

# Every model the pipeline downloads, with its licence. Checked 6 October 2026 on Hugging Face.
MODEL_REGISTRY = {
    "facebook/map-anything-apache": {
        "role": "camera poses, intrinsics and metric depth (C2)",
        "licence": "Apache-2.0 (weights); code Apache-2.0",
        "gated": False,
        "revision": "00f9c245bbcb60522d1ed7f9e9d88462c6e3f38a",
        "download_bytes": 4914062480,
    },
    "google/owlv2-base-patch16-ensemble": {
        "role": "open-vocabulary object detection for per-object view counts (C2) and the inventory (C4)",
        "licence": "Apache-2.0",
        "gated": False,
        "revision": "cfd3195ba4ea9592eec887ded089f4c08eff231d",
        "download_bytes": 619918824,
    },
    "facebook/sam2.1-hiera-small": {
        "role": "object masks from detector boxes, for the inventory's boxes and colours (C4)",
        "licence": "Apache-2.0",
        "gated": False,
        "revision": "ee5bba1d82bb8749febdf90f45e84b687142ba03",
        "download_bytes": 184305280,  # model.safetensors; the repository's .pt copy is not fetched
    },
}
# Code (not weights) fetched by torch.hub when MapAnything builds its DINOv2 encoder. Uniception
# asks for the repository's moving main branch; ``pinned_torch_hub`` pins it to this commit.
# The weights come from the MapAnything checkpoint above, so no DINOv2 weights are downloaded.
TORCH_HUB_PINS = {
    "facebookresearch/dinov2": {
        "ref": "7764ea0f912e53c92e82eb78a2a1631e92725fc8",
        "licence": "Apache-2.0 (code)",
        "role": "DINOv2 encoder definition used by MapAnything",
        # Checked in the downloaded checkout, 6 October 2026: the repository is Apache-2.0 as a
        # whole, but this commit's hubconf.py imports dinov2.hub.cell_dino and dinov2.hub.xray_dino
        # (LICENSE_CELL_DINO_CODE: CC BY 4.0 code; LICENSE_CELL_DINO_MODELS and LICENSE_XRAY_DINO_MODEL:
        # non-commercial research licences for their weights). Importing hubconf.py runs that code.
        "note": ("this commit's hubconf.py also imports the Cell-DINO (CC BY 4.0 code) and X-Ray-DINO modules, "
                 "whose weights are non-commercial research only; no weights are fetched and none are used"),
    },
}
# Considered and not used, recorded so the founder can decide later:
REJECTED_MODELS = {
    "facebook/VGGT-1B": "CC BY-NC 4.0 (non-commercial only)",
    "facebook/VGGT-1B-Commercial": "commercial licence, gated behind an application form on Hugging Face",
    "facebook/map-anything": "CC BY-NC 4.0 (non-commercial only); the -apache variant is used instead",
    "naver/MASt3R": "CC BY-NC-SA 4.0 (non-commercial only)",
}

POSE_MODEL = "facebook/map-anything-apache"
TARGET_SIZE = (392, 518)  # (width, height): MapAnything's 3:4 portrait size at the 518 setting
STORE_STRIDE = 2  # keep every 2nd pixel of the point maps on disk
HALF_MODULES = ("encoder", "info_sharing")
# fp32 but without cuDNN: its fp32 conv workspace reserves ~3 GB at 24 views for no benefit here.
NO_CUDNN_MODULES = ("ray_dirs_encoder", "depth_encoder")


@dataclass
class GpuStats:
    seconds: float = 0.0
    prepare_seconds: float = 0.0  # decoding and resizing photos for the model (CPU)
    peak_allocated_mib: float = 0.0
    peak_reserved_mib: float = 0.0
    batches: list[dict[str, Any]] = field(default_factory=list)


def nvidia_smi_memory() -> tuple[int, int] | None:
    """(used MiB, total MiB) of GPU 0 from nvidia-smi, or None if unavailable."""
    exe = shutil.which("nvidia-smi")
    if not exe:
        return None
    try:
        out = subprocess.run(
            [exe, "--query-gpu=memory.used,memory.total", "--format=csv,noheader,nounits"],
            capture_output=True, text=True, timeout=20, check=True,
        ).stdout.strip().splitlines()[0]
        used, total = (int(x.strip()) for x in out.split(","))
        return used, total
    except (subprocess.SubprocessError, ValueError, IndexError, OSError):
        return None


GAME_WINDOW_PREFIXES = ("Garage Game", "EnFractal")  # the game, and a build from before the rename
GAME_PROGRAMS = ("godot", "garagegame", "enfractal")  # the editor/runner, or an exported build


def visible_windows() -> list[tuple[str, str]]:
    """(title, program file name) of every visible top-level window. Windows only, in milliseconds.

    ``tasklist /v`` gives the same answer but takes about 14 s per call on the founder's machine.
    """
    import ctypes
    from ctypes import wintypes

    user32 = ctypes.WinDLL("user32", use_last_error=True)
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.OpenProcess.restype = wintypes.HANDLE
    out: list[tuple[str, str]] = []

    def program(hwnd) -> str:
        pid = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        handle = kernel32.OpenProcess(0x1000, False, pid.value)  # PROCESS_QUERY_LIMITED_INFORMATION
        if not handle:
            return ""
        try:
            buf = ctypes.create_unicode_buffer(1024)
            size = wintypes.DWORD(len(buf))
            ok = kernel32.QueryFullProcessImageNameW(handle, 0, buf, ctypes.byref(size))
            return buf.value.replace("\\", "/").rsplit("/", 1)[-1] if ok else ""
        finally:
            kernel32.CloseHandle(handle)

    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def each(hwnd, _):
        if user32.IsWindowVisible(hwnd):
            n = user32.GetWindowTextLengthW(hwnd)
            if n:
                buf = ctypes.create_unicode_buffer(n + 1)
                user32.GetWindowTextW(hwnd, buf, n + 1)
                out.append((buf.value, program(hwnd)))
        return True

    user32.EnumWindows(each, 0)
    return out


def game_window_open(windows: list[tuple[str, str]] | None = None) -> bool:
    """Whether the game is open (the founder may be playing on this GPU).

    A window titled Garage Game... (or EnFractal... from an older build) owned by Godot or an EnFractal build counts; a folder or an editor
    tab named EnFractal does not. Windows only; elsewhere, or if the check fails, False.
    """
    if windows is None:
        if not sys.platform.startswith("win"):
            return False
        try:
            windows = visible_windows()
        except (OSError, AttributeError, ValueError):
            return False
    return any(title.startswith(GAME_WINDOW_PREFIXES) and any(p in exe.lower() for p in GAME_PROGRAMS)
               for title, exe in windows)


def wait_while_game_runs(*, retries: int = 240, delay_s: float = 30.0, log=print) -> float:
    """Pause GPU work while the game is open; returns the seconds waited."""
    waited = 0.0
    for attempt in range(retries):
        if not game_window_open():
            return waited
        if attempt == 0:
            log("The game is open on this GPU; pausing GPU work until it closes...")
        time.sleep(delay_s)
        waited += delay_s
    raise RuntimeError("The game stayed open; GPU work was not started. Run the coverage step again later.")


def wait_for_vram(need_mib: int, *, retries: int = 20, delay_s: float = 30.0, log=print) -> int:
    """Wait (politely) until ``need_mib`` is free; other lanes may be capturing on this GPU."""
    wait_while_game_runs(log=log)
    for attempt in range(retries + 1):
        mem = nvidia_smi_memory()
        if mem is None:
            return 0
        used, total = mem
        free = total - used
        if free >= need_mib:
            return free
        log(f"GPU busy: {free} MiB free, need {need_mib} MiB; waiting {delay_s:.0f}s ({attempt + 1}/{retries})")
        time.sleep(delay_s)
    raise RuntimeError(f"GPU never had {need_mib} MiB free after {retries} retries; try again later.")


MAX_CROP_FRACTION = 0.08  # a photo may lose this much of its width or height to fit the model's frame
PAD_COLOUR = (124, 116, 104)  # close to the image-normalisation mean, so padding reads as "nothing"


@dataclass(frozen=True)
class InputLayout:
    """Where a photo's pixels land in the model's fixed-size input."""

    scale: float  # original pixels to model pixels
    left: int  # columns cut from the left of the scaled photo (a near-3:4 photo is cropped)
    top: int
    pad_x: int  # columns of padding left of the scaled photo (any other shape is padded, never cropped)
    pad_y: int
    content_w: int  # size of the photo's own pixels inside the input
    content_h: int


def input_layout(width: int, height: int, target: tuple[int, int] = TARGET_SIZE) -> InputLayout:
    """How a photo is fitted to the model input.

    MapAnything wants every photo in a batch at one size, here the 3:4 portrait ``target``. A photo
    close to that shape (an iPhone's 3:4 portrait, off by about 1%) is scaled to cover the frame
    and centre-cropped, as before. A photo of any other shape is scaled to fit inside the frame and
    padded: a landscape photo cropped to portrait would lose 43% of its width, a 9:16 one 26% of its
    height, and the walls beside the lost strip would go uncovered in the report.
    """
    tw, th = target
    cover = max(tw / width, th / height)
    rw, rh = math.ceil(width * cover), math.ceil(height * cover)
    if max(1 - tw / rw, 1 - th / rh) <= MAX_CROP_FRACTION:
        return InputLayout(cover, (rw - tw) // 2, (rh - th) // 2, 0, 0, tw, th)
    fit = min(tw / width, th / height)
    cw, ch = min(tw, round(width * fit)), min(th, round(height * fit))
    return InputLayout(fit, 0, 0, (tw - cw) // 2, (th - ch) // 2, cw, ch)


def open_reduced(path: Path, min_size: tuple[int, int]) -> Image.Image:
    """Open an image, letting the JPEG decoder skip detail beyond twice ``min_size``.

    Draft mode decodes a 24 MP photo at 1/2, 1/4 or 1/8 scale directly from the DCT, several
    times faster than decoding it whole and resizing. The aspect ratio is kept.
    """
    im = Image.open(path)
    if im.format == "JPEG":
        im.draft("RGB", (2 * min_size[0], 2 * min_size[1]))
    return im


def fit_to_canvas(im: Image.Image, target: tuple[int, int] = TARGET_SIZE) -> tuple[Image.Image, InputLayout]:
    """An RGB image fitted to a ``target``-sized canvas as ``input_layout`` says."""
    tw, th = target
    layout = input_layout(im.width, im.height, target)
    if (layout.content_w, layout.content_h) != (tw, th):
        canvas = Image.new("RGB", target, PAD_COLOUR)
        canvas.paste(im.resize((layout.content_w, layout.content_h), Image.Resampling.LANCZOS),
                     (layout.pad_x, layout.pad_y))
        return canvas, layout
    rw, rh = math.ceil(im.width * layout.scale), math.ceil(im.height * layout.scale)
    im = im.resize((rw, rh), Image.Resampling.LANCZOS)
    return im.crop((layout.left, layout.top, layout.left + tw, layout.top + th)), layout


def load_model_view(path: Path, target: tuple[int, int] = TARGET_SIZE) -> tuple[Image.Image, InputLayout]:
    """A photo as the model sees it, and where its pixels are in that picture."""
    with open_reduced(path, target) as im:
        return fit_to_canvas(im.convert("RGB"), target)


def content_mask(layout: InputLayout, target: tuple[int, int] = TARGET_SIZE, stride: int = STORE_STRIDE) -> np.ndarray:
    """True where a stored point-map pixel comes from the photo itself and not from padding."""
    tw, th = target
    mask = np.zeros((th, tw), bool)
    mask[layout.pad_y:layout.pad_y + layout.content_h, layout.pad_x:layout.pad_x + layout.content_w] = True
    return mask[::stride, ::stride]


def _cast(obj: Any, dtype) -> Any:
    """Cast floating tensors inside tensors, dataclasses, tuples, lists and dicts."""
    import dataclasses

    import torch

    if torch.is_tensor(obj):
        return obj.to(dtype) if obj.is_floating_point() else obj
    if dataclasses.is_dataclass(obj) and not isinstance(obj, type):
        changes = {f.name: _cast(getattr(obj, f.name), dtype) for f in dataclasses.fields(obj) if f.init}
        return dataclasses.replace(obj, **changes)
    if isinstance(obj, tuple) and hasattr(obj, "_fields"):
        return type(obj)(*(_cast(x, dtype) for x in obj))
    if isinstance(obj, (tuple, list)):
        return type(obj)(_cast(x, dtype) for x in obj)
    if isinstance(obj, dict):
        return {k: _cast(v, dtype) for k, v in obj.items()}
    return obj


def _run_in_half(module) -> None:
    """Store a submodule's weights in fp16 and cast at its boundary (fp16 in, fp32 out)."""
    import torch

    module.half()
    module.register_forward_pre_hook(lambda m, args, kwargs: (_cast(args, torch.float16), _cast(kwargs, torch.float16)),
                                     with_kwargs=True)
    module.register_forward_hook(lambda m, args, output: _cast(output, torch.float32))


def _without_cudnn(module) -> None:
    import torch

    def off(m, args):
        m._cudnn_was = torch.backends.cudnn.enabled
        torch.backends.cudnn.enabled = False

    def on(m, args, output):
        torch.backends.cudnn.enabled = getattr(m, "_cudnn_was", True)

    module.register_forward_pre_hook(off)
    module.register_forward_hook(on)


@contextlib.contextmanager
def pinned_torch_hub():
    """Route torch.hub loads of known repositories to a pinned commit of the official repository.

    The default would download whatever is on ``main`` today and run its ``hubconf.py``.
    ``skip_validation`` only skips torch's check that the ref is a branch or tag head; the
    download still comes from the official repository named in ``TORCH_HUB_PINS``.
    """
    import torch

    real = torch.hub.load

    def load(repo_or_dir, model, *args, **kwargs):
        pin = TORCH_HUB_PINS.get(repo_or_dir) if isinstance(repo_or_dir, str) else None
        if pin is not None:
            repo_or_dir = f"{repo_or_dir}:{pin['ref']}"
            kwargs["skip_validation"] = True
            kwargs["trust_repo"] = True
            kwargs["force_reload"] = False
        return real(repo_or_dir, model, *args, **kwargs)

    torch.hub.load = load
    try:
        yield
    finally:
        torch.hub.load = real


FULL_FRAME_DIAGONAL_MM = 43.2666


def exif_intrinsics(photo: dict[str, Any], target: tuple[int, int] = TARGET_SIZE) -> np.ndarray | None:
    """Pinhole intrinsics of the model input from the 35 mm-equivalent focal length in EXIF.

    Phone photos are distortion-corrected and the principal point is close to the centre, so the
    35 mm equivalent (defined on the frame diagonal) gives the focal length in pixels.

    None for a digitally zoomed photo, which is then left to the model's own estimate. The garage
    set shows why: iPhone 17 photos with DigitalZoomRatio 1.42 (eight of them, pixel size
    unchanged) keep a 26 mm equivalent, while the one at 1.71 records 44 mm, which is 26 mm times
    the zoom. EXIF does not say consistently whether the equivalent includes the zoom, and a wrong
    focal length bends the whole batch it is in. See ``exif.digitally_zoomed``.
    """
    exif = photo.get("exif") or {}
    f35 = exif.get("focal_length_35mm")
    size = photo.get("image") or {}
    W, H = size.get("width"), size.get("height")
    if not f35 or not W or not H or digitally_zoomed(exif):
        return None
    f_px = f35 / FULL_FRAME_DIAGONAL_MM * math.hypot(W, H)
    lay = input_layout(W, H, target)
    return np.array([[f_px * lay.scale, 0.0, W * lay.scale / 2 - lay.left + lay.pad_x],
                     [0.0, f_px * lay.scale, H * lay.scale / 2 - lay.top + lay.pad_y],
                     [0.0, 0.0, 1.0]])


def batch_size_for(free_mib: int, *, headroom_mib: int = 1000, fixed_mib: int = 3800, per_view_mib: int = 85,
                   lo: int = 8, hi: int = 32) -> int:
    """Views per batch that fit in free VRAM (fp16 trunk).

    Measured on the RTX 2070 SUPER (8 GB), 6 October 2026: peak reserved 4472 MiB at 8 views,
    5826 at 24 and 6462 at 32 (about 3.8 GB fixed plus 83 MiB per view). 32 is the largest
    batch measured, so it is the ceiling.
    """
    n = (free_mib - headroom_mib - fixed_mib) // per_view_mib
    return int(max(lo, min(hi, n)))


class MapAnythingBackend:
    """MapAnything in fp16 weights with memory-efficient inference, sized for an 8 GB card."""

    name = "mapanything"

    def __init__(self, model_id: str = POSE_MODEL, *, half: bool = True, head_minibatch: int = 2, log=print):
        import torch

        self.torch = torch
        self.head_minibatch = head_minibatch
        self.model_id = model_id
        self.half = half
        self.log = log
        self.model = None
        self.stats = GpuStats()
        self.weights_mib = 0.0

    def load(self) -> None:
        from mapanything.models import MapAnything

        info = MODEL_REGISTRY[self.model_id]
        t0 = time.perf_counter()
        with pinned_torch_hub():
            model = MapAnything.from_pretrained(self.model_id, revision=info["revision"])
        if self.half:
            # The image encoder and the multi-view transformer hold ~92% of the weights and run
            # under autocast; the small geometric encoders and heads stay fp32 because parts of them
            # run outside autocast and refuse mixed dtypes.
            for name in HALF_MODULES:
                _run_in_half(getattr(model, name))
        for name in NO_CUDNN_MODULES:
            _without_cudnn(getattr(model, name))
        self.model = model.to("cuda").eval()
        self.torch.cuda.synchronize()
        self.weights_mib = self.torch.cuda.memory_allocated() / 2**20
        self.log(f"Loaded {self.model_id} ({self.weights_mib:.0f} MiB on GPU) in {time.perf_counter() - t0:.1f}s")

    def unload(self) -> None:
        self.model = None
        self.torch.cuda.empty_cache()

    def _views(self, paths: list[Path], intrinsics: list[np.ndarray | None] | None = None,
               ) -> tuple[list[dict[str, Any]], list[InputLayout]]:
        import torchvision.transforms as tvf
        from uniception.models.encoders.image_normalizations import IMAGE_NORMALIZATION_DICT

        norm = IMAGE_NORMALIZATION_DICT["dinov2"]
        to_tensor = tvf.Compose([tvf.ToTensor(), tvf.Normalize(mean=norm.mean, std=norm.std)])
        views, layouts = [], []
        for i, path in enumerate(paths):
            img, layout = load_model_view(path)
            layouts.append(layout)
            view = {
                "img": to_tensor(img)[None],
                "true_shape": np.int32([img.size[::-1]]),
                "idx": i,
                "instance": str(i),
                "data_norm_type": ["dinov2"],
            }
            if intrinsics is not None and intrinsics[i] is not None:
                import torch

                view["intrinsics"] = torch.from_numpy(np.asarray(intrinsics[i], np.float32))[None]
            views.append(view)
        return views, layouts

    def predict(self, paths: list[Path], intrinsics: list[np.ndarray | None] | None = None,
                ) -> list[dict[str, np.ndarray]]:
        """Poses, intrinsics and point maps for one batch, all in the batch's own metric frame."""
        torch = self.torch
        if self.model is None:
            self.load()
        t_prep = time.perf_counter()
        views, layouts = self._views(paths, intrinsics)
        self.stats.prepare_seconds += time.perf_counter() - t_prep
        torch.cuda.reset_peak_memory_stats()
        torch.cuda.synchronize()
        t0 = time.perf_counter()
        with torch.no_grad():
            preds = self.model.infer(
                views,
                memory_efficient_inference=True,
                # A fixed, small head minibatch: the adaptive default takes 95% of free VRAM,
                # which spills into shared memory on Windows and crowds other GPU users.
                minibatch_size=self.head_minibatch,
                use_amp=True,
                amp_dtype="fp16",  # the RTX 20xx has no bf16
                apply_mask=True,
                mask_edges=True,
            )
        torch.cuda.synchronize()
        seconds = time.perf_counter() - t0
        peak_a = torch.cuda.max_memory_allocated() / 2**20
        peak_r = torch.cuda.max_memory_reserved() / 2**20
        self.stats.seconds += seconds
        self.stats.peak_allocated_mib = max(self.stats.peak_allocated_mib, peak_a)
        self.stats.peak_reserved_mib = max(self.stats.peak_reserved_mib, peak_r)
        self.stats.batches.append({"views": len(paths), "seconds": round(seconds, 2),
                                   "peak_allocated_mib": round(peak_a), "peak_reserved_mib": round(peak_r)})
        s = STORE_STRIDE
        out = []
        for pred, layout in zip(preds, layouts):
            pts = pred["pts3d_cam"][0].float().cpu().numpy()
            conf = pred["conf"][0].float().cpu().numpy()
            mask = pred["mask"][0, ..., 0].bool().cpu().numpy() if pred["mask"].ndim == 4 else pred["mask"][0].bool().cpu().numpy()
            K_model = pred["intrinsics"][0].float().cpu().numpy().astype(np.float64)
            K_store = K_model.copy()
            K_store[:2] /= s  # intrinsics of the stored, subsampled point map
            out.append({
                "c2w": pred["camera_poses"][0].float().cpu().numpy().astype(np.float64),
                "K": K_store,
                "K_model": K_model,
                "pts_cam": pts[::s, ::s].astype(np.float16),
                "conf": conf[::s, ::s].astype(np.float16),
                "mask": mask[::s, ::s] & content_mask(layout),  # padding is not a surface
            })
        del preds
        torch.cuda.empty_cache()
        return out
