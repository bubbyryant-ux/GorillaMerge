"""Makes a GorillaMerge pack out of another game's files, so you can put Gorilla Tag "into" that game.

Two kinds of packs:

  1. Blocks pack (from a game folder or a .zip/.jar/.pk3 of it)
         python make_pack.py "C:\\Games\\SomeGame" --name "Some Game"
     It looks for pictures (.png/.jpg) and sounds (.ogg/.wav/.mp3), turns the best-looking
     pictures into blocks, and makes a floating island out of them that you can visit
     from the wrist menu. You can build with the blocks anywhere, like Minecraft.

  2. Map pack (from a 3D level saved as .obj)
         python make_pack.py "C:\\Stuff\\level.obj" --name "Cool Level"
     The level shows up in the wrist menu and you can climb all over it with
     Gorilla Tag movement.

Nothing is copied: the pack just points at the files where they already are,
so keep them there. Many games hide their files inside big archive files
(Unity .assets, Unreal .pak). This tool can't open those; it only finds files
you can see in File Explorer.

After it runs, start Gorilla Tag. The new pack is in the wrist menu.
"""
import argparse
import json
import os
import re
import struct
import sys
import zipfile

GAME_PACKS = r"C:\Program Files (x86)\Steam\steamapps\common\Gorilla Tag\BepInEx\plugins\GorillaMerge\Packs"

IMAGE_EXT = {".png", ".jpg", ".jpeg"}
SOUND_EXT = {".ogg", ".wav", ".mp3"}
ARCHIVE_EXT = {".zip", ".jar", ".pk3"}

# Pictures with these words in their name are usually menus or icons, not world textures.
SKIP_WORDS = ("gui", "ui_", "_ui", "icon", "font", "button", "logo", "cursor", "hud", "menu", "splash",
              "portrait", "avatar", "screenshot", "thumbnail", "normal", "_n.", "specular", "rough",
              "metallic", "_ao", "height", "mask", "lightmap", "color", "colour", "/sun", "moon", "cloud",
              "vignette", "shadow", "particle", "rain", "sky", "blur", "background", "/bg", "mojang", "pack.png",
              "/misc/", "/item/", "/items/", "/entity/", "/mob/", "/gui/", "/effect/", "/font/", "/painting/")

# Words that hint what a picture is. The order matters: earlier words win.
KINDS = [
    ("grass", ("grass", "lawn", "turf")),
    ("leaves", ("leaves", "leaf", "foliage", "bush", "hedge")),
    ("log", ("log", "trunk", "bark")),
    ("planks", ("plank", "wood", "board", "timber")),
    ("dirt", ("dirt", "soil", "mud", "ground", "earth")),
    ("sand", ("sand", "desert", "beach")),
    ("stone", ("stone", "rock", "cobble", "cliff", "boulder")),
    ("brick", ("brick", "wall", "tile")),
    ("metal", ("metal", "iron", "steel", "plate")),
    ("glass", ("glass", "window", "crystal")),
    ("ice", ("ice", "frozen")),
    ("snow", ("snow",)),
    ("water", ("water",)),
    ("cloth", ("cloth", "wool", "carpet", "fabric")),
    ("floor", ("floor", "concrete", "pavement", "road")),
]
SOUND_OF = {"grass": "grass", "leaves": "grass", "log": "wood", "planks": "wood", "dirt": "gravel",
            "sand": "sand", "stone": "stone", "brick": "stone", "metal": "stone", "glass": "glass",
            "ice": "glass", "snow": "snow", "cloth": "cloth", "floor": "stone"}


def image_size(data):
    """Width and height from the start of a PNG or JPEG file (no extra libraries needed)."""
    if data[:8] == b"\x89PNG\r\n\x1a\n" and len(data) >= 24:
        return struct.unpack(">II", data[16:24])
    if data[:2] == b"\xff\xd8":
        i = 2
        while i + 9 < len(data):
            if data[i] != 0xFF:
                i += 1
                continue
            marker = data[i + 1]
            if marker in (0xC0, 0xC1, 0xC2):
                h, w = struct.unpack(">HH", data[i + 5:i + 9])
                return w, h
            i += 2 + struct.unpack(">H", data[i + 2:i + 4])[0]
    return None


def slug(text):
    s = re.sub(r"[^a-z0-9]+", "_", text.lower()).strip("_")
    return s or "block"


