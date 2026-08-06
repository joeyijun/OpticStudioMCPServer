using ZOSAPI;

namespace ZemaxMCP.Core.Session;

/// <summary>Production adapter for the ZOS-API system snapshot boundary.</summary>
internal sealed class ZosApiSystemSnapshot : IZosSystemSnapshot
{
    private readonly IOpticalSystem _system;

    public ZosApiSystemSnapshot(IOpticalSystem system) => _system = system;
    public string? SystemFile => _system.SystemFile;
    public IZosSystemSnapshot? CopySystem()
    {
        var copy = _system.CopySystem();
        return copy == null ? null : new ZosApiSystemSnapshot(copy);
    }
    public void SaveAs(string path) => _system.SaveAs(path);
    public void Close(bool saveChanges) => _system.Close(saveChanges);
}
