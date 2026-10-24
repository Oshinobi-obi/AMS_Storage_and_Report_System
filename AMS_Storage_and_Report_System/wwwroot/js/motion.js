// These are the animations of e-PINS: the dashboard numbers count up, and a check mark shows
// after Approve / Mark as issued. Nothing moves if the person turned animations off.
window.amsMotion = (function () {
    var reduce = window.matchMedia("(prefers-reduced-motion: reduce)");

    // Count up the numbers in the summary tiles
    var running = new WeakMap();
    function countUp(el) {
        if (reduce.matches || running.has(el)) return;
        // I only change the existing text node, so Blazor can still update the number later.
        var tn = el.firstChild;
        if (!tn || tn.nodeType !== 3 || el.childNodes.length !== 1) return;
        var orig = tn.nodeValue, text = orig.trim();
        var m = text.match(/^([^\d-]*)([\d,]+(?:\.\d+)?)(.*)$/);
        if (!m) return;
        var target = parseFloat(m[2].replace(/,/g, ""));
        if (!isFinite(target) || target <= 0) return;
        var decimals = (m[2].split(".")[1] || "").length;
        var commas = m[2].indexOf(",") >= 0 || target >= 1000;
        var fmt = function (v) {
            var s = v.toFixed(decimals);
            if (commas) s = Number(s).toLocaleString("en-PH", { minimumFractionDigits: decimals, maximumFractionDigits: decimals });
            return m[1] + s + m[3];
        };
        var start = performance.now(), dur = Math.min(900, 400 + target * 8), last = fmt(0);
        tn.nodeValue = last;
        running.set(el, true);
        function step(now) {
            // Blazor changed the number in the meantime, so stop and keep its value.
            if (tn.nodeValue !== last || tn.parentNode !== el) { running.delete(el); return; }
            var t = Math.min(1, (now - start) / dur), eased = 1 - Math.pow(1 - t, 3);
            last = t < 1 ? fmt(target * eased) : orig;
            tn.nodeValue = last;
            if (t < 1) requestAnimationFrame(step); else running.delete(el);
        }
        requestAnimationFrame(step);
    }

    function scan(root) {
        if (root.nodeType !== 1) return;
        if (root.matches && root.matches(".stat-value")) countUp(root);
        if (root.querySelectorAll) root.querySelectorAll(".stat-value").forEach(countUp);
    }

    new MutationObserver(function (records) {
        records.forEach(function (r) { r.addedNodes.forEach(scan); });
    }).observe(document.documentElement, { childList: true, subtree: true });

    // Check mark
    function success(text) {
        var el = document.createElement("div");
        el.className = "done-burst"; el.setAttribute("role", "status");
        el.innerHTML = '<div class="done-card"><svg viewBox="0 0 84 84" aria-hidden="true"><circle cx="42" cy="42" r="38"/><path d="M26 43 L37 54 L59 31"/></svg><span></span></div>';
        el.querySelector("span").textContent = text || "Done";
        document.body.appendChild(el);
        setTimeout(function () { el.remove(); }, 1700);
    }

    return { success: success };
})();
