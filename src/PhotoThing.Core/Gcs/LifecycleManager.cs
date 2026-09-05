using Google.Apis.Storage.v1.Data;

namespace PhotoThing.Core.Gcs;

// Pure POCO types for lifecycle rule data — independent of the Google SDK types.
// Unit-testable without any network or SDK instantiation.

public sealed record LifecycleRuleData(
    LifecycleAction Action,
    LifecycleCondition Condition);

public sealed record LifecycleAction(string Type, string? StorageClass);

public sealed record LifecycleCondition(int Age, IReadOnlyList<string> MatchesPrefix);

public static class LifecycleManager
{
    public static LifecycleRuleData BuildArchiveAfterRule(int days) =>
        new(
            new LifecycleAction("SetStorageClass", "ARCHIVE"),
            new LifecycleCondition(days, new[] { "blobs/" }));

    /// <summary>
    /// Convert our POCO rule into the Google client's Bucket.LifecycleData
    /// (Google.Apis.Storage.v1.Data v1.74.0.4115).
    ///
    /// Adaptation notes vs the brief:
    ///   - ConditionData.Age is int? (nullable) in the installed SDK — we assign (int?) days,
    ///     which is valid and preserves the same value.
    ///   - ConditionData.MatchesPrefix is the correct property name (not MatchesPrefixes).
    /// </summary>
    public static Bucket.LifecycleData ToGoogleLifecycle(LifecycleRuleData rule) => new()
    {
        Rule = new List<Bucket.LifecycleData.RuleData>
        {
            new()
            {
                Action = new Bucket.LifecycleData.RuleData.ActionData
                {
                    Type = rule.Action.Type,
                    StorageClass = rule.Action.StorageClass,
                },
                Condition = new Bucket.LifecycleData.RuleData.ConditionData
                {
                    Age = rule.Condition.Age,          // int? — implicit from int
                    MatchesPrefix = rule.Condition.MatchesPrefix.ToList(),
                },
            },
        },
    };
}
