# The character rig: the API Wave 2 builds on (K1)

**Status:** frozen for Wave 2 of the characters round ([RUN-2-CHARACTERS.md](../runs/RUN-2-CHARACTERS.md)). K1 owns
`game/scripts/native/Characters/{PartRole.cs,Library/**,Rig/**}`, `game/characters/**`, `tools/characters/**`,
`assets/characters/*/rig.json`, `game/tests/native/Characters/**` and this page. Everything is in the namespace
`GarageGame.Native.Characters`. A change to anything named here goes through the integrator.

Nothing in this code knows a character's name. Every behaviour comes from the part names, the converter's hints and
the geometry, so any folder the converter writes moves, and `rig.json` tunes it by hand.

## 1. Load and build

```csharp
var library = CharacterLibrary.Load(CharacterLibrary.ShippedRoot);   // "res://characters"; later also "user://characters"
CharacterBody? body = library.Build("stickbear");                    // null for null, unknown or refused: keep the prototype
if (body != null) visualRoot.AddChild(body);                          // feet at y=0, 0.10 m tall, facing -Z
```

- `CharacterLibrary.Load(root)` takes any root: `res://`, `user://` or an absolute path. It reads every folder under it in
  id order, validates it completely and builds its meshes; a refused folder never stops the others. Call it on the
  main thread (it makes meshes and materials; Godot crashed when they were made on worker threads).
- `library.Entries` lists every folder: `Id`, `Folder`, `Definition` (null when refused) and `RefusalReason` (plain
  text for logs and the HUD). `library.Characters` lists the loaded ones; `TryGet(id, out definition)`.
- `CharacterRig.Build(definition)` makes a new body each call (the meshes are shared). `CharacterRig.Build(folder)` and
  `CharacterRig.TryBuild(folder, out body, out reason)` read one folder directly.
- A folder is refused, with a reason, for: a folder name that is not a token (`[a-z][a-z0-9_]{0,63}`); a missing
  `character.json` or `character.glb`; malformed JSON or a repeated key; any key the converter does not write; a
  schema, version, unit or axis other than the converter's; a height other than 0.10 m, or bounds whose feet are not
  at y=0; a non-finite number anywhere (`1e999`); an unknown motion kind or effect hint; an effect without a hint; a
  bubble hint without see-through, shimmer and an `#RRGGBB` aura colour; no parts, more than 63 parts (64 bones), a
  repeated or missing parent, or a pivot that is not its parent's plus `local_pivot_m`; a GLB that is not one
  self-contained binary glTF 2.0 file (external files, skins, animations, cameras, textures, morph targets, interleaved
  or sparse accessors, non-triangle primitives, out-of-range indices, non-finite vertices); a GLB node tree or node
  translation that disagrees with `character.json`; more than **70,000 triangles**, or a count that disagrees with
  `character.json`; more than **210,000 vertices** copied into meshes (a shared accessor counts once for every primitive
  that uses it: the review's fix, 13 October); meshes that do not stand 0.10 m tall from y=0; any problem in `rig.json` (section 7).
- The GLB is read once as bytes and never through Godot's importer. Its meshes are built straight from the verified
  accessors (the same vertices, triangles in Godot's winding, normals and sRGB base colours as Godot's `GltfDocument`,
  tested index for index), because `GltfDocument` alone took 59 ms for the five.

**The shipped copies.** `res://` is `game/`, so the five live in `game/characters/<id>/` as `character.glb`,
`character.json` and `rig.json` only, copied from `assets/characters/` by `python tools/characters/sync_characters.py`
(`--check` reports stale copies; the suite checks freshness too). Each GLB has a committed `character.glb.import`
with `importer="keep"`: the editor never imports it, and an export packs the raw bytes (checked with `--export-pack`
on a probe project: the kept GLB is stored as `res://characters/<id>/character.glb`, an un-kept one only as an
imported `.scn`). `game/characters/.gitignore` re-includes those sidecars against the repository's `*.import` rule.
The JSON files ride the export's existing `*.json` include filter. Rerun the sync after changing `assets/characters`.

## 2. The body

```
CharacterBody (Node3D)
├── Skeleton (Skeleton3D)            bone 0 character_root at root_pivot_m; bone i+1 = part i, named and parented as the part
│   ├── <bone> (BoneAttachment3D)    one per bone, carrying the part's MeshInstance3D "Mesh" (rigid binding)
│   ├── LegIK (TwoBoneIK3D)          the leg-and-foot chains, inactive
│   ├── LookHead, LookEye_<eye>      LookAtModifier3D, inactive
│   ├── LegSprings, Springs          SpringBoneSimulator3D, each with a BodyCollider capsule
├── LookTarget (Node3D)              where the face looks
└── IkTargets (Node3D)               <leg>_target, <leg>_pole
```

The modifiers run in that child order after the pose is applied: leg IK, look-ats, then springs (which react to the
final pose of their parents). They step on the **physics tick** (`Skeleton3D.ModifierCallbackModeProcess` is `Physics`,
set by `CharacterRig.Build`): Godot's default steps them on the render frame, which made the springs' tuning depend on the
display's refresh rate (the review's fix, 13 October; physics interpolation is off in this project).

