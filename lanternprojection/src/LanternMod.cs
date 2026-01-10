using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.Client;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Datastructures;

namespace lanternprojection
{
    public class LanternConfig
    {
        public int UpdateIntervalMs { get; set; } = 50;
        public int LightDistance { get; set; } = 8;
        public float PositionChangeThreshold { get; set; } = 0.2f;
        public float RotationChangeThresholdDegrees { get; set; } = 2.0f;
        
        // Settings for vanilla lanterns
        public bool EnableVanillaLanterns { get; set; } = true;
        public bool RequirePreciousLining { get; set; } = false;
        
        // Mod support toggles (all off unless you want them)
        public bool EnableABCSReduxBackpacks { get; set; } = false;
        public bool EnableFueledWearableLights { get; set; } = false;
        public bool EnableAdventurersWalkingStick { get; set; } = false;
        
        // Enable for debug logging
        public bool DebugLogging { get; set; } = false;
    }

    public class PlayerLightState
    {
        public BlockPos LastPosition { get; set; }
        public double LastYaw { get; set; }
        public bool HadLightSource { get; set; }
        public byte[] LastLightHsv { get; set; }
        public int TicksSinceLastCheck { get; set; } = 0;
        public string LastCheckedItemCode { get; set; } = "";
    }

    public class LanternMod : ModSystem
    {
        private const string LIGHT_ENTITY_CODE = "lanternprojection:light";
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
            // Load config if possible, but catch issues
            try
            {
                config = api.LoadModConfig<LanternConfig>("lanternprojection.json");
            }
            catch (Exception ex)
            {
                try { api.Logger.Warning("[LanternProjection] Failed to load config file, using defaults: " + ex.Message); } catch { }
                config = null;
            }

            if (config == null)
            {
                config = new LanternConfig();
            }

            try
            {
                api.StoreModConfig(config, "lanternprojection.json");
            }
            catch
            {
                // Ignore store errors
            }
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
            
            sapi.Logger.Notification($"[LanternProjection] Config: Vanilla={config.EnableVanillaLanterns}, PreciousLining={config.RequirePreciousLining}, ABCS={config.EnableABCSReduxBackpacks}, FueledWearable={config.EnableFueledWearableLights}, WalkingStick={config.EnableAdventurersWalkingStick}");
        }

        public override void StartClientSide(ICoreClientAPI capi)
        {
            base.StartClientSide(capi);
        }

        private void DebugLog(string message)
        {
            if (config.DebugLogging && sapi != null)
            {
                sapi.Logger.Debug($"[LanternProjection] {message}");
            }
        }

        public void LanternTick(float dt)
        {
            foreach (IPlayer player in sapi.World.AllOnlinePlayers)
            {
                try
                {
                    IServerPlayer splayer = player as IServerPlayer;
                    if (player.Entity == null || splayer?.ConnectionState != EnumClientState.Playing)
                        continue;

                    BlockPos currentPos = player.Entity.Pos.AsBlockPos;
                    double currentYaw = player.Entity.Pos.Yaw;

                    var (hasLightSource, currentLightHsv) = GetCurrentLightSource(player, currentPos);

                    lock (stateLock)
                    {
                        if (!playerStates.TryGetValue(player, out PlayerLightState state))
                        {
                            state = new PlayerLightState
                            {
                                LastPosition = currentPos,
                                LastYaw = currentYaw
                            };
                            playerStates[player] = state;
                        }

                        // Kill dead lights
                        if (playerLights.TryGetValue(player, out var existingLight))
                        {
                            if (existingLight == null || !existingLight.Alive)
                            {
                                playerLights.Remove(player);
                            }
                        }

                        bool needsUpdate = DetermineNeedsUpdate(state, currentPos, currentYaw, hasLightSource, currentLightHsv);

                        if (needsUpdate)
                        {
                            if (hasLightSource)
                            {
                                UpdatePlayerLight(player, currentLightHsv);
                            }
                            else
                            {
                                DespawnLight(player);
                            }

                            state.LastPosition = currentPos;
                            state.LastYaw = currentYaw;
                            state.HadLightSource = hasLightSource;
                            state.LastLightHsv = currentLightHsv?.ToArray();
                        }

                        if (hasLightSource)
                        {
                            ExtendLightLifetime(player);
                        }
                    }
                }
                catch (Exception ex)
                {
                    sapi.Logger.Error($"[LanternProjection] Error in tick for {player.PlayerName}: {ex.Message}");
                }
            }
        }

