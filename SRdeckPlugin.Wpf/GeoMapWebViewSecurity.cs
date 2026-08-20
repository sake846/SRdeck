using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.Web.WebView2.Core;

namespace SRdeckPlugin.Wpf;

public static class GeoMapWebViewSecurity
{
    private const string ProductToken = "SRdeck/1.0 (+https://github.com/sake846/SRdeck)";
    private static readonly ConditionalWeakTable<CoreWebView2, object> ConfiguredCores = new();
    private static readonly object ConfiguredMarker = new();

    public static void Configure(CoreWebView2 core)
    {
        ArgumentNullException.ThrowIfNull(core);
        lock (ConfiguredCores)
        {
            if (ConfiguredCores.TryGetValue(core, out _)) return;
            ConfiguredCores.Add(core, ConfiguredMarker);
        }

        CoreWebView2Settings settings = core.Settings;
        if (!settings.UserAgent.Contains("SRdeck/", StringComparison.Ordinal))
        {
            settings.UserAgent = $"{settings.UserAgent} {ProductToken}".Trim();
        }
        settings.AreDevToolsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsWebMessageEnabled = true;

        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, args) =>
        {
            if (!IsPermittedResource(args.Request.Uri))
            {
                args.Response = core.Environment.CreateWebResourceResponse(
                    Stream.Null, 403, "Forbidden", "Content-Type: text/plain");
            }
        };
        core.NavigationStarting += (_, args) =>
        {
            if (IsInternalDocument(args.Uri)) return;
            args.Cancel = true;
            if (args.IsUserInitiated) OpenPermittedExternalLink(args.Uri);
        };
        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            if (args.IsUserInitiated) OpenPermittedExternalLink(args.Uri);
        };
        core.DownloadStarting += (_, args) => args.Cancel = true;
        core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
    }

    private static bool IsPermittedResource(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)) return false;
        if (uri.Scheme.Equals("about", StringComparison.OrdinalIgnoreCase) ||
            uri.Scheme.Equals("data", StringComparison.OrdinalIgnoreCase) ||
            uri.Scheme.Equals("blob", StringComparison.OrdinalIgnoreCase)) return true;
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;

        return (uri.Host.Equals("unpkg.com", StringComparison.OrdinalIgnoreCase) &&
                uri.AbsolutePath.StartsWith("/leaflet@1.9.4/dist/", StringComparison.Ordinal)) ||
               (uri.Host.Equals("tile.openstreetmap.org", StringComparison.OrdinalIgnoreCase) &&
                uri.AbsolutePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsInternalDocument(string value)
    {
        if (value.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase)) return true;
        return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
               uri.Scheme.Equals("about", StringComparison.OrdinalIgnoreCase) &&
               uri.AbsolutePath.Equals("blank", StringComparison.OrdinalIgnoreCase);
    }

    private static void OpenPermittedExternalLink(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !uri.Host.Equals("www.openstreetmap.org", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.Equals("/copyright", StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // A missing or policy-blocked browser must not crash the host process.
        }
    }
}
