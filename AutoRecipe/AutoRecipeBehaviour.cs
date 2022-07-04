using HarmonyLib;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(RaycastInteractable))]
public class AutoRecipeBehaviour : MonoBehaviour, IRaycastable
{
    public static HashSet<AutoRecipeBehaviour> Instances = new HashSet<AutoRecipeBehaviour>();

    public bool IsKill { get; private set; } = false;

    private DisplayTextManager displayText;
    private CookingTable_Recipe_UI recipeUI;

    private static Dictionary<Item_Base, Material> materialsDict = Traverse.Create<CookingTable_Recipe_UI>().Field<Dictionary<Item_Base, Material>>("generatedMaterials").Value;

    private CookingTable cookingPot;
    private CookingTable_Pot asPot => cookingPot as CookingTable_Pot;
    private CookingTable_Juicer asJuicer => cookingPot as CookingTable_Juicer;

    private bool preparationCalculated = false;
    private readonly List<CookingTable_Slot> wrongOrFreeSlots = new List<CookingTable_Slot>();
    private readonly List<Item_Base> unpreparedIngredients = new List<Item_Base>();
    private readonly Dictionary<TextMeshProUGUI, Color> originalTMPLabelColors = new Dictionary<TextMeshProUGUI, Color>();
    private readonly Dictionary<Text, Color> originalLabelColors = new Dictionary<Text, Color>();

    private bool wasRayed = false, wasWasRayed = false;

    public void OnEnable()
    {
        if (this.IsKill)
            return;

        this.recipeUI = this.GetComponent<CookingTable_Recipe_UI>();
        this.SetCookingTable(this.FindClosestCookingTable());
        Instances.Add(this);
    }

    public void OnDisable()
    {
        Instances.Remove(this);
        this.SetCookingTable(null);
        if (this.displayText != null)
            this.displayText.HideDisplayTexts();
    }

    public void Update()
    {
        if (this.IsKill)
            return;

        // Raft's Pickup.RaycastForRayInteractables() is broken
        // It doesn't call OnRayEnter or OnRayExit if you move your view from one RaycastInteractable to another in one frame (easier than you'd think)
        if (this.wasWasRayed && !this.wasRayed)
            this.OnRayExit();
        else
        {
            this.wasWasRayed = this.wasRayed;
            this.wasRayed = false;
        }
    }

