addToLibrary({
    $siaGraphicsCanvasBridge__deps: ['$GL'],
    $siaGraphicsCanvasBridge__postset: `
#if PTHREADS
        if (!ENVIRONMENT_IS_PTHREAD)
#endif
            globalThis.__siaGraphicsNextTurn ??= () => new Promise(resolve => setTimeout(resolve, 0));
    `,
    $siaGraphicsCanvasBridge: {
        devices: new Map(), observers: new WeakMap(), hosts: new Map(), nextToken: 0, initialized: false,
        release(id, token) {
            const record = this.devices.get(id);
            if (!record || record.token !== token) return;
            record.closed = true;
            record.port?.close();
            record.observer.records.delete(record);
            if (!record.observer.records.size)
                record.device.removeEventListener('uncapturederror', record.observer.listener);
            this.devices.delete(id);
            delete GL.offscreenCanvases[id];
        },
    },
    siaGraphicsInitCanvasWorker__sig: 'i',
    siaGraphicsInitCanvasWorker__deps: ['$siaGraphicsCanvasBridge', '$GL'],
    siaGraphicsInitCanvasWorker: () => {
#if PTHREADS
        if (!ENVIRONMENT_IS_PTHREAD) return 0;
        if (siaGraphicsCanvasBridge.initialized) return 1;
        siaGraphicsCanvasBridge.initialized = true;
        self.addEventListener('message', (event) => {
            if (event.data.siaGraphicsCanvas !== 'bind') return;
            event.stopImmediatePropagation();
            const { id, token, canvas, port, error } = event.data;
            const record = siaGraphicsCanvasBridge.devices.get(id);
            if (!record || record.token !== token) { port?.close(); return; }
            if (error) { record.error ||= error; return; }
            record.port = port;
            if (canvas) GL.offscreenCanvases[id] = { id, offscreenCanvas: canvas };
            record.ready = true;
        }, true);
        return 1;
#else
        return 0;
#endif
    },
    siaGraphicsBindCanvasMain__sig: 'vppii',
    siaGraphicsBindCanvasMain__proxy: 'async',
    siaGraphicsBindCanvasMain__deps: ['$siaGraphicsCanvasBridge', '$UTF8ToString', 'free'
#if PTHREADS
        , '$PThread'
#endif
    ],
    siaGraphicsBindCanvasMain: (selector, thread, readback, token) => {
        const id = UTF8ToString(selector).slice(1);
        const worker =
#if PTHREADS
            PThread.pthreads[thread];
#else
            undefined;
#endif
        let channel;
        try {
            if (!worker) throw new Error('Render Worker is unavailable');
            if (siaGraphicsCanvasBridge.hosts.has(id)) throw new Error('Canvas already has a render target');
            const canvas = document.getElementById(id);
            if (!(canvas instanceof HTMLCanvasElement)) throw new Error('Canvas was not found: ' + id);
            channel = new MessageChannel();
            const owner = { token, thread, port: channel.port1, canvas, context: null, image: null };
            if (readback) {
                owner.context = canvas.getContext('2d', { alpha: false });
                if (!owner.context) throw new Error('Ordinary canvas context is unavailable');
                channel.port1.onmessage = (event) => {
                    if (siaGraphicsCanvasBridge.hosts.get(id) !== owner) return;
                    const { pixels, width, height } = event.data;
                    if (canvas.width !== width || canvas.height !== height) {
                        canvas.width = width; canvas.height = height; owner.image = null;
                    }
                    owner.image ??= new ImageData(width, height);
                    owner.image.data.set(pixels);
                    owner.context.putImageData(owner.image, 0, 0);
                };
                worker.postMessage({ siaGraphicsCanvas: 'bind', id, token, port: channel.port2 }, [channel.port2]);
            } else {
                if (!canvas.transferControlToOffscreen) throw new Error('OffscreenCanvas is unavailable');
                const offscreen = canvas.transferControlToOffscreen();
                worker.postMessage({ siaGraphicsCanvas: 'bind', id, token, canvas: offscreen, port: channel.port2 }, [offscreen, channel.port2]);
            }
            siaGraphicsCanvasBridge.hosts.set(id, owner);
        } catch (error) {
            channel?.port1.close(); channel?.port2.close();
            worker?.postMessage({ siaGraphicsCanvas: 'bind', id, token, error: error.message });
        }
        finally { _free(selector); }
    },
    siaGraphicsDetachCanvasMain__sig: 'vppi',
    siaGraphicsDetachCanvasMain__proxy: 'async',
    siaGraphicsDetachCanvasMain__deps: ['$siaGraphicsCanvasBridge', '$UTF8ToString', 'free'],
    siaGraphicsDetachCanvasMain: (selector, thread, token) => {
        try {
            const id = UTF8ToString(selector).slice(1);
            const owner = siaGraphicsCanvasBridge.hosts.get(id);
            if (!owner || owner.token !== token || owner.thread !== thread) return;
            owner.port.close();
            siaGraphicsCanvasBridge.hosts.delete(id);
        }
        finally { _free(selector); }
    },
    siaGraphicsObserveDevice__sig: 'ipp',
    siaGraphicsObserveDevice__deps: ['$siaGraphicsCanvasBridge', '$UTF8ToString', '$WebGPU'],
    siaGraphicsObserveDevice: (selector, handle) => {
        const id = UTF8ToString(selector).slice(1);
        if (siaGraphicsCanvasBridge.devices.has(id)) return 0;
        const device = WebGPU.getJsObject(handle);
        let observer = siaGraphicsCanvasBridge.observers.get(device);
        if (!observer) {
            observer = { records: new Set(), error: '' };
            observer.listener = (event) => {
                for (const record of observer.records) record.error ||= event.error.message;
            };
            device.lost.then((info) => {
                observer.error = 'Device lost: ' + info.message;
                for (const record of observer.records) record.error ||= observer.error;
            });
            siaGraphicsCanvasBridge.observers.set(device, observer);
        }
        if (!observer.records.size) device.addEventListener('uncapturederror', observer.listener);
        const record = { device, observer, token: ++siaGraphicsCanvasBridge.nextToken,
            error: observer.error, closed: false, ready: false, pending: false, port: null };
        observer.records.add(record);
        siaGraphicsCanvasBridge.devices.set(id, record);
        return record.token;
    },
    siaGraphicsCanvasReady__sig: 'ip',
    siaGraphicsCanvasReady__deps: ['$siaGraphicsCanvasBridge', '$UTF8ToString'],
    siaGraphicsCanvasReady: (selector) =>
        siaGraphicsCanvasBridge.devices.get(UTF8ToString(selector).slice(1))?.ready ? 1 : 0,
    siaGraphicsCanvasUsesRgba__sig: 'i',
    siaGraphicsCanvasUsesRgba: () => navigator.gpu.getPreferredCanvasFormat() === 'rgba8unorm' ? 1 : 0,
    siaGraphicsDeviceError__sig: 'pp',
    siaGraphicsDeviceError__deps: ['$siaGraphicsCanvasBridge', '$UTF8ToString', '$stringToNewUTF8'],
    siaGraphicsDeviceError: (selector) => {
        const error = siaGraphicsCanvasBridge.devices.get(UTF8ToString(selector).slice(1))?.error;
        return error ? stringToNewUTF8(error) : 0;
    },
    siaGraphicsReadPixels__sig: 'vppiii',
    siaGraphicsReadPixels__deps: ['$siaGraphicsCanvasBridge', '$UTF8ToString', '$WebGPU', '$runtimeKeepalivePush', '$runtimeKeepalivePop'],
    siaGraphicsReadPixels: (selector, buffer, width, height, stride) => {
        const record = siaGraphicsCanvasBridge.devices.get(UTF8ToString(selector).slice(1));
        const source = WebGPU.getJsObject(buffer);
        record.pending = true;
        runtimeKeepalivePush();
        Promise.resolve().then(() => source.mapAsync(GPUMapMode.READ, 0, stride * height)).then(() => {
            try {
                if (record.closed) return;
                const mapped = new Uint8Array(source.getMappedRange(0, stride * height));
                const pixels = new Uint8Array(width * height * 4);
                for (let row = 0; row < height; row++)
                    pixels.set(mapped.subarray(row * stride, row * stride + width * 4), row * width * 4);
                for (let alpha = 3; alpha < pixels.length; alpha += 4) pixels[alpha] = 255;
                record.port.postMessage({ pixels, width, height }, [pixels.buffer]);
            } finally { source.unmap(); }
        }).catch((error) => {
            if (!record.closed) record.error ||= error.message;
        }).finally(() => { record.pending = false; runtimeKeepalivePop(); });
    },
    siaGraphicsPixelsPending__sig: 'ip',
    siaGraphicsPixelsPending__deps: ['$siaGraphicsCanvasBridge', '$UTF8ToString'],
    siaGraphicsPixelsPending: (selector) =>
        siaGraphicsCanvasBridge.devices.get(UTF8ToString(selector).slice(1))?.pending ? 1 : 0,
    siaGraphicsReleaseCanvas__sig: 'vpi',
    siaGraphicsReleaseCanvas__deps: ['$siaGraphicsCanvasBridge', '$UTF8ToString'],
    siaGraphicsReleaseCanvas: (selector, token) =>
        siaGraphicsCanvasBridge.release(UTF8ToString(selector).slice(1), token),
});
