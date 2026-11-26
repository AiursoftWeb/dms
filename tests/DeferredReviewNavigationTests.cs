using Aiursoft.DMS.Lockbox.Android;

namespace Aiursoft.DMS.Tests;

[TestClass]
public sealed class DeferredReviewNavigationTests
{
    [TestMethod]
    public void NotificationsDuringAnOperationCoalesceUntilIdle()
    {
        var navigation = new DeferredReviewNavigation();
        navigation.Request();
        Assert.IsFalse(navigation.TryConsume(busy: true, CancellationToken.None));
        navigation.Request();
        Assert.IsFalse(navigation.TryConsume(busy: true, CancellationToken.None));
        Assert.IsTrue(navigation.TryConsume(busy: false, CancellationToken.None));
        Assert.IsFalse(navigation.TryConsume(busy: false, CancellationToken.None));
    }

    [TestMethod]
    public void DestroyedActivityDoesNotNavigateAfterItsOperationEnds()
    {
        var navigation = new DeferredReviewNavigation();
        navigation.Request();
        Assert.IsFalse(navigation.TryConsume(busy: false, new CancellationToken(canceled: true)));
    }
}
