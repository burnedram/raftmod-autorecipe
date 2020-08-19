using HarmonyLib;
using System.Linq;
using System.Reflection;

public class AutoRecipe : Mod
{
    public Harmony harmony;

    public void Start()
    {
        harmony = new Harmony("me.fistme.raft.autorecipe");
        harmony.PatchAll(Assembly.GetExecutingAssembly());

        // We use FindObjectsOfType() here in the case of mod unloading->loading, as Unity might not actually detach our scripts even if Destroy()ed
        foreach (var autoRecipe in FindObjectsOfType<AutoRecipeBehaviour>())
            autoRecipe.Kill();

        foreach (var comp in FindObjectsOfType<CookingPot_Recipe_UI>())
        {
            if (BlockCreator.GetPlacedBlocks().Contains(comp.GetComponent<Block>()))
                comp.gameObject.AddComponent<AutoRecipeBehaviour>();
        }
    }

    public void OnModUnload()
    {
        harmony.UnpatchAll();
        Destroy(gameObject);

        // ToList() because Kill() will Destroy() the script, which in turn calls OnDisable(), which modifies AutoRecipeBehaviour.Instances
        foreach (var recipe in AutoRecipeBehaviour.Instances.ToList())
            recipe.Kill();
    }



    [HarmonyPatch]
    public class AutoRecipePatches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Block), "OnFinishedPlacement")]
        public static void Block_OnFinishedPlacement_Postfix(Block __instance)
        {
            if (__instance.GetComponent<CookingPot_Recipe_UI>() != null && __instance.GetComponents<AutoRecipeBehaviour>().All(ar => ar.IsKill))
                __instance.gameObject.AddComponent<AutoRecipeBehaviour>();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CookingPot), "OnBlockPlaced")]
        public static void CookingPot_OnBlockPlaced_Postfix(CookingPot __instance)
        {
            foreach (var autoRecipe in AutoRecipeBehaviour.Instances)
                autoRecipe.OnCookingPotPlaced(__instance);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CookingPot), "OnDestroy")]
        public static void CookingPot_OnDestroy_Postfix(CookingPot __instance)
        {
            if (!Traverse.Create(__instance).Field<bool>("hasBeenPlaced").Value)
                return;

            foreach (var autoRecipe in AutoRecipeBehaviour.Instances)
                autoRecipe.OnCookingPotRemoved(__instance);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Slot), "RefreshComponents")]
        public static void Slot_RefreshComponents_Postfix()
        {
            foreach (var autoRecipe in AutoRecipeBehaviour.Instances)
                autoRecipe.OnInventorySlotRefreshed();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CookingPot_Slot), "ClearItem")]
        public static void CookingPot_Slot_ClearItem_Postfix(CookingPot_Slot __instance)
        {
            foreach (var autoRecipe in AutoRecipeBehaviour.Instances)
                autoRecipe.OnCookingPotSlotItemChanged(__instance);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CookingPot_Slot), "InsertItem")]
        public static void CookingPot_Slot_InsertItem_Postfix(CookingPot_Slot __instance)
        {
            foreach (var autoRecipe in AutoRecipeBehaviour.Instances)
                autoRecipe.OnCookingPotSlotItemChanged(__instance);
        }
    }
}