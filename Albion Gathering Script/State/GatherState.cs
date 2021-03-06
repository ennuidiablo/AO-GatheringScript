﻿using Ennui.Api;
using Ennui.Api.Direct.Object;
using Ennui.Api.Meta;
using Ennui.Api.Method;
using Ennui.Api.Script;
using System;
using System.Collections.Generic;

namespace Ennui.Script.Official
{
    public class GatherState : StateScript
    {
        private Configuration config;
        private Context context;

        private IHarvestableObject harvestableTarget;
        private IMobObject mobTarget;
        private int gatherAttempts = 0;
        private List<long> blacklist = new List<long>();
        private Random rand = new Random();
        private Vector3<float> mountLoc;

        public GatherState(Configuration config, Context context)
        {
            this.config = config;
            this.context = context;
        }

        private void Reset()
        {
            harvestableTarget = null;
            mobTarget = null;
            gatherAttempts = 0;
        }

        private bool NeedsNew()
        {
            if (harvestableTarget == null && mobTarget == null)
            {
                return true;
            }

            if (mobTarget != null && (!mobTarget.IsValid || mobTarget.CurrentHealth <= 0))
            {
                return true;
            }

            if (harvestableTarget != null && (!harvestableTarget.IsValid || harvestableTarget.Depleted))
            {
                return true;
            }

            if (gatherAttempts >= config.GatherAttemptsTimeout)
            {
                if (harvestableTarget != null)
                {
                    blacklist.Add(harvestableTarget.Id);
                }
                if (mobTarget != null)
                {
                    blacklist.Add(mobTarget.Id);
                }
                return true;
            }

            return false;
        }

        private IMobObject Closest(Vector3<float> center, List<IMobObject> mobs)
        {
            var dist = 0.0f;
            IMobObject closest = null;
            foreach (var m in mobs)
            {
                var cdist = m.Location.SimpleDistance(center);
                if (closest == null || cdist < dist)
                {
                    dist = cdist;
                    closest = m;
                }
            }

            return closest;
        }

        private bool FindResource(Vector3<float> center)
        {
            context.State = "Finding resource...";

            Reset();
            var territoryAreas = new List<Area>();
            var graph = Graphs.LookupByDisplayName(Game.ClusterName);
            foreach (var t in graph.Territories)
            {
                var tCenter = t.Center;
                var tSize = t.Size;
                var tCenter3d = new Vector3f(tCenter.X, 0, tCenter.Y);
                var tBegin = tCenter3d.Translate(0 - (tSize.X / 2), -100, 0 - (tSize.Y / 2));
                var tEnd = tCenter3d.Translate(tSize.X / 2, 100, tSize.Y / 2);
                var tArea = new Area(tBegin, tEnd);
                territoryAreas.Add(tArea);
            }

            if (config.AttackMobs)
            {
                var lpo = Players.LocalPlayer;
                if (lpo != null && config.IgnoreMobsOnLowHealth && lpo.HealthPercentage > config.IgnoreMobHealthPercent)
                {
                    var resourceMobs = new List<IMobObject>();
                    foreach (var ent in Entities.MobChain.ExcludeByArea(territoryAreas.ToArray()).ExcludeWithIds(blacklist.ToArray()).AsList)
                    {
                        var drops = ent.HarvestableDropChain.FilterByTypeSet(SafeTypeSet.BatchConvert(config.TypeSetsToUse)).AsList;
                        Logging.Log(drops.ToString());
                        if (drops.Count > 0)
                        {

                            resourceMobs.Add(ent);
                        }
                    }

                    if (resourceMobs.Count > 0 && !Closest(center, resourceMobs).IsUnderAttack)
                    {
                        mobTarget = Closest(center, resourceMobs);
                    }
                }
            }

            harvestableTarget = Objects
                .HarvestableChain
                .FilterDepleted()
                .ExcludeWithIds(blacklist.ToArray())
                .ExcludeByArea(territoryAreas.ToArray())
                .FilterByTypeSet(SafeTypeSet.BatchConvert(config.TypeSetsToUse))//getTypesByToolsAndConfig())) needs testing//This will get all currently not broken tools as resource collection options and what you have selected for config options
                .FilterWithSetupState(HarvestableSetupState.Invalid)
                .FilterWithSetupState(HarvestableSetupState.Owned)
                .Closest(center);
            
            if (mobTarget != null && harvestableTarget != null)
            {
                var mobDist = mobTarget.Location.SimpleDistance(center);
                var resDist = harvestableTarget.Location.SimpleDistance(center);
                if (mobDist < resDist)
                {
                    harvestableTarget = null;
                }
                else
                {
                    mobTarget = null;
                }
            }

            return harvestableTarget != null || mobTarget != null;
        }
        //Get what i currently have
        private List<SafeTypeSet> getTypesByTools()
        {
            var types = EquipmentUtils.GetCollectionTypesRemaining(Api);
            List<SafeTypeSet> retList = new List<SafeTypeSet>();
            foreach (KeyValuePair<ResourceType, List<int>> entry in types)
                for (int i=0;i< entry.Value.Count; i++)
                    retList.Add(new SafeTypeSet(entry.Value[i] + 1, entry.Value[i] + 1, entry.Key, 0 , i < entry.Value[entry.Value.Count-1] ? 3 : 0));
            return retList;
        }
        //Get what i currently have and what I've selected to farm
        private List<SafeTypeSet> getTypesByToolsAndConfig()
        {
            List<SafeTypeSet> allCurrentToolTypes = getTypesByTools();
            List<SafeTypeSet> retList = new List<SafeTypeSet>();
            foreach (var i in allCurrentToolTypes)
                foreach (var j in config.TypeSetsToUse)
                    if (i.Type == j.Type && i.MinTier == j.MinTier && !retList.Contains(j))
                        retList.Add(j);

            return retList;
        }
        

