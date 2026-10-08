from pathlib import Path
import re,json,subprocess
root=Path('.work/naga-csharp/storage-formats');root.mkdir(exist_ok=True)
source=Path('.reference/wgpu/naga/src/front/wgsl/parse/conv.rs').read_text();section=source[source.index('pub fn map_storage_format'):source.index('pub fn map_derivative')];formats=re.findall(r'"([a-z0-9]+)" => Sf::',section);records=[]
for fmt in formats:
 component='u64' if fmt=='r64uint' else 'u32' if fmt.endswith('uint') else 'i32' if fmt.endswith('sint') else 'f32'
 path=root/(fmt+'.wgsl');path.write_text(f'@group(0) @binding(0) var image:texture_storage_2d<{fmt},read_write>; @compute @workgroup_size(1) fn main(){{textureStore(image,vec2i(0),vec4<{component}>({component}(1)));let value=textureLoad(image,vec2i(0));}}')
 row=dict(format=fmt,component=component)
 for label,tool in [('managed',['.dotnet/dotnet.exe','.work/naga-csharp/harness/bin/Release/net10.0/harness.dll']),('reference',['.work/naga-csharp/oracle-target/release/sia-naga-reference-oracle.exe'])]:
  spv=root/(fmt+'-'+label+'.spv');p=subprocess.run(tool+[str(path),str(spv)],capture_output=True,text=True);row[label]=dict(code=p.returncode,message=p.stdout+p.stderr)
  if p.returncode==0:
   v=subprocess.run(['.work/naga-csharp/spirv-tools-local/tools/Release/spirv-val.exe','--target-env','vulkan1.1',str(spv)],capture_output=True,text=True);row[label+'_validation']=dict(code=v.returncode,message=v.stdout+v.stderr)
 records.append(row)
(root/'baseline.json').write_text(json.dumps(records,indent=2));print('formats',len(formats));print('managed',sum(r['managed']['code']==0 for r in records));print('reference',sum(r['reference']['code']==0 for r in records));print('reference invalid',[(r['format'],r['reference_validation']['message']) for r in records if r.get('reference_validation',{}).get('code',0)!=0])
