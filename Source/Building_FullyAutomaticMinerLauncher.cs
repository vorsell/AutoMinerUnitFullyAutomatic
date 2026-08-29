using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using TheAutoMiner;
using UnityEngine;
using Verse;

namespace AutoMinerUnitFullyAutomatic
{
    [StaticConstructorOnStartup]
    public sealed class Building_FullyAutomaticMinerLauncher : Building_AutoMinerLauncher
    {
        private const string PrototypePodDefName = "ProtoAutoMiner_Pod";
        private const string StandardPodDefName = "AutoMiner_Pod";
        private const string AsteroidPodDefName = "AstAutoMiner_Pod";

        private static readonly FieldInfo BaseAutoRebuildModeField =
            AccessTools.Field(typeof(Building_AutoMinerLauncher), "autoRebuildMode");
        private static readonly MethodInfo TryPlaceBlueprintMethod =
            AccessTools.Method(typeof(Building_AutoMinerLauncher), "TryPlaceAutoRebuildBlueprint");
        private static readonly MethodInfo CancelBlueprintMethod =
            AccessTools.Method(typeof(Building_AutoMinerLauncher), "CancelExistingBlueprint");
        private static readonly MethodInfo CreateAutoRebuildGizmoMethod =
            AccessTools.Method(typeof(Building_AutoMinerLauncher), "CreateAutoRebuildGizmo");
        private static readonly MethodInfo ChooseWorldTargetMethod =
            AccessTools.Method(typeof(CompAutoMinerLaunchable), "ChoseWorldTarget");
        private static readonly FieldInfo BringVacstoneField =
            AccessTools.Field(typeof(CompAutoMinerLaunchable), "bringVacstone");

        private static readonly FieldInfo OriginalCancelIconField =
            AccessTools.Field(typeof(Building_AutoMinerLauncher), "CancelIcon");
        private static readonly Texture2D StrategyFirstDiscoveredIcon =
            ContentFinder<Texture2D>.Get("UI/TargetStrategy_FirstDiscovered");
        private static readonly Texture2D StrategyNearestIcon =
            ContentFinder<Texture2D>.Get("UI/TargetStrategy_Nearest");
        private static readonly Texture2D StrategyFarthestIcon =
            ContentFinder<Texture2D>.Get("UI/TargetStrategy_Farthest");

        private static readonly string[] OriginalAutoRebuildLabelKeys =
        {
            "AutoMinerAutoRebuildOff",
            "AutoMinerAutoRebuildProto",
            "AutoMinerAutoRebuildStandard",
            "AutoMinerAutoRebuildAsteroid"
        };

        private bool automationEnabled = true;
        private AutoRebuildMode suspendedAutoRebuildMode = AutoRebuildMode.Off;
        private bool hasSuspendedAutoRebuildMode;
        private LandPodMode landPodMode;
        private bool asteroidPodsEnabled;
        private bool bringVacstone = true;
        private TargetSelectionMode targetSelectionMode;
        private bool asteroidStrandedMessageShown;
        private bool landOnlyMismatchMessageShown;
        private bool reflectionFailureLogged;
        private bool podWaitDiagnosticsLogged;
        private bool roofBeforeBuildMessageShown;
        private bool launchPortOccupiedMessageShown;
        private int lastRoofedBuiltPodId = -1;
        private int lastStrandedPodId = -1;
        private bool defaultsApplied;
        private string cachedAutomationStatusKey;
        private bool autoRebuildPresentationCached;
        private bool autoRebuildPresentationFailureLogged;
        private AutoRebuildMode cachedAutoRebuildPresentationMode;
        private string cachedAutoRebuildLabel;
        private string cachedAutoRebuildDesc;
        private Texture cachedAutoRebuildIcon;

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            if (!respawningAfterLoad && !defaultsApplied)
            {
                ApplyDefaultsForNewLauncher();
            }

