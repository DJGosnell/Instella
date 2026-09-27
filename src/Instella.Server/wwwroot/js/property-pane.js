// Property pane viewport detection for mobile responsive behavior
window.propertyPane = {
    init: function(dotNetRef) {
        const checkMobile = () => {
            const isMobile = window.innerWidth < 768;
            dotNetRef.invokeMethodAsync('OnViewportChanged', isMobile);
        };

        // Listen for resize events
        window.addEventListener('resize', checkMobile);

        // Initial check
        checkMobile();

        // Return object with dispose method for cleanup
        return {
            dispose: () => window.removeEventListener('resize', checkMobile)
        };
    }
};

// Scroll to a specific row in the tree-grid
window.scrollToRow = function(gridElement, rowIndex) {
    if (!gridElement) return;
    const rows = gridElement.querySelectorAll('.tree-grid-row');
    if (rowIndex >= 0 && rowIndex < rows.length) {
        rows[rowIndex].scrollIntoView({ block: 'nearest', behavior: 'smooth' });
    }
};
