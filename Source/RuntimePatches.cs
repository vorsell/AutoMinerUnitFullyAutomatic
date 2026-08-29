using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using TheAutoMiner;
using Verse;

namespace AutoMinerUnitFullyAutomatic
{
    [StaticConstructorOnStartup]
    internal static class RuntimePatches
    {
        private const string HarmonyOwner = "vorsel.autominerunit.fullyautomatic";
        private static readonly FieldInfo FinishTickField =
            AccessTools.Field(typeof(AutoMinerOccupiedSite), "finishTick");

        static RuntimePatches()
        {
            Log.Message("[AMUFA] Loaded Auto Miner Unit - Fully Automatic diagnostic build 2026-08-26.5.");
            Harmony harmony = new Harmony(HarmonyOwner);
            TryPatch(
                harmony,
                AccessTools.Method(typeof(WorldObjectsHolder), "Add", new Type[] { typeof(WorldObject) }),
                null,
                AccessTools.Method(typeof(RuntimePatches), "WorldObjectAddedPostfix"));
            TryPatch(
                harmony,
                AccessTools.Method(typeof(WorldObjectsHolder), "Remove", new Type[] { typeof(WorldObject) }),
                null,
                AccessTools.Method(typeof(RuntimePatches), "WorldObjectRemovedPostfix"));
            TryPatch(
                harmony,
                AccessTools.Method(typeof(TravellingAutoMiner), "TargetLost"),
                AccessTools.Method(typeof(RuntimePatches), "TargetLostPrefix"),
                null);

            TryPatchTranspiler(
                harmony,
                AccessTools.Method(typeof(FlyShipLeavingAutoMiner), "LeaveMap"),
                AccessTools.Method(typeof(RuntimePatches), "LaunchMessageTranspiler"));
            TryPatch(
                harmony,
                AccessTools.PropertyGetter(typeof(CompAutoMinerLaunchable), "CanLaunchNow"),
                null,
                AccessTools.Method(typeof(RuntimePatches), "CanLaunchNowPostfix"));
        }

        private static void TryPatchTranspiler(Harmony harmony, MethodBase original, MethodInfo transpiler)
        {
            if (original == null || transpiler == null)
            {
                Log.Error("[Auto Miner Unit - Fully Automatic] Required transpiler target was not found; the affected feature has been disabled.");
                return;
            }

            try
            {
                harmony.Patch(original, transpiler: new HarmonyMethod(transpiler));
            }
            catch (Exception exception)
            {
                string typeName = original.DeclaringType == null ? "<unknown>" : original.DeclaringType.FullName;
                Log.Error("[Auto Miner Unit - Fully Automatic] Failed to transpile " + typeName + "." + original.Name + ": " + exception);
            }
        }

        private static void TryPatch(Harmony harmony, MethodBase original, MethodInfo prefix, MethodInfo postfix)
        {
            if (original == null)
            {
                Log.Error("[Auto Miner Unit - Fully Automatic] Required patch target was not found; the affected feature has been disabled.");
                return;
            }

            try
            {
                harmony.Patch(
                    original,
                    prefix == null ? null : new HarmonyMethod(prefix),
                    postfix == null ? null : new HarmonyMethod(postfix));
            }
            catch (Exception exception)
            {
                string typeName = original.DeclaringType == null ? "<unknown>" : original.DeclaringType.FullName;
                Log.Error("[Auto Miner Unit - Fully Automatic] Failed to patch " + typeName + "." + original.Name + ": " + exception);
            }
        }

        private static IEnumerable<CodeInstruction> LaunchMessageTranspiler(
            IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo originalMessage = AccessTools.Method(
                typeof(Messages),
                "Message",
                new Type[] { typeof(string), typeof(LookTargets), typeof(MessageTypeDef), typeof(bool) });
            MethodInfo replacement = AccessTools.Method(typeof(RuntimePatches), "ShowLaunchMessageIfEnabled");
            int replaced = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.Calls(originalMessage))
                {
                    instruction.operand = replacement;
                    replaced++;
                }

                yield return instruction;
            }

            if (replaced != 1)
            {
                Log.Error("[AMUFA] Expected one launch notification call but replaced " + replaced + ".");
            }
        }

        private static void ShowLaunchMessageIfEnabled(
            string text,
            LookTargets lookTargets,
            MessageTypeDef messageType,
            bool historical)
        {
            if (!FullyAutomaticMod.ArrivalOnlyMessages)
            {
                Messages.Message(text, lookTargets, messageType, historical);
            }
        }

        private static void CanLaunchNowPostfix(CompAutoMinerLaunchable __instance, ref bool __result)
        {
            if (!__result || __instance == null || __instance.parent == null ||
                __instance.parent.Map == null ||
                !(__instance.GetAdjacentLauncher() is Building_FullyAutomaticMinerLauncher))
            {
                return;
            }

            IntVec3 position = __instance.parent.Position;
            if (__instance.parent.Map.roofGrid.Roofed(position.x, position.z))
            {
                __result = false;
            }
        }

        private static void WorldObjectAddedPostfix(WorldObject __0)
        {
            AutomationWorldComponent component = AutomationWorldComponent.Current;
            if (component != null)
            {
                component.NotifyWorldObjectAdded(__0);
            }
        }

        private static void WorldObjectRemovedPostfix(WorldObject __0)
        {
            AutomationWorldComponent component = AutomationWorldComponent.Current;
            if (component != null)
            {
                component.NotifyWorldObjectRemoved(__0);
            }
        }

        private static bool TargetLostPrefix(TravellingAutoMiner __instance)
        {
            if (__instance == null || __instance.missionData == null || FinishTickField == null || Find.WorldObjects == null)
            {
                return true;
            }

            AutoMinerOccupiedSite occupied = Find.WorldObjects.AllWorldObjects
                .OfType<AutoMinerOccupiedSite>()
                .FirstOrDefault(site => site.Tile == __instance.missionData.destinationTile);
            if (occupied == null)
            {
                return true;
            }

            int currentTick = Find.TickManager == null ? 0 : Find.TickManager.TicksGame;
            int finishTick = (int)FinishTickField.GetValue(occupied);
            if (finishTick <= currentTick)
            {
                return true;
            }

            int remainingTicks = finishTick - currentTick;
            FinishTickField.SetValue(occupied, currentTick + Math.Max(1, remainingTicks / 2));
            Messages.Message("AMUFA_DuplicateAccelerated".Translate(), MessageTypeDefOf.PositiveEvent, false);
            __instance.Destroy();
            return false;
        }
    }
}


