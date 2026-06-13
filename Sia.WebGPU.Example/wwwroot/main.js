import { dotnet } from './_framework/dotnet.js';

const { runMain, Module } = await dotnet.create();
Module.canvas = document.getElementById('canvas');

await runMain();
