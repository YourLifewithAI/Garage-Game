"""Line of sight for the mock host: what an avatar can perceive right now.

The founder's rule (6 October 2026): a companion perceives anything within line of sight of its
avatar. The mock tests it geometrically against the room manifest:

- the eye is the avatar's position raised to its eye height: 0.087 m for both avatars, since the
  companion shrank to the player's 10 cm body (game/native/WorldScaleProfile.cs, SmallPlayer);
- each candidate entity is sampled at 15 points on its bounds: the centre, the eight corners and the
  six face centres, each pulled 1 cm (or a quarter of the size, whichever is smaller) inside the box;
- a sample is seen when the segment from the eye to it crosses no occluder: the room's shell parts
  (floor, walls, ceiling, as their polygons' boxes) and every object and creation box, except the
  entity itself and any box that contains the eye;
- the entity is perceived when at least one sample is seen.

Avatars and effects never occlude. Everything here is axis-aligned boxes, which is what the mock
knows about shapes; the kernel host in Run 2 replaces it with physics ray casts against the real
colliders. The rule, not this geometry, is the contract.
"""
from __future__ import annotations

from typing import Iterable, Sequence

Vec = Sequence[float]
# The 10 cm body (WorldScaleProfile.SmallPlayer): 0.10 m tall, radius 0.02 m, eye 0.087 m, reach 0.15 m.
# The founder shrank the companion to the player's size on 6 October 2026; it was 0.24 m with its eye at 0.205 m.
BODY_HEIGHT_M = 0.10
BODY_RADIUS_M = 0.02
EYE_HEIGHT_M = {"companion": 0.087, "player": 0.087}
REACH_M = 0.15
SAMPLE_INSET_M = 0.01
DEGENERATE_PAD_M = 0.001


def eye_point(position: Vec, kind: str) -> list[float]:
    return [position[0], position[1] + EYE_HEIGHT_M.get(kind, EYE_HEIGHT_M["companion"]), position[2]]


def sample_points(box_min: Vec, box_max: Vec) -> list[list[float]]:
    inset = [min(SAMPLE_INSET_M, (box_max[i] - box_min[i]) / 4) for i in range(3)]
    low = [box_min[i] + inset[i] for i in range(3)]
    high = [box_max[i] - inset[i] for i in range(3)]
    mid = [(low[i] + high[i]) / 2 for i in range(3)]
    points = [mid]
    for x in (low[0], high[0]):
        for y in (low[1], high[1]):
            for z in (low[2], high[2]):
                points.append([x, y, z])
    for axis in range(3):
        for end in (low[axis], high[axis]):
            point = list(mid)
            point[axis] = end
            points.append(point)
    return points


def padded(box_min: Vec, box_max: Vec) -> tuple[list[float], list[float]]:
    """A box with a little thickness on any axis where it has none (a wall polygon's box)."""
    low, high = list(box_min), list(box_max)
    for i in range(3):
        if high[i] - low[i] < 2 * DEGENERATE_PAD_M:
            low[i] -= DEGENERATE_PAD_M
            high[i] += DEGENERATE_PAD_M
    return low, high


def contains(box_min: Vec, box_max: Vec, point: Vec) -> bool:
    return all(box_min[i] <= point[i] <= box_max[i] for i in range(3))


def segment_hits_box(start: Vec, end: Vec, box_min: Vec, box_max: Vec, epsilon: float = 1e-6) -> bool:
    """True when the open segment start->end passes through the box (slab test)."""
    t_enter, t_exit = epsilon, 1.0 - epsilon
    for i in range(3):
        direction = end[i] - start[i]
        if abs(direction) < 1e-12:
            if start[i] < box_min[i] or start[i] > box_max[i]:
                return False
            continue
        t1 = (box_min[i] - start[i]) / direction
        t2 = (box_max[i] - start[i]) / direction
        if t1 > t2:
            t1, t2 = t2, t1
        t_enter = max(t_enter, t1)
        t_exit = min(t_exit, t2)
        if t_enter > t_exit:
            return False
    return True


def visible(eye: Vec, target_id: str, target_min: Vec, target_max: Vec,
            occluders: Iterable[tuple[str, Vec, Vec]]) -> bool:
    blockers = [(low, high) for oid, low, high in occluders if oid != target_id and not contains(low, high, eye)]
    for point in sample_points(target_min, target_max):
        if not any(segment_hits_box(eye, point, low, high) for low, high in blockers):
            return True
    return False
