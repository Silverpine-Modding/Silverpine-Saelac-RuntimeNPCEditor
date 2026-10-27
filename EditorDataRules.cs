#nullable enable

using System;
using System.Collections.Generic;

namespace RuntimeNPCEditor;

// Pure rules shared by the editor and the regression harness.
internal static class EditorDataRules
{
    internal static readonly IReadOnlyList<int> TotalCaps =
        Array.AsReadOnly(new[] { 14, 49, 79, 100 });

    internal static bool IsSavedSelection(int index, int count) =>
        index >= 0 && index < count;

    internal static bool ValidatePlayerValues(
        int level, int dailyCap, int totalCap, out string error)
    {
        if (level < 0 || level > 100 || dailyCap < 0 || dailyCap > 110)
        {
            error = "Level must be 0-100; daily cap must be 0-110.";
            return false;
        }
        // These are the only stages handled by NeuralNPC's cap progression.
        if (totalCap != 14 && totalCap != 49
            && totalCap != 79 && totalCap != 100)
        {
            error = "Total cap must be 14, 49, 79, or 100 (game stages).";
            return false;
        }
        error = "";
        return true;
    }

    internal static string ResolveKnowledgeVariables(
        string value, string playerName, string npcName) =>
        value.Replace("%PLAYER_NAME%", playerName).Replace("%NAME%", npcName);

    internal static bool KnowledgeNamesMatch(
        string first, string second, string playerName, string npcName) =>
        string.Equals(first, second, StringComparison.OrdinalIgnoreCase)
        || string.Equals(
            ResolveKnowledgeVariables(first, playerName, npcName),
            ResolveKnowledgeVariables(second, playerName, npcName),
            StringComparison.OrdinalIgnoreCase);

    internal static bool KnowledgeTextChanged(
        string? previousName, string? previousLore,
        string name, string lore) =>
        previousName != name || previousLore != lore;
}
