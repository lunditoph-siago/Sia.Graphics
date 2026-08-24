import { dotnet } from './_framework/dotnet.js';

const canvas = document.getElementById('canvas');

function getCanvasWidth() {
    return Math.max(1, Math.round(window.innerWidth));
}

function getCanvasHeight() {
    return Math.max(1, Math.round(window.innerHeight));
}

function showError(message) {
    let overlay = document.getElementById('error-overlay');

    if (!overlay) {
        overlay = document.createElement('pre');
        overlay.id = 'error-overlay';
        overlay.style.cssText = `
            position:fixed;
            right:10px;
            bottom:10px;
            max-width:600px;
            max-height:50vh;
            overflow:auto;
            margin:0;
            padding:10px;
            background:#222;
            color:#f88;
            border-radius:6px;
            font:12px monospace;
            white-space:pre-wrap;
            word-break:break-word;
            z-index:99999;
        `;
        overlay.setAttribute('role', 'alert');
        overlay.setAttribute('aria-live', 'assertive');
        document.body.appendChild(overlay);
    }

    overlay.textContent += message + '\n\n';
    overlay.scrollTop = overlay.scrollHeight;
}

function formatErrorValue(value) {
    if (value instanceof Error) {
        return value.stack ?? `${value.name}: ${value.message}`;
    }
    if (typeof value === 'string') {
        return value;
    }

    try {
        return JSON.stringify(value, null, 2) ?? String(value);
    } catch {
        return String(value);
    }
}

const originalConsoleError = console.error.bind(console);
console.error = (...values) => {
    originalConsoleError(...values);
    showError('[console.error] ' + values.map(formatErrorValue).join(' '));
};

window.addEventListener('error', (e) => {
    showError('[error] ' + (e.error?.stack ?? e.message));
});

window.addEventListener('unhandledrejection', (e) => {
    showError('[unhandledrejection] ' + (e.reason?.stack ?? e.reason));
});

try {
    const { runMain, Module, setModuleImports } = await dotnet.create();

    Module.canvas = canvas;
    Module.print = console.log;
    Module.printErr = (line) => console.error('[stderr]', line);
    setModuleImports('main.js', { getCanvasWidth, getCanvasHeight });

    await runMain();
} catch (err) {
    console.error('[startup]', err);
}
