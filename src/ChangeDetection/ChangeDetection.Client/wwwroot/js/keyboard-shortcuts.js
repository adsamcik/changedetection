// JavaScript module for capturing keyboard shortcuts and invoking Blazor callbacks

let dotNetRef = null;
let keydownHandler = null;

/**
 * Initializes the keyboard shortcut listener.
 * @param {object} dotNetReference - The .NET object reference for callbacks
 */
export function initialize(dotNetReference) {
    // Clean up any existing handler first
    if (keydownHandler) {
        document.removeEventListener('keydown', keydownHandler);
    }
    
    dotNetRef = dotNetReference;
    
    keydownHandler = (event) => {
        // Guard against disposed reference
        if (!dotNetRef) return;
        
        const key = event.key;
        const ctrlKey = event.ctrlKey || event.metaKey; // Support Cmd on Mac
        const altKey = event.altKey;
        const shiftKey = event.shiftKey;
        const tagName = event.target.tagName.toLowerCase();
        
        // Check if focus is in an input field
        const isInput = tagName === 'input' || 
                       tagName === 'textarea' || 
                       tagName === 'select' ||
                       event.target.isContentEditable;

        // Always allow Escape
        if (key === 'Escape') {
            dotNetRef.invokeMethodAsync('HandleKeyDown', key, ctrlKey, altKey, shiftKey, tagName, isInput);
            return;
        }

        // For Ctrl+K, prevent default browser behavior (opens browser search)
        if (key === 'k' && ctrlKey && !altKey && !shiftKey) {
            event.preventDefault();
            dotNetRef.invokeMethodAsync('HandleKeyDown', key, ctrlKey, altKey, shiftKey, tagName, isInput);
            return;
        }

        // For "/" when not in input, prevent default (Firefox quick find)
        if (key === '/' && !isInput) {
            event.preventDefault();
            dotNetRef.invokeMethodAsync('HandleKeyDown', key, ctrlKey, altKey, shiftKey, tagName, isInput);
            return;
        }

        // For "?" (shift+/) when not in input
        if (key === '?' && !isInput) {
            event.preventDefault();
            dotNetRef.invokeMethodAsync('HandleKeyDown', key, ctrlKey, altKey, shiftKey, tagName, isInput);
            return;
        }

        // Single letter shortcuts (n, r) - only when not in input
        if (!isInput && !ctrlKey && !altKey && (key === 'n' || key === 'r')) {
            dotNetRef.invokeMethodAsync('HandleKeyDown', key, ctrlKey, altKey, shiftKey, tagName, isInput);
            return;
        }
    };

    document.addEventListener('keydown', keydownHandler);
}

/**
 * Disposes the keyboard shortcut listener.
 */
export function dispose() {
    if (keydownHandler) {
        document.removeEventListener('keydown', keydownHandler);
        keydownHandler = null;
    }
    dotNetRef = null;
}

/**
 * Focuses an element by its ID.
 * @param {string} elementId - The ID of the element to focus
 * @returns {boolean} - True if element was found and focused
 */
export function focusElement(elementId) {
    const element = document.getElementById(elementId);
    if (element) {
        element.focus();
        // If it's an input, also select its content
        if (element.tagName.toLowerCase() === 'input' && element.select) {
            element.select();
        }
        return true;
    }
    return false;
}
