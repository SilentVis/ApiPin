// Small browser-side helpers for ApiPin.UI. Everything else lives in C#.

let shortcutHandler = null;

export function registerShortcuts(dotnet) {
    unregisterShortcuts();
    shortcutHandler = e => {
        // Tab inserts spaces in code editors instead of moving focus.
        if (e.key === 'Tab' && !e.ctrlKey && !e.altKey && e.target.matches?.('textarea.code-input')) {
            e.preventDefault();
            document.execCommand('insertText', false, '  ');
            return;
        }

        if (!(e.ctrlKey || e.metaKey) || e.altKey) return;
        const key = e.key.toLowerCase();
        let action = null;
        if (key === 'enter') action = 'send';
        else if (key === 't' && !e.shiftKey) action = 'newTab';
        else if (key === 'w') action = 'closeTab';
        else if (key === 'tab') action = e.shiftKey ? 'prevTab' : 'nextTab';
        else if (key === 'pagedown') action = 'nextTab';
        else if (key === 'pageup') action = 'prevTab';
        if (!action) return;

        e.preventDefault();
        e.stopPropagation();
        dotnet.invokeMethodAsync('OnShortcut', action);
    };
    document.addEventListener('keydown', shortcutHandler, true);
}

export function unregisterShortcuts() {
    if (shortcutHandler) document.removeEventListener('keydown', shortcutHandler, true);
    shortcutHandler = null;
}

// Drag the splitter to resize; the position is stored as the --split CSS variable on the container.
export function initSplitter(container, handle) {
    const saved = parseFloat(loadPreference('split', ''));
    if (saved > 0) container.style.setProperty('--split', saved + '%');

    handle.addEventListener('pointerdown', down => {
        down.preventDefault();
        handle.setPointerCapture(down.pointerId);
        container.classList.add('resizing');
        let pct = null;

        const move = e => {
            const r = container.getBoundingClientRect();
            const sideBySide = container.classList.contains('split-v');
            pct = sideBySide ? (e.clientX - r.left) / r.width * 100 : (e.clientY - r.top) / r.height * 100;
            pct = Math.min(85, Math.max(15, pct));
            container.style.setProperty('--split', pct.toFixed(1) + '%');
        };
        const up = () => {
            handle.removeEventListener('pointermove', move);
            container.classList.remove('resizing');
            if (pct !== null) savePreference('split', pct.toFixed(1));
        };
        handle.addEventListener('pointermove', move);
        handle.addEventListener('pointerup', up, { once: true });
        handle.addEventListener('pointercancel', up, { once: true });
    });
}

export function loadPreference(name, fallback) {
    try {
        const value = localStorage.getItem('apipin.' + name);
        return value === null ? fallback : JSON.parse(value);
    } catch {
        return fallback;
    }
}

export function savePreference(name, value) {
    try {
        localStorage.setItem('apipin.' + name, JSON.stringify(value));
    } catch {
        // Storage unavailable: the preference just isn't remembered.
    }
}

export async function copyText(text) {
    try {
        await navigator.clipboard.writeText(text);
    } catch {
        const area = document.createElement('textarea');
        area.value = text;
        document.body.appendChild(area);
        area.select();
        document.execCommand('copy');
        area.remove();
    }
}

export function downloadFile(name, contentType, base64) {
    const bytes = Uint8Array.from(atob(base64), c => c.charCodeAt(0));
    const url = URL.createObjectURL(new Blob([bytes], { type: contentType }));
    const a = document.createElement('a');
    a.href = url;
    a.download = name;
    a.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
}

export function showMatch(container, index) {
    if (!container) return;
    const marks = container.querySelectorAll('mark');
    marks.forEach(m => m.classList.remove('current'));
    const mark = marks[index];
    if (mark) {
        mark.classList.add('current');
        mark.scrollIntoView({ block: 'center', inline: 'nearest' });
    }
}
