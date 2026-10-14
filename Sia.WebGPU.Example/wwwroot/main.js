import { dotnet } from "./_framework/dotnet.js";
import { createCanvas, nextCanvasFrame, disposeCanvas } from "./canvas.js";
import { createUi } from "./ui.js";

const ui = createUi();

async function loadBinaryBase64(path) {
    const response = await fetch(path);
    if (!response.ok) {
        throw new Error(`Unable to load "${path}": ${response.status} ${response.statusText}`);
    }
    const bytes = new Uint8Array(await response.arrayBuffer());
    let binary = "";
    for (let offset = 0; offset < bytes.length; offset += 0x8000) {
        binary += String.fromCharCode(...bytes.subarray(offset, offset + 0x8000));
    }
    return btoa(binary);
}

function createErrorReporter(onError) {
    const state = { overlay: null };
    return (message) => {
        onError(message);
        if (!state.overlay) {
            state.overlay = document.createElement("pre");
            state.overlay.id = "error-overlay";
            state.overlay.setAttribute("role", "alert");
            state.overlay.setAttribute("aria-live", "assertive");
            document.body.appendChild(state.overlay);
        }

        state.overlay.append(message + "\n\n");
        state.overlay.scrollTop = state.overlay.scrollHeight;
    };
}

function formatErrorValue(value) {
    if (value instanceof Error) {
        return value.stack ?? `${value.name}: ${value.message}`;
    }
    if (typeof value === "string") {
        return value;
    }

    try {
        return JSON.stringify(value, null, 2) ?? String(value);
    } catch {
        return String(value);
    }
}

const originalConsoleError = console.error.bind(console);
const reportError = createErrorReporter(ui.callbacks.onError);
console.error = (...values) => {
    originalConsoleError(...values);
    reportError("[console.error] " + values.map(formatErrorValue).join(" "));
};

window.addEventListener("error", (e) => {
    reportError("[error] " + (e.error?.stack ?? e.message));
});

window.addEventListener("unhandledrejection", (e) => {
    reportError("[unhandledrejection] " + formatErrorValue(e.reason));
});

try {
    if (!navigator.gpu)
        throw new Error("WebGPU is unavailable. Use a browser and device with WebGPU support.");
    dotnet.withModuleConfig({ onDownloadResourceProgress: ui.callbacks.onProgress });
    const { runMain, Module, setModuleImports } = await dotnet.create();

    Module.print = console.log;
    Module.printErr = (line) => console.error("[stderr]", line);
    setModuleImports("main.js", {
        loadBinaryBase64,
        createCanvas: () => {
            ui.callbacks.onLoading("creating renderer");
            return createCanvas(ui.renderScale);
        },
        nextCanvasFrame,
        disposeCanvas,
        canvasPresentation: () => ui.state.canvas,
        shaderFormat: () => ui.state.shader,
        setTitle: (title) => {
            document.title = title;
            ui.callbacks.onReady();
        },
    });

    await runMain();
} catch (err) {
    console.error("[startup]", err);
}
