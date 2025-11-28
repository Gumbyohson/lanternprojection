using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.Client;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;

namespace lanternprojection
{
    public class LanternConfig
    {
        public int UpdateIntervalMs { get; set; } = 50;
        public float LightSpacing { get; set; } = 7.0f;
        public int FirstLightDistance { get; set; } = 12;
    }

    public class PlayerLightState
    {
        public BlockPos LastPosition { get; set; }
        public double LastYaw { get; set; }
        public bool HadLightSource { get; set; }
        public byte[] LastLightHsv { get; set; }
        public int LastLightDistance { get; set; }
        public int FastUpdateCounter { get; set; } = 0;
        public bool IsMoving { get; set; } = false;
    }

    public class LanternMod : ModSystem
    {
        private ICoreAPI api;
        private ICoreServerAPI sapi;
        private LanternConfig config;
        private Dictionary<IPlayer, List<Entity>> playerLights = new Dictionary<IPlayer, List<Entity>>();
        private Dictionary<IPlayer, PlayerLightState> playerStates = new Dictionary<IPlayer, PlayerLightState>();
        private int updateCounter = 0;

        public override void Start(ICoreAPI api)
        {
            this.api = api;
            sapi = api as ICoreServerAPI;

            config = api.LoadModConfig<LanternConfig>("lantern.json") ?? new LanternConfig();
            api.StoreModConfig(config, "lantern.json");
        }

        public override void StartServerSide(ICoreServerAPI sapi)
        {
            this.sapi = sapi;
            sapi.World.RegisterGameTickListener(LanternTick, config.UpdateIntervalMs);

            // Hook into player disconnect event to clean up lights
            sapi.Event.PlayerDisconnect += (player) =>
            {
                CleanupPlayerLights(player);
            };

            // Hook into server initialization to clean up lingering lights immediately
            sapi.Event.SaveGameLoaded += () =>
            {
                CleanupAllPlayerLights();
                RemoveAllLingeringLights();
            };
        }

        public override void StartClientSide(ICoreClientAPI capi)
        {
            base.StartClientSide(capi);
        }

        public void LanternTick(float par)
        {
            updateCounter++;
            IPlayer[] players = sapi.World.AllOnlinePlayers;

            foreach (IPlayer player in players)
            {
                IServerPlayer splayer = player as IServerPlayer;
                if (player.Entity != null && splayer.ConnectionState == EnumClientState.Playing)
                {
                    BlockPos currentPos = player.Entity.Pos.AsBlockPos;
                    double currentYaw = player.Entity.Pos.Yaw;
                    bool hasLightSource = false;
                    byte[] currentLightHsv = null;
                    int currentLightDistance = 0;

                    // Prioritize light sources: offhand -> main hand

                    // Check for light source in the offhand slot first
                    if (player.Entity.LeftHandItemSlot != null && IsValidLightSource(player.Entity.LeftHandItemSlot))
                    {
                        hasLightSource = true;
                        ItemSlot slot = player.Entity.LeftHandItemSlot;
                        CollectibleObject lantern = slot.Itemstack.Collectible;
                        currentLightHsv = lantern.GetLightHsv(api.World.BlockAccessor, currentPos, slot.Itemstack) ?? new byte[] { 7, 3, 5 };
                        currentLightDistance = slot.Itemstack.Collectible.Attributes["lightdistance"].AsInt();
                    }

                    // If no light source is found in the offhand, check the main hand (active hotbar slot)
                    if (!hasLightSource && player.InventoryManager.ActiveHotbarSlot != null && IsValidLightSource(player.InventoryManager.ActiveHotbarSlot))
                    {
                        hasLightSource = true;
                        ItemSlot slot = player.InventoryManager.ActiveHotbarSlot;
                        CollectibleObject lantern = slot.Itemstack.Collectible;
                        currentLightHsv = lantern.GetLightHsv(api.World.BlockAccessor, currentPos, slot.Itemstack) ?? new byte[] { 7, 3, 5 };
                        currentLightDistance = slot.Itemstack.Collectible.Attributes["lightdistance"].AsInt();
                    }

                    if (!playerStates.TryGetValue(player, out PlayerLightState state))
                    {
                        state = new PlayerLightState();
                        state.LastPosition = currentPos;
                        state.LastYaw = currentYaw;
                        playerStates[player] = state;
                    }

                    bool needsUpdate = false;
                    bool isCurrentlyMoving = false;

                    if (state.LastPosition == null)
                    {
                        needsUpdate = true;
                    }
                    else if (state.HadLightSource != hasLightSource)
                    {
                        needsUpdate = true;
                    }
                    else if (hasLightSource)
                    {
                        bool positionChanged = Math.Abs(currentPos.X - state.LastPosition.X) >= 0.2 || Math.Abs(currentPos.Z - state.LastPosition.Z) >= 0.2;
                        bool rotationChanged = Math.Abs(currentYaw - state.LastYaw) >= 2 * Math.PI / 180; // 2 degrees

                        isCurrentlyMoving = positionChanged || rotationChanged;

                        if (isCurrentlyMoving)
                        {
                            needsUpdate = true;
                        }
                    }
                    else if ((currentLightHsv != null && state.LastLightHsv != null && !currentLightHsv.SequenceEqual(state.LastLightHsv)) || currentLightDistance != state.LastLightDistance)
                    {
                        needsUpdate = true;
                    }

                    if (needsUpdate)
                    {
                        if (hasLightSource)
                        {
                            UpdatePlayerLights(player, currentLightHsv, currentLightDistance);
                        }
                        else
                        {
                            CleanupPlayerLights(player);
                        }

                        state.LastPosition = currentPos;
                        state.LastYaw = currentYaw;
                        state.HadLightSource = hasLightSource;
                        state.LastLightHsv = currentLightHsv?.ToArray();
                        state.LastLightDistance = currentLightDistance;
                    }

                    // Prevent cleanup of lights if the lantern is out
                    if (hasLightSource)
                    {
                        if (playerLights.TryGetValue(player, out var activeLights))
                        {
                            foreach (var light in activeLights)
                            {
                                if (light != null && light.Alive)
                                {
                                    light.WatchedAttributes.SetInt("lifetime", int.MaxValue); // Extend lifetime indefinitely
                                }
                            }
                        }
                        continue;
                    }

                    // Shorten light lifetime if the player doesn't have their lantern out
                    if (!hasLightSource && playerLights.TryGetValue(player, out var lights))
                    {
                        foreach (var light in lights)
                        {
                            if (light != null && light.Alive)
                            {
                                sapi.World.RegisterCallback((dt) => light.Die(EnumDespawnReason.Expire, null), 500); // Delay removal slightly
                            }
                        }
                        lights.Clear();
                        playerLights.Remove(player);
                    }
                }
            }
        }

