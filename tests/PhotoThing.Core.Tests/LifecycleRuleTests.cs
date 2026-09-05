using FluentAssertions;
using PhotoThing.Core.Gcs;
using Xunit;

public class LifecycleRuleTests
{
    [Fact]
    public void Archive_rule_targets_blobs_prefix_and_age()
    {
        var rule = LifecycleManager.BuildArchiveAfterRule(180);
        rule.Action.Type.Should().Be("SetStorageClass");
        rule.Action.StorageClass.Should().Be("ARCHIVE");
        rule.Condition.Age.Should().Be(180);
        rule.Condition.MatchesPrefix.Should().Contain("blobs/");
    }
}
