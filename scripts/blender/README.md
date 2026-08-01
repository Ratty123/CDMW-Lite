# Blender verification scripts

Scripts here run inside Blender, on the interpreter Blender ships. They are not part of Archive
Lite's product, its build, or `scripts/test_archive_lite.ps1`, and nothing in the application
invokes them. `scripts/verify_archive_lite_source.ps1` exempts this one directory from the
Python ban for that reason, and only this one.

They exist because a rigged export has a property no reader of the file's own bytes can check:
whether a real importer takes the armature and deforms the mesh with it. A glTF whose joints are
bound to the wrong vertices, or whose inverse bind matrices are wrong, still parses cleanly, still
weights every vertex to 1.0, and still drives nothing.

## verify_rigged_glb.py

```bash
"C:\Program Files\Blender Foundation\Blender 5.1\blender.exe" --background --python scripts/blender/verify_rigged_glb.py -- exported.glb --bones 249
```

Asserts, failing with a nonzero status rather than printing and passing:

1. Exactly one armature imports, with the expected bone count when `--bones` is given.
2. A mesh is bound to it by an armature modifier, and the mesh is selected *by that modifier* --
   not by taking the first `MESH` object, which can be placeholder geometry the import brought in
   alongside the real mesh.
3. Every vertex belongs to a vertex group, and every group names a bone the armature has.
4. Every vertex's weights sum to 1.0.
5. Posing a named bone 45 degrees moves vertices, leaves others alone, and the ones that moved are
   nearer that bone than the ones that did not.

Pass `--bone "<name>"` to choose which bones to pose; it defaults to `Bip01 R UpperArm` and
`Bip01 R Hand`. Use bones the rig actually has: an armour piece carries only the bones it binds to
plus their ancestors, so a legwear mesh has `Bip01 R Thigh` but no `Bip01 R Hand`.

Reference runs on exports from the shipped archives:

| asset | bones | vertices | posed bone | moved | mean distance, moved vs still |
| --- | --- | --- | --- | --- | --- |
| `cd_phw_00_nude_00_0001_damian.pac` | 249 | 13,740 | `Bip01 R UpperArm` | 3,153 | 0.490 vs 0.730 |
| `cd_phw_00_nude_00_0001_damian.pac` | 249 | 13,740 | `Bip01 R Hand` | 1,983 | 0.099 vs 0.681 |
| `cd_phw_00_lb_0057.pac` | 53 | 8,379 | `Bip01 R Thigh` | 5,547 | 0.287 vs 0.320 |
| `cd_phm_00_ub_0003.pac` | 101 | 2,768 | `Bip01 Spine1` | 2,128 | 0.293 vs 0.373 |

All four report 0 unweighted vertices and 0 whose weights miss 1.0.

## What is not rigged, and why

A mesh only gets an armature when the `.pab` its own name or directory nominates accounts for its
palette. Across a random sweep of 23 character `.pac` files, 10 came back rigged, 1 rigid, and 12
`palette_unresolved` — NPCs, monsters, and a few player accessories. That last group is a real
coverage gap rather than a defect, and widening the search is not the fix:

- Two of them (a helmet accessory and a pike) resolve against **no** skeleton in the archive at
  all. Their rig is not among the 257 `.pab` files that ship.
- The rest resolve against **several**. An NPC upper body's 88-entry palette resolves completely
  against nine different humanoid rigs, a monster's against ten, and one head mesh's against
  twenty-four, because a palette of common biped bone names is satisfied by any biped rig. Once
  the rig is not nominated by name, "the palette resolves" stops identifying which rig it is, and
  binding to the wrong one gives an armature of the right bone *names* in the wrong *places*.

There is no NPC skeleton in the archive — no `nhm`/`nhw` `.pab` exists — so those meshes must
borrow a rig that is named nowhere in their own path. Closing that gap needs evidence beyond the
palette, and until there is some, those meshes export unrigged with the reason recorded in the
package manifest's `skeleton.note`.
