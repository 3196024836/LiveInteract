from pathlib import Path
import zipfile,json
root=Path(__file__).resolve().parents[1];source=root/'dist/LiveInteract-0.3.0-win-x64'
target=source.parent/(source.name+'.zip')
if target.exists():raise SystemExit('Preserve existing archive; choose a new version')
files=sorted(p for p in source.rglob('*') if p.is_file())
private_names={'interactions.db','rules.json','settings.json','Player.log','Player-prev.log'}
if any(f.name in private_names or any(x in {'DouyinBrowser','GameReceipts','original-saves'} for x in f.relative_to(source).parts) for f in files):raise RuntimeError('Private user data must not be archived')
with zipfile.ZipFile(target,'x',zipfile.ZIP_DEFLATED,compresslevel=6) as archive:
    for f in files:archive.write(f,f.relative_to(source.parent))
with zipfile.ZipFile(target) as archive:
    bad=archive.testzip()
    if bad:raise RuntimeError('Archive CRC failed: '+bad)
    names=archive.namelist()
    if len(names)!=len(files):raise RuntimeError('Archive inventory mismatch')
report={'Archive':str(target),'Entries':len(files),'UncompressedBytes':sum(f.stat().st_size for f in files),'Bytes':target.stat().st_size,'CrcVerified':True,'ProfilesIncluded':any('/profiles/' in n and n.endswith('.json') for n in names),'OriginalUserDataIncluded':False}
(root/'artifacts/audit-0.3/release-zip.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf8');print(json.dumps(report,ensure_ascii=True,indent=2))
