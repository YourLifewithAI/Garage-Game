# Interpretation format, version 1

All coordinates are drawing units: `[image_x, image_y, depth_toward_viewer]`.
No metre conversion is needed. Width and height must use the same unit.
The front render preserves image left/right: game X is negative image X,
game Y is negative image Y, game Z is negative depth. The GLB faces -Z.

Coordinates describe the **interpreted** shape, not a mandatory literal trace.
Shape/proportion notes in the player's description take precedence over the
drawn contour: widen, round or reshape it as requested, while retaining the
face, expression, features, asymmetry and wobble. Keep equal coordinate units;
change contour landmarks rather than stretching the whole character's face.
Refit attachments and pivots to the new body. Check front and three-quarter
views; added depth alone does not satisfy a request for a wider front.

Optional root `applied_notes` is an array of strings, each pairing a description
note with the concrete interpretation change. Record all notes applied, and
any unresolved ambiguity in `uncertainties`. It is saved in interpretation.json
and character.json; the offline converter does not interpret prose or execute
it. Older interpretations may omit the field.

Root fields (all required):

```json
{
  "version": 1,
  "name": "A character",
  "image_size": [800, 1000],
  "origin": [400, 900, 0],
  "observations": ["What the drawing actually shows"],
  "uncertainties": ["What was inferred"],
  "colour_reasoning": "Where the supplied colours were placed",
  "motion": {"kind": "hop", "reason": "Short legs beneath a round body"},
  "palette": {"body": "#D29A54", "ink": "#302B39"},
  "parts": [
    {"name": "body", "role": "body", "parent": null,
     "pivot": [400, 600, 0], "shapes": [
       {"kind": "ellipsoid", "center": [400, 550, 0],
        "radii": [100, 140, 70], "colour": "body"}
     ]}
  ]
}
```

Parts have unique lowercase names (`[a-z][a-z0-9_]*`), a descriptive `role`,
`parent` (null or the name of a previous part), a joint `pivot` in absolute
drawing coordinates, and one or more shapes. Empty shapes are allowed for
joint-only nodes. A part's pivot does not change its shapes' coordinates.

Every shape has `kind` and `colour` (a palette key). Optional `surface` is a
previous part's name: that part's **first shape** must be an ellipsoid or volume.
The new shape's depth coordinates become offsets from that shape's front skin.
The skin is evaluated at every vertex, so pads and faces follow the curvature.
The part's pivot remains an absolute coordinate, even when its shapes attach.

Shapes:

- `ellipsoid`: `center: [x,y,d]`, `radii: [rx,ry,rd]`, all radii positive.
- `volume`: `contour: [[x,y], ...]` (3–64 points, no repeated last point),
  `center: [x,y,d]`, optional `thickness: positive_number`. The default is
  0.95 times the narrower smoothed contour dimension, with full rounded shoulders
  and the interpreted front silhouette unchanged (including note-driven
  reshaping). Explicit thickness is useful for
  thin marks and shallow props. Center x/y is a descriptive
  landmark; depth sets the middle plane. Concave outlines are supported. The
  contour is smoothed by Catmull–Rom interpolation unless `smooth: false`.
  A triangulated surface rounds both sides into a closed cushion, with skin
  thickness increasing with distance from the outline. Holes are unsupported.
  `smooth: false` is useful for deliberate points such as a heart's bottom.
- `tube`: `points: [[x,y,d], ...]` (2–64), `radii: [r,...]` (one per point,
  strictly positive; use a small value such as 0.3 for tips).
  Smoothed centerline and round cross-section, with **flat** closed end caps.
  Overlap joins; for a rounded free end add an ellipsoid with the endpoint radius
  in the same part (or taper the endpoint to a small positive radius).
  Optional `smooth: false` makes straight sections. Radius is interpolated
  with the centerline; avoid coincident points and extreme radius changes.

Multiple shapes in a part share its pivot: a face or a set of painted marks
need not have one child per stroke. Keep bands off a narrowing silhouette edge,
or continue their geometry round it; surface attachment is only the front skin.

Optional part fields (converter-local hints, no shared schema change):

```json
{
  "kind": "effect",
  "motion_hint": {"kind": "twinkle", "reason": "Loose star beside the wand"},
  "material_hint": {
    "kind": "bubble", "see_through": true, "rim": "shimmer",
    "aura_colour": "#B4EED8"
  }
}
```

`kind` defaults to `solid`; `effect` requires `motion_hint` with a kind of
`twinkle`, `orbit`, `drift`, `flicker` or `pulse` and a short reason. Give each
independently moving effect its own part and pivot. An aura without geometry
may be an empty effect node. Motion hints describe intent; no animation is baked.
`material_hint` is independent of part kind, and applies to every shape in that
part. A bubble's shape colours are its **opaque fallback**, usually pale blue.
Keep its face and held props in separate solid parts if they should stay opaque.

Both hints and kind are in character.json and node extras.garage_game_part.
Hinted GLB materials are named `bubble__<part>__<colour>` and carry
extras.garage_game_material, including `fallback_colour` as sRGB hex. Characters
converted before the rename carry enfractal_part and enfractal_material; the game
and the turntable read either key (contracts/README.md, "Schema ids"). Base colours
remain opaque for readers without a bubble shader. The CPU turntable previews
only explicitly hinted materials; the game's Look lane supplies the final shader.

Record the chosen sketch and any repositioned loose effects in observations.
Uneven feet are a pose, not an instruction to change leg lengths. Origin is
between the feet (or the floating body's bottom); the lowest geometry sets y=0.
All geometry, including props/effects, is scaled together to 0.10 m high.
No textures, automatic OCR, skeletal rigging, paths, URLs, scripts or tool calls.
