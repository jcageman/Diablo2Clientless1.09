using D2NG.Core.D2GS;
using Xunit;

namespace D2NG.Core.Tests.D2GS
{
    public class PointTests
    {
        [Fact]
        public void GetPointPastPointInSameDirection1()
        {
            var pointFrom = new Point(0, 1);
            var pointTo = new Point(0, 10);
            Assert.Equal(new Point(0, 20), pointFrom.GetPointPastPointInSameDirection(pointTo, 10));
        }

        [Fact]
        public void GetPointPastPointInSameDirection2()
        {
            var pointFrom = new Point(5158, 1821);
            var pointTo = new Point(5148, 1823);
            Assert.Equal(new Point(5138, 1824), pointFrom.GetPointPastPointInSameDirection(pointTo, 10));
        }

        [Fact]
        public void GetPointBeforePointInSameDirection1()
        {
            var pointFrom = new Point(0, 1);
            var pointTo = new Point(0, 10);
            Assert.Equal(new Point(0, 5), pointFrom.GetPointBeforePointInSameDirection(pointTo, 5));
        }

        [Fact]
        public void GetPointBeforePointInSameDirection2()
        {
            var pointFrom = new Point(5158, 1821);
            var pointTo = new Point(5148, 1823);
            Assert.Equal(new Point(5152, 1822), pointFrom.GetPointBeforePointInSameDirection(pointTo, 5));
        }
    }
}
