using HarmonyLib;
using System.Linq;
using System.Reflection;

public class AutoRecipe : Mod
{
    public Harmony harmony;

    public void Start()
    {
        this.harmony = new Harmony("me.fistme.raft.autorecipe");
        this.harmony.PatchAll(Assembly.GetExecutingAssembly());

        // We use FindObjectsOfType() here in the case of mod unloading->loading, as Unity might not actually detach our scripts even if Destroy()ed
        foreach (AutoRecipeBehaviour autoRecipe in FindObjectsOfType<AutoRecipeBehaviour>())
            autoRecipe.Kill();

        foreach (CookingTable_Recipe_UI comp in FindObjectsOfType<CookingTable_Recipe_UI>())
        {
            if (BlockCreator.GetPlacedBlocks().Contains(comp.GetComponent<Block>()))
                comp.gameObject.AddComponent<AutoRecipeBehaviour>();
        }
    }

    public void OnModUnload()
    {
        this.harmony.UnpatchAll();
        Destroy(this.gameObject);

        // ToList() because Kill() will Destroy() the script, which in turn calls OnDisable(), which modifies AutoRecipeBehaviour.Instances
        foreach (AutoRecipeBehaviour recipe in AutoRecipeBehaviour.Instances.ToList())
            recipe.Kill();
    }



    [HarmonyPatch]
    public class AutoRecipePatches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Block), "OnFinishedPlacement")]
        public static void Block_OnFinishedPlacement_Postfix(Block __instance)
        {
            if (__instance.GetComponent<CookingTable_Recipe_UI>() != null && __instance.GetComponents<AutoRecipeBehaviour>().All(ar => ar.IsKill))
                __instance.gameObject.AddComponent<AutoRecipeBehaviour>();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CookingTable), "OnBlockPlaced")]
        public static void CookingTable_OnBlockPlaced_Postfix(CookingTable __instance)
        {
                foreach (AutoRecipeBehaviour autoRecipe in AutoRecipeBehaviour.Instances)
                    autoRecipe.OnCookingTablePlaced(__instance);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CookingTable), "OnDestroy")]
        public static void CookingTable_OnDestroy_Postfix(CookingTable __instance)
        {
            if (!Traverse.Create(__instance).Field<bool>("hasBeenPlaced").Value)
                return;
                foreach (AutoRecipeBehaviour autoRecipe in AutoRecipeBehaviour.Instances)
                    autoRecipe.OnCookingTableRemoved(__instance);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Slot), "RefreshComponents")]
        public static void Slot_RefreshComponents_Postfix()
        {
            foreach (AutoRecipeBehaviour autoRecipe in AutoRecipeBehaviour.Instances)
                autoRecipe.OnInventorySlotRefreshed();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CookingTable_Slot), "ClearItem")]
        public static void CookingTable_Slot_ClearItem_Postfix(CookingTable_Slot __instance)
        {
            foreach (AutoRecipeBehaviour autoRecipe in AutoRecipeBehaviour.Instances)
                autoRecipe.OnCookingTableSlotItemChanged(__instance);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CookingTable_Slot), "InsertItem")]
        public static void CookingTable_Slot_InsertItem_Postfix(CookingTable_Slot __instance)
        {
                foreach (AutoRecipeBehaviour autoRecipe in AutoRecipeBehaviour.Instances)
                    autoRecipe.OnCookingTableSlotItemChanged(__instance);
        }
    }
}