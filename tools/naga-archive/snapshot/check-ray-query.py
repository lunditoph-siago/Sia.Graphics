from pathlib import Path
import ctypes as C,json,re,subprocess
from vulkan_compute import Vulkan,Features2,S,head,u,p,i

root=Path('.work/naga-csharp/ray-query');root.mkdir(exist_ok=True)
harness=['.dotnet/dotnet.exe','.work/naga-csharp/harness/bin/Release/net10.0/harness.dll']
oracle=['.work/naga-csharp/oracle-target/release/sia-naga-reference-oracle.exe']
validator=['.work/naga-csharp/spirv-tools-local/tools/Release/spirv-val.exe','--target-env','vulkan1.1']
rows=[]
def run(label,args,accepted=True,contains=None):
    result=subprocess.run(list(map(str,args)),capture_output=True,text=True,timeout=60)
    message=result.stdout+result.stderr
    passed=(result.returncode==0)==accepted and (contains is None or contains in message)
    rows.append(dict(check=label,passed=passed,code=result.returncode,message=message))
    (root/'checks.json').write_text(json.dumps(rows,indent=2))
    if not passed:raise RuntimeError(label+': '+message)

source_text=Path('Sia.Graphics/Sia.Spirv.Naga.Tests/RayQueryTests.cs').read_text()
source=re.search(r'internal const string Source = """\n(.*?)\n        """;',source_text,re.S).group(1)
fixtures=[(name,Path('.reference/wgpu/naga/tests/in/wgsl')/(name+'.wgsl'),'o=0.25' if name=='overrides-ray-query' else 'unresolved',False)
    for name in ['ray-query','aliased-ray-query','ray-query-no-init-tracking','overrides-ray-query']]
for name,text,committed in [
    ('operations',source,True),
    ('candidate-only',source.replace('return rayQueryGetCommittedIntersection(handle);','return rayQueryGetCandidateIntersection(handle);').replace('rayQueryGenerateIntersection(handle, 0.5);','rayQueryConfirmIntersection(handle);'),False),
    ('vertex-committed',source.replace('enable wgpu_ray_query;','enable wgpu_ray_query; enable wgpu_ray_query_vertex_return;').replace('acceleration_structure','acceleration_structure<vertex_return>').replace('var query: ray_query;','var query: ray_query<vertex_return>;').replace('_ = rayQueryProceed(handle);','_ = getCommittedHitVertexPositions(handle);'),True),
    ('vertex-candidate',source.replace('enable wgpu_ray_query;','enable wgpu_ray_query; enable wgpu_ray_query_vertex_return;').replace('acceleration_structure','acceleration_structure<vertex_return>').replace('var query: ray_query;','var query: ray_query<vertex_return>;').replace('_ = rayQueryProceed(handle);','_ = getCandidateHitVertexPositions(handle);'),True),
    ('descriptor-array',source.replace('enable wgpu_ray_query;','enable wgpu_ray_query; enable wgpu_binding_array;').replace('var scene: acceleration_structure;','var scene: binding_array<acceleration_structure,4>;').replace('trace(scene)','trace(scene[0])'),True),
    ('nonuniform-descriptor-array',source.replace('enable wgpu_ray_query;','enable wgpu_ray_query; enable wgpu_binding_array;').replace('var scene: acceleration_structure;','var scene: binding_array<acceleration_structure,4>;').replace('fn main()','fn main(@builtin(global_invocation_id) id:vec3u)').replace('trace(scene)','trace(scene[id.x%4u])'),True),
]:
    path=root/(name+'.wgsl');path.write_text(text);fixtures.append((name,path,'7=0.25',committed))

for name,path,values,committed in fixtures:
    # The corpus candidate fixture contains Generate, whose safety guard reads
    # the current committed distance during traversal.
    committed=committed or name in ['ray-query','aliased-ray-query','ray-query-no-init-tracking']
    spv=root/(name+'-managed.spv');canonical=root/(name+'-canonical.wgsl');back=root/(name+'-back.spv');wgsl=root/(name+'-back.wgsl')
    reference_panic=name=='nonuniform-descriptor-array'
    run(name+'-reference-input',oracle+[path],not reference_panic,'unreachable' if reference_panic else None)
    run(name+'-managed-spv',harness+[path,spv,values]);run(name+'-native',validator+[spv])
    run(name+'-canonical',harness+[path,canonical]);run(name+'-reference-canonical',oracle+[canonical],not reference_panic,'unreachable' if reference_panic else None)
    run(name+'-reemit',harness+[spv,back]);run(name+'-native-reemit',validator+[back])
    run(name+'-wgsl-back',harness+[spv,wgsl],not committed,'traversal' if committed else None)
    if not committed:
        run(name+'-reference-back',oracle+[wgsl]);run(name+'-reference-recompile',oracle+[wgsl,root/(name+'-reference-back.spv')]);run(name+'-native-reference-back',validator+[root/(name+'-reference-back.spv')])
    if reference_panic:
        print(name,'managed paths checked; reference validation panics',flush=True)
        continue
    reference=root/(name+'-reference.spv')
    run(name+'-reference-spv',oracle+[path,reference,'' if values=='unresolved' else values])
    invalid_reference='descriptor-array' in name
    run(name+'-reference-native',validator+[reference],not invalid_reference,'does not match' if invalid_reference else None)
    if not invalid_reference:
        reference_back=root/(name+'-reference-back.spv')
        run(name+'-reference-reemit',harness+[reference,reference_back]);run(name+'-reference-reemit-native',validator+[reference_back])
    imported=root/(name+'-reference-import.wgsl')
    import_ok=name in ['candidate-only','overrides-ray-query']
    run(name+'-reference-import',harness+[reference,imported],import_ok,None if import_ok else 'traversal')
    if import_ok:
        imported_spv=root/(name+'-reference-import-recompiled.spv')
        run(name+'-reference-import-wgsl',oracle+[imported])
        run(name+'-reference-import-compile',oracle+[imported,imported_spv])
        run(name+'-reference-import-native',validator+[imported_spv])
    print(name,'checked; reference import expected success' if import_ok else 'checked; committed conversion rejected',flush=True)

header=Path('.reference/SakuraEngine-engine/engine/packages/vulkan-headers/1.4.324/vulkan/vulkan_core.h').read_text()
stype=int(re.search(r'VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_RAY_QUERY_FEATURES_KHR = (\d+)',header).group(1))
RayQuery=S('RayQuery',head+[('rayQuery',u)])
Extension=S('Extension',[('name',C.c_char*256),('version',u)])
with Vulkan() as vk:
    function=vk.lib.vkEnumerateDeviceExtensionProperties;function.restype=i;function.argtypes=[p,p,p,p]
    count=u();assert function(vk.physical,None,C.byref(count),None)==0
    extensions=(Extension*count.value)();assert function(vk.physical,None,C.byref(count),extensions)==0
    names=[extension.name.decode() for extension in extensions]
    ray=RayQuery(stype,None,0);features=Features2(1000059000,C.addressof(ray));vk.vkGetPhysicalDeviceFeatures2(vk.physical,C.byref(features))
    evidence=dict(device=vk.name,rayQuery=bool(ray.rayQuery),rayQueryExtension='VK_KHR_ray_query' in names,accelerationStructureExtension='VK_KHR_acceleration_structure' in names)
    (root/'device.json').write_text(json.dumps(evidence,indent=2));print(evidence,flush=True)
print(f'{sum(row["passed"] for row in rows)}/{len(rows)} checks passed')
