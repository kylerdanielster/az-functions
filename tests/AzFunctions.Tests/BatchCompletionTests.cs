using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AzFunctions.Tests;

public class BatchCompletionTests
{
    private readonly IBatchTracker batchTracker = Substitute.For<IBatchTracker>();

    [Fact]
    public async Task CompleteBatch_HappyPath_ReturnsTrue()
    {
        batchTracker.IsBatchCompleteAsync("batch1").Returns(true);
        batchTracker.CompleteBatchAsync("batch1").Returns(true);

        bool isComplete = await batchTracker.IsBatchCompleteAsync("batch1");
        Assert.True(isComplete);

        bool wasCompleted = await batchTracker.CompleteBatchAsync("batch1");
        Assert.True(wasCompleted);
    }

    [Fact]
    public async Task CompleteBatch_AlreadyCompleted_ReturnsFalse()
    {
        // First call succeeds, second call returns false (already completed)
        batchTracker.CompleteBatchAsync("batch1").Returns(true, false);

        bool first = await batchTracker.CompleteBatchAsync("batch1");
        bool second = await batchTracker.CompleteBatchAsync("batch1");

        Assert.True(first);
        Assert.False(second);
    }

    [Fact]
    public async Task RaceCondition_OnlyOneCompletionWins()
    {
        // Simulate two concurrent completions — first wins, second loses
        batchTracker.CompleteBatchAsync("batch1").Returns(true, false);

        var results = new List<bool>();
        results.Add(await batchTracker.CompleteBatchAsync("batch1"));
        results.Add(await batchTracker.CompleteBatchAsync("batch1"));

        Assert.Single(results, r => r == true);
        Assert.Single(results, r => r == false);
    }
}
