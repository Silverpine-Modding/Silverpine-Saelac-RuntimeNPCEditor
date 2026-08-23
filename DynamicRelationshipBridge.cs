#nullable enable

using HarmonyLib;
using System;
using System.Reflection;

namespace RuntimeNPCEditor;

/// <summary>
/// Optional, reflection-only bridge to Dynamic NPC Relationships. Runtime NPC
/// Editor does not take an assembly dependency on the dynamic plugin.
/// </summary>
internal static class DynamicRelationshipBridge
{
    private const string PluginTypeName =
        "DynamicNPCRelationships.Plugin, DynamicNPCRelationships";

    internal static bool IsAvailable
    {
        get
        {
            MethodInfo? method = FindMethod("IsRuntimeAvailable");
            if (method == null)
                return false;
            try
            {
                return method.Invoke(null, null) is bool available
                    && available;
            }
            catch
            {
                return false;
            }
        }
    }

    internal static bool IsGloballyEnabled => InvokeBooleanMethod(
        "IsDynamicSystemGloballyEnabled", fallback: true);

    internal static bool HasCurrentSaveData => InvokeBooleanMethod(
        "HasDynamicSystemSaveDataForCurrentSave", fallback: true);

    internal static bool IsEnabledForCurrentSave => InvokeBooleanMethod(
        "IsDynamicSystemEnabledForCurrentSave", fallback: true);

    internal static bool CanToggleCurrentSave => FindMethod(
        "SetDynamicSystemEnabledForCurrentSave") != null;

    internal static bool SetEnabledForCurrentSave(bool enabled)
    {
        MethodInfo? method = FindMethod(
            "SetDynamicSystemEnabledForCurrentSave");
        if (method == null)
            return false;
        try
        {
            return method.Invoke(null, new object?[] { enabled })
                is bool changed && changed;
        }
        catch (Exception exception)
        {
            Plugin.Log.LogWarning(
                "Dynamic relationship save-wide toggle failed: "
                + exception);
            return false;
        }
    }

    internal static bool CanToggleCrossFactionRumors => FindMethod(
        "SetCrossFactionRumorBypassEnabled") != null;

    internal static bool CrossFactionRumorsEnabled => InvokeBooleanMethod(
        "IsCrossFactionRumorBypassEnabled", fallback: false);

    internal static bool SetCrossFactionRumorsEnabled(bool enabled)
    {
        MethodInfo? method = FindMethod(
            "SetCrossFactionRumorBypassEnabled");
        if (method == null)
            return false;
        try
        {
            return method.Invoke(null, new object?[] { enabled })
                is bool changed && changed;
        }
        catch (Exception exception)
        {
            Plugin.Log.LogWarning(
                "Cross-faction rumor toggle failed: " + exception);
            return false;
        }
    }

    private static Type? GetPluginType() =>
        Type.GetType(PluginTypeName, throwOnError: false)
        ?? AccessTools.TypeByName("DynamicNPCRelationships.Plugin");

    private static MethodInfo? FindMethod(string name) =>
        GetPluginType()?.GetMethod(
            name, BindingFlags.Public | BindingFlags.Static);

    private static bool InvokeBooleanMethod(string name, bool fallback)
    {
        MethodInfo? method = FindMethod(name);
        if (method == null)
            return fallback;
        try
        {
            return method.Invoke(null, null) is bool result
                ? result
                : fallback;
        }
        catch (Exception exception)
        {
            Plugin.Log.LogWarning(
                $"Dynamic relationship bridge call {name} failed: "
                + exception);
            return fallback;
        }
    }

    internal static DynamicRelationshipSnapshot? TryGetSnapshot(
        NeuralNPC first,
        NeuralNPC second)
    {
        MethodInfo? method = FindMethod("TryGetRelationshipInfo");
        if (method == null)
            return null;

        object?[] arguments =
        {
            first, second,
            0, "", 0, false, 0,
            0, 0, 0, 0, 0, 0, 0, 0, false, false, false
        };
        try
        {
            if (!(method.Invoke(null, arguments) is bool found) || !found)
                return null;
            bool hasEnemiesTier = TryGetConfiguredEnemyThreshold(
                out int enemyThreshold);
            DynamicRelationshipSnapshot snapshot = new()
            {
                Score = (int)(arguments[2] ?? 0),
                Stage = arguments[3] as string ?? "",
                NextReviewScore = (int)(arguments[4] ?? 0),
                Suspended = (bool)(arguments[5] ?? false),
                InteractionCount = (int)(arguments[6] ?? 0),
                StrangerThreshold = (int)(arguments[7] ?? 0),
                AcquaintanceThreshold = (int)(arguments[8] ?? 0),
                FriendPromotionThreshold = (int)(arguments[9] ?? 0),
                LoverPromotionThreshold = (int)(arguments[10] ?? 0),
                MarriagePromotionThreshold = (int)(arguments[11] ?? 0),
                FriendRetentionThreshold = (int)(arguments[12] ?? 0),
                LoverRetentionThreshold = (int)(arguments[13] ?? 0),
                MarriageRetentionThreshold = (int)(arguments[14] ?? 0),
                LoversEnabled = (bool)(arguments[15] ?? false),
                MarriageEnabled = (bool)(arguments[16] ?? false),
                PromotionAwaitingConversation =
                    (bool)(arguments[17] ?? false),
                HasEnemiesTier = hasEnemiesTier,
                EnemyThreshold = enemyThreshold,
                MinimumScore = GetConfiguredMinimumScore(),
                MaximumScore = GetConfiguredMaximumScore()
            };
            PopulatePromotionLockInfo(first, second, snapshot);
            PopulateTierLockInfo(first, second, snapshot);
            return snapshot;
        }
        catch (Exception exception)
        {
            Plugin.Log.LogWarning(
                "Dynamic relationship status lookup failed: " + exception);
            return null;
        }
    }

