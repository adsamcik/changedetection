// JavaScript module for handling browser beforeunload event
// Used by UnsavedChangesGuard component to warn about unsaved changes

let beforeUnloadHandler = null;

export function registerBeforeUnload() {
    if (beforeUnloadHandler) {
        return; // Already registered
    }
    
    beforeUnloadHandler = (e) => {
        // Modern browsers require returnValue to be set
        e.preventDefault();
        e.returnValue = '';
        return '';
    };
    
    window.addEventListener('beforeunload', beforeUnloadHandler);
}

export function unregisterBeforeUnload() {
    if (beforeUnloadHandler) {
        window.removeEventListener('beforeunload', beforeUnloadHandler);
        beforeUnloadHandler = null;
    }
}
