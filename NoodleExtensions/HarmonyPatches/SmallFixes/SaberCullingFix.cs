using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Heck;
using UnityEngine;

namespace NoodleExtensions.HarmonyPatches.SmallFixes;

[HeckPatch(PatchType.Features)]
[HarmonyPatch(typeof(SaberTrailRenderer))]
internal static class SaberCullingFix
{
    // Never cull trails, but keep the box finite: Unity 6 turns infinite extents into
    // NaN world bounds and asserts "Invalid AABB" on every culling pass.
    private static readonly Bounds _bounds = new(Vector3.zero, Vector3.one * 1_000_000f);
    private static readonly MethodInfo _boundsSetter = AccessTools.PropertySetter(typeof(Mesh), nameof(Mesh.bounds));

    [HarmonyPostfix]
    [HarmonyPatch(typeof(SaberTrailRenderer), nameof(SaberTrailRenderer.UpdateMesh))]
    private static void InfiniteBounds(Mesh ____mesh)
    {
        ____mesh.bounds = _bounds;
    }

    [HarmonyTranspiler]
    [HarmonyPatch(nameof(SaberTrailRenderer.UpdateMesh))]
    private static IEnumerable<CodeInstruction> PreventBoundsUpdate(IEnumerable<CodeInstruction> instructions)
    {
        return new CodeMatcher(instructions)
            /*
             * this._mesh.triangles = this._indices;
             * -- this._mesh.bounds = SaberTrailRenderer._bounds;
             */
            .MatchForward(false, new CodeMatch(OpCodes.Callvirt, _boundsSetter))
            .Advance(-3)
            .RemoveInstructions(4)
            .InstructionEnumeration();
    }
}
