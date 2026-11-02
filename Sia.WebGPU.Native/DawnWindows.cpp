#include <dawn/native/DawnNative.h>
#include <windows.h>
#include <cstring>
#include <string>

extern "C" WGPUInstance siaDawnCreateInstance(WGPUInstanceDescriptor const* descriptor) {
    try {
        WGPUInstanceDescriptor value = WGPU_INSTANCE_DESCRIPTOR_INIT;
        if (descriptor) value = *descriptor;
        dawn::native::DawnInstanceDescriptor extensions;
        for (auto chain = value.nextInChain; chain; chain = chain->next) {
            if (chain->sType == static_cast<WGPUSType>(extensions.sType)) {
                return wgpuCreateInstance(descriptor);
            }
        }
        std::wstring systemDirectory(GetSystemDirectoryW(nullptr, 0), L'\0');
        auto length = GetSystemDirectoryW(systemDirectory.data(), static_cast<UINT>(systemDirectory.size()));
        if (length == 0 || length >= systemDirectory.size()) return nullptr;
        systemDirectory.resize(length);
        systemDirectory += L'\\';
        auto bytes = WideCharToMultiByte(CP_UTF8, 0, systemDirectory.data(), static_cast<int>(systemDirectory.size()), nullptr, 0, nullptr, nullptr);
        if (bytes == 0) return nullptr;
        std::string path(bytes, '\0');
        if (!WideCharToMultiByte(CP_UTF8, 0, systemDirectory.data(), static_cast<int>(systemDirectory.size()), path.data(), bytes, nullptr, nullptr)) return nullptr;
        std::string_view paths[] = {path};
        extensions.additionalRuntimeSearchPaths = paths;
        extensions.nextInChain = reinterpret_cast<wgpu::ChainedStruct const*>(value.nextInChain);
        value.nextInChain = reinterpret_cast<WGPUChainedStruct*>(&extensions);
        return wgpuCreateInstance(&value);
    } catch (...) {
        return nullptr;
    }
}

extern "C" WGPUProc siaDawnGetProcAddress(WGPUStringView name) {
    auto length = name.length == WGPU_STRLEN ? (name.data ? std::strlen(name.data) : 0) : name.length;
    if (length == sizeof("wgpuCreateInstance") - 1 && name.data && std::memcmp(name.data, "wgpuCreateInstance", length) == 0) {
        return reinterpret_cast<WGPUProc>(&siaDawnCreateInstance);
    }
    return wgpuGetProcAddress(name);
}
