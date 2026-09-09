namespace QueueEverything;

[HarmonyPatch(typeof(OrganEnhancerGUI))]
public static class OrganEnhancerGuiPatches
{
    private const string WhitePrefix = "organ_enhance_w_";
    private const string RedPrefix = "organ_enhance_r_";

    private static readonly Dictionary<List<Item>, List<Item>> PreviewNeedsOverride = new();

    [HarmonyPrefix, HarmonyPatch(nameof(OrganEnhancerGUI.OnCraft))]
    public static bool OnCraft_Prefix(OrganEnhancerGUI __instance, CraftDefinition craft, int amount, ref bool __result,
        WorldGameObject ____craftery_wgo, ref BaseItemCellGUI ____current_item_gui)
    {
        if (!Plugin.ForceMultiCraft.Value || amount <= 1 || craft?.id == null || ____craftery_wgo == null) return true;

        if (!TryGetAxis(craft.id, out var isWhite)) return true;

        var multiInventory = !GlobalCraftControlGUI.is_global_control_active
            ? MainGame.me.player.GetMultiInventoryForInteraction()
            : GUIElements.me.craft.multi_inventory;

        var chain = BuildChain(craft, isWhite, amount, multiInventory, out var stoppedForCap);

        if (chain.Count < amount && !stoppedForCap)
        {
            if (Plugin.DebugEnabled)
            {
                Plugin.WriteLog($"[OrganEnhancer] axis={(isWhite ? "white" : "red")} requested={amount} reachable={chain.Count} - not enough resources for the full request, no craft applied");
            }

            __result = false;
            return false;
        }

        for (var i = 0; i < chain.Count - 1; i++)
        {
            multiInventory.RemoveItems(chain[i].needs, MultiInventory.DestinationType.AllFromFirst);
        }

        var finalDef = chain[chain.Count - 1];
        var craftSucceeded = ____craftery_wgo.components.craft.Craft(finalDef, null, null, finalDef.needs, ignore_crafts_list: true);

        if (Plugin.DebugEnabled)
        {
            var finalValue = isWhite ? finalDef.output[0].GetWhiteSkullsValue() : finalDef.output[0].GetRedSkullsValue();
            var stepsDesc = string.Join("; ", chain.Select(d => $"{d.id}[needs={NeedsToString(d.needs)}]"));
            Plugin.WriteLog($"[OrganEnhancer] axis={(isWhite ? "white" : "red")} requested={amount} applied={chain.Count} finalValue={finalValue} craftSucceeded={craftSucceeded} steps=[{stepsDesc}]");
        }

        ____current_item_gui = null;
        GUIElements.me.craft.Hide(play_hide_sound: false);
        if (GlobalCraftControlGUI.is_global_control_active)
        {
            GUIElements.me.global_craft_control_gui.Open();
        }
        __instance.Hide();

        __result = true;
        return false;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(CraftItemGUI), nameof(CraftItemGUI.Redraw))]
    public static void CraftItemGUI_Redraw_Prefix(CraftItemGUI __instance, int ____amount)
    {
        var craft = __instance.craft_definition;
        if (!Plugin.ForceMultiCraft.Value || ____amount <= 1 || craft?.id == null) return;
        if (craft.needs == null || craft.output == null || craft.output.Count == 0) return;
        if (!TryGetAxis(craft.id, out var isWhite)) return;

        var totals = new Dictionary<string, int>(StringComparer.Ordinal);
        AddNeeds(totals, craft.needs);
        var currentOutput = craft.output[0];

        for (var step = 1; step < ____amount; step++)
        {
            var rawValue = isWhite ? currentOutput.GetWhiteSkullsValue() : currentOutput.GetRedSkullsValue();
            if (rawValue >= OrganEnhancerGUI.MAX_ORGAN_ENHANCING_CUP) break;

            var source = GameBalance.me.GetDataOrNull<CraftDefinition>($"{(isWhite ? WhitePrefix : RedPrefix)}{NextLevelIndex(rawValue)}");
            if (source == null) break;

            AddNeeds(totals, source.needs);
            currentOutput = OrganEnhancerGUI.GetModifiedItemForCraftOutput(currentOutput, isWhite);
        }

        PreviewNeedsOverride[craft.needs] = totals.Select(kv => new Item(kv.Key, kv.Value)).ToList();
    }

    [HarmonyPrefix, HarmonyPatch(typeof(BaseItemCellGUI), nameof(BaseItemCellGUI.DrawIngredients))]
    public static void DrawIngredients_Prefix(ref List<Item> items, ref int amount)
    {
        if (amount <= 1 || items == null) return;
        if (PreviewNeedsOverride.TryGetValue(items, out var corrected))
        {
            items = corrected;
            amount = 1;
        }
    }

    [HarmonyPostfix, HarmonyPriority(Priority.Low)]
    [HarmonyPatch(typeof(CraftDefinition), nameof(CraftDefinition.GetSpendTxt))]
    public static void GetSpendTxt_Postfix(CraftDefinition __instance, WorldGameObject wgo, ref string __result, int multiplier = 1)
    {
        if (!Plugin.ForceMultiCraft.Value || multiplier <= 1 || GlobalCraftControlGUI.is_global_control_active) return;
        if (__instance?.id == null || __instance.output == null || __instance.output.Count == 0) return;
        if (!TryGetAxis(__instance.id, out var isWhite)) return;
        if (string.IsNullOrEmpty(__result)) return;

        const string marker = "(en)[/c]";
        var markerIndex = __result.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0) return;

        var numStart = markerIndex + marker.Length;
        var numEnd = numStart;
        while (numEnd < __result.Length && char.IsDigit(__result[numEnd])) numEnd++;
        if (numEnd == numStart) return;

        var totalEnergy = 0;
        var craftDef = __instance;
        var currentOutput = __instance.output[0];

        for (var step = 0; step < multiplier; step++)
        {
            if (step > 0)
            {
                var rawValue = isWhite ? currentOutput.GetWhiteSkullsValue() : currentOutput.GetRedSkullsValue();
                if (rawValue >= OrganEnhancerGUI.MAX_ORGAN_ENHANCING_CUP) break;

                var source = GameBalance.me.GetDataOrNull<CraftDefinition>($"{(isWhite ? WhitePrefix : RedPrefix)}{NextLevelIndex(rawValue)}");
                if (source == null) break;

                craftDef = source;
                currentOutput = OrganEnhancerGUI.GetModifiedItemForCraftOutput(currentOutput, isWhite);
            }

            var stepEnergy = craftDef.energy is { has_expression: true } ? Mathf.CeilToInt(craftDef.energy.EvaluateFloat(wgo)) : 0;
            totalEnergy += Plugin.ExhaustlessEnabled ? Mathf.CeilToInt(stepEnergy / 2f) : stepEnergy;
        }

        __result = __result.Substring(0, numStart) + totalEnergy.ToString(CultureInfo.InvariantCulture) + __result.Substring(numEnd);
    }

    [HarmonyPrefix, HarmonyPatch(typeof(CraftItemGUI), nameof(CraftItemGUI.OnAmountPlus))]
    public static bool OnAmountPlus_Prefix(CraftItemGUI __instance, int ____amount)
    {
        var craft = __instance.current_craft;
        if (!Plugin.ForceMultiCraft.Value || craft?.id == null) return true;
        if (craft.output == null || craft.output.Count == 0) return true;
        if (!TryGetAxis(craft.id, out var isWhite)) return true;

        return ____amount < ComputeMaxAmount(craft, isWhite);
    }

    private static int ComputeMaxAmount(CraftDefinition craft, bool isWhite)
    {
        var multiInventory = !GlobalCraftControlGUI.is_global_control_active
            ? MainGame.me.player.GetMultiInventoryForInteraction()
            : GUIElements.me.craft.multi_inventory;

        return BuildChain(craft, isWhite, int.MaxValue, multiInventory, out _).Count;
    }

    private static List<CraftDefinition> BuildChain(CraftDefinition craft, bool isWhite, int amount, MultiInventory multiInventory, out bool stoppedForCap)
    {
        var chain = new List<CraftDefinition> { craft };
        var cumulativeNeeds = new Dictionary<string, int>(StringComparer.Ordinal);
        AddNeeds(cumulativeNeeds, craft.needs);
        var currentOutput = craft.output[0];
        stoppedForCap = false;

        for (var step = 1; step < amount; step++)
        {
            var rawValue = isWhite ? currentOutput.GetWhiteSkullsValue() : currentOutput.GetRedSkullsValue();
            if (rawValue >= OrganEnhancerGUI.MAX_ORGAN_ENHANCING_CUP)
            {
                stoppedForCap = true;
                break;
            }

            var stepDef = BuildStepDefinition(isWhite, NextLevelIndex(rawValue), currentOutput);
            if (stepDef == null) break;

            var candidateNeeds = new Dictionary<string, int>(cumulativeNeeds, StringComparer.Ordinal);
            AddNeeds(candidateNeeds, stepDef.needs);
            if (!multiInventory.IsEnoughItems(candidateNeeds.Select(kv => new Item(kv.Key, kv.Value)).ToList())) break;

            chain.Add(stepDef);
            cumulativeNeeds = candidateNeeds;
            currentOutput = stepDef.output[0];
        }

        return chain;
    }

    private static bool TryGetAxis(string craftId, out bool isWhite)
    {
        if (craftId.StartsWith(WhitePrefix, StringComparison.Ordinal)) { isWhite = true; return true; }
        if (craftId.StartsWith(RedPrefix, StringComparison.Ordinal)) { isWhite = false; return true; }
        isWhite = false;
        return false;
    }

    private static int NextLevelIndex(int rawValue)
    {
        if (rawValue < 0) return 1;
        return rawValue >= OrganEnhancerGUI.MAX_ORGAN_ENHANCING_CUP ? OrganEnhancerGUI.MAX_ORGAN_ENHANCING_CUP : rawValue + 1;
    }

    private static void AddNeeds(Dictionary<string, int> totals, List<Item> needs)
    {
        if (needs == null) return;
        foreach (var n in needs)
        {
            totals.TryGetValue(n.id, out var current);
            totals[n.id] = current + n.value;
        }
    }

    private static string NeedsToString(List<Item> needs)
    {
        if (needs == null || needs.Count == 0) return "none";
        return string.Join(",", needs.Select(n => $"{n.id}x{n.value}"));
    }

    private static CraftDefinition BuildStepDefinition(bool isWhite, int level, Item previousOutput)
    {
        var prefix = isWhite ? WhitePrefix : RedPrefix;
        var source = GameBalance.me.GetDataOrNull<CraftDefinition>($"{prefix}{level}");
        if (source == null) return null;

        var output = OrganEnhancerGUI.GetModifiedItemForCraftOutput(previousOutput, isWhite);
        output.min_value = new SmartExpression();
        output.max_value = new SmartExpression();
        output.self_chance = new SmartExpression { default_value = 1f };

        return new CraftDefinition
        {
            id = source.id,
            craft_in = new List<string> { "soul_workbench" },
            sanity = new SmartExpression(),
            disable_multi_craft = source.disable_multi_craft,
            icon = source.icon,
            condition = source.condition,
            needs = source.needs,
            energy = source.energy,
            craft_time = source.craft_time,
            enqueue_type = source.enqueue_type,
            output = new List<Item> { output }
        };
    }
}
