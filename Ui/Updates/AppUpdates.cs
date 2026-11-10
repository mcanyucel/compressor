using Bridgewiz.AppUpdate;
using Bridgewiz.AppUpdate.Avalonia;

namespace Ui.Updates;

/// <summary>
/// Update checks through app-update-server (the Bridgewiz.AppUpdate packages). The server offers the newest uploaded
/// installer (installer\upload.ps1); the client downloads it, checks its SHA-256 and runs it silently, and the
/// installer closes the app, replaces it and starts it again.
/// </summary>
public static class AppUpdates
{
    /// <summary>The name the update server knows the app by (upload.ps1 uploads under it).</summary>
    public const string Name = "Compressor";

    /// <summary>The update server; settings.json's updateServer overrides it.</summary>
    public static readonly Uri DefaultServer = new("https://software.mustafacanyucel.com/");

    /// <summary>The running version (Directory.Build.props).</summary>
    public static Version Version { get; } = typeof(AppUpdates).Assembly.GetName().Version ?? new Version(0, 0, 0);

    private static readonly HttpClient Http = UpdateClient.CreateHttpClient(Options(DefaultServer));

    private static UpdateOptions Options(Uri server) => new() { AppName = Name, CurrentVersion = Version, Server = server };

    public static UpdateClient Client(UserSettings settings) =>
        new(Http, Options(Uri.TryCreate(settings.UpdateServer, UriKind.Absolute, out Uri? server) ? server : DefaultServer));

    /// <param name="beforeInstall">Runs before the installer starts; false postpones the update.</param>
    public static UpdatePrompt Prompt(UserSettings settings, Func<Task<bool>> beforeInstall) =>
        new(Client(settings), settings) { BeforeInstall = beforeInstall };
}
