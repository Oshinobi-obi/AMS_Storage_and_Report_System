// This is the seasonal theme (e-PINS > Settings > Seasonal theme).
// amsSeason.set("halloween") turns it on in the open page without reloading ("" turns it off).
// Moving the mouse tilts the decorations a little, so near and far ones move apart (3D).
window.amsSeason = {
    set: function (name) { document.documentElement.setAttribute("data-season", name || ""); }
};
(function () {
    var root = document.documentElement, queued = false, x = 0, y = 0;
    if (window.matchMedia && matchMedia("(prefers-reduced-motion: reduce)").matches) return;
    window.addEventListener("pointermove", function (e) {
        if (!root.getAttribute("data-season")) return;
        x = e.clientX / window.innerWidth - 0.5;
        y = e.clientY / window.innerHeight - 0.5;
        if (queued) return;
        queued = true;
        requestAnimationFrame(function () {
            queued = false;
            root.style.setProperty("--sx", x.toFixed(3));
            root.style.setProperty("--sy", y.toFixed(3));
        });
    }, { passive: true });
})();
