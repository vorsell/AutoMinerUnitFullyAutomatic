using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace AutoMinerUnitFullyAutomatic
{
    public sealed class FullyAutomaticSettings : ModSettings
    {
        public bool arrivalOnlyMessages = true;
        public int defaultFuelLimit = 300;
        public bool defaultAutomationEnabled = true;
        public TargetSelectionMode defaultTargetSelectionMode = TargetSelectionMode.FirstDiscovered;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref arrivalOnlyMessages, "arrivalOnlyMessages", true);
            Scribe_Values.Look(ref defaultFuelLimit, "defaultFuelLimit", 300);
            Scribe_Values.Look(ref defaultAutomationEnabled, "defaultAutomationEnabled", true);
            Scribe_Values.Look(
                ref defaultTargetSelectionMode,
                "defaultTargetSelectionMode",
                TargetSelectionMode.FirstDiscovered);
            defaultFuelLimit = Mathf.Clamp(defaultFuelLimit, 0, 400);
        }
    }

    public sealed class FullyAutomaticMod : Mod
    {
        internal static FullyAutomaticSettings Settings;

        public FullyAutomaticMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<FullyAutomaticSettings>();
        }

        public override string SettingsCategory()
        {
            return "Auto Miner Unit - Fully Automatic";
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);

            bool arrivalOnly = ArrivalOnlyMessages;
            listing.CheckboxLabeled(
                "AMUFA_ArrivalOnlyMessages".Translate(),
                ref arrivalOnly,
                "AMUFA_ArrivalOnlyMessagesDesc".Translate(),
                0f,
                1f);
            SetArrivalOnlyMessages(arrivalOnly);

            listing.GapLine();
            listing.Label("AMUFA_DefaultSettingsHeader".Translate());

            Rect fuelLabelRect = listing.GetRect(Text.LineHeight);
            Widgets.Label(
                fuelLabelRect,
                "AMUFA_DefaultFuelLimit".Translate(Settings.defaultFuelLimit));
            TooltipHandler.TipRegion(fuelLabelRect, "AMUFA_DefaultFuelLimitDesc".Translate());
            Settings.defaultFuelLimit = Mathf.RoundToInt(
                listing.Slider(Settings.defaultFuelLimit, 0f, 400f));

            listing.CheckboxLabeled(
                "AMUFA_DefaultAutomation".Translate(),
                ref Settings.defaultAutomationEnabled,
                "AMUFA_DefaultAutomationDesc".Translate(),
                0f,
                1f);

            Rect strategyLabelRect = listing.GetRect(Text.LineHeight);
            Widgets.Label(strategyLabelRect, "AMUFA_DefaultTargetStrategy".Translate());
            TooltipHandler.TipRegion(
                strategyLabelRect,
                "AMUFA_DefaultTargetStrategyDesc".Translate());
            Rect strategyButtonRect = listing.GetRect(30f);
            if (Widgets.ButtonText(
                    strategyButtonRect,
                    StrategyLabel(Settings.defaultTargetSelectionMode)))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>
                {
                    new FloatMenuOption(
                        StrategyLabel(TargetSelectionMode.FirstDiscovered),
                        delegate { Settings.defaultTargetSelectionMode = TargetSelectionMode.FirstDiscovered; }),
                    new FloatMenuOption(
                        StrategyLabel(TargetSelectionMode.Nearest),
                        delegate { Settings.defaultTargetSelectionMode = TargetSelectionMode.Nearest; }),
                    new FloatMenuOption(
                        StrategyLabel(TargetSelectionMode.Farthest),
                        delegate { Settings.defaultTargetSelectionMode = TargetSelectionMode.Farthest; })
                };
                Find.WindowStack.Add(new FloatMenu(options));
            }

            listing.Gap();
            listing.GapLine();
            Rect resetButtonRect = listing.GetRect(30f);
            if (Widgets.ButtonText(resetButtonRect, "AMUFA_ResetSettings".Translate()))
            {
                ResetAllSettings();
            }
            TooltipHandler.TipRegion(resetButtonRect, "AMUFA_ResetSettingsDesc".Translate());

            listing.End();
        }

        private static void ResetAllSettings()
        {
            if (Settings == null)
            {
                return;
            }

            Settings.arrivalOnlyMessages = true;
            Settings.defaultFuelLimit = 300;
            Settings.defaultAutomationEnabled = true;
            Settings.defaultTargetSelectionMode = TargetSelectionMode.FirstDiscovered;
            Settings.Write();
        }

        private static string StrategyLabel(TargetSelectionMode mode)
        {
            switch (mode)
            {
                case TargetSelectionMode.Nearest:
                    return "AMUFA_StrategyNearest".Translate();
                case TargetSelectionMode.Farthest:
                    return "AMUFA_StrategyFarthest".Translate();
                default:
                    return "AMUFA_StrategyOldest".Translate();
            }
        }

        internal static bool ArrivalOnlyMessages
        {
            get { return Settings == null || Settings.arrivalOnlyMessages; }
        }

        internal static int DefaultFuelLimit
        {
            get { return Settings == null ? 300 : Mathf.Clamp(Settings.defaultFuelLimit, 0, 400); }
        }

        internal static bool DefaultAutomationEnabled
        {
            get { return Settings == null || Settings.defaultAutomationEnabled; }
        }

        internal static TargetSelectionMode DefaultTargetSelectionMode
        {
            get
            {
                return Settings == null
                    ? TargetSelectionMode.FirstDiscovered
                    : Settings.defaultTargetSelectionMode;
            }
        }

        internal static void SetArrivalOnlyMessages(bool value)
        {
            if (Settings == null || Settings.arrivalOnlyMessages == value)
            {
                return;
            }

            Settings.arrivalOnlyMessages = value;
            Settings.Write();
        }
    }
}

