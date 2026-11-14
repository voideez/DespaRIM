using System.Collections.Generic;
using LudeonTK;
using RimWorld;
using Verse;

namespace DespaRIM
{
    /// <summary>
    /// Testing tools.
    ///  • Debug actions menu (Dev Mode → "Debug actions" icon → "DespaRIM" section).
    ///    These are ToolMapForPawns tools: pick the action, then click a pawn.
    ///  • Buttons in the pawn inspector (Patch_Pawn_GetGizmos), visible in Dev Mode only.
    /// </summary>
    public static class DebugActions_Despair
    {
        private const string Category = "DespaRIM";

        // ===================================================================
        // Debug Actions menu
        // ===================================================================

        [DebugAction(Category, "Trigger Despair break", actionType = DebugActionType.ToolMapForPawns, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void TriggerDespairTool(Pawn p)
        {
            ForceDespair(p);
        }

        [DebugAction(Category, "Fill low-mood counter (MTB armed)", actionType = DebugActionType.ToolMapForPawns, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void FillCounterTool(Pawn p)
        {
            FillCounter(p);
        }

        [DebugAction(Category, "Despair: 10 sec left", actionType = DebugActionType.ToolMapForPawns, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void AlmostOverTool(Pawn p)
        {
            SetAlmostOver(p);
        }

        [DebugAction(Category, "Despair: rescue now", actionType = DebugActionType.ToolMapForPawns, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void RescueTool(Pawn p)
        {
            if (p?.MentalState is MentalState_Despair state)
                state.Rescue("DespaRIM_Debug_RescuedByDev".Translate());
        }

        [DebugAction(Category, "Clear despair immunity", actionType = DebugActionType.ToolMapForPawns, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ClearImmunityTool(Pawn p)
        {
            ClearImmunity(p);
        }

        [DebugAction(Category, "Log despair status", actionType = DebugActionType.ToolMapForPawns, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void LogStatusTool(Pawn p)
        {
            ShowStatus(p);
        }

        // ===================================================================
        // Inspector buttons
        // ===================================================================

        public static IEnumerable<Gizmo> GetDevGizmos(Pawn pawn)
        {
            bool inDespair = pawn.MentalState is MentalState_Despair;

            if (!inDespair)
            {
                yield return new Command_Action
                {
                    defaultLabel = "DespaRIM_Dev_Trigger".Translate(),
                    defaultDesc = "DespaRIM_Dev_Trigger_Desc".Translate(),
                    icon = TexCommand.DesirePower,
                    action = () => ForceDespair(pawn)
                };
            }
            else
            {
                yield return new Command_Action
                {
                    defaultLabel = "DespaRIM_Dev_AlmostOver".Translate(),
                    defaultDesc = "DespaRIM_Dev_AlmostOver_Desc".Translate(),
                    icon = TexCommand.DesirePower,
                    action = () => SetAlmostOver(pawn)
                };
            }

            yield return new Command_Action
            {
                defaultLabel = "DespaRIM_Dev_Status".Translate(),
                defaultDesc = "DespaRIM_Dev_Status_Desc".Translate(),
                icon = TexCommand.DesirePower,
                action = () => ShowStatus(pawn)
            };
        }

        // ===================================================================
        // Implementation
        // ===================================================================

        /// <summary>Starts the break instantly, bypassing the wait, the mood check and immunity.</summary>
        public static void ForceDespair(Pawn pawn)
        {
            if (pawn == null)
                return;

            if (pawn.MentalState is MentalState_Despair)
            {
                Messages.Message("DespaRIM_Debug_AlreadyInDespair".Translate(pawn.Named("PAWN")), pawn, MessageTypeDefOf.RejectInput, false);
                return;
            }

            if (!DespairUtility.IsCandidate(pawn))
            {
                Messages.Message("DespaRIM_Debug_NotCandidate".Translate(pawn.Named("PAWN")), pawn, MessageTypeDefOf.RejectInput, false);
                return;
            }

            bool started = pawn.mindState.mentalStateHandler.TryStartMentalState(
                DespaRIMDefOf.DespaRIM_Despair,
                "DespaRIM_Debug_Reason".Translate(),
                forced: true,
                forceWake: true,
                causedByMood: true);

            if (!started)
                Messages.Message("DespaRIM_Debug_Failed".Translate(pawn.Named("PAWN")), pawn, MessageTypeDefOf.RejectInput, false);
        }

        /// <summary>Sets the low-mood counter to the threshold: MTB rolls begin on the next check.</summary>
        public static void FillCounter(Pawn pawn)
        {
            GameComponent_DespairTracker tracker = GameComponent_DespairTracker.Instance;
            if (pawn == null || tracker == null)
                return;
            tracker.SetLowMoodTicks(pawn, DespaRIMMod.Settings.RequiredLowMoodTicks);
            Messages.Message("DespaRIM_Debug_CounterFilled".Translate(pawn.Named("PAWN")), pawn, MessageTypeDefOf.NeutralEvent, false);
        }

        public static void SetAlmostOver(Pawn pawn)
        {
            if (pawn?.MentalState is MentalState_Despair state)
            {
                state.DebugSetTicksLeft(GenTicks.SecondsToTicks(10f));
                Messages.Message("DespaRIM_Debug_AlmostOver".Translate(pawn.Named("PAWN")), pawn, MessageTypeDefOf.NeutralEvent, false);
            }
        }

        public static void ClearImmunity(Pawn pawn)
        {
            var memories = pawn?.needs?.mood?.thoughts?.memories;
            if (memories == null)
                return;
            memories.RemoveMemoriesOfDef(DespaRIMDefOf.DespaRIM_TotalDespair);
            Messages.Message("DespaRIM_Debug_ImmunityCleared".Translate(pawn.Named("PAWN")), pawn, MessageTypeDefOf.NeutralEvent, false);
        }

        public static void ShowStatus(Pawn pawn)
        {
            if (pawn == null)
                return;

            DespaRIMSettings s = DespaRIMMod.Settings;
            int ticks = GameComponent_DespairTracker.Instance?.GetLowMoodTicks(pawn) ?? 0;
            float mood = pawn.needs?.mood?.CurLevelPercentage ?? -1f;

            string stateInfo = "-";
            if (pawn.MentalState is MentalState_Despair st)
            {
                stateInfo = $"place={st.placeKind}, cell={st.despairCell}, lockedIn={st.lockedIn}, " +
                            $"left={st.ticksLeft}/{st.ticksTotal}, doors={st.LockedDoors.Count}";
            }

            string text = "DespaRIM_Debug_Status".Translate(
                pawn.Named("PAWN"),
                mood >= 0f ? mood.ToStringPercent() : "-",
                s.moodThreshold.ToStringPercent(),
                (ticks / (float)GenDate.TicksPerDay).ToString("0.00"),
                s.daysRequired,
                DespairUtility.HasImmunity(pawn).ToStringYesNo(),
                DespairUtility.IsCandidate(pawn).ToStringYesNo(),
                stateInfo).Resolve();

            Log.Message("[DespaRIM] " + text);
            Messages.Message(text, pawn, MessageTypeDefOf.NeutralEvent, false);
        }
    }
}
