from pathlib import Path
import subprocess,json
root=Path('.work/naga-csharp/mesh');rows=[]
cases={
 'unused-small':'enable wgpu_mesh_shader; var<task_payload> payload:bool; @task @payload(payload) @workgroup_size(1) fn main()->@builtin(mesh_task_size) vec3u { return vec3u(1); }',
 'used-small':'enable wgpu_mesh_shader; var<task_payload> payload:bool; @task @payload(payload) @workgroup_size(1) fn main()->@builtin(mesh_task_size) vec3u { payload=true; return vec3u(1); }',
 'atomic':'enable wgpu_mesh_shader; var<task_payload> payload:atomic<u32>; @task @payload(payload) @workgroup_size(1) fn main()->@builtin(mesh_task_size) vec3u { atomicStore(&payload,0u); let old=atomicAdd(&payload,1u); return vec3u(old+1u); }',
}
for name,source in cases.items():
 path=root/('payload-'+name+'.wgsl');path.write_text(source);result=subprocess.run(['.work/naga-csharp/oracle-target/release/sia-naga-reference-oracle.exe',str(path)],capture_output=True,text=True);rows.append(dict(name=name,code=result.returncode,message=result.stdout+result.stderr));print(name,result.returncode,result.stdout+result.stderr,flush=True)
(root/'payload-probe.json').write_text(json.dumps(rows,indent=2))