            defaultsApplied = true;
            cachedAutomationStatusKey = null;
            if (automationEnabled)
            {
                SuspendBaseAutoRebuild();
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref automationEnabled, "automationEnabled", true);
            Scribe_Values.Look(ref suspendedAutoRebuildMode, "suspendedAutoRebuildMode", AutoRebuildMode.Off);
            Scribe_Values.Look(ref hasSuspendedAutoRebuildMode, "hasSuspendedAutoRebuildMode", false);
            Scribe_Values.Look(ref landPodMode, "landPodMode", LandPodMode.Off);
            Scribe_Values.Look(ref asteroidPodsEnabled, "asteroidPodsEnabled", false);
            Scribe_Values.Look(ref bringVacstone, "bringVacstone", true);
            Scribe_Values.Look(ref targetSelectionMode, "targetSelectionMode", TargetSelectionMode.FirstDiscovered);
            Scribe_Values.Look(ref asteroidStrandedMessageShown, "asteroidStrandedMessageShown", false);
            Scribe_Values.Look(ref landOnlyMismatchMessageShown, "landOnlyMismatchMessageShown", false);
            Scribe_Values.Look(ref roofBeforeBuildMessageShown, "roofBeforeBuildMessageShown", false);
            Scribe_Values.Look(ref launchPortOccupiedMessageShown, "launchPortOccupiedMessageShown", false);
            Scribe_Values.Look(ref lastRoofedBuiltPodId, "lastRoofedBuiltPodId", -1);
            Scribe_Values.Look(ref lastStrandedPodId, "lastStrandedPodId", -1);
            Scribe_Values.Look(ref defaultsApplied, "defaultsApplied", false);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                defaultsApplied = true;
                cachedAutomationStatusKey = null;
                if (automationEnabled)
                {
                    SuspendBaseAutoRebuild();
                }
                else
                {
                    RestoreBaseAutoRebuild();
                }
            }
        }

        protected override void Tick()
        {
            base.Tick();
            if (!Spawned || !this.IsHashIntervalTick(250))
            {
                return;
            }

            if (!LaunchPortOccupiedByOtherConstruction())
            {
                launchPortOccupiedMessageShown = false;
            }

            if (automationEnabled)
            {
                TryAutomate();
            }

            RefreshAutomationStatus();
        }

        public override string GetInspectString()
        {
            string baseInspect = base.GetInspectString();
            if (cachedAutomationStatusKey == null)
            {
                RefreshAutomationStatus();
            }

            string statusLine = "AMUFA_CurrentStatus".Translate().ToString() +
                cachedAutomationStatusKey.Translate().ToString();
            return string.IsNullOrEmpty(baseInspect)
                ? statusLine
                : baseInspect + "\n" + statusLine;
        }

        private void RefreshAutomationStatus()
        {
            cachedAutomationStatusKey = CalculateAutomationStatusKey();
        }

        private void InvalidateAutomationStatus()
        {
            cachedAutomationStatusKey = null;
        }

        private string CalculateAutomationStatusKey()
        {
            if (!automationEnabled)
            {
                return "AMUFA_StatusAutomationOff";
            }

            if (Map == null)
            {
                return "AMUFA_StatusNoReachableTarget";
            }

            if (FuelingPortRoofed())
            {
                return "AMUFA_StatusRoofed";
            }

            CompAutoMinerLaunchable builtPod = FindAdjacentPod();
            if (builtPod != null && builtPod.parent != null && builtPod.parent.def != null)
            {
                bool asteroidPod = builtPod.parent.def.defName == AsteroidPodDefName;
                bool categoryEnabled = asteroidPod
                    ? asteroidPodsEnabled && AsteroidMinerOptionAvailable()
                    : landPodMode != LandPodMode.Off;
                if (!categoryEnabled)
                {
                    return "AMUFA_StatusNoReachableTarget";
                }

                WorldObject theoreticalTarget = TargetSelector.ChooseTarget(
                    this,
                    builtPod.parent.def,
                    asteroidPod,
                    targetSelectionMode,
                    false,
                    false);
                if (theoreticalTarget == null)
                {
                    return "AMUFA_StatusNoReachableTarget";
                }

                WorldObject fueledTarget = TargetSelector.ChooseTarget(
                    this,
                    builtPod.parent.def,
                    asteroidPod,
                    targetSelectionMode,
                    true,
                    false);
                if (fueledTarget == null)
                {
                    return "AMUFA_StatusFuelInsufficient";
                }

                return asteroidPod ? "AMUFA_StatusPreparingAsteroid" : "AMUFA_StatusPreparingLand";
            }

            ThingDef pendingPodDef = PendingPodDefAtFuelingPort();
            if (pendingPodDef != null)
            {
                return pendingPodDef.defName == AsteroidPodDefName
                    ? "AMUFA_StatusPreparingAsteroid"
                    : "AMUFA_StatusPreparingLand";
            }

            ThingDef asteroidPodDef = PodDef(AsteroidPodDefName);
            if (AsteroidMinerOptionAvailable() && asteroidPodsEnabled &&
                TargetSelector.ChooseTarget(
                    this, asteroidPodDef, true, targetSelectionMode, false, false) != null)
            {
                return LaunchPortOccupiedByOtherConstruction()
                    ? "AMUFA_StatusLaunchPortOccupied"
                    : "AMUFA_StatusPreparingAsteroid";
            }

            ThingDef landPodDef = ConfiguredLandPodDef();
            if (landPodDef != null && TargetSelector.ChooseTarget(
                    this, landPodDef, false, targetSelectionMode, false, false) != null)
            {
                return LaunchPortOccupiedByOtherConstruction()
                    ? "AMUFA_StatusLaunchPortOccupied"
                    : "AMUFA_StatusPreparingLand";
            }

            return "AMUFA_StatusNoReachableTarget";
        }
        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo gizmo in base.GetGizmos())
            {
                Command_Action command = gizmo as Command_Action;
                if (command != null && IsOriginalAutoRebuildCommand(command))
                {
                    if (automationEnabled)
                    {
                        ApplySuspendedAutoRebuildPresentation(command);
                        command.Disable("AMUFA_AutoRebuildManaged".Translate());
                    }

                    yield return command;
                    continue;
                }

                if (automationEnabled && command != null && IsOriginalManualBuildCommand(command))
                {
                    continue;
                }

                yield return gizmo;
            }

            yield return AutomationCommand();
            if (!automationEnabled)
            {
                yield break;
            }

            bool advancedOptions = AdvancedMinerOptionsAvailable();
            bool asteroidOption = AsteroidMinerOptionAvailable();
            yield return TargetSelectionCommand();
            yield return LandPodCommand(advancedOptions);
            if (asteroidOption)
            {
                yield return AsteroidPodCommand();
                if (asteroidPodsEnabled)
                {
                    yield return VacstoneCommand();
                }
            }
        }

        private void TryAutomate()
        {
            ForceBaseAutoRebuildOff();
            CompAutoMinerLaunchable adjacentPod = FindAdjacentPod();
            if (adjacentPod != null)
            {
                HandleBuiltPod(adjacentPod);
                return;
            }

            podWaitDiagnosticsLogged = false;
            if (FuelingPortRoofed())
            {
                NotifyRoofBlockedBeforeBuild();
                return;
            }

            roofBeforeBuildMessageShown = false;
            bool launchPortOccupied = LaunchPortOccupiedByOtherConstruction();
            ThingDef asteroidPodDef = PodDef(AsteroidPodDefName);
            if (asteroidPodsEnabled && AsteroidMinerOptionAvailable())
            {
                WorldObject asteroidTarget = TargetSelector.ChooseTarget(
                    this, asteroidPodDef, true, targetSelectionMode, false, false);
                if (asteroidTarget != null)
                {
                    asteroidStrandedMessageShown = false;
                    landOnlyMismatchMessageShown = false;
                    if (launchPortOccupied)
                    {
                        NotifyLaunchPortOccupied();
                        return;
                    }

                    TryPlaceConfiguredBlueprint(AutoRebuildMode.AsteroidMiner);
                    return;
                }
            }

            ThingDef landPodDef = ConfiguredLandPodDef();
            if (landPodDef != null)
            {
                WorldObject landTarget = TargetSelector.ChooseTarget(
                    this, landPodDef, false, targetSelectionMode, false, false);
                if (landTarget != null)
                {
                    landOnlyMismatchMessageShown = false;
                    if (launchPortOccupied)
                    {
                        NotifyLaunchPortOccupied();
                        return;
                    }

                    TryPlaceConfiguredBlueprint(
                        landPodMode == LandPodMode.Prototype
                            ? AutoRebuildMode.ProtoMiner
                            : AutoRebuildMode.StandardMiner);
                    return;
                }
            }

            NotifyLandOnlyMismatchIfNeeded();
        }

        private void HandleBuiltPod(CompAutoMinerLaunchable pod)
        {
            if (FuelingPortRoofed())
            {
                NotifyRoofBlockedForBuiltPod(pod);
                return;
            }

            string podDefName = pod.parent == null || pod.parent.def == null ? null : pod.parent.def.defName;
            bool asteroidPod = podDefName == AsteroidPodDefName;
            if (asteroidPod)
            {
                if (!asteroidPodsEnabled || !AsteroidMinerOptionAvailable())
                {
                    ClearStrandedMessageState();
                    return;
                }

                WorldObject theoreticalTarget = TargetSelector.ChooseTarget(
                    this, pod.parent.def, true, targetSelectionMode, false, !podWaitDiagnosticsLogged);
                if (theoreticalTarget == null)
                {
                    podWaitDiagnosticsLogged = true;
                    NotifyNoReachableTargetForBuiltPod(pod);
                    return;
                }

                ClearStrandedMessageState();
                WorldObject target = TargetSelector.ChooseTarget(
                    this, pod.parent.def, true, targetSelectionMode, true, !podWaitDiagnosticsLogged);
                if (target == null)
                {
                    podWaitDiagnosticsLogged = true;
                    return;
                }

                podWaitDiagnosticsLogged = false;
                if (BringVacstoneField != null)
                {
                    BringVacstoneField.SetValue(pod, bringVacstone);
                }

                Launch(pod, target);
                return;
            }

            bool allowedLandPod = landPodMode != LandPodMode.Off &&
                (podDefName == PrototypePodDefName ||
                    (podDefName == StandardPodDefName && AdvancedMinerOptionsAvailable()));
            if (!allowedLandPod)
            {
                ClearStrandedMessageState();
                return;
            }

            WorldObject theoreticalLandTarget = TargetSelector.ChooseTarget(
                this, pod.parent.def, false, targetSelectionMode, false, !podWaitDiagnosticsLogged);
            if (theoreticalLandTarget == null)
            {
                podWaitDiagnosticsLogged = true;
                NotifyNoReachableTargetForBuiltPod(pod);
                return;
            }

            ClearStrandedMessageState();
            WorldObject landTarget = TargetSelector.ChooseTarget(
                this, pod.parent.def, false, targetSelectionMode, true, !podWaitDiagnosticsLogged);
            if (landTarget != null)
            {
                podWaitDiagnosticsLogged = false;
                Launch(pod, landTarget);
            }
            else
            {
                podWaitDiagnosticsLogged = true;
            }
        }

        private void Launch(CompAutoMinerLaunchable pod, WorldObject target)
        {
            if (ChooseWorldTargetMethod == null)
            {
                LogReflectionFailure("TheAutoMiner.CompAutoMinerLaunchable.ChoseWorldTarget was not found.");
                return;
            }

            try
            {
                Log.Message("[AMUFA] Launch attempt: pod=" + pod.parent.def.defName +
                    ", target=" + target.LabelCap +
                    ", tile=" + target.Tile +
                    ", launcher=" + GetUniqueLoadID() + ".");
                AutomationWorldComponent component = AutomationWorldComponent.Current;
                if (component != null)
                {
                    component.NotifyTargetReserved(target.Tile);
                }

                object result = ChooseWorldTargetMethod.Invoke(
                    pod,
                    new object[] { new GlobalTargetInfo(target) });
                Log.Message("[AMUFA] Launch target method returned " +
                    (result == null ? "null" : result.ToString()) + ".");
            }
            catch (TargetInvocationException exception)
            {
                LogReflectionFailure(exception.InnerException == null ? exception.ToString() : exception.InnerException.ToString());
            }
            catch (Exception exception)
            {
                LogReflectionFailure(exception.ToString());
            }
        }

        private void TryPlaceConfiguredBlueprint(AutoRebuildMode mode, bool logDiagnostics = false)
        {
            if (BaseAutoRebuildModeField == null || TryPlaceBlueprintMethod == null)
            {
                LogReflectionFailure("TheAutoMiner automatic rebuild members were not found.");
                return;
            }

            try
            {
                BaseAutoRebuildModeField.SetValue(this, mode);
                TryPlaceBlueprintMethod.Invoke(this, null);
                if (logDiagnostics)
                {
                    LogBlueprintPlacementResult(mode);
                }
            }
            catch (TargetInvocationException exception)
            {
                LogReflectionFailure(exception.InnerException == null ? exception.ToString() : exception.InnerException.ToString());
            }
            catch (Exception exception)
            {
                LogReflectionFailure(exception.ToString());
            }
            finally
            {
                if (automationEnabled)
                {
                    ForceBaseAutoRebuildOff();
                }
            }
        }

        private void LogBlueprintPlacementResult(AutoRebuildMode mode)
        {
            ThingDef expectedDef = PodDefForMode(mode);
            if (expectedDef == null || Map == null)
            {
                Log.Warning("[AMUFA] Blueprint placement failed: no pod Def or map for mode " + mode + ".");
                return;
            }

            IntVec3 fuelingPortCell = FuelingPortUtility.GetFuelingPortCell(this);
            bool placed = fuelingPortCell.GetThingList(Map)
                .OfType<Blueprint_Build>()
                .Any(blueprint => blueprint.def != null &&
                    blueprint.def.entityDefToBuild == expectedDef);
            if (placed)
            {
                Log.Message("[AMUFA] Blueprint placed: " + expectedDef.defName +
                    " at " + fuelingPortCell.ToString() + " for " + GetUniqueLoadID() + ".");
                return;
            }

            string unfinishedResearch = expectedDef.researchPrerequisites == null
                ? "none"
                : string.Join(",", expectedDef.researchPrerequisites
                    .Where(project => project != null && !project.IsFinished)
                    .Select(project => project.defName).ToArray());
            string occupants = string.Join(",", fuelingPortCell.GetThingList(Map)
                .Where(thing => thing != null && thing.def != null)
                .Select(thing => thing.def.defName).ToArray());
            Log.Warning("[AMUFA] Blueprint was not placed: pod=" + expectedDef.defName +
                ", mode=" + mode +
                ", portCell=" + fuelingPortCell.ToString() +
                ", unfinishedResearch=" + (string.IsNullOrEmpty(unfinishedResearch) ? "none" : unfinishedResearch) +
                ", occupants=" + (string.IsNullOrEmpty(occupants) ? "none" : occupants) + ".");
        }

        private ThingDef PodDefForMode(AutoRebuildMode mode)
        {
            switch (mode)
            {
                case AutoRebuildMode.ProtoMiner:
                    return PodDef(PrototypePodDefName);
                case AutoRebuildMode.StandardMiner:
                    return PodDef(StandardPodDefName);
                case AutoRebuildMode.AsteroidMiner:
                    return PodDef(AsteroidPodDefName);
                default:
                    return null;
            }
        }

        private CompAutoMinerLaunchable FindAdjacentPod()
        {
            if (Map == null)
            {
                return null;
            }

            IntVec3 fuelingPortCell = FuelingPortUtility.GetFuelingPortCell(this);
            foreach (Thing thing in fuelingPortCell.GetThingList(Map))
            {
                string defName = thing.def == null ? null : thing.def.defName;
                if (defName != AsteroidPodDefName &&
                    defName != PrototypePodDefName &&
                    defName != StandardPodDefName)
                {
                    continue;
                }

                CompAutoMinerLaunchable launchable = thing.TryGetComp<CompAutoMinerLaunchable>();
                if (launchable != null)
                {
                    return launchable;
                }
            }

            return null;
        }

        private ThingDef PendingPodDefAtFuelingPort()
        {
            if (Map == null)
            {
                return null;
            }

            IntVec3 fuelingPortCell = FuelingPortUtility.GetFuelingPortCell(this);
            foreach (Thing thing in fuelingPortCell.GetThingList(Map))
            {
                ThingDef entityDef = thing == null || thing.def == null
                    ? null
                    : thing.def.entityDefToBuild as ThingDef;
                if (entityDef != null &&
                    (entityDef.defName == PrototypePodDefName ||
                     entityDef.defName == StandardPodDefName ||
                     entityDef.defName == AsteroidPodDefName))
                {
                    return entityDef;
                }
            }

            return null;
        }

        private bool LaunchPortOccupiedByOtherConstruction()
        {
            if (Map == null)
            {
                return false;
            }

            IntVec3 fuelingPortCell = FuelingPortUtility.GetFuelingPortCell(this);
            foreach (Thing thing in fuelingPortCell.GetThingList(Map))
            {
                if (thing == null || thing == this || thing.def == null)
                {
                    continue;
                }

                ThingDef entityDef = thing.def.entityDefToBuild as ThingDef;
                if (IsMinerPodDef(thing.def) || IsMinerPodDef(entityDef))
                {
                    continue;
                }

                if (thing is Blueprint || thing is Frame || thing is Building)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsMinerPodDef(ThingDef def)
        {
            if (def == null)
            {
                return false;
            }

            return def.defName == PrototypePodDefName ||
                def.defName == StandardPodDefName ||
                def.defName == AsteroidPodDefName;
        }

        private bool FuelingPortRoofed()
        {
            if (Map == null)
            {
                return false;
            }

            IntVec3 fuelingPortCell = FuelingPortUtility.GetFuelingPortCell(this);
            return Map.roofGrid.Roofed(fuelingPortCell.x, fuelingPortCell.z);
        }

        private void NotifyRoofBlockedBeforeBuild()
        {
            if (roofBeforeBuildMessageShown)
            {
                return;
            }

            roofBeforeBuildMessageShown = true;
            Messages.Message(
                "AMUFA_RoofBlocked".Translate(LabelCap),
                this,
                MessageTypeDefOf.CautionInput,
                false);
        }

        private void NotifyRoofBlockedForBuiltPod(CompAutoMinerLaunchable pod)
        {
            if (pod == null || pod.parent == null)
            {
                return;
            }

            int podId = pod.parent.thingIDNumber;
            if (lastRoofedBuiltPodId == podId)
            {
                return;
            }

            lastRoofedBuiltPodId = podId;
            Messages.Message(
                "AMUFA_RoofBlocked".Translate(LabelCap),
                pod.parent,
                MessageTypeDefOf.CautionInput,
                false);
        }

        private void NotifyLaunchPortOccupied()
        {
            if (launchPortOccupiedMessageShown)
            {
                return;
            }

            launchPortOccupiedMessageShown = true;
            Messages.Message(
                "AMUFA_LaunchPortOccupied".Translate(LabelCap),
                this,
                MessageTypeDefOf.CautionInput,
                false);
        }
        private void NotifyNoReachableTargetForBuiltPod(CompAutoMinerLaunchable pod)
        {
            if (pod == null || pod.parent == null)
            {
                return;
            }

            int podId = pod.parent.thingIDNumber;
            if (asteroidStrandedMessageShown && lastStrandedPodId == podId)
            {
                return;
            }

            asteroidStrandedMessageShown = true;
            lastStrandedPodId = podId;
            Messages.Message(
                "AMUFA_AsteroidPodStranded".Translate(),
                pod.parent,
                MessageTypeDefOf.CautionInput,
                false);
        }

        private void ClearStrandedMessageState()
        {
            asteroidStrandedMessageShown = false;
            lastStrandedPodId = -1;
        }

        private void NotifyLandOnlyMismatchIfNeeded()
        {
            landOnlyMismatchMessageShown = false;
        }

        private void SuspendBaseAutoRebuild()
        {
            if (BaseAutoRebuildModeField == null)
            {
                return;
            }

            if (!hasSuspendedAutoRebuildMode)
            {
                suspendedAutoRebuildMode = (AutoRebuildMode)BaseAutoRebuildModeField.GetValue(this);
                autoRebuildPresentationCached = false;
                hasSuspendedAutoRebuildMode = true;
            }

            ForceBaseAutoRebuildOff();
        }

        private void RestoreBaseAutoRebuild()
        {
            if (!hasSuspendedAutoRebuildMode || BaseAutoRebuildModeField == null)
            {
                return;
            }

            BaseAutoRebuildModeField.SetValue(this, suspendedAutoRebuildMode);
            suspendedAutoRebuildMode = AutoRebuildMode.Off;
            autoRebuildPresentationCached = false;
            hasSuspendedAutoRebuildMode = false;
        }

        private void ApplySuspendedAutoRebuildPresentation(Command_Action command)
        {
            AutoRebuildMode mode = hasSuspendedAutoRebuildMode
                ? suspendedAutoRebuildMode
                : AutoRebuildMode.Off;
            if (!autoRebuildPresentationCached || cachedAutoRebuildPresentationMode != mode)
            {
                CacheAutoRebuildPresentation(mode);
            }

            command.defaultLabel = cachedAutoRebuildLabel ?? AutoRebuildLabel(mode);
            if (cachedAutoRebuildDesc != null)
            {
                command.defaultDesc = cachedAutoRebuildDesc;
            }

            if (cachedAutoRebuildIcon != null)
            {
                command.icon = cachedAutoRebuildIcon;
            }
        }

        private void CacheAutoRebuildPresentation(AutoRebuildMode mode)
        {
            autoRebuildPresentationCached = true;
            cachedAutoRebuildPresentationMode = mode;
            cachedAutoRebuildLabel = AutoRebuildLabel(mode);
            cachedAutoRebuildDesc = null;
            cachedAutoRebuildIcon = null;
            if (BaseAutoRebuildModeField == null || CreateAutoRebuildGizmoMethod == null)
            {
                return;
            }

            AutoRebuildMode activeMode = (AutoRebuildMode)BaseAutoRebuildModeField.GetValue(this);
            try
            {
                BaseAutoRebuildModeField.SetValue(this, mode);
                Command_Action originalCommand = CreateAutoRebuildGizmoMethod.Invoke(this, null) as Command_Action;
                if (originalCommand != null)
                {
                    cachedAutoRebuildLabel = originalCommand.defaultLabel;
                    cachedAutoRebuildDesc = originalCommand.defaultDesc;
                    cachedAutoRebuildIcon = originalCommand.icon;
                }
            }
            catch (Exception exception)
            {
                if (!autoRebuildPresentationFailureLogged)
                {
                    autoRebuildPresentationFailureLogged = true;
                    Exception details = exception is TargetInvocationException && exception.InnerException != null
                        ? exception.InnerException
                        : exception;
                    Log.Warning("[Auto Miner Unit - Fully Automatic] Could not mirror the original auto-rebuild button presentation: " + details);
                }
            }
            finally
            {
                BaseAutoRebuildModeField.SetValue(this, activeMode);
            }
        }

        private static string AutoRebuildLabel(AutoRebuildMode mode)
        {
            switch (mode)
            {
                case AutoRebuildMode.ProtoMiner:
                    return "AutoMinerAutoRebuildProto".Translate();
                case AutoRebuildMode.StandardMiner:
                    return "AutoMinerAutoRebuildStandard".Translate();
                case AutoRebuildMode.AsteroidMiner:
                    return "AutoMinerAutoRebuildAsteroid".Translate();
                default:
                    return "AutoMinerAutoRebuildOff".Translate();
            }
        }

        private void ForceBaseAutoRebuildOff()
        {
            if (BaseAutoRebuildModeField != null)
            {
                BaseAutoRebuildModeField.SetValue(this, AutoRebuildMode.Off);
            }
        }

        private static bool AdvancedMinerOptionsAvailable()
        {
            if (DebugSettings.godMode)
            {
                return true;
            }

            ResearchProjectDef fabrication = DefDatabase<ResearchProjectDef>.GetNamedSilentFail("Fabrication");
            return fabrication != null && fabrication.IsFinished;
        }

        private static bool AsteroidMinerOptionAvailable()
        {
            if (DebugSettings.godMode)
            {
                return true;
            }

            ThingDef asteroidPod = PodDef(AsteroidPodDefName);
            return asteroidPod != null && asteroidPod.IsResearchFinished;
        }

        internal float AutomationFuelLimit
        {
            get
            {
                CompRefuelable fuel = GetComp<CompRefuelable>();
                if (fuel == null)
                {
                    return 0f;
                }

                return Mathf.Clamp(fuel.TargetFuelLevel, 0f, fuel.Props.fuelCapacity);
            }
        }

        private void ApplyDefaultsForNewLauncher()
        {
            automationEnabled = FullyAutomaticMod.DefaultAutomationEnabled;
            targetSelectionMode = FullyAutomaticMod.DefaultTargetSelectionMode;
            CompRefuelable fuel = GetComp<CompRefuelable>();
            if (fuel != null)
            {
                fuel.TargetFuelLevel = Mathf.Clamp(
                    FullyAutomaticMod.DefaultFuelLimit,
                    0f,
                    fuel.Props.fuelCapacity);
            }
        }

        private ThingDef ConfiguredLandPodDef()
        {
            switch (landPodMode)
            {
                case LandPodMode.Prototype:
                    return PodDef(PrototypePodDefName);
                case LandPodMode.Standard:
                    return AdvancedMinerOptionsAvailable() ? PodDef(StandardPodDefName) : null;
                default:
                    return null;
            }
        }

        private static ThingDef PodDef(string defName)
        {
            return DefDatabase<ThingDef>.GetNamedSilentFail(defName);
        }

        private Texture2D CommandIcon(string preferredPodDefName)
        {
            ThingDef iconDef = preferredPodDefName == null ? def : PodDef(preferredPodDefName);
            return iconDef == null || iconDef.uiIcon == null ? def.uiIcon : iconDef.uiIcon;
        }

        private Texture2D DisabledIcon()
        {
            Texture2D originalIcon = OriginalCancelIconField == null
                ? null
                : OriginalCancelIconField.GetValue(null) as Texture2D;
            return originalIcon ?? TexCommand.ForbidOn;
        }

        private Command_Action AutomationCommand()
        {
            return new Command_Action
            {
                defaultLabel = (automationEnabled ? "AMUFA_AutomationOn" : "AMUFA_AutomationOff").Translate(),
                defaultDesc = (automationEnabled ? "AMUFA_AutomationOnDesc" : "AMUFA_AutomationOffDesc").Translate(),
                icon = automationEnabled ? TexCommand.ForbidOff : DisabledIcon(),
                action = delegate
                {
                    if (automationEnabled)
                    {
                        automationEnabled = false;
                        RestoreBaseAutoRebuild();
                    }
                    else
                    {
                        SuspendBaseAutoRebuild();
                        automationEnabled = true;
                    }

                    ReconcileConfiguredBlueprint();
                }
            };
        }

        private Command_Action LandPodCommand(bool advancedOptions)
        {
            string labelKey;
            string descKey;
            string iconDefName;
            LandPodMode displayedMode = advancedOptions
                ? landPodMode
                : (landPodMode == LandPodMode.Prototype ? LandPodMode.Prototype : LandPodMode.Off);
            switch (displayedMode)
            {
                case LandPodMode.Prototype:
                    labelKey = "AMUFA_LandProto";
                    descKey = "AMUFA_LandProtoDesc";
                    iconDefName = PrototypePodDefName;
                    break;
                case LandPodMode.Standard:
                    labelKey = "AMUFA_LandStandard";
                    descKey = "AMUFA_LandStandardDesc";
                    iconDefName = StandardPodDefName;
                    break;
                default:
                    labelKey = "AMUFA_LandOff";
                    descKey = "AMUFA_LandOffDesc";
                    iconDefName = PrototypePodDefName;
                    break;
            }

            return new Command_Action
            {
                defaultLabel = labelKey.Translate(),
                defaultDesc = descKey.Translate(),
                icon = displayedMode == LandPodMode.Off ? DisabledIcon() : CommandIcon(iconDefName),
                action = delegate
                {
                    landPodMode = advancedOptions
                        ? (LandPodMode)(((int)landPodMode + 1) % 3)
                        : (displayedMode == LandPodMode.Prototype ? LandPodMode.Off : LandPodMode.Prototype);
                    ReconcileConfiguredBlueprint();
                }
            };
        }

        private Command_Action AsteroidPodCommand()
        {
            return new Command_Action
            {
                defaultLabel = (asteroidPodsEnabled ? "AMUFA_AsteroidOn" : "AMUFA_AsteroidOff").Translate(),
                defaultDesc = (asteroidPodsEnabled ? "AMUFA_AsteroidOnDesc" : "AMUFA_AsteroidOffDesc").Translate(),
                icon = asteroidPodsEnabled ? CommandIcon(AsteroidPodDefName) : DisabledIcon(),
                action = delegate
                {
                    asteroidPodsEnabled = !asteroidPodsEnabled;
                    ReconcileConfiguredBlueprint();
                }
            };
        }

        private Command_Action TargetSelectionCommand()
        {
            string labelKey;
            string descKey;
            Texture2D icon;
            switch (targetSelectionMode)
            {
                case TargetSelectionMode.Nearest:
                    labelKey = "AMUFA_StrategyNearest";
                    descKey = "AMUFA_StrategyNearestDesc";
                    icon = StrategyNearestIcon;
                    break;
                case TargetSelectionMode.Farthest:
                    labelKey = "AMUFA_StrategyFarthest";
                    descKey = "AMUFA_StrategyFarthestDesc";
                    icon = StrategyFarthestIcon;
                    break;
                default:
                    labelKey = "AMUFA_StrategyOldest";
                    descKey = "AMUFA_StrategyOldestDesc";
                    icon = StrategyFirstDiscoveredIcon;
                    break;
            }

            return new Command_Action
            {
                defaultLabel = labelKey.Translate(),
                defaultDesc = descKey.Translate(),
                icon = icon,
                action = delegate
                {
                    targetSelectionMode = (TargetSelectionMode)(((int)targetSelectionMode + 1) % 3);
                    InvalidateAutomationStatus();
                }
            };
        }

        private Command_Action VacstoneCommand()
        {
            return new Command_Action
            {
                defaultLabel = (bringVacstone ? "AMUFA_VacstoneOn" : "AMUFA_VacstoneOff").Translate(),
                defaultDesc = (bringVacstone ? "AMUFA_VacstoneOnDesc" : "AMUFA_VacstoneOffDesc").Translate(),
                icon = CommandIcon(AsteroidPodDefName),
                action = delegate { bringVacstone = !bringVacstone; }
            };
        }

        private void ReconcileConfiguredBlueprint()
        {
            InvalidateAutomationStatus();
            CancelAllMinerBlueprints();
            if (!automationEnabled)
            {
                return;
            }

            CompAutoMinerLaunchable builtPod = FindAdjacentPod();
            if (builtPod != null)
            {
                if (FuelingPortRoofed())
                {
                    NotifyRoofBlockedForBuiltPod(builtPod);
                }

                return;
            }

            if (FuelingPortRoofed())
            {
                NotifyRoofBlockedBeforeBuild();
                return;
            }

            roofBeforeBuildMessageShown = false;
            bool launchPortOccupied = LaunchPortOccupiedByOtherConstruction();
            if (!launchPortOccupied)
            {
                launchPortOccupiedMessageShown = false;
            }

            ThingDef asteroidPodDef = PodDef(AsteroidPodDefName);
            if (AsteroidMinerOptionAvailable() && asteroidPodsEnabled && TargetSelector.ChooseTarget(
                    this, asteroidPodDef, true, targetSelectionMode, false, true) != null)
            {
                if (launchPortOccupied)
                {
                    NotifyLaunchPortOccupied();
                    return;
                }

                TryPlaceConfiguredBlueprint(AutoRebuildMode.AsteroidMiner, true);
                return;
            }

            ThingDef landPodDef = ConfiguredLandPodDef();
            if (landPodDef != null && TargetSelector.ChooseTarget(
                    this, landPodDef, false, targetSelectionMode, false, true) != null)
            {
                if (launchPortOccupied)
                {
                    NotifyLaunchPortOccupied();
                    return;
                }

                TryPlaceConfiguredBlueprint(
                    landPodMode == LandPodMode.Prototype
                        ? AutoRebuildMode.ProtoMiner
                        : AutoRebuildMode.StandardMiner,
                    true);
            }
        }

        private void CancelAllMinerBlueprints()
        {
            if (CancelBlueprintMethod == null)
            {
                LogReflectionFailure("TheAutoMiner blueprint cancellation method was not found.");
                return;
            }

            try
            {
                CancelBlueprintMethod.Invoke(this, new object[] { AutoRebuildMode.ProtoMiner });
                CancelBlueprintMethod.Invoke(this, new object[] { AutoRebuildMode.StandardMiner });
                CancelBlueprintMethod.Invoke(this, new object[] { AutoRebuildMode.AsteroidMiner });
            }
            catch (TargetInvocationException exception)
            {
                LogReflectionFailure(exception.InnerException == null ? exception.ToString() : exception.InnerException.ToString());
            }
            catch (Exception exception)
            {
                LogReflectionFailure(exception.ToString());
            }
        }
        private static bool IsOriginalAutoRebuildCommand(Command_Action command)
        {
            string label = command.defaultLabel;
            return OriginalAutoRebuildLabelKeys.Any(key => label == key.Translate());
        }

        private static bool IsOriginalManualBuildCommand(Command_Action command)
        {
            string label = command.defaultLabel;
            string[] podDefNames =
            {
                PrototypePodDefName,
                StandardPodDefName,
                AsteroidPodDefName
            };

            foreach (string defName in podDefNames)
            {
                ThingDef podDef = PodDef(defName);
                if (podDef != null && label == "BuildThing".Translate(podDef.label))
                {
                    return true;
                }
            }

            return false;
        }

        private void LogReflectionFailure(string details)
        {
            if (reflectionFailureLogged)
            {
                return;
            }

            reflectionFailureLogged = true;
            Log.Error("[Auto Miner Unit - Fully Automatic] Automation was disabled for " + GetUniqueLoadID() + ": " + details);
            automationEnabled = false;
            RestoreBaseAutoRebuild();
            InvalidateAutomationStatus();
        }
    }
}



