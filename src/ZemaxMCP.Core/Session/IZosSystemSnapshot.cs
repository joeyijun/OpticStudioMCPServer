namespace ZemaxMCP.Core.Session;

/// <summary>
/// Minimal ZOS-API boundary needed by the pre-change snapshot safety policy.
/// Keeping this boundary free of ZOSAPI makes the policy executable in public
/// CI with a fake optical system, without distributing Zemax assemblies.
/// </summary>
internal interface IZosSystemSnapshot
{
    string? SystemFile { get; }
    IZosSystemSnapshot? CopySystem();
    void SaveAs(string path);
    void Close(bool saveChanges);
}
