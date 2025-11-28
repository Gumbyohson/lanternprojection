using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Server;
using Vintagestory.API.Config;
using Vintagestory.API.Common;

[assembly: ModInfo("lanternprojection",
    Description = "Lantern projection system with Harmony patches",
    Website = "",
    Authors = new[] { "Gumbyohson" })]

namespace lanternprojection;
public class lanternprojectionCore : ModSystem
{
    public static ILogger Logger { get; private set; }
    public static string ModId { get; private set; }
    public static ICoreAPI Api { get; private set; }
    public static Harmony HarmonyInstance { get; private set; }
    private LanternMod lanternMod;

    public override void StartPre(ICoreAPI api)
    {
        base.StartPre(api);
        Api = api;
        Logger = Mod.Logger;
        ModId = Mod.Info.ModID;
        HarmonyInstance = new Harmony(ModId);
        HarmonyInstance.PatchAll();
        
        Logger.Notification("[lanternprojection] Core system starting pre-init...");
    }
    
    public override void Start(ICoreAPI api)
    {
        base.Start(api);
        
        Logger.Notification("[lanternprojection] Core system starting main init...");
        
        api.RegisterEntity("EntityLight", typeof(EntityLight));
        Logger.Notification("[lanternprojection] Registered EntityLight type");
        
        lanternMod = new LanternMod();
        lanternMod.Start(api);
        
        if (api is ICoreServerAPI sapi)
        {
            lanternMod.StartServerSide(sapi);
            Logger.Notification("[lanternprojection] LanternMod initialized on server side");
        }
        else if (api is ICoreClientAPI capi)
        {
            lanternMod.StartClientSide(capi);
            Logger.Notification("[lanternprojection] LanternMod initialized on client side");
        }
    }
    
    public override void StartServerSide(ICoreServerAPI api)
    {
        base.StartServerSide(api);
        Logger.Notification("[lanternprojection] Core system server side started");
    }
    
    public override void StartClientSide(ICoreClientAPI api)
    {
        base.StartClientSide(api);
        Logger.Notification("[lanternprojection] Core system client side started");
    }
    
    public override void Dispose()
    {
        HarmonyInstance?.UnpatchAll(ModId);
        HarmonyInstance = null;
        Logger = null;
        ModId = null;
        Api = null;
        lanternMod = null;
        base.Dispose();
    }
}
