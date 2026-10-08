import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

const [runtime, assemblyPath, intrinsicPath, artifactPath] = process.argv.slice(2);
if (!artifactPath) throw new Error('Expected host runtime, shader PE, intrinsic PE, direct artifact directory.');
const { dotnet } = await import(pathToFileURL(resolve(runtime)));
const host = await dotnet.create();
const exports = await host.getAssemblyExports('CompilerHost.dll');
const entries = JSON.parse(readFileSync(resolve(artifactPath, 'entries.json')));
if (!entries.length) throw new Error('No shader fixtures were exported.');
const passed = exports.Sia.Spirv.Compiler.Validation.CompilerHost.Run(JSON.stringify({
    assembly: readFileSync(assemblyPath).toString('base64'),
    intrinsics: readFileSync(intrinsicPath).toString('base64'),
    entries: entries.map(entry => ({ ...entry,
        wgsl: readFileSync(resolve(artifactPath, entry.name + '.wgsl'), 'utf8'),
        spirv: readFileSync(resolve(artifactPath, entry.name + '.spv')).toString('base64'),
    })),
}));
if (passed !== entries.length) throw new Error('Host did not validate every fixture.');
console.log(passed + ' compiler fixtures passed in one managed host runtime.');
