# Run 2: the opening (planned 16 October; to build later)

**Read with** [AGENTS.md](../../AGENTS.md), [docs/game/MECHANICS.md](../game/MECHANICS.md) (decisions 49 and 61 to 63, 70 and 71), [the magic design](../magic/README.md) ("Fire", "Expertise for Bubbles, Fire and Grow") and the room-to-island intro already built (`game/scripts/native/Intro/`, about 15 s on every load, skippable; [RUN-2-STATUS.md](RUN-2-STATUS.md), the tenth session). **Status: the founder's idea of 16 October, written down to build later.** The founder: "it helps set the stage for the game. We get a whole narrative in thirty to forty-five seconds." Fire and its campsites come first (the next packets); this opening is built once they exist.

## The scene, in order (30 to 45 seconds, the first load of the first island only)

1. **The desk.** An animation of the founder at a desk, coding with Claude on the computer screen, until they pass out and nap on the keyboard.
2. **The room becomes the island.** Zoom out; the room is deconstructed into a landscape (the intro that exists today: the room's boxes becoming land); the walls fall away; it is night.
3. **The wake.** The player wakes by the island's starting campfire, at night. They put a hand to their head, confused, and look around at the strange place.
4. **The Gubble arrives.** The Gubble emerges out of the fire. The player is startled.
5. **The rain.** It starts to rain, the campfire goes out and everything goes dark. The player shivers.
6. **Friendly.** The Gubble shows it is friendly by relighting the fire.
7. **The chime.** A chime or announcement sound plays, the one repeated whenever the Gubble learns an expertise; the player and the Gubble both look around.
8. **The card.** An announcement card, two lines, one above the other:

   > The Gubble has learned Fire.
   > What will you do with it?

## What it rests on

- **The starting campfire** (decision 61): every island has one; the player wakes beside it on the first load (decision 49's start spot falls back to it). Learning Fire here is decision 61's "the Gubble learns Fire when the player first rests at the starting campfire": in the opening, the relighting is the moment it is learned.
- **The expertise announcement:** the chime and the two-line card ("The Gubble has learned X. What will you do with it?") are general, for every expertise the Gubble learns; the Fire packet may build them first and the opening reuses them.
- **The rain** is the island's weather (the nature kernel's rain); the fire going out is fire under rain (Snuff's condition, decision 63, without the expertise).
- **Animation:** the player's hand to the head, looking around, startled, shivering, and the Gubble emerging from the fire, are new character animations (the characters' rig and gaits, Lane P and the characters' lane); the desk scene is a short authored sequence (a new asset, not a real room or photo of the founder; any likeness of the founder or their desk needs the founder's approval before it is made or committed).

## Packets (when it is built)

- **O1 the scene's beats** (Sonnet, the intro's lane): the sequence from the existing intro's end to the card, on the first load of the first island only (a flag in the team's island save), skippable like today's intro, never headless or in a boot check.
- **O2 the animations** (Sonnet or Opus by the characters' routing): the player's wake, confusion, startle and shiver; the Gubble rising out of the fire.
- **O3 the desk prologue** (a look and art packet; the founder judges it): the desk, the screen, the nap, the zoom out into the room.
- **The founder judges** the whole opening in play, as with every look.
