const modes = {
    canvas: { key: "sia-webgpu-canvas", serialize: String },
    shader: { key: "sia-webgpu-backend", serialize: (value) => `dawn-${value}` },
};
const scaleKey = "sia-webgpu-render-scale";
const clamp = (value) => Math.min(3, Math.max(1, value));
const readScale = (value) =>
    value && Number.isFinite(Number(value)) ? clamp(Number(value)) : null;
const computeScale = (scale, { width, height, screenWidth, screenHeight, pixelRatio }) => {
    if (scale !== null) return scale;
    const dpr = Number.isFinite(pixelRatio) ? Math.max(1, pixelRatio) : 1;
    return clamp(
        Math.min(((screenWidth || width) * dpr) / width, ((screenHeight || height) * dpr) / height)
    );
};
const navigate = ({ canvas, shader }) => {
    const url = new URL(location.href);
    url.searchParams.set("canvas", canvas);
    url.searchParams.set("shader", shader);
    location.assign(url);
};
const setSelected = (button, selected) => {
    button.classList.toggle("active", selected);
    button.setAttribute("aria-pressed", String(selected));
};

export function createUi({ onModeChange = navigate } = {}) {
    const abort = new AbortController();
    const listen = (target, name, callback) =>
        target.addEventListener(name, callback, { signal: abort.signal });
    const parameters = new URLSearchParams(location.search);
    const state = {
        canvas:
            (parameters.get("canvas") ?? localStorage.getItem(modes.canvas.key)) === "normal"
                ? "normal"
                : "offscreen",
        shader:
            (parameters.get("shader") ?? localStorage.getItem(modes.shader.key)?.split("-")[1]) ===
            "spirv"
                ? "spirv"
                : "wgsl",
        scale: readScale(localStorage.getItem(scaleKey)),
        phase: "loading",
        visible: false,
        message: "establishing threaded runtime",
        errors: [],
    };
    const view = {
        stage: document.getElementById("canvas-host"),
        bar: document.getElementById("bar"),
        boot: document.getElementById("boot"),
        log: document.getElementById("log"),
        scale: document.getElementById("render-scale"),
        auto: document.getElementById("auto-scale"),
        buttons: [...document.querySelectorAll("[data-canvas], [data-shader]")],
    };
    const renderScale = () =>
        computeScale(state.scale, {
            width: Math.max(1, view.stage.clientWidth || innerWidth || 1),
            height: Math.max(1, view.stage.clientHeight || innerHeight || 1),
            screenWidth: screen.width,
            screenHeight: screen.height,
            pixelRatio: devicePixelRatio,
        });
    const render = () => {
        view.buttons.forEach((button) => {
            const mode = "canvas" in button.dataset ? "canvas" : "shader";
            setSelected(button, button.dataset[mode] === state[mode]);
        });
        setSelected(view.auto, state.scale === null);
        if (document.activeElement !== view.scale)
            view.scale.value = Math.round(renderScale() * 100) / 100;
        view.log.textContent = [
            "[INIT ] sia.webgpu bootstrap",
            `[INFO ] dawn/webgpu · ${state.shader} · ${state.canvas}`,
            `[LOAD ] ${state.message}`,
            ...state.errors.map((message) => `[FAIL ] ${message}`),
        ].join("\n");
        view.boot.classList.toggle("collapsed", state.phase === "ready");
        view.bar.classList.toggle("visible", state.visible);
        view.stage.classList.toggle("visible", state.visible);
    };
    const update = (patch) => {
        Object.assign(state, patch);
        render();
    };
    const callbacks = {
        onModeChange(mode, value) {
            if (value === state[mode]) return;
            localStorage.setItem(modes[mode].key, modes[mode].serialize(value));
            update({ [mode]: value });
            onModeChange(state);
        },
        onScaleChange(value) {
            if (Number.isFinite(value)) {
                state.scale = clamp(value);
                localStorage.setItem(scaleKey, String(state.scale));
                view.scale.value = state.scale;
            }
            render();
        },
        onAutoScale() {
            localStorage.removeItem(scaleKey);
            update({ scale: null });
        },
        onStorage({ key }) {
            if (key === scaleKey || key === null)
                update({ scale: readScale(localStorage.getItem(scaleKey)) });
        },
        onLoading(message) {
            update({ message });
        },
        onProgress(loaded, total) {
            update({ message: `runtime resources ${loaded} / ${total}` });
        },
        onReady() {
            if (state.phase !== "loading") return;
            update({ phase: "ready", visible: true, message: "first frame rendered" });
        },
        onError(message) {
            state.errors.push(message);
            update({ phase: "failed" });
        },
        onResize: render,
    };

    Object.entries(modes).forEach(([mode, { key, serialize }]) => {
        localStorage.setItem(key, serialize(state[mode]));
        document.querySelectorAll(`[data-${mode}]`).forEach((button) => {
            listen(button, "click", () => callbacks.onModeChange(mode, button.dataset[mode]));
        });
    });
    listen(view.scale, "change", () => callbacks.onScaleChange(view.scale.valueAsNumber));
    listen(view.auto, "click", callbacks.onAutoScale);
    listen(window, "resize", callbacks.onResize);
    listen(window, "storage", callbacks.onStorage);
    render();
    return { state, callbacks, renderScale, dispose: () => abort.abort() };
}