        private Vector3<float> RandomRoamPoint()
        {
            if (config.RoamPoints.Count == 0)
            {
                return null;
            }
            if (config.RoamPoints.Count == 1)
            {
                return config.RoamPoints[0].RealVector3();
            }
            return config.RoamPoints[rand.Next(config.RoamPoints.Count)].RealVector3();
        }


        private Boolean ShouldUseMount(float heldWeight, float dist)
        {
            var useMount = true;
            
            if (dist >= 15)
            {
                useMount = true;
            }
            else if (heldWeight >= 100 && dist >= 6)
            {
                useMount = true;
            }
            else if (heldWeight >= 120 && dist >= 3)
            {
                useMount = true;
            }
            else if (heldWeight >= 135)
            {
                useMount = true;
            }

            return useMount;
        }

        public override int OnLoop(IScriptEngine se)
        {
            var localPlayer = Players.LocalPlayer;
            var localLocation = localPlayer.Location;

            // <------------ wtf123 start
            if (blacklist.Count > 500)
            {
                blacklist.Clear();
            }
                if (localPlayer.HealthPercentage < 30 || blacklist.Count>500)
            {
                blacklist.Clear();
                var loc = localLocation;
                Logging.Log("Add mobcamp point " + loc.X + " " + loc.Y + " " + loc.Z);
                config.ResourceClusterName = Game.ClusterName;
                config.mobCamps.Add(new SafeVector3(new Vector3f(loc.X, loc.Y, loc.Z)));
            }

            if (localPlayer.IsMounted || mountLoc == null)
            {
                mountLoc = localLocation;
                var loc = mountLoc;
                this.config.mountLoc = new SafeVector3(new Vector3f(loc.X, loc.Y, loc.Z));
            }
            this.config.blackList = blacklist.Count;

            // <------------ wtf123 End
            if (config.RepairDest != null && Api.HasBrokenItems() && (config.skipRepairing == false))
            {
                blacklist.Clear();
                parent.EnterState("repair");
                return 0;
            }

            if (localPlayer == null)
            {
                context.State = "Failed to find local player!";
                Logging.Log("Failed to find local player!", LogLevel.Error);
                return 10_000;
            }

            if (localPlayer.IsUnderAttack)
            {
                Logging.Log("Local player under attack, fight back!", LogLevel.Atom);
                parent.EnterState("combat");
                return 0;
            }

            //if (!config.GatherArea.RealArea(Api).Contains(localLocation))
            if (!config.ResourceArea.RealArea(Api).Contains(localLocation))
            {
                context.State = "Walking to gather area...";
                Logging.Log("Local player not in gather area, walk there!", LogLevel.Atom);

				// MadMonk Extras
				
				context.State = "Walking to Exit waypoint first...";

				var configb = new PointPathFindConfig();
				configb.ClusterName = this.config.RepairClusterName;
				configb.Point = this.config.ExitDest.RealVector3();
				configb.UseWeb = false;
				configb.UseMount = true;
				Movement.PathFindTo(configb);

				if (config.roamPointFirst)
				{
					context.State = "Walking to roam waypoint first...";

					var config = new PointPathFindConfig();
					config.ClusterName = this.config.ResourceClusterName;
					config.Point = RandomRoamPoint();
					config.UseWeb = false;
					config.UseMount = true;
					Movement.PathFindTo(config);
					return 0;
				}

				var moveConfig = new PointPathFindConfig();
                moveConfig.UseWeb = false;
                moveConfig.ClusterName = config.ResourceClusterName;
                if (Movement.PathFindTo(moveConfig) != PathFindResult.Success)
                {
                    Logging.Log("Local player failed to find path to resource area!", LogLevel.Error);
                    return 10_000;
                }

                return 0;
            }
            
			config.currentWeight = localPlayer.TotalHoldWeight;
            var currentWeight = config.currentWeight;
            var maxHoldWeight = config.MaxHoldWeight;
            if (((localPlayer.TotalHoldWeight + 0.0f)/ (config.MaxHoldWeight + 0.0f)) > 1.0f)
            {
                Logging.Log("Local player has too much weight, banking!", LogLevel.Atom);
                blacklist.Clear();
				parent.EnterState("bank");
                return 0;
            }

             

            if (NeedsNew())
            {
                if (!FindResource(localLocation))
                {
                    var point = RandomRoamPoint();
                    if (point == null)
                    {
                        context.State = "Failed to find roam point!";
                        Logging.Log("Cannot roam as roam points were not added!");
                        return 15000;
                    }

                    context.State = "Roaming";
                    var moveConfig = new PointPathFindConfig();
                    moveConfig.UseWeb = false;
                    moveConfig.ClusterName = config.ResourceClusterName;
                    moveConfig.Point = point;
                    moveConfig.UseMount = true;
                    moveConfig.ExitHook = (() =>
                    {
                        var local = Players.LocalPlayer;
                        if (local != null)
                        {
                            return FindResource(local.Location);
                        }
                        return false;
                    });

                    if (Movement.PathFindTo(moveConfig) != PathFindResult.Success)
                    {
                        context.State = "Failed to find path to roaming point...";
                        return 10_000;
                    }
                    return 5000;
                }
            }

            if (harvestableTarget != null)
            {
                if (config.ingnoreMobCampNodes)
                {
                    var territoryAreas = new List<Area>();
                    var graph = Graphs.LookupByDisplayName(Game.ClusterName);
                    foreach (var t in graph.Territories)
                    {
                        var tCenter = t.Center;
                        var tSize = t.Size;
                        var tCenter3d = new Vector3f(tCenter.X, 0, tCenter.Y);
                        var tBegin = tCenter3d.Translate(0 - (tSize.X / 2), -100, 0 - (tSize.Y / 2));
                        var tEnd = tCenter3d.Translate(tSize.X / 2, 100, tSize.Y / 2);
                        var tArea = new Area(tBegin, tEnd);
                        territoryAreas.Add(tArea);
                    }
                    foreach (var ent in Entities.MobChain.ExcludeByArea(territoryAreas.ToArray()).ExcludeWithIds(blacklist.ToArray()).AsList)
                    {
                        var mobDistanceFromNode = harvestableTarget.Location.SimpleDistance(ent.Location);
                        var drops = ent.HarvestableDropChain.FilterByTypeSet(SafeTypeSet.BatchConvert(config.TypeSetsToUse)).AsList;
                        if (mobDistanceFromNode < 40 && drops.Count <= 0)
                        {
                            blacklist.Add(harvestableTarget.Id);
                            Reset();
                            return 0;
                        }
                    }
                }
                 if (config.mobCamps.Count > 0)
                {
                    foreach (var m in config.mobCamps)
                    {
                        var mobCampArea = m.RealVector3().Expand(25, 3, 25);
                        if (mobCampArea.Contains(harvestableTarget.Location))
                        {
                            if (!blacklist.Contains(harvestableTarget.Id))
                            {
                                blacklist.Add(harvestableTarget.Id);
                                Reset();
                                return 0;
                            }
                        }
                    }
                }
                if (harvestableTarget.Type == ResourceType.Fiber)
                {
                    var area = harvestableTarget.Location.Expand(8, 8, 8);
                    if (area.Contains(mountLoc))
                    {
                        if (localPlayer.IsMounted)
                        {
                            localPlayer.ToggleMount(false);
                        }

                        context.State = "Attempting to gather " + harvestableTarget.Id + " (" + gatherAttempts + ")";
                        harvestableTarget.Click();

                        //if (harvestableTarget.RareState == 0 && harvestableTarget.Tier != 3)
                        if (harvestableTarget.RareState == 0 && config.blacklistEnabled)
                        {
                            if (!blacklist.Contains(harvestableTarget.Id))
                                blacklist.Add(harvestableTarget.Id);
                        }

                        Time.SleepUntil(() =>
                        {
                            return localPlayer.IsHarvesting;
                        }, 1000);

                        if (!localPlayer.IsHarvesting)
                        {
                            gatherAttempts = gatherAttempts + 1;
                        }

                        return 100;
                    }
                    else
                    {
                        context.State = "Walking to resource...";

                        var dist = mountLoc.SimpleDistance(harvestableTarget.Location);
                        var config = new PointPathFindConfig();
                        this.config.dist = dist;
                        config.ClusterName = this.config.ResourceClusterName;
                        config.UseWeb = false;
                        config.Point = harvestableTarget.Location;
                        config.UseMount = ShouldUseMount(currentWeight, dist);
                        config.ExitHook = (() =>
                        {
                            var lpo = Players.LocalPlayer;
                            if (lpo == null) return false;

                            if (!lpo.IsMounted && lpo.IsUnderAttack)
                            {
                                parent.EnterState("combat");
                                return true;
                            }

                            if (!harvestableTarget.IsValid || harvestableTarget.Depleted)
                            {
                                return true;
                            }

                            return false;
                        });

                        var result = Movement.PathFindTo(config);
                        if (result == PathFindResult.Failed)
                        {
                            context.State = "Failed to path find to resource!";
                            blacklist.Add(harvestableTarget.Id);
                            Reset();
                        }
                        return 0;
                    }
                }
                else
                {
                    var area = harvestableTarget.InteractBounds;
                    if (area.Contains(localLocation))
                    {
                        if (localPlayer.IsMounted)
                        {
                            localPlayer.ToggleMount(false);
                        }

                        context.State = "Attempting to gather " + harvestableTarget.Id + " (" + gatherAttempts + ")";
                        harvestableTarget.Click();

                        //if (harvestableTarget.RareState == 0 && harvestableTarget.Tier != 3)
                        if (harvestableTarget.RareState == 0 && config.blacklistEnabled)
                        {
                            if (!blacklist.Contains(harvestableTarget.Id))
                                blacklist.Add(harvestableTarget.Id);
                        }


                        Time.SleepUntil(() =>
                        {
                            return localPlayer.IsHarvesting;
                        }, 3000);

                        if (!localPlayer.IsHarvesting)
                        {
                            gatherAttempts = gatherAttempts + 1;
                        }

                        return 100;
                    }
                    else
                    {
                        context.State = "Walking to resource...";

                        var dist = localLocation.SimpleDistance(harvestableTarget.Location);
                        var config = new ResourcePathFindConfig();
                        this.config.dist = dist;
                        config.ClusterName = this.config.ResourceClusterName;
                        config.UseWeb = false;
                        config.Target = harvestableTarget;
                        config.UseMount = ShouldUseMount(currentWeight, dist);
                        config.ExitHook = (() =>
                        {
                            var lpo = Players.LocalPlayer;
                            if (lpo == null) return false;

                            if (!lpo.IsMounted && lpo.IsUnderAttack)
                            {
                                parent.EnterState("combat");
                                return true;
                            }

                            if (!harvestableTarget.IsValid || harvestableTarget.Depleted)
                            {
                                return true;
                            }

                            return false;
                        });

                        var result = Movement.PathFindTo(config);
                        if (result == PathFindResult.Failed)
                        {
                            context.State = "Failed to path find to resource!";
                            blacklist.Add(harvestableTarget.Id);
                            Reset();
                        }
                        return 0;
                    }
                }

            }
            else if (mobTarget != null && !mobTarget.IsUnderAttack)
            {
                if (config.ingnoreMobCampNodes)
                {
                    var territoryAreas = new List<Area>();
                    var graph = Graphs.LookupByDisplayName(Game.ClusterName);
                    foreach (var t in graph.Territories)
                    {
                        var tCenter = t.Center;
                        var tSize = t.Size;
                        var tCenter3d = new Vector3f(tCenter.X, 0, tCenter.Y);
                        var tBegin = tCenter3d.Translate(0 - (tSize.X / 2), -100, 0 - (tSize.Y / 2));
                        var tEnd = tCenter3d.Translate(tSize.X / 2, 100, tSize.Y / 2);
                        var tArea = new Area(tBegin, tEnd);
                        territoryAreas.Add(tArea);
                    }
                    foreach (var ent in Entities.MobChain.ExcludeByArea(territoryAreas.ToArray()).ExcludeWithIds(blacklist.ToArray()).AsList)
                    {
                        if (ent != mobTarget)
                        {
                            var mobDistanceFromNode = mobTarget.Location.SimpleDistance(ent.Location);
                            var drops = ent.HarvestableDropChain.FilterByTypeSet(SafeTypeSet.BatchConvert(config.TypeSetsToUse)).AsList;
                            if (mobDistanceFromNode < 40 && drops.Count <= 0)
                            {
                                blacklist.Add(mobTarget.Id);
                                Reset();
                                return 0;
                            }
                        }
                    }
                }
                if (config.mobCamps.Count > 0)
                {
                    foreach (var m in config.mobCamps)
                    {
                        var mobCampArea = m.RealVector3().Expand(25, 3, 25);
                        if (mobCampArea.Contains(mobTarget.Location))
                        {
                            if (!blacklist.Contains(mobTarget.Id))
                            {
                                blacklist.Add(mobTarget.Id);
                                Reset();
                                return 0;
                            }
                        }
                    }
                }
                if (config.mobCamps.Count > 0)
                {
                    foreach (var m in config.mobCamps)
                    {
                        var mobCampArea = m.RealVector3().Expand(20, 8, 20);
                        if (mobCampArea.Contains(mobTarget.Location))
                        {
                            if (!blacklist.Contains(mobTarget.Id))
                                blacklist.Add(mobTarget.Id);
                        }
                    }
                }
                var mobGatherArea = mobTarget.Location.Expand(8, 8, 8);
                if (mobGatherArea.Contains(localLocation))
                {
                    context.State = "Attempting to kill mob";

                    if (localPlayer.IsMounted)
                    {
                        localPlayer.ToggleMount(false);
                    }

                    localPlayer.SetSelectedObject(mobTarget);
                    localPlayer.AttackSelectedObject();

                    if (localPlayer.HealthPercentage < 1)
                    {
                        var loc = localLocation;
                        Logging.Log("Add mobcamp point " + loc.X + " " + loc.Y + " " + loc.Z);
                        config.ResourceClusterName = Game.ClusterName;
                        config.mobCamps.Add(new SafeVector3(new Vector3f(loc.X, loc.Y, loc.Z)));
                    }

                    if (!mobTarget.IsValid || mobTarget.CurrentHealth <= 0 && config.blacklistEnabled)
                    {
                        blacklist.Add(mobTarget.Id);
                    }
                    var mobDrop = mobTarget.HarvestableDropChain
                           .FilterByTypeSet(SafeTypeSet.BatchConvert(config.TypeSetsToUse))
                           .AsList;
                    if (mobDrop[0].Type == ResourceType.Unknown);

                    //if (mobTarget.RareState == 0 || mobDrop[0].Tier == 2) mobTarget
                    if (mobTarget.RareState == 0 && this.config.blacklistEnabled)
                    {
                        if (!blacklist.Contains(mobTarget.Id))
                            blacklist.Add(mobTarget.Id);
                    }


                    Time.SleepUntil(() =>
                    {
                        return localPlayer.IsUnderAttack;
                    }, 1000);

                    if (localPlayer.IsUnderAttack)
                    {
                        parent.EnterState("combat");
                    }
                    else
                    {
                        gatherAttempts = gatherAttempts + 1;
                    }

                    return 100;
                }
                else
                {
                    context.State = "Walking to mob...";

                    var dist = mountLoc.SimpleDistance(mobTarget.Location);
                    var config = new PointPathFindConfig();
                    //this.config.dist = dist;
                    config.ClusterName = this.config.ResourceClusterName;
                    config.UseWeb = false;
                    config.Point = mobTarget.Location;
                    config.UseMount = ShouldUseMount(currentWeight, dist);
                    config.ExitHook = (() =>
                    {
                        var lpo = Players.LocalPlayer;
                        if (lpo == null) return false;

                        if (!lpo.IsMounted && lpo.IsUnderAttack)
                        {
                            parent.EnterState("combat");
                            return true;
                        }
                        

                        //if (mobTarget.RareState == 0)
                        if (mobTarget.RareState == 0 && this.config.blacklistEnabled)
                        {
                            if (!blacklist.Contains(mobTarget.Id))
                                blacklist.Add(mobTarget.Id);
                        }

                        if (!mobTarget.IsValid || mobTarget.CurrentHealth <= 0)
                        {
                            blacklist.Add(mobTarget.Id);
                            return true;
                        }


                        return false;
                    });

                    if (Movement.PathFindTo(config) == PathFindResult.Failed)
                    {
                        context.State = "Failed to path find to mob!";
                        blacklist.Add(mobTarget.Id);
                        Reset();
                    }
                    return 0;
                }
            }
            return 100;
        }
    }
}