def nice_name(text):
    return re.sub(r"[_\-.]+", " ", text).strip().title()


def kind_of(name):
    low = name.lower()
    for kind, words in KINDS:
        if any(w in low for w in words):
            return kind
    return None


def walk(source):
    """Yields (spec, file name, header bytes) for every picture and sound, inside archives too."""
    def from_archive(path):
        try:
            with zipfile.ZipFile(path) as z:
                for entry in z.namelist():
                    ext = os.path.splitext(entry)[1].lower()
                    if ext in IMAGE_EXT or ext in SOUND_EXT:
                        head = z.open(entry).read(4096) if ext in IMAGE_EXT else b""
                        yield f"zip:{path}!{entry}", entry, head
        except (zipfile.BadZipFile, OSError):
            return

    if os.path.isfile(source):
        if os.path.splitext(source)[1].lower() in ARCHIVE_EXT:
            yield from from_archive(os.path.abspath(source))
        return
    for root, _, files in os.walk(source):
        for f in files:
            path = os.path.abspath(os.path.join(root, f))
            ext = os.path.splitext(f)[1].lower()
            if ext in ARCHIVE_EXT:
                yield from from_archive(path)
            elif ext in IMAGE_EXT:
                try:
                    with open(path, "rb") as fh:
                        head = fh.read(65536)
                except OSError:
                    continue
                yield "file:" + path.replace("\\", "/"), path, head
            elif ext in SOUND_EXT:
                yield "file:" + path.replace("\\", "/"), path, b""


def make_voxel_pack(source, name, max_blocks):
    pictures, sounds = [], []
    for spec, path, head in walk(source):
        base = os.path.basename(path)
        ext = os.path.splitext(base)[1].lower()
        if ext in SOUND_EXT:
            sounds.append((spec, base.lower()))
            continue
        low = path.lower().replace("\\", "/")
        if any(w in low for w in SKIP_WORDS):
            continue
        size = image_size(head)
        if not size:
            continue
        w, h = size
        # Square-ish pictures between 8 and 512 pixels; tall strips are animations (first frame is used).
        if w < 8 or w > 512 or not (h == w or (h > w and h % w == 0) or 0.75 <= w / h <= 1.34):
            continue
        stem = os.path.splitext(base)[0]
        kind = kind_of(stem)
        # Pictures in folders like "blocks" or "textures" are more likely to be world surfaces.
        texture_folder = any(w in low for w in ("/block", "/tile", "/terrain", "/texture", "/material", "/environment", "/world"))
        main_word = dict(KINDS)[kind][0] if kind else ""
        # Best first: the plain version ("stone" beats "stone_bricks_cracked"), still water beats flowing.
        score = (0 if texture_folder else 1, 0 if main_word and re.match(main_word + r"(?![a-z])", stem.lower()) else 1,
                 1 if "flow" in stem.lower() else 0, len(stem), abs(w - 32))
        pictures.append((score, spec, stem, kind))

    pictures.sort()
    # Take a mix: the best picture of each kind first, then more of each kind in turns,
    # then everything else, never more than two from the same family (e.g. "concrete_...").
    queues = {}
    for _, spec, stem, kind in pictures:
        queues.setdefault(kind, []).append((spec, stem))
    order = [k for k, _ in KINDS if k in queues] + ([None] if None in queues else [])
    blocks, used, by_kind, families = [], set(), {}, {}

    def add(spec, stem, kind):
        bid = slug(stem)
        family = bid.split("_")[0]
        if bid in used or families.get(family, 0) >= 2:
            return False
        used.add(bid)
        families[family] = families.get(family, 0) + 1
        block = {"id": bid, "name": nice_name(stem), "texture": spec, "sound": SOUND_OF.get(kind, "stone"), "hardness": 1}
        if kind == "leaves":
            block["render"] = "leaves"
            block["hardness"] = 0.2
        elif kind in ("glass", "ice"):
            block["render"] = "glass"
            if kind == "ice":
                block["surface"] = "ice"
        elif kind == "water":
            block["render"] = "water"
            block["item"] = False
        elif kind in ("grass", "dirt", "sand", "snow"):
            block["hardness"] = 0.5
        blocks.append(block)
        by_kind.setdefault(kind, bid)
        return True

    progress = True
    while len(blocks) < max_blocks and progress:
        progress = False
        for kind in order:
            if kind is None and any(k is not None and queues[k] for k in order):
                continue  # unknown pictures only after the known kinds run out
            q = queues[kind]
            while q:
                spec, stem = q.pop(0)
                if add(spec, stem, kind):
                    progress = True
                    break
            if len(blocks) >= max_blocks:
                break
    if not blocks:
        sys.exit("No usable pictures found. The game probably keeps them inside archive files this tool can't open.")

    def pick(*kinds):
        for k in kinds:
            if k in by_kind:
                return by_kind[k]
        return None

    solid = [b["id"] for b in blocks if b.get("render") not in ("water", "glass", "leaves")]
    first = solid[0] if solid else blocks[0]["id"]
    world = {
        "seaLevel": 30,
        "hills": 9,
        "surface": pick("grass", "floor", "dirt", "sand", "stone") or first,
        "under": pick("dirt", "sand", "stone") or first,
        "stone": pick("stone", "brick", "floor") or first,
        "beach": pick("sand", "dirt") or first,
        "lakeBed": pick("dirt", "stone") or first,
        "caves": True,
    }
    if "water" in by_kind:
        world["water"] = by_kind["water"]
    if "log" in by_kind:
        world["trees"] = [{"log": by_kind["log"], "leaves": by_kind.get("leaves", ""), "chance": 0.012}]

    # Sounds: guess from the file names, every block shares them.
    def find(*words):
        hits = [s for s, n in sounds if any(w in n for w in words)]
        return "|".join(hits[:8]) or None

    sound = {"break": find("break", "destroy", "hit", "impact", "dig", "smash", "crack"),
             "place": find("place", "put", "drop", "land"),
             "step": find("step", "foot", "walk")}
    sound = {k: v for k, v in sound.items() if v}
    pack = {
        "name": name,
        "type": "voxel",
        "about": "Made by make_pack.py from " + os.path.abspath(source),
        "sky": "#78A7FF",
        "blocks": blocks,
        "world": world,
    }
    if sound:
        pack["sounds"] = {g: sound for g in sorted(set(SOUND_OF.values()))}
    explode = find("explo", "boom", "blast")
    if explode:
        pack["events"] = {"explode": explode}
    music = [s for s, n in sounds if "music" in n or "theme" in n or "song" in n]
    if music:
        pack["music"] = "|".join(music[:20])
    return pack


