import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';

export async function runtimeCaptureBrowser(page,root,output,check,shot){
  const source=process.env.RUNTIME_CAPTURE_EXPORT || path.join(root,'fixtures/physical-runtime/capture-portable.json');
  const text=await fs.readFile(source,'utf8');
  await page.locator('.capture-input').setInputFiles(source);
  await page.locator('.runtime-panel').waitFor({state:'visible'});
  assert.match(await page.locator('.runtime-panel').innerText(),/Changes between polls are unknown/);
  await page.locator('.runtime-panel summary').first().click();
  assert.match(await page.locator('.runtime-panel pre').first().innerText(),/18446744073709551615/);
  assert.match(await page.locator('#connection-state').innerText(),/no target connection/);
  check('Exported runtime capture opens offline with exact UInt64 text and truthful polling limits');
  const downloadPromise=page.waitForEvent('download');await page.getByRole('button',{name:'Download capture JSON',exact:true}).click();
  const download=await downloadPromise;const saved=path.join(output,'capture-roundtrip.json');await download.saveAs(saved);
  assert.equal(await fs.readFile(saved,'utf8'),text);check('Runtime capture download preserves supplied JSON bytes');
  await page.setViewportSize({width:1280,height:800});await shot('runtime-capture-light-1280');
  await page.getByRole('button',{name:'Toggle color theme'}).click();await shot('runtime-capture-dark-1280');
  await page.getByRole('button',{name:'Toggle color theme'}).click();
  const forged=JSON.parse(text);forged.capture.nativeQualified=true;
  assert.match(await page.evaluate(value=>{try{projectExplorer.loadRuntimeCapture(JSON.stringify(value));return 'accepted';}catch(e){return e.message;}},forged),/Invalid runtime/);
  const hostile=JSON.parse(text);hostile.capture.plan.signals[0].selection.symbol='<img src=x onerror=alert(1)>';
  hostile.rows.push({...hostile.rows[0],sequence:hostile.rows.length,readResultJson:null,errorCode:'RuntimeTimeout'});
  hostile.rows.push({...hostile.rows[0],sequence:hostile.rows.length,kind:'gap',selectionId:null,readResultJson:null,errorCode:null,skippedPolls:7});
  hostile.capture.recordedProgress.rows=hostile.rows.length;
  await page.evaluate(value=>projectExplorer.loadRuntimeCapture(JSON.stringify(value)),hostile);
  assert.equal(await page.locator('.runtime-panel img').count(),0);
  assert.match(await page.locator('.runtime-panel').innerText(),/RuntimeTimeout · value unknown/);
  assert.match(await page.locator('.runtime-panel').innerText(),/Skipped 7 scheduled polls/);
  await page.locator('.runtime-filter').selectOption(hostile.capture.plan.signals[0].selectionId);
  assert.match(await page.locator('.runtime-panel').innerText(),/Skipped 7 scheduled polls/);
  check('Capture viewer rejects false qualification and renders hostile names, errors and gaps as text');
  const many=JSON.parse(text);many.rows=Array.from({length:205},(_,sequence)=>({...many.rows[0],sequence}));many.capture.recordedProgress.rows=205;
  await page.evaluate(value=>projectExplorer.loadRuntimeCapture(JSON.stringify(value)),many);
  assert.equal(await page.locator('.runtime-panel tbody tr').count(),100);await page.getByRole('button',{name:'Next',exact:true}).click();
  assert.equal(await page.locator('.runtime-panel tbody tr').first().locator('td').first().innerText(),'100');
  await page.getByRole('button',{name:'Next',exact:true}).click();assert.equal(await page.locator('.runtime-panel tbody tr').count(),5);
  check('Capture rows paginate at 100 without rendering the full data set');
  const associated=JSON.parse(await fs.readFile(path.join(root,'fixtures/physical-runtime/capture-associated.json'),'utf8'));
  await page.evaluate(value=>projectExplorer.loadRuntimeCapture(JSON.stringify(value)),associated);
  await page.locator('.runtime-association > summary').click();
  await page.locator('.runtime-association details > summary').first().click();
  assert.match(await page.locator('.runtime-association').innerText(),/These links do not qualify diagnostic assertions/);
  assert.match(await page.locator('.runtime-association').innerText(),/DB_Line/);
  assert.match(await page.locator('.runtime-association').innerText(),/Conv2/);
  await page.setViewportSize({width:1280,height:800});await shot('runtime-association-light-1280');
  await page.getByRole('button',{name:'Toggle color theme'}).click();await shot('runtime-association-dark-1280');
  await page.getByRole('button',{name:'Toggle color theme'}).click();
  for(const field of ['addressMatchVerified','programMatchVerified','sourceAuthenticityVerified','nativeQualified']){
    const promoted=structuredClone(associated);promoted.capture.plan.association[field]=true;
    assert.match(await page.evaluate(value=>{try{projectExplorer.loadRuntimeCapture(JSON.stringify(value));return 'accepted';}catch(e){return e.message;}},promoted),/Invalid runtime/);
  }
  const mismatched=structuredClone(associated);mismatched.capture.plan.association.signals[0].signalId='wrong';
  assert.match(await page.evaluate(value=>{try{projectExplorer.loadRuntimeCapture(JSON.stringify(value));return 'accepted';}catch(e){return e.message;}},mismatched),/Invalid runtime/);
  const malformed=structuredClone(associated);malformed.capture.plan.association.selectedInstance=null;
  assert.deepEqual(await page.evaluate(value=>{const before=projectExplorer.runtimeCapture;try{projectExplorer.loadRuntimeCapture(JSON.stringify(value));return ['accepted',false];}catch(e){return [e.message,projectExplorer.runtimeCapture===before];}},malformed),['Invalid runtime capture export or unsupported evidence claims.',true]);
  const escaped=structuredClone(associated);escaped.capture.plan.request.association.mappings[0].reason='<img src=x onerror=alert(1)>';
  await page.evaluate(value=>projectExplorer.loadRuntimeCapture(JSON.stringify(value)),escaped);
  assert.equal(await page.locator('.runtime-panel img').count(),0);
  check('Capture source mappings render exact retained declarations and refuse promoted or mismatched evidence claims');
  const bound=JSON.parse(await fs.readFile(path.join(root,'fixtures/physical-runtime/capture-handoff.json'),'utf8'));
  await page.evaluate(value=>projectExplorer.loadRuntimeCapture(JSON.stringify(value)),bound);
  await page.locator('.runtime-handoff > summary').click();
  assert.match(await page.locator('.runtime-handoff').innerText(),/No operation or diagnostic assertion is admitted/);
  assert.match(await page.locator('.runtime-handoff').innerText(),/Session owner/);
  assert.match(await page.locator('.runtime-handoff').innerText(),/Capture owner/);
  await shot('runtime-handoff-light-1280');
  for(const mutate of [
    ...['deviceIdentityVerified','programMatchVerified','effectiveRightsVerified','sourceAuthenticityVerified','operationAdmitted','assertionsAdmitted'].map(key=>p=>{p.handoff[key]=true;}),
    p=>{delete p.handoff;},p=>{delete p.request.handoffRef;},p=>{p.handoff.session.context.epoch='0'.repeat(32);},
    p=>{p.handoff.reviewId='0'.repeat(64);},p=>{p.handoff.session.peerVerified=false;},p=>{p.handoff.session.credential.cipherSha256='invalid';},
    p=>{p.handoff.session.subjectId='bad\ud800';},p=>{p.handoff.session.extra=true;},p=>{p.handoff=null;}
  ]){
    const bad=structuredClone(bound);mutate(bad.capture.plan);
    assert.deepEqual(await page.evaluate(value=>{const before=projectExplorer.runtimeCapture;try{projectExplorer.loadRuntimeCapture(JSON.stringify(value));return ['accepted',false];}catch(e){return [e.message,projectExplorer.runtimeCapture===before];}},bad),['Invalid runtime capture export or unsupported evidence claims.',true]);
  }
  const injected=structuredClone(bound);injected.capture.plan.handoff.session.subjectId='<img src=x onerror=alert(1)>';
  await page.evaluate(value=>projectExplorer.loadRuntimeCapture(JSON.stringify(value)),injected);
  assert.equal(await page.locator('.runtime-handoff img').count(),0);
  check('Capture handoff renders separate owners and unverified authority; forged flags, contexts and malformed identities preserve the prior view');
  const alarm=JSON.parse(await fs.readFile(path.join(root,'fixtures/physical-runtime/capture-alarm.json'),'utf8'));
  await page.evaluate(value=>projectExplorer.loadRuntimeCapture(JSON.stringify(value)),alarm);
  await page.locator('.runtime-alarm > summary').click();
  assert.match(await page.locator('.runtime-alarm').innerText(),/Selected for capture/);
  assert.match(await page.locator('.runtime-alarm').innerText(),/Not selected in this capture/);
  assert.match(await page.locator('.runtime-panel').innerText(),/RuntimeTimeout · value unknown/);
  assert.equal(await page.locator('.runtime-alarm details').count(),3);
  await shot('runtime-alarm-light-1280');
  await page.getByRole('button',{name:'Toggle color theme'}).click();await shot('runtime-alarm-dark-1280');
  await page.getByRole('button',{name:'Toggle color theme'}).click();
  for(const mutate of [
    ...['nativeQualified','sourceAuthenticityVerified','runtimeAlarmAssociationVerified','coverageComplete'].map(k=>a=>{a[k]=true;}),
    a=>{a.bindings[2].storage.runtimeCallContextVerified=true;},a=>{a.bindings[0].declaration.liveVerified=true;},
    a=>{a.selectors[0].signalId='other';},a=>{a.bindings[0].signal.datatype='UInt';},
    a=>{a.bindings[1].signal.evidence.liveVerified=true;},a=>{a.bindings[1].storage.gaps=['unresolved'];},
    a=>{a.bindings[0]=null;},a=>{a.bindings=[];},a=>{a.allSelectedMatched=false;}
  ]){
    const bad=structuredClone(alarm);mutate(bad.capture.plan.association.alarmBindings);
    assert.deepEqual(await page.evaluate(value=>{const before=projectExplorer.runtimeCapture;try{projectExplorer.loadRuntimeCapture(JSON.stringify(value));return ['accepted',false];}catch(e){return [e.message,projectExplorer.runtimeCapture===before];}},bad),['Invalid runtime capture export or unsupported evidence claims.',true]);
  }
  const alarmInjected=structuredClone(alarm);
  alarmInjected.capture.plan.association.alarmBindings.bindings[0].operand.metadata.rawLabel='<img src=x onerror=alert(1)>';
  await page.evaluate(value=>projectExplorer.loadRuntimeCapture(JSON.stringify(value)),alarmInjected);
  await page.locator('.runtime-alarm > summary').click();await page.locator('.runtime-alarm details > summary').first().click();
  assert.equal(await page.locator('.runtime-alarm img').count(),0);
  assert.match(await page.locator('.runtime-alarm').innerText(),/<img src=x onerror=alert\(1\)>/);
  const alarmDownload=page.waitForEvent('download');await page.getByRole('button',{name:'Download capture JSON',exact:true}).click();
  const alarmSaved=path.join(output,'alarm-capture-roundtrip.json');await (await alarmDownload).saveAs(alarmSaved);
  assert.equal(await fs.readFile(alarmSaved,'utf8'),JSON.stringify(alarmInjected));
  check('Alarm capture keeps selected and unselected bindings distinct, rejects promoted claims without replacing the view, and escapes original supplied evidence');
  const sinkText=await page.evaluate(h=>{
    // Bypass the loaders: numeric fields must stay escaped at the sink even if validation ever loosens.
    const texts=[],capture=structuredClone(projectExplorer.runtimeCapture.doc),row=capture.rows[0];
    capture.rows=[{...row,sequence:h},{...row,sequence:1,kind:'gap',selectionId:null,readResultJson:null,errorCode:null,skippedPolls:h}];
    projectExplorer.runtimeCapture={doc:capture,text:'',filter:'',page:0};projectExplorer.renderRuntimeCapture();texts.push(document.querySelector('.runtime-panel').innerText);
    const ua={schemaVersion:'diagnostic-ua-observation.v1',acquisitionMode:'polling',limits:'',experimentId:'e',bindingId:'b',snapshotRef:'s',unselectedSignalIds:[],events:{},hypotheses:[],binding:{},
      signals:[{signalId:'s1',observationCount:h,goodValues:h,uncertain:h,bad:h}],captureSource:{recordedProgress:{state:'ended',terminalReason:null},uncommittedDataUnknown:false,plan:{},rows:[{sequence:h,elapsedMilliseconds:0,kind:'read'}]}};
    projectExplorer.runtimeCapture={doc:ua,text:'',filter:'',page:0};projectExplorer.renderRuntimeCapture();texts.push(document.querySelector('.runtime-panel').innerText);
    return texts;
  },'<img src=x onerror=alert(1)>');
  assert.equal(await page.locator('.runtime-panel img').count(),0);
  assert.match(sinkText[0],/<img src=x onerror=alert\(1\)>\t/);assert.match(sinkText[0],/Skipped <img src=x onerror=alert\(1\)> scheduled polls/);
  assert.match(sinkText[1],/s1 · <img src=x onerror=alert\(1\)> observations · <img src=x onerror=alert\(1\)> good/);
  check('Capture and UA observation views escape row sequences, gap counts and signal counts at the HTML sink');
  await page.getByRole('button',{name:'Back to project',exact:true}).click();assert.equal(await page.locator('.workspace').isVisible(),true);
  await page.setViewportSize({width:1440,height:1000});
}