    private static void PopulatePromotionLockInfo(
        NeuralNPC first,
        NeuralNPC second,
        DynamicRelationshipSnapshot snapshot)
    {
        MethodInfo? method = FindMethod("TryGetPromotionLockInfo");
        if (method == null)
            return;
        object?[] arguments = { first, second, false, "", "", 2 };
        try
        {
            if (!(method.Invoke(null, arguments) is bool found) || !found)
                return;
            snapshot.PromotionCeilingLocked =
                (bool)(arguments[2] ?? false);
            snapshot.PromotionCeilingStage = arguments[3] as string ?? "";
            snapshot.PromotionLockReason = arguments[4] as string ?? "";
            snapshot.RomanticRejectionThreshold =
                (int)(arguments[5] ?? 2);
        }
        catch (Exception exception)
        {
            Plugin.Log.LogWarning(
                "Dynamic relationship promotion-lock lookup failed: "
                + exception);
        }
    }

    private static void PopulateTierLockInfo(
        NeuralNPC first,
        NeuralNPC second,
        DynamicRelationshipSnapshot snapshot)
    {
        MethodInfo? method = FindMethod("TryGetTierLockInfo");
        if (method == null)
            return;
        object?[] arguments = { first, second, false };
        try
        {
            if (method.Invoke(null, arguments) is bool found && found)
                snapshot.TierLocked = (bool)(arguments[2] ?? false);
        }
        catch (Exception exception)
        {
            Plugin.Log.LogWarning(
                "Dynamic relationship tier-lock lookup failed: "
                + exception);
        }
    }

    private static int GetConfiguredMaximumScore()
    {
        MethodInfo? method = FindMethod("GetConfiguredMaximumScore");
        if (method == null)
            return 150;
        try
        {
            return method.Invoke(null, null) is int value ? value : 150;
        }
        catch
        {
            return 150;
        }
    }

    private static int GetConfiguredMinimumScore()
    {
        MethodInfo? method = FindMethod("GetConfiguredMinimumScore");
        if (method == null)
            return -100;
        try
        {
            return method.Invoke(null, null) is int value ? value : -100;
        }
        catch
        {
            return -100;
        }
    }

