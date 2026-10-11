# Background

How this bug works, and how the fix was arrived at.

## The symptom

Players have reported since late 2024 that furniture sinks: metal shelves end up half-buried in the floor, safes settle into the ground, lockers disappear beneath it. Sometimes the contents of a shelf stay floating where the shelf used to be. The reports cluster around the December 2024 update that introduced Safehouse Customization.

## What actually happens

When you leave a location and come back, the game rebuilds it. During that rebuild, each piece of furniture briefly exists twice: the copy the level designers placed, and the restored copy carrying whatever state your save recorded.

The game runs a routine at that moment whose job is to settle loose objects onto the ground, so that when you break a table down, the things sitting on it fall properly. That routine runs on the *designer's* copy, but the objects it selects include *restored* copies nearby that it has no business touching. It then drives those objects downward.

The result is furniture pushed below the floor. The movement is purely vertical: sideways position and rotation are untouched, which turns out to be the key to fixing it safely.

The corrupted position is not saved immediately. Your save still holds the correct position at the moment the location loads, and the damage is written on the *next* save. That is why the problem can seem to appear out of nowhere some time after the visit that caused it.

## How consistent is it?

Very. In one interior, two specific shelves sink by **1.0266 m** and **0.4275 m** every time. Those exact figures were reproduced four separate ways:

| Source | What was running |
|---|---|
| A save from March 2025 | a full mod set |
| A fresh instrumented run | diagnostic build of this mod |
| A run with this mod removed | MelonLoader and other mods only |
| A clean-room run | nothing at all; MelonLoader removed from the game entirely |

All four agree to four decimal places. The last one is what matters: the bug reproduces with no mod loader present at all, so it is a defect in the game and not an interaction between mods.

## How the fix works

**Prevention.** The settling routine is allowed to move an object that is resting on the item being processed, and prevented from moving anything else. An object counts as resting on it only when two things are true: its lowest point is at or above the item's top, and its footprint overlaps the item's footprint. An object resting on a surface sits within a couple of centimeters of that surface's top; the displaced shelves sat over half a meter below the object that displaced them. The threshold sits between those, with a wide margin on either side.

Both tests need a measurement of each object, and the item being processed is often switched off by the time the routine runs, so nothing of it is being drawn. In that case the mod measures it from its meshes instead. If either object cannot be measured at all, the object is left where it is. That is the safe direction: an object left in place can be picked up, and furniture pushed under a floor cannot.

Two earlier rules were wrong, and each one passed its tests until a test made something really need to fall or really need to stay.

The first keyed off a component the game attaches to objects it intends to drop. Breaking down a table with things on it left four items hanging in the air.

The second compared heights only. It had no sideways test, and when the item being processed was switched off it compared against that item's pivot point, which for something standing on a floor is the floor. A chair across the room was judged to be resting on a metal box it was nowhere near, and it sank. A stricter version then refused to move anything it could not measure, which left everything on a broken-down table hanging in the air, because loose items never fall by themselves.

The current rule was checked in both directions before shipping. Fifteen objects resting on two tables fell when the tables were broken down, landing where they land with the mod disabled, and the chair stayed where it was on the exact call that had sunk it.

**Restoration.** The designer's copy of each object exists briefly during loading, before the game discards it. The mod reads the intended position from it in that window, then compares every restored object against it. Anything sitting below where it belongs, with sideways position and rotation unchanged, still in its own location, and not something the game drops on purpose, is put back.

Everything else is left alone. Furniture you moved, furniture you carried in from elsewhere, and objects the game legitimately settles are all excluded, because for those the designer's position is not the right answer. Only the height is corrected, which also makes the pass self-limiting: once something is back where it belongs, it no longer matches and cannot be moved again.

An earlier version of this check never repaired anything outdoors. An outdoor object is authored in one part of a region and restored into another, and the two carry different names for the same place. The check now treats them as one location. A safe that had been sitting eight meters under a marsh came back up.

## Containers that vanish outright

Some reports describe a container not sinking but disappearing altogether, most often a safe or a storage box, sometimes several at once after a few visits. That is a different defect with a different cause, and this mod stops it as well.

Some containers exist as a pair in the same spot, one for each group of difficulty settings, with only one of the two switched on in any game. The game keeps a list of objects that have been broken down. When a location loads, it looks up each entry on that list by the object's identifier, and if that fails, by position, taking the first breakdown object within a centimeter.

The switched-off member of a pair ends up on that list as broken down. On later loads its identifier no longer resolves, so the lookup falls back to position, and the object it finds in that spot is the other member of the pair: your intact container. The game applies the entry to it, switches it off, and about a second later removes it. Once the save is written, its position and contents are gone.

The removal step was added in the December 2024 update. Older versions run the same lookup with the same fallback and never remove anything as a result.

The fix refuses a position match when the entry and the object it found both carry an identifier and the two differ. An entry is then only ever applied to the object it was written for. Where either side has no identifier, the game's own behavior is left alone. This was observed live in two locations, with the container lost when the fix was off and kept when it was on.

## Bringing back what was already lost

Stopping the removal does nothing for a container a save has already lost, so the mod also recovers those. It is deliberately narrow. It acts only on an object that is the live member of one of these pairs and whose saved record says it is removed or switched off. That is the one cause that was traced, and there are seventeen such pairs in the game. Anything else that is missing was removed by the game on purpose, or by you, and is left alone.

When such a location loads, the designer's copy of the lost container is still there for a moment before the game discards it. The mod rewrites the saved record to say the container is placed at that position, and the game then restores it the same way it restores everything else you have placed. The stale list entry that caused the removal is refused for that load, so the container is not switched off again.

What comes back inside it depends on what the save still holds. The game does not let you break down or pick up a container you have not searched, or one that still has items in it. So a lost container in either state cannot have been your doing, and it comes back with its saved contents. An empty container, or one whose contents record is gone, could just as well have been broken down by you, and the save does not say which. Those are recovered only if you turn on the optional setting, and they come back empty, because what they held is unknown and rolling fresh loot would be a cheat. A safe recovered that way comes back unlocked and marked as searched, so it does not invite you to crack it for nothing.

If anything goes wrong while the mod is deciding, it changes nothing and the location loads as the game would have loaded it.

## A separate bug this mod does not fix

Some loot spawns in mid-air. In one cabin a tin of oats hangs 0.886 m above the floor with nothing beneath it, in the same spot every time, on a first visit, with no mods loaded. The game never attempts to settle it. The item is placed there, not displaced.

This mod leaves it alone. Correcting it would mean dropping unsupported objects onto whatever is below, and there is no way to distinguish a mistake like this from something meant to hang or be wall-mounted. It is mentioned here because it is likely what people mean when they report shelves sinking while their contents stay floating: those are two different problems.
