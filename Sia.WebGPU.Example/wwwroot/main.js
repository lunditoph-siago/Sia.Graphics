import { dotnet } from './_framework/dotnet.js';

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
        document.body.appendChild(overlay);
    }

    overlay.textContent += message + '\n\n';
}

window.addEventListener('error', (e) => {
    showError('[error] ' + (e.error?.stack ?? e.message));
});

window.addEventListener('unhandledrejection', (e) => {
    showError('[unhandledrejection] ' + (e.reason?.stack ?? e.reason));
});

try {
    const { runMain, Module } = await dotnet.create();

    Module.canvas = document.getElementById('canvas');
    Module.print = console.log;

    Module.printErr = (line) => {
        console.error(line);
        showError('[stderr] ' + line);
    };

    await runMain();
} catch (err) {
    console.error(err);
    showError('[startup] ' + (err?.stack ?? err));
}
