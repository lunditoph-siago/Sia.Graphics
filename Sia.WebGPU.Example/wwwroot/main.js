import { dotnet } from './_framework/dotnet.js';
import { createCanvas, nextCanvasFrame, disposeCanvas } from './canvas.js';

const canvasMode = new URLSearchParams(location.search).get('canvas') === 'normal' ? 'normal' : 'offscreen';
const shaderFormat = new URLSearchParams(location.search).get('shader') === 'spirv' ? 'spirv' : 'wgsl';
const canvasToggle = document.getElementById('canvas-toggle');
const shaderToggle = document.getElementById('shader-toggle');
canvasToggle.setAttribute('aria-pressed', String(canvasMode === 'normal'));
canvasToggle.setAttribute('aria-label', canvasMode === 'normal' ? 'Use OffscreenCanvas' : 'Use ordinary Canvas');
document.getElementById('canvas-mode').textContent = canvasMode === 'normal' ? 'Canvas' : 'OffscreenCanvas';
shaderToggle.setAttribute('aria-pressed', String(shaderFormat === 'spirv'));
shaderToggle.setAttribute('aria-label', shaderFormat === 'spirv' ? 'Use WGSL shaders' : 'Use C# SPIR-V shaders');
shaderToggle.textContent = shaderFormat === 'spirv' ? 'SPIR-V' : 'WGSL';
canvasToggle.addEventListener('click', () => {
    const url = new URL(location.href);
    url.searchParams.set('canvas', canvasMode === 'normal' ? 'offscreen' : 'normal');
    location.assign(url);
});
shaderToggle.addEventListener('click', () => {
    const url = new URL(location.href);
    url.searchParams.set('shader', shaderFormat === 'spirv' ? 'wgsl' : 'spirv');
    location.assign(url);
});

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
    if (!navigator.gpu) throw new Error('WebGPU is unavailable. Use a browser and device with WebGPU support.');
    const { runMain, Module, setModuleImports } = await dotnet.create();

    Module.print = console.log;
    Module.printErr = (line) => console.error('[stderr]', line);
    setModuleImports('main.js', {
        loadBinaryBase64,
        createCanvas: () => {
            document.getElementById('preview-controls').hidden = false;
            return createCanvas();
        },
        nextCanvasFrame, disposeCanvas,
        canvasPresentation: () => canvasMode,
        shaderFormat: () => shaderFormat,
        setTitle: title => { document.title = title; },
    });

    await runMain();
} catch (err) {
    console.error('[startup]', err);
}
