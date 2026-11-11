using Kevsoft.MigrateMongo.MongoDriver;
using Shouldly;

namespace Kevsoft.MigrateMongo.Tests.MongoDriver;

public sealed class MongoMigrationExecutorMergeTests
{
    [Fact]
    public void An_invalid_search_plan_makes_the_merged_plan_invalid()
    {
        var index = new IndexPlan { Declaration = "sha256:index" };
        var search = new SearchIndexPlan { Invalid = true };
        search.Problems.Add("catalog: more than one declared search index is named products.");

        var merged = MongoMigrationExecutor.Merge(index, search);

        merged.Invalid.ShouldBeTrue();
        merged.Problems.ShouldContain("catalog: more than one declared search index is named products.");
    }

    [Fact]
    public void A_search_capability_problem_does_not_make_the_merged_plan_invalid()
    {
        var index = new IndexPlan { Declaration = "sha256:index" };
        var search = new SearchIndexPlan();
        search.Problems.Add("catalog: this deployment doesn't support Atlas search indexes.");

        var merged = MongoMigrationExecutor.Merge(index, search);

        merged.Invalid.ShouldBeFalse();
        merged.Problems.ShouldContain("catalog: this deployment doesn't support Atlas search indexes.");
    }
}
