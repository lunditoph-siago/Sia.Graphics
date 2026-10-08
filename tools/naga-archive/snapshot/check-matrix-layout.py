from pathlib import Path
import json,struct,subprocess,sys
sys.path.insert(0,str(Path('.work/naga-csharp').resolve()))
from vulkan_compute import Vulkan
root=Path('.work/naga-csharp/matrix-layout');rows=[]
if '--resume' in sys.argv:rows=[r for r in json.loads((root/'checks.json').read_text()) if r['passed']]
harness=['.dotnet/dotnet.exe','.work/naga-csharp/harness/bin/Release/net10.0/harness.dll']
oracle=['.work/naga-csharp/oracle-target/release/sia-naga-reference-oracle.exe']
validator='.work/naga-csharp/spirv-tools-local/tools/Release/spirv-val.exe'
def save():(root/'checks.json').write_text(json.dumps(rows,indent=2))
def run(name,args,rejection=None):
 p=subprocess.run(list(map(str,args)),capture_output=True,text=True,timeout=60);message=p.stdout+p.stderr
 passed=p.returncode==0 if rejection is None else p.returncode!=0 and rejection in message
 rows.append(dict(check=name,passed=passed,code=p.returncode,expected_rejection=rejection,message=message,command=list(map(str,args))));save()
 if not passed:raise RuntimeError(name+': '+message)
def native(name,path):run(name,[validator,'--target-env','vulkan1.2',path])
for case in json.loads((root/'fixtures.json').read_text()):
 name=case['name'];original=root/(name+'.spv');back=root/(name+'-back.spv');wgsl=root/(name+'.wgsl');reference=root/(name+'-reference.spv');managed=root/(name+'-managed.spv')
 final=name+('-roundtrip-bytes-1' if case['uniform'] and case['memoryFlags']&1 else '-managed-bytes-1')
 if any(r['check']==final for r in rows):print(name,'already verified',flush=True);continue
 rows=[r for r in rows if not r['check'].startswith(name+'-')]
 native(name+'-input-native',original);run(name+'-emit',harness+[original,back]);native(name+'-back-native',back)
 rejection='storage-buffer root' if case['uniform'] and case['memoryFlags']&1 else None
 run(name+'-wgsl',harness+[original,wgsl],rejection);paths=[('input',original),('roundtrip',back)]
 if not rejection:
  run(name+'-reference',oracle+[wgsl,reference]);native(name+'-reference-native',reference)
  run(name+'-managed',harness+[wgsl,managed]);native(name+'-managed-native',managed);paths += [('reference',reference),('managed',managed)]
 for seed in [0,1]:
  columns=case['columns'];rr=case['rows'];c=seed*(columns-1);r=seed*(rr-1)
  kind=case['kind'];count=1 if kind=='direct' else 2;size=176 if kind=='direct' else 352 if kind=='array' else 416
  data=bytearray(struct.pack('<'+'I'*(size//4),*([0x3f400000]*(size//4))));selected=0 if kind=='direct' else seed
  struct.pack_into('<4I',data,0,c,r,selected,0);struct.pack_into('<f',data,size-16,919.)
  width=2 if case['half'] else 4;fmt='<e' if case['half'] else '<f'
  def offset(matrix,column,row):return 16+matrix*(160 if kind=='array' else 192)+(row if case['rowMajor'] else column)*case['stride']+(column if case['rowMajor'] else row)*width
  matrices=[]
  for item in range(count):
   values=[float(11+column*10+row+seed*100+item*60) for column in range(columns) for row in range(rr)];matrices.append(values)
   for column in range(columns):
    for row in range(rr):struct.pack_into(fmt,data,offset(item,column,row),values[column*rr+row])
   if kind=='nested':struct.pack_into('<f',data,16+item*192+160,701.+item)
  before=matrices[selected]
  after=before.copy();operation=case['operation']
  if not case['uniform']:
   if operation=='component':after[c*rr+r]=301.
   elif operation=='column':after[c*rr:c*rr+rr]=[float(401+i) for i in range(rr)]
   elif operation=='matrix':after=[float(501+column*10+row) for column in range(columns) for row in range(rr)]
   elif operation=='swap':after=matrices[1-selected].copy()
  expectedMemory=bytearray(data)
  updated=matrices.copy();updated[selected]=after
  if operation=='swap' and not case['uniform']:updated=matrices[::-1]
  for item,values in enumerate(updated):
   for column in range(columns):
    for row in range(rr):struct.pack_into(fmt,expectedMemory,offset(item,column,row),values[column*rr+row])
  expected=before+[before[c*rr+r]]*2+after+[919.]
  for label,path in paths:
   with Vulkan() as device:
    output=device.execute(path.read_bytes(),bytes(data),len(expected)*4,1,inputDescriptorType=6 if case['uniform'] else 7)
    actual=list(struct.unpack('<'+'f'*len(expected),output));passed=actual==expected
    rows.append(dict(check=name+'-'+label+'-gpu-'+str(seed),passed=passed,device=device.name,expected=expected,actual=actual));save()
   if not passed:raise RuntimeError(rows[-1])
   with Vulkan() as device:
    actual=device.execute(path.read_bytes(),bytes(data),len(expected)*4,1,inputDescriptorType=6 if case['uniform'] else 7,returnInput=True)
    passed=all(actual[i]==expectedMemory[i] for i in range(len(data)))
    rows.append(dict(check=name+'-'+label+'-bytes-'+str(seed),passed=passed,device=device.name,expected=expectedMemory.hex(),actual=actual.hex()));save()
   if not passed:raise RuntimeError(rows[-1])
 print(name,'passed',flush=True)
print(sum(r['passed'] for r in rows),'/',len(rows),'matrix layout checks passed',flush=True)
