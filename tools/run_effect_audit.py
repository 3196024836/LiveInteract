"""Runs each non-spawn effect in the isolated QA save, recording before/after runtime state."""
from pathlib import Path
import json,time,uuid,sys,re
ROOT=Path(__file__).resolve().parents[1]; CONTROL=ROOT/'artifacts/audit-0.3/control'
REPORT=ROOT/('artifacts/audit-0.3/effect-audit-'+(sys.argv[1] if len(sys.argv)>1 else 'latest')+'.json')
LOG=Path(r'C:\Program Files (x86)\Steam\steamapps\common\Hollow Knight Silksong\BepInEx\LogOutput.log')
def req(c,timeout=45):
    id='effect-'+uuid.uuid4().hex; f=CONTROL/(id+'.request.json');t=CONTROL/(id+'.tmp');t.write_text(json.dumps(c,ensure_ascii=False),encoding='utf8');t.rename(f)
    until=time.monotonic()+timeout
    while time.monotonic()<until:
        r=CONTROL/(id+'.result.json')
        if r.exists():return json.loads(r.read_text(encoding='utf8'))['Result']
        time.sleep(.04)
    raise RuntimeError('No game receipt '+id)
def qa(op,**values):
    r=req({'Kind':'qa','Operation':op,**values})
    if not r['Success']:raise RuntimeError(r['Message'])
    return json.loads(r['Message'])
def act(id,args=None,seconds=1.4):
    return req({'Kind':'action','Request':{'ActionId':id,'Arguments':args or {},'DurationSeconds':seconds,'AllowPersistent':True,'DisplayText':'隔离效果验收','Triggerer':'测试观众小明','EffectText':''}})
def own(s):return [e for e in s.get('Enemies',[]) if '[LiveInteract]' in e['Name']]
def field(s,path):
    for k in path.split('.'):
        if not isinstance(s,dict):return None
        s=s.get(k)
    return s
A=json.loads((ROOT/'src/LiveInteract.Contracts/actions.json').read_text(encoding='utf8'))
for id,name in re.findall(r'Id = "(cast.[^"]+)", Name = "([^"]+)"',(ROOT/'src/LiveInteract.Contracts/SkillCatalog.cs').read_text(encoding='utf8')):
    A.append({'Id':id,'Name':name,'Parameters':[],'IsTemporary':False})
