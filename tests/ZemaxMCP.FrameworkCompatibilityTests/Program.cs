using System.Text;
using ZemaxMCP.Server.Compatibility;

EngineeringWorkflowAssertions.Run();

if (!FrameworkCompatibility.IsFinite(0) || !FrameworkCompatibility.IsFinite(double.MaxValue) ||
    FrameworkCompatibility.IsFinite(double.NaN) || FrameworkCompatibility.IsFinite(double.PositiveInfinity) ||
    FrameworkCompatibility.IsFinite(double.NegativeInfinity))
    throw new Exception("Finite-value compatibility failed.");

using (var input = new MemoryStream(Encoding.UTF8.GetBytes("abc")))
    if (FrameworkCompatibility.Sha256Hex(input) != "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")
        throw new Exception("Streaming SHA-256 compatibility failed.");

foreach (var path in new[] { @"C:\Samples\lens.zrd", @"\\server\share\lens.zrd" })
    if (!FrameworkCompatibility.IsFullyQualifiedPath(path)) throw new Exception("Absolute path rejected.");
foreach (var path in new[] { "", "lens.zrd", @"C:lens.zrd", @"\Samples\lens.zrd" })
    if (FrameworkCompatibility.IsFullyQualifiedPath(path)) throw new Exception("Relative path accepted.");

var values = new Dictionary<string, int>();
if (!values.TryAdd("existing", 1) || values.TryAdd("existing", 2) || values["existing"] != 1)
    throw new Exception("Dictionary admission must preserve the first value.");

Console.WriteLine("net48 optical kernels compiled; finite numbers, streaming SHA-256, absolute paths and dictionary admission passed.");
