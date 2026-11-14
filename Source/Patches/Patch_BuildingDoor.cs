using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DespaRIM.Patches
{
    /// <summary>
    /// Blocks doors locked by a despairing pawn.
    ///
    /// Building_Door.PawnCanOpen is the single place where the game decides whether a pawn may open a door.
    /// Regions and reachability (Region.Allows → CanPhysicallyPass), path following (Pawn_PathFollower),
    /// path costs (PathUtility) and more all go through it.
    /// If PawnCanOpen returns false, the door is closed to everyone except the pawn who locked it.
    ///
    /// Compatibility:
    ///  • Postfix rather than Prefix: other mods' results are never overridden, we only turn true → false.
    ///  • Both the base method and every override in subclasses (including other mods' doors) are patched;
    ///    otherwise a subclass that does not call base would bypass the lock.
    ///  • Fully null- and destroyed-door safe; any exception is swallowed and logged once.
    ///  • Cheap check: if nothing is locked, it exits immediately (almost always).
    /// </summary>
    [HarmonyPatch]
    public static class Patch_BuildingDoor_PawnCanOpen
    {
        private static bool errorLogged;

        [HarmonyTargetMethods]
        public static IEnumerable<MethodBase> TargetMethods()
        {
            var result = new List<MethodBase>();

            MethodInfo baseMethod = AccessTools.Method(typeof(Building_Door), nameof(Building_Door.PawnCanOpen), new[] { typeof(Pawn) });
            if (baseMethod != null)
                result.Add(baseMethod);

            // Every Building_Door subclass (vanilla and modded) that declares its own PawnCanOpen override
            foreach (Type sub in typeof(Building_Door).AllSubclasses())
            {
                MethodInfo m = sub.GetMethod(nameof(Building_Door.PawnCanOpen),
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                    null, new[] { typeof(Pawn) }, null);
                if (m != null && !m.IsAbstract && m.HasMethodBody() && !result.Contains(m))
                    result.Add(m);
            }
            return result;
        }

        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        public static void Postfix(Building_Door __instance, Pawn p, ref bool __result)
        {
            // The door already won't open for this pawn; nothing to change
            if (!__result)
                return;
            if (DespairDoorLocks.Count == 0 || __instance == null || p == null)
                return;

            try
            {
                if (DespairDoorLocks.IsLockedAgainst(__instance, p))
                    __result = false;
            }
            catch (Exception e)
            {
                if (!errorLogged)
                {
                    errorLogged = true;
                    Log.Error("[DespaRIM] Door lock check failed (shown once): " + e);
                }
            }
        }
    }
}
