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
