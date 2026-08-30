#include <EGL/egl.h>
#include <EGL/eglext.h>
#include <stdint.h>

enum {
    SIA_WEBGPU_BACKEND_BROWSER_GLES = 2,
};

typedef uint32_t WGPUStatus;
extern WGPUStatus wgpuSurfacePresent(void *surface);

uint32_t siaWebGpuGetBackend(void)
{
    return SIA_WEBGPU_BACKEND_BROWSER_GLES;
}

uint32_t siaWebGpuSurfacePresent(void *surface)
{
    return wgpuSurfacePresent(surface);
}

EGLDisplay eglGetPlatformDisplay(
    EGLenum platform,
    void *nativeDisplay,
    const EGLAttrib *attributes)
{
    (void)platform;
    (void)attributes;
    return eglGetDisplay((EGLNativeDisplayType)nativeDisplay);
}

EGLSurface eglCreatePlatformWindowSurface(
    EGLDisplay display,
    EGLConfig config,
    void *nativeWindow,
    const EGLAttrib *attributes)
{
    return eglCreateWindowSurface(
        display,
        config,
        (EGLNativeWindowType)nativeWindow,
        (const EGLint *)attributes);
}
