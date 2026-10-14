const url = new URL(location.href);
const parameters = url.searchParams;
const canvasKey = 'sia-webgpu-canvas';
const shaderKey = 'sia-webgpu-backend';
const scaleKey = 'sia-webgpu-render-scale';
export const canvasMode =
    (parameters.get('canvas') ?? localStorage.getItem(canvasKey)) === 'normal'
        ? 'normal'
        : 'offscreen';
export const shaderFormat =
    (parameters.get('shader') ?? localStorage.getItem(shaderKey)?.split('-')[1]) === 'spirv'
        ? 'spirv'
        : 'wgsl';

const modeUrl = (source, name, value) => {
    const target = new URL(source);
    target.searchParams.set(name, value);
    return target;
};
const select = (button, selected) => {
    button.classList.toggle('active', selected);
    button.setAttribute('aria-pressed', String(selected));
};
const bindMode = (name, value, key, serialize = String) => {
    localStorage.setItem(key, serialize(value));
    parameters.set(name, value);
    document.querySelectorAll(`[data-${name}]`).forEach((button) => {
        select(button, button.dataset[name] === value);
        button.addEventListener('click', () => {
            const next = button.dataset[name];
            if (next === value) return;
            localStorage.setItem(key, serialize(next));
            location.assign(modeUrl(url, name, next));
        });
    });
};
bindMode('canvas', canvasMode, canvasKey);
bindMode('shader', shaderFormat, shaderKey, (value) => `dawn-${value}`);

const stage = document.getElementById('canvas-host');
const scaleInput = document.getElementById('render-scale');
const autoButton = document.getElementById('auto-scale');
const clamp = (value) => Math.min(3, Math.max(1, value));
const computeScale = (saved, { width, height, screenWidth, screenHeight, pixelRatio }) => {
    if (saved && Number.isFinite(Number(saved))) return clamp(Number(saved));
    const dpr = Number.isFinite(pixelRatio) ? Math.max(1, pixelRatio) : 1;
    return clamp(
        Math.min(((screenWidth || width) * dpr) / width, ((screenHeight || height) * dpr) / height)
    );
};
export function renderScale() {
    const width = Math.max(1, stage.clientWidth || innerWidth || 1);
    const height = Math.max(1, stage.clientHeight || innerHeight || 1);
    return computeScale(localStorage.getItem(scaleKey), {
        width,
        height,
        screenWidth: screen.width,
        screenHeight: screen.height,
        pixelRatio: devicePixelRatio,
    });
}
function updateScale() {
    select(autoButton, !localStorage.getItem(scaleKey));
    if (document.activeElement !== scaleInput)
        scaleInput.value = Math.round(renderScale() * 100) / 100;
}
scaleInput.addEventListener('change', () => {
    if (!Number.isFinite(scaleInput.valueAsNumber)) {
        updateScale();
        return;
    }
    const value = clamp(scaleInput.valueAsNumber);
    localStorage.setItem(scaleKey, String(value));
    scaleInput.value = value;
    updateScale();
});
autoButton.addEventListener('click', () => {
    localStorage.removeItem(scaleKey);
    updateScale();
});
window.addEventListener('resize', updateScale);
updateScale();

const boot = document.getElementById('boot');
const log = document.getElementById('log');
let finished = false;
const bootMessage = (message) =>
    [
        '[INIT ] sia.webgpu bootstrap',
        `[INFO ] dawn/webgpu · ${shaderFormat} · ${canvasMode}`,
        `[LOAD ] ${message}`,
    ].join('\n');
export function loading(message) {
    log.textContent = bootMessage(message);
}
export const bootModuleConfig = {
    onDownloadResourceProgress(loaded, total) {
        loading(`runtime resources ${loaded} / ${total}`);
    },
};
export function ready() {
    if (finished) return;
    finished = true;
    loading('first frame rendered');
    boot.classList.add('collapsed');
    document.getElementById('bar').classList.add('visible');
    stage.classList.add('visible');
}
export function failed(message) {
    finished = true;
    log.textContent += `\n[FAIL ] ${message}`;
    boot.classList.remove('collapsed');
}
loading('establishing threaded runtime');
