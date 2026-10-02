// Small helpers called from Blazor through IJSRuntime.
window.ams = {
    // Submits a real HTML form (used after a confirmation dialog, e.g. Log out).
    submitForm: function (form) {
        if (!form) return;
        if (typeof form.requestSubmit === "function") form.requestSubmit();
        else form.submit();
    }
};
