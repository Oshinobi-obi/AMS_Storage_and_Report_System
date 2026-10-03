// ui.js — light/dark theme switch and the Philippine-time clock (UTC+8).
window.amsUi = (function () {
    var KEY = "ams-theme";

    function current() { return document.documentElement.getAttribute("data-theme") === "dark" ? "dark" : "light"; }

    function apply(theme) {
        document.documentElement.setAttribute("data-theme", theme);
        document.documentElement.setAttribute("data-bs-theme", theme);
    }

    function toggle() {
        var next = current() === "dark" ? "light" : "dark";
        apply(next);
        try { localStorage.setItem(KEY, next); } catch (e) { }
        return next;
    }

    // Clock: always Philippine time, whatever time zone the computer is set to.
    var dateFmt = new Intl.DateTimeFormat("en-PH", { timeZone: "Asia/Manila", weekday: "short", month: "short", day: "numeric", year: "numeric" });
    var timeFmt = new Intl.DateTimeFormat("en-PH", { timeZone: "Asia/Manila", hour: "numeric", minute: "2-digit", second: "2-digit", hour12: true });

    function tick() {
        var now = new Date();
        var d = dateFmt.format(now), t = timeFmt.format(now);
        document.querySelectorAll("[data-ph-clock]").forEach(function (el) {
            var de = el.querySelector("[data-date]"), te = el.querySelector("[data-time]");
            if (de && de.textContent !== d) de.textContent = d;
            if (te && te.textContent !== t) te.textContent = t;
            el.setAttribute("title", d + ", " + t + " (Philippine time, UTC+8)");
        });
    }
    tick();
    setInterval(tick, 1000);

    return { current: current, toggle: toggle, tick: tick };
})();