| Member | What |
|---|---|
| `Definition` | the validated `CharacterDefinition` (parts, hints, display name, GLB SHA-256, sidecar) |
| `Skeleton`, `BoneCount`, `Bones[i]` | the skeleton and one `RigBone` per bone |
| `CharacterBody.RootBone` (0) | `character_root`, on the ground: scale it for squash and stretch and the feet stay put |
| `BodyBone`, `HeadBone` | the body part's bone; the head's, or the body's when there is no head |
| `Profile` | the measured `MotionProfile` (section 6) |
| `Pose` | this frame's `PoseBuffer` (section 3) |
| `Secondary`, `Look`, `Legs`, `LegSolver` | springs, look-ats, leg IK (sections 4 and 5) |
| `FindBone(name)`, `FindBone(role, side?, nth)`, `CountBones(role, side?)`, `GetBones(role, span, side?)` | lookups; allocation-free |
| `Attachment(bone)`, `PartMesh(bone)` | the bone's `BoneAttachment3D` and its mesh (null for a joint-only part) |
| `ApplyPose()` | hands `Pose` to the skeleton (changed bones only) |
| `FinalBoneTransform(bone)` | the bone after the modifiers, in the body's space, as of the last skeleton update |
| `FinalPoseHash()`, `LowestPointY(pose?)`, `FinalLowestPointY()` | test helpers (section 3) |
| `UseLegIk(on)` | plants the feet with the IK and mutes the leg springs, or frees them |
| `RestBounds`, `Notes` | the meshes' bounds at rest; what the inference skipped (a spent budget, a chain rig.json removed) |

`RigBone`: `Index`, `Name`, `Parent`, `Part` (-1 for the root), `Role`, `Side`, `Kind` (solid or effect), `Effect`
(the effect hint), `Bubble` (the bubble hint and aura colour, or null), `IsRigid`, `RestLocal` (local_pivot_m),
`RestModel` (pivot_m), `MeshBounds` (relative to the joint), `HasMesh`, `Depth`, `Children`, `ReachM`.

### Roles and sides

`PartRoles.Infer(name, kind, effect)`: the role is the part name's head noun, the last word that is not a side, a
colour, a place or a number (`eye_glint_screen_left` is a glint, `arch_red` an arch, `floating_sparkle_0` a sparkle,
`straps_*` a mark); `hat_band` is its own role; an effect with no known noun takes its role from its hint (flicker:
flame, pulse: aura, others: sparkle); anything else is décor.

