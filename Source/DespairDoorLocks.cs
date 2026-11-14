using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DespaRIM
{
    /// <summary>
    /// Registry of doors locked by despairing pawns: door → who locked it.
    ///
    /// Building_Door.PawnCanOpen is called very often (pathfinding, regions, reachability),
    /// so the check is as cheap as possible: when nothing is locked (almost always),
    /// the patch exits after a single Count == 0 check.
    ///
    /// The registry is not saved by itself. The source of truth is the door list inside
    /// MentalState_Despair (which is saved). After loading a save the registry is rebuilt
    /// from the active breaks (GameComponent_DespairTracker.FinalizeInit).
    ///
    /// THREAD SAFETY: in RimWorld 1.6 pathfinding calls PawnCanOpen from Unity Jobs worker
    /// threads (PathGridDoorsBlockedJob). The registry therefore uses copy-on-write: the dictionary
    /// readers see is never mutated. Writers (main thread only) build a new dictionary and swap
    /// the reference atomically. Readers take no locks and can never observe a dictionary
    /// in the middle of a modification.
    /// </summary>
    public static class DespairDoorLocks
    {
        private static volatile Dictionary<Building_Door, Pawn> locks = new Dictionary<Building_Door, Pawn>();
        private static readonly object writeLock = new object();
        private static readonly List<Building_Door> tmpRemove = new List<Building_Door>();

        public static int Count => locks.Count;

        /// <summary>Atomically replaces the registry snapshot (copy-on-write).</summary>
        private static void Mutate(System.Action<Dictionary<Building_Door, Pawn>> change)
        {
            lock (writeLock)
            {
                var copy = new Dictionary<Building_Door, Pawn>(locks);
                change(copy);
                locks = copy;
            }
        }

        /// <summary>Full reset (new game or save load).</summary>
        public static void Clear()
        {
            lock (writeLock)
            {
                locks = new Dictionary<Building_Door, Pawn>();
            }
        }

        /// <summary>Locks a door on behalf of a pawn.</summary>
        public static void Lock(Building_Door door, Pawn locker)
        {
            if (door == null || locker == null || !door.Spawned)
                return;
            Mutate(d => d[door] = locker);
            ClearReachability(door.Map);
        }

        /// <summary>Unlocks every door locked by this pawn.</summary>
        public static void UnlockAllFor(Pawn locker)
        {
            if (locker == null || locks.Count == 0)
                return;

            Map mapToClear = null;
            tmpRemove.Clear();
            foreach (var kv in locks)
            {
                if (kv.Value == locker)
                    tmpRemove.Add(kv.Key);
            }
            if (tmpRemove.Count == 0)
                return;

            for (int i = 0; i < tmpRemove.Count; i++)
            {
                Building_Door door = tmpRemove[i];
                if (door != null && door.Spawned)
                    mapToClear = door.Map;
            }
            Mutate(d =>
            {
                for (int i = 0; i < tmpRemove.Count; i++)
                    d.Remove(tmpRemove[i]);
            });
            tmpRemove.Clear();

            ClearReachability(mapToClear ?? locker.MapHeld);
        }

        /// <summary>Whether the door is locked by a despairing pawn (for anyone but that pawn).</summary>
        public static bool IsLocked(Building_Door door)
        {
            var snapshot = locks;
            if (door == null || snapshot.Count == 0)
                return false;
            return snapshot.TryGetValue(door, out Pawn locker) && IsLockStillValid(door, locker);
        }

        /// <summary>Who locked the door (or null).</summary>
        public static Pawn GetLocker(Building_Door door)
        {
            var snapshot = locks;
            if (door == null || snapshot.Count == 0)
                return null;
            return snapshot.TryGetValue(door, out Pawn locker) && IsLockStillValid(door, locker) ? locker : null;
        }

        /// <summary>
        /// Main check used by the Harmony patch: is this pawn forbidden from opening the door?
        /// The pawn who locked it can still open it (but won't: they are busy with their job).
        /// Null- and destroyed-object safe. Never touches any caches: it is called from inside
        /// reachability calculations, where clearing caches would be dangerous.
        /// </summary>
        public static bool IsLockedAgainst(Building_Door door, Pawn pawn)
        {
            var snapshot = locks; // local reference to an immutable snapshot
            if (snapshot.Count == 0 || door == null || pawn == null)
                return false;
            if (!snapshot.TryGetValue(door, out Pawn locker))
                return false;
            if (locker == pawn)
                return false;
            return IsLockStillValid(door, locker);
        }

        /// <summary>
        /// A lock only holds while the door is still on the map and the pawn who locked it
        /// is alive, on the same map and still in despair.
        /// </summary>
        public static bool IsLockStillValid(Building_Door door, Pawn locker)
        {
            if (door == null || locker == null)
                return false;
            if (door.Destroyed || !door.Spawned || door.Map == null)
                return false;
            if (locker.Dead || locker.Destroyed || !locker.Spawned || locker.Map != door.Map)
                return false;
            return locker.MentalState is MentalState_Despair;
        }

        /// <summary>
        /// Removes stale entries (broken doors, dead or rescued pawns).
        /// Called from the regular tick, never from the patch.
        /// </summary>
        public static void PruneInvalid()
        {
            if (locks.Count == 0)
                return;

            tmpRemove.Clear();
            foreach (var kv in locks)
            {
                if (!IsLockStillValid(kv.Key, kv.Value))
                    tmpRemove.Add(kv.Key);
            }
            if (tmpRemove.Count == 0)
                return;

            var maps = new HashSet<Map>();
            for (int i = 0; i < tmpRemove.Count; i++)
            {
                Building_Door door = tmpRemove[i];
                if (door != null && door.Spawned && door.Map != null)
                    maps.Add(door.Map);
            }
            Mutate(d =>
            {
                for (int i = 0; i < tmpRemove.Count; i++)
                    d.Remove(tmpRemove[i]);
            });
            tmpRemove.Clear();

            foreach (Map map in maps)
                ClearReachability(map);
            if (maps.Count == 0 && Find.CurrentMap != null)
                ClearReachability(Find.CurrentMap);
        }

        /// <summary>Rebuilds the registry from the active breaks (after loading a save).</summary>
        public static void RebuildFromGame()
        {
            Clear();
            if (Find.Maps == null)
                return;

            foreach (Map map in Find.Maps)
            {
                IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
                for (int i = 0; i < pawns.Count; i++)
                {
                    if (pawns[i].MentalState is MentalState_Despair state)
                        state.ReRegisterLocks();
                }
            }
        }

        private static void ClearReachability(Map map)
        {
            // Door passability changed, so the map's reachability cache must be reset
            map?.reachability?.ClearCache();
        }
    }
}
