from pathlib import Path
import json,os
root=Path(__file__).resolve().parents[1];audit=root/'artifacts/audit-0.3';saves=Path(os.environ['LOCALAPPDATA']).parent/'LocalLow/Team Cherry/Hollow Knight Silksong';data=Path(os.environ['LOCALAPPDATA'])/'LiveInteract';prod=root/'dist/LiveInteract-0.3.0-win-x64'
checks=[];runtime_logs=[]
for backup,target,label in [(audit/'original-saves',saves,'OriginalSaves'),(audit/'original-settings',data,'OriginalSettings')]:
    rows=[]
    for f in backup.rglob('*'):
        if not f.is_file():continue
        if label=='OriginalSaves' and f.name in ['Player.log','Player-prev.log']:
            runtime_logs.append(str(f.relative_to(backup)));continue
        current=target/f.relative_to(backup);rows.append({'File':str(f.relative_to(backup)),'Exists':current.exists(),'SameBytes':current.exists() and f.read_bytes()==current.read_bytes()})
    checks.append({'Group':label,'Count':len(rows),'AllUnchanged':all(r['SameBytes'] for r in rows),'Changed':[r for r in rows if not r['SameBytes']]})
profile=json.loads(next((prod/'profiles').glob('*.json')).read_text(encoding='utf8'))
report={'Preservation':checks,'RuntimeLogsExpectedToChange':runtime_logs,'NativeSqlite':[str(f.relative_to(prod)) for f in prod.rglob('e_sqlite3.dll')],'ForbiddenGameAssemblies':[str(f.relative_to(prod)) for p in ['Assembly-CSharp.dll','UnityEngine*.dll'] for f in prod.rglob(p)],'DefaultRuleCount':len(profile['Rules']),'DefaultChatRules':sum(r['Trigger']['Type']==0 for r in profile['Rules']),'QaConfigRemoved':not (Path(r'C:\Program Files (x86)\Steam\steamapps\common\Hollow Knight Silksong\BepInEx\plugins\LiveInteract')/'liveinteract-qa.json').exists(),'FileCount':sum(f.is_file() for f in prod.rglob('*'))}
(audit/'preservation-and-inventory.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf8');print(json.dumps(report,ensure_ascii=True,indent=2))
if not all(c['AllUnchanged'] for c in checks):raise SystemExit('Original files changed: inspect before restoring')
if report['ForbiddenGameAssemblies'] or not report['NativeSqlite'] or not report['QaConfigRemoved']:raise SystemExit('Invalid release inventory')
