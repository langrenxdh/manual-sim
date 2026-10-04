namespace Sim.Training.Tests;

/// <summary>Parking sensors on the town map: distances to a parked car and to the edge of the car park.</summary>
public class ParkingSensorTests
{
    private static readonly TownMap Town =
        TownMap.FromJson(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "config", "town.json")));
    private static readonly TownCarDef Car = Configs.Exercises().TownCar!;

    private static readonly ParkingSensorParams Sensors = new()
    {
        FrontRangeM = 1.2, RearRangeM = 1.5, FrontActiveBelowKmh = 15, InnerSensorShare = 0.4, CornerAngleDeg = 30, StepM = 0.02,
    };

    private const double North = Math.PI / 2;

    /// <summary>Lined up behind the parked car in bay 4 of the first car park, the front bumper <paramref name="gapM"/> short of it.</summary>
    private static (double X, double Y) BehindParkedCar(double gapM)
    {
        Assert.Contains(4, Town.CarPark.ParkedBays);
        var (minX, minY, maxX, maxY) = Town.Bays().ElementAt(4);
        var car = Town.ParkedCars().Single(c => c.MinX >= minX && c.MaxX <= maxX && c.MinY >= minY && c.MaxY <= maxY);
        return ((car.MinX + car.MaxX) / 2, car.MinY - gapM - Car.FrontOfReferenceM);
    }

    [Fact]
    public void Front_MeasuresTheGapToAParkedCar_RearIsOffInAForwardGear()
    {
        var (x, y) = BehindParkedCar(1.0);
        var r = ParkingSensors.Measure(Town, Car, x, y, North, 0, reverse: false, Sensors);

        Assert.NotNull(r.FrontM);
        Assert.InRange(r.FrontM!.Value, 0.97, 1.03);
        Assert.Null(r.RearM);
    }

    [Fact]
    public void Rear_InReverse_MeasuresTheGapToTheCarParkEdge()
    {
        // In the first car park, west of the entrance, rear bumper 0.8 m from its south edge.
        var cp = Town.CarPark;
        double x = cp.Corner[0] + 9, y = cp.Corner[1] + 0.8 + Car.RearOfReferenceM;
        var r = ParkingSensors.Measure(Town, Car, x, y, North, -0.5, reverse: true, Sensors);

        Assert.NotNull(r.RearM);
        Assert.InRange(r.RearM!.Value, 0.77, 0.83);
        Assert.Null(r.FrontM); // the car park is open ahead
        Assert.Equal(r.RearM, r.NearestM);
    }

    [Fact]
    public void InTheMiddleOfARoad_NothingIsInRange()
    {
        var r = ParkingSensors.Measure(Town, Car, 100, 0, 0, -0.5, reverse: true, Sensors);

        Assert.Null(r.FrontM);
        Assert.Null(r.RearM);
        Assert.Null(r.NearestM);
    }

    [Fact]
    public void FrontSensors_AreOff_AboveTheirSpeed()
    {
        var (x, y) = BehindParkedCar(1.0);
        var r = ParkingSensors.Measure(Town, Car, x, y, North, 20 / 3.6, reverse: false, Sensors);

        Assert.Null(r.FrontM);
    }
}
