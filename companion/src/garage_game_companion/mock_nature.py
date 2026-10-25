"""Nature for the mock host: Grow, Fire and its kinds, the campfires, repair, undo and nature.describe, as the kernel host answers them
(game/scripts/native/Kernel/CommandHostNature.cs, CommandHostGrow.cs, CommandHostFireKinds.cs, CommandHostCamps.cs,
CommandHostFirepits.cs and CommandHostConsequences.cs; contracts/README.md "Nature"), minus physics.

What the mock keeps (room state, in memory with the room; the kernel saves it with the island):
- **Plants.** The island's plant copies (`plant:<set>-<row>`) the game side gives it (`add_plant`: the box world reads no plant
  tables), each with its root, its natural height and its fuel class. Grow grows every plant rooted in its circle by the pack's
  share of its natural size, up to its cap (twice its natural size or 0.4 m taller, whichever comes first), twice the share while
  it rains; fire burns them down, leaving ash. As in the kernel, plants are no entities: no query lists them.
- **The ground.** A Fire cast's 5 cm cells are sampled from the box world: the floor (or a walkable shell part) or the top of a
  thing whose footprint holds the cell, below the cast's point, its material deciding its fuel (wood, grass, fabric, paper and
  the like burn; stone and cardboard do not); a plant rooted in a cell adds its fuel. Sheltered ground (locked things, and the
  builds a fire may not scorch) and snuffed ground do not catch.
- **The fire.** One at a time. It takes hold within 13 cm of its centre (a Fireline along its whole line), lays ash there, and
  burns its whole circle at once until its duration ends (the mock does not spread it cell by cell): the things in its reach burn
  by the fire rule (locked things never; a creation is scorched, in Creative only once the player said yes; the island's stock
  and plants burn down, gone until undone), slowed by rain. Snuff aimed at it puts it all out.
- **The campfires.** The starting campfire 0.2 m in front of the player's spawn, lit from the start, and the manifest's firepits
  (`x_firepits`, each kept where it stands on the floor, 0.6 m from every other campfire; the box world has no land to spread
  the kernel's own firepits over, so a room without them has none). A Fire cast that reaches an unlit or put-out one lights it,
  and nothing else; 30 s of rain puts a lit one out (the starting campfire only once the team has learned Fire); Snuff puts one
  out. `player_rests` is the rest key: the first rest by the starting campfire teaches Fire (decision 72).
- **The weather.** The kernel's six-minute sequence on the host's clock from the room's load: dry for two minutes, rain, dry.
- **Grow's unlocks** (decisions 76 and 104 to 107, CommandHostGrowKinds.cs and its siblings): the team's Grow expertise count opens
  seedless growth and replanting at two, the bridge at three and ivy at five, each refused permission_denied before it in the kernel's
  words and order. Sprouts on bare fertile ground (a shell part of grass or soil) copy the nearest grass and flower plants on the
  kernel's lattice; replanting returns the nearest burnt plant that seeds as a seedling; both grow through three phases of an hour of
  daylight on the host's clock (decision 122: only the hours from 06:00 to 18:00 count, so a night cast grows from sunrise; the mock has
  no island clock record: one game hour a real hour, its hour of day in UTC). A bridge is dragged from lip to lip over the box
  world's floors (a walkable top at each end, level, 0.4 to 1.2 m, nothing standable within 0.15 m under the deck between them,
  nothing in the way). The box world has no faces to climb, so never ivy (the kernel finds a wall by the room's physics), as never a
  waterpad. Sprouts and bridges burn as plants and are undone as the kernel's are.
- **Undo.** Each Grow, Fire and repair is one undo step, recorded with its mode; room.undo takes the latest steps back by room
  revision, in Creative only, as the kernel's NatureUndo does. Lighting a firepit is no step, as in the kernel.

What the mock leaves to the kernel (it needs the body, water or the island's land): water (so never a Will-o'-wisp or a
waterpad, as never a boat), fire spreading by wind and fuel, fire hurting the player and its notices and lessons, the hints, the
firepits' spread over land. A build the Gubble's fire scorches in Challenge and Live is a consequence fact, as in the kernel.

The security rules hold exactly as the kernel's: the price follows the principal (mock_energy.py), never a field; no Fire before
the first rest, whoever asks; the Gubble's Fire behind the player's "Let the Gubble use fire" (`set_gubble_fire`, the player's
control, never a request), one fire of its own at a time and 20 s after its last; Snuff lighting nothing, so none of the Gubble's
fire guardrails stop it; Creative's one ask before a fire burns a build, the player's no lighting the fire with the builds spared.
test_kernel_alignment.py holds every word and number here equal to the kernel's.
"""
from __future__ import annotations

import copy
import math
import re
import secrets
from dataclasses import dataclass, field
from typing import Callable

from .refusals import HostError

# common.schema.json plant_id: plant:<set>-<row>, the row in decimal without leading zeros.
_PLANT_ID = re.compile(r"plant:[a-z][a-z0-9_]{0,31}-(0|[1-9][0-9]{0,7})")
_PLAYER = "player:local"
_COMPANION = "companion:local"
_GUBBLE = "avatar:companion"

# ---- NatureLaws and NatureState (Kernel/NatureState.cs) ----
STEP_S = 0.5  # NatureLaws.StepS: the simulation's step
WEATHER_CYCLE_S = 360.0  # NatureLaws.WeatherCycleS
WEATHER_DRY_S = 120.0  # NatureLaws.WeatherDryS
WEATHER_RAMP_S = 45.0  # NatureLaws.WeatherRampS
WEATHER_WET_UNTIL_S = 225.0  # NatureLaws.WeatherWetUntilS
WIND_TURN_S = 150.0  # NatureLaws.WindTurnS
WIND_CALM_MPS = 0.12  # NatureLaws.WindCalmMps
WIND_RAIN_MPS = 0.22  # NatureLaws.WindRainMps
WEATHER_RAIN01 = 0.65  # NatureLaws.WeatherRain01: above it, "rain"
WEATHER_SHOWERS01 = 0.02  # NatureLaws.WeatherShowers01: above it, "showers"
FRESH_MOISTURE = 0.35  # NatureLaws.FreshMoisture
RAIN_MOISTURE = 0.35  # NatureLaws.RainMoisture
GROUND_GRASS_FUEL = 0.7  # NatureLaws.GrassFuel
GROUND_WOOD_FUEL = 1.0  # NatureLaws.WoodFuel
GROUND_LIGHT_FUEL = 0.8  # NatureLaws.LightFuel
FLOWER_FUEL = 0.35  # NatureLaws.FlowerFuel (Green ash's fuel)
IGNITION_RADIUS_M = 0.13  # NatureLaws.IgnitionRadiusM
IGNITION_MAX_MOISTURE = 0.8  # NatureLaws.IgnitionMaxMoisture
OBJECT_BURN_PER_INTENSITY_S = 0.22 / 0.6  # NatureLaws.ObjectBurnPerIntensityS
OBJECT_CATCH_M = 0.08  # NatureLaws.ObjectCatchM: burning ground this close to a thing's bounds burns it
SHELTER_MARGIN_M = 0.04  # NatureLaws.ShelterMarginM
PLAYER_BURN_M = 0.12  # NatureLaws.PlayerBurnM
COMPANION_FIRE_COOLDOWN_S = 20.0  # NatureLaws.CompanionFireCooldownS
CELL_M = 0.05  # NatureState.CellM
MAX_BURNING = 200  # NatureState.MaxBurning
MAX_GROWN_PLANTS = 4096  # NatureState.MaxGrownPlants
MAX_BURNT_PLANTS = 8192  # NatureState.MaxBurntPlants
MAX_UNDO_STEPS = 8  # NatureState.MaxUndoSteps
# ---- CommandHostNature ----
MAX_FIRE_TARGETS = 64  # CommandHost.MaxFireTargets
MAX_PREVIEW_CELLS = 200  # CommandHost.MaxPreviewCells
FLAMMABLE_MATERIALS = frozenset({"wood", "bark", "foliage", "fabric", "paper", "plastic", "leaf"})  # CommandHost.FlammableMaterials
MAX_NATURE_PREVIEWS = 32  # the kernel keeps at most 32 previews
PREVIEW_LIFETIME_S = 300.0  # CommandHost.ApprovalLifetime
# ---- PlantLaws (Kernel/CommandHostGrow.cs) ----
CAP_FACTOR = 2.0  # PlantLaws.CapFactor
CAP_EXTRA_M = 0.4  # PlantLaws.CapExtraM
LEGACY_GROW_SHARE = 0.25  # PlantLaws.LegacyGrowShare
ROOT_HEIGHT_TOLERANCE_M = 0.5  # PlantLaws.RootHeightToleranceM
FIRE_REACH_MARGIN_M = 0.5  # PlantLaws.FireReachMarginM
PLANT_FUEL = {"grass": 0.5, "herb": 0.6, "shrub": 0.8, "broadleaf": 1.0, "conifer": 1.0}  # PlantLaws.GrassFuel ... TreeFuel
GROWN_FUEL_FACTOR = 0.5  # PlantLaws.GrownFuelFactor
ASH_LEFT = 0.6  # PlantLaws.AshLeft
RAIN_GARDEN_FACTOR = 2.0  # PlantLaws.RainGardenFactor
THICKET_SPAN_M = 1.0  # PlantLaws.ThicketSpanM
THICKET_LOOKS_AT = 48  # PlantLaws.ThicketLooksAt
# ---- FireKindLaws (Kernel/CommandHostFireKinds.cs) ----
FIRELINE_LEARN_WINDOW_S = 300.0  # FireKindLaws.FirelineLearnWindowS
FIRELINE_LEARN_APART_M = 0.5  # FireKindLaws.FirelineLearnApartM
FIRELINE_LEARN_OFF_LINE_M = 0.25  # FireKindLaws.FirelineLearnOffLineM
FIRELINE_MAX_M = 3.0  # FireKindLaws.FirelineMaxM
FIRELINE_MIN_M = 0.2  # FireKindLaws.FirelineMinM
FIRELINE_HALF_WIDTH_M = 0.04  # FireKindLaws.FirelineHalfWidthM
FIRELINE_SPREAD_M = 0.3  # FireKindLaws.FirelineSpreadM
SNUFF_RADIUS_M = 1.5  # FireKindLaws.SnuffRadiusM
SNUFF_DAMP_S = 30.0  # FireKindLaws.SnuffDampS
SNUFF_AIM_M = 0.15  # FireKindLaws.SnuffAimM
SNUFF_AIM_RISE_M = 0.3  # FireKindLaws.SnuffAimRiseM
SNUFF_RISE_M = 0.5  # FireKindLaws.SnuffRiseM
MAX_FIRE_RADIUS_M = 1.5  # NatureBlock.MaxFireRadiusM
# ---- CampLaws (Kernel/CommandHostCamps.cs) ----
STARTING_ID = "camp:start"  # CampLaws.StartingId
FIREPIT_PREFIX = "firepit:"  # CampLaws.FirepitPrefix
STARTING_FIRE_AHEAD_M = 0.2  # CampLaws.StartingFireAheadM
REST_RADIUS_M = 0.3  # CampLaws.RestRadiusM
REST_RISE_M = 0.15  # CampLaws.RestRiseM
FLAMES_M = 0.05  # CampLaws.FlamesM
FLAMES_RISE_M = 0.08  # CampLaws.FlamesRiseM
SAME_CAMP_M = 2 * REST_RADIUS_M  # CampLaws.SameCampM
FIREPIT_GROUND_M = 0.1  # CampLaws.FirepitGroundM
FIREPIT_PROBE_M = 0.25  # CampLaws.FirepitProbeM
RAIN_OUT_S = 30.0  # CampLaws.RainOutS
FIREPIT_CATCH_M = 0.12  # CampLaws.FirepitCatchM
FIREPIT_CATCH_RISE_M = 0.3  # CampLaws.FirepitCatchRiseM
MAX_FIREPITS = 16  # CampLaws.MaxFirepits
# The materials wallet is retired (P-Inventory-2, decision 116: logs and stones are stacks in the pockets and the chest); a new
# island's is empty whatever the pack says (the kernel's NatureState.FreshWallet).
FRESH_WALLET: dict[str, int] = {}
# Fire's and Grow's expertise (Kernel/ExpertiseRecord.cs ExpertiseLists), in their orders.
FIRE_EXPERTISE = ("first_fire", "campfire", "fireline", "snuff", "will_o_wisp")
GROW_EXPERTISE = ("tall_plant", "thicket", "green_ash", "rain_garden", "waterpad")
# What the place made of a Fire cast (FireKindWords), as the preview's and the receipt's data.kind say it.
KIND_FIRE, KIND_FIREPIT, KIND_FIRELINE, KIND_SNUFF = "fire", "firepit", "fireline", "snuff"
# ---- Grow's unlocks (Kernel/CommandHostGrowKinds.cs GrowKindWords and GrowKindLaws) ----
KIND_SPROUTS, KIND_REPLANT, KIND_BRIDGE, KIND_IVY = "sprouts", "replant", "bridge", "ivy"
SEEDLESS_AT, BRIDGE_AT, IVY_AT = 2, 3, 5  # GrowKindLaws.SeedlessAt, BridgeAt, IvyAt: the team's Grow expertise count
SEEDLING_START = 0.5  # GrowKindLaws.SeedlingStart
SEEDLING_PHASES = 3  # GrowKindLaws.SeedlingPhases
SEEDLING_PHASE_H = 1.0  # GrowKindLaws.SeedlingPhaseH (game hours of daylight; the mock's game hour is a real hour)
# GrowKindLaws.DaylightFrom and DaylightUntil (decision 122): the island day's daylight as fractions of its day, 06:00 to 18:00; a
# seedling's phases count only those hours. The mock has no island clock record: its hour of day is its clock's, in UTC.
DAYLIGHT_FROM, DAYLIGHT_UNTIL = 0.25, 0.75
SPROUTS_PER_CAST = 12  # GrowKindLaws.SproutsPerCast
SPROUT_APART_M = 0.15  # GrowKindLaws.SproutApartM
SPROUT_LATTICE_M = 0.18  # GrowKindLaws.SproutLatticeM
SPROUT_JITTER_M = 0.01  # GrowKindLaws.SproutJitterM
SPROUT_SETS_MIXED = 3  # GrowKindLaws.SproutSetsMixed
SPROUT_SET_SEARCH_M = 32.0  # GrowKindLaws.SproutSetSearchM
MAX_SPROUTS = 1024  # NatureState.MaxSprouts
MAX_BRIDGES = 8  # NatureState.MaxBridges
BRIDGE_MIN_M = 0.4  # GrowKindLaws.BridgeMinM
BRIDGE_MAX_M = 1.2  # GrowKindLaws.BridgeMaxM
BRIDGE_WIDTH_M = 0.1  # GrowKindLaws.BridgeWidthM
BRIDGE_LEVEL_M = 0.02  # GrowKindLaws.BridgeLevelM
BRIDGE_LIP_M = 0.04  # GrowKindLaws.BridgeLipM
BRIDGE_VOID_DROP_M = 0.15  # GrowKindLaws.BridgeVoidDropM
BRIDGE_SAMPLE_M = 0.025  # GrowKindLaws.BridgeSampleM
BRIDGE_EDGE_M = 0.004  # GrowKindLaws.BridgeEdgeM
BRIDGE_CLEAR_M = 0.1  # GrowKindLaws.BridgeClearM

