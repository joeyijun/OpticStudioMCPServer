using System;
using System.Security.Cryptography;
using System.Text;

namespace ZemaxMCP.Launcher;

/// <summary>
/// UI-independent high-frequency diagnostic cursor. A cursor belongs to BOTH
/// an endpoint and its authenticated credential; never replay one across a
/// changed token, even if the URL is identical.
/// </summary>
internal sealed class ScopedDeltaCursor
{
    private string _scopeDigest = "";
    internal string Cursor { get; private set; } = "";
    internal bool Bound => _scopeDigest.Length > 0;

    private static string Scope(string endpoint,string token)
    {
        if(string.IsNullOrWhiteSpace(endpoint))
            throw new ArgumentException("A nonempty MCP endpoint is required.");
        using(var hash=SHA256.Create())
            return Convert.ToBase64String(hash.ComputeHash(
                Encoding.UTF8.GetBytes(endpoint.TrimEnd('/').ToUpperInvariant()+"\n"+token)));
    }

    /// <returns>True when the endpoint or bearer identity changed.</returns>
    internal bool Bind(string endpoint,string token)
    {
        var next=Scope(endpoint,token);
        if(string.Equals(_scopeDigest,next,StringComparison.Ordinal))return false;
        _scopeDigest=next;
        Cursor="";
        return true;
    }

    internal bool Matches(string endpoint,string token) =>
        _scopeDigest.Length>0 &&
        string.Equals(_scopeDigest,Scope(endpoint,token),StringComparison.Ordinal);

    internal string UrlSuffix(string suffix) =>
        suffix+"?cursor="+Uri.EscapeDataString(Cursor);

    internal void Update(string cursor)
    {
        if(cursor==null || cursor.Length!=64 ||
           !cursor.All(IsHex))
            throw new ArgumentException("Host returned an invalid opaque 64-digit cursor.");
        Cursor=cursor;
    }

    private static bool IsHex(char c) =>
        c>='0'&&c<='9'||c>='a'&&c<='f'||c>='A'&&c<='F';

    internal void ForceFullRefresh() => Cursor="";
}