        private (bool hasLight, byte[] lightHsv) GetCurrentLightSource(IPlayer player, BlockPos currentPos)
        {
            byte[] defaultHsv = new byte[] { 7, 3, 5 };
            var validLights = new List<(byte[] hsv, byte brightness)>();

            // Check offhand
            if (player.Entity.LeftHandItemSlot != null && IsValidLightSource(player.Entity.LeftHandItemSlot))
            {
                byte[] hsv = GetLightHsv(player.Entity.LeftHandItemSlot, currentPos, defaultHsv);
                validLights.Add((hsv, hsv[2]));
            }

            // Check main hand
            if (player.InventoryManager.ActiveHotbarSlot != null && IsValidLightSource(player.InventoryManager.ActiveHotbarSlot))
            {
                byte[] hsv = GetLightHsv(player.InventoryManager.ActiveHotbarSlot, currentPos, defaultHsv);
                validLights.Add((hsv, hsv[2]));
            }

            // Check worn gear for headlamps and backpacks
            foreach (var inventory in player.InventoryManager.Inventories)
            {
                string invClassName = inventory.Value?.ClassName ?? "";
                if (invClassName.Contains("character") || invClassName.Contains("backpack") || 
                    invClassName.Contains("gear") || invClassName.Contains("wearable"))
                {
                    foreach (var slot in inventory.Value)
                    {
                        if (slot != null && !slot.Empty && IsValidLightSource(slot))
                        {
                            byte[] hsv = GetLightHsv(slot, currentPos, defaultHsv);
                            validLights.Add((hsv, hsv[2]));
                        }
                    }
                }
            }

            if (validLights.Count == 0)
                return (false, null);

            var brightest = validLights.OrderByDescending(l => l.brightness).First();
            return (true, brightest.hsv);
        }

        private byte[] GetLightHsv(ItemSlot slot, BlockPos pos, byte[] defaultHsv)
        {
            try
            {
                var collectible = slot.Itemstack?.Collectible;
                if (collectible == null)
                    return defaultHsv;

                string domain = collectible.Code?.Domain ?? "";
                string itemCode = collectible.Code?.Path ?? "";

                // Special handling for FueledWearableLights - read from item's static Attributes
                if (domain == "fueledwearablelights" || 
                    (itemCode.Contains("wearablelight") && (itemCode.Contains("lamp") || itemCode.Contains("candle"))))
                {
                    // The LightHsv is stored in the item type's Attributes JSON
                    var itemAttrs = collectible.Attributes;
                    if (itemAttrs != null)
                    {
                        // Try to read LightHsv array from attributes
                        var lightHsvAttr = itemAttrs["LightHsv"];
                        if (lightHsvAttr != null && lightHsvAttr.Exists)
                        {
                            int[] lightHsvInt = lightHsvAttr.AsArray<int>();
                            if (lightHsvInt != null && lightHsvInt.Length >= 3)
                            {
                                return new byte[] { (byte)lightHsvInt[0], (byte)lightHsvInt[1], (byte)lightHsvInt[2] };
                            }
                        }

                        // Alternative: try lowercase
                        lightHsvAttr = itemAttrs["lightHsv"];
                        if (lightHsvAttr != null && lightHsvAttr.Exists)
                        {
                            int[] lightHsvInt = lightHsvAttr.AsArray<int>();
                            if (lightHsvInt != null && lightHsvInt.Length >= 3)
                            {
                                return new byte[] { (byte)lightHsvInt[0], (byte)lightHsvInt[1], (byte)lightHsvInt[2] };
                            }
                        }

                        // Try getting it as a nested property inside wearableLightSource or similar
                        string[] nestedPaths = { "wearableLightSource", "lightSource", "light" };
                        foreach (var path in nestedPaths)
                        {
                            var nested = itemAttrs[path];
                            if (nested != null && nested.Exists)
                            {
                                var nestedHsv = nested["LightHsv"] ?? nested["lightHsv"];
                                if (nestedHsv != null && nestedHsv.Exists)
                                {
                                    int[] lightHsvInt = nestedHsv.AsArray<int>();
                                    if (lightHsvInt != null && lightHsvInt.Length >= 3)
                                    {
                                        return new byte[] { (byte)lightHsvInt[0], (byte)lightHsvInt[1], (byte)lightHsvInt[2] };
                                    }
                                }
                            }
                        }
                    }

                    // Fallback: use a reasonable default based on actual FueledWearableLights JSON values
                    if (itemCode.Contains("carbide"))
                    {
                        return new byte[] { 9, 1, 12 };
                    }
                    if (itemCode.Contains("candle"))
                    {
                        return new byte[] { 9, 7, 8 };
                    }
                    if (itemCode.Contains("oil"))
                    {
                        return new byte[] { 9, 6, 6 };
                    }
                    return new byte[] { 9, 5, 10 };
                }

                // Standard path for other items
                return collectible.GetLightHsv(api.World.BlockAccessor, pos, slot.Itemstack) ?? defaultHsv;
            }
            catch (Exception ex)
            {
                DebugLog($"Error getting LightHsv: {ex.Message}");
            }
            return defaultHsv;
        }

