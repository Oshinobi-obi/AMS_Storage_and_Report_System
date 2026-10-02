// showToast("Saved.", "success" | "danger") — short message at the bottom of the screen.
window.showToast = function (message, type) {
    const container = document.getElementById('toast-container');
    if (!container) return;

    const toast = document.createElement('div');
    toast.className = 'app-toast app-toast-' + (type || 'success');
    toast.setAttribute('role', 'status');
    toast.textContent = message;
    container.appendChild(toast);

    while (container.children.length > 3) container.firstElementChild.remove();
    setTimeout(function () { toast.remove(); }, 4500);
};
