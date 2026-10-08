from pathlib import Path
source=Path('.work/naga-csharp/check-pointer-descriptor-array.py').read_text().replace("root=Path('.work/naga-csharp/pointer-descriptor-array')","root=Path('.work/naga-csharp/pointer-descriptor-array-dynamic')")
exec(compile(source,str(Path(__file__).resolve()),'exec'),globals())
