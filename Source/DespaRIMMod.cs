using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace DespaRIM
{
    /// <summary>
    /// Mod entry point. RimWorld instantiates it after all assemblies have been loaded.
    /// Loads the settings, applies the Harmony patches and registers the settings window.
    /// </summary>
    public class DespaRIMMod : Mod
    {
        public const string HarmonyId = "voide.desparim";

        /// <summary>Global access to the settings from anywhere in the code.</summary>
        public static DespaRIMSettings Settings { get; private set; }

        public DespaRIMMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<DespaRIMSettings>();

            var harmony = new Harmony(HarmonyId);
            harmony.PatchAll(Assembly.GetExecutingAssembly());

            Log.Message("[DespaRIM] Loaded. Harmony patches applied.");
        }

        public override string SettingsCategory()
        {
            return "DespaRIM_SettingsCategory".Translate();
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Settings.DoWindowContents(inRect);
        }
    }

    /// <summary>
    /// References to the mod's defs. The game fills these in automatically after the XML is loaded.
    /// </summary>
    [DefOf]
    public static class DespaRIMDefOf
    {
        public static MentalBreakDef DespaRIM_DespairBreak;
        public static MentalStateDef DespaRIM_Despair;

        public static JobDef DespaRIM_DespairAct;
        public static JobDef DespaRIM_BreakDespairDoor;

        public static ThoughtDef DespaRIM_TotalDespair;
        public static ThoughtDef DespaRIM_ShockAndGuilt;
        public static ThoughtDef DespaRIM_SecondChance;
        public static ThoughtDef DespaRIM_ColonistLostToDespair;

        public static HediffDef DespaRIM_DespairHediff;

        static DespaRIMDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(DespaRIMDefOf));
        }
    }
}
