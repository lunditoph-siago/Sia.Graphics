from pathlib import Path
import runpy, json, re
suite=runpy.run_path('.work/naga-csharp/check-mesh-shader.py')
root=suite['root'];run=suite['run'];native=suite['native'];isolate=suite['isolate'];harness=suite['harness'];oracle=suite['oracle'];rows=suite['rows']
for original in sorted(root.glob('native-task-helper-*.spv'))+sorted(root.glob('native-mesh-helper-*.spv')):
 if not re.fullmatch(r'native-task-helper-(True|False)-[01]-(True|False)|native-mesh-helper-(True|False)', original.stem):continue
 name=original.stem;back=root/(name+'-back.wgsl');output=root/(name+'-back.spv');reference=root/(name+'-reference.spv');reverse=root/(name+'-roundtrip.wgsl')
 native(name+'-original-native',original)
 run(name+'-read',harness+[original,back]);run(name+'-reference-wgsl',oracle+[back]);run(name+'-emit',harness+[original,output]);native(name+'-back-native',output)
 run(name+'-reference-compile',oracle+[back,reference]);isolate(name+'-reference',reference)
 run(name+'-roundtrip',harness+[output,reverse]);run(name+'-reference-roundtrip',oracle+[reverse])
 print(name,'passed',flush=True)
for name in ['payload-atomic-task','payload-atomic-mesh']:
 source=root/(name+'.wgsl');canonical=root/(name+'-canonical.wgsl');spv=root/(name+'.spv');back=root/(name+'-back.wgsl');reemitted=root/(name+'-back.spv');reference=root/(name+'-reference.spv')
 run(name+'-source-reference',oracle+[source]);run(name+'-canonical',harness+[source,canonical]);run(name+'-canonical-reference',oracle+[canonical])
 run(name+'-compile',harness+[source,spv]);native(name+'-whole-vulkan',spv,['VUID-PrimitiveTriangleIndicesEXT-PrimitiveTriangleIndicesEXT-07054']);isolate(name,spv)
 run(name+'-read',harness+[spv,back]);run(name+'-back-reference',oracle+[back]);run(name+'-reemit',harness+[spv,reemitted]);isolate(name+'-roundtrip',reemitted)
 run(name+'-reference-compile',oracle+[source,reference]);isolate(name+'-reference',reference)
 run(name+'-reference-import',harness+[reference,root/(name+'-reference-back.wgsl')]);run(name+'-reference-import-oracle',oracle+[root/(name+'-reference-back.wgsl')])
 run(name+'-reference-native-reemit',harness+[reference,root/(name+'-reference-back.spv')]);isolate(name+'-reference-native-reemit',root/(name+'-reference-back.spv'))
 print(name,'passed',flush=True)
(root/'control-checks.json').write_text(json.dumps(rows,indent=2))
print(sum(row['passed'] for row in rows),'/',len(rows),'control checks passed',flush=True)
