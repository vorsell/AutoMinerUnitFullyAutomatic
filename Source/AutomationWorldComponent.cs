using System.Collections.Generic;
using System.Linq;
using RimWorld.Planet;
using TheAutoMiner;
using Verse;

namespace AutoMinerUnitFullyAutomatic
{
    public sealed class AutomationWorldComponent : GameComponent
    {
        private const int ReservationCacheInterval = 250;

        private Dictionary<int, int> firstSeenTicks = new Dictionary<int, int>();
        private readonly List<WorldObject> landTargets = new List<WorldObject>();
        private readonly List<WorldObject> asteroidTargets = new List<WorldObject>();
        private readonly HashSet<PlanetTile> reservedTiles = new HashSet<PlanetTile>();
        private bool targetsDirty = true;
        private bool reservationsDirty = true;
        private int reservationsRebuiltTick = -1;

        public AutomationWorldComponent(Game game)
        {
        }

        public static AutomationWorldComponent Current
        {
            get
            {
                return Verse.Current.Game == null
                    ? null
                    : Verse.Current.Game.GetComponent<AutomationWorldComponent>();
            }
        }

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref firstSeenTicks, "targetFirstSeenTicks", LookMode.Value, LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && firstSeenTicks == null)
            {
                firstSeenTicks = new Dictionary<int, int>();
            }
        }

        public override void StartedNewGame()
        {
            targetsDirty = true;
            reservationsDirty = true;
        }

        public override void LoadedGame()
        {
            targetsDirty = true;
            reservationsDirty = true;
        }

        public void NotifyWorldObjectAdded(WorldObject worldObject)
        {
            if (worldObject != null && TargetSelector.IsPotentialTarget(worldObject) &&
                !firstSeenTicks.ContainsKey(worldObject.ID))
            {
                firstSeenTicks[worldObject.ID] = Find.TickManager == null ? 0 : Find.TickManager.TicksGame;
            }

            targetsDirty = true;
            reservationsDirty = true;
        }

        public void NotifyWorldObjectRemoved(WorldObject worldObject)
        {
            targetsDirty = true;
            reservationsDirty = true;
        }

        public void NotifyTargetReserved(PlanetTile tile)
        {
            RebuildReservationsIfNeeded();
            reservedTiles.Add(tile);
        }

        public bool IsTargetReserved(PlanetTile tile)
        {
            RebuildReservationsIfNeeded();
            return reservedTiles.Contains(tile);
        }

        public int FirstSeenTick(WorldObject worldObject)
        {
            if (worldObject == null)
            {
                return int.MaxValue;
            }

            int tick;
            if (!firstSeenTicks.TryGetValue(worldObject.ID, out tick))
            {
                tick = Find.TickManager == null ? 0 : Find.TickManager.TicksGame;
                firstSeenTicks[worldObject.ID] = tick;
            }

            return tick;
        }

        public IList<WorldObject> Targets(bool asteroid)
        {
            RebuildTargetsIfDirty();
            return asteroid ? asteroidTargets : landTargets;
        }

        private void RebuildTargetsIfDirty()
        {
            if (!targetsDirty || Find.WorldObjects == null)
            {
                return;
            }

            targetsDirty = false;
            landTargets.Clear();
            asteroidTargets.Clear();

            foreach (WorldObject worldObject in Find.WorldObjects.AllWorldObjects.ToList())
            {
                if (TargetSelector.IsLandTarget(worldObject))
                {
                    landTargets.Add(worldObject);
                }
                else if (TargetSelector.IsAsteroidTarget(worldObject))
                {
                    asteroidTargets.Add(worldObject);
                }

                if (TargetSelector.IsPotentialTarget(worldObject) && !firstSeenTicks.ContainsKey(worldObject.ID))
                {
                    firstSeenTicks[worldObject.ID] = Find.TickManager == null ? 0 : Find.TickManager.TicksGame;
                }
            }
        }

        private void RebuildReservationsIfNeeded()
        {
            int currentTick = Find.TickManager == null ? 0 : Find.TickManager.TicksGame;
            if (!reservationsDirty && reservationsRebuiltTick >= 0 &&
                currentTick - reservationsRebuiltTick < ReservationCacheInterval)
            {
                return;
            }

            reservationsDirty = false;
            reservationsRebuiltTick = currentTick;
            reservedTiles.Clear();

            if (Find.WorldObjects != null)
            {
                foreach (WorldObject worldObject in Find.WorldObjects.AllWorldObjects)
                {
                    TravellingAutoMiner travelling = worldObject as TravellingAutoMiner;
                    if (travelling != null && travelling.missionData != null)
                    {
                        reservedTiles.Add(travelling.missionData.destinationTile);
                    }

                    AutoMinerOccupiedSite occupied = worldObject as AutoMinerOccupiedSite;
                    if (occupied != null)
                    {
                        reservedTiles.Add(occupied.Tile);
                    }
                }
            }

            if (Find.Maps == null)
            {
                return;
            }

            foreach (Map map in Find.Maps)
            {
                foreach (Thing thing in map.listerThings.AllThings)
                {
                    FlyShipLeavingAutoMiner leaving = thing as FlyShipLeavingAutoMiner;
                    if (leaving != null && leaving.missionData != null)
                    {
                        reservedTiles.Add(leaving.missionData.destinationTile);
                    }
                }
            }
        }
    }
}

