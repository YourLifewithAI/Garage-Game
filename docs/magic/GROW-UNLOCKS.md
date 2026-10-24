# Grow's unlocks: what each would do

For the founder, before any code: how [decision 76](../game/MECHANICS.md)'s three unlocks would behave, and the built mechanism each reuses. Every number is a proposal.

## What all three share

- **Gate.** The team's Grow expertise count, as `ActiveLightAllowance` counts Glow's: 2 opens seedless growth, 3 bridges, 5 ivy. Earlier, the cast is refused `permission_denied` before the price.
- **One command.** Each is `nature.cast` Grow; the place decides, as for Fire (decision 77). Grow already answers `preview: true`.
- **Price, Gubble.** 4 ordered, 2 as the Gubble's idea, no hold. The Gubble may cast all three; none takes a `target`. Only the bridge adds collision, one-sided over the void alone, so it cannot push an avatar.
- **Saved, undone.** Each keeps a list in the nature block beside `plants` and `waterpads`, in the undo step too (Creative only); the manifest stays pinned.

## Seedless growth (two)

- **Cast.** Grow on bare soil, now refused "no plant to grow there".
- **Appears.** Up to 12 sprouts in the 0.5 m circle, 0.15 m apart (a tuft is 5 cm), on fertile ground (grass or soil) with the moisture and light Bloom needed (0.12), copying the nearest grass and flower sets by a hash of the spot. Each is a half-size seedling; two more casts make it natural. At most 1,024 an island (about 80 KB saved).
- **Replanting.** On burnt ground the cast returns every burnt plant in the circle as a seedling: burn cleared in `Nature.Plants`, manifest untouched, `seeds` sets only. That is decision 67's "unless replanted".
- **Fire.** Sprouts burn as plants; a burnt one is deleted.
- **Ask.** Seedlings at half size or natural? Replant the whole circle, or the nearest plant?

## A bridge of plants (three)

- **Cast.** Hold 2 and drag from lip to lip, as 6 drags a Fireline (`end_m`); both lips and the middle within the Gubble's 2 m reach and sight.
- **Appears.** A level deck 0.1 m wide over the void only, 0.4 to 1.2 m long (a running hop is 0.36), lips level within 0.02 m (one step) so the body walks on and off. A true void: ground 0.15 m or more below (over twice the 0.065 m hop) or swimmable water, nothing in the way. At most 8. Reuses the waterpad's list and one-sided view.
- **Fire.** Burns as a plant, caught by burning ground at a lip; solid until burnt down, then whoever is on it falls as usual.
- **Ask.** Is 1.2 m enough? Is 0.02 m too strict, or should the deck take the lower lip and ramp its last 0.1 m?

## Wall ivy (five)

- **Cast.** Grow at a wall at least 70 degrees steep; the ivy roots on standable ground within 0.4 m below the aim.
- **What changes.** The controller already grabs any face too steep to stand on (`Climbable`), at the bar's price (free in Creative); so ivy changes the price: no bar on its strip, checked in `SpendPlayerClimb`. Grabbing still needs a bar above empty.
- **Bounds.** 0.4 m long, one band: it saves one band of bar, the limit grown trees keep. 0.1 m wide, grown whole by one cast, at most 8, drawn, not collided.
- **Fire.** Burns as a plant, caught by burning ground at its foot; the price returns when it is gone.
- **Ask.** Free or half price? Ivy on every ledge would cheapen the band ladder: accept, or one per ledge?

## What P-Grow would build

One Opus kernel packet.

- **Kernel:** `GlowProgress.cs` (the count), new `CommandHostGrowKinds.cs` (like `CommandHostFireKinds.cs`), `CommandHostGrow.cs`, `CommandHostNature.cs`, `NatureState.cs`, `NatureStore.cs`, `CommandHostRoomState.cs`, `CommandHostModes.cs`.
- **Room:** new `SproutView.cs`, `BridgeView.cs`, `IvyView.cs`; `PlantCopiesView.cs` (seedlings below 1).
- **Ui:** `RoomHud.Gubble.cs`, a key-2 drag file like `RoomHud.FireWheel.cs`, `RoomHud.MagicNature.cs`, `JournalPages.cs`.
- **Integrator:** a contract round (`end_m` for Grow, `data.kind`), then the companion's mock.
- **Acceptance:** each kind refused below its count, working at it; sprouts deterministic, byte-exact on reload, undone; a burnt tree returns at half size with collision; a small avatar crosses 1.2 m on a bridge and falls when it burns; ivy free in Challenge, priced again once burnt; no Gubble cast moves the player.