# ---- the kernel's words, word for word (test_kernel_alignment.py NatureMatchesTheKernel) ----
NO_NATURE_ABILITY = "This island does not grant that nature ability."
FIRE_NOT_LEARNED = "The Gubble has not learned Fire yet: rest by the starting campfire first."  # CommandHost.FireNotLearned
FIRELINE_NOT_LEARNED = "The Gubble has not learned Fireline yet: cast plain Fire instead."  # CommandHost.FirelineNotLearned
FIRE_OFF = "The player has turned off \"Let the Gubble use fire\"."  # CommandHost.FireOffMessage
FIRE_BUSY_OWN = "Your fire is still burning: one fire at a time. Wait for it to go out."
FIRE_BUSY_OTHER = "Let the current fire finish first."
GUBBLE_FIRE_BURNING = "The Gubble's fire is burning: one fire at a time. Let it burn out first."  # CommandHost.GubbleFireBurningWords
GUBBLE_FIRE_BURNING_SNUFF = ("The Gubble's fire is burning: one fire at a time. Let it burn out, or aim Fire at it to Snuff "
                             "it.")  # CommandHost.GubbleFireBurningSnuffWords
FIRE_WAIT = "Wait about {seconds} seconds after your last fire before lighting another."
CASTER_NOT_ALLOWED = "This island does not allow this caster to use that ability."
RADIUS_TOO_BIG = "That radius is more than this island allows."
DURATION_TOO_LONG = "That duration is more than this island allows."
CAST_OUTSIDE = "Choose a point inside this island."
CAST_TOO_FAR = "Bring the Gubble closer before casting."
CAST_UNSEEN = "The team must see the place the Gubble will affect."
TOO_MANY_TARGETS = "That fire could touch too many things. Choose a smaller area."
NO_DRY_FUEL = "There is no dry fuel at that spot."
NO_DRY_FUEL_LINE = "There is no dry fuel along that line."
FIRE_PREVIEW_AGAIN = "Preview this fire again before casting: the place, what it would reach or its permission has changed."
NOTHING_TAKES_HOLD = "Nothing can take hold at the center of that area."
FIRE_HOLD_BUILDS = ("{whose} would reach your builds. Is it OK if they burn? Approve: they burn and stay repairable, free in "
                    "Creative. Deny: the fire stops at them.")
FIRE_HOLD_KEYED_BUILDS = ("The Gubble asks to light a fire here, and it would reach your builds. Approve: it is lit and they may "
                          "burn, repairable free in Creative. Deny: no fire.")
FIRE_HOLD_KEYED = "The Gubble asks to light a fire here. Approve this exact area or deny it."
FIRELINE_TOO_LONG = "A Fireline is at most {most} m long."
FIRELINE_TOO_SHORT = "That line is too short: drag it out, or cast plain Fire."
FIRELINE_OUTSIDE = "Choose a line inside this island."
FIRELINE_TOO_FAR = "Bring the Gubble closer: the whole line must be within its reach."
FIRELINE_UNSEEN = "The team must see the whole line the Gubble will light."
NOTHING_BURNING = "Nothing is burning there now."
KIND_PREVIEW_AGAIN = "Preview this again before casting: the place or what is burning there has changed."
SNUFF_HOLD_KEYED = "The Gubble asks to put out the fire here with Snuff. Approve this exact area or deny it."
SNUFF_HOLD_CAMPFIRE = ("The Gubble asks to put out your campfire with Snuff. Approve: it goes out until the Gubble's Fire relights it. "
                       "Deny: it keeps burning.")
FIREPIT_HOLD = "The Gubble asks to light this firepit. Approve this exact firepit or deny it."
GROW_NO_PLANT = ("There is no plant to grow there: Grow makes the island's own plants bigger (grass, flowers, ferns, shrubs and "
                 "trees).")
GROW_FULL = "Every plant there is already full size."
GROW_TOO_MANY = "The island already keeps {most} grown plants, the most it can: none more can grow."
GROW_HOLD = "The Gubble asks to grow the plants in this area."
SPROUTS_LOCKED = "Growing plants from bare soil comes once the Gubble has learned two of Grow's expertise."  # CommandHost.SproutsLocked
REPLANT_LOCKED = "Replanting a burnt plant comes once the Gubble has learned two of Grow's expertise."  # CommandHost.ReplantLocked
BRIDGE_LOCKED = "A bridge of plants comes once the Gubble has learned three of Grow's expertise."  # CommandHost.BridgeLocked
IVY_LOCKED = "Climbing ivy comes once the Gubble has learned five of Grow's expertise."  # CommandHost.IvyLocked
GROW_STILL_GROWING = "The seedlings there are still growing: give them time."  # CommandHost.GrowStillGrowing
SPROUTS_NOTHING_TO_COPY = "No grass or flowers grow near enough for the sprouts to copy."  # CommandHost.SproutsNothingToCopy
SPROUTS_GROUND_FULL = "Sprouts already grow in every free spot there."  # CommandHost.SproutsGroundFull
SPROUTS_TOO_MANY = "The island already keeps {most} sprouts, the most it can."
SPROUTS_HOLD = "The Gubble asks to grow sprouts on the bare ground here."
REPLANT_STANDING = "Someone is standing where the plant would grow back: step away from it first."  # CommandHost.ReplantStandingWords
REPLANT_HOLD = "The Gubble asks to replant a burnt plant here."
BRIDGE_OUTSIDE = "Choose a bridge inside this island."
BRIDGE_TOO_FAR = "Bring the Gubble closer: both lips of the bridge must be within its reach."
BRIDGE_UNSEEN = "The team must see the whole gap the bridge will cross."
BRIDGE_TOO_MANY = "The island already keeps {most} bridges of plants, the most it can."
BRIDGE_TOO_LONG = "A bridge of plants is at most {most} m long."
BRIDGE_TOO_SHORT = "A bridge of plants is at least {least} m long: a narrower gap can be hopped."
BRIDGE_NO_LIP = "A bridge of plants grows from lip to lip: start and end the drag on solid ground at the gap's edges."  # CommandHost.BridgeNoLip
BRIDGE_NOT_LEVEL = "The two lips are not level: a bridge of plants grows level, its lips within 2 cm of each other."  # CommandHost.BridgeNotLevel
BRIDGE_NO_GAP = "There is no gap to cross there: a bridge of plants grows only over a drop or deep water."  # CommandHost.BridgeNoGap
BRIDGE_TWO_GAPS = "There is ground in the middle of that gap: a bridge of plants crosses one gap, from lip to lip."  # CommandHost.BridgeTwoGaps
BRIDGE_IN_THE_WAY = "Something is in the way of the bridge."  # CommandHost.BridgeInTheWay
BRIDGE_HERE = "A bridge of plants already crosses here."  # CommandHost.BridgeHere
BRIDGE_AVATAR = "Someone is in the gap where the bridge would grow: wait until it is clear."  # CommandHost.BridgeAvatar
BRIDGE_HOLD = "The Gubble asks to grow a bridge of plants across this gap."
REPAIR_NOT_HERE = "That is not in this room."
REPAIR_LOCKED = "Only the player can unlock this protected creation."
REPAIR_NOT_NEEDED = "That creation does not need repair."
# The kernel's RepairSpend and RepairAsk (P-Inventory-2): a repair spends logs and stones, the pockets first, then the chest.
REPAIR_NO_MATERIALS = "This repair needs {needs}, more than your pockets and the chest hold."
REPAIR_HOLD = "The Gubble asks to repair {target}, which you made."
UNDO_NOT_EARLIER = "Undo names an earlier room revision."
UNDO_SEALED = "That change happened in Challenge or Live, where what happens stays: it cannot be undone."
UNDO_ONLY_NATURE = ("The latest change is not one the island can undo yet: only nature changes (Grow, Fire, repairs) "
                    "can be undone.")
UNDO_NOT_REACHABLE = "That revision is not just before a nature change the island can still undo."
UNDO_LOCKED = "Unlock the protected thing before undoing what fire did to it."
UNDO_HOLD = "The Gubble asks to undo your latest nature change."
REMAINS_GONE = "That is not in this room."
REMAINS_SCORCHED = "This creation is scorched; repair it before using, changing, protecting or building on it."
# The rest key (CommandHost.RestNearFire ... RestInFlames) and what sitting down says.
REST_NEAR_FIRE = "Sit down by a lit campfire to rest: come closer to the fire."
REST_UNLIT = "This firepit is not lit yet: ask the Gubble to cast Fire on it."
REST_OUT = "This campfire is out: ask the Gubble to relight it."
REST_IN_FLAMES = "You are standing in the fire: step out of the flames to sit down beside it."
REST_SITS = "You sit down by the campfire and rest."
REST_NEW_CAMP = "You sit down by the campfire and rest. This is your camp now: you will wake here."
# The consequence facts' lines (CommandHostConsequences).
BUILD_BURNT_START = "The Gubble's fire burned "
POP_FALL_PREFIX = "The Gubble popped your bubble and you fell"  # CommandHost.PopFallPrefix

# The founder's fire rule (CommandHost.FireRule): what fire does to a kind of thing in a mode, and whether the player is asked first.
FIRE_RULE = {
    ("locked", "creative"): ("fireproof", False), ("locked", "challenge"): ("fireproof", False), ("locked", "live"): ("fireproof", False),
    ("creation", "creative"): ("scorches", True), ("creation", "challenge"): ("scorches", False), ("creation", "live"): ("scorches", False),
    ("stock", "creative"): ("burns_down", False), ("stock", "challenge"): ("burns_down", False), ("stock", "live"): ("burns_down", False),
    ("plant", "creative"): ("burns_down", False), ("plant", "challenge"): ("burns_down", False), ("plant", "live"): ("burns_down", False),
}


def weather_at(weather_s: float) -> tuple[float, list[float], str]:
    """The kernel's NatureState.WeatherAt: (rain 0 to 1, the wind in m/s, the pattern) at a point of the weather clock."""
    t = weather_s % WEATHER_CYCLE_S
    fall_from = WEATHER_WET_UNTIL_S + WEATHER_RAMP_S
    rain = (0.0 if t < WEATHER_DRY_S else (t - WEATHER_DRY_S) / WEATHER_RAMP_S if t < WEATHER_DRY_S + WEATHER_RAMP_S
            else 1.0 if t < WEATHER_WET_UNTIL_S else (fall_from - t) / WEATHER_RAMP_S if t < fall_from else 0.0)
    direction = weather_s / WIND_TURN_S
    speed = WIND_CALM_MPS + WIND_RAIN_MPS * rain
    wind = [math.cos(direction) * speed, 0.0, math.sin(direction) * speed]
    pattern = "rain" if rain > WEATHER_RAIN01 else "showers" if rain > WEATHER_SHOWERS01 else "fair"
    return rain, wind, pattern


def daylight_hours(from_hour: float, span_h: float) -> float:
    """The hours of daylight in a span of span_h hours from an hour of the day (GrowKindLaws.DaylightHours, decision 122): only those
    from DAYLIGHT_FROM to DAYLIGHT_UNTIL of each day count, so a whole day counts 12 and a night none."""
    day, light = 24.0, (DAYLIGHT_UNTIL - DAYLIGHT_FROM) * 24.0

    def since(hours: float) -> float:
        days = math.floor(hours / day)
        return days * light + min(max(hours - days * day - DAYLIGHT_FROM * day, 0.0), light)

    start = from_hour - math.floor(from_hour / day) * day
    return max(0.0, since(start + max(0.0, span_h)) - since(start))


def _r(value: float) -> float:
    rounded = round(float(value), 4)
    return 0.0 if rounded == 0 else rounded


def _flat(a, b) -> float:
    return math.hypot(a[0] - b[0], a[2] - b[2])


def _grid(point) -> tuple[int, int]:
    return math.floor(point[0] / CELL_M), math.floor(point[2] / CELL_M)


def _key(x: int, z: int) -> str:
    return f"{x}:{z}"


def disk(center, radius: float):
    """The grid cells whose centres lie within radius of center, horizontally (NatureState.Disk), in the kernel's order."""
    cx, cz = _grid(center)
    span = math.ceil(radius / CELL_M) + 1
    for x in range(cx - span, cx + span + 1):
        for z in range(cz - span, cz + span + 1):
            if ((x + 0.5) * CELL_M - center[0]) ** 2 + ((z + 0.5) * CELL_M - center[2]) ** 2 <= radius * radius:
                yield x, z


def line_cells(start, end) -> list[tuple[int, int]]:
    """The ground cells along a line, their centres within FIRELINE_HALF_WIDTH_M of it, from its start (CommandHost.LineCells)."""
    ax, az, bx, bz = start[0], start[2], end[0], end[2]
    ux, uz = bx - ax, bz - az
    length2 = max(ux * ux + uz * uz, 1e-8)
    half = FIRELINE_HALF_WIDTH_M
    x0, z0 = _grid([min(ax, bx) - half, 0, min(az, bz) - half])
    x1, z1 = _grid([max(ax, bx) + half, 0, max(az, bz) + half])
    found = []
    for x in range(x0, x1 + 1):
        for z in range(z0, z1 + 1):
            cx, cz = (x + 0.5) * CELL_M, (z + 0.5) * CELL_M
            t = min(1.0, max(0.0, ((cx - ax) * ux + (cz - az) * uz) / length2))
            if math.hypot(ax + ux * t - cx, az + uz * t - cz) <= half:
                found.append((t, x, z))
    return [(x, z) for _t, x, z in sorted(found)]


def along_one_line(p, q, r) -> bool:
    """Three centres at least FIRELINE_LEARN_APART_M apart, each within FIRELINE_LEARN_OFF_LINE_M of the line through the two
    farthest apart (CommandHost.AlongOneLine)."""
    points = [(p[0], p[2]), (q[0], q[2]), (r[0], r[2])]
    best, chosen = -1.0, (0, 1, 2)
    for a, b, c in ((0, 1, 2), (0, 2, 1), (1, 2, 0)):
        apart = math.dist(points[a], points[b])
        if apart < FIRELINE_LEARN_APART_M:
            return False
        if apart > best:
            best, chosen = apart, (a, b, c)
    i, j, k = chosen
    length = math.dist(points[i], points[j])
    along = ((points[j][0] - points[i][0]) / length, (points[j][1] - points[i][1]) / length)
    offset = (points[k][0] - points[i][0], points[k][1] - points[i][1])
    return abs(offset[0] * along[1] - offset[1] * along[0]) <= FIRELINE_LEARN_OFF_LINE_M


# ------------------------------------------------------------------ the state