        private bool IsValidLightSource(ItemSlot slot)
        {
            if (slot.Itemstack?.Collectible?.Attributes?.KeyExists("lightdistance") == true)
            {
                string itemCode = slot.Itemstack.Collectible.Code?.Path;
                // Restrict to specific items (e.g., lanterns)
                return itemCode != null && itemCode.Contains("lantern");
            }
            return false;
        }

        private bool IsValidWearableLightSource(ItemSlot slot)
        {
            if (slot.Itemstack?.Collectible?.Attributes?.KeyExists("lightdistance") == true)
            {
                string itemCode = slot.Itemstack.Collectible.Code?.Path;
                // Check for wearable light sources (e.g., backpacks or headlamps)
                return itemCode != null && (itemCode.Contains("backpack") || itemCode.Contains("headlamp"));
            }
            return false;
        }

        private void CleanupPlayerLights(IPlayer player)
        {
            if (playerLights.TryGetValue(player, out var lights))
            {
                foreach (var light in lights)
                {
                    if (light != null && light.Alive && light.WatchedAttributes.GetString("ownerUid") == player.PlayerUID)
                    {
                        light.Die(EnumDespawnReason.Expire, null);
                    }
                }
                lights.Clear();
                playerLights.Remove(player);
            }
        }

        private void UpdatePlayerLights(IPlayer player, byte[] lighthsv, int distance)
        {
            if (!playerLights.TryGetValue(player, out var lights) || lights.Count == 0)
            {
                SpawnSingleLight(player, lighthsv, distance);
                return;
            }

            Entity light = lights[0];
            if (light != null && light.Alive)
            {
                EntityPos spawnloc = player.Entity.Pos.AheadCopy(8);
                light.ServerPos.SetPos(spawnloc);
                light.ServerPos.SetYaw(0f);
                light.Pos.SetFrom(spawnloc);
                light.PositionBeforeFalling.Set(spawnloc);
                light.WatchedAttributes.SetBytes("hsv", lighthsv);
            }
            else
            {
                SpawnSingleLight(player, lighthsv, distance);
            }
        }

        private void SpawnSingleLight(IPlayer player, byte[] lighthsv, int distance)
        {
            var lights = new List<Entity>();

            EntityPos spawnloc = player.Entity.Pos.AheadCopy(8);

            EntityProperties type = sapi.World.GetEntityType(new AssetLocation("lanternprojection:light"));
            Entity light = sapi.ClassRegistry.CreateEntity(type);
            light.ServerPos.SetPos(spawnloc);
            light.ServerPos.SetYaw(0f);
            light.Pos.SetFrom(spawnloc);
            light.PositionBeforeFalling.Set(spawnloc);
            light.WatchedAttributes.SetBytes("hsv", lighthsv);
            light.WatchedAttributes.SetString("ownerUid", player.PlayerUID);
            sapi.World.SpawnEntity(light);
            lights.Add(light);

            playerLights[player] = lights;
        }

        public void CleanupAllPlayerLights()
        {
            foreach (var player in playerLights.Keys.ToList())
            {
                CleanupPlayerLights(player);
            }
        }

        private void RemoveAllLingeringLights()
        {
            foreach (var entity in sapi.World.LoadedEntities.Values)
            {
                if (entity.Code?.Path == "light" && entity.WatchedAttributes.GetString("ownerUid") == null)
                {
                    entity.Die(EnumDespawnReason.Expire, null);
                }
            }
        }
    }

    public class EntityLight : EntityAgent
    {
        public override void Initialize(EntityProperties properties, ICoreAPI api, long InChunkIndex3d)
        {
            this.LightHsv = this.WatchedAttributes.GetBytes("hsv", new byte[] { 7, 3, 5 });
            base.LightHsv = this.WatchedAttributes.GetBytes("hsv", new byte[] { 7, 3, 5 });
            base.Initialize(properties, api, InChunkIndex3d);
        }
    }
}