| Role (`rig.json` token) | Words |
|---|---|
| `body`, `head` | body, torso, belly; head |
| `eye`, `pupil`, `lid`, `brow`, `mouth`, `nose`, `cheek`, `glint` | eye, eyeball; pupil, iris; lid, eyelid; brow, eyebrow; mouth, smile, grin; nose, snout; cheek, blush; glint, highlight, shine |
| `arm`, `hand`, `paw`, `digit` | arm; hand, mitten, fist; paw; digit, claw, finger, thumb, toe, pad, nail |
| `leg`, `foot`, `shoe` | leg; foot, feet; shoe, boot |
| `mark` | strap, stripe, band, line, mark, nostril, freckle, spot (ink-ish marks) |
| `hat`, `hat_band`, `hair`, `horn`, `ear`, `tail` | hat, cap, brim; hat_band; hair, hairline, curl, fringe, bangs, tuft, ponytail; horn, antler; ear; tail |
| `torch`, `flame`, `wand`, `aura`, `sparkle` | torch, lantern; flame, fire; wand, staff; aura, glow, halo; sparkle, star, twinkle, glitter |
| `calyx`, `arch`, `heart`, `decor` | calyx, leaf, leaves, stem; arch, rainbow; heart; anything else |

Plurals of these words count. `PartRoles.IsHand` (hand or paw), `IsFoot` (foot or shoe), `IsInkLike`, `IsEffect` group
them. **Sides** are `BodySide.Right` (+X, the converter's `screen_left`), `BodySide.Left` (-X, `screen_right`) and
`Center`; a bare `left`/`right` follows the same screen convention, and a part with no side word takes its parent's.
(The enum is `BodySide` because `Godot.Side` exists and Wave 2 files import both namespaces.) **Rigid:** décor, and
any part `rig.json` lists in `rigid`, rides its parent: no chain, no look-at, no IK, and `PoseBuffer` ignores writes.

## 3. The pose API

```csharp
public readonly record struct PoseClock(double TimeS, float DeltaS, long Frame);
public enum PoseLayer { Rest = 0, Gait = 1, Contact = 2, Life = 3, Secondary = 4, Gesture = 5, Tumble = 6 }

public interface IPoseLayer<TState> where TState : struct
{
    PoseLayer Layer { get; }
    void Apply(PoseBuffer pose, in TState state, in PoseClock clock);
}

var stack = new PoseStack<BodyState>(body);   // TState: the controller's snapshot (Wave 0); tests use their own struct
stack.Add(gait); stack.Add(contact); stack.Add(life);   // sorted by Layer; equal layers keep their order
stack.Run(state, clock);                     // every frame: Reset, each layer, ApplyPose, re-tune the springs to Δt
stack.Suspend(PoseLayer.Gait, true);         // the tumble holds Gait, Contact and Life while it owns the bones
stack.Evaluate(state, clock);                // the same without touching the skeleton (tests, hashes)
```

**`PoseBuffer`** holds one bone-local position, rotation and scale per bone index, exactly as `Skeleton3D`'s bone
poses: relative to the parent bone, with rest = `RestLocal`, identity rotation, unit scale. Rests carry no rotation, so
a rotation turns a part about its own joint, with the character's axes at rest (+X its right, +Y up, -Z forward).

| Call | Effect |
|---|---|
| `Reset()` | every bone to rest; `Layer` back to `Rest` |
| `Begin(layer)` | the next layer starts; throws if it would run before one it must follow (`PoseStack` calls it) |
| `SetPosition(b, p)`, `AddPosition(b, d)` | position relative to the parent's joint; `Add` moves in the parent's frame |
| `SetRotation(b, q)`, `AddRotation(b, d)` | rotation (normalised); `Add` is `rotation = d * rotation`: a turn about the joint in the parent's frame |
| `SetScale(b, s)`, `MultiplyScale(b, f)` | scale; squash with `(1/√y, y, 1/√y)` keeps volume |
| `GetPosition/GetRotation/GetScale/GetLocal(b)`, `RestPosition(b)`, `Parent(b)`, `IsRigid(b)` | reads |
| `ModelTransform(b)` | the bone in the character's space under this pose (before the modifiers), cached until the next write |
| `CopyFrom(other)`, `BlendToward(target, w)` | for the tumble's get-up blend (rigid bones untouched) |
| `ApplyTo(skeleton)` | what `CharacterBody.ApplyPose` calls; only changed bones are sent |
| `Hash()` | FNV-1a 64 over every value rounded to 1e-7, rotations sign-canonical: same frames, same hash, in any process |
| `NonFiniteWrites`, `RigidWrites` | counters since construction: a NaN, infinite or degenerate write is refused and counted (tests expect no new ones); a write to a rigid bone is ignored and counted |

Rules (from the plan): a layer sets only the bones it owns, or adds to others; a layer with nothing to say leaves the
pose alone; layers are pure functions of the snapshot, the clock and their own state, and allocate nothing per frame.
`Evaluate`, `Run`, the lookups and `Hash` allocate nothing (tested over 600 frames). After the modifiers, read
`FinalBoneTransform` or hash with `FinalPoseHash()`; both lag one frame behind `ApplyPose` (the skeleton updates at the
end of the frame) and are deterministic too (tested across processes). `LowestPointY(pose)` is the lowest mesh corner
under a pose (bounding-box corners, so conservative); `FinalLowestPointY()` the same after the modifiers.

The secondary layer (4) is not a buffer layer: the springs, look-ats and IK below are skeleton modifiers.

## 4. Springs

`body.Secondary` (`SecondaryMotion`): `Chains` (each a `SpringChain`: `Simulator`, `Setting`, `Kind`, `RootBone`,
`EndBone`, `Bones`, `TailDirection`, `TailLengthM`, `LengthM`, `RootSegmentM`, `PendulumHz`, `FrequencyHz`,
`DampingRatio`, `RadiusM`, `GravityMps`, `Collides`), `Simulator`, `LegSimulator`, `BodyCollider`, `SetWind(velocity)`
(the engine's external force on both simulators), `SetLegSpringsActive(on)`, `MatchTimestep(Δt)`.

Chains are inferred in priority order, at most 6 of at most 6 bones: a hat (and a hat child), hair (down its hair
children), ears and tails, **dangling** legs (leg then foot) and feet without legs under a `hop` or `float`, arms (arm
then hand) under a `waddle` or `float` (the hop and stride gaits swing arms themselves), horns, flames, wands, and
`drift`/`orbit` sparkles. Leg chains run on `LegSimulator`, the rest on `Simulator`. A chain collides with a capsule
inside the body (80% of the body's narrower half-width, 90% of its height) unless one of its joints starts inside it.

**Tuning.** The engine's spring is a Verlet step whose stiffness acts like gravity: stiffness `(2πf)²·L·Δt` makes a
joint whose segment is `L` long swing at `f`. Each chain's `f` is the pendulum of its whole length, `√(g/L)/2π` with
g = 3.5 m/s², times the profile's and the chain's springiness; the damping ratio is 0.12 times the spring_damping
multipliers. Measured: Cloudpuff's leg swings within 20% of its tuned 1.17 Hz. Because the step depends on Δt,
`PoseStack.Run` re-tunes stiffness and drag whenever the frame delta moves by 5%; frame-rate independent.

**Held.** `Secondary.Held` holds every chain, the leg chains included, while something owns the bones (the tumble's
flop); the hold belongs to the rig, so contact letting go of the leg IK (`UseLegIk(false)`) cannot restart them.

**Known limits.** A sparkle's joint is its centre, so its spring tilts it rather than moving it: drift and orbit
*paths* are a layer's job (add a position to the sparkle's bone). Godot warns that spring bones under a scaled parent
"will likely not behave as expected": keep the root's squash small (the plan's ≤ 15%) and expect K3 to check it.
Gravity is 0 by default (a non-zero engine gravity never returns a part to its drawn pose).

## 5. Look-at and leg IK

`body.Look` (`LookRig`): `Target` (move it; it starts 1 m ahead of the eyes), `Head` (a `LookAt` on the head, or on the
body when `HeadIsBody`), `Eyes` (up to two, one per side), `Count` (at most 3), `SetActive(on, influence)`. Each
`LookAt` has `Modifier`, `Bone`, `YawLimitRad`, `PitchLimitRad`: head ±50°/±30°, body-as-head ±20°/±10°, eyes
±25°/±15°, soft from 80% of the limit (`Relative`, forward -Z, yaw about +Y first). Godot's `PrimaryLimitAngle` is the
whole symmetric range, so the rig passes twice these half-angles (measured). Built **inactive**: turning the body to a
target would fight the gait, so the life and contact layers decide.

`body.Legs` (`LegIk`, at most 2, one `TwoBoneIK3D` named `LegIK`): `Setting`, `HipBone` (the leg), `FootBone`,
`Side`, `Target`, `Pole`, `SoleOffset` (the virtual end bone: the foot's sole, below its joint, in the foot's space),
`RestSole`. With legs of one rigid part there is no knee: the two joints are the hip and the ankle, and the solver
reaches the sole to `Target` with the ankle bending toward `Pole` (in front). **Inactive** until contact calls
`body.UseLegIk(true)`, which also mutes the leg springs; measured to put the sole within 0.5 mm of a reachable target.
Stickbear and Cloudpuff have two each; the others none.

## 6. The motion profile

`body.Profile` (`MotionProfile`), measured from the meshes at build (`Describe(bones)` prints it):

| Value | Measured as |
|---|---|
| `HeightM`, `WidthM`, `DepthM`; `BodyWidthM`, `BodyDepthM`, `BodyHeightM` | the rest bounds of all meshes; of the body part |
| `LegCount`, `HipHeightM` | legs; the mean height of their joints (Stickbear about 0.043 m, Cloudpuff 0.030 m) |
| `FootCount`, `FootLengthM`, `FootSpacingM` | feet or shoes; their longer horizontal size; right-to-left distance |
| `LegLengthM` | the pendulum: the hip height; legless, the foot length; footless, half the body's depth |
| `NaturalCadenceHz` | √(g/L)/2π, g = 3.5: one full stride cycle (Stickbear 1.44 Hz, Cloudpuff 1.72 Hz) |
| `ReachM` | a planted foot's travel per step, 2·L·sin 30° |
| `WalkStrideM` | the walk speed (0.32 m/s) over the cadence: longer legs, fewer and longer strides |
| `Roundness` | the body's narrower horizontal size over 65% of the height, clamped to 1 (Stickbear 0.19, the others 0.75 to 0.98) |
| `VolumeM3` | the solid parts' bounding ellipsoids |
| `SquashAmount` | 0.02 + 0.12 · roundness (Stickbear 0.043, Potato Man 0.127) |
| `SettleTimeS` | 0.12 s + 3 s/m · ∛volume (Stickbear 0.20 s, the round four 0.28 to 0.29 s) |
| `HangingParts` | per chain: length, pendulum and tuned frequency |
| `Gait` | the motion hint, except a `stride` with no legs becomes a `waddle` |

Effective values: `StrideAt(speed)` (the walk stride × `stride`, lengthened at a run by √(speed/walk)) and
`CadenceAt(speed)` = speed / `StrideAt(speed)`; phase gaits by distance: `cycles += distance / StrideAt(speed)`.
`Bounce` = rig.json's `bounce` × `BounceScale` (the testing key sets `BounceScale` to 0.5, 1 or 1.5); `Squash` =
`SquashAmount` × `squash` × `Bounce`; `SettleS` = `SettleTimeS` × `settle`. The other multipliers are read as
`Multipliers.HopHeight`, `.Drift`, `.Lean`, `.Sway`, `.ArmSwing`: K3 scales its own base shapes by them.

## 7. rig.json, the sidecar (schema `enfractal.character.rig`, version 1)

Optional, hand-written, beside `character.json`; validated strictly: an unknown key, part or role, a repeated rigid
entry, or a number out of range refuses the whole character with the reason.

```json
{
  "schema": "enfractal.character.rig",
  "version": 1,
  "notes": "Free text for people, up to 2000 characters.",
  "roles": { "wing_screen_left": "arm" },
  "rigid": ["calyx"],
  "chains": {
    "hat": { "springiness": 0.8, "damping": 0.8 },
    "leg_screen_left": { "enabled": false },
    "calyx_leaf": { "enabled": true, "max_bones": 2, "gravity_mps": 0.0, "radius_m": 0.002, "collide": false }
  },
  "profile": { "bounce": 1.1, "squash": 1.15, "stride": 0.8, "settle": 1.2, "springiness": 1.25,
               "spring_damping": 0.8, "hop_height": 1.3, "drift": 1.6, "lean": 1.3, "sway": 1.3, "arm_swing": 1.3 }
}
```

- `roles`: part → role token (section 2). `rigid`: parts that ride their parent.
- `chains`, keyed by the chain's root part: `enabled` (false removes an inferred chain; true makes one at any free
  part, before the inferred ones), `springiness` 0.25 to 4, `damping` 0.1 to 5 (multiplies the ratio), `gravity_mps` 0
  to 1, `radius_m` 0.0005 to 0.01, `max_bones` 1 to 6, `collide`.
