namespace Sim.Core;

/// <summary>
/// Straight road described by its grade (rise/run, positive = uphill in the direction of travel)
/// as a piecewise-linear function of distance along the road.
/// </summary>
public sealed class Road
{
    private readonly Curve _grade;

    public Road(Curve gradeByPositionM) => _grade = gradeByPositionM;

    public static Road Flat() => Constant(0);

    public static Road Constant(double grade) => new(new Curve([(0, grade)]));

    public double GradeAt(double positionM) => _grade.Evaluate(positionM);
}
