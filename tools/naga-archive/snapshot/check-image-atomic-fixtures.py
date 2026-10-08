from pathlib import Path
import subprocess
root=Path('.work/naga-csharp/image-atomic');harness=['.dotnet/dotnet.exe','.work/naga-csharp/harness/bin/Release/net10.0/harness.dll'];oracle=['.work/naga-csharp/oracle-target/release/sia-naga-reference-oracle.exe'];validator=['.work/naga-csharp/spirv-tools-local/tools/Release/spirv-val.exe','--target-env','vulkan1.1']
for width in [32,64]:
 source=Path('.reference/wgpu/naga/tests/in/wgsl/atomicTexture'+('-int64' if width==64 else '')+'.wgsl')
 for label,tool in [('managed',harness),('reference',oracle)]:
  spv=root/f'{label}{width}.spv'
  for args in [tool+[source,spv],validator+[spv],harness+[spv,root/f'{label}{width}-back.wgsl'],oracle+[root/f'{label}{width}-back.wgsl'],harness+[spv,root/f'{label}{width}-back.spv'],validator+[root/f'{label}{width}-back.spv']]:
   p=subprocess.run(list(map(str,args)),capture_output=True,text=True);print(width,label,args[-1],p.returncode,p.stdout+p.stderr,flush=True)
   if p.returncode:raise SystemExit(1)