def make_map_pack(obj, name, scale):
    return {
        "name": name,
        "type": "map",
        "model": os.path.abspath(obj).replace("\\", "/"),
        "scale": scale,
        "surface": "stone",
        "sky": "#8EC3FF",
    }


def main():
    parser = argparse.ArgumentParser(description="Make a GorillaMerge pack from another game's files.")
    parser.add_argument("source", help="a game folder, a .zip/.jar/.pk3, or a 3D level .obj")
    parser.add_argument("--name", help="what the pack is called in the wrist menu")
    parser.add_argument("--out", help="the Packs folder to write into (default: the installed mod's Packs folder)")
    parser.add_argument("--max-blocks", type=int, default=40, help="most blocks to make (default 40)")
    parser.add_argument("--scale", type=float, default=1.0, help="map packs: size multiplier for the level")
    args = parser.parse_args()

    if not os.path.exists(args.source):
        sys.exit(f"Can't find {args.source}")
    name = args.name or nice_name(os.path.splitext(os.path.basename(os.path.normpath(args.source)))[0])
    out = args.out or (GAME_PACKS if os.path.isdir(GAME_PACKS) else os.path.join(os.path.dirname(__file__), "..", "Packs"))

    if args.source.lower().endswith(".obj"):
        pack = make_map_pack(args.source, name, args.scale)
    else:
        pack = make_voxel_pack(args.source, name, args.max_blocks)

    folder = os.path.join(out, re.sub(r"[^A-Za-z0-9 _-]+", "", name).strip() or "Pack")
    os.makedirs(folder, exist_ok=True)
    with open(os.path.join(folder, "pack.json"), "w", encoding="utf-8") as f:
        json.dump(pack, f, indent=2)
    what = f"{len(pack['blocks'])} blocks" if pack["type"] == "voxel" else "a map"
    print(f"Made '{name}' with {what}: {os.path.join(folder, 'pack.json')}")
    print("Start Gorilla Tag and pick it from the wrist menu (or edit pack.json to tweak it).")


if __name__ == "__main__":
    main()
