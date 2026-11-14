using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace DespaRIM
{
    /// <summary>
    /// "Break down the door": a colonist bashes a door locked by a despairing pawn until it breaks.
    /// Damage scales with manipulation and melee skill; durability depends on the door's material:
    /// a wooden door gives way quickly, a plasteel one holds out for a long time.
    /// </summary>
    public class JobDriver_BreakDespairDoor : JobDriver
    {
        private const TargetIndex DoorInd = TargetIndex.A;

        /// <summary>Ticks between blows.</summary>
        private const int TicksBetweenBlows = 80;

        /// <summary>Base damage per blow before multipliers.</summary>
        private const float BaseBlowDamage = 14f;

        private int ticksToNextBlow = TicksBetweenBlows;

        private Building_Door Door => job.GetTarget(DoorInd).Thing as Building_Door;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            // No reservation: two or three colonists can bash the door together
            return true;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref ticksToNextBlow, "ticksToNextBlow", TicksBetweenBlows);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(DoorInd);
            // The lock is gone (pawn rescued or dead): no need to keep bashing
            this.FailOn(() => !DespairDoorLocks.IsLocked(Door));

            yield return Toils_Goto.GotoThing(DoorInd, PathEndMode.Touch);

            Toil bash = ToilMaker.MakeToil("DespaRIM_BashDoor");
            bash.defaultCompleteMode = ToilCompleteMode.Never;
            bash.handlingFacing = true;
            bash.activeSkill = () => SkillDefOf.Melee;
            bash.tickIntervalAction = delta =>
            {
                Building_Door door = Door;
                if (door == null || !door.Spawned)
                {
                    ReadyForNextToil();
                    return;
                }

                pawn.rotationTracker.FaceTarget(door);
                ticksToNextBlow -= delta;
                if (ticksToNextBlow > 0)
                    return;

                ticksToNextBlow = TicksBetweenBlows;
                Blow(door);
            };
            bash.WithProgressBar(DoorInd, () =>
            {
                Building_Door door = Door;
                if (door == null || door.MaxHitPoints <= 0)
                    return 1f;
                return 1f - door.HitPoints / (float)door.MaxHitPoints;
            });
            yield return bash;
        }

        private void Blow(Building_Door door)
        {
            float manipulation = pawn.health.capacities.GetLevel(PawnCapacityDefOf.Manipulation);
            int meleeSkill = pawn.skills?.GetSkill(SkillDefOf.Melee)?.Level ?? 0;
            float damage = BaseBlowDamage * Mathf.Max(0.2f, manipulation) * (1f + meleeSkill * 0.05f);

            Map map = door.Map;
            IntVec3 pos = door.Position;

            var dinfo = new DamageInfo(DamageDefOf.Blunt, damage, 0f, -1f, pawn);
            door.TakeDamage(dinfo);

            SoundDefOf.Pawn_Melee_Punch_HitBuilding_Generic?.PlayOneShot(new TargetInfo(pos, map));
            if (map != null)
                FleckMaker.ThrowDustPuff(pos, map, 0.8f);

            pawn.skills?.Learn(SkillDefOf.Melee, 8f);
        }
    }

    /// <summary>
    /// Context menu option (right-click a locked door): "Break down the door (inside: X)".
    /// In RimWorld 1.6 float menu providers are discovered automatically through reflection,
    /// so no Harmony is needed here.
    /// </summary>
    public class FloatMenuOptionProvider_BreakDespairDoor : FloatMenuOptionProvider
    {
        protected override bool Drafted => true;
        protected override bool Undrafted => true;
        protected override bool Multiselect => false;
        protected override bool RequiresManipulation => true;

        protected override FloatMenuOption GetSingleOptionFor(Thing clickedThing, FloatMenuContext context)
        {
            if (!(clickedThing is Building_Door door))
                return null;

            Pawn pawn = context.FirstSelectedPawn;
            if (pawn == null || !DespairDoorLocks.IsLockedAgainst(door, pawn))
                return null;

            Pawn locker = DespairDoorLocks.GetLocker(door);
            string label = "DespaRIM_FloatMenu_BreakDoor".Translate(locker != null ? locker.LabelShort : "?");

            if (!pawn.CanReach(door, PathEndMode.Touch, Danger.Deadly))
                return new FloatMenuOption(label + ": " + "NoPath".Translate().CapitalizeFirst(), null);

            return FloatMenuUtility.DecoratePrioritizedTask(
                new FloatMenuOption(label, () =>
                {
                    Job job = JobMaker.MakeJob(DespaRIMDefOf.DespaRIM_BreakDespairDoor, door);
                    job.playerForced = true;
                    pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                }, MenuOptionPriority.High),
                pawn, door);
        }
    }
}
