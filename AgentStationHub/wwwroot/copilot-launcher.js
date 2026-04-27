// =============================================================================
// copilot-launcher.js
//
// Global opener for the floating Copilot CLI panel defined in
// Components/CopilotLauncher.razor. Exposed so plain anchors / nav links
// anywhere on the site can trigger the panel with a one-liner:
//
//     window.copilotLauncher.open()
//
// The Blazor component registers itself via `.register(dotNetRef)` after
// its first render. Calls to .open() before registration are queued and
// flushed once the component is live (covers the race where the user
// clicks faster than MainLayout's interactive circuit finishes booting).
//
// History note: this file used to wrap a custom xterm.js terminal that
// pumped Copilot CLI bytes through SignalR. That stack produced visible
// REP/box-drawing glitches with Copilot's Ink-based TUI and was replaced
// by an <iframe> pointing at the ttyd service in the sidecar container.
// All that survived was this small DotNetObjectReference dispatcher.
// =============================================================================
(function () {
    let ref = null;
    let queued = false;

    function register(dotNetRef) {
        ref = dotNetRef;
        if (queued) {
            queued = false;
            ref.invokeMethodAsync('OpenFromJs').catch(err =>
                console.warn('[copilotLauncher] open failed:', err));
        }
    }

    function open() {
        if (ref) {
            ref.invokeMethodAsync('OpenFromJs').catch(err =>
                console.warn('[copilotLauncher] open failed:', err));
        } else {
            queued = true;
        }
    }

    window.copilotLauncher = { register, open };
})();
