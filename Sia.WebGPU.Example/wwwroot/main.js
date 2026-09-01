import { dotnet } from './_framework/dotnet.js';

const canvas = document.getElementById('canvas');

function getCanvasWidth() {
    return Math.max(1, Math.round(window.innerWidth));
}

function getCanvasHeight() {
    return Math.max(1, Math.round(window.innerHeight));
}

async function loadBinaryBase64(path) {
    const response = await fetch(path);
    if (!response.ok) {
        throw new Error(`Unable to load '${path}': ${response.status} ${response.statusText}`);
    }
    const bytes = new Uint8Array(await response.arrayBuffer());
    let binary = '';
    for (let offset = 0; offset < bytes.length; offset += 0x8000) {
        binary += String.fromCharCode(...bytes.subarray(offset, offset + 0x8000));
    }
    return btoa(binary);
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
    let translateSpirvToWgsl = () => {
        throw new Error('This build does not contain the SPIR-V translation asset.');
    };
    try {
        const { createSpirvPolyfill } = await import('./spirv/sia-spirv-polyfill.js');
        translateSpirvToWgsl = await createSpirvPolyfill(
            new URL('./spirv/sia-spirv-naga.wasm', import.meta.url));
    } catch (err) {
        console.info('[startup] SPIR-V translator is not present in this WGSL build.', err);
    }

    Module.canvas = canvas;
    Module.print = console.log;
    Module.printErr = (line) => console.error('[stderr]', line);
    setModuleImports('main.js', { getCanvasWidth, getCanvasHeight, loadBinaryBase64 });
    setModuleImports('sia-spirv-polyfill.js', { translateSpirvToWgsl });

    await runMain();
} catch (err) {
    console.error('[startup]', err);
}
