# Traits: what bodies, land, plants and animals are like

Squared with [docs/game/MECHANICS.md](../game/MECHANICS.md) on 14 October 2026: where this page and that design differ, the design wins.

**Status: a first draft for the founder to react to (13 October 2026).** A conversation starter, not a specification; every number is a provisional value to tune by playing. The founder, 7 October: "I want this to be the basis and the inspiration, but it doesn't need to be exact." So traits are principles ("water runs downhill", "tall mountains often have snow") with room for invention, and no island needs every feature.

**Written for the 10× island** (decided by the founder, 13 October). The room is magnified ten times; the body, the Gubble, trees, cottages, plants, stones and water depths keep today's size. The garage island is about 67 × 77 m: 80 seconds to run across, 4 minutes to walk. Its tallest crag rises about 22 m, over 200 body heights: to a person, a 370 m mountain.

Sources: [research/01-natural-history.md](research/01-natural-history.md) and [research/02-traits-in-games.md](research/02-traits-in-games.md).

The player's and the Gubble's numbers (hearts, the bar, pips, knacks, growth) are now held, as provisional values with the founder's answers of 14 October, in [docs/game/MECHANICS.md](../game/MECHANICS.md) (its section 11); where this draft and that document differ, MECHANICS wins. The tables below label what the code holds today and what the design changes.

## In short

1. **A trait is a principle with a starting value,** kept as data. The mode adjusts it, growth raises it, and the AI acts within it but never writes it.
2. **The body is shared; the look is the player's own.** A knack picked at creation moves one number by at most 20 percent, never geometry. The Gubble is a ghost: no stamina, no injury, and unseen by animals; its abilities draw on the team's pips.
3. **The ground decides what stands on it.** Rock holds anything, soil what is levelled, sand needs posts. Mountains are climbed ledge to ledge.
4. **Plants follow light, water, soil and height.** Fire eats them and ash feeds them. Grass returns by itself; trees return when someone plants them.
5. **Foxes, owls and rabbits live where there is food and cover.** In Challenge and Live, the night holds real danger, and every danger has an answer the player can see.

## 1. How traits work

**A trait** is a named quality of a body, a material, a plant or an animal. It has three parts:
- a principle: "sand will not hold a steep slope";
- a provisional value: sand rests at about 32°;
- a visible sign: dry sand pours, while damp sand holds a wall.

**Where traits live:**

| Place | Holds | Today |
|---|---|---|
| **The island's rules pack** (`game/rules/<island>/vN.json`) | The kinds of material, plant and animal, and their laws | Abilities only. Material, plant and animal sections would be a contract change |
| **The island's own facts,** written by the generator | Its snowline, treeline, each landform's rock, sites for animal homes | Snowline (15 m on the 10× garage), rock from each object's colour, planting |
| **The bodies** | The player's and the Gubble's size, movement and vitals | The body profile, the controller's tuning, `PlayerVitals` per mode |
| **The living state** | The ground's moisture, fuel, growth and ash; the animal homes | The nature field (5 cm cells) is built; the homes are new |

**How a value is set.** The island gives the base; the mode turns consequences off (Creative) or on (Challenge, Live), scaled by the Challenge difficulty (Gentle halves every hurt and removes none, Brave adds half: MECHANICS section 8); growth raises it on its own track. The engine's limits always hold and a lock beats everything, as in the magic design.

**The AI acts within the traits.** The Gubble plants, lights, carries, builds and warns. It never edits a trait, spawns an animal or calms one. It learns traits as the player does: from what shows and from the journal. In Challenge and Live, where there is no undo, its mistakes cost the player. Previews, the fire guardrails, the journal's consequence facts and, later, Trust are the guardrails; the keyed "yes" before a build burns exists only in Creative. That is how consequences teach it.

**Two habits from the magic design:**
- **Show the hidden inputs:** wet ground looks darker, and a rabbit thumps before the warren empties.
- **Chance changes when, never what:** dry grass always burns.

## 2. Player characters and Gubbles

### The player (today's values, with the design's; all provisional)

