using System;
using System.Text.RegularExpressions;

public static class Program
{
    private static string Id(string line)
    {
        var match = Regex.Match(line, "\\\"id\\\"\\s*:\\s*(\\\"(?:\\\\.|[^\\\"])*\\\"|-?\\d+(?:\\.\\d+)?)");
        return match.Success ? match.Groups[1].Value : "null";
    }

    public static void Main()
    {
        string line;
        while ((line = Console.ReadLine()) != null)
        {
            if (!line.Contains("\"id\"")) continue;
            var id = Id(line);
            var initialize = line.Contains("\"method\":\"initialize\"");
            Console.WriteLine(initialize
                ? "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"protocolVersion\":\"2025-03-26\",\"serverInfo\":{\"name\":\"fake-mcp\",\"version\":\"1.0\"}}}"
                : "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{}}");
            Console.Out.Flush();
        }
    }
}
