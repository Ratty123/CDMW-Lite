"""Assert that an exported GLB or FBX carries a rig Blender can actually pose.

Run headless, with no addon beyond the shipped importers:

    blender --background --python scripts/blender/verify_rigged_mesh.py -- <file.glb|file.fbx> [--bones N] [--bone "Bip01 R UpperArm"]

This runs inside Blender, on Blender's own interpreter. It is not part of Archive Lite's product,
its build, or its focused test gate; it exists because the one thing no reader of the file's own
bytes can establish is whether a real importer takes the rig and moves the mesh with it.

Every check raises on failure. A script that prints a complaint and exits zero is a script that
reports a broken rig as a pass, which is the failure mode worth guarding against here: a glTF with
joints and weights imports perfectly happily while driving nothing at all.
"""

import sys
import math

import bpy
from mathutils import Quaternion

WEIGHT_TOLERANCE = 1.0e-4

# What counts as a vertex having moved, as a fraction of the model's own size. Relative because
# the two formats do not arrive at the same scale: Blender reads an FBX as centimetres and lands
# the same character a hundredth the size of its GLB, and a fixed threshold is then a hundred
# times stricter on one than the other -- quietly discounting real deformation on the smaller.
MOVEMENT_FRACTION = 5.0e-5

# Bones to pose. The first is a limb far enough down the arm to move a recognisable share of a
# body; the second is small and distal, so the vertices it moves have to be tightly around it.
DEFAULT_POSE_BONES = ("Bip01 R UpperArm", "Bip01 R Hand")


class RigCheckError(AssertionError):
    """A rig that did not survive the round trip."""


def require(condition, message):
    if not condition:
        raise RigCheckError(message)


def parse_arguments(argv):
    arguments = argv[argv.index("--") + 1:] if "--" in argv else []
    require(arguments, "no .glb path was given after --")
    options = {"path": arguments[0], "bones": None, "pose_bones": []}
    index = 1
    while index < len(arguments):
        flag = arguments[index]
        if flag == "--bones":
            options["bones"] = int(arguments[index + 1])
            index += 2
        elif flag == "--bone":
            options["pose_bones"].append(arguments[index + 1])
            index += 2
        else:
            raise RigCheckError(f"unrecognised argument {flag!r}")
    if not options["pose_bones"]:
        options["pose_bones"] = list(DEFAULT_POSE_BONES)
    return options


def import_mesh(path):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    lowered = path.lower()
    if lowered.endswith(".glb") or lowered.endswith(".gltf"):
        bpy.ops.import_scene.gltf(filepath=path)
    elif lowered.endswith(".fbx"):
        bpy.ops.import_scene.fbx(filepath=path)
    else:
        raise RigCheckError(f"{path!r} is neither a .glb nor a .fbx")


def find_armature():
    armatures = [obj for obj in bpy.data.objects if obj.type == "ARMATURE"]
    require(len(armatures) == 1, f"expected exactly one armature, found {len(armatures)}")
    return armatures[0]


def find_deformed_meshes(armature):
    """Every mesh the armature actually drives.

    Selected by their armature modifier, never by taking the first MESH object: an import can
    bring in geometry alongside the real mesh, and picking the first one tests whichever object
    happened to be created first while reporting on the one that matters.

    There may be more than one. A GLB export puts every submesh in a single mesh under one node,
    an FBX export gives each its own object, and both are bound to the same armature.
    """

    meshes = [
        obj for obj in bpy.data.objects
        if obj.type == "MESH"
        and any(mod.type == "ARMATURE" and mod.object == armature for mod in obj.modifiers)
    ]
    require(meshes, "no mesh object is bound to the armature by an armature modifier")
    return meshes


def check_weights(meshes, armature):
    bone_names = {bone.name for bone in armature.data.bones}
    unweighted = 0
    off_unity = 0
    worst = 0.0
    vertices = 0
    groups = set()
    for mesh in meshes:
        group_names = {group.index: group.name for group in mesh.vertex_groups}
        unknown = sorted(name for name in group_names.values() if name not in bone_names)
        require(
            not unknown,
            f"{mesh.name}: {len(unknown)} vertex group(s) name no bone in the armature: {unknown[:5]}",
        )
        groups.update(group_names.values())
        vertices += len(mesh.data.vertices)
        for vertex in mesh.data.vertices:
            total = sum(element.weight for element in vertex.groups)
            if not vertex.groups or total <= 0.0:
                unweighted += 1
                continue
            worst = max(worst, abs(total - 1.0))
            if abs(total - 1.0) > WEIGHT_TOLERANCE:
                off_unity += 1
    require(unweighted == 0, f"{unweighted} vertices belong to no vertex group")
    require(
        off_unity == 0,
        f"{off_unity} vertices have weights that do not sum to 1.0 (worst error {worst:.6g})",
    )
    return vertices, len(groups), worst


