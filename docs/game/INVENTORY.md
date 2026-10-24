# The inventory: what a chest at the camp would do

For the founder, before any code. Today the hand carries one loose thing (0.5 kg), Fractals are a count, gathered logs and stones a wallet, food is eaten where it is found ([D1](../runs/RUN-2-PLAYTEST-ROUND-2.md)), and the camp's Inventory page says "coming" ([decision 87](MECHANICS.md)). Every number is a proposal.

## Your questions first

| Question | Proposal | Why |
|---|---|---|
| Pockets, or the camp's chest? | A chest at the camp, no pockets. The hand holds one thing, or one unit of food. | The camp stays the hub, and a 10 cm body carries what its hand does. A full hand cannot climb. |
| What counts? | Stacks: apples, berries, a reserved id for seeds. Unique: any loose thing carried home. Fractals and the materials wallet stay; logs and stones still gather. | A unique thing is a room entity and keeps its id. Grow works without seeds (decision 76), so nothing yields one yet. |
| What may the Gubble do? | Read the chest and point food out. Never store, take or hand over. | No companion command targets the player's avatar or fills its hand. Fetch still brings a loose thing to you. |
| Loss? | A knockout or deep-water return sets the hand's loose thing down on the nearest ground; a food unit goes back to the chest. The chest never spills. | Failure stays soft (MECHANICS section 1), and a dropped thing can be walked back to. |
| One chest per team or island? | Per island save. | A unique thing's id belongs to one room; the wallet and camp live there too. Only Fractals travel. |
| Does Creative share it? | No: its own chest, as its own save. The copy into Challenge starts empty, as it empties the wallet. | A chest filled freely in Creative would be a free start. |

**One cost to weigh.** Resting at a camp heals a heart in 4 s (decision 91), so food matters only once carried out, one unit at a time. The chest mostly keeps and shows. Is that enough to build for?

## How it would play

- **In the world.** E picks up a loose thing, as today. On food E still eats on the spot (decision 92); F picks one unit up instead.
- **At a camp** (within 0.5 m of a lit campfire, as for spending Fractals), E with a thing or unit in hand stores it: after "gather", before the set-on and set-down in E's order. Empty-handed, E sits.
- **The Inventory page** lists each stack with its count and use ("Apples, 12 · half a heart"), each unique thing by name, and the materials. Take puts one in an empty hand. Eat heals at once, at D1's amounts (apple two quarters, berries one), and at full hearts keeps the unit. Away from camp, E eats a unit in hand.
- **Caps.** Unit weights are game values: apple 0.02 kg, berries 0.005 kg, seed 0.001 kg. No stack outweighs the 0.5 kg hand: 20 apples, 50 berries, 100 seeds. The chest holds 12 entries (stacks and things) and 2 kg, the Gubble's own carry limit, so the pair can always move it. Past either: `occupied`.

## The data

A `chest` block in the island save's team block (room state's `x_play_chest`, beside the camp's), so per team, mode and island:

- `stacks`: kind to count, at most 8 kinds, 1 to the cap.
- `things`: ids of stored loose things, at most 8. The entity stays in room state as removed and stored, as a gathered log does, so taking it out restores the same id. Names are untrusted display text.
- Masses, caps and uses come from a new optional `inventory` block in the rules pack, as `fractals` does.
- **Migration.** Absent means empty; an empty chest writes no key; older saves load unchanged (within version 1). The Creative-to-Challenge copy (`Strip`, `Ui/Menu/MenuSaves.cs`) drops it. A unit in hand is not saved: it returns to the chest.
- **Boundary.** `inventory.store` and `inventory.take` join `player_only_ops`: the companion is refused `permission_denied` before anything is looked up, no field carries a price or an approval, and neither moves the player. The companion reads counts in a new `player.status` `chest`, as it reads `wallet`, and never writes.

## What it rests on, and is not

It rests on D1's food (the first stacks), the sandbox's grab and release, and the camp screen. It is not crafting, hunger, equipment or sorting; the Build knacks and carry growth stay deferred ([MECHANICS section 4](MECHANICS.md)).

## What P-Inventory would build

1. **Contract round (integrator):** the ops in `player_only_ops`, `item.pickup` naming a food source, `player.status.chest`, the `inventory` block, `x_play_chest`, validator, examples, mock refusals.
2. **One Opus kernel packet:** the two commands and checks, the save block, a `Chest` reader beside `Nature.Wallet`, the hands-full rule counting a unit, the knockout's set-down.
3. **A Sonnet HUD pass:** the live page, E's prompts, F on food, the copy's strip.

**Acceptance:** the companion's store and take refused before lookup; store then take keeps id and count; a save without the block loads empty, and an empty chest saves byte-identical; caps refuse `occupied`; Eat at full hearts keeps the unit; no command moves the player; the copy into Challenge starts empty.
