// Small browser-side helpers for ApiPin.UI. Everything else lives in C#.

let shortcutHandler = null;

export function registerShortcuts(dotnet) {
    unregisterShortcuts();
    shortcutHandler = e => {
        // Tab inserts spaces in code editors instead of moving focus.
        if (suggestState) return; // the {{variable}} suggestion list handles its own keys
        if (e.key === 'Tab' && !e.ctrlKey && !e.altKey && e.target.matches?.('textarea.code-input')) {
            e.preventDefault();
            document.execCommand('insertText', false, '  ');
            return;
        }

        const key = e.key.toLowerCase();
        let action = null;

        // Alt alternatives for browsers, which keep Ctrl+T / Ctrl+W / Ctrl+Tab for themselves.
        if (e.altKey && !e.ctrlKey && !e.metaKey && !e.shiftKey) {
            if (e.code === 'KeyT') action = 'newTab';
            else if (e.code === 'KeyW') action = 'closeTab';
        }
        else if (!(e.ctrlKey || e.metaKey) || e.altKey) return;
        else if (key === 'enter') action = 'send';
        else if (key === 's') action = e.shiftKey ? 'saveAs' : 'save';
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

// ---------- {{variable}} inputs: highlight backdrop alignment and suggestions ----------

const mirrored = ['fontFamily', 'fontSize', 'fontWeight', 'fontStyle', 'lineHeight', 'letterSpacing', 'wordSpacing', 'tabSize',
    'paddingTop', 'paddingRight', 'paddingBottom', 'paddingLeft', 'borderTopWidth', 'borderRightWidth', 'borderBottomWidth', 'borderLeftWidth',
    'textIndent', 'textTransform'];

// Copies the input's text metrics to the backdrop so highlighted {{refs}} sit exactly under the typed text.
export function attachVarHost(host) {
    const field = host?.querySelector('input, textarea');
    const backdrop = host?.querySelector('.var-backdrop');
    if (!field || !backdrop || host.dataset.attached) return;
    host.dataset.attached = '1';

    const copy = () => {
        const style = getComputedStyle(field);
        for (const p of mirrored) backdrop.style[p] = style[p];
        backdrop.style.borderStyle = 'solid';
        backdrop.style.borderColor = 'transparent';
    };
    const sync = () => {
        backdrop.scrollLeft = field.scrollLeft;
        backdrop.scrollTop = field.scrollTop;
    };
    copy();
    field.addEventListener('scroll', sync);
    field.addEventListener('input', () => requestAnimationFrame(sync));
    field.addEventListener('keyup', sync);
    field.addEventListener('focus', copy);
    field.addEventListener('input', () => suggest(host, field));
    field.addEventListener('keydown', e => suggestKey(e, host, field), true);
    field.addEventListener('blur', () => setTimeout(() => { if (suggestState?.field === field) closeSuggest(); }, 150));
    new ResizeObserver(sync).observe(field);
}

let suggestState = null;

function suggest(host, field) {
    const before = field.value.slice(0, field.selectionStart ?? field.value.length);
    const match = /\{\{\s*([^{}\s]*)$/.exec(before);
    if (!match) return closeSuggest();

    let names = [];
    try { names = JSON.parse(host.dataset.vars || '[]'); } catch { }
    const typed = match[1].toLowerCase();
    const items = names.filter(n => n.toLowerCase().includes(typed))
        .sort((a, b) => (a.toLowerCase().startsWith(typed) ? 0 : 1) - (b.toLowerCase().startsWith(typed) ? 0 : 1) || a.localeCompare(b))
        .slice(0, 12);
    if (items.length === 0) return closeSuggest();

    let list = suggestState?.list;
    if (!list) {
        list = document.createElement('div');
        list.className = 'var-suggest';
        list.addEventListener('mousedown', e => {
            const item = e.target.closest('[data-name]');
            if (!item || !suggestState) return;
            e.preventDefault();
            accept(item.dataset.name);
        });
        document.body.appendChild(list);
    }
    suggestState = { list, field, items, index: 0, start: before.length - match[0].length };
    render();

    const r = host.getBoundingClientRect();
    list.style.left = Math.min(r.left, window.innerWidth - 280) + 'px';
    const below = r.bottom + 2;
    list.style.top = (below + 240 > window.innerHeight ? Math.max(4, r.top - 2 - list.offsetHeight) : below) + 'px';
}

function render() {
    const { list, items, index } = suggestState;
    list.innerHTML = '';
    items.forEach((name, i) => {
        const el = document.createElement('div');
        el.className = 'var-suggest-item' + (i === index ? ' active' : '') + (name.startsWith('$') ? ' dyn' : '');
        el.dataset.name = name;
        el.textContent = name;
        list.appendChild(el);
    });
}

function suggestKey(e, host, field) {
    if (!suggestState || suggestState.field !== field) return;
    const s = suggestState;
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
        s.index = (s.index + (e.key === 'ArrowDown' ? 1 : -1) + s.items.length) % s.items.length;
        render();
    } else if (e.key === 'Enter' || e.key === 'Tab') {
        accept(s.items[s.index]);
    } else if (e.key === 'Escape') {
        closeSuggest();
    } else {
        return;
    }
    e.preventDefault();
    e.stopPropagation();
}

function accept(name) {
    const { field, start } = suggestState;
    const caret = field.selectionStart ?? field.value.length;
    let after = field.value.slice(caret);
    after = after.replace(/^[^{}\s]*\s*\}\}/, ''); // replace a reference that was already closed
    const inserted = '{{' + name + '}}';
    field.value = field.value.slice(0, start) + inserted + after;
    const pos = start + inserted.length;
    field.setSelectionRange(pos, pos);
    closeSuggest();
    field.dispatchEvent(new Event('input', { bubbles: true }));
    field.focus();
}

function closeSuggest() {
    suggestState?.list.remove();
    suggestState = null;
}
