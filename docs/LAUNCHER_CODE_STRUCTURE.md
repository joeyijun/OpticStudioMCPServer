# Launcher code ownership

The dashboard layout and behavior are unchanged by this refactor.
`MainWindow.xaml.cs` retains shared window state, construction, appearance,
load/close lifecycle, tray creation and common reporting. UI coordination is
split into partials:

| File | Responsibility |
| --- | --- |
| MainWindow.Configuration.cs | Local installation selection and run-configuration events |
| MainWindow.Connection.cs | Secure setup import/export and connection diagnostics |
| MainWindow.Service.cs | Start, safe stop, recovery and updater handoff |
| MainWindow.Status.cs | Health/activity polling and status presentation |
| MainWindow.Tasks.cs | Task selection, result presentation and cancellation UI |
| MainWindow.Clients.cs | AI client configuration actions |
| MainWindow.Updates.cs | Update UI, cancellation and maintenance coordination |
| MainWindow.Settings.cs | Preference loading/saving and local secrets |

Partials are organization boundaries, not independent state owners. They share
the same window instance and retain Dispatcher access rules. Protocol/network
helpers remain in `McpDiagnosticsClient`; task presentation remains in
`TaskCenterViewModel`; startup arguments remain in `HostLaunchPlan`;
downloads remain in `UpdateDownloader`. The new `UpdatePackageService`
owns version parsing and signed-package verification/extraction without WPF
controls. Maintenance admission stays in the window's service/update
coordination and is never bypassed.

Source-based regression checks read all `MainWindow*.cs` partials. Offline
XAML rendering, compiled-assembly reflection tests, safe-stop tests and updater
tests are retained. These tests do not instantiate the production window or
modify the running OpticStudio service.