    private static bool TryGetConfiguredEnemyThreshold(out int threshold)
    {
        threshold = -50;
        MethodInfo? method = FindMethod("GetConfiguredEnemyThreshold");
        if (method == null)
            return false;
        try
        {
            if (!(method.Invoke(null, null) is int value))
                return false;
            threshold = value;
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static bool NotifyExternalOverride(
        NeuralNPC first,
        NeuralNPC second) =>
        InvokePairMethod(
            "NotifyExternalRelationshipOverride", first, second);

    internal static bool CanBegin(
        NeuralNPC first,
        NeuralNPC second) =>
        InvokePairMethod("CanBeginDynamicTracking", first, second);

    internal static bool IsPairEnabledBySaveDefaults(
        NeuralNPC first,
        NeuralNPC second)
    {
        MethodInfo? method = FindMethod("IsPairEnabledBySaveDefaults");
        if (method == null)
            return true;
        try
        {
            return method.Invoke(null, new object?[] { first, second })
                is bool enabled && enabled;
        }
        catch (Exception exception)
        {
            Plugin.Log.LogWarning(
                "Dynamic relationship default-policy lookup failed: "
                + exception);
            return true;
        }
    }

    internal static bool Begin(
        NeuralNPC first,
        NeuralNPC second) =>
        InvokePairMethod("BeginDynamicTracking", first, second);

    internal static bool Resume(
        NeuralNPC first,
        NeuralNPC second) =>
        InvokePairMethod("ResumeDynamicRelationship", first, second);

    internal static bool Reset(
        NeuralNPC first,
        NeuralNPC second) =>
        InvokePairMethod("ResetDynamicRelationship", first, second);

    internal static bool CanEditState =>
        FindMethod("SetDynamicRelationshipState") != null;

    internal static bool SetState(
        NeuralNPC first,
        NeuralNPC second,
        int score,
        string stage,
        bool tierLocked)
    {
        MethodInfo? method = FindMethod("SetDynamicRelationshipState");
        if (method == null)
            return false;
        try
        {
            return method.Invoke(
                    null,
                    new object?[]
                    {
                        first,
                        second,
                        score,
                        stage,
                        tierLocked
                    })
                is bool saved && saved;
        }
        catch (Exception exception)
        {
            Plugin.Log.LogWarning(
                "Dynamic relationship state edit failed: " + exception);
            return false;
        }
    }

    private static bool InvokePairMethod(
        string methodName,
        NeuralNPC first,
        NeuralNPC second)
    {
        MethodInfo? method = FindMethod(methodName);
        if (method == null)
            return false;
        try
        {
            return method.Invoke(null, new object?[] { first, second })
                is bool result && result;
        }
        catch (Exception exception)
        {
            Plugin.Log.LogWarning(
                $"Dynamic relationship bridge call {methodName} failed: "
                + exception);
            return false;
        }
    }
}

internal sealed class DynamicRelationshipSnapshot
{
    internal int Score;
    internal string Stage = "";
    internal int NextReviewScore;
    internal bool Suspended;
    internal int InteractionCount;
    internal bool HasEnemiesTier;
    internal int EnemyThreshold = -50;
    internal int StrangerThreshold;
    internal int AcquaintanceThreshold;
    internal int FriendPromotionThreshold;
    internal int LoverPromotionThreshold;
    internal int MarriagePromotionThreshold;
    internal int FriendRetentionThreshold;
    internal int LoverRetentionThreshold;
    internal int MarriageRetentionThreshold;
    internal bool LoversEnabled;
    internal bool MarriageEnabled;
    internal bool PromotionAwaitingConversation;
    internal int MinimumScore = -100;
    internal int MaximumScore = 150;
    internal bool TierLocked;
    internal bool PromotionCeilingLocked;
    internal string PromotionCeilingStage = "";
    internal string PromotionLockReason = "";
    internal int RomanticRejectionThreshold = 2;

    internal string GetDisplayText()
    {
        string state = Suspended
            ? $"Dynamic: suspended ({Stage})"
            : $"Dynamic: {Stage}";
        if (TierLocked)
            state += " [tier locked: score and tier frozen]";
        string nextReview = NextReviewScore > MaximumScore
            ? "none"
            : NextReviewScore.ToString();
        string loverPromotion = LoversEnabled
            ? LoverPromotionThreshold.ToString()
            : "disabled";
        string marriagePromotion = MarriageEnabled
            ? MarriagePromotionThreshold.ToString()
            : "disabled";
        bool belowLockedCeiling = PromotionCeilingLocked
            && GetStageRank(Stage) < GetStageRank(PromotionCeilingStage);
        bool hasNextPromotion = TierLocked
            ? false
            : PromotionCeilingLocked
            ? belowLockedCeiling
            : Stage == "Enemies"
                || Stage == "Acquaintance"
                || Stage == "Stranger"
                || (Stage == "Friend" && LoversEnabled)
                || (Stage == "Lover" && MarriageEnabled);
        if (NextReviewScore > MaximumScore)
            hasNextPromotion = false;
        nextReview = hasNextPromotion ? nextReview : "none";
        string pendingContext = PromotionAwaitingConversation
            ? "\nPromotion pending: complete a shared conversation for fresh context."
            : "";
        string ceilingContext = PromotionCeilingLocked
            ? "\nPromotion ceiling: " + PromotionCeilingStage
                + " (demotion and automatic recovery remain possible; no "
                + "further promotion queries).\nLock reason: "
                + PromotionLockReason
            : "\nPermanent romantic viability review after: "
                + RomanticRejectionThreshold
                + " directional rejections.";
        string enemyThreshold = HasEnemiesTier
            ? $"Enemy at/below {EnemyThreshold} (returns to Stranger above it); "
            : "";
        return $"{state} | Score: {Score} | Direct interactions: "
            + $"{InteractionCount}\nStage thresholds: {enemyThreshold}"
            + $"Acquaintance falls to Stranger at/below "
            + $"{StrangerThreshold}; Stranger rises to Acquaintance at/above "
            + $"{AcquaintanceThreshold}\nPromotion thresholds: Friend "
            + $"{FriendPromotionThreshold}, Lover {loverPromotion}, Married "
            + $"{marriagePromotion} | Next review: {nextReview}\nRetention "
            + $"thresholds: Friend {FriendRetentionThreshold}, Lover "
            + $"{LoverRetentionThreshold}, Married "
            + $"{MarriageRetentionThreshold}{pendingContext}{ceilingContext}";
    }

    private static int GetStageRank(string stage) => stage switch
    {
        "Enemies" => -2,
        "Stranger" => -1,
        "Acquaintance" => 0,
        "Friend" => 1,
        "Lover" => 2,
        "Married" => 3,
        _ => int.MinValue
    };
}
