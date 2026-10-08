import pathlib,subprocess,json
root=pathlib.Path(__file__).resolve().parents[2];work=root/'.work/naga-csharp/override-array-probes';work.mkdir(exist_ok=True)
oracle=root/'.work/naga-csharp/oracle-target/release/sia-naga-reference-oracle.exe'
main='@compute @workgroup_size(1) fn main() { data[0] = 1u; }'
cases={
'direct':'override n=4u; var<workgroup> data:array<u32,n>; '+main,
'expression':'override n=4u; var<workgroup> data:array<u32,n+1u>; '+main,
'missing':'override n:u32; var<workgroup> data:array<u32,n>; '+main,
'negative-default':'override n=-1i; var<workgroup> data:array<u32,n>; '+main,
'zero-default':'override n=0u; var<workgroup> data:array<u32,n>; '+main,
'nested':'override n=4u; var<workgroup> data:array<array<u32,n>,2>; @compute @workgroup_size(1) fn main() {}',
'struct':'override n=4u; struct S { a:array<u32,n> } var<workgroup> data:S; @compute @workgroup_size(1) fn main() {}',
'local':'override n=4u; @compute @workgroup_size(1) fn main() { var data:array<u32,n>; }',
'immutable':'override n=4u; var<workgroup> data:array<u32,n>; @compute @workgroup_size(1) fn main() { let x=data; _=x[0]; }',
'pointer':'override n=4u; var<workgroup> data:array<u32,n>; fn write_value(p:ptr<workgroup,array<u32,n>>) { (*p)[0]=1u; } @compute @workgroup_size(1) fn main() { write_value(&data); }',
'by-value':'override n=4u; var<workgroup> data:array<u32,n>; fn read(p:array<u32,n>)->u32 { return p[0]; } @compute @workgroup_size(1) fn main() { _=read(data); }',
'private-pointer':'override n=4u; var<workgroup> data:array<u32,n>; fn fetch(p:ptr<private,array<u32,n>>)->u32 { return (*p)[0]; } @compute @workgroup_size(1) fn main() { data[0]=1u; }',
'return':'override n=4u; var<workgroup> data:array<u32,n>; fn read()->array<u32,n> { return data; } @compute @workgroup_size(1) fn main() {}',
'binding':'enable wgpu_binding_array; override n=4u; @group(0) @binding(0) var data:binding_array<texture_2d<f32>,n>; @compute @workgroup_size(1) fn main() { _=textureLoad(data[0],vec2i(0),0); }',
'storage':'override n=4u; @group(0) @binding(0) var<storage,read_write> data:array<u32,n>; '+main,
'constructed':'override n=4u; var<workgroup> data:array<u32,n>; @compute @workgroup_size(1) fn main() { data=array<u32,n>(); }',
}
records=[]
for name,source in cases.items():
 path=work/f'{name}.wgsl';path.write_text(source,encoding='utf-8')
 for step,extra in [('parse',[]),('wgsl',[work/f'{name}-reference.wgsl']),('resolved',[work/f'{name}-resolved.spv','n=3'])]:
  p=subprocess.run([str(oracle),str(path),*map(str,extra)],capture_output=True,text=True,encoding='utf-8',errors='replace')
  log=p.stdout+p.stderr;(work/f'{name}-{step}.log').write_text(log,encoding='utf-8');records.append(dict(case=name,step=step,code=p.returncode,message=log.splitlines()[0] if log else ''))
(work/'manifest.json').write_text(json.dumps(records,indent=2),encoding='utf-8')
for r in records: print(r)
