using RimWorld;
using UnityEngine;
using Verse;

namespace DespaRIM
{
    /// <summary>
    /// Stores and draws the mod settings.
    /// Values are persisted to Config/Mod_&lt;id&gt;_DespaRIMMod.xml via ExposeData().
    /// Every UI string comes from Languages/*/Keyed through .Translate().
    /// </summary>
    public class DespaRIMSettings : ModSettings
    {
        // ---------- Defaults ----------
        public const float DefaultMoodThreshold = 0.20f;   // 20%
        public const int DefaultDaysRequired = 10;          // 10 in-game days
        public const float DefaultMtbDays = 60f;            // MTB of 60 days
        public const float DefaultRescueHours = 2f;         // 2 hours = 5000 ticks
        public const bool DefaultAllowPrisoners = true;

        // ---------- Slider bounds ----------
        public const float MinMoodThreshold = 0.05f, MaxMoodThreshold = 0.40f;
        public const int MinDays = 1, MaxDays = 30;
        public const float MinMtb = 10f, MaxMtb = 150f;
        public const float MinRescueHours = 0.5f, MaxRescueHours = 6f;

        /// <summary>Ticks per in-game hour (60000 ticks per day / 24).</summary>
        public const int TicksPerHour = GenDate.TicksPerHour; // 2500

        // ---------- Current values ----------
        /// <summary>Mood threshold (0..1). Stress accumulates while mood stays below it.</summary>
        public float moodThreshold = DefaultMoodThreshold;

        /// <summary>How many consecutive days mood must stay below the threshold before the break becomes possible.</summary>
        public int daysRequired = DefaultDaysRequired;

        /// <summary>Mean Time Between: average number of days between breaks once the threshold has been passed.</summary>
        public float mtbDays = DefaultMtbDays;

        /// <summary>How many in-game hours the player has to rescue the pawn.</summary>
        public float rescueHours = DefaultRescueHours;

        /// <summary>Whether prisoners of the colony can suffer the break.</summary>
        public bool allowPrisoners = DefaultAllowPrisoners;

        // ---------- Derived values ----------
        public int RequiredLowMoodTicks => daysRequired * GenDate.TicksPerDay;
        public int RescueTicks => Mathf.Max(60, Mathf.RoundToInt(rescueHours * TicksPerHour));

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref moodThreshold, "moodThreshold", DefaultMoodThreshold);
            Scribe_Values.Look(ref daysRequired, "daysRequired", DefaultDaysRequired);
            Scribe_Values.Look(ref mtbDays, "mtbDays", DefaultMtbDays);
            Scribe_Values.Look(ref rescueHours, "rescueHours", DefaultRescueHours);
            Scribe_Values.Look(ref allowPrisoners, "allowPrisoners", DefaultAllowPrisoners);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                ClampAll(); // guard against hand-edited config files
            }
        }

        public void ResetToDefaults()
        {
            moodThreshold = DefaultMoodThreshold;
            daysRequired = DefaultDaysRequired;
            mtbDays = DefaultMtbDays;
            rescueHours = DefaultRescueHours;
            allowPrisoners = DefaultAllowPrisoners;
        }

        private void ClampAll()
        {
            moodThreshold = Mathf.Clamp(moodThreshold, MinMoodThreshold, MaxMoodThreshold);
            daysRequired = Mathf.Clamp(daysRequired, MinDays, MaxDays);
            mtbDays = Mathf.Clamp(mtbDays, MinMtb, MaxMtb);
            rescueHours = Mathf.Clamp(rescueHours, MinRescueHours, MaxRescueHours);
        }

        /// <summary>
        /// Draws the "Options → Mod settings → DespaRIM" window.
        /// </summary>
        public void DoWindowContents(Rect inRect)
        {
            var list = new Listing_Standard();
            list.Begin(inRect);

            Text.Font = GameFont.Medium;
            list.Label("DespaRIM_Settings_Title".Translate());
            Text.Font = GameFont.Small;
            list.Label("DespaRIM_Settings_Subtitle".Translate().Colorize(ColoredText.SubtleGrayColor));
            list.GapLine();

            // 1. Mood threshold: 5%..40%, 1% steps
            float threshold = list.SliderLabeled(
                "DespaRIM_Settings_MoodThreshold".Translate(moodThreshold.ToStringPercent()),
                moodThreshold, MinMoodThreshold, MaxMoodThreshold, 0.6f,
                "DespaRIM_Settings_MoodThreshold_Tip".Translate());
            moodThreshold = Mathf.Round(threshold * 100f) / 100f;
            list.Gap(6f);

            // 2. Days before the break can happen: 1..30
            float days = list.SliderLabeled(
                "DespaRIM_Settings_DaysRequired".Translate(daysRequired),
                daysRequired, MinDays, MaxDays, 0.6f,
                "DespaRIM_Settings_DaysRequired_Tip".Translate());
            daysRequired = Mathf.Clamp(Mathf.RoundToInt(days), MinDays, MaxDays);
            list.Gap(6f);

            // 3. MTB: 10..150 days
            float mtb = list.SliderLabeled(
                "DespaRIM_Settings_Mtb".Translate(mtbDays.ToString("0")),
                mtbDays, MinMtb, MaxMtb, 0.6f,
                "DespaRIM_Settings_Mtb_Tip".Translate());
            mtbDays = Mathf.Round(mtb);
            // Hint: chance per day = 1 - e^(-1/MTB)
            float dailyChance = 1f - Mathf.Exp(-1f / Mathf.Max(1f, mtbDays));
            list.Label("DespaRIM_Settings_Mtb_Daily".Translate(dailyChance.ToStringPercent("0.0"))
                .Colorize(ColoredText.SubtleGrayColor));
            list.Gap(6f);

            // 4. Rescue time: 0.5..6 hours, 0.25 h steps
            float hours = list.SliderLabeled(
                "DespaRIM_Settings_RescueHours".Translate(rescueHours.ToString("0.##"), RescueTicks),
                rescueHours, MinRescueHours, MaxRescueHours, 0.6f,
                "DespaRIM_Settings_RescueHours_Tip".Translate());
            rescueHours = Mathf.Round(hours * 4f) / 4f;
            list.Gap(6f);

            // 5. Prisoners
            list.CheckboxLabeled("DespaRIM_Settings_AllowPrisoners".Translate(), ref allowPrisoners,
                "DespaRIM_Settings_AllowPrisoners_Tip".Translate());

            list.GapLine();

            // Summary
            list.Label("DespaRIM_Settings_Summary".Translate(
                moodThreshold.ToStringPercent(), daysRequired, mtbDays.ToString("0"), rescueHours.ToString("0.##")));

            list.Gap(12f);
            if (list.ButtonText("DespaRIM_Settings_Reset".Translate(), null, 0.4f))
            {
                ResetToDefaults();
            }

            list.End();
        }
    }
}