- `profile`: the named multipliers, 0 to 4 (`bounce` 0 to 3), default 1.

The five shipped sidecars hold starting values from the plan's profile table: the Gubble's soft, wide lean and quick
flame and sparkles; Potato Man's heavy, rolling waddle with short steps and a lagging hat and hair; Tomato Man's
springy, high hops and trailing wand; Cloudpuff's floaty drift and slow leg springs; Stickbear's long strides, little
squash and swinging arms. The family's tuning replaces them.

## 8. Budgets and measurements (tests: `res://tests/native_characters.tscn`)

The suite prints `CHARACTERS_PROFILE <id>` (each measured profile), `CHARACTERS_HASHES` and `CHARACTERS_LOAD`, then
`NATIVE_CHARACTERS: n/n checks passed`. `-- --skip-budget` drops its two wall-clock and memory checks where wall time
means nothing (a cloud container); the founder's machine keeps them.

| Budget | Measured |
|---|---|
| bones ≤ 64, chains ≤ 6 of ≤ 6 bones, IK ≤ 2, look-ats ≤ 3 | Stickbear 39 bones (the most); chains 2 to 4; look-ats 3 each |
| triangles ≤ 60,000 (refused over 70,000) | Potato Man 55,848 |
| load: all five read, validated and rigged within 150 ms | 94 to 126 ms after the engine's boot-time first uses (JSON, files, a first mesh: 29 to 40 ms, paid by the room's load in the game); 133 to 145 ms in a fresh process (Debug build, cold JIT); about 50 ms warm |
| memory ≤ 25 MB | static +4.7 MB, managed +2.5 MB for the library and five bodies (headless: no GPU memory) |
| frame time (0.4 ms both bodies) | not measured here: K3 measures with the gaits; layers and hashing allocate nothing |

