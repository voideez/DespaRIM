using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DespaRIM
{
    /// <summary>
    /// Per-pawn counter of "how many consecutive ticks mood has been below the threshold".
    ///
    /// RimWorld creates this component for every game automatically (constructor taking Game)
    /// and saves it with the save file. Every 250 ticks (≈ 4 seconds at 1× speed):
    ///   • mood below the threshold → counter += 250;
    ///   • mood above the threshold or immunity active → counter = 0;
    ///   • counter ≥ N days → roll the MTB chance for a break (MentalBreakWorker_Despair.TryRollBreak).
    /// </summary>
    public class GameComponent_DespairTracker : GameComponent
    {
        public const int CheckInterval = 250;
        private const int PruneInterval = GenDate.TicksPerDay;

        public static GameComponent_DespairTracker Instance { get; private set; }

        // key: pawn's thingIDNumber, value: ticks of uninterrupted low mood
        private Dictionary<int, int> lowMoodTicks = new Dictionary<int, int>();

        // working lists for saving the dictionary
        private List<int> tmpKeys;
        private List<int> tmpValues;

        private readonly List<Pawn> tmpPawns = new List<Pawn>();

        public GameComponent_DespairTracker(Game game)
        {
            Instance = this;
            // New game or loaded save: locks from a previous session are no longer valid
            DespairDoorLocks.Clear();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref lowMoodTicks, "lowMoodTicks", LookMode.Value, LookMode.Value, ref tmpKeys, ref tmpValues);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && lowMoodTicks == null)
                lowMoodTicks = new Dictionary<int, int>();
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            Instance = this;
            // Restore door locks from active breaks (they are saved inside MentalState_Despair)
            DespairDoorLocks.RebuildFromGame();
        }

        public override void GameComponentTick()
        {
            int ticks = Find.TickManager.TicksGame;

            if (ticks % CheckInterval == 0)
            {
                DespairDoorLocks.PruneInvalid();
                TickAllMaps();
            }

            if (ticks % PruneInterval == 123)
                PruneDeadEntries();
        }

        // ===================================================================

        private void TickAllMaps()
        {
            List<Map> maps = Find.Maps;
            if (maps == null)
                return;

            for (int m = 0; m < maps.Count; m++)
            {
                // Copy the list: a break may change a pawn's state mid-loop
                tmpPawns.Clear();
                tmpPawns.AddRange(maps[m].mapPawns.AllHumanlikeSpawned);
                for (int i = 0; i < tmpPawns.Count; i++)
                    TrackPawn(tmpPawns[i]);
            }
            tmpPawns.Clear();
        }

        private void TrackPawn(Pawn pawn)
        {
            if (!DespairUtility.IsCandidate(pawn))
                return;

            int id = pawn.thingIDNumber;

            // Immunity after a survived break: the counter does not accumulate
            if (DespairUtility.HasImmunity(pawn) || pawn.MentalState is MentalState_Despair)
            {
                lowMoodTicks.Remove(id);
                return;
            }

            float mood = pawn.needs.mood.CurLevelPercentage;
            if (mood >= DespaRIMMod.Settings.moodThreshold)
            {
                // The pawn has lifted their head; nothing to accumulate
                lowMoodTicks.Remove(id);
                return;
            }

            lowMoodTicks.TryGetValue(id, out int current);
            current += CheckInterval;
            lowMoodTicks[id] = current;

            // Before the required time has passed the break is impossible
            if (current < DespaRIMMod.Settings.RequiredLowMoodTicks)
                return;

            // Threshold passed: roll the MTB chance every interval
            if (MentalBreakWorker_Despair.TryRollBreak(pawn, CheckInterval, current))
                lowMoodTicks.Remove(id);
        }

        /// <summary>Removes entries for pawns that are no longer alive (dead, gone, destroyed).</summary>
        private void PruneDeadEntries()
        {
            if (lowMoodTicks.Count == 0)
                return;

            var alive = new HashSet<int>();
            foreach (Pawn p in PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive)
                alive.Add(p.thingIDNumber);

            var remove = new List<int>();
            foreach (int id in lowMoodTicks.Keys)
            {
                if (!alive.Contains(id))
                    remove.Add(id);
            }
            for (int i = 0; i < remove.Count; i++)
                lowMoodTicks.Remove(remove[i]);
        }

        // ===================================================================
        // Public API (used by debug tools and the break)
        // ===================================================================

        public int GetLowMoodTicks(Pawn pawn)
        {
            if (pawn == null)
                return 0;
            return lowMoodTicks.TryGetValue(pawn.thingIDNumber, out int v) ? v : 0;
        }

        public void SetLowMoodTicks(Pawn pawn, int ticks)
        {
            if (pawn == null)
                return;
            if (ticks <= 0)
                lowMoodTicks.Remove(pawn.thingIDNumber);
            else
                lowMoodTicks[pawn.thingIDNumber] = ticks;
        }

        public void ResetPawn(Pawn pawn)
        {
            if (pawn != null)
                lowMoodTicks.Remove(pawn.thingIDNumber);
        }

        public override void AppendDebugString(System.Text.StringBuilder sb)
        {
            sb.AppendLine("DespaRIM tracked pawns: " + lowMoodTicks.Count + ", locked doors: " + DespairDoorLocks.Count);
        }
    }
}
