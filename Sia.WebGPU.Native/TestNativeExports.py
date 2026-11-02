import ctypes
import json
import pathlib
import re
import sys

root = pathlib.Path(__file__).resolve().parent
rid = sys.argv[1]
directory = pathlib.Path(sys.argv[2]) / rid / 'native'
tag = re.search(r'#define WEBGPU_NATIVE_RELEASE_TAG "(v[0-9]{8}\.[0-9]{6})"', (root / '../Sia.WebGPU.Generators/Resources/webgpu.h').read_text()).group(1)
assert (directory / 'native-release.txt').read_text(encoding='utf-8-sig').strip() == tag
name = 'wgpu_native.dll' if rid.startswith('win-') else 'libwgpu_native.dylib' if rid.startswith('osx-') else 'libwgpu_native.so'
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
assert instance, 'WebGPU instance creation failed'
try:
    library.wgpuInstanceProcessEvents(instance)
finally:
    library.wgpuInstanceRelease(instance)
print(json.dumps({'Runtime': rid, 'Release': tag, 'Exports': len(symbols),
                  'PointerBytes': ctypes.sizeof(ctypes.c_void_p), 'Instance': 'PASS'}))
