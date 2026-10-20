"""Physical HP/velocity proof in isolated game; never drives the original save."""
from pathlib import Path
import json,time,uuid
ROOT=Path(__file__).resolve().parents[1]; CONTROL=ROOT/'artifacts/audit-0.3/control'
def request(c):
    id='combat-'+uuid.uuid4().hex; f=CONTROL/(id+'.request.json'); t=f.with_suffix('.tmp'); t.write_text(json.dumps(c,ensure_ascii=False),encoding='utf8');t.rename(f)
    end=time.monotonic()+40
    while time.monotonic()<end:
        p=CONTROL/(id+'.result.json')
        if p.exists():
            try:return json.loads(p.read_text(encoding='utf8'))['Result']
            except (PermissionError,json.JSONDecodeError):pass
        time.sleep(.04)
    raise TimeoutError(id)
def qa(op,**kw):
    r=request({'Kind':'qa','Operation':op,**kw})
    if not r['Success']:raise RuntimeError(r['Message'])
    return json.loads(r['Message'])
def act(id,args=None,seconds=10):
    r=request({'Kind':'action','Request':{'ActionId':id,'Arguments':args or {},'DurationSeconds':seconds,'AllowPersistent':True,'Triggerer':'测试观众小明'}})
    if not r['Success']:raise RuntimeError(r['Message'])
    return r
if not qa('state')['Isolated']:raise RuntimeError('Isolated save required')
def prepare():
    qa('prepare',Unlock=True);qa('restore-position')
    until=time.monotonic()+5
    while not qa('state')['Hero']['CanTakeDamage'] and time.monotonic()<until:time.sleep(.1)
def enemy(s):return next(e for e in s['Enemies'] if '[LiveInteract]' in e['Name'])
rows=[]
for action,kind in [('damage.all','nail'),('damage.needle','nail'),('damage.tools','tools'),('damage.silk','silk')]:
    prepare();act('health.invincible');act('spawn.bone-goomba',{'count':'1','offsetX':'4','offsetY':'1'})
    e=enemy(qa('state'));act('enemy.health',{'enemy':e['Name']+'#'+str(e['Id']),'value':'500'});time.sleep(.2)
    before=enemy(qa('state'))['Hp'];base=enemy(qa('probe-hit',DamageKind=kind,Damage=10,Tool='Straight Pin'))['Hp'];time.sleep(.2)
    act(action,{'value':'2'});after=enemy(qa('probe-hit',DamageKind=kind,Damage=10,Tool='Straight Pin'))['Hp']
    rows.append({'Test':action,'BeforeHp':before,'BaselineHp':base,'MultipliedHp':after,'Passed':before-base==10 and base-after==20})
(ROOT/'artifacts/audit-0.3/combat-damage-prefix.json').write_text(json.dumps(rows,ensure_ascii=False,indent=2),encoding='utf8')
for name,setup,damage,expectedhp,expectedshield in [
    ('shield absorbs actual damage',('health.blue',{'value':'2'}),1,4,1),
    ('damage taken multiplier',('damage.taken',{'value':'0.5'}),2,3,0),
    ('invincible blocks hit',('health.invincible',{}),2,4,0),
    ('survive clamps lethal hit',('health.survive',{}),99,1,0)]:
    prepare();act(*setup);before=qa('state');receipt=act('health.damage',{'value':str(damage)});time.sleep(.15);after=qa('state')
    rows.append({'Test':name,'BeforeHp':before['Player']['health'],'AfterHp':after['Player']['health'],'ShieldAfter':after['Modifiers']['shieldPoints'],'Receipt':receipt,'Passed':after['Player']['health']==expectedhp and after['Modifiers']['shieldPoints']==expectedshield})
for name,action,value,mult in [('move speed','move.speed','2',2),('freeze move','move.freeze',None,0)]:
    prepare();baseline=qa('probe-move',Axis=1)['Hero']['VelocityX'];act(action,{'value':value} if value else {});after=qa('probe-move',Axis=1)['Hero']['VelocityX']
    rows.append({'Test':name,'BeforeVelocity':baseline,'AfterVelocity':after,'Passed':abs(after-baseline*mult)<.01})
report={'ActualGame':True,'IsolatedSave':True,'Count':len(rows),'Passed':all(x['Passed'] for x in rows),'Results':rows}
p=ROOT/'artifacts/audit-0.3/combat-audit-v10.json';p.write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf8')
for r in rows:print(r['Test'],r['Passed'],flush=True)
if not report['Passed']:raise SystemExit(1)
