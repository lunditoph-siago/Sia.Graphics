const parameters = new URLSearchParams(location.search);
const canvasKey = 'sia-webgpu-canvas';
const shaderKey = 'sia-webgpu-backend';
const scaleKey = 'sia-webgpu-render-scale';
export const canvasMode = (parameters.get('canvas') ?? localStorage.getItem(canvasKey)) === 'normal' ? 'normal' : 'offscreen';
export const shaderFormat = (parameters.get('shader') ?? localStorage.getItem(shaderKey)?.split('-')[1]) === 'spirv' ? 'spirv' : 'wgsl';
localStorage.setItem(canvasKey, canvasMode);
localStorage.setItem(shaderKey, `dawn-${shaderFormat}`);

for (const [name, value, key] of [['canvas', canvasMode, canvasKey], ['shader', shaderFormat, shaderKey]]) {
    for (const button of document.querySelectorAll(`[data-${name}]`)) {
        const selected = button.dataset[name] === value;
        button.classList.toggle('active', selected);
        button.setAttribute('aria-pressed', String(selected));
        button.addEventListener('click', () => {
            const next = button.dataset[name];
            if (next === value) return;
            localStorage.setItem(key, name === 'shader' ? `dawn-${next}` : next);
            const url = new URL(location.href);
            url.searchParams.set('canvas', name === 'canvas' ? next : canvasMode);
            url.searchParams.set('shader', name === 'shader' ? next : shaderFormat);
            location.assign(url);
        });
    }
}

const stage = document.getElementById('stage');
const scaleInput = document.getElementById('render-scale');
const autoButton = document.getElementById('auto-scale');
const clamp = value => Math.min(3, Math.max(1, value));
export function renderScale() {
    const saved = localStorage.getItem(scaleKey);
    if (saved && Number.isFinite(Number(saved))) return clamp(Number(saved));
    const width = Math.max(1, stage.clientWidth || innerWidth || 1);
    const height = Math.max(1, stage.clientHeight || innerHeight || 1);
    const dpr = Number.isFinite(devicePixelRatio) ? Math.max(1, devicePixelRatio) : 1;
    return clamp(Math.min((screen.width || width) * dpr / width, (screen.height || height) * dpr / height));
}
function updateScale() {
    const automatic = !localStorage.getItem(scaleKey);
    autoButton.classList.toggle('active', automatic);
    autoButton.setAttribute('aria-pressed', String(automatic));
    if (document.activeElement !== scaleInput) scaleInput.value = Math.round(renderScale() * 100) / 100;
}
scaleInput.addEventListener('change', () => {
    if (!Number.isFinite(scaleInput.valueAsNumber)) { updateScale(); return; }
    const value = clamp(scaleInput.valueAsNumber);
    localStorage.setItem(scaleKey, String(value));
    scaleInput.value = value;
    updateScale();
});
autoButton.addEventListener('click', () => { localStorage.removeItem(scaleKey); updateScale(); });
window.addEventListener('resize', updateScale);
updateScale();

const boot = document.getElementById('boot');
const log = document.getElementById('log');
let finished = false;
export function loading(message) { log.textContent = `[INIT ] sia.webgpu bootstrap\n[INFO ] dawn/webgpu · ${shaderFormat} · ${canvasMode}\n[LOAD ] ${message}`; }
export const bootModuleConfig = {
    onDownloadResourceProgress(loaded, total) { loading(`runtime resources ${loaded} / ${total}`); },
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
