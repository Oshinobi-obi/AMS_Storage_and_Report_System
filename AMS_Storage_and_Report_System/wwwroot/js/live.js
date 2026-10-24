// This is for the sound and desktop notifications of the live updates (Blazor calls these).
window.amsLive = (function () {
    const supported = () => "Notification" in window;
    const sounds = {};
    let activated = false;          // has the person clicked or typed on this page yet?
    let pendingUrl = null;          // a sound the browser blocked, to play once sound is allowed

    function sound(url) {
        if (!sounds[url]) { sounds[url] = new Audio(url); sounds[url].preload = "auto"; }
        return sounds[url];
    }

    // Browsers only allow sound after a click or key press on the page. So on every click or key,
    // I "warm up" any sound that isn't unlocked yet (some browsers need it per sound).
    function unlockAll() {
        activated = true;
        // A sound that was blocked before plays now at full volume (the click allows it)...
        let skip = null;
        if (pendingUrl) {
            skip = sound(pendingUrl);
            pendingUrl = null;
            hideHint();
            skip.muted = false;
            skip.dataset.unlocked = "1";
            play(skip.src);
        }
        // ...and the other sounds are warmed up silently so they can play later without a click.
        Object.values(sounds).forEach(a => {
            if (a === skip || (a.dataset && a.dataset.unlocked)) return;
            a.muted = true;
            a.play().then(() => { a.pause(); a.currentTime = 0; a.muted = false; a.dataset.unlocked = "1"; })
                    .catch(() => { a.muted = false; });
        });
    }
    document.addEventListener("pointerdown", unlockAll, { capture: true });
    document.addEventListener("keydown", unlockAll, { capture: true });

    // Small button that only shows when the browser blocked a sound.
    let hint = null;
    function showHint() {
        if (hint) return;
        hint = document.createElement("button");
        hint.type = "button";
        hint.className = "sound-hint";
        hint.innerHTML = '<i class="bi bi-volume-mute-fill" aria-hidden="true"></i> Sound is blocked. Click to turn on sound alerts';
        hint.addEventListener("click", () => { unlockAll(); hideHint(); });
        document.body.appendChild(hint);
    }
    function hideHint() { if (hint) { hint.remove(); hint = null; } }

    function play(url) {
        const a = sounds[url] || Object.values(sounds).find(x => x.src === url) || sound(url);
        try { a.currentTime = 0; } catch { }
        const p = a.play();
        if (p && p.catch) {
            p.catch(err => {
                if (err && err.name === "NotAllowedError") { pendingUrl = url; showHint(); }
            });
        }
    }

    return {
        preload: function (...urls) { urls.flat().filter(u => typeof u === "string").forEach(sound); },
        permission: function () { return supported() ? Notification.permission : "unsupported"; },
        requestPermission: async function () {
            unlockAll();
            if (!supported()) return "unsupported";
            try { return await Notification.requestPermission(); } catch { return Notification.permission; }
        },
        notify: function (title, body, soundUrl, url) {
            if (soundUrl) play(soundUrl);
            // Desktop notification when this tab isn't on screen. The page shows its own banner or toast.
            if (supported() && Notification.permission === "granted" && document.visibilityState !== "visible") {
                try {
                    const n = new Notification(title, { body: body, icon: "/favicon.png", tag: url || title });
                    n.onclick = function () { window.focus(); if (url) window.location.href = url; n.close(); };
                } catch { }
            }
        }
    };
})();
