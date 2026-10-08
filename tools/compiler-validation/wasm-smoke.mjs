import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

const [loader, runtime, assemblyPath, intrinsicPath, artifactPath] = process.argv.slice(2);
if (!artifactPath) throw new Error('Expected loader, runtime, shader PE, intrinsic PE, direct artifact directory.');
const { createShaderCompiler } = await import(pathToFileURL(resolve(loader)));
const api = await createShaderCompiler(pathToFileURL(resolve(runtime)).href);
const assembly = readFileSync(assemblyPath), intrinsics = readFileSync(intrinsicPath);
const entries = JSON.parse(readFileSync(resolve(artifactPath, 'entries.json')));
if (!entries.length) throw new Error('No shader fixtures were exported.');
for (const entry of entries) {
    const wgsl = api.ilToWgsl(assembly, entry.token, intrinsics);
    const spirv = api.ilToSpirv(assembly, entry.token, intrinsics);
    if (wgsl !== readFileSync(resolve(artifactPath, entry.name + '.wgsl'), 'utf8')
        || !Buffer.from(spirv).equals(readFileSync(resolve(artifactPath, entry.name + '.spv'))))
        throw new Error(entry.name + ': Wasm/native output mismatch');
    const translated = api.spirvToWgsl(spirv);
    if (!api.wgslToSpirv(translated).length) throw new Error(entry.name + ': empty translated module');
    console.log('PASS ' + entry.name);
}
console.log(entries.length + ' compiler Wasm fixtures passed.');
