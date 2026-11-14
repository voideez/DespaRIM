using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace DespaRIM
{
    /// <summary>Where the pawn chose to live out their despair (used in letters and the inspector).</summary>
    public enum DespairPlaceKind : byte
    {
        Here = 0,        // no suitable room, stays where they are
        Bedroom = 1,     // their own bedroom
        CurrentRoom = 2, // the room they are already in
        OtherRoom = 3,   // the nearest quiet room
        PrisonCell = 4   // their own cell (prisoner)
    }

    /// <summary>
    /// Shared checks and place-finding for the Despair break.
    /// </summary>
    public static class DespairUtility
    {
        /// <summary>Maximum size (in cells) of a room that can be locked.</summary>
        public const int MaxLockableRoomCells = 160;

        /// <summary>Maximum number of doors a lockable room may have.</summary>
        public const int MaxLockableRoomDoors = 4;

        /// <summary>How far to search for someone else's quiet room if the pawn has none.</summary>
        public const float MaxOtherRoomSearchDistance = 70f;

        private static readonly List<Building_Door> tmpDoors = new List<Building_Door>();

        // ===================================================================
        // Candidates
        // ===================================================================

        /// <summary>
        /// Whether the pawn takes part in the mechanic at all (accumulates stress and can break).
        /// That means player colonists (slaves included) and, if enabled in the settings, colony prisoners.
        /// Children, babies, mutants, quest lodgers and pawns without a mood need are excluded.
        /// </summary>
        public static bool IsCandidate(Pawn pawn)
        {
            if (pawn == null || pawn.Dead || pawn.Destroyed || !pawn.Spawned)
                return false;
            if (pawn.RaceProps == null || !pawn.RaceProps.Humanlike)
                return false;
            if (pawn.needs?.mood == null || pawn.mindState?.mentalStateHandler == null)
                return false;
            if (!pawn.DevelopmentalStage.Adult())
                return false;
            if (pawn.IsMutant || pawn.IsQuestLodger())
                return false;

            if (pawn.IsPrisonerOfColony)
                return DespaRIMMod.Settings.allowPrisoners;

            return pawn.Faction != null && pawn.Faction.IsPlayer && !pawn.IsPrisoner;
        }

        /// <summary>
        /// Immunity: while "Total despair" is active the break cannot happen again (prevents death loops).
        /// </summary>
        public static bool HasImmunity(Pawn pawn)
        {
            var memories = pawn?.needs?.mood?.thoughts?.memories;
            return memories != null && memories.GetFirstMemoryOfDef(DespaRIMDefOf.DespaRIM_TotalDespair) != null;
        }

        // ===================================================================
        // Rooms and doors
        // ===================================================================

        /// <summary>All doors adjacent to a room (found through its border cells).</summary>
        public static List<Building_Door> GetRoomDoors(Room room, Map map)
        {
            var result = new List<Building_Door>();
            if (room == null || map == null)
                return result;

            foreach (IntVec3 cell in room.BorderCells)
            {
                if (!cell.InBounds(map))
                    continue;
                if (cell.GetEdifice(map) is Building_Door door && !result.Contains(door))
                    result.Add(door);
            }
            return result;
        }

        /// <summary>
        /// Whether a room is suitable for locking oneself in: enclosed, not huge, not outdoors,
        /// not a doorway itself, with 1–4 doors.
        /// </summary>
        public static bool IsLockableRoom(Room room, Map map)
        {
            if (room == null || map == null)
                return false;
            if (room.IsDoorway || room.PsychologicallyOutdoors || room.TouchesMapEdge || room.IsHuge)
                return false;
            if (room.CellCount > MaxLockableRoomCells)
                return false;

            tmpDoors.Clear();
            tmpDoors.AddRange(GetRoomDoors(room, map));
            int count = tmpDoors.Count;
            tmpDoors.Clear();
            return count >= 1 && count <= MaxLockableRoomDoors;
        }

        /// <summary>
        /// Finds a cell inside the room for the pawn to stand on: standable, not a door and not a bed.
        /// Picks the one closest to the room's center so the pawn does not block a doorway.
        /// </summary>
        public static bool TryFindCellInRoom(Pawn pawn, Room room, out IntVec3 result)
        {
            result = IntVec3.Invalid;
            Map map = pawn.Map;
            if (room == null || map == null)
                return false;

            IntVec3 center = room.ExtentsClose.CenterCell;
            float bestDist = float.MaxValue;

            foreach (IntVec3 c in room.Cells)
            {
                if (!c.InBounds(map) || !c.Standable(map))
                    continue;
                Building edifice = c.GetEdifice(map);
                if (edifice is Building_Door || edifice is Building_Bed)
                    continue;
                if (!pawn.CanReach(c, PathEndMode.OnCell, Danger.Deadly))
                    continue;

                float d = c.DistanceToSquared(center);
                if (d < bestDist)
                {
                    bestDist = d;
                    result = c;
                }
            }
            return result.IsValid;
        }

        /// <summary>
        /// Picks where the despairing pawn will go:
        ///  • a prisoner stays in their current cell and goes nowhere;
        ///  • a colonist goes to their bedroom, otherwise stays in the current room,
        ///    otherwise looks for the nearest quiet room, otherwise stays where they are.
        /// </summary>
        public static IntVec3 FindDespairCell(Pawn pawn, out DespairPlaceKind kind)
        {
            kind = DespairPlaceKind.Here;
            Map map = pawn.Map;
            if (map == null)
                return pawn.PositionHeld;

            // --- Prisoner: their current cell only ---
            if (pawn.IsPrisonerOfColony)
            {
                Room cell = pawn.GetRoom();
                if (IsLockableRoom(cell, map))
                {
                    kind = DespairPlaceKind.PrisonCell;
                    return TryFindCellInRoom(pawn, cell, out IntVec3 c) ? c : pawn.Position;
                }
                return pawn.Position;
            }

            // --- 1. Own bedroom ---
            Room bedroom = pawn.ownership?.OwnedRoom;
            if (IsLockableRoom(bedroom, map) && TryFindCellInRoom(pawn, bedroom, out IntVec3 bedCell))
            {
                kind = DespairPlaceKind.Bedroom;
                return bedCell;
            }

            // --- 2. Current room (unless it is a prison cell) ---
            Room current = pawn.GetRoom();
            if (current != null && !current.IsPrisonCell && IsLockableRoom(current, map)
                && TryFindCellInRoom(pawn, current, out IntVec3 curCell))
            {
                kind = DespairPlaceKind.CurrentRoom;
                return curCell;
            }

            // --- 3. Nearest quiet room (not a prison cell, not someone else's bedroom) ---
            Room best = null;
            float bestDist = MaxOtherRoomSearchDistance * MaxOtherRoomSearchDistance;
            IReadOnlyList<Room> rooms = map.regionGrid.AllRooms;
            for (int i = 0; i < rooms.Count; i++)
            {
                Room room = rooms[i];
                if (room == null || room == current || room.IsPrisonCell)
                    continue;
                if (room.Owners.Any(o => o != pawn))
                    continue;
                if (!IsLockableRoom(room, map))
                    continue;

                float d = room.ExtentsClose.CenterCell.DistanceToSquared(pawn.Position);
                if (d < bestDist && pawn.CanReach(room.ExtentsClose.CenterCell, PathEndMode.Touch, Danger.Deadly))
                {
                    bestDist = d;
                    best = room;
                }
            }
            if (best != null && TryFindCellInRoom(pawn, best, out IntVec3 otherCell))
            {
                kind = DespairPlaceKind.OtherRoom;
                return otherCell;
            }

            // --- 4. Nowhere to go ---
            return pawn.Position;
        }

        // ===================================================================
        // Thoughts
        // ===================================================================

        /// <summary>Safely gives a memory, respecting traits, genes, ideology, etc.</summary>
        public static void TryGiveMemory(Pawn pawn, ThoughtDef def, Pawn otherPawn = null)
        {
            if (pawn == null || def == null || pawn.Dead)
                return;
            var memories = pawn.needs?.mood?.thoughts?.memories;
            if (memories == null)
                return;
            // checkIfNullified: honors nullifyingTraits / nullifyingPrecepts / nullifyingHediffs / genes
            if (!ThoughtUtility.CanGetThought(pawn, def, checkIfNullified: true))
                return;
            memories.TryGainMemory(def, otherPawn);
        }

        /// <summary>
        /// Colony penalty for a despair death: -20 for 6 days to every free colonist on all maps,
        /// in caravans and in transit. Psychopaths, bloodlust, inhumanized (Anomaly) and anything
        /// nullifying the thought through ideology or genes are filtered in TryGiveMemory.
        /// </summary>
        public static void GiveColonyDeathThoughts(Pawn victim)
        {
            List<Pawn> colonists = PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists.ToList();
            for (int i = 0; i < colonists.Count; i++)
            {
                Pawn p = colonists[i];
                if (p == victim)
                    continue;
                TryGiveMemory(p, DespaRIMDefOf.DespaRIM_ColonistLostToDespair, victim);
            }
        }

        /// <summary>Translation key describing the place (for letters and the inspector).</summary>
        public static string PlaceKey(DespairPlaceKind kind)
        {
            switch (kind)
            {
                case DespairPlaceKind.Bedroom: return "DespaRIM_Place_Bedroom";
                case DespairPlaceKind.CurrentRoom: return "DespaRIM_Place_CurrentRoom";
                case DespairPlaceKind.OtherRoom: return "DespaRIM_Place_OtherRoom";
                case DespairPlaceKind.PrisonCell: return "DespaRIM_Place_PrisonCell";
                default: return "DespaRIM_Place_Here";
            }
        }
    }
}
