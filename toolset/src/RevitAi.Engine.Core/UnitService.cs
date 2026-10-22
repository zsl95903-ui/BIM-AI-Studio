using RevitAi.Engine.Abstractions;

namespace RevitAi.Engine.Core;

/// <summary>
/// 单位换算：Revit 内部长度单位是英尺；对模型暴露的口径是毫米（位移/尺寸）与米（标高）。
/// 长度换算常量采用 Revit 内部单位：1 英尺 = 304.8 毫米。
/// </summary>
public sealed class UnitService : IUnitConverter
{
    public double MillimetersPerFoot => 304.8;

    public double MmToFeet(double millimeters) => millimeters / MillimetersPerFoot;

    public double FeetToMm(double feet) => feet * MillimetersPerFoot;

    public double MetersToFeet(double meters) => meters * 1000.0 / MillimetersPerFoot;

    public double FeetToMeters(double feet) => feet * MillimetersPerFoot / 1000.0;

    public double Round(double value, int digits) => Math.Round(value, digits, MidpointRounding.AwayFromZero);
}
