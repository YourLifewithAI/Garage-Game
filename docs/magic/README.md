# The Gubble's magic: abilities, island rules and wishes

Squared with [docs/game/MECHANICS.md](../game/MECHANICS.md) on 14 October 2026: where this page and that design differ, the design wins.

**Status:** a design draft begun on 9 October 2026, updated with the founder's edits and decisions on 11 October. The playable implementation, begun on `codex/gubble-growth-runtime` and now merged into `run2/integration` (`6e41bc7`), includes the five Glow discoveries, expressive touches, Bloom (since renamed Grow, decision 52, with its flower trail retired), weather-driven Fire, mode-specific stamina/health and a player-started wish runner that has since been shelved (13 October: wishes and speech go to the AI already playing, parked as `run2/wishes-live`; the runner stays in the tree, dormant). See [the playtest checklist](PLAYTEST.md) for controls, provisional balance and the exact implementation limits. The baseline mechanics (hearts, the bar, pips, knacks, modes and growth) now live in [docs/game/MECHANICS.md](../game/MECHANICS.md), and their build plan is [RUN-2-MECHANICS.md](../runs/RUN-2-MECHANICS.md). The founder's word for this page's *discoveries* is *expertise* (`GlowProgress.Discoveries` in the code); this page keeps "discoveries", and the screen and the journal say expertise through the design's `names` block. This draft comes from the founder's notes, the kids' first wishes and five research reports in [research/](research/). Decisions the founder has made are marked **(decided)**; everything else is a proposal.

**The founder's decisions (9 October, evening):**
- **Fire is ability six,** built right after Bloom (Grow since decision 52).
- **The wheel works with no AI connected.** The founder: "This is fine for now. It's a placeholder testing system. We need to test them and see what works and what doesn't." So everything here is a starting point for playtests, not a fixed design.
- **The flower trail is on by default, and the player can turn it off.** (Retired with Bloom, decision 52: Grow leaves no trail.)
- **Retire the five geography-era templates now,** keeping the trigger machinery (a Lane P packet, under way).
- **Evaluate and update the key bindings** (see "The keys").

**The founder's clarification (10 October, decided):** consequence-free play belongs to Creative mode. Other modes, including Challenge, give the island's physics gameplay consequences: stamina limits climbing, and high falls, fire, critters and eventually competing players can damage health. Different modes have different rules; see "Modes: what the island asks of the player".

**The founder's answers (11 October, decided):**
- Burnt player/companion creations remain scorched and repairable at their original material costs. Other buildings and objects can burn completely, subject to locks and mode rules.
- Add weather patterns that Fire and Growth respond to. Player/Gubble control of weather is a long-term ability progression goal.
- Both direct player commands and connected AI may cast Fire through the same rules, including previews and confirmations.
- The player starts the wish runner; the game prompts them the first time. Keep the integration vendor-neutral and queue wishes while another runs.
- Start with curated Gubble content for children. All content the Gubble presents to them must be appropriate for an eleven-year-old.
- Try the proposed key bindings in playtesting.

