using D2NG.Core.D2GS;
using Xunit;

namespace D2NG.Core.Tests.D2GS
{
    public class PointTests
    {
        [Fact]
        public void GetPointPastPointInSameDirection1()
        {
            var pointFrom = new Point(5158, 1821);
            var pointTo = new Point(5148, 1823);
            Assert.Equal(new Point(5138, 1824), pointFrom.GetPointPastPointInSameDirection(pointTo, 10));
        }
    }
}