@dataclass
class Plant:
    """One of the island's plant copies, as the room's table gives it (contracts/README.md "Plant copies"): its root, its natural
    height and its fuel class."""

    id: str
    position: list[float]
    height_m: float
    fuel_class: str = "grass"
    kind: str = "plant"  # plant, tree or stone
    submerged: bool = False
    seeds: bool = True  # its set sets seed: a burnt one is replanted (contracts/README.md "Plant copies"); never a stone

    def grows(self) -> bool:  # PlantLaws.Grows
        return self.kind != "stone"

    def burns(self) -> bool:  # PlantLaws.Burns
        return self.kind != "stone" and PLANT_FUEL.get(self.fuel_class, 0) > 0 and not self.submerged

    def cap(self) -> float:  # PlantLaws.Cap
        return CAP_FACTOR if self.height_m <= 1e-4 else min(CAP_FACTOR, 1 + CAP_EXTRA_M / self.height_m)

    def bounds(self, factor: float) -> dict:
        """Its drawn bounds at a factor of its natural size, scaled about its root (the mock's stand-in for the mesh's: a quarter as
        wide either way as it is tall)."""
        x, y, z = self.position
        tall = self.height_m * factor
        half = max(0.02, tall / 4)
        return {"min_m": [x - half, y, z - half], "max_m": [x + half, y + tall, z + half]}


@dataclass
class PlantState:
    """What Grow and fire did to one plant (the kernel's NaturePlant): its factor of its natural size and how far it burned."""

    factor: float = 1.0
    burn: float = 0.0
    sown: float | None = None  # a replanted seedling: when it was sown, on the host's clock (decision 105); its factor is its phase

    @property
    def burnt(self) -> bool:
        return self.burn >= 1

    @property
    def seedling(self) -> bool:
        return self.sown is not None and not self.burnt and self.factor < 1

    @property
    def grown(self) -> bool:
        return not self.burnt and self.factor > 1


@dataclass
class Sprout:
    """A sprout (the kernel's NatureSprout, decision 104): the plant it copies, its root, when it was sown, its burn and its phase."""

    template: str
    position: list[float]
    sown: float
    burn: float = 0.0
    factor: float = SEEDLING_START


@dataclass
class Bridge:
    """A bridge of plants (the kernel's NatureBridge, decision 106): its lips at the deck's height and the void's span along it."""

    id: str
    start: list[float]
    end: list[float]
    span: tuple[float, float]
    burn: float = 0.0


@dataclass
class BurnRecord:
    """What fire did to one object or creation (the kernel's NatureObject): a creation's scorch and its repair quote; a stock
    object at 1 has burned down (gone from the room, a removed entity)."""

    creation: bool
    burn: float = 0.0
    repair_cost: dict[str, int] = field(default_factory=dict)

    @property
    def burned_down(self) -> bool:
        return not self.creation and self.burn >= 1


@dataclass
class Fire:
    """The one fire (the kernel's NatureFire): who lit it, at whose direction, its circle, its end on the weather clock, the
    builds it may scorch (each at its revision), and the cells that took (its burning ground)."""

    principal: str
    directed_by: str
    center: list[float]
    radius: float
    intensity: float
    revision: int
    ends_at: float
    authorized: dict[str, int]
    burning: dict[str, tuple[list[float], str]]  # its burning ground: cell key -> (the cell's centre, the surface it lies on)
    paid: bool = False


@dataclass
class UndoStep:
    """One durable nature change as room.undo takes it back (the kernel's NatureUndoStep): everything as it was before it."""

    principal: str
    revision: int
    base: int
    label: str
    mode: str
    objects: dict = field(default_factory=dict)  # id -> BurnRecord or None
    plants: dict = field(default_factory=dict)  # id -> PlantState or None
    ash: dict = field(default_factory=dict)  # cell key -> (y, ash) or None
    wallet: dict = field(default_factory=dict)
    sprouts: list | None = None  # the sprouts before the step changed them (None: it did not)
    bridges: list | None = None

    @property
    def undoable(self) -> bool:
        return self.mode == "creative"


@dataclass
class Target:
    """One thing a fire could reach (the kernel's FireTarget), what the rule does to it and whether it asks first."""

    id: str
    kind: str
    outcome: str
    ask_first: bool
    bounds: dict


@dataclass
class CampSite:
    id: str
    position: list[float]
    starting: bool


