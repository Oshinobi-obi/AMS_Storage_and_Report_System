// This runs in <head> before the page shows, so a saved dark theme doesn't
// flash white first. On the first visit it follows the computer's setting.
(function () {
    try {
        var saved = localStorage.getItem("ams-theme");
        var theme = saved || (window.matchMedia && matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light");
        document.documentElement.setAttribute("data-theme", theme);
        document.documentElement.setAttribute("data-bs-theme", theme);
    } catch (e) { }
})();
