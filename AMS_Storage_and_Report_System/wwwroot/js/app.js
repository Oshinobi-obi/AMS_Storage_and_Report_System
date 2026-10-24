// Small helpers that Blazor calls through IJSRuntime.
window.ams = {
    // Submits a real HTML form (I use it after a confirmation dialog, like Log out).
    submitForm: function (form) {
        if (!form) return;
        if (typeof form.requestSubmit === "function") form.requestSubmit();
        else form.submit();
    }
};
