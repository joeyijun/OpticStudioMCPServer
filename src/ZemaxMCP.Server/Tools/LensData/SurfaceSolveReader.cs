using ZOSAPI.Editors;
using ZemaxMCP.Core.Models;

namespace ZemaxMCP.Server.Tools.LensData;

// Share strongly typed native readback between get and set. Dynamic aliases
// such as MarginalRayAngle_Angle do not exist in the ZOS-API contract.
internal static class SurfaceSolveReader
{
    internal static SolveData Read(ISolveData data, string? material = null)
    {
        var result = new SolveData { SolveType = data.Type.ToString() };
        return data.Type switch
        {
            SolveType.SurfacePickup => result with
            {
                PickupSurface = data._S_SurfacePickup.Surface,
                PickupColumn = (int)data._S_SurfacePickup.Column,
                ScaleFactor = data._S_SurfacePickup.ScaleFactor,
                Offset = data._S_SurfacePickup.Offset
            },
            SolveType.MarginalRayHeight => result with
            { Height = data._S_MarginalRayHeight.Height, PupilZone = data._S_MarginalRayHeight.PupilZone },
            // Retain Height for existing clients; Angle is the explicit semantic field.
            SolveType.MarginalRayAngle => result with
            { Height = data._S_MarginalRayAngle.Angle, Angle = data._S_MarginalRayAngle.Angle },
            SolveType.ChiefRayHeight => result with { Height = data._S_ChiefRayHeight.Height },
            SolveType.ChiefRayAngle => result with
            { Height = data._S_ChiefRayAngle.Angle, Angle = data._S_ChiefRayAngle.Angle },
            SolveType.EdgeThickness => result with
            { Thickness = data._S_EdgeThickness.Thickness, RadialHeight = data._S_EdgeThickness.RadialHeight },
            SolveType.Position => result with
            { ReferenceSurface = data._S_Position.FromSurface, Position = data._S_Position.Length },
            SolveType.FNumber => result with { FNumber = data._S_FNumber.FNumber },
            SolveType.CenterOfCurvature => result with { ReferenceSurface = data._S_CenterOfCurvature.RefSurface },
            SolveType.MaterialSubstitute => result with
            { Catalog = data._S_MaterialSubstitute.Catalog, MaterialName = material },
            SolveType.MaterialOffset => result with
            { IndexOffset = data._S_MaterialOffset.NdOffset, AbbeOffset = data._S_MaterialOffset.VdOffset },
            _ => result
        };
    }
}