    public void OnIsRayed()
    {
        if (this.IsKill)
            return;

        this.wasRayed = true;

        if (this.displayText == null)
        {
            this.displayText = ComponentManager<DisplayTextManager>.Value;
            if (this.displayText == null)
                return;
        }

        if (CanvasHelper.ActiveMenu != MenuType.None || PlayerItemManager.IsBusy || !Helper.LocalPlayerIsWithinDistance(this.transform.position, Player.UseDistance))
        {
            this.displayText.HideDisplayTexts();
            return;
        }

        string recipeName = this.recipeUI.Recipe.Result.settings_Inventory.DisplayName;
        string recipeType = this.recipeUI.Recipe.GetRecipeTypeLabel();
        if (this.cookingPot == null)
        {
            this.displayText.ShowText($"{recipeName}\nNo {recipeType} in proximity", 0, true, 0);
            return;
        }

        FuelNetwork fuelNetwork = this.cookingPot.GetComponentInChildren<FuelNetwork>();
        if (fuelNetwork != null)
        {
            var pot = this.asPot;
            int itemCount = this.cookingPot.localPlayer.Inventory.GetItemCount(pot.Fuel.fuelItem);
            if (!pot.Fuel.HasMaxFuel() && itemCount > 0)
            {
                if (MyInput.GetButtonDown("Interact"))
                {
                    int fuel = Math.Min(itemCount, pot.Fuel.MaxFuel - pot.Fuel.GetFuelCount());
                    Message_Fuel message = new Message_Fuel(Messages.Fuel_Increment, this.cookingPot.localPlayer.Network.NetworkIDManager, this.cookingPot.ObjectIndex, this.cookingPot.localPlayer.steamID, fuel);
                    if (Raft_Network.IsHost)
                    {
                        pot.Fuel.AddFuel(fuel);
                        this.cookingPot.localPlayer.Inventory.RemoveItem(pot.Fuel.fuelItem.UniqueName, fuel);
                        this.cookingPot.localPlayer.Network.RPC(message, Target.Other, EP2PSend.k_EP2PSendReliable, NetworkChannel.Channel_Game);
                    }
                    else
                    {
                        this.cookingPot.localPlayer.Network.SendP2P(this.cookingPot.localPlayer.Network.HostID, message, EP2PSend.k_EP2PSendReliable, NetworkChannel.Channel_Game);
                    }
                }
            }
        }

        if (!this.preparationCalculated)
            this.CalculatePreparation();

        int missingItems = this.originalTMPLabelColors.Count + this.originalLabelColors.Count;
        if (missingItems > 0)
        {
            this.displayText.ShowText($"{recipeName}\nMissing {missingItems} ingredients", 0, true, 0);
            return;
        }

        if (this.wrongOrFreeSlots.Count != this.unpreparedIngredients.Count)
        {
            Debug.Log($"{recipeName}: There are {this.wrongOrFreeSlots.Count} slots that should be replaced, but there is actually {this.unpreparedIngredients.Count} items that needs to be placed");
            this.displayText.ShowText($"{recipeName}\nUnable to auto-prepare, check logs", 0, true, 0);
            return;
        }

        if (this.unpreparedIngredients.Count == 0)
        {
            if (this.cookingPot.CurrentRecipe != null)
                this.displayText.ShowText($"{recipeName}\nPrepared, but {recipeType} is busy", 0, true, 0);
            else if (this.asPot?.Fuel.GetFuelCount() == 0)
                this.displayText.ShowText($"{recipeName}\nNo fuel in {recipeType}", 0, true, 0);
            else if (this.asJuicer?.battery.NormalizedBatteryLeft == 0)
                this.displayText.ShowText($"{recipeName}\nNo power in {recipeType}", 0, true, 0);
            else
            {
                this.displayText.ShowText($"{recipeName}\nStart cooking", MyInput.Keybinds["Interact"].MainKey, 0, 0, true);
                if (MyInput.GetButtonDown("Interact"))
                {
                    Traverse.Create(this.cookingPot).Method("HandleStartCooking").GetValue();
                    this.ClearPreparation();
                }
            }
            return;
        }

        this.displayText.ShowText($"{recipeName}\nPrepare", MyInput.Keybinds["Interact"].MainKey, 0, 0, true);
        if (MyInput.GetButtonDown("Interact"))
        {
            // ToList() because our SetItem()/ClearItem() hooks will trigger
            // OrderBy() because it's fun to have the ingredients shuffled :)
            foreach ((CookingTable_Slot slot, Item_Base item) in this.wrongOrFreeSlots.Zip(this.unpreparedIngredients.OrderBy(_ => Guid.NewGuid()), (slot, item) => (slot, item)).ToList())
            {
                if (slot.HasItem)
                    slot.OnPickupItem(this.cookingPot.localPlayer, slot, slot.CurrentItem);

                if (!Traverse.Create(slot).Field<ItemObjectEnabler>("objectEnabler").Value.DoesAcceptItem(item))
                    Debug.Log($"{recipeName}: The item \"{item.UniqueName}\" can not be placed in cooking pot slot");
                else
                {
                    ItemInstance itemInstance = new ItemInstance(item, 1, item.MaxUses);
                    slot.OnInsertItem(this.cookingPot.localPlayer, slot, itemInstance);
                }
            }
            this.ClearPreparation();
        }
    }

    public void OnRayEnter()
    {
    }

    public void OnRayExit()
    {
        if (this.IsKill)
            return;

        this.wasRayed = this.wasWasRayed = false;

        this.ClearPreparation();
        if (this.displayText != null)
            this.displayText.HideDisplayTexts();
    }

    private CookingTable FindClosestCookingTable()
    {
        float sqrMag = float.MaxValue;
        CookingTable cookingPot = null;
        foreach (Block block in BlockCreator.GetPlacedBlocks())
        {
            CookingTable candidate = block.GetComponent<CookingTable>();
            if (candidate == null || !Traverse.Create(candidate).Field<bool>("hasBeenPlaced").Value)
                continue;

            switch (recipeUI.Recipe.RecipeType)
            {
                case CookingRecipeType.Juicer:
                    if (candidate is CookingTable_Pot)
                        continue;
                    break;
                case CookingRecipeType.CookingPot:
                    if (candidate is CookingTable_Juicer)
                        continue;
                    break;
            }

            float sqrMagToCandidate = (candidate.transform.position - this.transform.position).sqrMagnitude;
            if (sqrMagToCandidate <= Player.UseDistance * Player.UseDistance && sqrMagToCandidate < sqrMag)
            {
                sqrMag = sqrMagToCandidate;
                cookingPot = candidate;
            }
        }

        return cookingPot;
    }

