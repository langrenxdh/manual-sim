namespace Sim.Training.Tests;

/// <summary>M9c: the town map's geometry and on-road test.</summary>
public class TownMapTests
{
    private static readonly TownMap Town =
        TownMap.FromJson(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "config", "town.json")));

    [Fact]
    public void MainRoad_BendsLeftIntoTheRoundabout()
    {
        var main = Town.Expanded.Single(r => r.Road.Name == "main").Points;
        var end = main[^1];
        var rb = Town.Roundabout;

        // 200 m east, a 90 degree left arc of radius 60 (to 260, 60), then 140 m north.
        Assert.Equal(260, end.X, 3);
        Assert.Equal(200, end.Y, 3);
        Assert.Equal(rb.Center[1] - rb.OuterRadiusM, end.Y, 3); // meets the roundabout's outer edge
    }

    [Theory]
    [InlineData(100, 0, true)]      // main road centre
    [InlineData(100, 3.4, true)]    // just inside the edge (width 7)
    [InlineData(100, 3.6, false)]   // just outside
    [InlineData(260, 216, false)]   // roundabout island
    [InlineData(260, 228, true)]    // roundabout carriageway
    [InlineData(420, 310, true)]    // car park
    [InlineData(260, 300, true)]    // dead-end street
    [InlineData(150, 100, false)]   // open ground
    public void OnRoad_KnowsRoadsRoundaboutAndCarPark(double x, double y, bool expected)
    {
        Assert.Equal(expected, Town.OnRoad(x, y));
    }

    [Fact]
    public void CarPark_HasItsBaysAlongTheNorthEdge()
    {
        var bays = Town.Bays().ToList();
        var cp = Town.CarPark;

        Assert.Equal(cp.BayCount, bays.Count);
        Assert.All(bays, b => Assert.Equal(cp.Corner[1] + cp.Size[1], b.MaxY, 9));
        Assert.All(bays, b => Assert.True(b.MinX >= cp.Corner[0] && b.MaxX <= cp.Corner[0] + cp.Size[0] && b.MinY >= cp.Corner[1]));
        // Every bay without a parked car is road (a parked car is an obstacle).
        Assert.All(bays.Where((_, i) => !cp.ParkedBays.Contains(i)),
            b => Assert.True(Town.OnRoad((b.MinX + b.MaxX) / 2, (b.MinY + b.MaxY) / 2)));
    }

    /// <summary>
    /// Every road end joins another road, a roundabout or a car park (a point a few metres past it is still
    /// road), except the dead end, so free driving can go round the town without leaving the road.
    /// </summary>
    [Fact]
    public void EveryRoadEnd_JoinsTheNetwork_ExceptTheDeadEnd()
    {
        foreach (var (road, pts) in Town.Expanded.Where(r => r.Road.Name != "deadEnd"))
        {
            foreach (var (end, inner) in new[] { (pts[^1], pts[^2]), (pts[0], pts[1]) })
            {
                double dx = end.X - inner.X, dy = end.Y - inner.Y, len = Math.Sqrt(dx * dx + dy * dy);
                double px = end.X + dx / len * 2, py = end.Y + dy / len * 2;
                Assert.True(Town.OnRoad(px, py), $"{road.Name} ends at ({end.X:F0}, {end.Y:F0}) without joining anything");
            }
        }
    }

    [Fact]
    public void Town_IsAtLeastAKilometreAcross_WithTwoRoundaboutsAndCarParks()
    {
        var pts = Town.Expanded.SelectMany(r => r.Points).ToList();
        Assert.True(pts.Max(p => p.X) - pts.Min(p => p.X) >= 1000);
        Assert.True(Town.Roundabouts.Length >= 2 && Town.CarParks.Length >= 2);
        Assert.Contains(Town.Expanded, r => r.Points.Count > 1000); // a long road for the higher gears
    }

    [Fact]
    public void ParkedCars_SitInsideTheirBays_AndAreNotRoad()
    {
        var parked = Town.ParkedCars();
        Assert.NotEmpty(parked);
        var bays = Enumerable.Range(0, Town.CarParks.Length).SelectMany(k => Town.Bays(k)).ToList();
        foreach (var (minX, minY, maxX, maxY) in parked)
        {
            Assert.Contains(bays, b => minX >= b.MinX && maxX <= b.MaxX && minY >= b.MinY && maxY <= b.MaxY);
            Assert.False(Town.OnRoad((minX + maxX) / 2, (minY + maxY) / 2));
            Assert.True(Town.OnRoad((minX + maxX) / 2, minY - 0.3)); // the aisle in front of it is road
        }
    }

    [Fact]
    public void ExerciseBay_AndItsNeighbours_AreFree()
    {
        var parked = Town.ParkedCars();
        foreach (var (minX, minY, maxX, maxY) in Town.Bays().Take(3))
            Assert.True(Town.OnRoad((minX + maxX) / 2, (minY + maxY) / 2));
        Assert.Contains(Town.CarPark.ParkedBays, b => b > 2);
    }

    [Fact]
    public void EveryStart_IsOnTheRoad()
    {
        Assert.All(Town.Starts, s => Assert.True(Town.OnRoad(s.X, s.Y), s.Id));
    }
}
