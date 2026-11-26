// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

namespace Apodofos.Desktop;

/// <summary>
/// How the shell starts the Microsoft Edge WebView2 Runtime so that it has nothing to fetch: the only page it shows is the
/// embedded one. Without these switches the runtime contacts Microsoft services on its own, although the application asks for
/// nothing: the configuration service and the component updater from its network process, and, once the profile has been used,
/// a Microsoft service from the browser process through the Windows account sign-in library. They apply to this application's
/// own runtime processes only: documentation links open in the user's browser, which is not affected.
/// Measure again before a release, with a used profile as well as a new one (a Chromium network log does not see the sign-in
/// library: watch the connections of the processes too): a newer runtime can behave differently. How: docs/release.md and
/// tools/check-network.ps1.
/// Plain strings on purpose, so that the tests can compile this file on every operating system.
/// </summary>
internal static class WebView2Network
{
    internal static readonly IReadOnlyList<string> Switches =
    [
        "--disable-background-networking",                          // no requests to the configuration and experimentation service
        "--disable-component-update",                               // no component update checks or downloads
        "--disable-features=msOneAuthWAM,msForceOneAuthWAM",        // the Windows account sign-in library is not loaded (both names are needed)
        "--no-proxy-server",                                        // no proxy, therefore no proxy auto-discovery lookup either
        "--host-resolver-rules=\"MAP * ~NOTFOUND\"",                // backstop for the network process: no host name resolves
    ];

    /// <summary>The value of <c>CoreWebView2EnvironmentOptions.AdditionalBrowserArguments</c>.</summary>
    internal static string BrowserArguments => string.Join(' ', Switches);
}