## 9. For each Wave 2 packet

- **K3 (gaits, life):** `IPoseLayer<BodyState>` at `Gait` and `Life`; bones by role and side; the profile's stride,
  cadence, squash, settle and multipliers; squash on `RootBone`; blinks on `Lid`/`Eye` bones; `Pose.Hash()` and
  `LowestPointY` for the 600-frame tests; `Pose.NonFiniteWrites` stays 0.
- **K4 (contact):** a `Contact` layer; `Legs[i].Target` and `body.UseLegIk`; `Look.Target` and `Look.SetActive`;
  `Secondary.SetWind`; `FinalBoneTransform` for where a foot ended up.
- **K5 (the Gubble):** gestures as a `Gesture` layer adding to `RootBone`; anchors by role (`Aura`, `Flame`) through
  `body.Attachment(bone)` or a `BoneAttachment3D` of your own on `body.Skeleton`; bubble parts by `RigBone.Bubble`.
- **K7 (the tumble):** `stack.Suspend` for Gait, Contact and Life; a `Tumble` layer with `CopyFrom`/`BlendToward`;
  physical bones on `body.Skeleton` by role (`Body`, `Head`, `Arm`, `Leg`, props).
- **L8 (the look):** parts by `RigBone.Role`, `Kind`, `Effect` and `Bubble`, meshes by `PartMesh(bone)`; the surfaces
  carry `StandardMaterial3D`s named after the converter's palette keys with its base colours, as Godot's reader makes.
