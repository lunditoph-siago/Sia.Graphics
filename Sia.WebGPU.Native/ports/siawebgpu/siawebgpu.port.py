# Copyright 2026 Sia contributors.
# SPDX-License-Identifier: MIT

import os


LICENSE = 'MIT'
DESCRIPTION = 'Sia WebGPU compatibility port with Dawn/WebGPU and wgpu/GLES backends'
URL = 'https://github.com/lunditoph-siago/Sia.Graphics'

OPTIONS = {
    'backend': 'Backend implementation: dawn or wgpu. Default: dawn.',
    'archive': 'Path to the wasm32-unknown-emscripten wgpu-native archive.',
}

_VALID_OPTIONS = {
    'backend': ['dawn', 'wgpu'],
}

_opts = {
    'backend': 'dawn',
    'archive': None,
}

_port_dir = os.path.dirname(os.path.realpath(__file__))


def handle_options(options, error_handler):
    for option, value in options.items():
        if option == 'archive':
            _opts[option] = value
            continue
        value = value.lower()
        if option not in _VALID_OPTIONS:
            error_handler(f'unknown option [{option}]')
        if value not in _VALID_OPTIONS[option]:
            error_handler(
                f'[{option}] can be {_VALID_OPTIONS[option]}, got [{value}]')
        _opts[option] = value


def _wgpu_archive():
    if _opts['archive']:
        return os.path.realpath(_opts['archive'])
    override = os.environ.get('SIAWEBGPU_WGPU_ARCHIVE')
    if override:
        return os.path.realpath(override)
    return os.path.realpath(os.path.join(
        _port_dir, '..', '..', 'runtimes', 'browser-wasm', 'native',
        'libwgpu_native.a'))


def process_args(ports):
    if _opts['backend'] == 'dawn':
        return ['-DSIA_WEBGPU_BACKEND_DAWN=1']

    return [
        '-DSIA_WEBGPU_BACKEND_WGPU_GLES=1',
        '-fwasm-exceptions',
    ]


def linker_setup(ports, settings):
    if _opts['backend'] == 'dawn':
        return

    if settings.USE_WEBGPU:
        raise Exception('wgpu/GLES may not be used with -sUSE_WEBGPU=1')
    settings.MIN_WEBGL_VERSION = 2
    settings.MAX_WEBGL_VERSION = 2
    settings.FULL_ES3 = 1
    settings.WASM_LEGACY_EXCEPTIONS = 1


def get(ports, settings, shared):
    if settings.allowed_settings:
        return []

    if _opts['backend'] == 'dawn':
        return []

    archive = _wgpu_archive()
    if not os.path.isfile(archive):
        raise Exception(
            'wgpu backend requires runtimes/browser-wasm/native/'
            'libwgpu_native.a or SIAWEBGPU_WGPU_ARCHIVE')
    return [archive]


def clear(ports, settings, shared):
    pass
