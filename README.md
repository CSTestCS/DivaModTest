# VRoidDiva: VRoid models as Project DIVA Mega Mix+ modules

VRoidDiva converts a VRoid Studio model (`.vrm`, VRM 0.x or 1.0) into a
[DIVA Mod Loader](https://github.com/blueskythlikesclouds/DivaModLoader) mod for
**Hatsune Miku: Project DIVA Mega Mix+** (Steam). The mod adds a new module that you
can pick on the module select screen. Your model then dances in every song with the
game's own motions.

## Quick start (no command line needed)

1. Install [DIVA Mod Loader](https://github.com/blueskythlikesclouds/DivaModLoader/releases)
   into the game folder, if you haven't already.
2. Download **VRoidDiva.exe** from this repository's
   [Releases page](../../releases/tag/latest) and double-click it.
   Windows may warn about an unrecognised app: choose *More info → Run anyway*.
3. Click **Browse…** next to *VRoid model* and pick your `.vrm` file (or drag the
   file onto the window).
4. Check the *Game folder*. It is found automatically for normal Steam installs;
   otherwise click **Browse…** and pick the folder containing `DivaMegaMix.exe`
   (in Steam: right-click the game → *Manage → Browse local files*).
5. Type a module name, pick the character, and press **Convert**.
6. Start the game and choose the new module on that character's module select screen.

The app writes the mod straight into the game's `mods` folder and reads the game's
own `diva_main.cpk` for the skeleton, so there is nothing else to set up.
`VRoidDivaCli.exe` on the same page is the command-line version described under
[Usage](#usage-command-line).

> **Status:** the converter is fully tested against the file formats (every output
> file is read back with MikuMikuLibrary, and real VRoid/VRM sample models are
> converted in the test suite), but it has **not yet been tried in the running game**.
> Please report what you see. The [limitations](#limitations) section lists what
> is known not to work.

## How it works

1. **Read the VRM.** The meshes, skin weights, materials, embedded textures and
   humanoid bone assignments are read directly from the `.vrm` (binary glTF).
2. **Read the game's skeleton.** DIVA motions drive the game's own skeleton, so a
   module must be authored in the game's bind pose and must reference the game's
   bone IDs. VRoidDiva takes both from a stock module in *your* game files
   (`--reference`). No game data is included in this repository.
3. **Fit the model to the skeleton.** The VRM is turned to face the game's
   direction and scaled to the character's height. Then every humanoid bone (hips,
   spine, arms, legs, fingers, …) is moved onto the matching DIVA joint and rotated
   along the DIVA bone. Arms go from the VRM T-pose into DIVA's A-pose, and limbs
   are stretched to DIVA's bone lengths so elbows and knees bend in the right
   place. Vertices are blended through these transforms with their own skin
   weights, so the surface stays continuous. Bones that are not humanoid (hair,
   skirt, accessories, VRM 1.0 twist and aim helpers) follow the humanoid bone that
   drives them.
4. **Write the mod.** Object set (`.farc`), textures (DXT1/DXT5 with mipmaps),
   `mod_obj_db`/`mod_tex_db`, module table, character item table and the module
   name are written as DIVA Mod Loader `mod_` databases, which coexist with other
   mods.

The generated module equips one *replace* item. It replaces all fourteen stock base
body parts (head, torso, arms, hands, legs, feet): the converted model takes one
slot, and the other slots get an invisible stand-in object so none of Miku's own
body shows through.

## Requirements

- Hatsune Miku: Project DIVA Mega Mix+ with
  [DIVA Mod Loader](https://github.com/blueskythlikesclouds/DivaModLoader/releases)
  installed (or a mod manager that installs it).
- A VRoid model exported as `.vrm` (VRoid Studio: *Export → Export as VRM*).
- To build from source: the [.NET 8 SDK](https://dotnet.microsoft.com/download).
  The window app is Windows-only; the command-line version also runs on Linux
  (including Steam Deck) and macOS.

## Building

```sh
git clone --recurse-submodules <this repository>
cd DivaModTest
dotnet build -c Release
```

Run the command-line version with `dotnet run --project src/VRoidDiva -- <arguments>`.
To get single Windows executables (`VRoidDiva.exe` is the window app,
`VRoidDivaCli.exe` the command-line version):

```sh
dotnet publish src/VRoidDiva.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
dotnet publish src/VRoidDiva -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

The CI workflow (`.github/workflows/ci.yml`) builds both on every push and
publishes them on the [Releases page](../../releases/tag/latest).

## Usage (command line)

The examples call the command-line version `VRoidDiva`; the downloaded file is
named `VRoidDivaCli.exe`.

### 1. Point it at the game's skeleton

`--reference` accepts any of:

- the game's `.cpk` (in the game folder). VRoidDiva then reads Miku's default
  module items `rom/objset/mikitm001/301/501/681.farc` (hair, body, hands, shoes)
  from it. Use `--cpk-entry` (repeatable) to choose other archives.
- extracted object set archives (`.farc`) or `*_obj.bin` files, for example
  extracted with [Miku Miku Model](https://github.com/blueskythlikesclouds/MikuMikuLibrary/releases).
  `--reference` can be repeated, and the bones of all files are merged.

Check what a reference provides with:

```
VRoidDiva inspect mikitm301.farc
```

This lists every skinned object, its bones with their IDs and bind positions, and
which VRM humanoid bones the default bone map can place. You need at least the
hips, head and both upper arms. For good results you also want the arms, hands,
legs and feet, which usually means passing the hair, body, hands and shoes items
of one module together.

> The `.cpk` is read with MikuMikuLibrary's Mega Mix+ CPK reader (the same one Miku
> Miku Model uses). If that fails for your copy of the game, extract the archives
> with Miku Miku Model and pass the `.farc` files instead.

### 2. Convert

```
VRoidDiva convert MyCharacter.vrm ^
    --reference "C:\...\Hatsune Miku Project DIVA Mega Mix Plus\diva_main.cpk" ^
    --out "C:\...\Hatsune Miku Project DIVA Mega Mix Plus\mods\My Character" ^
    --name "My Character"
```

| Option | Meaning |
| --- | --- |
| `--name <text>` | Module name shown in the game (default: VRM title) |
| `--chara <name>` | Character the module belongs to: `MIKU` (default), `RIN`, `LEN`, `LUKA`, `NERU`, `HAKU`, `KAITO`, `MEIKO`, `SAKINE`, `TETO` |
| `--module-id <n>` | Module ID (default: derived from the name, 4000–8999) |
| `--item-no <n>` | Character item number (default: module ID) |
| `--cos <n>` | Costume number, `COS_<n>` (default: derived from the name, 600–998) |
| `--objset-id <n>` | Object set ID, 1–65535 (default: derived from the name, 40000–64999) |
| `--sort-index <n>` | Position in the module list (default: module ID) |
| `--bone-map <file>` | Bone map overrides (see below) |
| `--max-texture <px>` | Downscale larger textures (default 2048) |
| `--scale <f>` | Extra uniform scale after height matching (default 1.0) |
| `--no-stretch` | Keep the model's own limb lengths instead of DIVA's |
| `--all-cloth` | Use the `CLOTH` shader for every material |
| `--author <text>` | Author for `config.toml` |

Default IDs are derived from the module name, so the same name always gives the
same IDs. **IDs must not collide with other mods.** If another module, costume,
item or object set already uses one, pick free values with the options above.

### 3. Play

Copy (or write directly) the output folder into the game's `mods` folder and start
the game. The module appears for the chosen character. The output looks like this:

```
My Character/
├── config.toml                       DIVA Mod Loader manifest
└── rom/
    ├── mod_gm_module_tbl.farc        module entry (gm_module_id.bin)
    ├── mod_chritm_prop.farc          costume + item (e.g. mikitm_tbl.txt)
    ├── lang2/mod_str_array.toml      module name (module.<id> = "...")
    └── objset/
        ├── mikitm<item>.farc         the model (_obj.bin + _tex.bin)
        ├── mod_obj_db.bin
        └── mod_tex_db.bin
```

### Other commands

- `VRoidDiva vrm-info model.vrm` lists the VRM's humanoid bones, materials (with
  the DIVA shader each will get) and mesh statistics.
- `VRoidDiva inspect <reference>` is described above.

## Bone map

The default mapping (see `src/VRoidDiva/Retarget/BoneMap.cs`) uses the bone names of
the game's character skeleton:

| VRM humanoid | DIVA bone |
| --- | --- |
| hips | `kl_kosi_etc_wj` |
| spine | `j_mune_wj` |
| chest / upperChest | `kl_mune_b_wj` (the first one present claims it; the other follows it) |
| neck / head | `kl_kubi` / `j_kao_wj` |
| leftEye / rightEye / jaw | `kl_eye_l` / `kl_eye_r` / `kl_ago_wj` |
| left shoulder / upper arm / lower arm / hand | `kl_waki_l_wj` / `j_kata_l_wj_cu` / `j_ude_l_wj` / `kl_te_l_wj` |
| left thumb, index, middle, ring, little | `nl_oya_l_wj`, `nl_hito_l_wj`, `nl_naka_l_wj`, `nl_kusu_l_wj`, `nl_ko_l_wj` (+ `_b_`, `_c_` for the next joints) |
| left upper leg / lower leg / foot / toes | `j_momo_l_wj` / `j_sune_l_wj` / `kl_asi_l_wj_co` / `kl_toe_l_wj` |

The right side is the same with `_r_`. If a DIVA bone is missing from your
reference, that VRM bone's vertices follow its parent bone, and the tool prints a
warning. To change the mapping, pass a JSON file with `--bone-map`. Each VRM bone
gets a list of candidates; the first one present in the reference is used, and an
empty list disables the bone:

```json
{
  "jaw": [],
  "leftEye": ["kl_eye_l"]
}
```

VRM 0.x thumb names are converted to VRM 1.0 names (`ThumbMetacarpal`,
`ThumbProximal`, `ThumbDistal`) before mapping. `data/bone_map.example.json`
contains a sample.

## Limitations

- **No physics.** Hair, skirts and other spring bones are fixed to the bone they hang
  from (head, hips, chest…). They do not swing, and long skirts can clip through
  the legs during dances.
- **No facial expressions.** VRM expressions are blend shapes, which DIVA objects
  don't support. The face keeps its neutral expression and the mouth doesn't move.
  The eyes follow the game's eye movement only if your reference provides
  `kl_eye_l`/`kl_eye_r` as skin bones.
- **Materials are simplified.** Each material gets its base colour texture with the
  `SKIN` shader (skin and face materials) or the `CLOTH` shader (everything else).
  MToon shade colours, rim light, outlines, emission and normal maps are not
  converted. Masked and blended transparency are kept.
- **No module picture.** The module select screen shows no thumbnail sprite for the
  new module.
- **The stock face is hidden.** The module replaces the whole body, including Miku's
  head, so features tied to the stock face do not apply to the converted model.
- **Fit quality depends on proportions.** Models with very different proportions
  (chibi, very long arms) are stretched onto DIVA's skeleton. Stretching is limited
  to 0.5–2× per bone, and `--no-stretch` turns it off.

## Development

```sh
dotnet test                       # unit + end-to-end conversion tests
VROIDDIVA_SAMPLE_VRMS=/path/a.vrm:/path/b.vrm dotnet test   # also convert real models
```

The end-to-end tests generate a synthetic VRoid-style model and a synthetic DIVA
reference skeleton, run the full `convert` command, then read every output file
back with MikuMikuLibrary. They check that the joints land on the DIVA joints, that
VRM 0.x and 1.0 facing is handled, that limbs are stretched to DIVA lengths, that
VRM 1.0 constraint helpers follow their limb, that bone IDs and bind matrices match
the reference, and that the tables and databases agree with each other.

Project layout:

- `src/VRoidDiva.App`: the Windows window app (`VRoidDiva.exe`)
- `src/VRoidDiva`: the converter and the command-line version (`VRoidDivaCli.exe`);
  `Converter.cs` runs the whole pipeline, `GameLocator.cs` finds the game
- `src/VRoidDiva/Vrm`: glTF/VRM reader
- `src/VRoidDiva/Retarget`: bone map and the fitting onto the DIVA skeleton
- `src/VRoidDiva/Diva`: reference skeleton loader, object/texture builders, module
  tables and the mod writer
- `external/MikuMikuLibrary`: [MikuMikuLibrary](https://github.com/blueskythlikesclouds/MikuMikuLibrary)
  (git submodule), used for the DIVA object, texture, database and archive formats.
  Only its managed code is used, so no Windows-only native components are needed.

File formats and game behaviour (module and item tables, base body parts, bone
names) follow DIVA Mod Loader's documentation and the
[ReDIVA](https://github.com/korenkonder/ReDIVA) reimplementation of the game.
