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

// amsPrint.ris() — prints only the Requisition and Issue Slip on a clean white A4 page.
// The slip is copied into #ris-print-root (outside the page layout), so side columns,
// scroll boxes and the dark theme can't crop it or tint the paper. ris.css does the rest.
window.amsPrint = (function () {
    function cleanup() {
        document.documentElement.classList.remove("ris-printing");
        var old = document.getElementById("ris-print-root");
        if (old) old.remove();
    }
    function ris() {
        var sheet = document.querySelector(".ris-sheet");
        if (!sheet) { window.print(); return; }
        cleanup();
        var root = document.createElement("div");
        root.id = "ris-print-root";
        root.appendChild(sheet.cloneNode(true));
        document.body.appendChild(root);
        document.documentElement.classList.add("ris-printing");
        window.addEventListener("afterprint", cleanup, { once: true });
        var ready = document.fonts && document.fonts.ready ? document.fonts.ready : Promise.resolve();
        ready.then(function () { setTimeout(function () { window.print(); }, 50); });
    }
    return { ris: ris };
})();

// amsPhotos.shrink(url, maxSide, quality) — loads a saved catalog photo, scales it down to fit
// maxSide x maxSide and returns it as WebP bytes. Returns empty bytes when it is already small.
window.amsPhotos = {
    shrink: async function (url, maxSide, quality) {
        var res = await fetch(url, { cache: "no-store", credentials: "same-origin" });
        if (!res.ok) throw new Error("Photo not found (" + res.status + ")");
        var blob = await res.blob();
        var bmp = await createImageBitmap(blob);
        var scale = Math.min(1, maxSide / Math.max(bmp.width, bmp.height));
        if (scale === 1 && blob.size <= 300 * 1024) { bmp.close && bmp.close(); return new Uint8Array(0); }
        var canvas = document.createElement("canvas");
        canvas.width = Math.max(1, Math.round(bmp.width * scale));
        canvas.height = Math.max(1, Math.round(bmp.height * scale));
        canvas.getContext("2d").drawImage(bmp, 0, 0, canvas.width, canvas.height);
        bmp.close && bmp.close();
        var out = await new Promise(function (resolve) { canvas.toBlob(resolve, "image/webp", quality || 0.82); });
        if (!out || out.size >= blob.size) return new Uint8Array(0);   // no gain: keep the original
        return new Uint8Array(await out.arrayBuffer());
    }
};

// amsDatePicker — places the open calendar (DatePicker.razor) next to its field.
// It floats over everything (dialogs included), is as wide as the field, opens above the
// field when there's no room below, never leaves the screen, and follows the field on scroll.
window.amsDatePicker = (function () {
    var current = null;
    var MARGIN = 8, GAP = 4, MIN_WIDTH = 296;

    function place(toggle, panel, minWidth) {
        var r = toggle.getBoundingClientRect();
        var vw = document.documentElement.clientWidth, vh = window.innerHeight;
        panel.style.position = "fixed";
        panel.style.right = "auto";
        panel.style.bottom = "auto";
        var w = Math.min(Math.max(r.width, minWidth || MIN_WIDTH), vw - 2 * MARGIN);
        panel.style.width = w + "px";
        var h = panel.offsetHeight;
        // Wider than the field: grow toward the middle of the screen (right edges line up for fields on the right)
        var left = w > r.width && r.left + r.width / 2 > vw / 2 ? r.right - w : r.left;
        left = Math.min(Math.max(left, MARGIN), vw - w - MARGIN);
        var top = r.bottom + GAP;
        if (top + h > vh - MARGIN) {
            var above = r.top - GAP - h;
            top = above >= MARGIN ? above : Math.max(MARGIN, vh - MARGIN - h);
        }
        panel.style.left = left + "px";
        panel.style.top = top + "px";
        // A transformed parent moves "fixed" elements; correct for it if that happens.
        var a = panel.getBoundingClientRect();
        if (Math.abs(a.left - left) > 1 || Math.abs(a.top - top) > 1) {
            panel.style.left = (2 * left - a.left) + "px";
            panel.style.top = (2 * top - a.top) + "px";
        }
        panel.classList.add("is-placed");
    }

    function detach() {
        if (!current) return;
        window.removeEventListener("resize", current.handler);
        window.removeEventListener("scroll", current.handler, true);
        current = null;
    }

    function attach(toggle, panel, minWidth) {
        if (!toggle || !panel) return;
        if (!current || current.panel !== panel) {
            detach();
            var handler = function () {
                if (document.body.contains(panel) && document.body.contains(toggle)) place(toggle, panel, minWidth); else detach();
            };
            window.addEventListener("resize", handler);
            window.addEventListener("scroll", handler, true);
            current = { panel: panel, handler: handler };
        }
        place(toggle, panel, minWidth);
    }

    return { attach: attach, detach: detach };
})();
