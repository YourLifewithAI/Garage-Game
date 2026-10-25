# The hotbar: the pockets and the abilities on one bar

**Status: the integrator's proposal of 25 October, built as the first version at the founder's word (MECHANICS.md decisions 119 and 128); the layout is provisional until the founder plays.** The founder: a hotbar on the bottom of the screen; the selected item is in the player's hand; the pockets panel becomes the hotbar so the player never stops to pick an item; the ability wheel is clunky for the same reason, so abilities belong on the bar too, always visible.

## The bar

- **Ten slots** along the bottom, keys **1 to 0**. One slot is selected at a time; a key or the mouse wheel over the bar selects it. The bar never captures the mouse and never stops the body (decision 119); Esc touches nothing on it.
- **Two layers, one bar.** The **pockets** layer is the ten pocket slots (decision 120); the **abilities** layer is Glow, Grow, Bubbles, Fireworks, Build, Fire and the slots to come, in the wheel's order. **Tab** switches the layers. The layer not shown stays visible as a thin dimmed strip above the bar with its selection, so both are always on screen; its keys do nothing until it is brought down.
- **Why layers and not one mixed row:** the founder asked for ten pocket slots and for every ability on the bar; one row of ten cannot hold both, and a player-arranged mix (abilities as tokens among items) is a second system to build and explain. Layers keep every slot, every key and one habit: Tab, then a number. If play shows the switch is a nuisance, the next version is a mixed row the player arranges, with the pockets' overflow in the camp screen.

## The hand

- **A pocket slot selected is in the hand.** A stack's unit is armed: **E** or the left button uses it (food is eaten, `inventory.use`; a seed is planted when planting exists; a log or stone is set down, `inventory.drop`), and the count on the slot falls. **F** drops one unit loose before the player (`inventory.drop` on the selected slot) when nothing in the world wants F: no thing in reach to pick up, no unit lying loose to gather, nothing in the hands (the world first, as E asks it, so F still gathers the next log or picks the dropped unit up again; E's prompt names it, "E: eat an apple · F drop one", decision 130). A unique thing is taken into the hand from the pockets (`inventory.take` to the hand) and behaves as a thing picked up with F: E sets it down or stores it, F drops it. Selecting another slot returns a held unique thing to its pocket slot.
- **Picking up** with F or E fills the pockets as today (a stack if the kind stacks, a slot if unique), so what is picked lands on the bar at once.
- **An ability selected is armed.** An ability key records its slot whatever the hands carry (decision 129: the kernel's select on this layer never puts it away). The number keys do what they did on the wheel: every key casts exactly as it did (Glow, Bubbles, Fireworks and Build on the press, Fire on 6 on the click since decision 83, key 2's tap on release and its sweep as the bridge); a hold that shows a ghost before casting is not built for any key and would be new work. The wheel's padlock and lock words move onto the slot. The right button's hold still opens the wheel for one round (decision 128); the smart ask's tap is unchanged.
- **The H help opens above the bar** (decision 130): from the top margin down to the bar's caption, so the bar stays visible and clickable. The help is taller than that room, so its lines scroll (the wheel over it, or its scroll bar) with the playtest line pinned below them; folded it is still the small "H keys" box at the lower left.

## What the kernel adds

`inventory.select` (player-only): the selected slot, kept in the team file per mode so a reload keeps the hand; `player.status.hand` says what is selected and whether it is a unit, a thing in the hand, or an ability. No companion command touches the selection (the hand is the player's, AGENTS.md). The pockets' caps are counts (decision 120).

## What P-Hotbar builds

1. **Kernel** (with the second inventory packet): `inventory.select`, the hand's reader, the unique thing's take-to-hand and return.
2. **HUD**: the bar, its layers and Tab, the keys, the dimmed strip, the slot's lock words, the pockets panel retired into the bar, the camp screen's Inventory page keeping Take and Store, the H help.
3. **Acceptance**: a key selects within a frame; the body walks while anything on the bar is selected; a unit used from the bar falls by one; a unique thing selected is in the hand and returns on another selection; a locked ability's slot shows the lock words and casts nothing; Tab switches and Esc ignores the bar; the added cost within the HUD's 0.5 ms budget.

## For the founder's first look

The bar at 1152x648: are ten slots readable; does the dimmed second layer read as "also yours"; does Tab feel like one habit or a stop; is the mouse wheel on the bar wanted; which abilities should be the first six.
