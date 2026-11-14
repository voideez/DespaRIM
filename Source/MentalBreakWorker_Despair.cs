using RimWorld;
using Verse;
using Verse.AI;

namespace DespaRIM
{
    /// <summary>
    /// Trigger logic for the Despair break.
    ///
    /// Important: RimWorld's regular mental break system NEVER picks this break (CommonalityFor = 0).
    /// It is only fired from GameComponent_DespairTracker once all conditions are met:
    ///   1) mood has stayed below the threshold (setting, 20%) for at least N consecutive days (setting, 10);
    ///   2) after that, an MTB chance is rolled every check interval (setting, 60 days);
    ///   3) the pawn has no immunity ("Total despair" from a previous break).
    /// </summary>
    public class MentalBreakWorker_Despair : MentalBreakWorker
    {
        /// <summary>The vanilla MentalBreaker must never pick this break at random.</summary>
        public override float CommonalityFor(Pawn pawn, bool moodCaused = false)
        {
            return 0f;
        }

        public override bool BreakCanOccur(Pawn pawn)
        {
            if (!DespairUtility.IsCandidate(pawn))
                return false;
            if (DespairUtility.HasImmunity(pawn))
                return false;
            if (pawn.InMentalState || pawn.Downed || pawn.InAggroMentalState)
                return false;
            if (pawn.mindState.mentalStateHandler.MentalBreaksBlocked())
                return false;
            return base.BreakCanOccur(pawn);
        }

        public override bool TryStart(Pawn pawn, string reason, bool causedByMood)
        {
            if (!BreakCanOccur(pawn))
                return false;
            return base.TryStart(pawn, reason, causedByMood);
        }

        /// <summary>
        /// Rolls the MTB chance. Called by the tracker every <paramref name="checkInterval"/> ticks,
        /// but only once the required number of days has already passed.
        /// </summary>
        /// <param name="pawn">the pawn</param>
        /// <param name="checkInterval">ticks between checks</param>
        /// <param name="lowMoodTicks">consecutive ticks of mood below the threshold (for the letter text)</param>
        /// <returns>true if the break started</returns>
        public static bool TryRollBreak(Pawn pawn, int checkInterval, int lowMoodTicks)
        {
            DespaRIMSettings s = DespaRIMMod.Settings;

            // Rand.MTBEventOccurs(mtb, time unit, interval):
            // chance per interval = 1 - exp(-interval / (mtb * unit))
            if (!Rand.MTBEventOccurs(s.mtbDays, GenDate.TicksPerDay, checkInterval))
                return false;

            MentalBreakWorker worker = DespaRIMDefOf.DespaRIM_DespairBreak.Worker;
            if (!worker.BreakCanOccur(pawn))
                return false;

            string reason = "DespaRIM_BreakReason".Translate(
                pawn.Named("PAWN"),
                (lowMoodTicks / (float)GenDate.TicksPerDay).ToString("0.#"),
                s.moodThreshold.ToStringPercent()).Resolve();

            return worker.TryStart(pawn, reason, causedByMood: true);
        }
    }
}