def evaluated_positions(mesh):
    depsgraph = bpy.context.evaluated_depsgraph_get()
    evaluated = mesh.evaluated_get(depsgraph)
    data = evaluated.to_mesh()
    positions = [evaluated.matrix_world @ vertex.co.copy() for vertex in data.vertices]
    evaluated.to_mesh_clear()
    return positions


def model_size(positions_per_mesh):
    """The diagonal of everything the armature drives, so thresholds can be relative to it."""

    lows = [float("inf")] * 3
    highs = [float("-inf")] * 3
    for positions in positions_per_mesh:
        for position in positions:
            for axis in range(3):
                lows[axis] = min(lows[axis], position[axis])
                highs[axis] = max(highs[axis], position[axis])
    if lows[0] > highs[0]:
        return 1.0
    return max(math.dist(lows, highs), 1.0e-12)


def check_deformation(meshes, armature, bone_name):
    """Pose one bone and assert the vertices that moved are the ones near it.

    A rig can import with every joint, every weight and every group in place and still drive
    nothing -- wrong inverse bind matrices, joints bound to the wrong nodes, a skin the mesh does
    not reference. Nothing short of moving a bone and watching the vertices catches that.

    Every bound mesh is measured together. An FBX export splits the submeshes into separate
    objects, and most of them are legitimately unmoved by any one bone -- a head does not follow
    an upper arm -- so judging each on its own would demand movement where there should be none.
    """

    require(bone_name in armature.pose.bones, f"the armature has no bone named {bone_name!r}")
    bpy.context.view_layer.update()
    rest = [evaluated_positions(mesh) for mesh in meshes]
    origin = armature.matrix_world @ armature.pose.bones[bone_name].head.copy()

    pose_bone = armature.pose.bones[bone_name]
    previous_mode = pose_bone.rotation_mode
    pose_bone.rotation_mode = "QUATERNION"
    pose_bone.rotation_quaternion = Quaternion((1.0, 0.0, 0.0), math.radians(45.0))
    bpy.context.view_layer.update()
    posed = [evaluated_positions(mesh) for mesh in meshes]

    pose_bone.rotation_quaternion = Quaternion()
    pose_bone.rotation_mode = previous_mode
    bpy.context.view_layer.update()

    threshold = model_size(rest) * MOVEMENT_FRACTION
    moved_distances = []
    still_distances = []
    for before_mesh, after_mesh in zip(rest, posed):
        require(len(before_mesh) == len(after_mesh), "posing the rig changed the vertex count")
        for before, after in zip(before_mesh, after_mesh):
            distance_to_bone = (before - origin).length
            if (after - before).length > threshold:
                moved_distances.append(distance_to_bone)
            else:
                still_distances.append(distance_to_bone)

    require(moved_distances, f"posing {bone_name!r} by 45 degrees moved no vertex at all")
    require(still_distances, f"posing {bone_name!r} moved every vertex in the mesh")
    moved_mean = sum(moved_distances) / len(moved_distances)
    still_mean = sum(still_distances) / len(still_distances)
    require(
        moved_mean < still_mean,
        f"posing {bone_name!r} moved vertices a mean {moved_mean:.4f} from the bone against "
        f"{still_mean:.4f} for those that stayed put; the rig is bound to the wrong points",
    )
    return len(moved_distances), moved_mean, still_mean


def main():
    options = parse_arguments(sys.argv)
    import_mesh(options["path"])
    armature = find_armature()
    bone_count = len(armature.data.bones)
    print(f"armature {armature.name!r}: {bone_count} bones")
    if options["bones"] is not None:
        require(
            bone_count == options["bones"],
            f"expected {options['bones']} bones, imported {bone_count}",
        )

    meshes = find_deformed_meshes(armature)
    vertices, groups, worst = check_weights(meshes, armature)
    print(f"mesh {', '.join(repr(mesh.name) for mesh in meshes)}: {vertices} vertices, "
          f"{groups} vertex groups, 0 unweighted, 0 off 1.0 (worst error {worst:.3g})")

    for bone_name in options["pose_bones"]:
        moved, moved_mean, still_mean = check_deformation(meshes, armature, bone_name)
        print(f"posing {bone_name!r}: moved {moved} vertices at mean distance {moved_mean:.3f} "
              f"from the bone against {still_mean:.3f} for those that did not")

    print("RIG OK")


if __name__ == "__main__":
    try:
        main()
    except Exception as error:  # noqa: BLE001 - a headless run has to fail with a nonzero status
        print(f"RIG CHECK FAILED: {error}", file=sys.stderr)
        sys.exit(1)