        private bool DetermineNeedsUpdate(PlayerLightState state, BlockPos currentPos, double currentYaw, 
            bool hasLightSource, byte[] currentLightHsv)
        {
            // Light state changed (toggled on/off)
            if (state.HadLightSource != hasLightSource)
                return true;

            // HSV changed (brightness/color change)
            if (currentLightHsv != null && state.LastLightHsv != null && !currentLightHsv.SequenceEqual(state.LastLightHsv))
                return true;

            // Periodic update every 10 ticks
            state.TicksSinceLastCheck++;
            if (state.TicksSinceLastCheck >= 10)
            {
                state.TicksSinceLastCheck = 0;
                if (hasLightSource)
                    return true;
            }

            if (state.LastPosition == null)
                return true;

            if (hasLightSource)
            {
                float rotThreshold = config.RotationChangeThresholdDegrees * (float)Math.PI / 180f;
                bool posChanged = Math.Abs(currentPos.X - state.LastPosition.X) >= config.PositionChangeThreshold || 
                                  Math.Abs(currentPos.Z - state.LastPosition.Z) >= config.PositionChangeThreshold;
                bool rotChanged = Math.Abs(currentYaw - state.LastYaw) >= rotThreshold;

                if (posChanged || rotChanged)
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

        private void DespawnLight(IPlayer player)
        {
            if (playerLights.TryGetValue(player, out var light))
            {
                if (light != null && light.Alive)
                {
                    light.Die(EnumDespawnReason.Expire, null);
                }
                playerLights.Remove(player);
            }
        }

        private bool IsValidLightSource(ItemSlot slot)
        {
            if (slot?.Itemstack?.Collectible == null)
                return false;

            string itemCode = slot.Itemstack.Collectible.Code?.Path ?? "";
            string domain = slot.Itemstack.Collectible.Code?.Domain ?? "";

            // Check FueledWearableLights first (has its own toggle state)
            if (config.EnableFueledWearableLights)
            {
                bool isFueled = IsFueledWearableLight(slot, itemCode, domain);
                if (isFueled)
                {
                    return true;
                }
            }

            // For other items, check if they're actually emitting light
            if (!IsCurrentlyEmittingLight(slot))
                return false;

            // Vanilla lanterns
            if (config.EnableVanillaLanterns && IsVanillaLantern(slot, itemCode, domain))
                return true;

            // ABCSRedux backpack lanterns
            if (config.EnableABCSReduxBackpacks && domain == "abcsredux" && itemCode.Contains("lantern"))
                return true;

            // Walking stick lantern
            if (config.EnableAdventurersWalkingStick && domain == "walkingstick" && itemCode.Contains("walkingstick-lantern"))
                return true;

            return false;
        }

        private bool IsCurrentlyEmittingLight(ItemSlot slot)
        {
            try
            {
                byte[] hsv = slot.Itemstack?.Collectible?.GetLightHsv(api.World.BlockAccessor, null, slot.Itemstack);
                return hsv != null && hsv.Length >= 3 && hsv[2] > 0;
            }
            catch
            {
                return false;
            }
        }

        private bool IsVanillaLantern(ItemSlot slot, string itemCode, string domain)
        {
            if (!itemCode.Contains("lantern"))
                return false;

            // Exclude mod lanterns
            if (domain == "abcsredux" || domain == "fueledwearablelights" || domain == "walkingstick")
                return false;

            if (config.RequirePreciousLining)
            {
                var stackAttrs = slot.Itemstack?.Attributes;
                var collectibleAttrs = slot.Itemstack?.Collectible?.Attributes;
                
                string lining = stackAttrs?.GetString("lining", null) ?? collectibleAttrs?["lining"]?.AsString() ?? "";
                string material = collectibleAttrs?["material"]?.AsString() ?? "";

                return material == "electrum" || lining == "gold" || lining == "silver" || lining == "electrum";
            }

            return true;
        }

        private bool IsFueledWearableLight(ItemSlot slot, string itemCode, string domain)
        {
            // Check if this is a wearable light
            bool isWearable = (domain == "fueledwearablelights" && itemCode.Contains("wearablelight")) ||
                              (itemCode.Contains("wearablelight") && (itemCode.Contains("lamp") || itemCode.Contains("candle")));

            if (!isWearable)
                return false;

            // The FueledWearableLights mod stores toggle state in ItemStack.Attributes
            var attrs = slot.Itemstack?.Attributes;
            
            if (attrs == null)
                return false;

            // Try to get the turnedOn attribute
            if (attrs.HasAttribute("turnedOn"))
            {
                return attrs.GetBool("turnedOn", false);
            }

            // Try alternative attribute names
            string[] possibleAttrNames = { "turnedon", "TurnedOn", "isOn", "lit", "active", "state" };
            foreach (var attrName in possibleAttrNames)
            {
                if (attrs.HasAttribute(attrName))
                {
                    bool isOn = attrs.GetBool(attrName, false);
                    if (isOn) return true;
                    
                    int intVal = attrs.GetInt(attrName, 0);
                    if (intVal != 0) return true;
                }
            }

            return false;
        }

        private void CleanupPlayerLights(IPlayer player)
        {
            lock (stateLock)
            {
                DespawnLight(player);
                playerStates.Remove(player);
            }
        }

        private void UpdatePlayerLight(IPlayer player, byte[] lighthsv)
        {
            // If HSV changed significantly, respawn the light to ensure it updates
            if (playerLights.TryGetValue(player, out var existingLight) && existingLight != null && existingLight.Alive)
            {
                byte[] currentHsv = existingLight.WatchedAttributes.GetBytes(LIGHT_HSV_ATTRIBUTE, null);
                if (currentHsv == null || !currentHsv.SequenceEqual(lighthsv))
                {
                    SpawnLight(player, lighthsv);
                    return;
                }
            }

            if (!playerLights.TryGetValue(player, out var light) || light == null || !light.Alive)
            {
                SpawnLight(player, lighthsv);
                return;
            }

            try
            {
                EntityPos pos = player.Entity.Pos.AheadCopy(config.LightDistance);
                light.ServerPos.SetPos(pos);
                light.ServerPos.SetYaw(0f);
                light.Pos.SetFrom(pos);
                light.PositionBeforeFalling.Set(pos);
            }
            catch (Exception ex)
            {
                sapi.Logger.Error($"[LanternProjection] Error updating light: {ex.Message}");
                SpawnLight(player, lighthsv);
            }
        }

        private void SpawnLight(IPlayer player, byte[] lighthsv)
        {
            DespawnLight(player);

            try
            {
                EntityPos pos = player.Entity.Pos.AheadCopy(config.LightDistance);

                EntityProperties type = sapi.World.GetEntityType(new AssetLocation(LIGHT_ENTITY_CODE));
                if (type == null)
                {
                    sapi.Logger.Error($"[LanternProjection] Entity type not found: {LIGHT_ENTITY_CODE}");
                    return;
                }

                Entity light = sapi.ClassRegistry.CreateEntity(type);
                if (light == null)
                {
                    sapi.Logger.Error("[LanternProjection] Failed to create light entity");
                    return;
                }

                light.ServerPos.SetPos(pos);
                light.ServerPos.SetYaw(0f);
                light.Pos.SetFrom(pos);
                light.PositionBeforeFalling.Set(pos);
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

        public override void OnGameTick(float dt)
        {
            base.OnGameTick(dt);
            
            // Update light HSV from watched attributes each tick
            byte[] hsv = this.WatchedAttributes.GetBytes("hsv", null);
            if (hsv != null && hsv.Length >= 3)
            {
                this.LightHsv = hsv;
            }
        }
    }
}
