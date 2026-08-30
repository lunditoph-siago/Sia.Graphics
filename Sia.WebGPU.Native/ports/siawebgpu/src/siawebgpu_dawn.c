#include <stdint.h>

enum {
    SIA_WEBGPU_BACKEND_BROWSER_WEBGPU = 1,
    SIA_WEBGPU_STATUS_SUCCESS = 1,
};

uint32_t siaWebGpuGetBackend(void)
{
    return SIA_WEBGPU_BACKEND_BROWSER_WEBGPU;
}

uint32_t siaWebGpuSurfacePresent(void *surface)
{
    (void)surface;
    return SIA_WEBGPU_STATUS_SUCCESS;
}
