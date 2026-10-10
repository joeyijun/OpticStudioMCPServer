using System.Reflection;

namespace ZemaxMCP.Server.Tools.Analysis;

/// <summary>Version-tolerant invocation of the documented 2025/2026
/// ILDERow.GetCoatingPerformanceData API. No static reference to new COM
/// interfaces: old ZOS-API versions fail clearly instead of inventing RTA.</summary>
public static class CoatingPerformanceReflection
{
    public sealed record Polarized(double S,double P,double Unpolarized);
    public sealed record Coefficients(Polarized Reflection,Polarized Transmission,
        Polarized Absorption,double ResidualS,double ResidualP,bool PassiveRange);

    internal static Coefficients Read(object row,Type rowInterface,
        double incidenceAngleDegrees,double wavelengthMicrometers,string direction)
    {
        if(row==null || rowInterface==null || !double.IsFinite(incidenceAngleDegrees) ||
            incidenceAngleDegrees<0 || incidenceAngleDegrees>89.9 ||
            !double.IsFinite(wavelengthMicrometers) || wavelengthMicrometers<=0 ||
            direction is not ("inward" or "outward"))
            throw new ArgumentException("Expected a valid LDE row, 0..89.9 degree AOI, positive wavelength in micrometers and inward/outward direction.");
        var dataGetter=rowInterface.GetMethod("GetCoatingPerformanceData",Type.EmptyTypes)
            ?? throw new NotSupportedException(
                "Installed ZOS-API lacks ILDERow.GetCoatingPerformanceData. Upgrade OpticStudio/ZOS-API for native coating R/T/A readback.");
        var data=dataGetter.Invoke(row,null)
            ?? throw new InvalidOperationException("Native LDE GetCoatingPerformanceData returned null.");
        var performanceInterface=dataGetter.ReturnType;
        var calculate=performanceInterface.GetMethod("GetCoatingPerformance",
            BindingFlags.Public|BindingFlags.Instance)
            ?? throw new NotSupportedException("Native ICoatingPerformanceData.GetCoatingPerformance is unavailable.");
        var args=calculate.GetParameters();
        if(args.Length!=3 || !args[2].ParameterType.IsEnum)
            throw new NotSupportedException("Native coating API direction enum signature is incompatible.");
        var travel=Enum.Parse(args[2].ParameterType,direction,ignoreCase:true);
        calculate.Invoke(data,new object[]{incidenceAngleDegrees,wavelengthMicrometers,travel});

        Polarized ReadPolarization(string name)
        {
            var prop=performanceInterface.GetProperty(name)
                ?? throw new NotSupportedException("Native coating API missing "+name+".");
            var result=prop.GetValue(data)
                ?? throw new InvalidOperationException("Native coating "+name+" returned null.");
            var parameterType=prop.PropertyType;
            var ps=parameterType.GetProperty("S")?.GetValue(result);
            var pp=parameterType.GetProperty("P")?.GetValue(result);
            if(ps==null||pp==null)
                throw new NotSupportedException("Native coating "+name+" S/P data are unavailable.");
            var ss=Convert.ToDouble(ps,System.Globalization.CultureInfo.InvariantCulture);
            var sp=Convert.ToDouble(pp,System.Globalization.CultureInfo.InvariantCulture);
            if(!double.IsFinite(ss)||!double.IsFinite(sp))
                throw new InvalidDataException("Native coating "+name+" has non-finite S/P intensities.");
            return new Polarized(ss,sp,(ss+sp)*0.5);
        }
        var r=ReadPolarization("Reflection");
        var t=ReadPolarization("Transmission");
        var a=ReadPolarization("Absorption");
        var residualS=1-r.S-t.S-a.S;
        var residualP=1-r.P-t.P-a.P;
        var passive=new[]{r.S,r.P,t.S,t.P,a.S,a.P}.All(value=>value>=0&&value<=1);
        if(!double.IsFinite(residualS)||!double.IsFinite(residualP))
            throw new InvalidDataException("Coating S/P R+T+A arithmetic overflow.");
        return new Coefficients(r,t,a,residualS,residualP,passive);
    }
}
