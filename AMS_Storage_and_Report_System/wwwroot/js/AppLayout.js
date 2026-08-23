function toggleSidebar() {
    document.getElementById('app-sidebar').classList.toggle('open');

    const backdrop = document.getElementById('sidebar-backdrop');
    if (backdrop.style.display === 'none') {
        backdrop.style.display = 'block';
    } else {
        backdrop.style.display = 'none';
    }
}