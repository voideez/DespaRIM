using System.Collections.Generic;
using RimWorld;
using Verse;
using HarmonyLib;

namespace DespaRIM.Patches
{
    /// <summary>
    /// Dev Mode: buttons in the selected pawn's inspector (only visible with development mode enabled).
    /// </summary>
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
    public static class Patch_Pawn_GetGizmos
    {
        [HarmonyPostfix]
        public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, Pawn __instance)
        {
            foreach (Gizmo g in __result)
                yield return g;

            if (!Prefs.DevMode || __instance == null || !__instance.Spawned)
                yield break;
            if (__instance.RaceProps == null || !__instance.RaceProps.Humanlike)
                yield break;
            if (__instance.Faction != Faction.OfPlayer && !__instance.IsPrisonerOfColony)
                yield break;

            foreach (Gizmo g in DebugActions_Despair.GetDevGizmos(__instance))
                yield return g;
        }
    }
}
