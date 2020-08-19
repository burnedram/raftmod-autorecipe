using HarmonyLib;
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
    private CookingPot_Recipe_UI recipeUI;

    private CookingPot cookingPot;

    private bool preparationCalculated = false;
    private List<CookingPot_Slot> wrongOrFreeSlots = new List<CookingPot_Slot>();
    private List<Item_Base> unpreparedIngredients = new List<Item_Base>();
    private Dictionary<TextMeshProUGUI, Color> originalTMPLabelColors = new Dictionary<TextMeshProUGUI, Color>();
    private Dictionary<Text, Color> originalLabelColors = new Dictionary<Text, Color>();

    private bool wasRayed = false, wasWasRayed = false;

    public void OnEnable()
    {
        if (IsKill)
            return;

        recipeUI = GetComponent<CookingPot_Recipe_UI>();
        SetCookingPot(FindClosestCookingPot());
        Instances.Add(this);
    }

    public void OnDisable()
    {
        Instances.Remove(this);
        SetCookingPot(null);
        if (displayText != null)
            displayText.HideDisplayTexts();
    }

    public void Update()
    {
        if (IsKill)
            return;

        // Raft's Pickup.RaycastForRayInteractables() is broken
        // It doesn't call OnRayEnter or OnRayExit if you move your view from one RaycastInteractable to another in one frame (easier than you'd think)
        if (wasWasRayed && !wasRayed)
            OnRayExit();
        else
        {
            wasWasRayed = wasRayed;
            wasRayed = false;
        }
    }

    public void OnIsRayed()
    {
        if (IsKill)
            return;

        wasRayed = true;

        if (displayText == null)
        {
            displayText = ComponentManager<DisplayTextManager>.Value;
            if (displayText == null)
                return;
        }

        if (CanvasHelper.ActiveMenu != MenuType.None || PlayerItemManager.IsBusy || !Helper.LocalPlayerIsWithinDistance(transform.position, Player.UseDistance))
        {
            displayText.HideDisplayTexts();
            return;
        }

        var recipeName = recipeUI.Recipe.Result.settings_Inventory.DisplayName;

        if (cookingPot == null)
        {
            displayText.ShowText($"{recipeName}\nNo cookingpot in proximity", 0, true, 0);
            return;
        }

        if (!preparationCalculated)
            CalculatePreparation();

        var missingItems = originalTMPLabelColors.Count + originalLabelColors.Count;
        if (missingItems > 0)
        {
            displayText.ShowText($"{recipeName}\nMissing {missingItems} ingredients", 0, true, 0);
            return;
        }

        if (wrongOrFreeSlots.Count != unpreparedIngredients.Count)
        {
            Debug.Log($"There are {wrongOrFreeSlots.Count} slots that should be replaced, but there is actually {unpreparedIngredients.Count} items that needs to be placed");
            displayText.ShowText($"{recipeName}\nUnable to auto-prepare, check logs", 0, true, 0);
            return;
        }

        if (unpreparedIngredients.Count == 0)
        {
            if (cookingPot.CurrentRecipe != null)
                displayText.ShowText($"{recipeName}\nPrepared, but cookingpot is busy", 0, true, 0);
            else if (cookingPot.Fuel.GetFuelCount() == 0)
                displayText.ShowText($"{recipeName}\nNo fuel in cookingpot", 0, true, 0);
            else
            {
                displayText.ShowText($"{recipeName}\nStart cooking", MyInput.Keybinds["Interact"].MainKey, 0, 0, true);
                if (MyInput.GetButtonDown("Interact"))
                {
                    Traverse.Create(cookingPot).Method("HandleStartCooking").GetValue();
                    ClearPreparation();
                }
            }
            return;
        }

        displayText.ShowText($"{recipeName}\nPrepare", MyInput.Keybinds["Interact"].MainKey, 0, 0, true);
        if (MyInput.GetButtonDown("Interact"))
        {
            // ToList() because our SetItem()/ClearItem() hooks will trigger
            // OrderBy() because it's fun to have the ingredients shuffled :)
            foreach ((var slot, var item) in wrongOrFreeSlots.Zip(unpreparedIngredients.OrderBy(_ => Guid.NewGuid()), (slot, item) => (slot, item)).ToList())
            {
                if (slot.HasItem)
                    slot.OnPickupItem(cookingPot.localPlayer, slot, slot.CurrentItem);

                slot.OnInsertItem(cookingPot.localPlayer, slot, item);
            }
            ClearPreparation();
        }
    }

    public void OnRayEnter()
    {
    }

    public void OnRayExit()
    {
        if (IsKill)
            return;

        wasRayed = wasWasRayed = false;

        ClearPreparation();
        if (displayText != null)
            displayText.HideDisplayTexts();
    }

    private CookingPot FindClosestCookingPot()
    {
        float sqrMag = float.MaxValue;
        CookingPot cookingPot = null;
        foreach (var block in BlockCreator.GetPlacedBlocks())
        {
            var candidate = block.GetComponent<CookingPot>();
            if (candidate == null || !Traverse.Create(candidate).Field<bool>("hasBeenPlaced").Value)
                continue;

            var sqrMagToCandidate = (candidate.transform.position - transform.position).sqrMagnitude;
            if (sqrMagToCandidate <= Player.UseDistance * Player.UseDistance && sqrMagToCandidate < sqrMag)
            {
                sqrMag = sqrMagToCandidate;
                cookingPot = candidate;
            }
        }

        return cookingPot;
    }

    public void OnCookingPotPlaced(CookingPot newCookingPot)
    {
        if (IsKill)
            return;

        var sqrMag = cookingPot == null ? float.MaxValue : (cookingPot.transform.position - transform.position).sqrMagnitude;
        var newSqrMag = (newCookingPot.transform.position - transform.position).sqrMagnitude;

        if (newSqrMag <= Player.UseDistance * Player.UseDistance && newSqrMag < sqrMag)
            SetCookingPot(newCookingPot);
    }

    public void OnCookingPotRemoved(CookingPot oldCookingPot)
    {
        if (IsKill)
            return;

        if (cookingPot == null || oldCookingPot.ObjectIndex != cookingPot.ObjectIndex)
            return;

        SetCookingPot(null);
    }

    private void SetCookingPot(CookingPot newCookingPot)
    {
        cookingPot = newCookingPot;
        ClearPreparation();
    }

    private void CalculatePreparation()
    {
        ClearPreparation();
        preparationCalculated = true;
        wrongOrFreeSlots.AddRange(cookingPot.Slots);
        var player = cookingPot.localPlayer;
        var recipe = recipeUI.Recipe;

        for (int i = 0; i < recipe.RecipeCost.Length; i++)
        {
            var cost = recipe.RecipeCost[i];

            var alreadyPrepared = wrongOrFreeSlots.Where(slot => slot.HasItem && cost.items.Any(item => item.UniqueIndex == slot.CurrentItem.UniqueIndex)).Take(cost.amount).ToList();
            foreach (var slot in alreadyPrepared)
                wrongOrFreeSlots.Remove(slot);

            var amount = alreadyPrepared.Count();
            if (amount == cost.amount)
                continue;

            foreach (var item in cost.items)
            {
                int toAdd = Math.Min(cost.amount - amount, player.Inventory.GetItemCount(item));
                if (toAdd > 0)
                {
                    amount += toAdd;
                    unpreparedIngredients.AddRange(Enumerable.Repeat(item, toAdd));

                    if (amount == cost.amount)
                        break;
                }
            }

            if (amount != cost.amount)
            {
                var costCollection = Traverse.Create(recipeUI).Field<CostCollection>("costCollection").Value;
                var tmpLabel = Traverse.Create(costCollection.costBoxes[i]).Field<TextMeshProUGUI>("labelTextMeshPro").Value;

                if (tmpLabel != null)
                {
                    originalTMPLabelColors[tmpLabel] = tmpLabel.color;
                    tmpLabel.color = Color.red;
                }
                else
                {
                    var label = Traverse.Create(costCollection.costBoxes[i]).Field<Text>("label").Value;
                    originalLabelColors[label] = label.color;
                    label.color = Color.red;
                }
            }
        }
    }

    public void OnInventorySlotRefreshed()
    {
        if (IsKill)
            return;

        ClearPreparation();
    }

    public void OnCookingPotSlotItemChanged(CookingPot_Slot slot)
    {
        if (IsKill || cookingPot == null || !cookingPot.Slots.Contains(slot))
            return;

        ClearPreparation();
    }

    private void ClearPreparation()
    {
        preparationCalculated = false;

        foreach ((var tmpLabel, var color) in originalTMPLabelColors)
            tmpLabel.color = color;
        originalTMPLabelColors.Clear();

        foreach ((var label, var color) in originalLabelColors)
            label.color = color;
        originalLabelColors.Clear();

        wrongOrFreeSlots.Clear();
        unpreparedIngredients.Clear();
        originalTMPLabelColors.Clear();
        originalLabelColors.Clear();
    }

    public void Kill()
    {
        if (IsKill)
            return;

        var interactable = GetComponent<RaycastInteractable>();
        DestroyImmediate(this);
        DestroyImmediate(interactable);
        IsKill = true;
    }
}
