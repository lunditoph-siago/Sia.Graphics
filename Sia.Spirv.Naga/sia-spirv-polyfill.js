export async function createSpirvPolyfill(wasmUrl) {
    const response = await fetch(wasmUrl);
    if (!response.ok) {
        throw new Error(`Unable to load the Naga Wasm module: ${response.status} ${response.statusText}`);
    }

    const { instance } = await WebAssembly.instantiate(await response.arrayBuffer());
    const api = instance.exports;
    const requiredExports = [
        'memory',
        'sia_spirv_abi_version',
        'sia_spirv_context_create',
        'sia_spirv_context_destroy',
        'sia_spirv_context_abi',
        'sia_spirv_context_resize_input',
        'sia_spirv_context_translate',
    ];
    for (const name of requiredExports) {
        if (!(name in api)) {
            throw new Error(`The Naga Wasm module is missing the '${name}' export.`);
        }
    }

    if (api.sia_spirv_abi_version() !== 1) {
        throw new Error(`Unsupported Sia SPIR-V ABI version '${api.sia_spirv_abi_version()}'.`);
    }

    const context = api.sia_spirv_context_create();
    if (!context) {
        throw new Error('Creating the Sia SPIR-V translation context failed.');
    }
    const decoder = new TextDecoder();
    const translate = (spirv) => {
        const bytes = Uint8Array.from(spirv);
        const resizeStatus = api.sia_spirv_context_resize_input(context, bytes.byteLength);
        if (resizeStatus !== 0) {
            throw new Error('Allocating the Naga input buffer failed.');
        }

        let abi = readAbi(api, context);
        new Uint8Array(api.memory.buffer, abi.inputPointer, abi.inputLength).set(bytes);

        const status = api.sia_spirv_context_translate(context);
        abi = readAbi(api, context);
        const output = new Uint8Array(
            api.memory.buffer,
            abi.outputPointer,
            abi.outputLength);
        const text = decoder.decode(output);
        if (status !== 0) {
            throw new Error(text);
        }
        return text;
    };
    translate.dispose = () => api.sia_spirv_context_destroy(context);
    return translate;
}

function readAbi(api, context) {
    const pointer = api.sia_spirv_context_abi(context);
    if (!pointer) {
        throw new Error('Reading the Sia SPIR-V ABI descriptor failed.');
    }
    const view = new DataView(api.memory.buffer, pointer, 32);
    const version = view.getUint32(0, true);
    const structSize = view.getUint32(4, true);
    if (version !== 1 || structSize < 32) {
        throw new Error(`Unsupported Sia SPIR-V ABI descriptor '${version}/${structSize}'.`);
    }
    return {
        status: view.getInt32(8, true),
        inputPointer: view.getUint32(16, true),
        inputLength: view.getUint32(20, true),
        outputPointer: view.getUint32(24, true),
        outputLength: view.getUint32(28, true),
    };
}
