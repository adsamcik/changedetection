// Theme toggle JavaScript module for handling system theme detection and DOM updates

let dotNetRef = null;
let mediaQuery = null;
let mediaQueryHandler = null;

/**
 * Gets the current system theme preference.
 * @returns {"light" | "dark"} The system theme preference
 */
export function getSystemTheme() {
    if (window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches) {
        return 'dark';
    }
    return 'light';
}

/**
 * Sets the data-theme attribute on the document root element.
 * @param {"light" | "dark"} theme - The theme to set
 */
export function setThemeAttribute(theme) {
    document.documentElement.setAttribute('data-theme', theme);
}

/**
 * Starts watching for system theme changes and notifies Blazor.
 * @param {object} dotNetReference - The .NET object reference for callbacks
 */
export function watchSystemTheme(dotNetReference) {
    // Clean up any existing watcher
    stopWatchingSystemTheme();
    
    dotNetRef = dotNetReference;
    mediaQuery = window.matchMedia('(prefers-color-scheme: dark)');
    
    mediaQueryHandler = (event) => {
        if (dotNetRef) {
            const theme = event.matches ? 'dark' : 'light';
            dotNetRef.invokeMethodAsync('OnSystemThemeChanged', theme);
        }
    };
    
    // Use addEventListener for modern browsers
    if (mediaQuery.addEventListener) {
        mediaQuery.addEventListener('change', mediaQueryHandler);
    } else {
        // Fallback for older browsers
        mediaQuery.addListener(mediaQueryHandler);
    }
}

/**
 * Stops watching for system theme changes.
 */
export function stopWatchingSystemTheme() {
    if (mediaQuery && mediaQueryHandler) {
        if (mediaQuery.removeEventListener) {
            mediaQuery.removeEventListener('change', mediaQueryHandler);
        } else {
            mediaQuery.removeListener(mediaQueryHandler);
        }
    }
    mediaQuery = null;
    mediaQueryHandler = null;
    dotNetRef = null;
}
