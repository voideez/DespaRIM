using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace DespaRIM
{
    /// <summary>
    /// Hands the job to a despairing pawn. Hooked into the Humanlike think tree through
    /// a ThinkTreeDef with insertTag "Humanlike_PostMentalState" (see Defs/ThinkTreeDefs).
    /// </summary>
    public class JobGiver_DespairAct : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            if (!(pawn.MentalState is MentalState_Despair state))
                return null;

            // The place became unreachable (door locked, wall built): stay where they are
            if (!state.despairCell.IsValid || !state.despairCell.InBounds(pawn.Map)
                || !pawn.CanReach(state.despairCell, PathEndMode.OnCell, Danger.Deadly))
            {
                state.despairCell = pawn.Position;
                if (!state.lockedIn)
                    state.placeKind = DespairPlaceKind.Here;
            }

            Job job = JobMaker.MakeJob(DespaRIMDefOf.DespaRIM_DespairAct, state.despairCell);
            job.locomotionUrgency = LocomotionUrgency.Walk; // walks slowly, head down
            return job;
        }
    }

    /// <summary>
    /// The "Despair" job: walk to the place → lock the doors from the inside → wait with a progress bar above the head.
    /// The timer lives in MentalState_Despair; when it runs out, the state itself kills the pawn.
    /// </summary>
    public class JobDriver_DespairAct : JobDriver
    {
        private const TargetIndex CellInd = TargetIndex.A;

        /// <summary>Progress bar offset: a positive value draws it above the head.</summary>
        private const float ProgressBarOffsetZ = 0.95f;

        private Effecter progressBar;

        private MentalState_Despair State => pawn.MentalState as MentalState_Despair;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            // The cell is not reserved: if it is occupied, the pawn stands next to it, which is fine
            return true;
        }

        public override string GetReport()
        {
            MentalState_Despair state = State;
            if (state != null && state.lockedIn && state.ticksLeft >= 0)
            {
                return "DespaRIM_JobReport_Locked".Translate(
                    state.ticksLeft.ToStringTicksToPeriod(allowSeconds: false, shortForm: true));
            }
            return base.GetReport();
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            // The job only makes sense while the break lasts
            this.FailOn(() => State == null);

            // 1. Walk to the place
            yield return Toils_Goto.GotoCell(CellInd, PathEndMode.OnCell);

            // 2. Lock the doors from the inside (no-op if already locked)
            Toil lockToil = ToilMaker.MakeToil("DespaRIM_LockIn");
            lockToil.initAction = () =>
            {
                pawn.pather?.StopDead();
                State?.LockIn();
            };
            lockToil.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return lockToil;

            // 3. Wait: timer plus a progress bar above the head
            Toil wait = ToilMaker.MakeToil("DespaRIM_Wait");
            wait.defaultCompleteMode = ToilCompleteMode.Never;
            wait.handlingFacing = true;
            wait.initAction = () =>
            {
                pawn.pather?.StopDead();
                // Turns away in a random direction
                pawn.Rotation = Rot4.Random;
            };
            wait.tickIntervalAction = delta =>
            {
                MentalState_Despair state = State;
                if (state == null)
                    return;

                state.AdvanceTimer(delta);
                TickProgressBar(state);

                // Occasional grief icon above the head, for atmosphere
                if (pawn.IsHashIntervalTick(900, delta) && pawn.Spawned)
                    FleckMaker.ThrowMetaIcon(pawn.Position, pawn.Map, FleckDefOf.IncapIcon);
            };
            wait.AddFinishAction(CleanupProgressBar);
            yield return wait;
        }

        /// <summary>
        /// Custom progress bar instead of Toil.WithProgressBar: the vanilla one is only shown for
        /// player pawns, but we need it above prisoners too. The bar fills up as time runs out.
        /// </summary>
        private void TickProgressBar(MentalState_Despair state)
        {
            if (!pawn.Spawned)
                return;

            if (progressBar == null)
            {
                progressBar = EffecterDefOf.ProgressBar.Spawn();
                return;
            }

            progressBar.EffectTick(pawn, TargetInfo.Invalid);

            if (progressBar.children != null && progressBar.children.Count > 0
                && progressBar.children[0] is SubEffecter_ProgressBar sub && sub.mote != null)
            {
                sub.mote.progress = Mathf.Clamp01(state.Progress);
                sub.mote.offsetZ = ProgressBarOffsetZ;
                sub.mote.alwaysShow = true;
            }
        }

        private void CleanupProgressBar()
        {
            if (progressBar != null)
            {
                progressBar.Cleanup();
                progressBar = null;
            }
        }
    }
}