| Trait | Principle | Starting value |
|---|---|---|
| Size | A person in a miniature world | 10 cm tall, 2 cm radius (4 cm across), eyes at 8.7 cm, reach 15 cm |
| Moving | Brisk, because the island is big | Walk 0.32 m/s, run 0.96 m/s, a hop of two-thirds the body's height, a 2 cm step. **Design:** running drains the bar in Challenge and Live (the cost is a pack value, about 40 s of running from a full starting bar); the Quick feet knack lifts walk and run by up to 20% (M1, M5) |
| Climbing | Any face too steep to stand on can be climbed, slowly and on purpose | 0.12 m/s. Free in Creative; uses stamina in Challenge and Live. **Design:** the Steady climber knack gives up to 20% more height for the same bar (M5) |
| Stamina | Enough for a tree at first; mountains come with growth | Counted in metres of climbing: 0.45 m to start, up to 3 m; today's code adds 0.10 m for each new climb of at least 25 cm. Refills on any standable ground. **Design:** 0.40 m to start, ten bands of 0.40 m over two bars to 4.0 m, each opened by five found stamina Fractals (M1 and M4; the per-climb reward retires, open point L). An empty bar drops you from the face, and speed is halved until it refills to a quarter (M5) |
| Swimming | Calmer than walking; you can always get out | 60% of walking speed at the surface; diving is possible. **Design:** deep water and time under water drain the bar; out of stamina in deep water the scene fades and you are back on the shore where you went in (decided 15 October, built), out of breath you float up (built); Strong swimmer and Long breath are knacks (M1, M5) |
| Health | Falls, fire and animals hurt, in Challenge and Live only | 100. A 30 cm drop is safe; 1.5 m nearly knocks you out. Fire close by costs about 12 a second. At zero you wake at the spawn with half health and stamina, keeping traits and creations. **Design:** 5 hearts of 20 (100), drawn in quarters, with +1 heart per five found heart Fractals to a cap of 30 hearts (600; 720 with the Big heart knack); every hurt lands on a whole quarter (M1). A 30 cm drop is safe (the Cat feet knack raises it to 0.45, 0.60 or 0.75 m); fire costs one quarter a second, 20 s to burn five hearts (M1); an owl bites for two quarters and a fox for three. At zero hearts you wake at camp (the spawn today) with half your hearts and half the bar, the pips half full, keeping traits and creations. **[Open K: fall damage absolute, the recommendation, so more hearts survive bigger falls, or scaled to the maximum; the 1.5 m sentence above stays today's until K is answered]** |
| Carrying | One thing in your hands | Up to 0.5 kg |
| Senses | Line of sight, one shared map | Darkness limits sight. **New:** sounds as cues (a thump, a hoot), with a shown direction for players who cannot hear |

Everything above that is not marked **Design** is today's value. The design changes the Moving, Climbing, Stamina, Swimming and Health rows, in the packets M1, M4 and M5 of [RUN-2-MECHANICS.md](../runs/RUN-2-MECHANICS.md); nothing in it is built yet. The Size row is a correction found in passing, not a design change: the capsule's radius is 2 cm.

**Mountains on the 10× island.** The 22 m crag is seven times today's largest stamina (3 m), about five and a half times the design's largest bar (4.0 m), and three minutes of climbing. So a big mountain is climbed ledge to ledge, and each standable ledge is a rest. **Design (MECHANICS section 6, packet M4):** on the climbs the generator offers as routes, each ledge-to-ledge rise is one band, 0.4 m for the first band, and each ledge is a bench the walker can stand on, so the next ledge is always in view of a player one band short (this replaces this draft's first proposal of ledges every 2 to 3 m). One landmark per island is routed to its summit (the summit flag), or its summit stays the Gubble's and the route ends on a shoulder **[Open N]**. Scenic peaks may stay out of reach. Falls become the main danger, so section 3 adds soft landings.

**Growth** is the player's own track, per mode, apart from the Gubble's expertise. Five found stamina Fractals open the next stamina band and five found heart Fractals add a heart; growth is counted, never a currency (MECHANICS section 6). Fractals are found and verified by the game, never granted by a quest or by the AI, and in the recommended default a found Fractal has no kind until the player chooses at a camp whether five become a heart or the next band **[Open C]**. Today only stamina grows: 0.10 m for each new climb of at least 25 cm, a reward that retires when the bands land (M1 and M4; **[Open L]**). *A Short Hike* gates heights the same way, with golden feathers. Speed moves only through a knack. Growth only adds, so a returning player never feels slower.

**Drawn characters.** Every player makes their own (decided), on one shared collision body (decided earlier). **These can vary:** shape, colours, hats and props that don't collide, see-through parts, bounce, sounds and glow colour. **These stay shared:** the collision capsule, size, eye height and reach, and the base numbers: the base speeds, the base hop, the bar's base size and the five starting hearts. A knack, picked at creation, moves one of those numbers by at most 20 percent at full rank, all upside (the table is in MECHANICS section 7; Cat feet, which raises the safe drop height from 0.30 m to 0.75 m, is the founder's one exception to the 20 percent). The generator keeps this fair with two kinds of gate: a band gate no knack opens early, and a knack shortcut sized so that one knack at full rank opens it and the base body does not, with nothing reachable only with a knack **[Open E]**. Then every island fits every character, and no child's drawing is weaker than another's (decision 4).

### The Gubble, a ghost

| Trait | Principle | Today, or proposed |
|---|---|---|
| Body | The player's size, set apart from the world | 10 cm; hovers 2 cm above ground or water |
| Moving | Never climbs, swims or gets stuck | Walks when a route reaches; otherwise floats straight there, over water and up cliffs. It can scout a 22 m summit long before the player can climb it |
| Stamina, health | A ghost neither tires nor is hurt | Proposed: neither, in any mode. Its mistakes cost the player instead |
| Seen by | Its player only | Animals never react to it. Later: other Gubbles see it; other players only with permission |
| Light | Brings light; a torch it holds is an item | Glow; Torch is a Glow discovery (an expertise). No torch in its drawing since the torch round: the torch is a prop today and, once built, a carried item the Torch expertise makes ([RUN-2-TORCH.md](../runs/RUN-2-TORCH.md)) |
| Carrying | Fetches for the player | 2 kg today, four times the player's limit. **Proposal:** in Challenge and Live, only what the player could carry. MECHANICS lists 2 kg for the Gubble, today's value, and does not take up the proposal; open with the founder |
| Growth | Abilities grow by discovery | Expertise (the magic design's discoveries), which travels across modes; one pip of energy per five expertise; Trust later |
| Energy | The pair draws on one purse, and the price follows who directed the ability | Pips, one pool per team per mode: full price on the player's order, half on its own idea; previews and stops are free; in Creative a shadow pool that never refuses (MECHANICS section 5; not built, M1) |

**To settle (decision 5):** animals ignore the Gubble, but perhaps not what it does: its light is real light, and a log it carries drifts through the air.

## 3. Terrain

### Materials

The generator already gives every patch of ground one of these roles, choosing slate or sandstone from each object's colour; fire adds ash.

| Material | Foundation | Dig | Holds a slope of | Climb | Burns | Grows | Over time |
|---|---|---|---|---|---|---|---|
| **Hard rock** (slate) | Best | No (later: slowly, with a tool) | Sheer cliffs | Yes | Never | Moss, lichen | Sheds scree at cliff feet, slowly |
| **Soft rock** (sandstone) | Good | Slowly: steps, hollows, caves | Cliffs, terraces, visible layers | Yes | Never | Moss | Weathers to sand and soil |
| **Scree, gravel** | Poor unless walled | Yes | About 35° | Slips: costs more stamina | Never | Heather, slowly | Gathers below cliffs |
| **Soil** | Fair if levelled; soft when soaked | Yes | About 30°, more where rooted | Walk it up to 45° | Its plants do | Everything | Bare slopes wash down in rain until roots hold them |
| **Meadow** | As soil | As soil | As soil | No | Fast when dry | Grass, flowers | Grazed short by warrens |
| **Moss** | As soil | Yes | As soil | No | Barely when wet | Moss, ferns | Spreads in wet shade |
| **Sand** | Poor: posts or a stone footing | Easiest | About 32° dry, more when damp | No | Never | Reeds at the edges | Waves smooth it |
| **Snow** | None that lasts | Snow caves | About 35° | Slips | Puts fire out | Nothing | Above the snowline (15 m here); lower in winter |
| **Water** | Piles only (the jetty) | — | Always level | Swim | A firebreak | Reeds, fish | Runs downhill to the sea |
| **Ash** | As beneath | As beneath | As beneath | — | Already burnt | Regrows greener | Fades as plants return |
| **Worn path** | Firm | Yes | Gentle | No | Never | Nothing | Stays where feet keep it |

**Soft landings,** because falls hurt and the island is tall: deep water catches any fall (built). **Proposed:** snow, reeds, a shrub's crown and thick moss halve the landing speed, so a child learns to aim for the snowbank. These stack with the Cat feet knack, which raises the safe drop height: the ground halves the landing speed, the knack raises the height that is safe.

### Building

- **Buildings want flat, firm ground:** today, 60% of the footprint supported, with the centre over the support.
- **The ground sets the footing:** rock as it is, soil levelled or terraced, sand on posts, scree behind a wall, water on piles. The ghost draft shows what a spot needs and costs.
- **Slopes** (provisional): build freely up to about 12°, the generator's road grade. From 12° to 25°, terrace or use stilts. Building on cliffs comes later.
- **Failure is slow and readable.** Moving a support always warns first (decided). In Challenge and Live, the world can also take ground away: a bank washes out, posts burn, a hole is dug. **Proposal:** the building cracks and leans before it falls, so the pair can prop it up. What is lost comes back by real work: structures are rebuilt, while loose logs and boxes are simply gone (decided).
- **Build from what is near.** Hauling logs one at a time across 70 m takes minutes. Stone lies below cliffs and timber by groves, as the generator places them.

### How the land forms and changes

- **At import (built):** furniture lifts the land, erosion softens it, water finds the sea, soil gathers in hollows and plants follow. Landforms and the snowline are magnified ten times; streams, paths and plants keep the body's size.
- **During play, land changes only where something disturbed it,** so the island stays recognisable and cheap to simulate. The disturbances:
  - fire, which leaves ash, then regrowth;
  - digging and building;
  - rain on bare soil (proposed): a burnt or freshly dug slope washes down a little in heavy rain, until roots hold it;
  - snow, which comes and goes with the weather and the seasons (they follow the calendar).
- **The player shapes the near land; the mountains stay.** Digging moves handfuls. Moving mountains would be later magic.

## 4. Plants

The generator already plants by these rules:

| Kind | Thrives where | Growth (provisional) | Fire | Animals | The player can |
|---|---|---|---|---|---|
| **Broadleaf trees** | Low ground by streams, groves | A planted sapling is climbable after a few real days | Slow to catch, long to burn | Owls nest in old trunks; foxes den among the roots | Climb; gather fallen wood and leaves |
| **Conifers** | Ridges, high ground and shade, below the treeline (about 14.5 m) | A little slower | Catch easily, burn hot | Owls roost in them | Climb; gather cones and wood |
| **Shrubs** | Grove edges, banks | About a day | Medium | Cover for rabbits; berries for foxes | Hide; gather berries; a soft landing |
| **Grass** | Wherever soil gets light, on slopes under about 34° | Minutes from ash (built) | Fastest when dry and gold | Rabbits graze it | The fire's road when dry |
| **Flowers** | Morning sun, open meadow | Minutes (Bloom is built) | Light fuel | Rabbits eat them | Pick; Bloom (Bloom is Grow since decision 52) |
| **Heather** | High, dry tops | Days from ash | Burns hot | Cover | Walk through |
| **Ferns** | Shade, damp | Often first back after fire | Dry in autumn | Cover | Hide |
| **Reeds** | Where feet get wet | Quick | Barely | Fish shelter below | Hide; a soft landing |
| **Moss** | Wet shade, stone | Slow | Barely | — | Glow-moss in the dark |

- **Nothing grows on bare rock, open sand or water.**
- **Lost plants come back through real work** (decided). Ground cover returns from ash on its own, greener. Shrubs and trees return when someone plants a seed from a living plant, perhaps helped by the Gubble's growth magic.
- **The world keeps living while the player is away** (decided), and planted things keep growing. **Proposal:** cap growth while away at a few real days' worth.
- **Seasons and weather:** broadleaves drop their leaves in autumn, and the dry litter burns readily. Rain slows fire; wind drives it downwind, faster and hotter (decided).

## 5. Animals: foxes, owls and rabbits

### Shared principles

- **Animals live where there is food and cover,** each in a home: a warren, a den or a nest tree. The land sets the numbers. A first guess for the garage island: a few warrens on sunny banks, one or two fox families at the wood's edges, an owl pair per wood.
- **The home holds the traits.** Far from the avatars the island keeps only each home's numbers, as it does for the ground; close by, individual animals appear. While you are away, warrens grow and foxes raise cubs.
- **Animals never react to the Gubble.** They react to the player, light, fire and noise.
- **You can't outrun them, but you can out-think them:** they can't climb steep faces, they avoid deep water, and light and cover keep them off.
- **The food web is real but gentle on screen** (decision 3). Foxes and owls hunt rabbits. The chase is visible, the catch out of sight, and fewer rabbits tell the rest.
- **Signs before sightings:** tracks in mud and snow, holes in a bank, a feather under a tree, kept in the journal.

### The three

Sizes below assume storybook sizes (decision 1).

**Rabbits** (from the European rabbit):
- **Size:** about 8 cm long, waist-high to the player when sitting.
- **Home and food:** a warren in soft soil or sand, never rock, on a sunny bank near shrubs. They graze near their holes at dawn and dusk, keeping a short, flowery lawn that holds little fuel: a firebreak.
- **Senses:** wide eyes and good ears. One thumps and flashes its white tail, and the warren empties.
- **The player:** they bolt from a running player and creep back to a still one. Harmless in every mode.
- **Fire and weather:** they wait underground, then graze the new green on the ash.

**Foxes** (from the red fox):
- **Size:** about 13 cm long plus the tail, chest-high to the player.
- **Food and home:** anything, rabbits and late-summer berries included; it buries what it can't finish. Its den is in a bank or under roots at the wood's edge. It is out at dusk and at night.
- **Senses:** keen hearing and smell. It listens, then pounces into tall grass.
- **The player:**
  - **Creative:** curious and wary. It may carry a small dropped thing off to its den, where it can be found.
  - **Challenge and Live, at night:** it may stalk a player alone in the dark. It warns with a bark, and its eyes shine in the light. A nip costs about 15 health. Light keeps it off, and it won't follow up a cliff or into deep water.
- **Fire and weather:** it runs upwind from fire and keeps to its den in heavy rain.

**Owls** (from the tawny owl; the barn owl is the open-meadow alternative):
- **Size:** about 8 cm tall, with wings nearly twice the player's height.
- **Food and home:** it roosts in a dense conifer and nests in an old broadleaf's hole. It hunts only at night, dropping silently from a perch on small animals, young rabbits included. Its call at dusk says the night has begun.
- **Senses:** superb hearing, so **a still player under cover is safe.** A shadow across the moon is the warning.
- **The player:**
  - **Creative:** a watcher that calls.
  - **Challenge and Live, at night in the open:** it may swoop at a player far from cover or light, knocking them over for about 10 health; on a ledge, the fall is the danger. In spring it defends its nest tree, as real tawny owls do.
- **Weather:** rain dulls its hearing, so wet nights are quieter.

**Fish (built):** they live in ponds and the sea, dart from the player and ignore the Gubble. At 1.5 to 4.5 cm long, they are already near storybook size.

### Alive without punishing a child

- **Danger has a rhythm.** Day is safe; dusk is when the Gubble teaches light; night in the open is the risk, in Challenge and Live. Time follows the real clock today, so evening play meets the owls. Live is designed on an island clock record (MECHANICS section 9: four in-game days to a real day, seasons on the real calendar, a phase offset so an evening-only child still meets night), shaped now as data with no change in behaviour (decision 15, packet M6) and built later.
- **Every danger is announced and has a visible answer:** light, cover, stillness, a climb or water. Nothing hurts in Creative, nothing ambushes by day, and nothing is unsuited to an eleven-year-old.

## 6. How it fits together

**A dry, windy evening.** After three fair days the meadow is gold. For a wish, the Gubble lights a fire whose preview stops short of the player's hut, but the hut is downwind and the wind is rising. In Live the fire outruns the preview and scorches the hut while the rabbits wait underground; rain ends it an hour later. The journal notes "we burned upwind of the hut", and next time the Gubble checks the wind. A week on the meadow is greener, the rabbits are back and the hut is repaired at its material cost.

**A warren on a meadow.** A sunny bank of soft soil, shrubs, and a short lawn full of daisies. The player sits still and the rabbits come out. One thumps and they vanish: a fox trots along the wood's edge at dusk, listens, pounces into the long grass and misses.

**An owl near the camp.** A lantern glows by the pair's half-built shelter on a hill meadow, and the owl calls from the conifers. In Challenge, one more log waits across open grass: straight across is quick and exposed, along the hedge slow and covered. A shadow crosses the moon; the player freezes under a shrub, and the owl passes over.

**A hut on a slope.** On a 15° soil hillside the ghost draft turns amber: "cut a terrace, or build on stilts". On the sand below it says "posts needed"; on the rock shelf beside it, green, but a climb from the path. The player cuts a terrace, and in the next heavy rain the raw bank sheds a little soil onto the beach until grass takes hold.

## 7. Decisions for the founder

**Answered by the founder, 13 October 2026 (evening):** 1: C, storybook size, as one pack number with a key that cycles A, B and C; 2 to 6: as recommended. The same evening the founder asked for chosen physical traits at character creation (faster walking or running, stamina efficiency, one more heart, swimming, breath, jumping, building, crops, hitting); how that squares with decision 4's shared body is worked out in [docs/game/MECHANICS.md](../game/MECHANICS.md).

**Squared with the mechanics design, 14 October 2026:** the chosen physical traits became knacks, which move numbers by at most 20 percent and never geometry (MECHANICS section 7, decision 9); stamina grows by Fractals, not climbs (decision 8; the per-climb reward's retirement is open point L); fire is one quarter a second (decision 10); running drains the bar (decision 7, the founder's answer).

1. **How big are the animals?** Trees (about 0.3 to 0.9 m) and cottages (about 0.4 m) are sized as if the 10 cm player were a person.
   - **A, true to the miniature** (as a real animal is to a person, about ÷17): a rabbit 2.5 cm long, a fox knee-high, an owl smaller than the player. Natural and harmless, but tiny across a 70 m island, with no believable night danger.
   - **B, real-life size** (a 40 cm rabbit beside the 10 cm figure): a rabbit four times the player's height, a fox as tall as many of the trees, an owl with 1 m wings. Awe: walking into a warren, riding a rabbit, hiding from giants. But the ecology breaks (an owl can't roost in a tree shorter than itself), the danger overwhelms a child, and on the 10× island they no longer even match the room (there, a rabbit would be 4 m long).
   - **C, storybook size** (about ÷5, as in Brambly Hedge or *Fantastic Mr Fox*): a rabbit waist-high, a fox chest-high, an owl with wings twice the player's height. Readable on screen, fits trees and cottages, gives night a believable danger, and matches the fish.
   - **Recommended: C,** as one number in the pack, so that one playtest evening can try all three.
2. **How dangerous are animals in Challenge and Live?** Recommended: only at dusk and night, always announced, 10 to 15 health, always escapable. With no animal danger at all, the game is calmer, but the night loses its tension.
3. **What does the food web show?** Recommended: chases are visible and catches happen out of sight. With no hunting at all, it is gentler, but the foxes lose their purpose.
4. **Drawn characters: one shared body, or small differences** (for example, longer legs that jump higher but climb slower)? Recommended: shared for now. Differences are more expressive, but harder to balance, and they invite comparing children's drawings. *Amended 14 October:* the body and its base numbers stay shared; a knack, picked at creation, may raise one number by up to a fifth, never the capsule, size, eye height or reach (MECHANICS section 7).
5. **Do animals react to what the Gubble does?** Recommended: yes to its light, its fire and what it carries; never to the Gubble itself. The alternative is simpler, but then the Gubble's fire would scare nothing.
6. **What keeps living while the player is away?** Recommended: growth, regrowth, seasons and animal numbers, capped at a few real days. A fire left burning runs until the next rain in the weather cycle, and the journal tells the story. Pausing fire while away avoids a shock on return, but makes the world feel less alive.

## 8. A small first build

**Rabbits on a meadow, with two ground traits,** in two packets:

1. **The rules and the place** (Lane C, with the integrator):
   - **Materials as data:** a `materials` section in the rules pack (fertile, fuel, burrowable, footing, soft landing), read by the host in place of today's hard-coded fertility and fuel rule (a proposed contract change).
   - **Warren sites** in the island's facts: soft soil or sand, a gentle sunny slope, shrubs near, away from paths and the hamlet, with a check that each is burrowable.
2. **The warren** (Lanes P and L):
   - **A home in the nature state,** holding its rabbits and its lawn. Grazing lowers the lawn's fuel (a firebreak); fire and rain send the rabbits underground; numbers drift toward what the meadow supports.
   - **The rabbits are its visible life,** like the fish's school: they graze, hop, freeze, thump and bolt, come closer to a still player and ignore the Gubble.
   - **Their size is one pack value,** with a testing key that cycles A, B and C.

**The playtest asks:** does the meadow feel alive, does stillness earn trust without a tutorial, does the lawn show itself as a firebreak, and which size feels right? Foxes and owls follow on the same homes once size and danger are chosen.
