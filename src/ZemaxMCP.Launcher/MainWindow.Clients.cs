using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Newtonsoft.Json.Linq;

namespace ZemaxMCP.Launcher;

// UI coordination; shared state and lifecycle remain in MainWindow.xaml.cs.
public partial class MainWindow
{
    private void ConfigureDetected_Click(object sender, RoutedEventArgs e)
    {
        var configured = ConfigureDetectedClients();
        Report(configured.Count == 0 ? "No supported AI client was detected. Use the individual configuration buttons after installing one." : "Configured: " + string.Join(", ", configured) + ". Restart the client to connect.");
    }
    private void AiConfigMenu_Click(object sender, RoutedEventArgs e)
    {
        RefreshClientMenuIndicators();
        AiConfigButton.ContextMenu.PlacementTarget = AiConfigButton;
        AiConfigButton.ContextMenu.IsOpen = true;
    }
    private List<string> ConfigureDetectedClients()
    {
        var configured = new List<string>();
        foreach (var client in DetectedClientConfigurations())
        {
            try
            {
                client.Configure();
                configured.Add(client.Name);
            }
            catch (Exception ex)
            {
                Report("Could not configure " + client.Name + ": " + ex.Message);
            }
        }
        RefreshClientDashboard(null);
        return configured;
    }
    private void OfferFirstRunClientSetup()
    {
        if (_clientSetupPrompted || DetectedClientNames().Count == 0) return;
        _clientSetupPrompted = true;
        SaveSettings();
        var clients = Configurator.GetClientStatuses(McpUrl, McpToken)
            .Where(x => x.Detected && x.Configure != null).ToList();
        if (clients.All(x => x.Configured)) return;
        if (LauncherDialog.Confirm(this, "Connect your AI clients",
            "Configure the detected clients for this MCP endpoint. Other MCP entries will be kept. Restart each client after setup.",
            "Configure clients", clients.Select(x => (x.Name, x.Configured ? "Configured" : "Setup needed"))))
        {
            var configured = ConfigureDetectedClients();
            Report("Configured: " + string.Join(", ", configured) + ". Restart the client to connect.");
        }
    }
    private List<string> DetectedClientNames()
    {
        return DetectedClientConfigurations().Select(x => x.Name).ToList();
    }
    private List<(string Name, Action Configure)> DetectedClientConfigurations()
    {
        var clients = new List<(string, Action)>();
        foreach (var status in Configurator.GetClientStatuses(McpUrl, McpToken).Where(x => x.Detected && x.Configure != null))
            clients.Add((status.Name, () => status.Configure!(McpUrl, McpToken)));
        return clients;
    }
    private void ConfigureClient(string name, Action configure)
    {
        try { configure(); RefreshClientDashboard(null); Report(name + " configured for " + McpUrl + ". Restart the client to connect."); }
        catch (Exception ex) { Report("Could not configure " + name + ": " + ex.Message); }
    }
    private void Codex_Click(object sender, RoutedEventArgs e) => ConfigureClient("Codex", () => Configurator.ConfigureCodex(McpUrl, McpToken));
    private void Claude_Click(object sender, RoutedEventArgs e) => ConfigureClient("Claude Desktop", () => Configurator.ConfigureClaudeDesktop(McpUrl, McpToken));
    private void Cursor_Click(object sender, RoutedEventArgs e) => ConfigureClient("Cursor", () => Configurator.ConfigureCursor(McpUrl, McpToken));
    private void Antigravity_Click(object sender, RoutedEventArgs e) => ConfigureClient("Google Antigravity", () => Configurator.ConfigureAntigravity(McpUrl, McpToken));
    private void Kimi_Click(object sender, RoutedEventArgs e) => ConfigureClient("Kimi Code", () => Configurator.ConfigureKimi(McpUrl, McpToken));
    private void WorkBuddy_Click(object sender, RoutedEventArgs e) => ConfigureClient("WorkBuddy", () => Configurator.ConfigureWorkBuddy(McpUrl, McpToken));
    private void CopyGenericConfig_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.Clipboard.SetText(Configurator.GenericHttpJson(McpUrl, McpToken));
        Report("Generic HTTP MCP JSON copied. Paste it into an agent's MCP configuration and restart that agent.");
    }
    private void VsCode_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Configurator.ConfigureVsCode(McpUrl, McpToken);
            RefreshClientDashboard(null);
            Report("VS Code opened its MCP setup. Review and approve Zemax MCP there to finish configuration.");
        }
        catch (Exception ex) { Report("Could not open VS Code MCP setup: " + ex.Message); }
    }
}
