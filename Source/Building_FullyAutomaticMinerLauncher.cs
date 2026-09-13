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
        private enum AutomationDecisionStage
        {
            AutomationOff,
            NoReachableTarget,
            Roofed,
            BuiltPodDisabled,
            BuiltPodNoReachableTarget,
            BuiltPodWaitingFuel,
            BuiltPodReadyToLaunch,
            PendingBlueprint,
            LaunchPortOccupied,
            PlaceBlueprint
        }

        private sealed class AutomationDecision
        {
            public AutomationDecisionStage Stage;
            public CompAutoMinerLaunchable Pod;
            public WorldObject Target;
            public bool Asteroid;
            public AutoRebuildMode BlueprintMode;
        }

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
        private static readonly Texture2D VacstoneEnabledIcon =
            ContentFinder<Texture2D>.Get("Things/Building/Production/DeepDrill");

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
                cachedAutomationStatusKey = StatusKeyForDecision(TryAutomate());
            }
            else
            {
                RefreshAutomationStatus();
            }
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
            return StatusKeyForDecision(EvaluateAutomationDecision(false, false));
        }

        private AutomationDecision EvaluateAutomationDecision(
            bool logBuiltPodDiagnostics,
            bool logBlueprintDiagnostics)
        {
            if (!automationEnabled)
            {
                return new AutomationDecision { Stage = AutomationDecisionStage.AutomationOff };
            }

            if (Map == null)
            {
                return new AutomationDecision { Stage = AutomationDecisionStage.NoReachableTarget };
            }

            CompAutoMinerLaunchable builtPod = FindAdjacentPod();
            if (FuelingPortRoofed())
            {
                return new AutomationDecision
                {
                    Stage = AutomationDecisionStage.Roofed,
                    Pod = builtPod
                };
            }

            if (builtPod != null)
            {
                return EvaluateBuiltPodDecision(builtPod, logBuiltPodDiagnostics);
            }

            return EvaluateBlueprintDecision(logBlueprintDiagnostics);
        }

        private AutomationDecision EvaluateBuiltPodDecision(
            CompAutoMinerLaunchable pod,
            bool logDiagnostics)
        {
            ThingDef podDef = pod == null || pod.parent == null ? null : pod.parent.def;
            string podDefName = podDef == null ? null : podDef.defName;
            bool asteroidPod = podDefName == AsteroidPodDefName;
            bool categoryEnabled = asteroidPod
                ? asteroidPodsEnabled && AsteroidMinerOptionAvailable()
                : landPodMode != LandPodMode.Off &&
                    (podDefName == PrototypePodDefName || podDefName == StandardPodDefName) &&
                    PodOptionAvailable(podDefName);
            if (!categoryEnabled)
            {
                return new AutomationDecision
                {
                    Stage = AutomationDecisionStage.BuiltPodDisabled,
                    Pod = pod,
                    Asteroid = asteroidPod
                };
            }

            WorldObject fueledTarget = TargetSelector.ChooseTarget(
                this, podDef, asteroidPod, targetSelectionMode, true, logDiagnostics);
            if (fueledTarget != null)
            {
                return new AutomationDecision
                {
                    Stage = AutomationDecisionStage.BuiltPodReadyToLaunch,
                    Pod = pod,
                    Target = fueledTarget,
                    Asteroid = asteroidPod
                };
            }

            WorldObject plannedTarget = TargetSelector.ChooseTarget(
                this, podDef, asteroidPod, targetSelectionMode, false, logDiagnostics);
            if (plannedTarget == null)
            {
                return new AutomationDecision
                {
                    Stage = AutomationDecisionStage.BuiltPodNoReachableTarget,
                    Pod = pod,
                    Asteroid = asteroidPod
                };
            }

            return new AutomationDecision
            {
                Stage = AutomationDecisionStage.BuiltPodWaitingFuel,
                Pod = pod,
                Asteroid = asteroidPod
            };
        }

        private AutomationDecision EvaluateBlueprintDecision(bool logDiagnostics)
        {
            ThingDef pendingPodDef = PendingPodDefAtFuelingPort();
            if (pendingPodDef != null)
            {
                return new AutomationDecision
                {
                    Stage = AutomationDecisionStage.PendingBlueprint,
                    Asteroid = pendingPodDef.defName == AsteroidPodDefName
                };
            }

            ThingDef asteroidPodDef = PodDef(AsteroidPodDefName);
            if (asteroidPodsEnabled && AsteroidMinerOptionAvailable())
            {
                WorldObject asteroidTarget = TargetSelector.ChooseTarget(
                    this, asteroidPodDef, true, targetSelectionMode, false, logDiagnostics);
                if (asteroidTarget != null)
                {
                    return new AutomationDecision
                    {
                        Stage = LaunchPortOccupiedByOtherConstruction()
                            ? AutomationDecisionStage.LaunchPortOccupied
                            : AutomationDecisionStage.PlaceBlueprint,
                        Target = asteroidTarget,
                        Asteroid = true,
                        BlueprintMode = AutoRebuildMode.AsteroidMiner
                    };
                }
            }

            ThingDef landPodDef = ConfiguredLandPodDef();
            if (landPodDef != null)
            {
                WorldObject landTarget = TargetSelector.ChooseTarget(
                    this, landPodDef, false, targetSelectionMode, false, logDiagnostics);
                if (landTarget != null)
                {
                    return new AutomationDecision
                    {
                        Stage = LaunchPortOccupiedByOtherConstruction()
                            ? AutomationDecisionStage.LaunchPortOccupied
                            : AutomationDecisionStage.PlaceBlueprint,
                        Target = landTarget,
                        Asteroid = false,
                        BlueprintMode = landPodMode == LandPodMode.Prototype
                            ? AutoRebuildMode.ProtoMiner
                            : AutoRebuildMode.StandardMiner
                    };
                }
            }

            return new AutomationDecision { Stage = AutomationDecisionStage.NoReachableTarget };
        }

        private static string StatusKeyForDecision(AutomationDecision decision)
        {
            if (decision == null)
            {
                return "AMUFA_StatusNoReachableTarget";
            }

            switch (decision.Stage)
            {
                case AutomationDecisionStage.AutomationOff:
                    return "AMUFA_StatusAutomationOff";
                case AutomationDecisionStage.Roofed:
                    return "AMUFA_StatusRoofed";
                case AutomationDecisionStage.BuiltPodWaitingFuel:
                    return "AMUFA_StatusFuelInsufficient";
                case AutomationDecisionStage.LaunchPortOccupied:
                    return "AMUFA_StatusLaunchPortOccupied";
                case AutomationDecisionStage.PendingBlueprint:
                case AutomationDecisionStage.PlaceBlueprint:
                case AutomationDecisionStage.BuiltPodReadyToLaunch:
                    return decision.Asteroid
                        ? "AMUFA_StatusPreparingAsteroid"
                        : "AMUFA_StatusPreparingLand";
                default:
                    return "AMUFA_StatusNoReachableTarget";
            }
        }
        public override IEnumerable<Gizmo> GetGizmos()
        {
            bool automationCommandYielded = false;

            foreach (Gizmo gizmo in base.GetGizmos())
            {
                Command_Action command = gizmo as Command_Action;
                bool originalAutoRebuild = command != null && IsOriginalAutoRebuildCommand(command);
                bool originalManualBuild = command != null && IsOriginalManualBuildCommand(command);
                if (!automationCommandYielded && (originalAutoRebuild || originalManualBuild))
                {
                    automationCommandYielded = true;
                    yield return AutomationCommand();
                }

                if (originalAutoRebuild)
                {
                    if (automationEnabled)
                    {
                        continue;
                    }

                    yield return command;
                    continue;
                }

                if (automationEnabled && originalManualBuild)
                {
                    continue;
                }

                yield return gizmo;
            }

            if (!automationCommandYielded)
            {
                yield return AutomationCommand();
            }

            if (!automationEnabled)
            {
                yield break;
            }

            bool asteroidOption = AsteroidMinerOptionAvailable();
            yield return TargetSelectionCommand();
            yield return LandPodCommand();
            if (asteroidOption)
            {
                yield return AsteroidPodCommand();
                if (asteroidPodsEnabled)
                {
                    yield return VacstoneCommand();
                }
            }
        }

        private AutomationDecision TryAutomate()
        {
            ForceBaseAutoRebuildOff();
            AutomationDecision decision = EvaluateAutomationDecision(
                !podWaitDiagnosticsLogged,
                false);
            ExecuteAutomationDecision(decision);
            if (!automationEnabled || decision.Stage == AutomationDecisionStage.BuiltPodReadyToLaunch)
            {
                return EvaluateAutomationDecision(false, false);
            }

            return decision;
        }

        private void ExecuteAutomationDecision(AutomationDecision decision)
        {
            if (decision == null)
            {
                return;
            }

            if (decision.Pod == null)
            {
                podWaitDiagnosticsLogged = false;
            }

            if (decision.Stage != AutomationDecisionStage.Roofed && decision.Pod == null)
            {
                roofBeforeBuildMessageShown = false;
            }

            switch (decision.Stage)
            {
                case AutomationDecisionStage.Roofed:
                    if (decision.Pod == null)
                    {
                        NotifyRoofBlockedBeforeBuild();
                    }
                    else
                    {
                        NotifyRoofBlockedForBuiltPod(decision.Pod);
                    }
                    break;
                case AutomationDecisionStage.BuiltPodDisabled:
                    ClearStrandedMessageState();
                    break;
                case AutomationDecisionStage.BuiltPodNoReachableTarget:
                    podWaitDiagnosticsLogged = true;
                    NotifyNoReachableTargetForBuiltPod(decision.Pod);
                    break;
                case AutomationDecisionStage.BuiltPodWaitingFuel:
                    ClearStrandedMessageState();
                    podWaitDiagnosticsLogged = true;
                    break;
                case AutomationDecisionStage.BuiltPodReadyToLaunch:
                    ClearStrandedMessageState();
                    podWaitDiagnosticsLogged = false;
                    if (decision.Asteroid && BringVacstoneField != null)
                    {
                        BringVacstoneField.SetValue(decision.Pod, bringVacstone);
                    }

                    Launch(decision.Pod, decision.Target);
                    break;
                case AutomationDecisionStage.LaunchPortOccupied:
                case AutomationDecisionStage.PlaceBlueprint:
                    ExecuteBlueprintDecision(decision, false);
                    break;
            }
        }

        private void ExecuteBlueprintDecision(AutomationDecision decision, bool logPlacement)
        {
            if (decision.Asteroid)
            {
                asteroidStrandedMessageShown = false;
            }

            if (decision.Stage == AutomationDecisionStage.LaunchPortOccupied)
            {
                NotifyLaunchPortOccupied();
                return;
            }

            TryPlaceConfiguredBlueprint(decision.BlueprintMode, logPlacement);
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

        private static bool PodOptionAvailable(string podDefName)
        {
            if (DebugSettings.godMode)
            {
                return true;
            }

            ThingDef podDef = PodDef(podDefName);
            return podDef != null && podDef.IsResearchFinished;
        }

        private static bool AsteroidMinerOptionAvailable()
        {
            if (DebugSettings.godMode)
            {
                return true;
            }

            return PodOptionAvailable(AsteroidPodDefName);
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
                    return PodOptionAvailable(PrototypePodDefName) ? PodDef(PrototypePodDefName) : null;
                case LandPodMode.Standard:
                    return PodOptionAvailable(StandardPodDefName) ? PodDef(StandardPodDefName) : null;
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
                        AutoRebuildMode restoredMode = hasSuspendedAutoRebuildMode
                            ? suspendedAutoRebuildMode
                            : AutoRebuildMode.Off;
                        automationEnabled = false;
                        CancelAllMinerBlueprints();
                        RestoreBaseAutoRebuild();
                        InvalidateAutomationStatus();
                        if (restoredMode != AutoRebuildMode.Off)
                        {
                            TryPlaceConfiguredBlueprint(restoredMode, true);
                        }
                    }
                    else
                    {
                        SuspendBaseAutoRebuild();
                        automationEnabled = true;
                        ReconcileConfiguredBlueprint();
                    }
                }
            };
        }

        private Command_Action LandPodCommand()
        {
            string labelKey;
            string descKey;
            string iconDefName;
            LandPodMode displayedMode = AvailableLandPodMode(landPodMode);
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
                    landPodMode = NextAvailableLandPodMode(displayedMode);
                    ReconcileConfiguredBlueprint();
                }
            };
        }

        private static LandPodMode AvailableLandPodMode(LandPodMode mode)
        {
            switch (mode)
            {
                case LandPodMode.Prototype:
                    return PodOptionAvailable(PrototypePodDefName) ? mode : LandPodMode.Off;
                case LandPodMode.Standard:
                    return PodOptionAvailable(StandardPodDefName) ? mode : LandPodMode.Off;
                default:
                    return LandPodMode.Off;
            }
        }

        private static LandPodMode NextAvailableLandPodMode(LandPodMode mode)
        {
            for (int offset = 1; offset <= 3; offset++)
            {
                LandPodMode candidate = (LandPodMode)(((int)mode + offset) % 3);
                if (candidate == LandPodMode.Off || AvailableLandPodMode(candidate) == candidate)
                {
                    return candidate;
                }
            }

            return LandPodMode.Off;
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
                icon = bringVacstone ? VacstoneEnabledIcon : DisabledIcon(),
                action = delegate { bringVacstone = !bringVacstone; }
            };
        }

        private void ReconcileConfiguredBlueprint()
        {
            InvalidateAutomationStatus();
            CancelAllMinerBlueprints();
            AutomationDecision decision = EvaluateAutomationDecision(false, true);
            if (decision.Stage == AutomationDecisionStage.AutomationOff)
            {
                return;
            }

            if (decision.Pod == null && decision.Stage != AutomationDecisionStage.Roofed)
            {
                roofBeforeBuildMessageShown = false;
                if (decision.Stage == AutomationDecisionStage.PlaceBlueprint ||
                    !LaunchPortOccupiedByOtherConstruction())
                {
                    launchPortOccupiedMessageShown = false;
                }
            }

            if (decision.Stage == AutomationDecisionStage.Roofed)
            {
                ExecuteAutomationDecision(decision);
            }
            else if (decision.Stage == AutomationDecisionStage.LaunchPortOccupied ||
                decision.Stage == AutomationDecisionStage.PlaceBlueprint)
            {
                ExecuteBlueprintDecision(decision, true);
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