class NatureMixin:
    """Mixed into MockHost. Nature is room state (reset with the room); the team's expertise and the player's setting are not."""

    def _init_nature_team(self) -> None:
        """What belongs to the team, not the room: the expertise nature teaches, and the player's own setting."""
        self.nature_expertise: dict[str, set[str]] = {"fire": set(), "grow": set()}
        # The player's setting "Let the Gubble use fire" (the profile's gubble_fire, on unless the player turns it off): only
        # set_gubble_fire, the player's control, changes it; no request reaches it.
        self.gubble_fire = True

    def _reset_nature(self) -> None:
        self._nature_epoch = self.clock.now()  # the weather clock starts with the room
        self._nature_t = 0.0  # the weather clock the simulation has reached
        self.plants: dict[str, Plant] = {}
        self.plant_state: dict[str, PlantState] = {}
        self.sprouts: list[Sprout] = []
        self.bridges: list[Bridge] = []
        self.burn_records: dict[str, BurnRecord] = {}
        self.ash: dict[str, tuple[float, float]] = {}  # cell key -> (its ground's height, ash)
        # What the kernel's pockets and chest hold of logs and stones, by material (the mock keeps no player inventory: empty unless a
        # test fills it); what a repair spends, and nature.describe's "wallet" (the name kept for its readers).
        self.wallet = self._fresh_wallet()
        self.fire: Fire | None = None
        self.undo_steps: list[UndoStep] = []
        self.undone_at, self.undone_to = -1, -1
        self.nature_changed_at = 0
        self._nature_previews: dict[str, tuple[str, tuple, int, float]] = {}
        self._snuff_zones: list[tuple[list[float], float, float]] = []
        self._recent_fires: list[tuple[float, list[float], int]] = []
        self._harmful_fires: set[int] = set()
        self._companion_fire_seen = -math.inf
        self._sparing = False
        self._build_fire = -1
        self._build_entry: dict | None = None
        self._builds_burnt: list[str] = []
        self._pop_fall_pending = False
        self._camps: list[CampSite] | None = None
        self._lit: set[str] = set()
        self._out: set[str] = set()
        self._camp_rain: dict[str, float] = {}
        self.home_camp: str | None = None

    def _fresh_wallet(self) -> dict[str, int]:
        """A new island's materials: none (the wallet is retired; traits.wallet.start is read and ignored, as the kernel's FreshWallet)."""
        return dict(FRESH_WALLET)

    def _repair_needs(self, cost: dict[str, int]) -> str:
        """The kernel's RepairSpend words for a cost: each material as the pack's kind names it ("3 logs and 1 stone"), in the pack's
        order of its kinds, then any material no kind is made of."""
        kinds = list((self.rules.inventory or {}).get("kinds", [])) if self.rules is not None else []
        by_material = {kind["material"]: (index, kind) for index, kind in enumerate(kinds) if "material" in kind}
        parts = []
        for material, units in sorted(cost.items(), key=lambda item: (by_material.get(item[0], (10**9, None))[0], item[0])):
            if units <= 0:
                continue
            kind = by_material.get(material, (0, None))[1]
            word = (kind["name"] if units == 1 else kind.get("plural", kind["name"])).lower() if kind is not None else material
            parts.append(f"{units} {word}")
        return "nothing" if not parts else parts[0] if len(parts) == 1 else ", ".join(parts[:-1]) + " and " + parts[-1]

    def _repair_spent(self, cost: dict[str, int]) -> dict[str, int]:
        """The kernel's RepairBill.Spent as data.spent (decision 131): each material's units by the pack's kind whose material it is."""
        kinds = list((self.rules.inventory or {}).get("kinds", [])) if self.rules is not None else []
        by_material = {kind["material"]: kind["id"] for kind in kinds if "material" in kind}
        return {by_material[material]: units for material, units in sorted(cost.items()) if units > 0 and material in by_material}

    def _nature_untouched(self) -> bool:
        return (not self.burn_records and not self.plant_state and not self.sprouts and not self.bridges and self.fire is None and not self.undo_steps and self.undone_at < 0
                and self.nature_changed_at == 0)

    # ------------------------------------------------------------------ the team's expertise

    @property
    def fire_learned(self) -> bool:
        """Decision 72: whether the team has learned Fire (the kernel's first rest at the starting campfire)."""
        return "first_fire" in self.nature_expertise["fire"]

    @fire_learned.setter
    def fire_learned(self, learned: bool) -> None:
        if learned:
            self.nature_expertise["fire"].add("first_fire")
        else:
            self.nature_expertise["fire"].discard("first_fire")

    def learn_fire(self) -> None:
        """The team learns Fire, as the kernel's first rest at the starting campfire teaches it (its LearnFireForTests): the host's
        own, never a request. player_rests is the rest itself."""
        with self._lock:
            self.fire_learned = True

    def _learned(self, ability: str, expertise: str) -> bool:
        return expertise in self.nature_expertise[ability]

    def _learn(self, ability: str, found) -> None:
        """What a committed cast or the host's own observation showed, learned once per team (the kernel's RecordExpertise)."""
        self.nature_expertise[ability].update(found)

    @property
    def grow_count(self) -> int:
        """Decision 76's count: the team's Grow expertise learned, which opens each of Grow's unlocks."""
        return len(self.nature_expertise["grow"])

    def grow_locked_kinds(self) -> list[str]:
        """The unlocks the team has not opened yet, in order (the kernel's GrowLockedKinds): capabilities.list's locked_kinds of Grow."""
        count = self.grow_count
        return ([KIND_SPROUTS, KIND_REPLANT] if count < SEEDLESS_AT else []) + ([KIND_BRIDGE] if count < BRIDGE_AT else []) + \
            ([KIND_IVY] if count < IVY_AT else [])

    def learn_grow(self, count: int) -> None:
        """The team knows the first count of Grow's expertise (the kernel's LearnGrowForTests): the host's own, never a request."""
        with self._lock:
            self._learn("grow", GROW_EXPERTISE[:count])

    def set_gubble_fire(self, allowed: bool) -> None:
        """The player's control for "Let the Gubble use fire" (the HUD's setting). No request reaches it."""
        with self._lock:
            self.gubble_fire = bool(allowed)

    # ------------------------------------------------------------------ the game side: plants, rain, the rest key, the fall

    def add_plant(self, plant_id: str, position: list[float], height_m: float, fuel_class: str = "grass", kind: str = "plant",
                  submerged: bool = False, seeds: bool | None = None) -> str:
        """One of the island's plant copies, as the room's table would give it (the box world reads no plant tables): its id
        (plant:<set>-<row>), its root, its natural height and its fuel class (grass, herb, shrub, broadleaf, conifer; none for a
        stone)."""
        with self._lock:
            if not _PLANT_ID.fullmatch(plant_id):
                raise ValueError("a plant copy's id is plant:<set>-<row>")
            self.plants[plant_id] = Plant(plant_id, [float(v) for v in position], float(height_m), fuel_class, kind, submerged,
                                          kind != "stone" if seeds is None else bool(seeds))
            return plant_id

    def plant_factor(self, plant_id: str) -> float:
        """A plant's size as a factor of its natural size now (0 once it burned down): what the game would draw."""
        with self._lock:
            self._refresh_seedlings()
            state = self.plant_state.get(plant_id)
            return 1.0 if state is None else 0.0 if state.burnt else state.factor

    def weather_now(self) -> tuple[float, list[float], str]:
        with self._lock:
            return weather_at(self._weather_s())

    def campfire_lit(self, camp_id: str) -> bool:
        with self._lock:
            self._advance_nature()
            return self._is_lit(camp_id)

    def player_rests(self) -> dict:
        """The rest key (the kernel's PlayerRest): the player sits down by a lit campfire within 0.3 m, out of its flames. The first
        rest by the starting campfire teaches Fire (decision 72), once per team; the campfire becomes the camp they wake at. The
        trusted HUD's call, never a command on the companion's surface."""
        with self._lock:
            self._advance_nature()
            feet = self.entities[self.avatars[_PLAYER]].position
            lit = self._nearest_campfire(feet, REST_RADIUS_M, lit_only=True)
            if lit is None:
                dark = self._nearest_campfire(feet, REST_RADIUS_M, lit_only=False)
                words = (REST_OUT if dark.id in self._out else REST_UNLIT) if dark is not None else REST_NEAR_FIRE
                return {"ok": False, "words": words, "campfire": dark.id if dark is not None else None}
            if self._campfire_flames_at(feet) is not None:
                return {"ok": False, "words": REST_IN_FLAMES, "campfire": lit.id}
            learned = lit.starting and not self.fire_learned
            if learned:
                self.fire_learned = True
            new_camp = self.home_camp != lit.id
            self.home_camp = lit.id
            return {"ok": True, "words": REST_SITS if lit.starting else REST_NEW_CAMP, "campfire": lit.id,
                    "learned_fire": learned, "new_camp": new_camp}

    def player_lands(self, hurt: bool, recovered: bool = False) -> bool:
        """The body's landing after a fall (the kernel's ObservePlayerFall; the mock has no body, so the game side says whether the
        landing hurt). The landing that ends the fall after the Gubble popped the player's bubble by its own action, when it hurt, is
        one consequence fact in the kernel's words that costs Trust (decision 96); in Creative nothing hurts. Returns whether a fact
        was written."""
        with self._lock:
            pending, self._pop_fall_pending = self._pop_fall_pending, False
            if not pending or not hurt or self.mode == "creative":
                return False
            at = self.entities[self.avatars[_PLAYER]].position
            place = self._nearest_known_place(at)
            line = POP_FALL_PREFIX + ("" if place is None else " near " + self._quoted(place)) + \
                (" until you had to recover." if recovered else ".")
            fact = self._fact("consequence", line, _COMPANION, _COMPANION, None, None, list(at))
            self._add_history(fact)
            # Decision 134: the Gubble hears it on its next result, with the Trust it moved.
            self._tell_gubble(fact, self._move_trust_for(True))
            return True

    def _note_pop(self, held_player: bool, by_gubble: bool) -> None:
        """A bubble ended: one that held the player and that the Gubble popped by its own action leaves the fall pending (the kernel's
        NotePopFallPending); any other end of a carry forgets it."""
        if held_player:
            self._pop_fall_pending = by_gubble

    # ------------------------------------------------------------------ the clock

    def _weather_s(self) -> float:
        return max(0.0, self.clock.now() - self._nature_epoch)

    def _rain(self, at: float | None = None) -> float:
        return weather_at(self._weather_s() if at is None else at)[0]

    def _advance_nature(self) -> None:
        """The simulation, in whole STEP_S steps up to the host's clock (the kernel's AdvanceNature): the fire burns what it reaches
        and ends at its time, rain on the Gubble's fire teaches Snuff, rain puts the campfires out, a fire burning outside Creative
        seals its undo step, and old snuffs dry. Time past an hour is dropped, as the kernel drops what it cannot catch up."""
        now = self._weather_s()
        if now - self._nature_t > 3600:
            self._nature_t = now - 3600
        while self._nature_t + STEP_S <= now:
            self._nature_t += STEP_S
            self._nature_step(self._nature_t)
        self._snuff_zones = [z for z in self._snuff_zones if z[2] > now]

    def _nature_step(self, t: float) -> None:
        rain = self._rain(t)
        fire = self.fire
        if fire is not None:
            if self.mode != "creative":
                step = self._step_at(fire.revision)
                if step is not None and step.undoable:
                    step.mode = self.mode
            self._burn(fire, STEP_S, rain)
            if fire.principal == _COMPANION:
                self._companion_fire_seen = t
                if rain > WEATHER_RAIN01 and fire.revision not in self._harmful_fires:
                    self._learn("fire", ["snuff"])  # decision 79: rain on a fire the Gubble started teaches Snuff
            if t >= fire.ends_at:
                self.fire = None
        if rain > WEATHER_RAIN01:
            for camp in self._ensure_camps():
                if not self._is_lit(camp.id) or (camp.starting and not self.fire_learned):
                    continue
                wet = self._camp_rain.get(camp.id, 0.0) + STEP_S
                self._camp_rain[camp.id] = wet
                if wet >= RAIN_OUT_S:
                    self._put_out_campfire(camp.id)
        elif self._camp_rain:
            self._camp_rain.clear()

    def _burning_near(self, point, within: float, below: float, above: float) -> bool:
        """Whether the fire's ground burns within `within` of a point horizontally, from `below` under it to `above` over it."""
        fire = self.fire
        if fire is None:
            return False
        x0, z0 = _grid([point[0] - within, 0, point[2] - within])
        x1, z1 = _grid([point[0] + within, 0, point[2] + within])
        for x in range(x0, x1 + 1):
            for z in range(z0, z1 + 1):
                cell = fire.burning.get(_key(x, z))
                if cell is not None and _flat(cell[0], point) <= within and point[1] - below <= cell[0][1] <= point[1] + above:
                    return True
        return False

    def _catches(self, box: dict) -> bool:
        """Whether burning ground lies in bounds grown by OBJECT_CATCH_M, its ground a little raised (the kernel's Catches)."""
        fire = self.fire
        low = [box["min_m"][i] - OBJECT_CATCH_M for i in range(3)]
        high = [box["max_m"][i] + OBJECT_CATCH_M for i in range(3)]
        (x0, z0), (x1, z1) = _grid(low), _grid(high)
        for x in range(x0, x1 + 1):
            for z in range(z0, z1 + 1):
                cell = fire.burning.get(_key(x, z)) if fire is not None else None
                if cell is not None:
                    c = cell[0]
                    if low[0] <= c[0] <= high[0] and low[2] <= c[2] <= high[2] and low[1] <= c[1] + 0.04 <= high[1]:
                        return True
        return False

    def _burn(self, fire: Fire, seconds: float, rain: float) -> None:
        """One step of a fire on what it reaches (the kernel's BurnNatureObjects and BurnPlants), recorded in its undo step."""
        step = self._step_at(fire.revision)
        rate = seconds * OBJECT_BURN_PER_INTENSITY_S * fire.intensity * (1 - rain)
        if rate <= 0:
            return
        for target in self._fire_targets(fire.center, fire.radius):
            if target.outcome == "fireproof":
                continue
            entity = self.entities[target.id]
            if target.outcome == "scorches" and fire.authorized.get(target.id) != entity.revision:
                continue
            if not self._catches(target.bounds):
                continue
            self._remember_object(step, target.id)
            record = self.burn_records.get(target.id)
            if record is None:
                record = self.burn_records[target.id] = BurnRecord(creation=target.kind == "creation",
                                                                   repair_cost=self._repair_quote(entity) if target.kind == "creation" else {})
            record.burn = min(1.0, record.burn + rate)
            if record.creation and record.burn > 0:
                self._note_build_burnt(fire, target.id)
                self._harmful_fires.add(fire.revision)
            if record.burned_down:
                entity.removed = True
                if entity.held_by:
                    self.holding.pop(entity.held_by, None)
                    entity.held_by = None
        room = MAX_BURNT_PLANTS - sum(1 for s in self.plant_state.values() if s.burn > 0)
        for plant in self._fire_plants(fire.center, fire.radius):
            state = self.plant_state.get(plant.id)
            if state is not None and state.burnt:
                continue
            if not self._catches(plant.bounds(state.factor if state is not None else 1.0)):
                continue
            if (state is None or state.burn <= 0) and room <= 0:
                continue
            if state is None or state.burn <= 0:
                room -= 1
            self._remember_plant(step, plant.id)
            state = self.plant_state.setdefault(plant.id, PlantState())
            state.burn = min(1.0, state.burn + rate / (1 + GROWN_FUEL_FACTOR * max(0.0, state.factor - 1)))
            if state.burnt:
                state.sown, state.factor = None, 1.0  # a replanted seedling burnt down is a burnt plant like any other
                self._leave_ash(step, plant.position)
        # Grow's unlocks burn as plants (decisions 104 and 106): a sprout gone at 1, leaving ash; a bridge caught at a lip.
        for sprout in list(self.sprouts):
            template = self.plants.get(sprout.template)
            if template is None or not self._catches({"min_m": [sprout.position[0] - 0.02, sprout.position[1], sprout.position[2] - 0.02],
                                                        "max_m": [sprout.position[0] + 0.02, sprout.position[1] + template.height_m * sprout.factor,
                                                                  sprout.position[2] + 0.02]}):
                continue
            self._remember_sprouts(step)
            sprout.burn = min(1.0, sprout.burn + rate)
            if sprout.burn >= 1:
                self.sprouts.remove(sprout)
                self._leave_ash(step, sprout.position)
        for bridge in list(self.bridges):
            if not self._catches(self._deck_box(bridge.start, bridge.end, -0.03, 0.03)):
                continue
            self._remember_bridges(step)
            bridge.burn = min(1.0, bridge.burn + rate)
            if bridge.burn >= 1:
                self.bridges.remove(bridge)

    # ------------------------------------------------------------------ the campfires

    def _ensure_camps(self) -> list[CampSite]:
        """The starting campfire first, then the manifest's firepits that stand on the floor (the kernel's EnsureCamps)."""
        if self._camps is not None:
            return self._camps
        spawn = next((s for s in self.room["spawns"] if s["role"] == "player"), {"position_m": [0.0, 0.0, 0.0], "yaw_deg": 0.0})
        yaw = math.radians(float(spawn.get("yaw_deg", 0.0)))
        at = [spawn["position_m"][0] - math.sin(yaw) * STARTING_FIRE_AHEAD_M, spawn["position_m"][1],
              spawn["position_m"][2] - math.cos(yaw) * STARTING_FIRE_AHEAD_M]
        ground = self._floor_below(at, at[1] + 0.2, 0.5)
        if ground is not None:
            at[1] = ground
        start = CampSite(STARTING_ID, [_r(v) for v in at], True)
        camps = [start]
        block = self.room.get("extensions", {}).get("x_firepits")
        items = block.get("items") if isinstance(block, dict) and set(block) == {"items"} else None
        if isinstance(items, list) and len(items) <= MAX_FIREPITS:
            bounds = self.room_bounds
            for number, item in enumerate(items, start=1):
                spot = item.get("position_m") if isinstance(item, dict) and set(item) == {"position_m"} else None
                if not (isinstance(spot, list) and len(spot) == 3 and all(isinstance(v, (int, float)) and math.isfinite(v) for v in spot)):
                    continue
                if not all(bounds["min_m"][i] - 0.001 <= spot[i] <= bounds["max_m"][i] + 0.001 for i in range(3)):
                    continue
                ground = self._floor_below(spot, spot[1] + FIREPIT_PROBE_M, FIREPIT_PROBE_M + FIREPIT_GROUND_M)
                if ground is None or abs(ground - spot[1]) > FIREPIT_GROUND_M:
                    continue
                here = [spot[0], ground, spot[2]]
                if any(_flat(here, c.position) < SAME_CAMP_M for c in camps):
                    continue
                camps.append(CampSite(FIREPIT_PREFIX + str(number), [_r(v) for v in here], False))
        self._camps = camps
        return camps

    @property
    def camp_sites(self) -> list[CampSite]:
        with self._lock:
            return list(self._ensure_camps())

    def _is_lit(self, camp_id: str) -> bool:
        return camp_id not in self._out if camp_id == STARTING_ID else camp_id in self._lit

    def _put_out_campfire(self, camp_id: str) -> bool:
        if not self._is_lit(camp_id):
            return False
        self._lit.discard(camp_id)
        self._out.add(camp_id)
        self._camp_rain.pop(camp_id, None)
        return True

    def _nearest_campfire(self, at, within: float, lit_only: bool) -> CampSite | None:
        best, nearest = None, math.inf
        for camp in self._ensure_camps():
            if lit_only and not self._is_lit(camp.id):
                continue
            flat = _flat(at, camp.position)
            if flat > within or abs(at[1] - camp.position[1]) > REST_RISE_M or flat >= nearest:
                continue
            best, nearest = camp, flat
        return best

    def _campfire_flames_at(self, feet) -> CampSite | None:
        for camp in self._ensure_camps():
            rise = feet[1] - camp.position[1]
            if _flat(feet, camp.position) <= FLAMES_M and -REST_RISE_M <= rise <= FLAMES_RISE_M and self._is_lit(camp.id):
                return camp
        return None

    def _unlit_firepit_under(self, center, radius: float) -> CampSite | None:
        """The unlit firepit or put-out campfire a Fire cast's circle reaches, the nearest (the kernel's UnlitFirepitUnder)."""
        best, nearest = None, math.inf
        for camp in self._ensure_camps():
            if self._is_lit(camp.id):
                continue
            flat = _flat(center, camp.position)
            if flat > radius + FIREPIT_CATCH_M or abs(center[1] - camp.position[1]) > FIREPIT_CATCH_RISE_M or flat >= nearest:
                continue
            best, nearest = camp, flat
        return best

    # ------------------------------------------------------------------ the box world's ground

    def _floor_below(self, at, top: float, depth: float) -> float | None:
        """The floor (a walkable shell part) under a point, from `top` down `depth` (the kernel's GroundBelow and StandableGround,
        which never stand on a thing): its height, or None."""
        best = None
        for entity in self.entities.values():
            if entity.kind != "shell" or "walkable_top" not in entity.affordances or entity.removed:
                continue
            box = entity.bounds()
            if box["min_m"][0] <= at[0] <= box["max_m"][0] and box["min_m"][2] <= at[2] <= box["max_m"][2]:
                y = box["max_m"][1]
                if top - depth <= y <= top and (best is None or y > best):
                    best = y
        return best

    def _ground_cell(self, x: int, z: int, near_y: float, shelter: list[dict], plant_fuel: dict[str, float]):
        """A cell's ground (the kernel's SampleNatureCell): the first walkable top under the probe (the floor, or a thing whose
        footprint holds the cell), its material's fuel and the plants rooted in it; None for no fuel, sheltered or damp ground.
        Returns (centre, surface, fuel)."""
        cx, cz = (x + 0.5) * CELL_M, (z + 0.5) * CELL_M
        bounds = self.room_bounds
        if not (bounds["min_m"][0] <= cx <= bounds["max_m"][0] and bounds["min_m"][2] <= cz <= bounds["max_m"][2]):
            return None
        top = min(bounds["max_m"][1] - 0.002, near_y + 0.1)
        best = None  # (y, surface, material)
        for entity in self.entities.values():
            if entity.removed or entity.kind == "avatar" or entity.held_by:
                continue
            if entity.kind == "shell" and "walkable_top" not in entity.affordances:
                continue
            box = entity.bounds()
            if not (box["min_m"][0] <= cx <= box["max_m"][0] and box["min_m"][2] <= cz <= box["max_m"][2]):
                continue
            y = box["max_m"][1]
            if y <= top and (best is None or y > best[0]):
                best = (y, entity.id, self._surface_material(entity))
        if best is None:
            return None
        y, surface, material = best
        fuel = (GROUND_GRASS_FUEL if material in ("grass", "foliage") else GROUND_WOOD_FUEL if material in ("wood", "bark")
                else GROUND_LIGHT_FUEL if material in ("fabric", "paper", "plastic", "cloth", "rubber", "leaf") else 0.0)
        fuel = max(fuel, plant_fuel.get(_key(x, z), 0.0))
        centre = [cx, y, cz]
        lifted = [cx, y + 0.02, cz]
        if any(all(b["min_m"][i] - SHELTER_MARGIN_M <= lifted[i] <= b["max_m"][i] + SHELTER_MARGIN_M for i in range(3)) for b in shelter):
            return None
        if self._in_snuff_zone(centre):
            return None
        moisture = FRESH_MOISTURE + self._rain() * RAIN_MOISTURE
        if fuel <= 0 or moisture >= IGNITION_MAX_MOISTURE:
            return None
        return centre, surface, fuel

    @staticmethod
    def _surface_material(entity) -> str:
        """What a cell lies on is made of (the kernel's NatureSurface): a shell part's material, a thing's first burning material
        (else its first), a build's first wood or leaf part (else stone)."""
        materials = list(entity.materials)
        if entity.kind == "creation":
            return next((m for m in materials if m in ("wood", "leaf")), "stone")
        return next((m for m in materials if m in FLAMMABLE_MATERIALS), materials[0] if materials else "")

    def _in_snuff_zone(self, at) -> bool:
        now = self._weather_s()
        return any(until > now and _flat(at, centre) <= radius and abs(at[1] - centre[1]) <= SNUFF_RISE_M
                   for centre, radius, until in self._snuff_zones)

    def _shelter(self, authorized: dict[str, int]) -> list[dict]:
        """The bounds whose ground does not burn (the kernel's RefreshNatureShelter): what the rule makes fireproof, and every
        creation the fire may not scorch."""
        boxes = []
        for entity in self.entities.values():
            if entity.removed or entity.kind not in ("object", "creation"):
                continue
            if entity.protected or (entity.kind == "creation" and authorized.get(entity.id) != entity.revision):
                boxes.append(entity.bounds())
        return boxes

    def _plant_fuel(self, center, radius: float) -> dict[str, float]:
        fuel: dict[str, float] = {}
        for plant in self._fire_plants(center, radius):
            state = self.plant_state.get(plant.id)
            factor = state.factor if state is not None else 1.0
            key = _key(*_grid(plant.position))
            fuel[key] = max(fuel.get(key, 0.0), min(1.0, PLANT_FUEL.get(plant.fuel_class, 0) * (1 + GROWN_FUEL_FACTOR * max(0.0, factor - 1))))
        return fuel

    def _fire_plants(self, center, radius: float) -> list[Plant]:
        """The plants a fire in this circle can reach: rooted within it and FIRE_REACH_MARGIN_M, burnable, not burnt down."""
        reach = radius + FIRE_REACH_MARGIN_M
        return [p for p in self.plants.values()
                if p.burns() and _flat(p.position, center) <= reach and abs(p.position[1] - center[1]) <= reach
                and not (p.id in self.plant_state and self.plant_state[p.id].burnt)]

    def _fire_targets(self, center, radius: float) -> list[Target]:
        """Every object and creation a fire at center could reach, whoever asks (the kernel's FireTargets): flammable, not held,
        its bounds grown by the radius holding the centre, with the rule's outcome for it now."""
        found = []
        for entity in self.entities.values():
            if entity.removed or entity.kind not in ("object", "creation") or entity.held_by:
                continue
            box = entity.bounds()
            if not all(box["min_m"][i] - radius <= center[i] <= box["max_m"][i] + radius for i in range(3)):
                continue
            if not any(m in FLAMMABLE_MATERIALS for m in entity.materials):
                continue
            kind = "locked" if entity.protected else "creation" if entity.kind == "creation" else "stock"
            outcome, ask = FIRE_RULE.get((kind, self.mode), FIRE_RULE[(kind, "creative")])
            found.append(Target(entity.id, entity.kind, outcome, ask, box))
        return sorted(found, key=lambda t: t.id)

    # ------------------------------------------------------------------ what the requester's team knows

    def _knows_for(self, principal: str) -> Callable[[str], bool]:
        """The kernel's TeamKnowsFor: the player knows everything; the companion what the team sees now or holds on its map."""
        if principal.startswith("player:"):
            return lambda _id: True
        known = set(self.team_sight(principal)) | set(self._memory(self._team_principal()).entries)
        return known.__contains__

    def _sees_cell_for(self, principal: str) -> Callable[[str], bool]:
        """The kernel's TeamSeesCellFor, by the cell's surface: the shell is always in the team's sight in the box world; a thing's
        top only when the team knows the thing."""
        knows = self._knows_for(principal)
        return lambda surface: surface.startswith("shell:") or knows(surface)

    # ------------------------------------------------------------------ the checks before any cast

    def _check_companion_fire(self) -> None:
        """The Gubble's Fire, preview or cast (the kernel's CheckCompanionFire): the player's setting, one fire of its own at a time,
        and the wait after its last."""
        if not self.gubble_fire:
            raise HostError("permission_denied", FIRE_OFF, field_path="$.args.capability")
        if self.fire is not None and self.fire.principal == _COMPANION:
            raise HostError("target_busy", FIRE_BUSY_OWN, field_path="$.args.capability", retryable=True)
        wait = COMPANION_FIRE_COOLDOWN_S - (self._weather_s() - self._companion_fire_seen)
        if wait > 0:
            raise HostError("rate_limited", FIRE_WAIT.format(seconds=math.ceil(wait)), field_path="$.args.capability", retryable=True,
                            actual=round(wait, 1))

    def _fire_busy_words(self, principal: str) -> str:
        """Why another cast must wait for the fire that burns now, in words for whoever asks (the kernel's FireBusyWords)."""
        if self.fire is not None and self.fire.principal == _COMPANION:
            if not principal.startswith("player:"):
                return FIRE_BUSY_OWN
            return GUBBLE_FIRE_BURNING_SNUFF if self._learned("fire", "snuff") else GUBBLE_FIRE_BURNING
        return FIRE_BUSY_OTHER

    def _check_cast(self, ability, args: dict, principal: str, one_fire: bool = True) -> dict:
        """A cast's place and size against the island's rules for this caster, in the kernel's order (CheckCast)."""
        center = [_r(v) for v in args["center_m"]]
        radius, duration = float(args["radius_m"]), float(args["duration_s"])
        if ("player" if principal.startswith("player:") else "companion") not in ability.cast_by:
            raise HostError("permission_denied", CASTER_NOT_ALLOWED, field_path="$.args.capability")
        if radius > ability.area_radius_max_m:
            raise HostError("invalid_args", RADIUS_TOO_BIG, field_path="$.args.radius_m", allowed=ability.area_radius_max_m)
        if duration > ability.duration_max_s:
            raise HostError("invalid_args", DURATION_TOO_LONG, field_path="$.args.duration_s", allowed=ability.duration_max_s)
        if not all(self.room_bounds["min_m"][i] - 0.02 <= center[i] <= self.room_bounds["max_m"][i] + 0.02 for i in range(3)):
            raise HostError("out_of_bounds", CAST_OUTSIDE, field_path="$.args.center_m")
        gubble = self.entities.get(_GUBBLE)
        if gubble is None or gubble.removed or math.dist(gubble.position, center) > ability.reach_m:
            raise HostError("out_of_bounds", CAST_TOO_FAR, field_path="$.args.center_m", retryable=True)
        if not self._team_sees_spot(center, principal, _GUBBLE):
            raise HostError("target_not_found", CAST_UNSEEN, field_path="$.args.center_m")
        if one_fire and self.fire is not None:
            raise HostError("target_busy", self._fire_busy_words(principal), retryable=True)
        intensity = ability.params["intensity"][2] if "intensity" in ability.params else 0.6
        return {"center": center, "radius": radius, "duration": duration, "intensity": intensity}

    def _burning_at(self, point) -> bool:
        """Whether something burns at a point (the kernel's BurningAt): the fire's ground within SNUFF_AIM_M of it, or a lit
        campfire whose ring is."""
        if self.fire is not None and self._burning_near(point, SNUFF_AIM_M, SNUFF_AIM_RISE_M, SNUFF_AIM_RISE_M):
            return True
        return any(self._is_lit(c.id) and _flat(c.position, point) <= SNUFF_AIM_M + FIREPIT_CATCH_M
                   and abs(c.position[1] - point[1]) <= FIREPIT_CATCH_RISE_M for c in self._ensure_camps())

    def _fire_kind_of(self, args: dict) -> str:
        """What a Fire cast is, by its place (the kernel's FireKindOf, decision 77): a drag a Fireline (refused until learned); on
        something burning Snuff, once learned; else plain Fire. The box world has no water, so never a Will-o'-wisp."""
        if "end_m" in args:
            if not self._learned("fire", "fireline"):
                raise HostError("permission_denied", FIRELINE_NOT_LEARNED, field_path="$.args.end_m")
            return KIND_FIRELINE
        if self._learned("fire", "snuff") and self._burning_at(args["center_m"]):
            return KIND_SNUFF
        return KIND_FIRE

    def _is_snuff(self, message: dict) -> bool:
        """Whether a nature.cast is Snuff here now: transient, no room revision (the kernel's FireTransient)."""
        args = message.get("args") if isinstance(message, dict) else None
        return (isinstance(message, dict) and message.get("op") == "nature.cast" and isinstance(args, dict) and args.get("capability") == "fire"
                and "end_m" not in args and self.fire_learned and self._learned("fire", "snuff") and isinstance(args.get("center_m"), list)
                and self._burning_at(args["center_m"]))

    # ------------------------------------------------------------------ nature.cast

    def _op_nature_cast(self, principal, message, apply, new_revision):
        """nature.cast in the kernel's order (CommandHostNature.NatureCommand): the Bubbles first (no ground change), then the
        island's ability, no Fire before the first rest, the place deciding Fire's kind, the Gubble's fire guardrails, the cast's
        checks, then Grow, Snuff, a Fireline or Fire (an unlit firepit on its path)."""
        if message["args"].get("capability") in ("bubble_carry", "bubble_boat", "bubble_lift"):
            return self._bubble_cast(principal, message, apply)
        args = message["args"]
        capability = args["capability"]
        ability = self.rules.ability(capability) if self.rules is not None else None
        if ability is None:
            raise HostError("unsupported_capability", NO_NATURE_ABILITY, field_path="$.args.capability")
        if capability == "fire" and not self.fire_learned:
            raise HostError("permission_denied", FIRE_NOT_LEARNED, field_path="$.args.capability")
        # Decision 106: a drag of Grow is a bridge, refused before its count ahead of the cast's checks (the kernel's RequireGrowDrag).
        if capability == "grow" and "end_m" in args and self.grow_count < BRIDGE_AT:
            raise HostError("permission_denied", BRIDGE_LOCKED, field_path="$.args.end_m")
        kind = self._fire_kind_of(args) if capability == "fire" else "grow"
        # Snuff lights nothing: it is not the Gubble's one fire, and passes its guardrails (the player's setting included).
        if capability == "fire" and not principal.startswith("player:") and kind in (KIND_FIRE, KIND_FIRELINE):
            self._check_companion_fire()
        cast = self._check_cast(ability, args, principal, one_fire=kind in (KIND_FIRE, KIND_FIRELINE))
        preview = message.get("preview") is True
        if capability != "fire":
            return self._cast_grow(principal, message, apply, new_revision, ability, cast, preview)
        if kind == KIND_SNUFF:
            return self._cast_snuff(principal, message, apply, ability, cast, preview)
        line_end = self._check_fireline(ability, args, cast, principal) if kind == KIND_FIRELINE else None
        return self._cast_fire(principal, message, apply, new_revision, ability, cast, preview, line_end)

    def _remember_preview(self, principal: str, signature: tuple) -> str:
        now = self.clock.now()
        for token in [t for t, p in self._nature_previews.items() if p[3] <= now]:
            del self._nature_previews[token]
        while len(self._nature_previews) >= MAX_NATURE_PREVIEWS:
            del self._nature_previews[next(iter(self._nature_previews))]
        token = secrets.token_hex(16)  # 128 bits from a cryptographic generator, as every host-made handle
        self._nature_previews[token] = (principal, signature, self.revision, now + PREVIEW_LIFETIME_S)
        return token

    def _use_preview(self, args: dict, principal: str, signature: tuple, words: str) -> str:
        token = args.get("preview_id")
        planned = self._nature_previews.get(token) if token is not None else None
        if planned is None or planned[0] != principal or planned[1] != signature or planned[3] <= self.clock.now() or planned[2] != self.revision:
            raise HostError("revision_conflict", words, field_path="$.args.preview_id", retryable=True)
        return token

    def _cast_grow(self, principal, message, apply, new_revision, ability, cast, preview):
        """A Grow cast where the place decides (the kernel's CastGrow, decisions 75 and 104 to 107): a drag a bridge; the box world has
        no water and no faces, so never a waterpad nor ivy; on burnt ground the nearest burnt plant that seeds replanted, once open;
        else every plant rooted in the circle that is not at its cap and no seedling still growing grows the pack's share of its natural
        size (twice while it rains), the ash there greens; where that finds nothing, sprouts on bare fertile ground, once open; each
        lock and every other "nothing to grow" refused before the price, in the kernel's order and words. The box world's plants bear
        no food, so never the kernel's cast that only ripens a plant at its cap (decision 127): its receipts count ripened_plants 0
        (decision 133)."""
        center, radius = cast["center"], cast["radius"]
        if "end_m" in message["args"]:
            return self._cast_bridge(principal, message, apply, new_revision, ability, cast, preview,
                                     self._check_bridge(ability, message["args"], cast, principal))
        self._refresh_seedlings()
        wall = None  # the box world has no faces a wall's ivy would climb (the kernel's WallAt asks the room's physics)
        share = cast["intensity"] if ability.primitive == "growth.grow" else LEGACY_GROW_SHARE
        raining = self._rain() > WEATHER_RAIN01
        if raining:
            share *= RAIN_GARDEN_FACTOR
        standing = [p for p in self._rooted_in(center, radius) if p.grows() and not (p.id in self.plant_state and self.plant_state[p.id].burnt)]
        rooted = [p for p in standing if not (p.id in self.plant_state and self.plant_state[p.id].seedling)]
        ash = self._ash_in(center, radius)
        burnt = self._burnt_to_replant(center, radius)
        if burnt and self.grow_count >= SEEDLESS_AT:
            return self._cast_replant(principal, message, apply, new_revision, ability, cast, preview, burnt[0], ash)
        growing = [p for p in rooted if self._factor(p.id) < p.cap() - 1e-4]
        room = MAX_GROWN_PLANTS - sum(1 for s in self.plant_state.values() if s.grown)
        kept = []
        for plant in growing:
            state = self.plant_state.get(plant.id)
            if state is not None and state.grown:
                kept.append(plant)
            elif room > 0:
                kept.append(plant)
                room -= 1
        if not kept and not ash:
            if wall is not None:
                raise HostError("permission_denied", IVY_LOCKED, field_path="$.args.center_m")
            if burnt:
                raise HostError("permission_denied", REPLANT_LOCKED, field_path="$.args.center_m")
            spots = self._sprout_spots(center, radius) if not standing else []
            if spots:
                if self.grow_count < SEEDLESS_AT:
                    raise HostError("permission_denied", SPROUTS_LOCKED, field_path="$.args.center_m")
                return self._cast_sprouts(principal, message, apply, new_revision, ability, cast, preview, spots)
            if any(_flat(s.position, center) <= radius and abs(s.position[1] - center[1]) <= ROOT_HEIGHT_TOLERANCE_M for s in self.sprouts):
                raise HostError("invalid_args", SPROUTS_GROUND_FULL, field_path="$.args.center_m")
            if len(standing) > len(rooted) and not rooted:
                raise HostError("invalid_args", GROW_STILL_GROWING, field_path="$.args.center_m")
            if not rooted:
                raise HostError("invalid_args", GROW_NO_PLANT, field_path="$.args.center_m")
            if not growing:
                raise HostError("invalid_args", GROW_FULL, field_path="$.args.center_m")
            raise HostError("invalid_args", GROW_TOO_MANY.format(most=MAX_GROWN_PLANTS), field_path="$.args.center_m")
        keyed = not principal.startswith("player:") and ability.tier == "keyed_yes"
        data = {"center_m": center, "radius_m": radius, "duration_s": cast["duration"], "cell_count": 0, "cells": [],
                "cells_truncated": False, "grown_plants": len(kept), "ripened_plants": 0, "requires_approval": keyed}
        if preview:
            return {"data": data}
        if keyed and not self._approving:
            return {"_ask": {"reason": GROW_HOLD, "touched": {}, "bind_revision": True}, "data": data}
        committed = {k: v for k, v in data.items() if k not in ("cells", "cells_truncated")}
        committed["started_cells"] = 0
        price = self._guard_energy(ability.capability, principal)
        if not apply:
            return {"data": committed}
        step = self._begin_undo("Grow", new_revision, principal)
        tall = False
        for plant in kept:
            self._remember_plant(step, plant.id)
            state = self.plant_state.setdefault(plant.id, PlantState())
            state.sown = None  # a replanted plant grown on from its natural size is a plant like any other again
            state.factor = min(plant.cap(), state.factor + share)
            tall |= state.factor >= plant.cap() - 1e-4
        self._green_ash(step, ash)
        self.nature_changed_at = new_revision
        self._charge_energy(price)
        learned = []
        if tall:
            learned.append("tall_plant")
        if kept and self._thicket_around(center, radius, kept):
            learned.append("thicket")
        if ash:
            learned.append("green_ash")
        if raining and kept:
            learned.append("rain_garden")
        self._learn("grow", learned)
        return {"data": committed}

    def _green_ash(self, step, ash: list[str]) -> None:
        for key in ash:
            self._remember_cell(step, key)
            self.ash.pop(key, None)  # Green ash: it greens at once

    # ------------------------------------------------------------------ Grow's unlocks

    def _seedling_factor(self, sown: float) -> float:
        """A seedling's factor now (GrowKindLaws.SeedlingFactor of its phase): a third of the way to natural a game hour of daylight
        (decision 122: the kernel's SeedlingDaylightH, from the hour of day it was sown, in UTC on the mock's clock)."""
        daylight = daylight_hours(sown / 3600.0 % 24, max(0.0, self.clock.now() - sown) / 3600.0)
        phase = min(SEEDLING_PHASES, math.floor(daylight / SEEDLING_PHASE_H + 1e-9))
        return 1.0 if phase >= SEEDLING_PHASES else SEEDLING_START + (1 - SEEDLING_START) * phase / SEEDLING_PHASES

    def _refresh_seedlings(self) -> None:
        """The seedlings' sizes follow the clock (the kernel's RefreshSeedlings): derived from when they were sown."""
        for state in self.plant_state.values():
            if state.sown is not None and not state.burnt:
                state.factor = self._seedling_factor(state.sown)
        for sprout in self.sprouts:
            sprout.factor = self._seedling_factor(sprout.sown)

    def _commit_grow_kind(self, principal, message, apply, new_revision, ability, cast, preview, kind: str, grown: int, hold: str,
                          change, learned=(), extra=None):
        """One of Grow's unlocks previewed, held for a keyed pack's yes or committed as one revision, priced as any Grow (the
        kernel's CommitGrowKind)."""
        keyed = not principal.startswith("player:") and ability.tier == "keyed_yes"
        data = {"center_m": cast["center"], "radius_m": cast["radius"], "duration_s": cast["duration"], "cell_count": 0, "cells": [],
                "cells_truncated": False, "grown_plants": grown, "ripened_plants": 0, "requires_approval": keyed, "kind": kind, **(extra or {})}
        if preview:
            return {"data": data}
        if keyed and not self._approving:
            return {"_ask": {"reason": hold, "touched": {}, "bind_revision": True}, "data": data}
        committed = {k: v for k, v in data.items() if k not in ("cells", "cells_truncated")}
        committed["started_cells"] = 0
        price = self._guard_energy(ability.capability, principal)
        if not apply:
            return {"data": committed}
        step = self._begin_undo("Grow", new_revision, principal)
        change(step)
        self.nature_changed_at = new_revision
        self._charge_energy(price)
        self._learn("grow", list(learned))
        return {"data": committed}

    @staticmethod
    def _spot_hash(i: int, j: int) -> int:
        """A lattice point's hash, the kernel's SpotHash (unsigned 32-bit arithmetic)."""
        h = ((i * 0x9E3779B1) ^ (j * 0x85EBCA77)) & 0xFFFFFFFF
        h ^= h >> 15
        h = (h * 0x2C1B3C6D) & 0xFFFFFFFF
        h ^= h >> 12
        h = (h * 0x297A2D39) & 0xFFFFFFFF
        return h ^ (h >> 15)

    def _sprout_spots(self, center, radius: float) -> list[tuple[list[float], int]]:
        """The free fertile spots of the circle a sprout may take, nearest first, at most 12 (the kernel's SproutSpots): lattice points
        on a shell part of grass or soil, no ash or fire there, 0.15 m from every sprout and every plant still standing."""
        spacing, row_m = SPROUT_LATTICE_M, SPROUT_LATTICE_M * math.sqrt(3) / 2
        points = []
        for j in range(math.floor((center[2] - radius) / row_m) - 1, math.ceil((center[2] + radius) / row_m) + 2):
            offset = spacing * 0.5 if j & 1 else 0.0
            for i in range(math.floor((center[0] - radius - offset) / spacing) - 1, math.ceil((center[0] + radius - offset) / spacing) + 2):
                h = self._spot_hash(i, j)
                x = i * spacing + offset + ((h & 0xFF) / 255 * 2 - 1) * SPROUT_JITTER_M
                z = j * row_m + (((h >> 8) & 0xFF) / 255 * 2 - 1) * SPROUT_JITTER_M
                distance = math.hypot(x - center[0], z - center[2])
                if distance <= radius:
                    points.append((distance, j, i, x, z, h))
        spots = []
        burning = set(self.fire.burning) if self.fire is not None else set()
        for _d, _j, _i, x, z, h in sorted(points):
            if len(spots) >= SPROUTS_PER_CAST:
                break
            ground = self._fertile_ground(x, z, center[1])
            if ground is None or abs(ground - center[1]) > ROOT_HEIGHT_TOLERANCE_M:
                continue
            key = _key(*_grid([x, 0.0, z]))
            if key in burning or (key in self.ash and self.ash[key][1] > 0):
                continue
            root = [x, ground, z]
            if any(_flat(s.position, root) < SPROUT_APART_M and abs(s.position[1] - root[1]) <= ROOT_HEIGHT_TOLERANCE_M for s in self.sprouts):
                continue
            if any(not (p.id in self.plant_state and self.plant_state[p.id].burnt) for p in self._rooted_in(root, SPROUT_APART_M)):
                continue
            spots.append((root, h))
        return spots

    def _fertile_ground(self, x: float, z: float, near_y: float) -> float | None:
        """The ground's height at a point when it is fertile: the highest walkable shell part's top under it, of grass or soil."""
        best = None
        for entity in self.entities.values():
            if entity.kind != "shell" or "walkable_top" not in entity.affordances or entity.removed:
                continue
            box = entity.bounds()
            if box["min_m"][0] <= x <= box["max_m"][0] and box["min_m"][2] <= z <= box["max_m"][2] and box["max_m"][1] <= near_y + 0.1:
                if best is None or box["max_m"][1] > best[0]:
                    best = (box["max_m"][1], self._surface_material(entity))
        return best[0] if best is not None and best[1] in ("grass", "soil") else None

    def _sprout_templates(self, center) -> list[Plant]:
        """The nearest plant of each of the nearest three grass and flower kinds round a point (the kernel's SproutTemplates; the
        mock's plants have no sets, so a kind is a plant's set name)."""
        best: dict[str, tuple[float, str]] = {}
        for plant in self.plants.values():
            if plant.kind != "plant" or plant.fuel_class not in ("grass", "herb") or not plant.seeds:
                continue
            distance = _flat(plant.position, center)
            if distance > SPROUT_SET_SEARCH_M:
                continue
            name = plant.id[len("plant:"):plant.id.rindex("-")]
            if name not in best or (distance, plant.id) < best[name]:
                best[name] = (distance, plant.id)
        return [self.plants[pid] for _d, pid in sorted(best.values())[:SPROUT_SETS_MIXED]]

    def _cast_sprouts(self, principal, message, apply, new_revision, ability, cast, preview, spots):
        """Sprouts on the free fertile spots (the kernel's CastSprouts, decision 104)."""
        templates = self._sprout_templates(cast["center"])
        if not templates:
            raise HostError("invalid_args", SPROUTS_NOTHING_TO_COPY, field_path="$.args.center_m")
        room = MAX_SPROUTS - len(self.sprouts)
        if room <= 0:
            raise HostError("invalid_args", SPROUTS_TOO_MANY.format(most=MAX_SPROUTS), field_path="$.args.center_m")
        kept = spots[:room]
        sown = math.floor(self.clock.now())

        def change(step):
            self._remember_sprouts(step)
            for root, h in kept:
                self.sprouts.append(Sprout(templates[h % len(templates)].id, list(root), sown, factor=self._seedling_factor(sown)))
        return self._commit_grow_kind(principal, message, apply, new_revision, ability, cast, preview, KIND_SPROUTS, len(kept), SPROUTS_HOLD, change)

    def _burnt_to_replant(self, center, radius: float) -> list[Plant]:
        return sorted((p for p in self._rooted_in(center, radius) if p.seeds and p.grows() and p.id in self.plant_state and self.plant_state[p.id].burnt),
                      key=lambda p: (_flat(p.position, center), p.id))

    def _avatar_where_it_grows(self, plant: Plant) -> bool:
        """Whether an avatar stands where a replanted tree's trunk would be (the kernel's AvatarWhereItGrows; the mock's plants
        without collision are all but trees)."""
        if plant.kind != "tree":
            return False
        box = plant.bounds(1.0)
        for avatar_id in ("avatar:player", _GUBBLE):
            avatar = self.entities.get(avatar_id)
            if avatar is None or avatar.removed:
                continue
            other = avatar.bounds()
            if all(other["min_m"][i] <= box["max_m"][i] and other["max_m"][i] >= box["min_m"][i] for i in range(3)):
                return True
        return False

    def _cast_replant(self, principal, message, apply, new_revision, ability, cast, preview, plant: Plant, ash: list[str]):
        """The nearest burnt plant back as a seedling, and the ash greened (the kernel's CastReplant, decision 105)."""
        if self._avatar_where_it_grows(plant):
            raise HostError("target_busy", REPLANT_STANDING, field_path="$.args.center_m", retryable=True)
        sown = math.floor(self.clock.now())

        def change(step):
            self._remember_plant(step, plant.id)
            self.plant_state[plant.id] = PlantState(factor=self._seedling_factor(sown), burn=0.0, sown=sown)
            self._green_ash(step, ash)
        return self._commit_grow_kind(principal, message, apply, new_revision, ability, cast, preview, KIND_REPLANT, 1, REPLANT_HOLD, change,
                                      learned=["green_ash"] if ash else [])

    def _check_bridge(self, ability, args: dict, cast: dict, principal: str):
        """A bridge's drag over the box world's floors, in the kernel's order (CheckBridge): the far lip inside the island, it and the
        middle within reach and sight, the island's most, the length, the lips' ground and their level, no other bridge there, one
        void between them, nothing in the way, no avatar where its deck would be."""
        end = [_r(v) for v in args["end_m"]]
        start = cast["center"]
        if not all(self.room_bounds["min_m"][i] - 0.02 <= end[i] <= self.room_bounds["max_m"][i] + 0.02 for i in range(3)):
            raise HostError("out_of_bounds", BRIDGE_OUTSIDE, field_path="$.args.end_m")
        middle = [(start[i] + end[i]) / 2 for i in range(3)]
        gubble = self.entities.get(_GUBBLE)
        for point in (end, middle):
            if gubble is None or gubble.removed or math.dist(gubble.position, point) > ability.reach_m:
                raise HostError("out_of_bounds", BRIDGE_TOO_FAR, field_path="$.args.end_m", retryable=True)
            if not self._team_sees_spot(point, principal, _GUBBLE):
                raise HostError("target_not_found", BRIDGE_UNSEEN, field_path="$.args.end_m")
        if len(self.bridges) >= MAX_BRIDGES:
            raise HostError("invalid_args", BRIDGE_TOO_MANY.format(most=MAX_BRIDGES), field_path="$.args.end_m")
        length = _flat(start, end)
        if length > BRIDGE_MAX_M + 1e-4:
            raise HostError("invalid_args", BRIDGE_TOO_LONG.format(most=f"{BRIDGE_MAX_M:.1f}"), field_path="$.args.end_m", allowed=BRIDGE_MAX_M)
        if length < BRIDGE_MIN_M:
            raise HostError("invalid_args", BRIDGE_TOO_SHORT.format(least=f"{BRIDGE_MIN_M:.1f}"), field_path="$.args.end_m")
        near = self._floor_below(start, start[1] + BRIDGE_LIP_M, 2 * BRIDGE_LIP_M)
        if near is None:
            raise HostError("invalid_args", BRIDGE_NO_LIP, field_path="$.args.center_m")
        far = self._floor_below(end, end[1] + BRIDGE_LIP_M, 2 * BRIDGE_LIP_M)
        if far is None:
            raise HostError("invalid_args", BRIDGE_NO_LIP, field_path="$.args.end_m")
        if abs(near - far) > BRIDGE_LEVEL_M + 1e-4:
            raise HostError("invalid_args", BRIDGE_NOT_LEVEL, field_path="$.args.end_m")
        deck = max(near, far)
        a, b = [start[0], deck, start[2]], [end[0], deck, end[2]]
        if any(self._decks_meet(bridge, a, b) for bridge in self.bridges):
            raise HostError("invalid_args", BRIDGE_HERE, field_path="$.args.end_m")
        span = self._void_span(a, b)
        if self._deck_blocked(a, b):
            raise HostError("invalid_args", BRIDGE_IN_THE_WAY, field_path="$.args.end_m")
        if self._avatar_over_span(a, b, span):
            raise HostError("target_busy", BRIDGE_AVATAR, field_path="$.args.end_m", retryable=True)
        return a, b, span

    def _void_under(self, at) -> bool:
        return self._floor_below(at, at[1] + 0.005, BRIDGE_VOID_DROP_M + 0.005) is None

    def _void_span(self, a, b) -> tuple[float, float]:
        """The one void the deck crosses, metres along it from a (the kernel's VoidSpan)."""
        length = _flat(a, b)
        steps = max(2, math.ceil(length / BRIDGE_SAMPLE_M))

        def at(s: float):
            return [a[i] + (b[i] - a[i]) * s / length for i in range(3)]
        voids = [0 < k < steps and self._void_under(at(length * k / steps)) for k in range(steps + 1)]
        if True not in voids:
            raise HostError("invalid_args", BRIDGE_NO_GAP, field_path="$.args.end_m")
        first, last = voids.index(True), len(voids) - 1 - voids[::-1].index(True)
        if not all(voids[first:last + 1]):
            raise HostError("invalid_args", BRIDGE_TWO_GAPS, field_path="$.args.end_m")

        def edge(ground: float, hole: float) -> float:
            while abs(hole - ground) > BRIDGE_EDGE_M:
                mid = (ground + hole) / 2
                if self._void_under(at(mid)):
                    hole = mid
                else:
                    ground = mid
            return hole
        return (_r(edge(length * (first - 1) / steps, length * first / steps)), _r(edge(length * (last + 1) / steps, length * last / steps)))

    def _deck_box(self, a, b, low: float, high: float) -> dict:
        half = BRIDGE_WIDTH_M / 2
        return {"min_m": [min(a[0], b[0]) - half, a[1] + low, min(a[2], b[2]) - half], "max_m": [max(a[0], b[0]) + half, a[1] + high, max(a[2], b[2]) + half]}

    def _deck_blocked(self, a, b) -> bool:
        """Whether a thing lies over the deck, from a step over it to BRIDGE_CLEAR_M (the box world's things are boxes)."""
        box = self._deck_box(a, b, BRIDGE_LEVEL_M + 0.001, BRIDGE_CLEAR_M)
        for entity in self.entities.values():
            if entity.removed or entity.kind == "avatar" or entity.held_by:
                continue
            other = entity.bounds()
            if all(other["min_m"][i] < box["max_m"][i] and other["max_m"][i] > box["min_m"][i] for i in range(3)):
                return True
        return False

    @staticmethod
    def _decks_meet(bridge: Bridge, a, b) -> bool:
        if abs(bridge.start[1] - a[1]) > BRIDGE_CLEAR_M:
            return False

        def to_segment(p, s, e) -> float:
            dx, dz = e[0] - s[0], e[2] - s[2]
            t = max(0.0, min(1.0, ((p[0] - s[0]) * dx + (p[2] - s[2]) * dz) / max(dx * dx + dz * dz, 1e-8)))
            return math.hypot(s[0] + dx * t - p[0], s[2] + dz * t - p[2])
        for k in range(17):
            mine = [a[i] + (b[i] - a[i]) * k / 16 for i in range(3)]
            theirs = [bridge.start[i] + (bridge.end[i] - bridge.start[i]) * k / 16 for i in range(3)]
            if to_segment(mine, bridge.start, bridge.end) < BRIDGE_WIDTH_M or to_segment(theirs, a, b) < BRIDGE_WIDTH_M:
                return True
        return False

    def _avatar_over_span(self, a, b, span) -> bool:
        """Whether an avatar is where the deck's collision would be: over the void, at the deck's height (the kernel's AvatarOverSpan)."""
        length = _flat(a, b)
        s, e = ([a[i] + (b[i] - a[i]) * span[k] / length for i in range(3)] for k in (0, 1))
        box = self._deck_box(s, e, -0.005, 0.005)
        for avatar_id in ("avatar:player", _GUBBLE):
            avatar = self.entities.get(avatar_id)
            if avatar is None or avatar.removed:
                continue
            other = avatar.bounds()
            if all(other["min_m"][i] <= box["max_m"][i] and other["max_m"][i] >= box["min_m"][i] for i in range(3)):
                return True
        return False

    def _cast_bridge(self, principal, message, apply, new_revision, ability, cast, preview, plan):
        """The bridge grown (the kernel's CastBridge, decision 106): its receipt's center_m and end_m are its lips as it grew."""
        a, b, span = plan

        def change(step):
            self._remember_bridges(step)
            used = {bridge.id for bridge in self.bridges}
            self.bridges.append(Bridge(next(f"bridge:{n}" for n in range(1, MAX_BRIDGES + 2) if f"bridge:{n}" not in used), a, b, span))
        return self._commit_grow_kind(principal, message, apply, new_revision, ability, {**cast, "center": a}, preview, KIND_BRIDGE, 1, BRIDGE_HOLD,
                                      change, extra={"end_m": b})

    def _remember_sprouts(self, step) -> None:
        if step is not None and step.sprouts is None:
            step.sprouts = copy.deepcopy(self.sprouts)

    def _remember_bridges(self, step) -> None:
        if step is not None and step.bridges is None:
            step.bridges = copy.deepcopy(self.bridges)

    def _factor(self, plant_id: str) -> float:
        state = self.plant_state.get(plant_id)
        return state.factor if state is not None else 1.0

    def _rooted_in(self, center, radius: float, tolerance: float = ROOT_HEIGHT_TOLERANCE_M) -> list[Plant]:
        return sorted((p for p in self.plants.values() if _flat(p.position, center) <= radius and abs(p.position[1] - center[1]) <= tolerance),
                      key=lambda p: p.id)

    def _ash_in(self, center, radius: float) -> list[str]:
        burning = set(self.fire.burning) if self.fire is not None else set()
        return [_key(x, z) for x, z in disk(center, radius)
                if _key(x, z) in self.ash and self.ash[_key(x, z)][1] > 0 and _key(x, z) not in burning
                and abs(self.ash[_key(x, z)][0] - center[1]) <= ROOT_HEIGHT_TOLERANCE_M]

    def _thicket_around(self, center, radius: float, grown: list[Plant]) -> bool:
        full = [p for p in self._rooted_in(center, radius + THICKET_SPAN_M)
                if not (p.id in self.plant_state and self.plant_state[p.id].burnt) and self._factor(p.id) >= p.cap() - 1e-4]
        full = sorted(full, key=lambda p: _flat(p.position, center))[:THICKET_LOOKS_AT]
        mine = {p.id for p in grown}

        def near(a, b):
            return _flat(a.position, b.position) <= THICKET_SPAN_M

        for i in range(len(full)):
            for j in range(i + 1, len(full)):
                if not near(full[i], full[j]):
                    continue
                for k in range(j + 1, len(full)):
                    if near(full[i], full[k]) and near(full[j], full[k]) and mine & {full[i].id, full[j].id, full[k].id}:
                        return True
        return False

    def _check_fireline(self, ability, args: dict, cast: dict, principal: str) -> list[float]:
        """A Fireline's end (the kernel's CheckFireline): inside the island, 0.2 to 3 m from its start, its end and its middle within
        the Gubble's reach and the team's sight."""
        end = [_r(v) for v in args["end_m"]]
        start = cast["center"]
        length = math.dist(start, end)
        if length > FIRELINE_MAX_M:
            raise HostError("invalid_args", FIRELINE_TOO_LONG.format(most=f"{FIRELINE_MAX_M:g}"), field_path="$.args.end_m",
                            allowed=FIRELINE_MAX_M)
        if length < FIRELINE_MIN_M:
            raise HostError("invalid_args", FIRELINE_TOO_SHORT, field_path="$.args.end_m")
        if not all(self.room_bounds["min_m"][i] - 0.02 <= end[i] <= self.room_bounds["max_m"][i] + 0.02 for i in range(3)):
            raise HostError("out_of_bounds", FIRELINE_OUTSIDE, field_path="$.args.end_m")
        middle = [(start[i] + end[i]) / 2 for i in range(3)]
        gubble = self.entities.get(_GUBBLE)
        for point in (end, middle):
            if gubble is None or math.dist(gubble.position, point) > ability.reach_m:
                raise HostError("out_of_bounds", FIRELINE_TOO_FAR, field_path="$.args.end_m", retryable=True)
            if not self._team_sees_spot(point, principal, _GUBBLE):
                raise HostError("target_not_found", FIRELINE_UNSEEN, field_path="$.args.end_m")
        return end

    def _cast_fire(self, principal, message, apply, new_revision, ability, cast, preview, line_end):
        """A Fire cast (the kernel's CastFire): previewed first, committed as exactly that preview; an unlit firepit in its circle is
        lit instead; Creative's one ask before it burns a build, the player's no lighting it with the builds spared."""
        args = message["args"]
        center, radius, duration, intensity = cast["center"], cast["radius"], cast["duration"], cast["intensity"]
        if line_end is None:
            firepit = self._unlit_firepit_under(center, radius)
            if firepit is not None:
                return self._light_firepit(principal, message, apply, new_revision, ability, cast, preview, firepit)
        if line_end is not None:
            most = min(ability.area_radius_max_m, MAX_FIRE_RADIUS_M)
            area_center = [(center[i] + line_end[i]) / 2 for i in range(3)]
            area_radius = min(most, _flat(center, line_end) / 2 + FIRELINE_SPREAD_M)
        else:
            area_center, area_radius = center, radius
        targets = self._fire_targets(area_center, area_radius)
        if len(targets) > MAX_FIRE_TARGETS:
            raise HostError("invalid_args", TOO_MANY_TARGETS, field_path="$.args.radius_m")
        burnable = [t for t in targets if t.outcome != "fireproof"]
        companion = not principal.startswith("player:")
        keyed = companion and ability.tier == "keyed_yes"
        asked = [t for t in burnable if t.outcome == "scorches" and t.ask_first]
        spare = self._sparing and not keyed
        must_ask = (bool(asked) and not spare) or keyed
        creations = {t.id: self.entities[t.id].revision for t in burnable if t.outcome == "scorches" and not (spare and t.ask_first)}
        shelter = self._shelter(creations)
        plant_fuel = self._plant_fuel(area_center, area_radius)
        if line_end is not None:
            cells = line_cells(center, line_end)

            def probe_y(x, z):
                ux, uz = line_end[0] - center[0], line_end[2] - center[2]
                t = min(1.0, max(0.0, (((x + 0.5) * CELL_M - center[0]) * ux + ((z + 0.5) * CELL_M - center[2]) * uz) / max(ux * ux + uz * uz, 1e-8)))
                return center[1] + (line_end[1] - center[1]) * t
        else:
            cells = list(disk(center, radius))

            def probe_y(x, z):
                return center[1]
        eligible = []
        for x, z in cells:
            ground = self._ground_cell(x, z, probe_y(x, z), shelter, plant_fuel)
            if ground is not None:
                eligible.append((_key(x, z), ground[0], ground[1]))
            if line_end is not None and len(eligible) >= MAX_BURNING:
                break
        if not eligible:
            raise HostError("invalid_args", NO_DRY_FUEL_LINE if line_end is not None else NO_DRY_FUEL, field_path="$.args.center_m")
        signature = ("fire", tuple(center), area_radius, duration, intensity, self.mode, tuple((t.id, t.outcome) for t in targets),
                     tuple(line_end) if line_end is not None else None)
        knows = self._knows_for(principal)
        sees = self._sees_cell_for(principal)
        seen = [c for c in eligible if sees(c[2])]
        known = [t.id for t in burnable if knows(t.id)]
        data = {"center_m": center, "radius_m": area_radius, "duration_s": duration, "cell_count": len(seen),
                "cells": [[_r(v) for v in c[1]] for c in seen[:MAX_PREVIEW_CELLS]], "cells_truncated": len(seen) > MAX_PREVIEW_CELLS}
        if line_end is not None:
            data.update(kind=KIND_FIRELINE, end_m=line_end)
        elif preview:
            data["kind"] = KIND_FIRE
        data.update(affected_ids=known, max_burning_cells=MAX_BURNING, requires_approval=must_ask)
        outcome = {"affected": known} if known else {}
        if preview:
            data["avatars_in_area"] = self._avatars_in_area(area_center, area_radius, principal)
            data["preview_id"] = self._remember_preview(principal, signature)
            return {**outcome, "data": data}
        token = self._use_preview(args, principal, signature, FIRE_PREVIEW_AGAIN)
        if must_ask and not self._approving:
            whose = "The Gubble's fire" if companion else "Your fire"
            reason = (FIRE_HOLD_BUILDS.format(whose=whose) if asked and not keyed else FIRE_HOLD_KEYED_BUILDS if asked else FIRE_HOLD_KEYED)
            return {"_ask": {"reason": reason, "touched": {t.id: self.entities[t.id].revision for t in asked}, "bind_revision": True,
                             "spares_on_deny": bool(asked) and not keyed}, **outcome, "data": data}
        # It takes hold within IGNITION_RADIUS_M of its centre (a Fireline along its whole line) and burns the rest of its ground at
        # once in the mock (the kernel spreads it there over time).
        lit = eligible if line_end is not None else [c for c in eligible if _flat(c[1], center) <= min(radius, IGNITION_RADIUS_M)]
        if not lit:
            raise HostError("invalid_args", NOTHING_TAKES_HOLD, field_path="$.args.center_m")
        price = self._guard_energy(KIND_FIRELINE if line_end is not None else ability.capability, principal)
        committed = {k: v for k, v in data.items() if k not in ("cells", "cells_truncated", "affected_ids")}
        committed["started_cells"] = sum(1 for c in lit if sees(c[2]))
        if not apply:
            return {**outcome, "data": committed}
        step = self._begin_undo("Fireline" if line_end is not None else "Fire", new_revision, principal)
        for key, centre, _surface in eligible:
            self._remember_cell(step, key)
            self.ash[key] = (centre[1], max(ASH_LEFT, self.ash.get(key, (0.0, 0.0))[1]))
        self.fire = Fire(principal, self._approved_by_for(principal), list(area_center), area_radius, intensity, new_revision,
                         self._weather_s() + duration, creations, {key: (centre, surface) for key, centre, surface in eligible},
                         paid=price > 0)
        self._nature_previews.pop(token, None)
        self.nature_changed_at = new_revision
        self._charge_energy(price)
        if line_end is None:
            self._note_fire_for_fireline(center, new_revision)
        return {**outcome, "data": committed}

    def _approved_by_for(self, principal: str) -> str:
        """At whose direction a fire is lit, as far as the host can verify it: the player when the player approved it."""
        return _PLAYER if self._approving else principal

    def _avatars_in_area(self, center, radius: float, principal: str) -> list[str]:
        inside = []
        for avatar in ("avatar:player", _GUBBLE):
            body = self.entities.get(avatar)
            if body is None or body.removed:
                continue
            if _flat(body.position, center) > radius + PLAYER_BURN_M or abs(body.position[1] - center[1]) > 0.5:
                continue
            if principal.startswith("player:") or self._view is None or avatar in self._view:
                inside.append(avatar)
        return inside

    def _light_firepit(self, principal, message, apply, new_revision, ability, cast, preview, firepit: CampSite):
        """A Fire cast on an unlit firepit lights it and nothing else (the kernel's LightFirepit): one cell, no undo step; the first
        firepit lit teaches Campfire."""
        center, radius, duration = cast["center"], cast["radius"], cast["duration"]
        keyed = not principal.startswith("player:") and ability.tier == "keyed_yes"
        signature = ("fire", "firepit", firepit.id, tuple(center), radius, self.mode)
        data = {"center_m": center, "radius_m": radius, "duration_s": duration, "cell_count": 1, "requires_approval": keyed}
        if preview:
            data.update(cells=[list(firepit.position)], cells_truncated=False, max_burning_cells=MAX_BURNING, avatars_in_area=[],
                        affected_ids=[], kind=KIND_FIREPIT, preview_id=self._remember_preview(principal, signature))
            return {"data": data}
        token = self._use_preview(message["args"], principal, signature, FIRE_PREVIEW_AGAIN)
        if keyed and not self._approving:
            return {"_ask": {"reason": FIREPIT_HOLD, "touched": {}, "bind_revision": True}, "data": data}
        price = self._guard_energy(ability.capability, principal)
        committed = dict(data, started_cells=1)
        if not apply:
            return {"data": committed}
        self._nature_previews.pop(token, None)
        self._out.discard(firepit.id)
        if not firepit.starting:
            self._lit.add(firepit.id)
        self._camp_rain.pop(firepit.id, None)
        self.nature_changed_at = new_revision
        self._charge_energy(price)
        self._note_fire_for_fireline(center, new_revision)
        if not firepit.starting:
            self._learn("fire", ["campfire"])
        return {"data": committed}

    def _cast_snuff(self, principal, message, apply, ability, cast, preview):
        """Snuff (the kernel's CastSnuff, decision 79): everything burning in a 1.5 m circle goes out, lit campfires too, the ground
        there damp for 30 s; no fire and no room revision (a transient receipt); the Gubble's Snuff of a lit campfire waits for the
        player's yes."""
        center = cast["center"]
        fire = self.fire
        burning = [(key, c, s) for key, (c, s) in fire.burning.items()
                   if _flat(c, center) <= SNUFF_RADIUS_M and abs(c[1] - center[1]) <= SNUFF_RISE_M] if fire is not None else []
        campfires = sorted(c.id for c in self._ensure_camps() if self._is_lit(c.id)
                           and _flat(c.position, center) <= SNUFF_RADIUS_M + FIREPIT_CATCH_M and abs(c.position[1] - center[1]) <= SNUFF_RISE_M)
        if not burning and not campfires:
            raise HostError("invalid_args", NOTHING_BURNING, field_path="$.args.center_m")
        companion = not principal.startswith("player:")
        keyed = companion and ability.tier == "keyed_yes"
        must_ask = keyed or (companion and bool(campfires))
        signature = ("fire", KIND_SNUFF, tuple(center), SNUFF_RADIUS_M, self.mode, tuple(campfires))
        sees = self._sees_cell_for(principal)
        seen = [c for c in burning if sees(c[2])]
        data = {"center_m": center, "radius_m": SNUFF_RADIUS_M, "duration_s": SNUFF_DAMP_S, "cell_count": len(seen),
                "requires_approval": must_ask, "kind": KIND_SNUFF}
        if preview:
            data.update(cells=[[_r(v) for v in c[1]] for c in seen[:MAX_PREVIEW_CELLS]], cells_truncated=len(seen) > MAX_PREVIEW_CELLS,
                        preview_id=self._remember_preview(principal, signature))
            return {"data": data}
        token = self._use_preview(message["args"], principal, signature, KIND_PREVIEW_AGAIN)
        if must_ask and not self._approving:
            reason = SNUFF_HOLD_KEYED if keyed and not campfires else SNUFF_HOLD_CAMPFIRE
            return {"_ask": {"reason": reason, "touched": {}, "bind_revision": True}, "data": data}
        price = self._guard_energy(KIND_SNUFF, principal)
        committed = dict(data, started_cells=0)
        if not apply:
            return {"data": committed}
        self._nature_previews.pop(token, None)
        now = self._weather_s()
        self._snuff_zones.append((list(center), SNUFF_RADIUS_M, now + SNUFF_DAMP_S))
        del self._snuff_zones[:max(0, len(self._snuff_zones) - 16)]
        if fire is not None and burning:
            for key, _c, _s in burning:
                del fire.burning[key]
            if not fire.burning:  # the fire is out once nothing of it burns; the Gubble's wait counts from now
                if fire.principal == _COMPANION:
                    self._companion_fire_seen = now
                self.fire = None
        for camp in campfires:
            self._put_out_campfire(camp)
        self._charge_energy(price)
        return {"data": committed}

    def _note_fire_for_fireline(self, center, revision: int) -> None:
        """A committed Fire cast toward Fireline (the kernel's NoteFireForFireline, decision 78): learned when this and two others of
        the last 5 minutes, each 0.5 m from the others, lie along one line, and none of the three hurt anyone or burned a build."""
        now = self.clock.now()
        self._recent_fires = [f for f in self._recent_fires if 0 <= now - f[0] <= FIRELINE_LEARN_WINDOW_S]
        earlier = [f for f in self._recent_fires if f[2] not in self._harmful_fires]
        self._recent_fires.append((now, list(center), revision))
        del self._recent_fires[:max(0, len(self._recent_fires) - 16)]
        if self._learned("fire", "fireline"):
            return
        for i in range(len(earlier)):
            for j in range(i + 1, len(earlier)):
                if along_one_line(earlier[i][1], earlier[j][1], center):
                    self._learn("fire", ["fireline"])
                    return

    def _bubble_on_fire(self, centre, radius: float) -> None:
        """A bubble cast on a fire teaches Snuff (decision 79; the kernel's StepFireKinds), never from a fire that burned a build."""
        fire = self.fire
        if fire is None or fire.revision in self._harmful_fires or self._learned("fire", "snuff"):
            return
        floor = centre[1] - radius
        if self._burning_near(centre, radius + SNUFF_AIM_M, centre[1] - floor + SNUFF_AIM_RISE_M, radius):
            self._learn("fire", ["snuff"])

    # ------------------------------------------------------------------ nature.repair

    def _op_nature_repair(self, principal, message, apply, new_revision):
        """A scorched creation repaired at its original material cost, free in Creative (the kernel's Repair)."""
        target = message["args"]["target"]
        if not principal.startswith("player:"):
            self._require(target, "$.args.target")  # the kernel's CheckPerceived: a companion names only what the team sees now
        entity = self.entities.get(target)
        if entity is None or entity.removed or entity.kind != "creation":
            raise HostError("target_not_found", REPAIR_NOT_HERE, field_path="$.args.target")
        if entity.protected:
            raise HostError("target_protected", REPAIR_LOCKED, field_path="$.args.target")
        record = self.burn_records.get(target)
        if record is None or not record.creation or record.burn <= 0:
            raise HostError("invalid_args", REPAIR_NOT_NEEDED, field_path="$.args.target")
        if self.fire is not None:
            raise HostError("target_busy", self._fire_busy_words(principal), retryable=True)
        free = self.mode == "creative"
        if not free and any(self.wallet.get(material, 0) < units for material, units in record.repair_cost.items()):
            raise HostError("invalid_args", REPAIR_NO_MATERIALS.format(needs=self._repair_needs(record.repair_cost)), field_path="$.args.target")
        data = {"cost": {} if free else dict(sorted(record.repair_cost.items()))}
        # Decision 131: what a paid repair spends, by kind, on its receipt (the preview's too: what it would spend), whoever repairs.
        spent = {} if free else self._repair_spent(record.repair_cost)
        if spent:
            data["spent"] = spent
        if message.get("preview") is True:
            return {"affected": [target], "data": data}
        # The Gubble's repair of a build the player made waits for the yes, as every change of what the player made does; a spend alone
        # never asks (decision 131: the Gubble repairs without asking when a repair spends logs or stones, and the spend is shown).
        if not principal.startswith("player:") and entity.created_by == _PLAYER and not self._approving:
            return {"_ask": {"reason": REPAIR_HOLD.format(target=target), "touched": {target: entity.revision}, "bind_revision": True},
                    "affected": [target], "data": data}
        if not apply:
            return {"affected": [target], "data": data}
        step = self._begin_undo("Repair", new_revision, principal)
        self._remember_object(step, target)
        if not free:
            for material, units in record.repair_cost.items():
                self.wallet[material] -= units
        del self.burn_records[target]
        self.nature_changed_at = new_revision
        return {"affected": [target], "data": data}

    @staticmethod
    def _repair_quote(entity) -> dict[str, int]:
        """One unit of material per authored part, from the creation's design (the kernel's QuoteRepair)."""
        cost: dict[str, int] = {}
        for material in entity.materials:
            cost[material] = cost.get(material, 0) + 1
        return cost

    # ------------------------------------------------------------------ room.undo

    def _op_room_undo(self, principal, message, apply, new_revision):
        """room.undo takes the latest nature changes back by room revision (the kernel's NatureUndo), in Creative only (outside it,
        _check_unsupported has refused it already); building, moving and protecting cannot be undone at all."""
        named = message["args"]["to_revision"]
        if named >= self.revision:
            raise HostError("invalid_args", UNDO_NOT_EARLIER, field_path="$.args.to_revision", actual=self.revision)
        steps = self._steps_back_to(named, self.revision)
        if steps is None:
            if self._latest_sealed(self.revision):
                raise HostError("unsupported_capability", UNDO_SEALED, field_path="$.args.to_revision")
            reachable = self._undo_target(self.revision)
            if reachable is None:
                raise HostError("unsupported_capability", UNDO_ONLY_NATURE, field_path="$.args.to_revision")
            raise HostError("invalid_args", UNDO_NOT_REACHABLE, field_path="$.args.to_revision", allowed=reachable)
        objects = sorted({entity_id for step in steps for entity_id in step.objects})
        if any(self.entities[e].protected for e in objects if e in self.entities):
            raise HostError("target_protected", UNDO_LOCKED, field_path="$.args.to_revision")
        needs = not principal.startswith("player:") and any(step.principal != principal for step in steps)
        knows = self._knows_for(principal)
        affected = [e for e in objects if knows(e)]
        data = {"undone": ", ".join(step.label for step in steps), "undone_steps": len(steps), "requires_approval": needs}
        outcome = {"affected": affected} if affected else {}
        if message.get("preview") is True:
            return {**outcome, "data": data}
        if needs and not self._approving:
            touched = {e: self.entities[e].revision for e in objects if e in self.entities}
            return {"_ask": {"reason": UNDO_HOLD, "touched": touched, "bind_revision": True}, **outcome, "data": data}
        if not apply:
            return {**outcome, "data": data}
        self._undo(steps, new_revision, named)
        return {**outcome, "data": data}

    def _effective(self, revision: int) -> int:
        return self.undone_to if revision == self.undone_at else revision

    def _step_at(self, revision: int) -> UndoStep | None:
        return next((s for s in reversed(self.undo_steps) if s.revision == revision), None)

    def _begin_undo(self, label: str, revision: int, principal: str) -> UndoStep:
        """A durable nature change's undo step (the kernel's BeginUndo): steps that no longer connect to it go, the oldest past 8."""
        step = UndoStep(principal, revision, self._effective(revision - 1), label, self.mode)
        if self.undo_steps and self.undo_steps[-1].revision != step.base:
            self.undo_steps.clear()
        self.undo_steps.append(step)
        del self.undo_steps[:max(0, len(self.undo_steps) - MAX_UNDO_STEPS)]
        return step

    def _steps_back_to(self, target: int, revision: int) -> list[UndoStep] | None:
        steps, goal, expected = [], self._effective(target), self._effective(revision)
        for step in reversed(self.undo_steps):
            if goal >= expected:
                break
            if step.revision != expected or not step.undoable:
                return None
            steps.append(step)
            expected = step.base
            if expected == goal:
                return steps
        return None

    def _undo_target(self, revision: int) -> int | None:
        last = self.undo_steps[-1] if self.undo_steps else None
        return last.base if last is not None and last.revision == self._effective(revision) and last.undoable else None

    def _latest_sealed(self, revision: int) -> bool:
        last = self.undo_steps[-1] if self.undo_steps else None
        return last is not None and last.revision == self._effective(revision) and not last.undoable

    def _remember_object(self, step: UndoStep | None, entity_id: str) -> None:
        if step is not None and entity_id not in step.objects:
            step.objects[entity_id] = copy.deepcopy(self.burn_records.get(entity_id))

    def _remember_plant(self, step: UndoStep | None, plant_id: str) -> None:
        if step is not None and plant_id not in step.plants:
            step.plants[plant_id] = copy.deepcopy(self.plant_state.get(plant_id))

    def _remember_cell(self, step: UndoStep | None, key: str) -> None:
        if step is not None and key not in step.ash:
            step.ash[key] = self.ash.get(key)

    def _leave_ash(self, step: UndoStep | None, at) -> None:
        key = _key(*_grid(at))
        self._remember_cell(step, key)
        self.ash[key] = (at[1], max(ASH_LEFT, self.ash.get(key, (0.0, 0.0))[1]))

    def _undo(self, steps: list[UndoStep], at: int, target: int) -> None:
        """Takes the steps back, newest first (the kernel's UndoSteps): every object, plant and ash cell as before them (no wallet: retired).
        Any fire goes out. The undo is itself a room revision standing for target's state."""
        for step in steps:
            for entity_id, record in step.objects.items():
                if record is None:
                    self.burn_records.pop(entity_id, None)
                else:
                    self.burn_records[entity_id] = copy.deepcopy(record)
                entity = self.entities.get(entity_id)
                if entity is not None and entity.kind == "object":
                    entity.removed = record is not None and record.burned_down
            for plant_id, state in step.plants.items():
                if state is None:
                    self.plant_state.pop(plant_id, None)
                else:
                    self.plant_state[plant_id] = copy.deepcopy(state)
            for key, cell in step.ash.items():
                if cell is None:
                    self.ash.pop(key, None)
                else:
                    self.ash[key] = cell
            if step.sprouts is not None:
                self.sprouts = copy.deepcopy(step.sprouts)
            if step.bridges is not None:
                self.bridges = copy.deepcopy(step.bridges)
        self.undo_steps = [s for s in self.undo_steps if all(s is not taken for taken in steps)]
        if self.fire is not None:
            if self.fire.principal == _COMPANION:
                self._companion_fire_seen = self._weather_s()
            self.fire = None
        self.undone_to = self._effective(target)
        self.undone_at = at
        self.nature_changed_at = at

    # ------------------------------------------------------------------ what fire left blocks the verbs

    def _check_nature_remains(self, principal: str, message: dict) -> None:
        """The kernel's CheckNatureRemains: a scorched creation can be repaired or removed, nothing else, until it is repaired
        (a stock object that burned down is a removed entity, refused like an unknown id everywhere)."""
        op, args = message["op"], message["args"]
        if op in ("goal.set", "goal.stop", "effect.start", "effect.stop", "effect.adjust", "nature.repair"):
            return
        named = []
        if isinstance(args.get("target"), str):
            named.append((args["target"], "$.args.target"))
        for item in args.get("targets", []) if isinstance(args.get("targets"), list) else []:
            if isinstance(item, str):
                named.append((item, "$.args.targets"))
        placement = args.get("placement")
        if isinstance(placement, dict) and isinstance(placement.get("on"), str):
            named.append((placement["on"], "$.args.placement.on"))
        for entity_id, path in named:
            record = self.burn_records.get(entity_id)
            if record is None or (self._view is not None and entity_id not in self._view):
                continue  # what the companion cannot see is refused as unknown where its command looks it up
            if record.creation and record.burn > 0 and not (op == "entity.remove" and path == "$.args.target"):
                raise HostError("target_busy", REMAINS_SCORCHED, field_path=path)

    # ------------------------------------------------------------------ the consequence of a build burnt

    def _note_build_burnt(self, fire: Fire, creation: str) -> None:
        """A build the Gubble's fire burns outside Creative (the kernel's NoteBuildBurnt): one consequence fact per fire, updated in
        place as it burns more, naming a build only as the team knows it; a new fact costs Trust when the fire was paid."""
        if fire.principal != _COMPANION or self.mode == "creative":
            return
        if self._build_fire == fire.revision and self._build_entry is not None:
            if creation in self._builds_burnt:
                return
            self._builds_burnt.append(creation)
            self._build_entry["line"] = self._plain(self._build_burnt_line(self._builds_burnt[0]), 200)
            self._retell_gubble(self._build_entry)
            return
        self._build_fire = fire.revision
        self._builds_burnt = [creation]
        view = self._team_view(creation)
        entity = self.entities[creation]
        box = entity.bounds()
        centre = [(box["min_m"][i] + box["max_m"][i]) / 2 for i in range(3)]
        self._build_entry = self._fact("consequence", self._build_burnt_line(creation), _COMPANION, fire.directed_by,
                                       creation if view is not None else None, view[0] if view is not None else None,
                                       view[1] if view is not None else centre)
        self._add_history(self._build_entry)
        self._tell_gubble(self._build_entry, self._move_trust_for(fire.paid))

    def _build_burnt_line(self, first: str) -> str:
        view = self._team_view(first)
        more = len(self._builds_burnt) - 1
        return BUILD_BURNT_START + ("one of your builds" if view is None else "your " + self._quoted(view[0])) + \
            ("." if more == 0 else f" and {more} more {'build' if more == 1 else 'builds'}.")

    # ------------------------------------------------------------------ nature.describe

    def _nature_describe(self, principal: str) -> dict:
        """The island's nature as the requester's team knows it (the kernel's NatureSnapshot)."""
        rain, wind, pattern = weather_at(self._weather_s())
        sees = self._sees_cell_for(principal)
        knows = self._knows_for(principal)
        burning = sum(1 for _c, surface in self.fire.burning.values() if sees(surface)) if self.fire is not None else 0
        scorched = sorted(e for e, r in self.burn_records.items() if r.creation and r.burn > 0 and knows(e))
        return {"nature_revision": self.nature_changed_at, "trail_enabled": False,
                "weather": {"pattern": pattern, "rain": round(rain, 4), "wind_mps": [_r(v) for v in wind]},
                "planted_cells": 0, "burning_cells": min(MAX_BURNING, burning), "wallet": dict(sorted(self.wallet.items())),
                "scorched": scorched, "can_undo": self.mode == "creative" and self._undo_target(self.revision) is not None, "notice": ""}


