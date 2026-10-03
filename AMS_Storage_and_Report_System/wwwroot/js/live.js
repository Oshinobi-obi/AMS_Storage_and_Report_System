// live.js — sound + desktop notifications for live updates (called from Blazor).
window.amsLive = (function () {
    const supported = () => "Notification" in window;
    const sounds = {};
    let unlocked = false;

    // Browsers only allow sound after the user has clicked or typed on the page.
    function unlock() {
        if (unlocked) return;
        unlocked = true;
        Object.values(sounds).forEach(a => {
            a.muted = true;
            a.play().then(() => { a.pause(); a.currentTime = 0; a.muted = false; }).catch(() => { a.muted = false; });
        });
    }
    document.addEventListener("pointerdown", unlock, { once: true, capture: true });
    document.addEventListener("keydown", unlock, { once: true, capture: true });

    function sound(url) {
        if (!sounds[url]) { sounds[url] = new Audio(url); sounds[url].preload = "auto"; }
        return sounds[url];
    }

    return {
        // Accepts preload("a.mp3", "b.mp3") or preload(["a.mp3", "b.mp3"]).
        preload: function (...urls) { urls.flat().filter(u => typeof u === "string").forEach(sound); },
        permission: function () { return supported() ? Notification.permission : "unsupported"; },
        requestPermission: async function () {
            unlock();
            if (!supported()) return "unsupported";
            try { return await Notification.requestPermission(); } catch { return Notification.permission; }
        },
        notify: function (title, body, soundUrl, url) {
            if (soundUrl) {
                const a = sound(soundUrl);
                try { a.currentTime = 0; a.play().catch(() => { }); } catch { }
            }
            // Desktop notification when this tab isn't on screen; the page shows its own toast.
            if (supported() && Notification.permission === "granted" && document.visibilityState !== "visible") {
                try {
                    const n = new Notification(title, { body: body, icon: "/favicon.png", tag: url || title });
                    n.onclick = function () { window.focus(); if (url) window.location.href = url; n.close(); };
                } catch { }
            }
        }
    };
})();
