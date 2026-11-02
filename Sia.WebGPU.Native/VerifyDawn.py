import ctypes
import json
import pathlib
import re
import sys

root = pathlib.Path(__file__).resolve().parent
rid = sys.argv[1]
directory = pathlib.Path(sys.argv[2]) / rid / 'native'
manifest = json.loads((root / 'Dawn.json').read_text(encoding='utf-8-sig'))
assert (directory / 'dawn-release.txt').read_text(encoding='utf-8-sig').strip() == manifest['Tag']
name = 'webgpu_dawn.dll' if rid.startswith('win-') else 'libwebgpu_dawn.dylib' if rid.startswith('osx-') else 'libwebgpu_dawn.so'
library = ctypes.CDLL(str((directory / name).resolve()))
header = (root / '../Sia.WebGPU.Generators/Resources/webgpu.h').read_text()
symbols = re.findall(r'^WGPU_EXPORT[^;\n]*\b(wgpu[A-Z]\w*)\(', header, re.M)
assert len(symbols) > 200
for symbol in symbols:
    getattr(library, symbol)
library.wgpuCreateInstance.argtypes = [ctypes.c_void_p]
library.wgpuCreateInstance.restype = ctypes.c_void_p
library.wgpuInstanceProcessEvents.argtypes = [ctypes.c_void_p]
library.wgpuInstanceRelease.argtypes = [ctypes.c_void_p]
instance = library.wgpuCreateInstance(None)
assert instance, 'Dawn instance creation failed'
try:
    library.wgpuInstanceProcessEvents(instance)
finally:
    library.wgpuInstanceRelease(instance)
print(json.dumps({'Runtime': rid, 'Release': manifest['Tag'], 'Exports': len(symbols),
                  'PointerBytes': ctypes.sizeof(ctypes.c_void_p), 'Instance': 'PASS'}))