    public void OnCookingTablePlaced(CookingTable newCookingTable)
    {
        if (this.IsKill)
            return;

        switch (recipeUI.Recipe.RecipeType)
        {
            case CookingRecipeType.Juicer:
                if (newCookingTable is CookingTable_Pot)
                    return;
                break;
            case CookingRecipeType.CookingPot:
                if (newCookingTable is CookingTable_Juicer)
                    return;
                break;
        }

        float sqrMag = this.cookingPot == null ? float.MaxValue : (this.cookingPot.transform.position - this.transform.position).sqrMagnitude;
        float newSqrMag = (newCookingTable.transform.position - this.transform.position).sqrMagnitude;

        if (newSqrMag <= Player.UseDistance * Player.UseDistance && newSqrMag < sqrMag)
            this.SetCookingTable(newCookingTable);
    }

    public void OnCookingTableRemoved(CookingTable oldCookingTable)
    {
        if (this.IsKill)
            return;

        if (this.cookingPot == null || oldCookingTable.ObjectIndex != this.cookingPot.ObjectIndex)
            return;

        this.SetCookingTable(null);
    }

    private void SetCookingTable(CookingTable newCookingTable)
    {
        this.cookingPot = newCookingTable;
        this.ClearPreparation();
    }

    private void CalculatePreparation()
    {
        this.ClearPreparation();
        this.preparationCalculated = true;
        this.wrongOrFreeSlots.AddRange(this.cookingPot.Slots);
        Network_Player player = this.cookingPot.localPlayer;
        SO_CookingTable_Recipe recipe = this.recipeUI.Recipe;

        CostCollection costCollection = null;
        for (int i = 0; i < recipe.RecipeCost.Length; i++)
        {
            CostMultiple cost = recipe.RecipeCost[i];

            List<CookingTable_Slot> alreadyPrepared = this.wrongOrFreeSlots.Where(slot => slot.HasItem && cost.items.Any(item => item.UniqueIndex == slot.CurrentItem.UniqueIndex)).Take(cost.amount).ToList();
            foreach (CookingTable_Slot slot in alreadyPrepared)
                this.wrongOrFreeSlots.Remove(slot);

            int amount = alreadyPrepared.Count();
            if (amount == cost.amount)
                continue;

            foreach (Item_Base item in cost.items)
            {
                int toAdd = Math.Min(cost.amount - amount, player.Inventory.GetItemCount(item));
                if (toAdd > 0)
                {
                    amount += toAdd;
                    this.unpreparedIngredients.AddRange(Enumerable.Repeat(item, toAdd));

                    if (amount == cost.amount)
                        break;
                }
            }

            if (amount != cost.amount)
            {
                if(costCollection == null)
                    costCollection = Traverse.Create(this.recipeUI).Field<CostCollection>("costCollection").Value;
                TextMeshProUGUI tmpLabel = Traverse.Create(costCollection.costBoxes[i]).Field<TextMeshProUGUI>("labelTextMeshPro").Value;

                if (tmpLabel != null)
                {
                    this.originalTMPLabelColors[tmpLabel] = tmpLabel.color;
                    tmpLabel.color = Color.red;
                }
                else
                {
                    Text label = Traverse.Create(costCollection.costBoxes[i]).Field<Text>("label").Value;
                    this.originalLabelColors[label] = label.color;
                    label.color = Color.red;
                }
            }
        }
        materialsDict.Remove(this.recipeUI.Recipe.Result);
        this.recipeUI.ShowRecipe(this.recipeUI.Recipe);
    }

    public void OnInventorySlotRefreshed()
    {
        if (this.IsKill)
            return;

        this.ClearPreparation();
    }

    public void OnCookingTableSlotItemChanged(CookingTable_Slot slot)
    {
        if (this.IsKill || this.cookingPot == null || !this.cookingPot.Slots.Contains(slot))
            return;

        this.ClearPreparation();
    }

    private void ClearPreparation()
    {
        this.preparationCalculated = false;

        foreach ((TextMeshProUGUI tmpLabel, Color color) in this.originalTMPLabelColors)
            tmpLabel.color = color;
        this.originalTMPLabelColors.Clear();

        foreach ((Text label, Color color) in this.originalLabelColors)
            label.color = color;
        this.originalLabelColors.Clear();

        this.wrongOrFreeSlots.Clear();
        this.unpreparedIngredients.Clear();
        this.originalTMPLabelColors.Clear();
        this.originalLabelColors.Clear();

        materialsDict.Remove(this.recipeUI.Recipe.Result);
        this.recipeUI.ShowRecipe(this.recipeUI.Recipe);
    }

    public void Kill()
    {
        if (this.IsKill)
            return;

        RaycastInteractable interactable = this.GetComponent<RaycastInteractable>();
        DestroyImmediate(this);
        DestroyImmediate(interactable);
        this.IsKill = true;
    }
}