**Checked against the research** once all five reports were in. These were added in that pass:
- the refusal that offers what is possible;
- the instant in-game acknowledgement while the AI thinks;
- fire's keyed "yes" near the player's builds (since 13 October: Creative's one ask before a build burns; Challenge and Live do not ask);
- saved spells, for later;
- a teammate whose words match its deeds (the founder corrected an earlier "magic, not a teammate");
- the key review;
- the wish ledger: wishes no island can grant yet, kept to grow new islands (the founder's idea, 9 October, evening).

## The idea

The Gubble is the player's magic. **Its abilities work through the island's own laws:** the Gubble names an intent and a place, and the land does the rest. "A waterfall here" puts a spring on the high ground, and the water finds its own way down. "Flowers on that hill" seeds the hill, and the sun and water decide where they bloom.

Each island has its own theme and laws: storybook nature magic, a sci-fi island where the magic is tech, a dragon island where you fight, tame, fly or care for dragons. **The engine gives every island a shared foundation:** movement, physics simulation, building, handling things and directing the Gubble use the same underlying systems **(decided)**. The chosen mode determines their gameplay rules and consequences, including stamina, health and injury **(clarified by the founder, 10 October)**.

The player directs the Gubble without words (a tap, a wheel, the number keys) or with a wish in words. Both use the same abilities under the same rules. The game, not the AI, says what happened.

## Principles

1. **The AI places causes; the island's laws make the effects.**
   - The AI never writes the fire's spread, the water's path or where flowers bloom.
   - Infinite Craft, Roblox's 4D schemas and Latitude's Voyage held together because a deterministic rule owner sat under the model (research 2).
2. **One engine verb, many looks.**
   - One light primitive is a wisp-glow on a storybook island, a floodlight on a sci-fi island and an ember sprite on a dragon island.
   - The controls and command path remain consistent across themes (research 3). Undo is a mode rule: it exists only in Creative (decided, 13 October; see "Modes").
3. **Each layer only narrows the one above. A lock beats every island and ability rule.**
4. **The game writes the receipts.**
   - "Done" comes only from the host's receipt.
   - A Gubble that says "OK" without acting is the most common complaint about AI companions (PUBG Ally, Project Sid: research 2).
5. **The same powers by wheel and by wish.**
   - A wish may combine abilities ("a glowing flower path from the jetty to the cottage").
   - It can never use one the island doesn't have.
6. **Darkness hides; light reveals.**
   - In Creative mode, exploration has no injury or stamina penalty. That consequence-free rule applies to Creative only **(decided)**.
   - Darkness can hold glow-moss and shy creatures (research 1), and can conceal dangerous terrain or creatures in Challenge modes. The mode's rules determine the consequences of encountering those hazards.
7. **Show the hidden inputs.**
   - Dry grass looks yellow and wet ground darker; drifting motes show the wind. Otherwise outcomes feel random (research 1).
   - Chance may change how fast something happens, never what happens: dry grass always burns, at a varying speed.
8. **A teammate whose words match its deeds.**
   - The Gubble is the player's own AI: brought by the player, or one day provided by the game (pending funding). It is a teammate that does magic on the player's behalf **(decided, 9 October; it replaces an earlier "magic, not a teammate")**.
   - It writes its own journal notes, so it feels like a buddy rather than rote AI (7 October).
   - What carries over from PUBG's AI ally, whose players mostly read it as a tool (research 2), is narrower: players stop trusting a teammate whose words contradict its actions, and judge it harshly where it is weak (there, combat).
   - So the Gubble never claims what the host has not confirmed, and its role leans on what AI does well (understanding a wish, composing abilities, remembering), not on twitch skill.
9. **Rules are data, never code.**
   - They are pinned and hashed like style presets.
   - No scripts, URLs or paths.
   - No rule text ever comes from the model or from world text.

## Modes: what the island asks of the player

**Decided by the founder, 10 October:** the same island can be played under different rules. Creative mode lets the player explore and experiment without real gameplay consequences. In Challenge and other possible modes, the island's physics has consequences for what the player can attempt and what happens when an attempt goes wrong.

| System | Creative mode | Challenge and other modes |
|---|---|---|
| Climbing and stamina | Stamina does not limit a climb | Climbing uses stamina. The starting player has enough to climb a tree, not a whole mountain; increasing capacity makes greater climbs possible |
| Health and injury | Falls, fire and attacks carry no injury penalty | A high fall, being caught in fire or being attacked by a critter can hurt the player |
| Competing players | No injury penalty | Competing players can hurt one another under the mode's combat rules; this belongs to later multiplayer work |
| Magic and resources | No ability costs, as already decided: the pips are a shadow pool that drains and refills but never refuses, with the prices shown | Costs and available resources depend on the island and mode; abilities cost pips from the team's pool, refilling ×1.5 / ×1 / ×0.75 in Gentle / Standard / Brave, and Live (solo preview) is Challenge Standard for now (MECHANICS sections 5 and 8); physical touches obey its damage and interaction rules |
| Undo | Nature changes can be undone (room.undo) | No undo at all, for the player or the Gubble, building edits included |
| The Gubble's actions | Cannot hurt the player | Hurt the player as anyone's would; the Gubble's AI hears of it and learns (see "Fire") |

**Decided by the founder, 13 October: consequences are feedback.** "Let things like fire or other actions that might hurt a player, hurt the player. This becomes feedback for the AI and there should be a learning mechanism of sorts that helps the AI make better choices later on." In Challenge and Live nothing holds the Gubble's actions for the player's sake; guardrails bound them and the game tells the AI what they did (see "Fire").

**Decided by the founder, 13 October: undo exists only in Creative.** "Being able to rewind any action or consequence removes stakes." In Challenge and Live, room.undo is refused for everything (unsupported_capability), for the player and the Gubble alike, and the Magic panel shows no undo control. *Built:* every nature change remembers the mode it was recorded in, and only a change recorded in Creative can be undone, so switching to Creative opens no back door; a fire lit in Creative that burns on after a switch to Challenge cannot be undone either.

This makes progression affect how the land can be explored. A visible mountain may be beyond the starting player's stamina even though its geometry is climbable. The player must judge the route against their current capacity. The Gubble's magic can help only through abilities the island and mode actually support.

**Designed since, as provisional values ([docs/game/MECHANICS.md](../game/MECHANICS.md) sections 4 to 6):** hearts of 20 health drawn in quarters, 5 at the start and at most 30, the maximum a field; the stamina bar in ten bands from 0.4 to 4.0 m, five found Fractals to a band; healing a quarter every 2.5 s after 10 s without harm, on safe ground; fire one quarter a second; recovery at camp with half. **Still to design:** the detailed rules for Challenge and other modes beyond those, damage from combat and critters (their verbs do not exist yet), and rewards beyond Fractals. *Built for now (today's code):* at zero health the player recovers at the spawn (the camp, until the founder places camps) with half health and stamina; stamina starts at 0.45 m and grows 0.10 m for each new climb of at least 25 cm, to 3 m; health is 100 and fire costs about 12 a second. Traits grow through found Fractals across islands within a mode, as described below. The player body's stamina and health follow a different progression track from the Gubble's magic (expertise and pips).

The shared engine enforces the selected rules for both manual play and AI-directed actions. Its authority checks, locks and performance limits remain in force in every mode; those protections do not make the player immune to gameplay injury. Multiplayer remains later work.

## The four layers

| Layer | Holds | Written by | Merges how |
|---|---|---|---|
| **1. Engine** | The shared movement and physics systems, the command path, the core verbs (walk, jump, grab, release, place, push, stack, observe, undo, stop), the **primitives** with their outer limits, the **measures** costs are charged on, the **meter kinds**, performance caps, the security boundary, the controls | The build | **Fixed implementation and outer limits.** Islands and modes select supported rules within them |
| **2. Island and selected mode** | The theme and its names (magic or tech), its **laws** as typed facts (fire spreads, water evaporates, the snowline, weather), materials, creatures, which abilities exist, and mode rules for stamina, health, damage, progression, meters and costs; the rule for visitors' abilities (later) | A **theme pack** with mode definitions (pinned and shared) plus **the island's own facts**, which the generator writes into the room (its snowline from its heights, its materials from its landforms) | Each mode stays within engine limits. Content is **add-only** (an island adds abilities, never removes walk, undo or stop); physical capacity and consequences depend on the chosen mode |
| **3. Abilities** | One engine primitive each; its limits inside the primitive's; the laws it requires; what it may target; its safety tier; its cost; its look | The theme pack | Narrow only |
| **4. The agent** | What the Gubble may do unasked, each ability's starting tier, spend limits | The engine's defaults, then the player's settings | Narrow only |

**Precedence, highest first:**
1. engine authority, protection and performance limits;
2. locks;
3. island laws and selected mode rules;
4. ability limits;
5. agent rules.

Baba Is You's maker called rule precedence the hardest problem in his game, so it is written down here once and tested (research 1).

Mode definitions belong to the island layer; they are not a fifth authority layer. Each mode is checked against the engine's outer limits. Creative's consequence-free settings are not the baseline that every other mode must narrow: enabling stamina and injury selects different supported rules.

**How an ability tests a law.** Factorio's recipes declare the planet conditions they need (pressure, gravity), and the engine checks them; the recipe runs no code (research 3). An EnFractal ability does the same: "call a spring" requires `water_evaporates: false`; "snow" requires a snowline.

**A schema sketch** for the engine block, an island's rules, an ability and a cost is in [research 3](research/03-island-rules-and-costs.md). It is the starting point for `island-rules.schema.json`. Its `needs_click` field predates the founder's no-click decision and becomes the tiers: T2 (preview, then commit) and T3 (a keyed "yes").

## Growth and touches (proposal)

**Design revised by the founder, 11 October; implementation begins with the progression foundation.** This develops the founder's idea: three Glow points becoming four, then five, with touches the player chooses. Growth means learning to do more with an ability. A touch gives it a personal expression; a touch that moves, burns or freezes something is also a new physical power and needs its own rules. Numerical tuning and the implementation suggestions below remain proposals unless marked decided.

**First implementation, 11 October, separate review branch:** successful self-cast Glow records Lantern in a persistent Glow journal. Both the player and connected companion use the same host checks. The profile keeps discoveries, successful-use counts and a curated Lantern hint; previews, failed casts, held requests and receipt replays earn nothing. The three/four/five allowance rules are implemented and tested, but only Lantern can currently be discovered in play. The other discoveries must gain their actual behaviour before four or five lights can be earned. This trial profile is separate from room saves and undo; it does not yet introduce mode selection or transfer progress between future modes. This branch has not replaced the founder's installed game.

### Learn through small discoveries

The baseline capability of each ability should be useful and enjoyable from the beginning. The Gubble's abilities will grow as the companion and the player use and experiment with each ability. This is not a companion-care chore and neglecting to use abilities does not come with penalties; it also does not define the survival rules of other modes.

In Challenge mode, physical progression opens up the player's ability to explore more places, thus unlocking more of the world around them. These we will call traits. Traits carry on throughout a given mode. For example, the player's initial stamina trait should be enough to climb a tree. They will need to unlock more stamina by completing challenges on various islands. Thus, with time and by exploring multiple islands and by completing various challenges, greater climbs become possible as the stamina trait increases above baseline.

**In the baseline design (14 October, [MECHANICS.md](../game/MECHANICS.md) sections 4 to 7):** the traits are hearts and the stamina bar, kept per mode and per team. A stamina band opens with five found stamina Fractals and a heart with five found heart Fractals; Fractals are found and verified by the game, never granted by a quest or by the AI. Knacks, picked at creation, move one rate by at most 20 percent. The Gubble's own track is expertise (this page's discoveries) and pips, shared across modes.

Learning an extra Glow point will improve the Gubble's magic but this is independent of how the player's stamina trait grows. These two fundamental capabilities follow different tracks. Each ability or character trait, be they baseline traits such as health and stamina or abilities like Glow or Fire, will have rules dependent on the mode being played. This way we set the progression rules. The discovery milestones below are for a proposed magic trial.

**Proposed trial:** Glow has multiple ways to gain new abilities. The system should be designed in a way that rewards experimentation. If certain conditions are met, these are called discoveries. They show up as little pictures and entries in the companion's journal page:

- Lantern: if the player explicitly casts Glow on the Gubble itself.
- Torch: explicitly casting Glow onto a tree, onto a building, or on top of a hill or a mountain creates a brighter Glow point that illuminates the landscape to a greater distance.
- Bioluminesce: Explicitly cast Glow into water. The orb of Glow goes away and a large portion of the water illuminates instead, highlighting any waves that might wash ashore.
- Constellation: placing Glow points close together allows tendrils of light to connect them. Connect the three baseline Glow points to make a basic constellation. The full version, with movable points and editable connections, unlocks after five Glow points are available. **Proposed dependency:** the basic constellation counts as a discovery toward the fifth light; its full version is a later enhancement, avoiding a circular unlock requirement.
- Glowing Bubbles: Once bubbles are designed to stay in place long enough, the glow magic will be able to illuminate the bubbles and move with the bubble. The Gubble will eventually be able to direct where the bubbles go. *Today:* `glowing_bubbles` already exists as a Glow variant in the host (`CommandHostEffects.cs`), though it is not yet a discovery.
- Skylight: if you try casting a Glow point into the sky, it shoots high up and takes on the appearance of a bright star. It casts little light, with a faint increase in nighttime illumination attributable to that source.

Some of these abilities will eventually be able to interact, such as constellation and skylight or constellation and bioluminesce and constellation and bubble. Each interaction will be unique and requires experimentation to unlock these abilities.

The host marks a discovery only after its condition is met. Repeated successful use can lead to hints, provisionally after ten casts; experimentation can reach the discoveries sooner. Both routes are valid. Two distinct discoveries unlock a fourth Glow light; five unlock a fifth. These counts and conditions are proposed tuning values, not a settled progression curve. Repetition may advance use milestones and hints, but cannot award the same discovery twice. The proposed first five discoveries are Lantern, Torch, Bioluminesce, basic Constellation and Skylight; none requires the fifth light. Glowing Bubbles waits for its bubble support. The host records completed discoveries, while the companion's journal presents and comments on them through the age-appropriate content path. There is no separate XP award for token spend, payment or idle following.

**Expertise and pips (14 October).** Every discovery counts as one expertise toward the Gubble's pips ([MECHANICS.md](../game/MECHANICS.md) section 5): the pool that pays for its abilities outside Creative starts at 8, and its cap rises one pip per five expertise learned by either of the pair, to a ceiling of 40 in data. **Only Glow has discoveries in the code so far** (the five above), so the cap reaches 9. The lists for Bubbles, Fire and Grow (Bloom's successor, decision 52) are below, under "Expertise for Bubbles, Fire and Grow"; with them the cap reaches 12.

The player can ignore the pictures and simply play. A small wiggle and “Sweet! Glow up! We've got an extra light!” acknowledges an unlock. No interruption or compulsory lesson. The journal shows what changed and what remains possible here. Anything not yet unlocked might have a field of unreadable text that hints there are more unlocks but doesn't give away the exact trigger for an as yet unlocked discovery. Extra capacity does not place lights automatically or silently increase their reach, size, brightness or duration.

Manual direction and future wishes earn exactly the same discoveries for the same successful outcomes if the conditions are met. A multi-action wish cannot earn a discovery twice. Automatic triggers, idle following, and restored effects don't trigger discoveries. However, a Gubble's unasked actions may earn discoveries (this already matters: from the baseline mechanics on, a cast on the Gubble's own initiative is its own idea, priced at half, and earns the same discoveries under the same conditions). However, the triggers shouldn't be made obvious to the AI companion, either. The game verifies player direction, AI companion actions, and results; the AI cannot award progress or declare a lesson complete.

### Expertise for Bubbles, Fire and Grow (M11, written 15 October)

**The rules every list shares, as Glow's.** The host verifies each condition itself, at a committed cast or from the simulation's own state; previews, refused or held commands, restored effects, idle following and anything the AI says earn nothing. Either of the pair earns them, the Gubble's own idea included, once per team, kept in the team's expertise store under the ability's id (the store's `abilities` map: `glow`, `bubbles`, `fire` and `grow`) and shared by every mode. A fire that hurt the player or scorched a creation earns no Fire expertise. The integrator's first lists for Bubbles and Fire (15 October) were replaced the same evening by the founder's, below. **Hints** follow Glow's: at a team's 10th, 20th, 30th, 40th and 50th successful cast of that ability, the hint of the first expertise in the list's order not yet learned; a hint points, never names the trigger; a list's unlearned entries show as smudged text in the journal. Each new expertise writes its pace line and the Gubble's wiggle line. **The pips' maximum** is 8 plus one per five expertise across every ability (ceiling 40 in the pack): Glow's five reach 9; with full lists for Bubbles, Fire and Grow, 12. Every number below is provisional.

**Bubbles** (`bubbles`; the founder's redesign of the evening of 15 October, MECHANICS.md decisions 55 to 60): one player-sized bubble cast at a spot, like Glow, and where it lands decides what it does. The list so far:

1. `bubble_carry`, **Bubble carry**: the Bubble cast on the player (the ghost first); learned by the first carry that sets the player down safely, by the player's pop, its timer or its deflate.
2. `bubble_boat`, **Bubble boat**: the Bubble cast on water becomes a boat; learned by casting it there (the first boat the player steps into).
3. `bubble_lift`, **Bubble lift**: a bubble led to a higher spot floats up there; learned the first time that works.
4. and 5. Not chosen yet (candidates: a bubble led all the way to the marked spot without popping; Glow cast on a bubble).

None is locked: the price (4, 4 and 6 ordered; 2, 2 and 3 as the Gubble's own idea) is the only gate, and the first one that works teaches it. One Bubble is active at a time; a recast pops the one there.

**Fire** (`fire`; the founder's design of the evening of 15 October, MECHANICS.md decisions 61 to 63), never rewarding harm:

1. `first_fire`, **Fire**: the Gubble learns Fire when the player first rests at the island's starting campfire; until then it has no Fire.
2. `campfire`, **Campfire**: the first firepit the Gubble lights with Fire, which becomes a campfire, a campsite the player can choose to return to (hearts refill faster there).
3. `fireline`, **Fireline**: Fire cast several times along one line; afterwards Fireline is cast by dragging a line.
4. `snuff`, **Snuff**: rain falls on a fire the Gubble started, or the Bubble is cast on a fire.
5. `will_o_wisp`, **Will-o'-wisp**: Fire cast on water; a ghostly flame hovers over the water, a lantern at night and a floating flame by day.

Each needs its behaviour built first (the island's firepits from the generator, resting, the fire line's drag, snuffing, the wisp), so the Fire packet builds the list with them.

**Grow** (`grow`; the founder's list of 16 October, night, MECHANICS.md decisions 75 and 76, which changed the fifth and the unlocks), in hint order; each is a committed Grow cast unless said, and each behaviour works from the start, the first time it works teaching it:

1. `tall_plant`, **Tall plant**: one plant grown to Grow's full size (its cap).
2. `thicket`, **Thicket**: three plants at full size within 1 m of one another (the constellation's idea).
3. `green_ash`, **Green ash**: Grow cast on burnt ground; the ash greens at once instead of over minutes.
4. `rain_garden`, **Rain garden**: Grow cast while it rains; the plants grow twice as much.
5. `waterpad`, **Waterpad**: Grow cast on water grows a large water lily leaf that the player can step onto (it replaced Waterside, "plants by water grow fuller").

**Grow's unlocks** follow its count, as Glow's lights do: two Grow expertise open growing without seeds (flowers and grass on bare soil, what Bloom painted); three open plants that grow across a gap like a bridge, horizontal only for now; five open ivy grown up a wall that the player can climb, for a limited distance (it replaced crops that grow faster). Each waits for its behaviour, since a discovery that promises a behaviour waits until it works.

**Grow's first version (proposed by the integrator on 16 October; agreed by the founder the same morning with one change, fire, as [MECHANICS.md](../game/MECHANICS.md) decision 67).** Facts it rests on: the island's scattered plants are rows in the manifest's plant tables (`plant:<set>-n`), drawn in 8 m tiles, and only trees have collision (a trunk, a hidden pole and one-sided perches); plants do not burn yet (the fire rebuild is the Fire packet's).

- **What grows:** every plant whose root lies in the circle: grass tufts, flowers, ferns, shrubs and trees. The ground's painted grass is terrain and does not grow. Cast at a point within reach (2 m) and sight; the circle 0.5 m (at most 1.0), about a tree or two and the plants round it.
- **How much:** each cast makes each plant a quarter of its natural size bigger. Full size (the cap) is twice its natural size or one band (0.4 m) taller than it started, whichever comes first: a 0.95 m tree is full in two casts at 1.35 m, a 5 cm tuft in four at 10 cm.
- **Climbing:** a grown tree is climbed as trees are today, its trunk, pole and perches grown with it; the climb still costs the bar, and its top perch is at most 0.4 m higher, so it never skips a whole ladder. Shrubs, ferns, grass and flowers grow but are not climbable in this version.
- **Saved:** the grown plants (id and size) in the island's nature save, at most 4,096; Creative's undo takes a cast back.
- **Fire:** fire burns plants, grown or not, as it burns any other object (decision 67): a plant catches, burns as fuel, more when grown, and burns down, gone with its collision, leaving ash. Plants cannot burn today (the scattered plants are not fire's objects yet), so making them burn is part of the Grow packet, not a later one; the founder: "it should burn it as it would any other object". A burnt plant stays gone, like any other burnt object, unless it is replanted (decision 67, replacing decision 52's regrowth); replanting waits for Grow's later unlocks (growing without seeds).
- **Price and names:** 4 ordered, 2 as the Gubble's idea (decision 52). Bloom's trail goes; `bloom` becomes `grow` in v5 (still a draft) with its own primitive, `growth.grow`, in the contract, the mock and the wheel; the store gains Grow's list above.

### Choose a touch

Start with **one optional expressive touch per ability**, selected outside the casting gesture: plain, or one available look. Choices can be swapped freely without spending points. Capacity growth and personal expression do not compete for a scarce currency, and plain remains a complete ability.

| Touch idea | Expression only | A physical version would additionally need |
|---|---|---|
| Sound | A short chime accompanying the cast, with mute and volume controls | Any creature reaction or other gameplay response explicitly supported by the island |
| Air | A curl of visible motes; no force | A supported force primitive, target limits and checks before anything moves |
| Fire | Ember-shaped particles, labelled “Ember sparkle”; no heat, ignition or extra illumination | Actual Fire support, fuel and spread laws, protected-target checks and the applicable preview/confirmation |
| Ice | Frost-shaped sparkles; no cooling, slippery surface or freezing | An implemented freezing ability and the island's material and surface rules |

Sound waits for an audio effects path, which does not exist yet. Physical fire, ice and Grow touches also remain future work. A visual resemblance never grants the physical power. The UI describes the actual result, and a wish for real fire cannot quietly become decorative embers without the player accepting that alternative.

Touches remain inside rendering and effect budgets; “cosmetic” does not mean unlimited. Any extra light must use the light budget. A future physical combination uses the normal command path and all component checks and costs. The game determines previews, confirmations and receipts; learning more never lowers an approval requirement. Gameplay mastery is separate from the document's T0–T3 authority tiers. Locks, protected creations, stop and applicable undo keep their existing protections.

### Learned together, allowed here

The loaded `storybook_wild` pack is v5 (`RoomWorld.DefaultRulesVersion`; its status is draft; v4, which names Grow Bloom, stays byte-exact) and permits **five Glows**, with 2 m reach and at most 1.5 m radius; Bubbles permit two active effects, Fireworks three, Grow one and Fire one. All effects together remain bounded by the engine's eight-active-effects room cap. **Three → four → five lights** is the learned allowance beneath the pack's five; the future island pack allowing at least five now exists. (This paragraph first described v2, which allowed three, and then v4.) Five lights still leave only three room-effect slots for everything else.

Learned progress belongs to this player and Gubble together. When travel exists, they remember it across islands, but each island and mode exposes only the abilities and touches it supports, narrowed by its limits and the remaining shared budget. “We learned five; this island allows three” explains the difference. A new theme does not automatically translate an unfamiliar ability into an equivalent one. **Decided, 14 October ([MECHANICS.md](../game/MECHANICS.md), decision 2):** expertise travels across modes; hearts, the bar, the pips pool and Fractals are per mode. Today the Glow record is kept once per mode, and the teams packet (M3) merges those into one expertise store per team. A copy of a Creative island into Challenge carries the builds, the planted and burnt ground, the journal and the map, and strips undo (decision 13).

Save completed discoveries, use milestones, delivered hints, unlocked steps and chosen touches together with normal progress; resume them without recounting casts or replaying celebrations. Undoing a cast need not erase learning, but repeating or reloading it cannot award the same discovery again. Missing progress in an older save starts at today's usable baseline. This needs a persistence design before implementation, not an AI journal inference or elaborate anti-cheat system.

### Small first version and founder choices

Build the Glow trial first: discovery conditions, both capacity upgrades (three to four to five), journal feedback and save/resume. Begin with that shared progression foundation, then implement the five discovery behaviours above and the full constellation enhancement. A discovery that promises a new behaviour must wait until that behaviour works. Add one freely selectable particle look only when its rendering support exists; defer sound, physical touches and other ability tracks until the trial is shown to work.

1. **How growth is earned:** recommend the one-time discoveries triggered by the conditions mentioned above or through repeated use of the glow ability; The alternative is player-selected challenges which show up in the journal. The companion could leave hints in the journal that don't tell the player what to do exactly, but hint at how certain conditions might be met. Tune pacing by playing before adding more milestones.
2. **Creative mode:** costs and injury/stamina penalties are disabled by decision. *Withdrawn, 14 October:* the proposal to make all growth available at once in Creative. Creative earns expertise under the same conditions as every mode, and the expertise travels ([MECHANICS.md](../game/MECHANICS.md) sections 2 and 8); the expressive touches may stay freely swappable. Challenge mode has its own progression rules there (found Fractals, bands, hearts).
3. **Touch scope:** recommend expression only first, freely swappable; introduce physical combinations as explicit powers later, once their primitives and protections exist.

## The ground layer

Most of the island's magic acts on one shared field over the land:
- **Cells:** about 5 cm, so roughly 10,000 for the garage.
- **Each cell holds:** cover (bare, grass, flowers, moss, sand, snow, ash), moisture, fuel, growth stage, burn stage and light.
- **Layers:** a ground layer and a cloud layer, as Divinity: Original Sin 2's surfaces do.
- **Saved:** the field is saved in room state, so the room stays data and the scene is derived.

Bloom's flowers (retired by decision 52; Grow acts on the island's plants instead), regrowth, snow and fire were all meant as rules on this one field. Growth adds fuel, fire uses it, and regrowth refills it. That is why fire was built right after Bloom: it reuses the field.

## Weather patterns

**Decided, 11 October:** islands have weather patterns, including rain and wind. Fire and Growth respond to those patterns from their first weather-aware versions. Rain wets the ground and suppresses fire; wind influences fire spread, drifting effects and the feel of the landscape. Moisture, available light and the island's growing conditions affect where and how plants flourish.

**Proposed implementation:** one shared weather state feeds the ground, fire and look systems. Conditions change over time rather than each spell inventing its own weather. Save enough weather state to resume the world's conditions consistently. Each island defines its patterns within the selected mode's rules and the engine's limits.

**Long term (decided direction):** abilities can grow until the player and Gubble can influence and eventually control weather. The first weather version supplies patterns for Fire and Growth to respond to; it does not yet give either avatar weather-control abilities.

## The Gubble's first abilities (the storybook island)

The kids' wishes (9 October): a trail of flowers wherever the Gubble walks, fireworks, bubbles, real fire that catches trees and buildings, and help building a house. The founder: light first, because it makes the Gubble immediately and subtly useful; it should be taught when it gets dark **(decided)**.

| | Ability | Engine primitive | Through the land's laws | Tier | First version |
|---|---|---|---|---|---|
| 1 | **Glow** | `light.emit` | A real light source (light comes only from real sources). It lights what it reaches and casts shadows. In the dark it reveals glow-moss and shy things | T0 auto, stop ends it | The Gubble glows; it lights a spot; it follows as a lantern; later it lights the hamlet's lanterns |
| 2 | **Bloom**, now **Grow** (decision 52 of MECHANICS.md: a cast that makes the plants in its circle bigger; the trail was removed, and the cells below record Bloom's original design; Grow's is in "Grow's first version") | `growth.seed` on the ground layer | Flowers only on soil: thick where sun and water are, sparse on dry crests, none on rock, water or sand | The trail is T0 and transient; painting a patch is T1 (auto with undo) | The trail behind the Gubble, blooming and fading after a few seconds (Divinity's "surface on path" is exactly this); **on by default, and the player can turn it off (decided)**. Paint a patch by dragging |
| 3 | **Bubbles** | `particles.float` | One player-sized bubble cast at a spot; the place decides what it is (decisions 55 to 60) | T0 | One bubble at a spot (no stream since the redesign): Later a big bubble that lifts a stone, or carries the a player across a pond or helps them reach high places: physics, not a script. The baseline design ([MECHANICS.md](../game/MECHANICS.md) section 5) names these as Bubbles expertise: **Bubble carry** (a bubble cast on the player that carries them for a timed duration; the ghost preview shows first, the player pops it at any moment with the dive key or X, and a bubble that pops in the air deflates into a slow drift down), **Bubble Boat** (cast on shallow water: a bubble with collision that floats; the player walks in and out and leaves with the dive key, and the Gubble may nudge and direct it, empty or occupied) and **Bubble lift** (a carry that rises, anywhere **[Open B]**). The player marks each result on its receipt |
| 4 | **Fireworks** | `particles.burst` plus `light.emit` | They light the land for a moment; best at night.    | T0 | A burst where you point.        |
| 5 | **Build** | `kit.assemble` (a ghost draft) | Snaps to flat ground, always enterable, sized for 10 cm figures (BUILDING.md) | The draft changes nothing; the player confirms the build | A bridge or ladder, then a cottage |
| 6 | **Fire (decided: right after Bloom)** | `element.ignite` on the ground layer | Needs air and fuel; spreads with dryness and wind; water stops it; burns to ash that regrows green | T2: previews its predicted burn first. In Creative only, one ask before it burns an unlocked build (13 October) | After Bloom (see "Fire" below) |

**Transform and effects: the first rules (proposed).**

*Effects* (Glow, Bubbles, Fireworks, the trail):
- capped in radius, duration and count by the primitive's outer limits and the island's narrower ones;
- light or cosmetic only, never collision (until they reach a certain level, in which case they affect other objects/players); Bubble Boat, a Bubbles expertise, is the first such case, a bubble with collision on shallow water that the player enters and leaves (the dive key leaves);
- an active-effect allowance per room that expands as a trait earned by the player, within the engine's hard performance ceiling;
- stop ends them.

*A transform:*
- keeps the thing's place and roughly its size;
- changes its kind or material only to one the island lists;
- at first, only unlocked movable things and creations, not the land;
- is previewed, then commits, as one undo step;
- stays physical: a log becomes a canoe that floats, a stone a crystal that is a real light.
- Anything that would touch the player's own builds always gets the preview with its stop ring, whatever the ability's usual tier.

**The command surface today.** `entity.transform`, `effect.start` and `style.set` are in the contract, but the host refuses them ("arrives with the first magic"). `capabilities.list` answers an empty list. The first packet fills `capabilities.list` from the island's abilities and accepts `effect.start` for Glow.

## Fire

The kids want fire that really catches trees and buildings. It is the best test of the whole system and the most expensive ability. These simulation rules draw on research 1, with consequences governed by the founder's mode decision; what is built so far is said as such:
- **It needs air and fuel and eats the fuel.** It spreads more where things burn easily, where it is dry and downwind (Noita, Powder Toy, Minecraft). *Built:* spread is a fixed rotating gate per neighbour, wider downwind; it does not yet slow as the fire ages.
- **Fire's danger depends on the mode.** Creative fire has no injury penalty. In Challenge and Live, being caught in fire damages health, whoever lit it, the Gubble included (decided, 13 October: see "Consequences are feedback" below); escape depends on the terrain, the player's capacity and the mode's spread rules. Spread speed and damage need tuning within the engine's limits.
- **Water and wet ground are firebreaks.** Rain from the island's weather patterns suppresses and can extinguish fire; wind and drying conditions influence its spread. Tune the rates with the shared weather and ground systems.
- **Burnt ground becomes ash, and ash regrows,** greener than before (Terra Nil, Breath of the Wild). Ecological recovery does not by itself heal the player or repair damaged creations.
- **What fire does to a thing (decided, 13 October, through the day; this replaces the morning's version).** Locks are always fireproof, in every mode. Creations, the player's or the Gubble's on the player's behalf, survive fire as scorched remains that keep their design: their triggers are off until they are repaired.
  - **Creative:** creations are no longer fireproof, but before any fire, the player's or the Gubble's, burns an unlocked one, the player is asked once, in plain words, whether they are OK with their build burning down. Yes: it burns to scorched remains, and repair is free in Creative. No: the fire is still lit and the burn stops at the build.
  - **Challenge and Live:** nothing asks. The player's own fire burns the player's creations with no warning and no ask: "This is part of learning the consequences of the actions you take." The Gubble's fire reaching the player's unlocked creation does not ask either; it just burns. Repair costs the original materials.
  - The island's own stock and generated objects burn down completely; what burned keeps its manifest id and the pose it burned at, which is what a later rebuild needs, and the manifest is never edited. A burnt tree's climbing collision goes with the tree.
  - *Built:* one table in the host, `FireRule`, holds a row per kind of thing and mode (what fire does, and whether it asks first), and every fire decision reads it (the preview's targets, the spread, what catches and the ask), so the next change is one edit. **One ask, never two:** the host decides every reason to ask at once and holds the command at most once; the player's yes commits it as approved and the player's no commits it with the builds spared (it approves nothing), so neither can ask again. The Fire preview only says the ask will come, and the hold-to-light gesture is the casting gesture every fire has. A creation the fire may not burn (spared, or built since it was lit) stops the burn as a lock does.
  - *Not built yet (the coming fire rebuild):* what becomes of the island's own things later (structures rebuild, plants replant with regrowth time, loose boxes and logs just go away). How undo treats burns beyond "only in Creative" waits for the founder.
- **Consequences are feedback (decided, 13 October).** The founder: "Let things like fire or other actions that might hurt a player, hurt the player. This becomes feedback for the AI and there should be a learning mechanism of sorts that helps the AI make better choices later on." This replaces the morning's rule that held the Gubble's fire for the player's yes when it would reach the player. *Built:*
  - **Guardrails (provisional numbers in `NatureLaws`):** the player's own setting "Let the Gubble use fire" (in the Magic panel, saved in the player's profile, on unless the player turns it off; the Gubble can never change it, and its Fire is refused with permission_denied while it is off); one fire of the Gubble's own at a time; a 20 s wait after its fire goes out (rate_limited, with the seconds left). The player is never slowed by them: the player's own Fire puts the Gubble's out and is lit. The player's Stop (X) puts every fire the Gubble lit out at once, whatever any budget says. After an injury recovery fire cannot hurt the player for 5 s, and the recovery point is never inside a burning area: the ground around it is put out and stays out for the grace.
  - **Feedback the AI can see:** the Fire preview lists the avatars inside its area, as far as the caster perceives them (an avatar the Gubble cannot see is never named). When the Gubble's fire hurts the player, the Gubble's next result, of any kind, carries a fixed-format notice: what happened, which fire, the player's health before and after, and whether they had to recover. No prose from anyone. It is delivered once; until then any query returns it.
  - **Learning across sessions:** the journal is the AI's memory. The host writes one fact per fire that hurt the player, in the game's own words ("The Gubble's fire burned you near "the jetty"."), naming only places the team knows, kept when routine entries are trimmed. journal.read with kind "consequence" reads these lessons back, and the Gubble is told to read them when it starts (a change to its MCP instructions, with the contract change for the new kind and the notices). *Planned (packet M8 of [RUN-2-MECHANICS.md](../runs/RUN-2-MECHANICS.md)):* consequence facts extend to a player's build that the Gubble's fire burnt (today only a hurt to the player writes one), and Trust, a stat the game computes from these facts and from the player's marks and never one the AI writes, reads them.
- **Caps:** at most 200 burning cells, the pack's radius (1.5 m today) and lifetime (60 s at most), and one fire on the island at a time (the player's and the Gubble's budgets sit on top). *Not built yet:* a visible fizzle to smoke when a cap is reached.
- **Time away (decided, 13 October).** Fire never rests: burning cells keep simulating wherever they are (they are few and bounded), and so does the ground inside a fire's circle. Everything else (flower lifetimes, regrowth, moisture) rests farther than 12 m from both avatars and catches up when one comes back, replaying the weather it missed in the simulation's own steps, so the result is the same as having simulated it, spread over a few frames (at most 8,192 cell steps a step; a whole weather cycle that changes nothing is skipped). The missed time is saved with the room. Time the game was closed is not caught up.
- **Locks are fireproof,** with a visible shimmer. "Spreads" and "destroys" are separate flags (the GriefPrevention plugin's model). *Not built yet:* the shimmer, and the separate flags (the rule table above stands in for them).
- **One fire groups its world changes into one undo step.** Every cell and object it burns is recorded under the action that lit it, and in Creative room.undo takes the fire back with the room revision before it (in Challenge and Live there is no undo). The preview shows the fire's circle, the ground that could catch, the things it would reach and the avatars inside it; it does not predict which cells will burn (no random generator is seeded from the room's revision: the spread is the fixed gate above). Undo never gives back health.
- **A fire budget per principal,** so the Gubble cannot light fires over and over. *Built:* see the guardrails above; each fire still needs a fresh preview.
- **Near the player's creations:** in Creative the one ask above; in Challenge and Live none. A locked build is never at risk.
- **Who may cast (decided, 11 October):** direct player commands and connected AI both use Fire from its first version. Both go through the same host rules and protected-target checks, and since 14 October the price follows the principal: Fire costs 6 pips when the player orders it and 3 as the Gubble's own idea; AI intent never supplies the required player confirmation.
- **Still open:** the repair interaction, damage and recovery rates, the guardrails' numbers, and how undo treats burns in Creative (the founder is deciding). The fire's spread by fuel and wind, and the island-object outcomes, are the coming fire rebuild. The burn outcome by mode, consequences as feedback, undo only in Creative and weather support are decided.

## Triggers: small laws the player and the Gubble make

"When X, do Y": "when I walk onto the jetty at night, light the lanterns."
- **Keep the machinery.** The creation kernel already compiles small node graphs (proximity, timers, light); keep that.
- **Retire the relics.** The geography era's five templates go (the rescue pad, sensor lantern, spinner, storm glider, updraft totem) **(decided by the founder: they would trip us up)**.
- **Place, test, run.** The Gubble places a trigger, tests it in a dry run that the host plays out in preview, and turns it on.
- **Bounded.** Triggers use only the island's abilities, have a cap per room and a rate per trigger, and are saved with the room.

They are the player's own laws for their island, on the same layer as the island's, only smaller.

## Directing the Gubble without words

From research 4 (Apex's ping, Pikmin, marking menus, Valheim, Dragon Quest Builders 2). **Tap does the smart thing; hold shows the choices.** One icon set and one numbering serve the wheel, the number keys and a hotbar, so each teaches the others.

**Built (10 October, the Glow round):** the smart ask on the right button, the wheel, 1 for Glow, Q for the recall, X for stop and [ and ] for the F4 view (shelved on 13 October: the game has two views, first and third person, and `[` and `]` are free) are in `run2/integration`, sent as the player's commands through the host, with no AI needed **(decided: the wheel works with no AI connected)**. Before the round, keys 1 to 5 sent follow, stay, come, stop and point.

### The keys: today and proposed

**Playtest decision (11 October):** try the proposed bindings below. This approves the playtest, not every binding as final; record conflicts and comfort issues after playing.

**How the keys work today** (`RoomHud._UnhandledInput`, `SmallPlayerController`):
- every key is a hard-coded physical key (the same place on any keyboard layout);
- none can be remapped;
- the help panel's text is written separately from the bindings.

| Key | Today | Proposed | Why |
|---|---|---|---|
| W A S D, Shift, Space, Ctrl | Move, sprint, jump (rise when diving), dive | Unchanged | |
| Mouse, left button | Look (F1, F2); orbit (F2 zoomed out past 0.8 m; was F3); the left button captures the mouse | Unchanged, plus aim. The reticle shows what "this" is | |
| Right button | Unbound | **Tap: the smart ask. Hold: the Gubble wheel** | The Gubble's own button (Apex's ping) |
| 1 to 5 | Follow, stay, come, stop, point ahead | **Glow, Grow, Bubbles, Fireworks, Build** (2 was Bloom until decision 52; 6 is Fire, decision 77; hold for the ghost, release to cast; unlearned ones shrug) | The orders move to the smart ask, the wheel and Q; "point ahead" becomes the smart ask's "go and look there" |
| Q, E | Turn the F4 view | **Q: come, then follow** (the recall). **E: the interaction key** (decision 90: the camp, a Fractal, a carried thing, food as it comes; round 2's P2) | The recall is the most-used order, next to W A S D |
| [ and ] | Unbound | **Turn the F4 view** (and drag with the middle button). Shelved on 13 October with F4: unbound again | Q and E move to the Gubble |
| X | Unbound | **Stop** the Gubble's action or countdown | |
| Z | Unbound | **Rest** by a lit campfire (decision 70; was Undo, which lives in the Magic panel, Creative only) | A second key for the camp for one round (decision 90) |
| Enter | Unbound | **The wish box** | |
| F, V | Pick up or put down, push | Unchanged | The player's own hands |
| B, R | Home to the jetty, recover | Unchanged | |
| Mouse wheel | Zoom (F3; since 13 October F2, from the shoulder out to 8 m) | Zoom; **while a ghost shows, it turns the ghost** | |
| F1, F2, O | Views (first person, third person), observe | Unchanged. F3 and F4 are shelved (13 October) | |
| H, C, Esc | Help, customise, release the mouse | Unchanged | |
| G | Next world physics (the founder's low-gravity leaps) | Unchanged for now; listed under "testing" in help; later a setting | |
| T, Shift+T, L | Time of day, season, lamps | Unchanged for now; listed under "testing" in help (time follows the real clock); later a setting | |

**Two steps, so nothing the founder plays loses its key in between** (both done: step 1 in PR #14, step 2 in the Glow round):
1. **Now (no change in behaviour):**
   - every binding moves into named input actions in Godot's input map, still physical keys;
   - the help panel reads its text from the map;
   - a test checks that no two actions share a key in the same view.

   That gives remappable keys (P5) and makes the next step a change of data.
2. **With the wheel and Glow:** switch to the proposed defaults above. Moving the orders off 1 to 4 before the wheel and the smart ask exist would leave them with no key.

Every key stays remappable.

**The wheel:** eight fixed slots, never reflowed, because muscle memory needs them fixed. Eight is within the measured limit for reliable flicks.

```
              1 Glow
     3 Bubbles      4 Fireworks
 Come (Q)                  Stay
     Fetch          2 Grow
              5 Build
```

- Abilities not learned yet show a dim "?".
- The target freezes when the button is pressed, so moving to a wedge doesn't change it.
- A ghost of the hovered ability shows at the target.

**The smart ask** (the first match wins; the reticle shows its icon before the press):
1. Aimed at a draft or a countdown: adjust it or stop it.
2. Aimed at the Gubble: switch between stay and follow.
3. Aimed at a carryable thing: fetch it (not a Fractal: a Fractal's pickup is the player's alone, so the Gubble may find Fractals and mark them as leads on the minimap, never gather them; [MECHANICS.md](../game/MECHANICS.md) section 6).
4. The target is dark and Glow is learned: light it.
5. Otherwise: go and look there.

**What the player sees:**
- **An acknowledgement within 100 ms:** a chirp, the Gubble's eye turns, and a thought bubble shows the ability's icon.
- **A tether** from the Gubble to the target, plus the ghost.
- **A draining ring** for preview-then-commit.
- **A happy wiggle** on success.
- **A refusal:** a head-shake, a reason icon and short optional read-aloud text ("no soil here for flowers").
- **A status chip** in colour and shape (following, staying, busy, waiting for you, can't), and an undo strip of the last three changes.

**Teaching at the moment of need.** Tutorials help little for mechanics players can discover (Andersen et al., 45,000 players). Navi is the warning. So:
1. The first time it gets dark around the player (the real clock's sunset, or a cave), the Gubble shivers and dims, and a sun pulses in its thought bubble.
2. Slot 1 unlocks with a sparkle. One key cap ("1"), the right-button icon, the word "Light!" and a sound.
3. The Gubble points at a dark nook hiding something lovely; lighting it leaves a lasting glow.
4. If ignored, it asks once more, then waits for the next dusk. The tip goes into the journal, to read again.
5. The other abilities follow the same pattern when they are useful:
   - Grow among a meadow's plants;
   - Bubbles by water;
   - Fireworks at a celebration (a build finished);
   - Build when the player asks for shelter.

## Building a home

Dragon Quest Builders 2 is the closest game: the player lays out a blueprint and the villagers build it. [BUILDING.md](../companion/BUILDING.md) is the design: kit pieces, never geometry; placing by relation; always enterable; the player confirms. On the island, "on top of the big box" becomes "on the butte".

1. Wheel, then Build, then a short shelf of blueprint icons: cottage, bridge, ladder, fence.
2. Pick the cottage. The Gubble flies to the spot and draws a see-through ghost, snapped to flat ground and sized for the figures. Where it can't go, it turns grey with a reason icon.
3. **Adjust it:**
   - hold the left button on the ghost to move it;
   - the wheel turns it, and Shift places it freely;
   - big icon buttons make it bigger or smaller, or change its style (storeys, roof, the island's kit).
4. Enter or the tick confirms. The Gubble builds it piece by piece, visibly. X stops it, and Z undoes it afterwards.
5. In words, "make it two storeys, against the cliff" edits the same draft.

None of this is built yet: not the draft in the host, the kit (Lane L's art), the ghost material, the assembly or the snapping.

## Making a wish

From research 5.

**Shelved, 13 October (decided):** wishes and speech go to the AI already playing (parked as `run2/wishes-live`), and the grown-ups' test mode for the AI's own words sits behind a password or other check on the Wishes page. The runner described below stays in the tree, dormant. Everything in this section is the design as it stood before the shelving, kept for when wishes return.

**What doesn't work.** No MCP feature reliably wakes the player's model when the game has a wish:
- **Sampling** is deprecated in the current specification (2026-07-28), and few clients support it.
- **Notifications** don't start a model turn.
- **A waiting tool** costs tokens while idle: roughly 1.5 to 2.5 million cached tokens an hour, and agents stop looping unpredictably.

**What does: a wish runner.** It is a small local program with no model of its own. It waits on the game without making model calls. For each wish it starts one turn of the player's chosen compatible harness with only the game's tools, and it resumes the same session where supported, so the Gubble remembers. Use a persistent process where the supported client allows it, or one invocation per wish otherwise. A CLI and an SDK can have different authentication terms; they are not interchangeable subscription routes.

**Startup and compatibility (decided, 11 October):** the player starts the runner. On first use, the game prompts them to choose, connect and start their AI; the prompt does not itself launch a program. The shared protocol is vendor-neutral so compatible AI systems can connect through adapters. The core experience must not require a vendor-only channel, webhook or other extension.

**Queueing (decided, 11 October):** a wish submitted while another runs waits in the queue. Process queued wishes in order; submitting another wish does not implicitly interrupt or merge with the active one. Stop remains a separate immediate control.

### Provider compatibility: initial research, 11 October

The founder's working assumption is to continue designing for player-owned subscriptions. Treat that as provisional for each provider and adapter, not a blanket finding that every integration is permitted.

- **Codex:** official documentation supports [non-interactive automation using saved CLI authentication](https://learn.chatgpt.com/docs/non-interactive-mode#authenticate-in-automation) and [ChatGPT subscription sign-in](https://learn.chatgpt.com/docs/auth). OpenAI also documents [ChatGPT plan usage for open-source and locally hosted applications](https://developers.openai.com/siwc/token-sharing-open-source), with a separate inquiry route for paid or remotely hosted applications. These establish supported integration paths; the exact EnFractal distribution and account setup still need matching to the applicable terms.
- **Claude Code:** Anthropic explicitly permits [running its unmodified CLI in another product under conditions](https://code.claude.com/docs/en/legal-and-compliance#can-customers-offer-claude-code-in-their-products): the developer agrees to its Commercial Terms, preserves native authentication choices, and each end user signs in and is billed directly. EnFractal must not collect or intermediate Claude credentials. The [Agent SDK's authentication guidance](https://code.claude.com/docs/en/agent-sdk/overview#get-started) separately restricts third-party consumer-account login, so using the SDK is not automatically the same route. [`claude -p` is documented](https://code.claude.com/docs/en/headless), but its authentication behavior must be checked for the chosen version.
- **Children and account eligibility:** curated game dialogue does not establish permission to use a consumer subscription. [OpenAI's consumer terms](https://openai.com/policies/terms-of-use/) require at least age thirteen and prohibit account sharing; [Anthropic's consumer terms](https://www.anthropic.com/legal/consumer-terms) require at least eighteen and prohibit making an account available to others. An appropriate integration for children needs its own provider/account eligibility review; a parent's sign-in alone does not establish it.

Other providers remain unverified. Keep the adapters replaceable so a supported API, local model or other permitted route can use the same game protocol. This research does not change the player-started, vendor-neutral design or authorize paid usage.

```
Wish box ──the player's words──▶ Game host (wish queue) ◀── runner role ── Wish runner (no model)
   ▲  the Gubble's words, receipts, progress      ▲ companion role              │ starts one turn per wish
   └───────────────────────────────────────── MCP server ◀── stdio ── the player's own harness
```

**The code it needs:**
1. **The wish box** in the HUD. The fixed commands are handled locally first ("follow me" never needs the AI).
2. **A wish queue in the host.**
   - The wish is stored as the player's own words (trusted intent, kept apart from world text): one line, at most 280 characters, cleaned by the text rules.
   - It keeps the entity under the crosshair, as an id, never a name, so "there" means something.
   - It becomes a task in the journal.
   - Later wishes wait their turn; stop bypasses the wish queue.
3. **Link roles.**
   - The runner connects in its own role, whose principal the game assigns. That role can read wishes but can't change the world.
   - The companion's role is unchanged.
4. **Contract additions:**
   - `wish.wait` and `wish.update` (runner role only);
   - `companion.say` (the Gubble's words: a caption over its head, journalled as the AI's; child-facing content uses the curated presentation path below). *Unbuilt:* the AI's own spoken words wait for this op, behind a grown-ups' password;
   - a `role` in the link's hello frame.
5. **`capabilities.list` from the island's rules and learned abilities,** so the AI knows what it can do here, what it can't, and why not. It exposes supported actions and effective limits, not undiscovered trigger recipes.
6. **The runner** (`companion/`):
   - harness templates as data, like `clients.json`;
   - the wish on standard input, never through a shell;
   - a check that the harness has only the game's tools before the wish is sent;
   - caps on time, turns and spend;
   - stop.
7. **Each command the AI sends while executing a wish is tied to that wish by the host's own records,** never by an id the model supplies, for per-wish budgets and the provenance rule. Future autonomous experiments need their own host-verified permission context, as allowed by the player's settings; they do not pretend to be player wishes. The same records decide the price ([MECHANICS.md](../game/MECHANICS.md) section 5): every Gubble command sent while a wish runs is charged as the player's order, whatever its action id or tag, because a companion-written tag could be left off to pay less.
8. **Tests:**
   - a sign saying "make a wish" never makes one;
   - a wish never approves a held command;
   - no world text ever reaches the runner's prompt;
   - the runner's role can't touch the world;
   - an extra tool aborts the run.

**While the AI thinks.** A bring-your-own AI may take seconds. The game acknowledges a wish the moment it is queued:
- the Gubble turns and a thinking bubble appears;
- the wish shows as pending;
- the game's reflexes (stop, undo) keep working;
- "done" waits for the host's receipt; until then the claim reads as "still thinking" (research 2).

**When a wish can't be granted.** The Gubble says so with the host's reason and offers what it can do: "I can't make a dragon yet. I can: [bubbles] [fireworks]" (research 4). Roblox's Wish Master found that players wish for things that don't exist; whether to offer the nearest lawful substitute is for playtests to settle.

**Saved spells (later).** A wish that worked could be kept in the journal as a named spell to cast again, without the AI, where the player aims: Infinite Craft's "first discovery", the AI's compositions becoming the player's own magic (research 2). It needs the ability set to settle first.

**The kids (decided, 11 October):**
- **Curated Gubble content first, appropriate for an eleven-year-old.** This applies to captions, speech, journal notes, discovery hints and explanations of ungranted wishes. An AI-written journal is not a separate route for unrestricted prose to reach a child.
- **Proposed presentation:** approved lines and templates with constrained, game-verified fields. Arbitrary AI output and world text are not inserted into those templates. The host's facts can still say what happened; sanitizing text alone does not make its content age-appropriate.
- Any later proposal for generated prose must retain the same age-appropriateness requirement. A grown-up switch would not waive it.
- **The wish's text goes to the selected AI provider.** Setup must explain this and use an eligible provider/account route; content curation and account eligibility are separate requirements.
- **Where this stands (13 October).** `companion.say` is unbuilt. The AI's own words are a grown-ups' test mode on the Wishes page behind a password or other check, and children get curated lines ([RUN-2-STATUS.md](../runs/RUN-2-STATUS.md), the eleventh session's decisions), pending the founder's confirmation. One path for the AI's words exists today: the `journal.note` command, whose notes appear on the journal page marked as the companion's own and untrusted ([JOURNAL.md](../companion/JOURNAL.md)); how they are presented to children is not decided **[Open]**.

**A wish benchmark:** the kids' five wishes, scripted. Each AI client is scored on the ability it picks, its parameters, its receipts against its claims, and how it handles a refusal. This is the acceptance test, as the house conversation was for building (research 2).

**One path for every AI.** Whether the player brings their own AI or the game one day provides one (the founder: pending, as it would need outside funding and real infrastructure), it comes through the same runner and the same MCP surface. A provided model gets no special path and no extra powers.

## Wishes no island can grant yet: the wish ledger

**The founder's idea (9 October, evening).** Wishes one island can't grant could become a database of requests. It would feed a process that builds islands able to grant them, eventually with a model that can build those islands safely. It is not for now, but the system should be in place for when models can do it.

**What the game can keep from day one** (local, small, alongside the wish queue):
- **For each wish that wasn't granted, or was granted only in part:**
  - the player's words;
  - the island's theme pack and version;
  - why: the host's refusal codes from the commands that were tried (trusted), and the AI's own label when it sent no command, such as "no ability for this" (kept, marked as the AI's);
  - what was offered instead, and whether the player took it.
- **Granted wishes** count too, as the measure of what already works.
- **The ledger stays on the player's machine:** in `user://`, never in Git, never sent anywhere. Sharing it comes much later, opt-in and with a parent's consent, because these are children's words ([SHARED-IMPROVEMENT.md](../ideas/SHARED-IMPROVEMENT.md)).

**What it can grow into:**
1. **Missing content.** Wishes cluster into what is missing: an ability, a creature, a kit, a law ("ride a dragon", "make it snow", "a slide down the mountain").
2. **Two queues.**
   - **Content** an island can add as data: theme packs, abilities on existing primitives, creatures, kits. A model can build these, because a theme pack can't carry code.
   - **Engine work** a wish needs that no primitive covers yet (flight, say). That stays engineering, by people or, later, by models through code review.
3. **A safe pipeline for a model that builds islands.**
   - The model proposes a theme pack as data.
   - The pack validator checks it: settings only narrow, limits stay inside the primitives', there is no code, and the text passes the text rules.
   - The original wishes are replayed against it as a benchmark.
   - A person reviews it, and it is pinned like a style preset.

   "Safely" comes from the layers: a model can only write what the engine already knows how to check.
4. **The sea as a way to find them.** A wish this island can't grant could point to an island that can: a dragon island on the horizon, a ferry from the jetty (RUN-2-OPEN-SEA.md, "Later"). An unanswered wish becomes a reason to travel.

**What to build now:** only the ledger record, with the wish queue (build step 2). Everything after it waits for shared islands and for models that can do the work.

## Costs

**(Decided)** No ability costs in Creative mode. In Challenge, cooperative and competitive modes, costs differ by island: summoning a dragon costs a lot; building a spaceship and shooting blasters have their own costs. The mode distinction also governs physical consequences: climbing consumes stamina and hazards can damage health, as described in "Modes: what the island asks of the player". Stamina and health are player state; the shared pip pool below does not make either a communal resource.

How one engine checks every island's costs (research 3):
- **The meter kinds belong to the engine; the island names them:**
  - a regenerating pool, like Fortnite's energy;
  - a stock of materials;
  - a bond with one creature (a dragon's trust gates riding; it isn't spent);
  - a cooldown;
  - a consequence (magic that breaks the island's laws costs extra, after Mage's paradox).
- **The baseline price is flat and whole (decided 14 October; [MECHANICS.md](../game/MECHANICS.md) section 5):** a price in whole pips per ability from an optional `cost` in the island's pack (an ordered price, always even, and an own-idea factor of 0.5), charged once at commit; previews, stops and refused commands are free. The player's order pays in full and the Gubble's own idea pays half, decided by the game from the connection a command arrived on, never from anything inside it: Glow costs 2 pips ordered and 1 as its own idea, Fire 6 and 3.
- **One pool per team per mode,** for the player and the Gubble together, so there is no second purse to shift a bill into. One pip returns every 5 s while the Gubble is within 2 m of the player, one every 15 s apart; at camp the pool wakes half full; the cap starts at 8 and rises one pip per five expertise, to a ceiling of 40 in data. An empty pool refuses with `energy_exhausted`, carrying the balance and the price.
- **Measured-by-effect pricing and running totals** (a price charged on what the action did, a base fee plus a rate, a price that rises with use over a time window) are later options per island, not the baseline.
- **Undo** exists only in Creative, where nothing is charged, so the earlier rule that undo refunds no more than was paid is dropped.
- **Creative mode turns ability costs and injury/stamina penalties off** (the pips become a shadow pool that drains and refills but never refuses, with the prices shown), while the engine's performance caps still hold.

## Wilder islands (later)

A theme is a pack of names, looks, laws, creatures, meters and abilities over the same primitives:
- **Sci-fi:** Glow is a floodlight drone; Bubbles are force-field orbs; Fireworks are plasma flares; blasters are a `force.project`; a ship's flight is charged when its assembly gains it.
- **Dragons:** `creature.summon` at tier 4, a trust bond per dragon, and feed, groom, ride and spar.
- **Visitors' abilities (multiplayer, later):** a visitor's ability is translated by its primitive, refused, or charged extra where it breaks the local laws (The Strange's recursions, Mage's paradox).
- **Where an island's theme comes from:** a setup question, with the room's contents as a hint.

## Safety

The command surface's rules hold unchanged:
- The principal is assigned by the adapter, and no request carries an approval.
- `protect.unlock` stays player-only.
- Every name, label and sign is data.

New with magic:
- the wish is the one text treated as intent, and only the wish box makes one;
- theme packs carry no code;
- destructive targets trace to the player's words or pointing;
- fire obeys locks, caps and the applicable world-edit undo; injury and recovery follow the selected mode's rules.

## Build order (proposed)

0. **Cleanup (done, PR #13).**
   - Retire the five templates and the old invention workshop UI, which nothing shipped reaches.
   - Give the tests neutral fixtures.
   - Keep the trigger machinery and the invention runtime that runs creations.
   - About 33 files reference the templates: the creation compiler, the contract examples (the spinner is the standard sample creation), the companion tests and the GDScript suites.
0b. **The keys into an input map** (done, PR #14).
1. **Glow, end to end, with no AI needed** (built in the Glow round, [RUN-2-GLOW.md](../runs/RUN-2-GLOW.md); the founder's playtest next).
   - A first `island-rules.schema.json` and the `storybook_wild` v1 pack, with its ability list.
   - `capabilities.list` and `effect.start` for Glow, within its limits.
   - The Gubble's glow look (Lane L).
   - The right button's smart ask and the first wheel wedge, with the new key defaults.
   - The dusk moment.
   - The acknowledgement, from the receipt.
2. **The wish path** (shelved 13 October: parked as `run2/wishes-live`, the runner dormant in the tree).
   - The wish box, the queue, the link roles, `companion.say`.
   - The runner, with Claude Code and Codex templates first.
   - The wish ledger's local record.
   - The wish benchmark.
3. **Bubbles and Fireworks** (effects; no ground layer needed).
4. **The ground layer and Bloom** (the trail, then painting; Bloom is Grow since decision 52, and the trail is retired).
5. **Build:** the draft, a small kit, a bridge, then a cottage.
6. **Fire.**

## Decisions and remaining design questions

Answered on 9 October: fire's place (sixth, after Bloom), the wheel without an AI (yes), the flower trail (on by default, can be turned off; retired by decision 52), and evaluating the keys (the table above, in two steps).

Decided on 10 October: consequence-free Creative play, with stamina limits and health damage in modes such as Challenge. The mode-specific rates, recovery, progression and undo consequences still need design.

Answered on 11 October:

1. **Fire:** player/companion creations are scorched and repairable at their original material costs; other buildings and objects may burn completely. Both the player and connected AI may cast under the same rules.
2. **Weather:** add patterns that Fire and Growth respond to; player/Gubble control comes through later ability growth.
3. **Wish runner:** player-started with a first-use prompt from the game, vendor-neutral. Subscription support is a provisional design assumption, with provider-specific findings and limits recorded above.
4. **Concurrent wishes:** queue them.
5. **Children's content:** curated first; all Gubble content they receive must be appropriate for an eleven-year-old.
6. **Bindings:** try the proposed defaults in playtesting.

Remaining work after the baseline mechanics ([MECHANICS.md](../game/MECHANICS.md); its numbers are provisional and tuned by playing): the repair interaction; which abilities get expertise tracks beyond Glow (Grow, Bubbles and Fire: packet M11); camps as placeable things; the Bubble's body rules (Bubble Boat's and the carry's collision: packet M9); and, if the wish runner returns, the exact provider/authentication route, distribution terms and child-account eligibility before implementing each adapter. Stamina, damage, recovery, mode-specific undo and transfers between modes are specified there (decisions 2, 3, 10 and 13 of 14 October).
