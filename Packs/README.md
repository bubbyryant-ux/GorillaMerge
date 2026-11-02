# Pack format

A pack is a folder with a `pack.json`. The folder name is the pack's id. Two types:

## Voxel pack (blocks + a generated island)

```json
{
  "name": "My Game",
  "type": "voxel",
  "sky": "#78A7FF", "fog": "#C0D8FF",
  "sun": "file:sun.png", "clouds": "file:clouds.png",
  "music": "file:music/",
  "sounds": {
    "stone": { "break": "file:sounds/break.ogg", "place": "file:sounds/place.ogg", "step": "file:sounds/step1.ogg|file:sounds/step2.ogg" }
  },
  "events": { "explode": "file:boom.wav", "fuse": "...", "click": "...", "travel": "..." },
  "blocks": [
    { "id": "grass", "name": "Grass", "top": "file:grass_top.png", "side": "file:grass_side.png", "bottom": "file:dirt.png", "sound": "stone" }
  ],
  "world": { "surface": "grass", "under": "dirt", "stone": "stone", "beach": "sand", "water": "water",
             "trees": [{ "log": "log", "leaves": "leaves", "chance": 0.01 }],
             "ores": [{ "block": "gem", "chance": 0.005, "maxY": 30 }] }
}
```

### Where pictures and sounds come from

| Spec | Means |
|---|---|
| `file:textures/a.png` | a file next to `pack.json` (absolute paths work too) |
| `zip:C:/Games/x.zip!textures/a.png` | a file inside any zip / jar / pk3 |
| `mc:block/stone` | from your Minecraft jar (sounds: `mc:dig/grass` plays `dig/grass1..4`) |
| `mc-music:game` | all of Minecraft's game music |
| `#RRGGBB` | a plain coloured tile |

Join several with `|` (a random one plays each time, or the first picture that exists is used).
A folder path for sounds/music uses every audio file in it. Sounds can be `.ogg`, `.wav` or `.mp3`.

### Block fields

| Field | |
|---|---|
| `id`, `name` | id used in `world`; name shown above your hand |
| `texture` | same picture on all sides, or use `top` / `bottom` / `side` / `end` / `north` / `south` / `east` / `west` |
| `overlay` | a picture drawn over the sides (Minecraft's green grass edge) |
| `tint`, `tintFaces` | colour to multiply in (`all`, `top`, `sides`) |
| `render` | `opaque` (default), `leaves`, `glass`, `water`, `portal` |
| `solid` | can you climb it (default: yes, except water and portal) |
| `hardness` | how long it takes to mine; `-1` = can't break; 40+ = TNT-proof |
| `sound` | which group in `sounds` it uses |
| `surface` | Gorilla Tag tap sound / feel: `stone`, `wood`, `grass`, `dirt`, `sand`, `glass`, `ice` (slippery!), `snow`, `cloth`, `metal` |
| `special` | `tnt` (punch to light it) or `portal` (walk in to travel) |
| `item` | show it in your block bar (default true) |
| `color` | colour to use if the picture can't be found |

## Map pack (any 3D level)

```json
{ "name": "Castle", "type": "map", "model": "castle.obj", "scale": 1.0, "surface": "stone", "sky": "#8EC3FF", "spawn": [0, 2, 0] }
```

`model` is a Wavefront `.obj`; its `.mtl` file and the `map_Kd` pictures next to it are loaded too.
Leave out `spawn` and you start on the floor in the middle of the level. Everything in the model can be climbed.
