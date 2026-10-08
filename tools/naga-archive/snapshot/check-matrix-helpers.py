from pathlib import Path
import json, struct, subprocess, sys
sys.path.insert(0,str(Path('.work/naga-csharp').resolve()))
from vulkan_compute import Vulkan
root=Path('.work/naga-csharp/matrix-helpers');rows=[]
if '--resume' in sys.argv:rows=json.loads((root/'checks.json').read_text())
harness=['.dotnet/dotnet.exe','.work/naga-csharp/harness/bin/Release/net10.0/harness.dll']
oracle=['.work/naga-csharp/oracle-target/release/sia-naga-reference-oracle.exe']
validator='.work/naga-csharp/spirv-tools-local/tools/Release/spirv-val.exe'
def save():(root/'checks.json').write_text(json.dumps(rows,indent=2))
def run(name,args,external=False):
 r=subprocess.run(list(map(str,args)),capture_output=True,text=True,timeout=60)
 rows.append(dict(check=name,passed=r.returncode==0,external=external,code=r.returncode,message=r.stdout+r.stderr,command=list(map(str,args))));save()
 if r.returncode and not external:raise RuntimeError(rows[-1])
 return r
def native(name,path,external=False):return run(name,[validator,'--target-env','vulkan1.2',path],external)
for case in json.loads((root/'fixtures.json').read_text()):
 name=case['name'];helper=case['kind'] in ['helper','whole','evaluation'];rowMajor=case['rowMajor'];workgroup=case.get('workgroup',False)
 final=name+('-control-bytes-5' if helper else '-managed-bytes-5')
 if any(r['check']==final and r['passed'] for r in rows):continue
 rows=[r for r in rows if not r['check'].startswith(name+'-')]
 original=root/(name+'.spv');back=root/(name+'-back.spv');wgsl=root/(name+'.wgsl');reference=root/(name+'-reference.spv');managed=root/(name+'-managed.spv')
 native(name+'-input-native',original);run(name+'-emit',harness+[original,back]);native(name+'-back-native',back)
 run(name+'-wgsl',harness+[original,wgsl]);run(name+'-reference',oracle+[wgsl,reference]);referenceValidation=native(name+'-reference-native',reference,external=workgroup)
 if referenceValidation.returncode and 'VUID-StandaloneSpirv-None-10684' not in referenceValidation.stderr:raise RuntimeError(rows[-1])
 run(name+'-managed',harness+[wgsl,managed]);native(name+'-managed-native',managed)
 paths=[('input',original),('roundtrip',back),('reference',reference),('managed',managed)]
 if referenceValidation.returncode:paths=[p for p in paths if p[0]!='reference']
 if helper:
  control=root/(name+'-control.spv')
  run(name+'-control-inline',['.work/naga-csharp/spirv-tools-local/tools/Release/spirv-opt.exe','--eliminate-dead-branches','--merge-return','--inline-entry-points-exhaustive','--eliminate-dead-functions',original,'-o',control])
  native(name+'-control-native',control);paths.append(('control',control))
 for seed in range(6):
  c=seed%3;r=seed%2
  if helper:
   data=bytearray(struct.pack('<44I',*([0x3f400000]*44)));struct.pack_into('<4I',data,0,c,r,0,0);struct.pack_into('<f',data,160,919.)
   def offset(c,r):return 16+(r if rowMajor else c)*32+(c if rowMajor else r)*4
   for column in range(3):
    for row in range(2):struct.pack_into('<f',data,offset(column,row),float(11+10*column+row+100*seed))
   x=11.+10*c+100*seed;y=x+1;expectedMemory=bytearray(data)
   if case['kind'] in ['helper','evaluation']:
    struct.pack_into('<f',expectedMemory,offset(c,0),x+100)
    if r:struct.pack_into('<f',expectedMemory,offset(c,1),y+2)
    expected=[y+2 if r else x+100,x+100,y+2 if r else y,919.]
    if case['kind']=='evaluation':struct.pack_into('<I',expectedMemory,0,(c+1)%3)
   else:
    tail=302.+(21.+100*seed if r else 0.)
    expected=[tail if r else 101.,101.+100*c,tail if c==2 else 102.+100*c,919.]
    if not workgroup:
     for column in range(3):
      for row in range(2):struct.pack_into('<f',expectedMemory,offset(column,row),tail if column==2 and row==1 else 101.+100*column+row)
  else:
   data=bytearray(struct.pack('<3I',c,r,seed%2));expectedMemory=data
   zero=case['kind']=='zero' or case.get('zero',False)
   expected=[0. if zero else float(11+10*c+r+(100*(seed%2) if case['kind']=='array' else 0)),71.,0. if zero else 919.]
   if case['kind']=='shared':expected=[float(11+10*c+r),float(111+10*c+r),float(11+10*c+r),919.]
  for label,path in paths:
   pointers=('full' if workgroup else True) if helper and label in ['input','control'] else False
   raw=helper and label=='input'
   with Vulkan(variablePointers=pointers) as device:
    output=device.execute(path.read_bytes(),bytes(data),len(expected)*4,1)
    actual=list(struct.unpack('<'+'f'*len(expected),output));passed=actual==expected
    rows.append(dict(check=name+'-'+label+'-gpu-'+str(seed),passed=passed,native_original=raw,device=device.name,features=device.features,expected=expected,actual=actual));save()
   if not passed and not raw:raise RuntimeError(rows[-1])
   with Vulkan(variablePointers=pointers) as device:
    actual=device.execute(path.read_bytes(),bytes(data),len(expected)*4,1,returnInput=True);passed=actual==expectedMemory
    rows.append(dict(check=name+'-'+label+'-bytes-'+str(seed),passed=passed,native_original=raw,device=device.name,expected=expectedMemory.hex(),actual=actual.hex()));save()
   if not passed and not raw:raise RuntimeError(rows[-1])
 print(name,'checks recorded',flush=True)
print(sum(r['passed'] for r in rows),'/',len(rows),'matrix helper checks passed',flush=True)
print('Unexpanded native input GPU failures:',sum(not r['passed'] and r.get('native_original',False) for r in rows),flush=True)
print('Reference emitter native validation failures:',sum(not r['passed'] and r.get('external',False) for r in rows),flush=True)
