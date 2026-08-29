using System;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using TheAutoMiner;
using UnityEngine;
using Verse;

namespace AutoMinerUnitFullyAutomatic
{
    internal sealed class TargetCandidate
    {
        public WorldObject WorldObject;
        public int RawDistance;
        public float EffectiveDistance;
        public float RequiredFuel;
        public int FirstSeenTick;
    }

    internal static class TargetSelector
    {
        private const string AsteroidMiningSiteDefName = "AsteroidMiningSite";
        private const string BasicAsteroidDefName = "AsteroidBasic";

        public static bool IsPotentialTarget(WorldObject worldObject)
        {
            return IsLandTarget(worldObject) || IsAsteroidTarget(worldObject);
        }

        public static bool IsLandTarget(WorldObject worldObject)
        {
            Site site = worldObject as Site;
            return site != null && site.parts != null && site.parts.Any(part =>
                part != null && part.parms.preciousLumpResources != null);
        }

        public static bool IsAsteroidTarget(WorldObject worldObject)
        {
            SpaceMapParent asteroid = worldObject as SpaceMapParent;
            if (asteroid == null || asteroid.preciousResource == null || asteroid.def == null)
            {
                return false;
            }

            string defName = asteroid.def.defName;
            return defName == AsteroidMiningSiteDefName || defName == BasicAsteroidDefName;
        }

        public static bool AnyPotentialTarget(bool asteroid)
        {
            AutomationWorldComponent component = AutomationWorldComponent.Current;
            return component != null && component.Targets(asteroid).Count > 0;
        }

        public static WorldObject ChooseTarget(
            Building_FullyAutomaticMinerLauncher launcher,
            ThingDef podDef,
            bool asteroid,
            TargetSelectionMode selectionMode,
            bool requireCurrentFuel,
            bool logDiagnostics)
        {
            AutomationWorldComponent component = AutomationWorldComponent.Current;
            CompProperties_AutoMinerLaunchable launchProperties =
                podDef == null ? null : podDef.GetCompProperties<CompProperties_AutoMinerLaunchable>();
            CompRefuelable fuel = launcher == null ? null : launcher.GetComp<CompRefuelable>();
            if (component == null || launchProperties == null || fuel == null || launcher.Map == null)
            {
                if (logDiagnostics)
                {
                    Log.Warning("[AMUFA] Target selection unavailable. component=" + (component != null) +
                        ", podDef=" + (podDef == null ? "null" : podDef.defName) +
                        ", launchProperties=" + (launchProperties != null) +
                        ", fuelComp=" + (fuel != null) +
                        ", map=" + (launcher != null && launcher.Map != null));
                }

                return null;
            }

            int total = 0;
            int valid = 0;
            int reserved = 0;
            int unreachable = 0;
            int outsideRange = 0;
            int insufficientFuel = 0;
            float fuelLimit = launcher.AutomationFuelLimit;
            float availableFuel = requireCurrentFuel
                ? Mathf.Min(fuel.Fuel, fuelLimit)
                : fuelLimit;
            TargetCandidate chosen = null;

            foreach (WorldObject target in component.Targets(asteroid))
            {
                total++;
                if (target == null || component.IsTargetReserved(target.Tile))
                {
                    reserved++;
                    continue;
                }

                int rawDistance = Find.WorldGrid.TraversalDistanceBetween(
                    launcher.Map.Tile,
                    target.Tile,
                    true,
                    int.MaxValue,
                    asteroid);
                if (rawDistance < 0)
                {
                    unreachable++;
                    continue;
                }

                PlanetLayerDef layerDef = target.Tile.LayerDef;
                float distanceFactor = layerDef == null ? 1f : layerDef.rangeDistanceFactor;
                float effectiveDistance = rawDistance * distanceFactor;
                if (launchProperties.fixedLaunchDistanceMax > 0 &&
                    effectiveDistance > launchProperties.fixedLaunchDistanceMax)
                {
                    outsideRange++;
                    continue;
                }

                float requiredFuel = Mathf.Max(
                    launchProperties.minFuelCost,
                    effectiveDistance * launchProperties.fuelPerTile);
                if (availableFuel + 0.001f < requiredFuel)
                {
                    insufficientFuel++;
                    continue;
                }

                TargetCandidate candidate = new TargetCandidate
                {
                    WorldObject = target,
                    RawDistance = rawDistance,
                    EffectiveDistance = effectiveDistance,
                    RequiredFuel = requiredFuel,
                    FirstSeenTick = component.FirstSeenTick(target)
                };
                valid++;
                if (IsBetterCandidate(candidate, chosen, selectionMode))
                {
                    chosen = candidate;
                }
            }

            if (logDiagnostics)
            {
                string mode = requireCurrentFuel ? "launch" : "blueprint";
                string summary = "[AMUFA] Target selection (" + mode + ", " +
                    (asteroid ? "asteroid" : "land") + "): total=" + total +
                    ", valid=" + valid +
                    ", reserved=" + reserved +
                    ", unreachable=" + unreachable +
                    ", outsideRange=" + outsideRange +
                    ", insufficientFuel=" + insufficientFuel +
                    ", fuelLimit=" + fuelLimit.ToString("0.##") +
                    ", availableFuel=" + availableFuel.ToString("0.##");
                if (chosen == null)
                {
                    Log.Message(summary + ", chosen=none");
                }
                else
                {
                    PlanetLayerDef chosenLayer = chosen.WorldObject.Tile.LayerDef;
                    float chosenFactor = chosenLayer == null ? 1f : chosenLayer.rangeDistanceFactor;
                    Log.Message(summary + ", chosen=" + chosen.WorldObject.LabelCap +
                        ", tile=" + chosen.WorldObject.Tile +
                        ", rawDistance=" + chosen.RawDistance +
                        ", layerFactor=" + chosenFactor.ToString("0.##") +
                        ", effectiveDistance=" + chosen.EffectiveDistance.ToString("0.##") +
                        ", requiredFuel=" + chosen.RequiredFuel.ToString("0.##"));
                }
            }

            return chosen == null ? null : chosen.WorldObject;
        }

        private static bool IsBetterCandidate(
            TargetCandidate candidate,
            TargetCandidate current,
            TargetSelectionMode selectionMode)
        {
            if (current == null)
            {
                return true;
            }

            int comparison;
            switch (selectionMode)
            {
                case TargetSelectionMode.Nearest:
                    comparison = candidate.EffectiveDistance.CompareTo(current.EffectiveDistance);
                    if (comparison != 0)
                    {
                        return comparison < 0;
                    }
                    break;
                case TargetSelectionMode.Farthest:
                    comparison = candidate.EffectiveDistance.CompareTo(current.EffectiveDistance);
                    if (comparison != 0)
                    {
                        return comparison > 0;
                    }
                    break;
            }

            comparison = candidate.FirstSeenTick.CompareTo(current.FirstSeenTick);
            if (comparison != 0)
            {
                return comparison < 0;
            }

            return candidate.WorldObject.ID < current.WorldObject.ID;
        }

        public static bool IsReserved(PlanetTile targetTile)
        {
            AutomationWorldComponent component = AutomationWorldComponent.Current;
            return component != null && component.IsTargetReserved(targetTile);
        }
    }
}

