using ZOSAPI.Editors.NCE;

namespace ZemaxMCP.Server.Tools.NonSequential;

internal static class NscObjectClassification
{
    // The built-in enum is stable across UI languages. Some runtime versions
    // return false for ObjectIsADetector even for built-in detector rows.
    internal static bool IsDetector(INCERow row) => row.TypeData.ObjectIsADetector || row.Type is
        ObjectType.DetectorColor or ObjectType.DetectorPolar or ObjectType.DetectorRectangle or
        ObjectType.DetectorSurface or ObjectType.DetectorVolume or ObjectType.ReverseRadianceDetector;
}