last=['scene.warp','scene.bench','health.kill']
if len(sys.argv)>2:A=[a for a in A if a['Id'] in sys.argv[2].split(',')]
A.sort(key=lambda a:(1 if a['Id'] in last else 0,last.index(a['Id']) if a['Id'] in last else 0))
rows=[]
s=qa('state')
if not s.get('Isolated') or not s.get('Hero'):raise RuntimeError('Need isolated playable save; refusing original-save access')
# Auditing parameters are deliberately different from the baseline where applicable.
overrides={'health.set':{'value':'3'},'health.max':{'value':'7'},'silk.add':{'value':'2'},'silk.set':{'value':'2'},'silk.max':{'value':'12'},'skill.unlock':{'ability':'hasDash','enabled':'false'},'scene.warp':{'scene':'Tut_01','gate':''},'skill.cast':{'fsm':'silkSpecialFSM','event':'NEEDLE THROW'},'enemy.asset':{'asset':'spawn.bone-goomba','count':'1'},'boss.asset':{'asset':'spawn.flower-queen-boss','count':'1'}}
scalar={'health.add':'Player.health','health.set':'Player.health','health.full':'Player.health','health.damage':'Player.health','health.kill':'Player.health','health.max':'Player.maxHealth','health.blue':'Modifiers.shieldPoints','silk.add':'Player.silk','silk.set':'Player.silk','silk.full':'Player.silk','silk.empty':'Player.silk','silk.max':'Player.CurrentSilkMax','currency.rosaries':'Player.geo','currency.shards':'Player.ShellShards','needle.upgrade':'Player.nailUpgrades','move.speed':'Hero.RUN_SPEED','move.jump':'Hero.JUMP_SPEED','move.dash':'Hero.DASH_SPEED','move.gravity':'Hero.DEFAULT_GRAVITY','move.scale':'Hero.ScaleY','move.freeze':'Hero.RUN_SPEED','move.attackSpeed':'Modifiers.attackSpeed','camera.zoom':'CameraZoom','visual.brightness':'Brightness','visual.contrast':'Contrast','time.scale':'TimeScale','time.freeze':'TimeScale','visual.hideHero':'Hero.HeroVisible','visual.hideHud':'HudVisible','health.invincible':'Modifiers.invincible','health.survive':'Modifiers.survive','silk.infinite':'Modifiers.infiniteSilk','tools.infinite':'Modifiers.infiniteTools','damage.all':'Modifiers.allDamage','damage.needle':'Modifiers.needleDamage','damage.tools':'Modifiers.toolDamage','damage.silk':'Modifiers.silkDamage','damage.taken':'Modifiers.incomingDamage','damage.knockback':'Modifiers.knockback'}
for a in A:
    id=a['Id'];r={'Id':id,'Name':a['Name'],'Scene':'','Passed':False,'Evidence':[]}
    try:
        s=qa('state')
        if s.get('Hero',{}).get('Dead'):raise RuntimeError('Actor dead from previous case; stop rather than fake a pass')
        qa('prepare',Unlock=True);qa('restore-position');time.sleep(.06)
        args={p['Key']:p.get('DefaultValue','') for p in a.get('Parameters',[])};args.update(overrides.get(id,{}))
        status=json.loads((CONTROL/'status.json').read_text(encoding='utf8'))
        if id.startswith('tools.'):
            if id in ['tools.unlock','tools.unlockAll']:qa('lock-tools')
            else:act('tools.unlockAll');qa('empty-tools')
            capacities=qa('state').get('ToolCapacities',{}); consumables=[k for k,v in capacities.items() if v>0]
            args['tool']=consumables[0] if id=='tools.give' and consumables else status.get('ToolIds',['Throwing Pin'])[0] if 'tool' in args else args.get('tool','')
            if 'tool' not in [p['Key'] for p in a.get('Parameters',[])]:args.pop('tool',None)
        if id=='needle.upgrade':args['value']='2' if qa('state')['Player']['nailUpgrades']!=2 else '1'
        if id=='crest.unlockAll':qa('lock-crests')
        if id=='crest.equip':
            act('crest.unlockAll');args['crest']='Hunter' if qa('state')['Player']['CurrentCrestID']!='Hunter' else 'Wanderer'
        enemy_case=id.startswith('enemy.') or id.startswith('boss.')
        if enemy_case and id not in ['enemy.asset','boss.asset']:
            act('health.invincible',seconds=10);sp=act('spawn.flower-queen-boss' if id.startswith('boss.') else 'spawn.bone-goomba',{'count':'1','offsetX':'4','offsetY':'1'},10)
            if not sp['Success']:raise RuntimeError('Target fixture failed: '+sp['Message'])
            time.sleep(.15)
            target=own(qa('state'))[0]
            if 'enemy' in args:args['enemy']=target['Name']+'#'+str(target['Id'])
            if id in ['enemy.damage','enemy.heal']:act('enemy.health',{'value':'100','enemy':target['Name']+'#'+str(target['Id'])})
        if id=='position.return':act('position.mark');act('position.move',{'x':'2','y':'0'})
        if id=='effects.reset':act('damage.all',{'value':'2'},10)
        if id=='move.airReset':qa('use-air-counters')
        before=qa('state');r['Scene']=before['Scene'];r['Arguments']=args;r['Before']=before
        offset=LOG.stat().st_size
        receipt=act(id,args,1.4);time.sleep(.18);after=qa('state')
        r['Receipt']=receipt;r['After']=after
        if a.get('IsTemporary'):
            time.sleep(1.45);r['Restored']=qa('state')
        with LOG.open('rb') as file:file.seek(offset);logs=file.read().decode('utf8',errors='replace')
        errors=[l for l in logs.splitlines() if '[Error' in l or 'Exception:' in l]
        r['UnityErrors']=errors[:8]
        changed=False
        if id in scalar:
            p=scalar[id];b=field(before,p);v=field(after,p);changed=b!=v and v is not None;r['Evidence'].append({'Field':p,'Before':b,'After':v})
            if a.get('IsTemporary') and id not in ['move.freeze']:
                restored=field(r['Restored'],p);r['Evidence'].append({'Restored':restored,'Baseline':b});r['RestorationConfirmed']=abs(restored-b)<.001 if isinstance(b,(float,int)) and not isinstance(b,bool) and isinstance(restored,(float,int)) else restored==b
        elif id.startswith('cast.') or id=='skill.cast':
            changed=field(before,'Hero.SkillState')!=field(after,'Hero.SkillState') or field(before,'Player.silk')!=field(after,'Player.silk');r['Evidence'].append({'BeforeState':field(before,'Hero.SkillState'),'AfterState':field(after,'Hero.SkillState'),'SilkBefore':field(before,'Player.silk'),'SilkAfter':field(after,'Player.silk')})
        elif id in ['tools.refill','tools.give','tools.infinite']:changed=sum(after.get('Tools',{}).values())>sum(before.get('Tools',{}).values());r['Evidence'].append({'ToolsBefore':before.get('Tools'),'ToolsAfter':after.get('Tools')})
        elif id in ['tools.unlock','tools.unlockAll']:changed=after.get('UnlockedTools',0)>before.get('UnlockedTools',0)
        elif id=='crest.unlockAll':changed=after.get('UnlockedCrests',0)>before.get('UnlockedCrests',0)
        elif id=='crest.equip':changed=field(before,'Player.CurrentCrestID')!=field(after,'Player.CurrentCrestID') and field(after,'Player.CurrentCrestID')==args['crest']
        elif id=='skill.unlock':changed=field(before,'Player.hasDash')!=field(after,'Player.hasDash')
        elif id in ['enemy.spawn','enemy.clone','enemy.random','boss.clone','enemy.asset','boss.asset']:changed=len(own(after))>len(own(before))
        elif id in ['enemy.damage','boss.damage']:changed=sum(e['Hp'] for e in own(after))<sum(e['Hp'] for e in own(before))
        elif id in ['enemy.health','enemy.heal','boss.heal']:changed=sum(e['Hp'] for e in own(after))>sum(e['Hp'] for e in own(before))
        elif id=='enemy.size':changed=own(after)[0]['ScaleY']!=own(before)[0]['ScaleY']
        elif id=='enemy.speed':changed=own(after)[0].get('AnimationRate')!=own(before)[0].get('AnimationRate') or own(after)[0].get('Animator')!=own(before)[0].get('Animator')
        elif id in ['enemy.killAll','enemy.clearSpawned']:changed=len(own(after))<len(own(before))
        elif id=='position.move':changed=abs(field(after,'Hero.X')-field(before,'Hero.X'))>.1
        elif id=='position.return':changed=abs(field(after,'Hero.X')-field(before,'Hero.X'))>1
        elif id=='position.mark':changed=True;r['Evidence'].append('Stored-position command acknowledged; return behavior is tested separately')
        elif id=='move.jumpNow':changed=field(after,'Hero.VelocityY')!=field(before,'Hero.VelocityY') or abs(field(after,'Hero.Y')-field(before,'Hero.Y'))>.1
        elif id=='move.dashNow':changed=abs(field(after,'Hero.X')-field(before,'Hero.X'))>.1 or field(after,'Hero.AirDashed')!=field(before,'Hero.AirDashed')
        elif id=='move.airReset':changed=field(before,'Hero.AirDashed')!=field(after,'Hero.AirDashed') or field(before,'Hero.DoubleJumped')!=field(after,'Hero.DoubleJumped')
        elif id=='effects.reset':changed=bool(before.get('Effects')) and not after.get('Effects')
        elif id in ['game.status','game.catalog']:changed=receipt['Success'];r['Evidence'].append(receipt['Message'])
        elif id=='game.capture':
            match=re.search(r'([A-Z]:\\[^\r\n]+\.png)',receipt['Message']);time.sleep(.3);changed=bool(match and Path(match.group(1)).exists());r['Evidence'].append(receipt['Message'])
        elif id.startswith('scene.'):
            time.sleep(2);final=qa('state');r['TransitionAfter']=final;changed=final.get('Scene')=='Tut_01' and bool(final.get('Hero'))
        r['RuntimeChangeConfirmed']=changed
        r['Passed']=bool(receipt['Success'] and changed and not errors and r.get('RestorationConfirmed',True))
    except Exception as exception:r['Error']=str(exception)
    rows.append(r);REPORT.write_text(json.dumps({'Complete':False,'Count':len(rows),'Passed':sum(bool(x.get('Passed')) for x in rows),'Results':rows},ensure_ascii=False,indent=2),encoding='utf8');print(id,'PASS' if r['Passed'] else 'FAIL',r.get('Receipt',{}).get('Message',r.get('Error','')),flush=True)
REPORT.write_text(json.dumps({'Complete':len(rows)==len(A),'Count':len(rows),'Passed':sum(bool(x.get('Passed')) for x in rows),'Results':rows},ensure_ascii=False,indent=2),encoding='utf8')
