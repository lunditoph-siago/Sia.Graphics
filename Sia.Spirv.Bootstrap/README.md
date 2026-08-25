# Sia SPIR-V bootstrap

This .NET tool performs the one-time registration required before a stock SDK
knows the independent `spirv-tools` workload ID.

```bash
dotnet tool install --global Sia.Spirv.Bootstrap --version 0.1.0-preview.1
dotnet spirv install
```

The install command detects the SDK selected in the current directory, writes
its matching baseline manifest, and invokes `dotnet workload install
spirv-tools`. Windows x64 and Linux x64 build hosts are supported. Use
`dotnet spirv install --source <feed>` for a private mirror or local package
source.

Run `dotnet spirv bootstrap` to register only the manifest without installing
the workload packs.
