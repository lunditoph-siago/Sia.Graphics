// Pass the URL of this compiler bundle's _framework/dotnet.js. Each loader owns
// its runtime; callers can retain it across shader translations/compilations.
export async function createShaderCompiler(runtimeUrl) {
    const { dotnet } = await import(runtimeUrl);
    const runtime = await dotnet.create();
    const exports = await runtime.getAssemblyExports('Sia.Spirv.Compiler.Browser.dll');
    const api = exports.Sia.Spirv.Compiler.Browser.ShaderCompiler;
    return {
        spirvToWgsl: bytes => api.SpirvToWgsl(encode(bytes)),
        wgslToSpirv: source => decode(api.WgslToSpirv(source)),
        ilToWgsl: (assembly, token, intrinsics = new Uint8Array()) => api.IlToWgsl(encode(assembly), token, encode(intrinsics)),
        ilToSpirv: (assembly, token, intrinsics = new Uint8Array()) => decode(api.IlToSpirv(encode(assembly), token, encode(intrinsics))),
    };
}

export async function createSpirvPolyfill(runtimeUrl) {
    return (await createShaderCompiler(runtimeUrl)).spirvToWgsl;
}

function encode(bytes) {
    const data = bytes instanceof Uint8Array ? bytes : new Uint8Array(bytes);
    let text = '';
    for (let i = 0; i < data.length; i += 8192) text += String.fromCharCode(...data.subarray(i, i + 8192));
    return btoa(text);
}

function decode(text) {
    return Uint8Array.from(atob(text), character => character.charCodeAt(0));
}
