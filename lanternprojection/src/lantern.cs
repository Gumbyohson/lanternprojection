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
        public float PositionChangeThreshold { get; set; } = 0.2f;
        public float RotationChangeThresholdDegrees { get; set; } = 2.0f;
        public int LightDespawnDelayMs { get; set; } = 500;
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
        public bool DespawnScheduled { get; set; } = false;
    }

    public class LanternMod : ModSystem
    {
        private const string DEFAULT_LIGHT_HSV = "7,3,5";
        private const string LIGHT_ENTITY_CODE = "lanternprojection:light";
        private const string LIGHT_DISTANCE_ATTRIBUTE = "lightdistance";
        private const string OWNER_UID_ATTRIBUTE = "ownerUid";
        private const string LIGHT_HSV_ATTRIBUTE = "hsv";
        
        private ICoreAPI api;
        private ICoreServerAPI sapi;
        private LanternConfig config;
        private Dictionary<IPlayer, Entity> playerLights = new Dictionary<IPlayer, Entity>();
        private Dictionary<IPlayer, PlayerLightState> playerStates = new Dictionary<IPlayer, PlayerLightState>();
        private object stateLock = new object();

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

            sapi.Event.PlayerDisconnect += (player) =>
            {
                CleanupPlayerLights(player);
            };

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
            IPlayer[] players = sapi.World.AllOnlinePlayers;

            foreach (IPlayer player in players)
            {
                try
                {
                    IServerPlayer splayer = player as IServerPlayer;
                    if (player.Entity == null || splayer?.ConnectionState != EnumClientState.Playing)
                    {
                        continue;
                    }

                    BlockPos currentPos = player.Entity.Pos.AsBlockPos;
                    double currentYaw = player.Entity.Pos.Yaw;
                    bool hasLightSource = false;
                    byte[] currentLightHsv = null;
                    int currentLightDistance = 0;

                    // Check for light source: offhand -> main hand
                    (hasLightSource, currentLightHsv, currentLightDistance) = GetCurrentLightSource(player, currentPos);

                    lock (stateLock)
                    {
                        if (!playerStates.TryGetValue(player, out PlayerLightState state))
                        {
                            state = new PlayerLightState();
                            state.LastPosition = currentPos;
                            state.LastYaw = currentYaw;
                            playerStates[player] = state;
                        }

                        bool needsUpdate = DetermineNeedsUpdate(state, currentPos, currentYaw, hasLightSource, currentLightHsv, currentLightDistance);

                        if (needsUpdate)
                        {
                            if (hasLightSource)
                            {
                                UpdatePlayerLights(player, currentLightHsv, currentLightDistance);
                                state.DespawnScheduled = false;
                            }
                            else
                            {
                                if (!state.DespawnScheduled)
                                {
                                    ScheduleLightDespawn(player);
                                    state.DespawnScheduled = true;
                                }
                            }

                            state.LastPosition = currentPos;
                            state.LastYaw = currentYaw;
                            state.HadLightSource = hasLightSource;
                            state.LastLightHsv = currentLightHsv?.ToArray();
                            state.LastLightDistance = currentLightDistance;
                        }

                        if (hasLightSource)
                        {
                            ExtendLightLifetime(player);
                        }
                    }
                }
                catch (Exception ex)
                {
                    sapi.Logger.Error($"[LanternProjection] Error in LanternTick for player {player.PlayerName}: {ex.Message}");
                }
            }
        }

        private (bool hasLight, byte[] lightHsv, int lightDistance) GetCurrentLightSource(IPlayer player, BlockPos currentPos)
        {
            byte[] defaultHsv = ParseHsvString(DEFAULT_LIGHT_HSV);

            // Check offhand first
            if (player.Entity.LeftHandItemSlot != null && IsValidLightSource(player.Entity.LeftHandItemSlot))
            {
                ItemSlot slot = player.Entity.LeftHandItemSlot;
                byte[] lightHsv = GetLightHsv(slot, currentPos, defaultHsv);
                int lightDistance = GetLightDistance(slot);
                return (true, lightHsv, lightDistance);
            }

            // Check main hand
            if (player.InventoryManager.ActiveHotbarSlot != null && IsValidLightSource(player.InventoryManager.ActiveHotbarSlot))
            {
                ItemSlot slot = player.InventoryManager.ActiveHotbarSlot;
                byte[] lightHsv = GetLightHsv(slot, currentPos, defaultHsv);
                int lightDistance = GetLightDistance(slot);
                return (true, lightHsv, lightDistance);
            }

            return (false, null, 0);
        }

        private byte[] GetLightHsv(ItemSlot slot, BlockPos pos, byte[] defaultHsv)
        {
            try
            {
                CollectibleObject collectible = slot.Itemstack?.Collectible;
                if (collectible != null)
                {
                    return collectible.GetLightHsv(api.World.BlockAccessor, pos, slot.Itemstack) ?? defaultHsv;
                }
            }
            catch (Exception ex)
            {
                sapi.Logger.Warning($"[LanternProjection] Error getting light HSV: {ex.Message}");
            }
            return defaultHsv;
        }

        private int GetLightDistance(ItemSlot slot)
        {
            try
            {
                if (slot.Itemstack?.Collectible?.Attributes?.KeyExists(LIGHT_DISTANCE_ATTRIBUTE) == true)
                {
                    return slot.Itemstack.Collectible.Attributes[LIGHT_DISTANCE_ATTRIBUTE].AsInt();
                }
            }
            catch (Exception ex)
            {
                sapi.Logger.Warning($"[LanternProjection] Error getting light distance: {ex.Message}");
            }
            return config.FirstLightDistance;
        }

        private byte[] ParseHsvString(string hsvString)
        {
            try
            {
                if (string.IsNullOrEmpty(hsvString))
                {
                    return new byte[] { 7, 3, 5 };
                }

                var parts = hsvString.Split(',');
                if (parts.Length == 3 && byte.TryParse(parts[0], out byte h) && 
                    byte.TryParse(parts[1], out byte s) && byte.TryParse(parts[2], out byte v))
                {
                    return new byte[] { h, s, v };
                }
            }
            catch { }
            return new byte[] { 7, 3, 5 };
        }

        private bool DetermineNeedsUpdate(PlayerLightState state, BlockPos currentPos, double currentYaw, 
            bool hasLightSource, byte[] currentLightHsv, int currentLightDistance)
        {
            if (state.LastPosition == null)
            {
                return true;
            }

            if (state.HadLightSource != hasLightSource)
            {
                return true;
            }

            if (hasLightSource)
            {
                float rotationThresholdRad = config.RotationChangeThresholdDegrees * (float)Math.PI / 180f;
                bool positionChanged = Math.Abs(currentPos.X - state.LastPosition.X) >= config.PositionChangeThreshold || 
                                       Math.Abs(currentPos.Z - state.LastPosition.Z) >= config.PositionChangeThreshold;
                bool rotationChanged = Math.Abs(currentYaw - state.LastYaw) >= rotationThresholdRad;

                if (positionChanged || rotationChanged)
                {
                    return true;
                }
            }

            if (currentLightHsv != null && state.LastLightHsv != null && !currentLightHsv.SequenceEqual(state.LastLightHsv))
            {
                return true;
            }

            if (currentLightDistance != state.LastLightDistance)
            {
                return true;
            }

            return false;
        }

        private void ExtendLightLifetime(IPlayer player)
        {
            if (playerLights.TryGetValue(player, out var light) && light != null && light.Alive)
            {
                light.WatchedAttributes.SetInt("lifetime", int.MaxValue);
            }
        }

        private void ScheduleLightDespawn(IPlayer player)
        {
            if (playerLights.TryGetValue(player, out var light) && light != null && light.Alive)
            {
                light.Die(EnumDespawnReason.Expire, null);
                playerLights.Remove(player);
            }
        }

        private bool IsValidLightSource(ItemSlot slot)
        {
            if (slot?.Itemstack?.Collectible?.Attributes?.KeyExists(LIGHT_DISTANCE_ATTRIBUTE) != true)
            {
                return false;
            }

            string itemCode = slot.Itemstack.Collectible.Code?.Path;
            return itemCode != null && itemCode.Contains("lantern");
        }

        private void CleanupPlayerLights(IPlayer player)
        {
            lock (stateLock)
            {
                if (playerLights.TryGetValue(player, out var light))
                {
                    if (light != null && light.Alive && light.WatchedAttributes.GetString(OWNER_UID_ATTRIBUTE) == player.PlayerUID)
                    {
                        light.Die(EnumDespawnReason.Expire, null);
                    }
                    playerLights.Remove(player);
                }

                if (playerStates.TryGetValue(player, out var state))
                {
                    state.DespawnScheduled = false;
                    playerStates.Remove(player);
                }
            }
        }

        private void UpdatePlayerLights(IPlayer player, byte[] lighthsv, int distance)
        {
            if (!playerLights.TryGetValue(player, out var light) || light == null || !light.Alive)
            {
                SpawnSingleLight(player, lighthsv, distance);
                return;
            }

            try
            {
                EntityPos spawnloc = player.Entity.Pos.AheadCopy(8);
                light.ServerPos.SetPos(spawnloc);
                light.ServerPos.SetYaw(0f);
                light.Pos.SetFrom(spawnloc);
                light.PositionBeforeFalling.Set(spawnloc);
                light.WatchedAttributes.SetBytes(LIGHT_HSV_ATTRIBUTE, lighthsv);
            }
            catch (Exception ex)
            {
                sapi.Logger.Error($"[LanternProjection] Error updating light position: {ex.Message}");
                SpawnSingleLight(player, lighthsv, distance);
            }
        }

        private void SpawnSingleLight(IPlayer player, byte[] lighthsv, int distance)
        {
            try
            {
                EntityPos spawnloc = player.Entity.Pos.AheadCopy(8);

                EntityProperties type = sapi.World.GetEntityType(new AssetLocation(LIGHT_ENTITY_CODE));
                if (type == null)
                {
                    sapi.Logger.Error($"[LanternProjection] Could not find entity type: {LIGHT_ENTITY_CODE}");
                    return;
                }

                Entity light = sapi.ClassRegistry.CreateEntity(type);
                if (light == null)
                {
                    sapi.Logger.Error($"[LanternProjection] Failed to create light entity");
                    return;
                }

                light.ServerPos.SetPos(spawnloc);
                light.ServerPos.SetYaw(0f);
                light.Pos.SetFrom(spawnloc);
                light.PositionBeforeFalling.Set(spawnloc);
                light.WatchedAttributes.SetBytes(LIGHT_HSV_ATTRIBUTE, lighthsv);
                light.WatchedAttributes.SetString(OWNER_UID_ATTRIBUTE, player.PlayerUID);
                sapi.World.SpawnEntity(light);

                playerLights[player] = light;
            }
            catch (Exception ex)
            {
                sapi.Logger.Error($"[LanternProjection] Error spawning light: {ex.Message}");
            }
        }

        public void CleanupAllPlayerLights()
        {
            lock (stateLock)
            {
                foreach (var player in playerLights.Keys.ToList())
                {
                    CleanupPlayerLights(player);
                }
            }
        }

        private void RemoveAllLingeringLights()
        {
            try
            {
                foreach (var entity in sapi.World.LoadedEntities.Values)
                {
                    if (entity?.Code?.Path == "light" && string.IsNullOrEmpty(entity.WatchedAttributes.GetString(OWNER_UID_ATTRIBUTE)))
                    {
                        entity.Die(EnumDespawnReason.Expire, null);
                    }
                }
            }
            catch (Exception ex)
            {
                sapi.Logger.Error($"[LanternProjection] Error removing lingering lights: {ex.Message}");
            }
        }
    }

    public class EntityLight : EntityAgent
    {
        public override void Initialize(EntityProperties properties, ICoreAPI api, long InChunkIndex3d)
        {
            byte[] defaultHsv = new byte[] { 7, 3, 5 };
            this.LightHsv = this.WatchedAttributes.GetBytes("hsv", defaultHsv);
            base.LightHsv = this.LightHsv;
            base.Initialize(properties, api, InChunkIndex3d);
        }
    }
}
