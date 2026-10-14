#include <pthread.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

extern int siaGraphicsInitCanvasWorker(void);
extern void siaGraphicsBindCanvasMain(char *selector, uintptr_t thread, int readback, int token);
extern void siaGraphicsDetachCanvasMain(char *selector, uintptr_t thread, int token);

static char *siaGraphicsCopySelector(const char *selector)
{
    size_t bytes = strlen(selector) + 1;
    char *copy = malloc(bytes);
    if (copy) memcpy(copy, selector, bytes);
    return copy;
}

int siaGraphicsBindCanvas(const char *selector, uintptr_t thread, int readback, int token)
{
    char *copy = siaGraphicsCopySelector(selector);
    if (!copy) return 1;
    siaGraphicsBindCanvasMain(copy, thread, readback, token);
    return 0;
}

int siaGraphicsDetachCanvas(const char *selector, uintptr_t thread, int token)
{
    char *copy = siaGraphicsCopySelector(selector);
    if (!copy) return 1;
    siaGraphicsDetachCanvasMain(copy, thread, token);
    return 0;
}

uintptr_t siaGraphicsCanvasThread(void)
{
    if (!siaGraphicsInitCanvasWorker()) return 0;
    return (uintptr_t)pthread_self();
}

void siaGraphicsFree(void *pointer) { free(pointer); }
