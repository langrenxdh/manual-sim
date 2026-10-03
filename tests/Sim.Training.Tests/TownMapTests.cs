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
        Assert.All(bays, b => Assert.True(Town.OnRoad((b.MinX + b.MaxX) / 2, (b.MinY + b.MaxY) / 2)));
    }

    [Fact]
    public void EveryStart_IsOnTheRoad()
    {
        Assert.All(Town.Starts, s => Assert.True(Town.OnRoad(s.X, s.Y), s.Id));
    }
}
