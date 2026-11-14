using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace DespaRIM
{
    /// <summary>
    /// The "Despair" mental state.
    ///
    /// Lifecycle:
    ///  1. PostStart: picks a place (bedroom / room / prison cell / "right here"), gives the
    ///     "Total despair" debuff (which doubles as immunity), adds the visible "Despair" hediff
    ///     and sends the letter.
    ///  2. JobGiver_DespairAct hands out JobDriver_DespairAct: walk to the place, lock the doors, wait.
    ///  3. The timer (setting, 2 h) only runs while the pawn is locked in and waiting. It lives here,
    ///     so interrupting the job does not reset it.
    ///  4. Rescue: stun, downed, arrest, or a colonist standing right next to the pawn
    ///     (after the door has been broken) → RecoverFromState → PostEnd → "Shock and guilt" + "Second chance".
    ///  5. Timer runs out → death, letter, -20 mood for the colony.
    /// </summary>
    public class MentalState_Despair : MentalState
    {
        // ---------- Saved state ----------
        public IntVec3 despairCell = IntVec3.Invalid;
        public DespairPlaceKind placeKind = DespairPlaceKind.Here;
        public int ticksLeft = -1;
        public int ticksTotal = -1;
        public bool lockedIn;
        public bool wasPrisonerAtStart;
        private bool diedOfDespair;
        private bool rescueHandled;
        private List<Building_Door> lockedDoors = new List<Building_Door>();

        /// <summary>How long the pawn may walk to the place before locking in wherever they stand.</summary>
        private const int MaxTicksToReachPlace = 2 * GenDate.TicksPerHour;

        /// <summary>Radius within which a colonist "reaches" the despairing pawn and talks them down.</summary>
        private const float RescueRadius = 1.9f;

        private static readonly AccessTools.FieldRef<Building_Door, bool> holdOpenRef =
            AccessTools.FieldRefAccess<Building_Door, bool>("holdOpenInt");

        // ---------- Properties ----------
        public float Progress => ticksTotal <= 0 ? 0f : Mathf.Clamp01(1f - ticksLeft / (float)ticksTotal);
        public IReadOnlyList<Building_Door> LockedDoors => lockedDoors;

        /// <summary>Cannot end "naturally": only rescue or death end it.</summary>
        protected override bool CanEndBeforeMaxDurationNow => false;

        /// <summary>Does not go to sleep in the middle of despair.</summary>
        public override bool AllowRestingInBed => false;

        public override string InspectLine
        {
            get
            {
                if (lockedIn && ticksLeft >= 0)
                    return "DespaRIM_InspectLocked".Translate(ticksLeft.ToStringTicksToPeriod(allowSeconds: false, shortForm: true));
                return "DespaRIM_InspectGoing".Translate(DespairUtility.PlaceKey(placeKind).Translate());
            }
        }

        // ===================================================================
        // Start
        // ===================================================================

        public override void PostStart(string reason)
        {
            base.PostStart(reason);

            DespaRIMSettings s = DespaRIMMod.Settings;
            ticksTotal = s.RescueTicks;
            ticksLeft = ticksTotal;
            wasPrisonerAtStart = pawn.IsPrisonerOfColony;
            lockedIn = false;
            lockedDoors.Clear();

            if (pawn.Spawned)
                despairCell = DespairUtility.FindDespairCell(pawn, out placeKind);
            else
                despairCell = IntVec3.Invalid;

            // Immunity plus the -50 / 3 days debuff
            DespairUtility.TryGiveMemory(pawn, DespaRIMDefOf.DespaRIM_TotalDespair);

            // Visible hediff: shown in the health tab and used as the cause in the death letter
            if (pawn.health.hediffSet.GetFirstHediffOfDef(DespaRIMDefOf.DespaRIM_DespairHediff) == null)
                pawn.health.AddHediff(DespaRIMDefOf.DespaRIM_DespairHediff);

            GameComponent_DespairTracker.Instance?.ResetPawn(pawn);

            SendBeginLetter(reason);
        }

        /// <summary>
        /// The vanilla letter is disabled: we send our own, including the place and the rescue time.
        /// </summary>
        public override TaggedString GetBeginLetterText()
        {
            return null;
        }

        private void SendBeginLetter(string reason)
        {
            if (!PawnUtility.ShouldSendNotificationAbout(pawn))
                return;

            string hours = (ticksTotal / (float)GenDate.TicksPerHour).ToString("0.##");
            TaggedString text = (wasPrisonerAtStart ? "DespaRIM_LetterText_Prisoner" : "DespaRIM_LetterText")
                .Translate(pawn.Named("PAWN"), DespairUtility.PlaceKey(placeKind).Translate(), hours)
                .AdjustedFor(pawn)
                .CapitalizeFirst();

            if (!reason.NullOrEmpty())
                text += "\n\n" + reason;

            text += "\n\n" + "DespaRIM_LetterHowToSave".Translate(pawn.Named("PAWN")).AdjustedFor(pawn);

            Find.LetterStack.ReceiveLetter(
                "DespaRIM_LetterLabel".Translate(pawn.LabelShortCap),
                text,
                LetterDefOf.ThreatBig,
                pawn);
        }

        // ===================================================================
        // Tick
        // ===================================================================

        public override void MentalStateTick(int delta)
        {
            if (pawn.Dead || !pawn.Spawned)
            {
                base.MentalStateTick(delta);
                return;
            }

            if (pawn.IsHashIntervalTick(30, delta))
            {
                // 1) Time is up: death
                if (lockedIn && ticksLeft <= 0)
                {
                    DieOfDespair();
                    return;
                }

                // 2) Stunned (blow, EMP, psycast): "jolted" out of despair
                if (pawn.stances?.stunner != null && pawn.stances.stunner.Stunned)
                {
                    Rescue("DespaRIM_RescueBy_Stun".Translate(pawn.Named("PAWN")));
                    return;
                }

                // 3) Arrested (became a colony prisoner without being one before)
                if (!wasPrisonerAtStart && pawn.IsPrisonerOfColony)
                {
                    Rescue("DespaRIM_RescueBy_Arrest".Translate(pawn.Named("PAWN")));
                    return;
                }

                // 4) Someone got through: the door was broken, or the pawn never managed to lock it
                if (lockedIn)
                {
                    Pawn rescuer = FindRescuerNearby();
                    if (rescuer != null)
                    {
                        Rescue("DespaRIM_RescueBy_Talk".Translate(pawn.Named("PAWN"), rescuer.Named("RESCUER")));
                        return;
                    }

                    CheckDoorsIntegrity();
                }
                else if (age > MaxTicksToReachPlace && despairCell != pawn.Position)
                {
                    // Walking for too long (stuck): lock in right where they stand
                    despairCell = pawn.Position;
                    placeKind = DespairPlaceKind.Here;
                    pawn.jobs?.EndCurrentJob(JobCondition.InterruptForced);
                }
            }

            base.MentalStateTick(delta);
        }

        /// <summary>
        /// Advances the timer. Called by JobDriver_DespairAct only while the pawn is locked in and waiting.
        /// </summary>
        public void AdvanceTimer(int delta)
        {
            if (!lockedIn || ticksLeft <= 0)
                return;
            ticksLeft = Mathf.Max(0, ticksLeft - delta);
        }

        // ===================================================================
        // Doors
        // ===================================================================

        /// <summary>
        /// The pawn has reached the place and locks every door of the room from the inside.
        /// If there is no room (outdoors), the timer simply starts.
        /// </summary>
        public void LockIn()
        {
            if (lockedIn)
                return;
            lockedIn = true;
            lockedDoors.Clear();

            Map map = pawn.Map;
            Room room = pawn.GetRoom();
            if (map != null && room != null && DespairUtility.IsLockableRoom(room, map))
            {
                foreach (Building_Door door in DespairUtility.GetRoomDoors(room, map))
                {
                    if (door == null || !door.Spawned)
                        continue;

                    // Turn off "hold open", otherwise the door would let everyone through
                    try { holdOpenRef(door) = false; }
                    catch { /* exotic doors may lack the field; not critical */ }

                    lockedDoors.Add(door);
                    DespairDoorLocks.Lock(door, pawn);
                }
            }

            if (PawnUtility.ShouldSendNotificationAbout(pawn))
            {
                string key = lockedDoors.Count > 0 ? "DespaRIM_MessageLockedIn" : "DespaRIM_MessageNoDoor";
                Messages.Message(key.Translate(pawn.Named("PAWN")).AdjustedFor(pawn).CapitalizeFirst(),
                    pawn, MessageTypeDefOf.ThreatBig);
            }
        }

        /// <summary>Re-registers the locks after loading a save.</summary>
        public void ReRegisterLocks()
        {
            if (lockedDoors == null)
                return;
            lockedDoors.RemoveAll(d => d == null || d.Destroyed || !d.Spawned);
            for (int i = 0; i < lockedDoors.Count; i++)
                DespairDoorLocks.Lock(lockedDoors[i], pawn);
        }

        /// <summary>Notifies the player when one of the locked doors has been broken.</summary>
        private void CheckDoorsIntegrity()
        {
            if (lockedDoors.Count == 0)
                return;

            int before = lockedDoors.Count;
            lockedDoors.RemoveAll(d => d == null || d.Destroyed || !d.Spawned);
            if (lockedDoors.Count < before)
            {
                DespairDoorLocks.PruneInvalid();
                if (PawnUtility.ShouldSendNotificationAbout(pawn))
                {
                    Messages.Message("DespaRIM_MessageDoorBroken".Translate(pawn.Named("PAWN")).AdjustedFor(pawn).CapitalizeFirst(),
                        pawn, MessageTypeDefOf.NeutralEvent);
                }
            }
        }

        /// <summary>
        /// Looks for a colonist standing right next to the pawn, in the same room.
        /// While the door is locked nobody can get in, so a rescue is only possible after breaking it.
        /// </summary>
        private Pawn FindRescuerNearby()
        {
            Map map = pawn.Map;
            Room myRoom = pawn.GetRoom();
            int cells = GenRadial.NumCellsInRadius(RescueRadius);

            for (int i = 0; i < cells; i++)
            {
                IntVec3 c = pawn.Position + GenRadial.RadialPattern[i];
                if (!c.InBounds(map))
                    continue;

                List<Thing> things = c.GetThingList(map);
                for (int j = 0; j < things.Count; j++)
                {
                    if (things[j] is Pawn other && IsValidRescuer(other) && other.GetRoom() == myRoom)
                        return other;
                }
            }
            return null;
        }

        private bool IsValidRescuer(Pawn other)
        {
            if (other == null || other == pawn || other.Dead || other.Downed || !other.Spawned)
                return false;
            if (!other.RaceProps.Humanlike || other.Faction == null || !other.Faction.IsPlayer)
                return false;
            if (other.IsPrisoner || other.InMentalState || !other.Awake())
                return false;
            if (!other.health.capacities.CapableOf(PawnCapacityDefOf.Talking)
                && !other.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation))
                return false;
            if (other.HostileTo(pawn))
                return false;
            return true;
        }

        // ===================================================================
        // Ending
        // ===================================================================

        /// <summary>Rescue: early exit from the state.</summary>
        public void Rescue(string howText)
        {
            if (pawn.MentalState != this)
                return;

            rescueHandled = true;
            GiveRescueOutcome(howText);
            RecoverFromState(); // → PostEnd
        }

        /// <summary>
        /// Rescue outcome: "Shock and guilt" (-15, 7 d), "Second chance" (+15, 10 d), hediff removed, message.
        /// "Total despair" stays until it expires: that is the immunity against a repeat.
        /// </summary>
        private void GiveRescueOutcome(string howText)
        {
            if (pawn.Dead)
                return;

            DespairUtility.TryGiveMemory(pawn, DespaRIMDefOf.DespaRIM_ShockAndGuilt);
            DespairUtility.TryGiveMemory(pawn, DespaRIMDefOf.DespaRIM_SecondChance);

            RemoveDespairHediff();

            if (PawnUtility.ShouldSendNotificationAbout(pawn))
            {
                TaggedString msg = "DespaRIM_MessageRescued".Translate(pawn.Named("PAWN")).AdjustedFor(pawn).CapitalizeFirst();
                if (!howText.NullOrEmpty())
                    msg = howText.CapitalizeFirst() + " " + msg;
                Messages.Message(msg, pawn, MessageTypeDefOf.PositiveEvent);
            }
        }

        /// <summary>Death once the timer runs out.</summary>
        private void DieOfDespair()
        {
            if (diedOfDespair || pawn.Dead)
                return;
            diedOfDespair = true;

            DespairDoorLocks.UnlockAllFor(pawn);
            lockedDoors.Clear();

            // The hediff becomes the "cause" in the vanilla death letter: "... died because of: despair"
            Hediff culprit = pawn.health.hediffSet.GetFirstHediffOfDef(DespaRIMDefOf.DespaRIM_DespairHediff)
                             ?? pawn.health.AddHediff(DespaRIMDefOf.DespaRIM_DespairHediff);

            bool notify = PawnUtility.ShouldSendNotificationAbout(pawn);
            Pawn victim = pawn;
            Map map = pawn.Map;
            IntVec3 pos = pawn.Position;

            // The colony learns of the loss (traits and ideology are checked in ThoughtUtility.CanGetThought)
            DespairUtility.GiveColonyDeathThoughts(victim);

            victim.Kill(null, culprit);

            // The vanilla "Death" letter has already been sent (with "despair" as the cause).
            // Add a short epitaph as a message so letters are not duplicated.
            if (notify && map != null)
            {
                Messages.Message(
                    "DespaRIM_MessageDied".Translate(victim.Named("PAWN")).AdjustedFor(victim).CapitalizeFirst(),
                    new TargetInfo(pos, map), MessageTypeDefOf.PawnDeath);
            }
        }

        public override void PostEnd()
        {
            // Doors are unlocked on ANY ending: rescue, death, state reset by another mod or by debug tools
            DespairDoorLocks.UnlockAllFor(pawn);
            lockedDoors.Clear();

            // The state was cleared externally (downed, arrested, psycast, another mod): that counts as a rescue too
            if (!rescueHandled && !diedOfDespair && !pawn.Dead)
            {
                rescueHandled = true;
                string how = pawn.Downed
                    ? "DespaRIM_RescueBy_Downed".Translate(pawn.Named("PAWN")).Resolve()
                    : null;
                GiveRescueOutcome(how);
            }

            base.PostEnd();
        }

        private void RemoveDespairHediff()
        {
            Hediff h = pawn.health?.hediffSet?.GetFirstHediffOfDef(DespaRIMDefOf.DespaRIM_DespairHediff);
            if (h != null)
                pawn.health.RemoveHediff(h);
        }

        // ===================================================================
        // Debug
        // ===================================================================

        /// <summary>Dev: leave only a few seconds on the timer.</summary>
        public void DebugSetTicksLeft(int ticks)
        {
            ticksLeft = Mathf.Clamp(ticks, 0, ticksTotal > 0 ? ticksTotal : ticks);
        }

        // ===================================================================
        // Saving
        // ===================================================================

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref despairCell, "despairCell", IntVec3.Invalid);
            Scribe_Values.Look(ref placeKind, "placeKind", DespairPlaceKind.Here);
            Scribe_Values.Look(ref ticksLeft, "ticksLeft", -1);
            Scribe_Values.Look(ref ticksTotal, "ticksTotal", -1);
            Scribe_Values.Look(ref lockedIn, "lockedIn", false);
            Scribe_Values.Look(ref wasPrisonerAtStart, "wasPrisonerAtStart", false);
            Scribe_Values.Look(ref diedOfDespair, "diedOfDespair", false);
            Scribe_Values.Look(ref rescueHandled, "rescueHandled", false);
            Scribe_Collections.Look(ref lockedDoors, "lockedDoors", LookMode.Reference);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (lockedDoors == null)
                    lockedDoors = new List<Building_Door>();
                lockedDoors.RemoveAll(d => d == null);
                if (ticksTotal <= 0)
                {
                    ticksTotal = DespaRIMMod.Settings.RescueTicks;
                    ticksLeft = ticksTotal;
                }
            }
        }
    }

    /// <summary>
    /// Decides whether the state can start (both through vanilla paths and through debug tools).
    /// </summary>
    public class MentalStateWorker_Despair : MentalStateWorker
    {
        public override bool StateCanOccur(Pawn pawn)
        {
            if (!base.StateCanOccur(pawn))
                return false;
            return DespairUtility.IsCandidate(pawn);
        }
    }
}
