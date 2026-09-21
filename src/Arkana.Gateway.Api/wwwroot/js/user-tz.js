// Detects the browser's IANA timezone ID for per-session display on the gateway.
// Called from MainLayout.razor via IJSRuntime after the first interactive render.
// Fallback chain is handled server-side in UserTimeService.TrySetBrowserTimeZone.
(function () {
    'use strict';
    window.getBrowserTimeZone = function () {
        try {
            return Intl.DateTimeFormat().resolvedOptions().timeZone;
        } catch (e) {
            return null;
        }
    };
})();
