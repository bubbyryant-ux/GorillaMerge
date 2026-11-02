# GorillaMerge

A Gorilla Tag mod (BepInEx) that puts other games **into** Gorilla Tag. Minecraft is the first one:
an endless Minecraft survival world (biomes, caves, ores, day and night, zombies, creepers, pigs)
that you explore with Gorilla Tag movement. You start with nothing: punch trees, craft at a real
Minecraft crafting table, smelt in a furnace, and build with what you collected, even back in
Gorilla Tag's own maps.

The Minecraft pictures and sounds are read from **your own Minecraft Java install** every time the
game starts (the newest release in `%APPDATA%\.minecraft\versions`). Nothing from Minecraft is
copied into the mod. Without Minecraft installed, the blocks show up as plain colours.

> GorillaMerge is a fan-made mod. It is not made by, endorsed by, or connected to Mojang, Microsoft or
> Another Axiom. You need your own copy of Minecraft Java Edition and Gorilla Tag. Please don't upload
> Minecraft's, Gorilla Tag's or any other game's files to this repository.

## Controls (VR)

| Do this | What happens |
|---|---|
| **A + B together** (right controller) | Teleport to the Minecraft world. Press again to come back. |
| Make a **fist** (grip) and **punch** a block | Mines it a bit with each punch (Minecraft's times: logs take ~6 punches by hand, stone needs a pickaxe to drop anything). A fast fist punch doesn't bounce you off the block; open hands and slow fists still climb. |
| Left **trigger** (hold) | Mine the block you point at. Cracks show progress. Point at a mob and pull it to hit. |
| Right **trigger** | Place the block you're holding where you point. Point at a **crafting table** or **furnace** to open it. |
| Hold **food** to your mouth (or hold the right trigger) | Eat. |
| Swing at a **mob** | Hit it (swords and axes hit harder). |
| **B** / **Y** | Next / previous hotbar slot. |
| **X** | Your inventory (2x2 crafting). In menus: point and pull the trigger to pick up / put down, grip for half a stack, B or Y to close. |
| Touch **Menu** above your left hand with your right finger | Inventory, worlds, Survival/Creative, building on/off, new world. |
| Walk into the **purple portal** | Back to Gorilla Tag from the Minecraft world. |

Desktop: middle mouse or F6 places, hold F7 mines, `[` `]` change slot, E inventory,
F8 opens the menu, F9 teleports to the world and back.

| Hold a **bow** and hold the right trigger, let go | Shoots an arrow (needs arrows; a full draw takes 1 second). Walk over your arrows to pick them back up. |
| Right trigger with **armor** | Puts it on (or drag it into the armor slots in your inventory). |
| **Hoe** on grass/dirt, then **seeds** on the farmland | Plants wheat. It grows in about 6 minutes; **bone meal** speeds it up. Make bread from 3 wheat. |
| **Shears** pointed at a sheep | Shears it for wool. |

The hotbar, armor, hearts and hunger float low in your view (HudScale / HudAngle in the settings).

Mobs: zombies and skeletons (they burn in sunlight; skeletons shoot arrows), creepers, spiders (climb walls,
only angry in the dark unless you hit them) and the occasional enderman (leaves you alone until you hit it,
then teleports around) come out at night and in caves. Torches stop them spawning nearby. Pigs, cows, sheep
and chickens wander the grass in the day: cook their meat in a furnace, use leather for armor, feathers and
flint for arrows, string for bows. Your stuff, the world and furnaces save automatically.

Hand tracking (Steam Link): pinch = trigger, fist = grip, so pinching places and fist-punching breaks.

## Where it works

Only **offline** or in **modded lobbies**. In public lobbies the blocks disappear and building is off,
because climbable blocks would be cheating against people without the mod.

## Playing with friends

In a modded lobby, friends who also have GorillaMerge play in **one Minecraft world**:

- The world belongs to whoever of you has been in the room longest. Everyone else presses A + B and
  lands in that player's world: same terrain, same builds, same spot in the sky, so you see each other's gorillas.
- Every block anyone places or breaks (and TNT holes, farmland, growing wheat) shows up for everyone.
- Only the owner's game saves the shared world. Everyone keeps their own inventory, and your own world
  waits for you, unchanged, until you play without friends again.
- If the owner leaves, the next player in line becomes the owner and everyone moves to their world.
- Mobs and dropped items are still separate on each player's game, and "New World" is hidden while friends are here.
- Nothing is sent to players without the mod, and nothing at all in public lobbies.

Blocks you build in Gorilla Tag's own maps are still only on your game.

## Settings

`Gorilla Tag\BepInEx\config\dev.gorillamerge.gorillatag.cfg` (made the first time the game runs):
block size, reach, punch speed, swapping hands, world size/seed, music, volume, and more.
Your builds and the Minecraft world are saved in `BepInEx\config\GorillaMerge\`.

## Other games (packs)

Each game is a folder in `BepInEx\plugins\GorillaMerge\Packs\` with a `pack.json`.
See [Packs/README.md](Packs/README.md) for the format.

The easy way is the pack maker:

```
python tools\make_pack.py "C:\Games\SomeGame" --name "Some Game"
python tools\make_pack.py "C:\Levels\castle.obj" --name "Castle"
```

The first one finds a game's pictures and sounds and turns them into blocks plus an island
world. The second one turns any 3D level saved as `.obj` into a map you can climb. Many games
keep their files packed inside big archives (Unity `.assets`, Unreal `.pak`), and the tool can
only use files you can see in File Explorer.

## Building the mod

```
dotnet build -c Release
```

This copies `GorillaMerge.dll` and the `Packs` folder into Gorilla Tag's `BepInEx\plugins\GorillaMerge`.
Delete that folder to uninstall.

Note: Utilla 1.6.27 (the modded-lobby library) crashes while loading on the late-October 2026 game
update, so its "game ready" event never fires. GorillaMerge starts on its own and also checks for
"MODDED" in the room's game mode, so it doesn't depend on that event.

If friends ever wreck your shared world, your world from before they joined is in
`BepInEx\config\GorillaMerge\backups` (the newest 10). Close the game and copy one over
`Minecraft_world.gmw` to go back.

## License

MIT, see [LICENSE](LICENSE). This covers GorillaMerge's own code only. Minecraft's and Gorilla Tag's
files belong to their owners and are never part of this project.
