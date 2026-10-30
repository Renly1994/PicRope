let editor, baseline, submitted, currentKey, busy=false, ready=false, activeTool='Rect', history={hasUndo:false,hasRedo:false};
const buttons=[...document.querySelectorAll('header button,#toolNavigation button')];
const drawingTools=new Set(['Pen','Arrow','Rect','Ellipse','Polygon','Line']);
const drawingStyle={stroke:'#d64c46',strokeWidth:4};
const textStyle={fontFamily:'Microsoft YaHei',fontSize:24,fontStyle:'normal',fill:'#d64c46',lineHeight:1.2,
  letterSpacing:0,align:'left',opacity:1,shadowOffsetX:0,shadowOffsetY:0,shadowBlur:0,shadowColor:'#000000',shadowOpacity:1};
let textSession,textSelection,textPointer,canvasStage,textRequest=0,textCommit=Promise.resolve();
let textResize,textBoundsFrame;
let toolSession;
const operationTools={Crop:'裁剪',Rotate:'旋转',Resize:'调整尺寸',Brightness:'调色',Filters:'滤镜',Image:'插入图片'};
const operationFields=['annotations','adjustments','resize','finetunes','finetunesProps','filter',
  'pastDesignStates','futureDesignStates','hasUndo','hasRedo','isResetted','haveNotSavedChanges'];
const status=document.getElementById('status');
const stateKey=state=>JSON.stringify([state.annotations,state.adjustments,state.resize,state.finetunes,state.finetunesProps,state.filter],(key,value)=>typeof value==='function'?value.toString():value instanceof HTMLImageElement?value.src:value);
const send=message=>window.chrome.webview.postMessage(message);
const showStatus=(text,error=false)=>{status.textContent=text;status.classList.toggle('error',error);};
const toolTabs={Pen:'Annotate',Arrow:'Annotate',Text:'Annotate',Rect:'Annotate',Ellipse:'Annotate',Polygon:'Annotate',Line:'Annotate',Image:'Annotate',Crop:'Adjust',Rotate:'Adjust',Resize:'Resize',Brightness:'Finetune',Filters:'Filters'};
const nextFrame=()=>new Promise(resolve=>requestAnimationFrame(resolve));
const clickNative=selector=>document.querySelector(selector)?.click();
const readState=()=>new Promise(resolve=>editor.updateState(state=>{resolve(state);return {};}));
async function changeHistory(redo){
  await finishText();
  textSelection=null;
  clickNative(redo?'.FIE_topbar-redo-button':'.FIE_topbar-undo-button');
}

function syncButtons(){
  buttons.forEach(button=>button.disabled=busy||!ready);
  document.getElementById('undo').disabled=busy||!ready||!history.hasUndo;
  document.getElementById('redo').disabled=busy||!ready||!history.hasRedo;
  document.getElementById('style').disabled=busy||!ready||activeTool==='Text'||document.body.dataset.mode!=='annotate';
  document.body.dataset.drawing=String(drawingTools.has(activeTool));
  document.body.dataset.text=String(activeTool==='Text');
  document.getElementById('drawingStyle').hidden=!(drawingTools.has(activeTool)||activeTool==='Text')||!!document.querySelector('.popup:not([hidden])');
  document.querySelector('.preset-widths').hidden=activeTool==='Text';
  document.querySelector('.text-style').hidden=activeTool!=='Text';
  positionDrawingStyle();
  paintTextBounds();
}
function positionDrawingStyle(){
  const anchor=document.querySelector('.tools>button[data-tool="'+activeTool+'"]')||document.getElementById('shapes');
  const bounds=anchor.getBoundingClientRect(),container=document.querySelector('.tools').getBoundingClientRect();
  document.getElementById('drawingStyle').style.left=(bounds.x+bounds.width/2-container.x)+'px';
}
function syncDrawingStyle(state){
  const selected=activeTool==='Text'&&textSelection?state?.annotations[textSelection]:state?.selectionsIds?.length===1?state.annotations[state.selectionsIds[0]]:null;
  const style=selected&&drawingTools.has(selected.name)?selected:drawingStyle;
  if(selected?.name==='Text'&&!textSession)Object.assign(textStyle,{fontSize:selected.fontSize,fill:selected.fill});
  document.querySelectorAll('[data-color]').forEach(button=>button.setAttribute('aria-pressed',String(button.dataset.color===(activeTool==='Text'?textStyle.fill:style.stroke))));
  document.querySelectorAll('[data-width]').forEach(button=>button.setAttribute('aria-pressed',String(Number(button.dataset.width)===style.strokeWidth)));
  document.querySelectorAll('[data-size]').forEach(button=>button.setAttribute('aria-pressed',String(Number(button.dataset.size)===textStyle.fontSize)));
}
function updateTextStyle(change){
  Object.assign(textStyle,change);syncDrawingStyle();
  if(textSession){Object.assign(textSession.style,change);resizeTextInput();send({action:'dirty',value:!!textSession.input.value.trim()||currentKey!==baseline});return;}
  editor.updateState(state=>{
    const id=textSelection;
    const annotation=state.annotations[id];
    if(annotation?.name!=='Text')return {};
    const ratio=change.fontSize?change.fontSize/annotation.fontSize:1;
    return {annotations:{[id]:{...change,width:annotation.width*ratio,height:annotation.height*ratio}},isDesignState:true};
  });
}
function paintTextBounds(){
  const frame=document.getElementById('textBounds');
  const node=textSelection&&canvasStage?.findOne('#'+textSelection);
  frame.hidden=!node||!!textSession||activeTool!=='Text'||busy;
  if(frame.hidden)return;
  const bounds=canvasStage.container().getBoundingClientRect(),matrix=node.getAbsoluteTransform().getMatrix().slice();
  matrix[4]+=bounds.x;matrix[5]+=bounds.y;
  frame.style.transform='matrix('+matrix.join(',')+')';
  frame.style.width=node.width()+'px';frame.style.height=node.height()+'px';
  if(!textBoundsFrame)textBoundsFrame=requestAnimationFrame(()=>{textBoundsFrame=null;paintTextBounds();});
}
function startTextResize(event){
  const handle=event.target.closest('[data-text-corner]');
  const node=textSelection&&canvasStage?.findOne('#'+textSelection);
  if(!handle||!node||busy||event.button!==0)return;
  event.preventDefault();event.stopPropagation();
  textRequest++;
  node.stopDrag();
  const corner=handle.dataset.textCorner;
  textResize={node,handle,pointerId:event.pointerId,
    original:{x:node.x(),y:node.y(),width:node.width(),height:node.height(),fontSize:node.fontSize()},
    inverse:node.getAbsoluteTransform().copy().invert(),position:node.getTransform().copy(),
    opposite:{x:corner.includes('w')?node.width():0,y:corner.includes('n')?node.height():0}};
  handle.setPointerCapture(event.pointerId);
}
function moveTextResize(event){
  if(!textResize||textResize.pointerId!==event.pointerId)return;
  const {node,original,inverse,position,opposite}=textResize;
  const bounds=canvasStage.container().getBoundingClientRect();
  const point=inverse.point({x:event.clientX-bounds.x,y:event.clientY-bounds.y});
  const diagonal={x:opposite.x?-original.width:original.width,y:opposite.y?-original.height:original.height};
  // 按对角线的投影等比例缩放，固定对角位置，让文本框和字号一起变化。
  const ratio=Math.max(8/original.fontSize,Math.min(500/original.fontSize,
    ((point.x-opposite.x)*diagonal.x+(point.y-opposite.y)*diagonal.y)/(original.width**2+original.height**2)));
  const origin=position.point({x:opposite.x*(1-ratio),y:opposite.y*(1-ratio)});
  node.setAttrs({...origin,width:original.width*ratio,height:original.height*ratio,fontSize:original.fontSize*ratio});
  node.getLayer().batchDraw();paintTextBounds();
}
function finishTextResize(cancel=false){
  if(!textResize)return;
  const {node,original,handle,pointerId}=textResize;textResize=null;
  if(handle.hasPointerCapture(pointerId))handle.releasePointerCapture(pointerId);
  if(cancel){node.setAttrs(original);node.getLayer().batchDraw();}
  else if(node.fontSize()!==original.fontSize){
    const change={x:node.x(),y:node.y(),width:node.width(),height:node.height(),fontSize:node.fontSize()};
    editor.updateState({annotations:{[node.id()]:change},isDesignState:true});
    textCommit=(async()=>{await nextFrame();await nextFrame();})();
  }
  paintTextBounds();
}
function resizeTextInput(){
  if(!textSession)return;
  const {input,style}=textSession;
  input.style.fontFamily='"'+style.fontFamily+'",sans-serif';
  input.style.fontSize=style.fontSize+'px';
  input.style.fontWeight=style.fontStyle.includes('bold')?'bold':'normal';
  input.style.fontStyle=style.fontStyle.includes('italic')?'italic':'normal';
  input.style.color=style.fill;input.style.lineHeight=String(style.lineHeight||1.2);
  const context=document.createElement('canvas').getContext('2d');
  context.font=input.style.fontStyle+' '+input.style.fontWeight+' '+style.fontSize+'px '+input.style.fontFamily;
  const lines=(input.value||input.placeholder).split('\n');
  textSession.width=Math.ceil(Math.max(...lines.map(line=>context.measureText(line).width))+style.fontSize/2);
  textSession.height=Math.ceil(lines.length*style.fontSize*(style.lineHeight||1.2));
  input.style.width=textSession.width+'px';input.style.height=textSession.height+'px';
}
async function finishText(cancel=false){
  textRequest++;
  finishTextResize(cancel);
  if(!textSession)return textCommit;
  const session=textSession;textSession=null;
  const value=session.input.value;session.input.remove();
  if(session.node){session.node.show();session.node.getLayer()?.batchDraw();}
  if(!cancel&&value.trim()){
    editor.updateState({annotations:{[session.id]:{...session.annotation,...session.style,
      id:session.id,name:'Text',text:value,x:session.x,y:session.y,width:session.width,height:session.height,
      stroke:'transparent',strokeWidth:0}},selectionsIds:[],isDesignState:true});
    textSelection=session.id;
    textCommit=(async()=>{await nextFrame();await nextFrame();})();
    await textCommit;
    const state=await readState();
    state.designLayer.findOne('#'+session.id)?.draggable(true);
  }
  paintTextBounds();
  send({action:'dirty',value:(currentKey||baseline)!==baseline});
}
async function beginText(event){
  const completion=finishText(),request=textRequest;
  await completion;
  if(request!==textRequest||activeTool!=='Text'||busy)return;
  const state=await readState(),layer=state.designLayer,stage=layer?.getStage();
  // 连续点击只接受最后一次请求，避免异步加载后出现重叠的输入框。
  if(request!==textRequest||activeTool!=='Text'||busy||!stage)return;
  const bounds=stage.container().getBoundingClientRect();
  const point={x:event.clientX-bounds.x,y:event.clientY-bounds.y};
  const hit=stage.getIntersection(point);
  if(hit&&hit.name()!=='Text'&&hit.attrs.draggable)return;
  const node=textNodeAtPoint(stage,point);
  const position=layer.getAbsoluteTransform().copy().invert().point(point);
  if(!node&&(position.x<0||position.y<0||position.x>state.shownImageDimensions.width||position.y>state.shownImageDimensions.height))return;
  const annotation=node?state.annotations[node.id()]:{};
  textSelection=node?node.id():null;
  if(node)Object.assign(textStyle,{fontFamily:annotation.fontFamily,fontSize:annotation.fontSize,fontStyle:annotation.fontStyle,fill:annotation.fill});
  const input=document.createElement('textarea');
  input.className='text-input';input.setAttribute('aria-label','输入文字');input.placeholder='输入文字';input.spellcheck=false;
  input.value=annotation.text||'';
  const matrix=(node||layer).getAbsoluteTransform().getMatrix().slice();
  if(!node){matrix[4]=point.x;matrix[5]=point.y;}
  matrix[4]+=bounds.x;matrix[5]+=bounds.y;
  input.style.transform='matrix('+matrix.join(',')+')';
  textSession={id:node?node.id():'text-'+crypto.randomUUID(),annotation,node,input,
    x:node?annotation.x:position.x,y:node?annotation.y:position.y,style:{...annotation,...textStyle}};
  if(node){node.hide();layer.batchDraw();}
  paintTextBounds();
  input.addEventListener('input',()=>{resizeTextInput();send({action:'dirty',value:input.value!==(annotation.text||'')||currentKey!==baseline});});
  input.addEventListener('keydown',event=>{
    if(event.isComposing||event.keyCode===229){event.stopPropagation();return;}
    if(event.key==='Escape'||(event.key==='Enter'&&!event.shiftKey)){
      event.preventDefault();event.stopPropagation();finishText(event.key==='Escape');
    }
  });
  document.body.appendChild(input);resizeTextInput();input.focus();syncDrawingStyle();
  await nextFrame();
  if(request===textRequest&&textSession?.input===input)input.focus();
}
function textNodeAtPoint(stage,point){
  const hit=stage.getIntersection(point);
  if(hit?.name()==='Text')return hit;
  if(hit?.attrs.draggable)return null;
  // 文字取消输入后，命中画布可能尚未重绘，按实际节点坐标查找。
  return [...stage.find('Text')].reverse().find(node=>{
    if(node.name()!=='Text'||!node.isVisible())return false;
    const local=node.getAbsoluteTransform().copy().invert().point(point);
    return local.x>=0&&local.y>=0&&local.x<=node.width()&&local.y<=node.height();
  })||null;
}
async function updateDrawingStyle(change){
  if(!ready||busy)return;
  Object.assign(drawingStyle,change);syncDrawingStyle();
  editor.render(Object.fromEntries([...drawingTools].map(tool=>[tool,{...drawingStyle}])));
  let selection=[];
  editor.updateState(state=>{
    selection=state.selectionsIds;
    const annotations={...state.annotations};
    let changed=false;
    for(const id of selection){
      if(drawingTools.has(annotations[id]?.name)){
        annotations[id]={...annotations[id],...change};changed=true;
      }
    }
    // 重建当前工具的选项，让颜色和粗细同时作用于已选标注及后续绘制。
    return {toolId:'Image',selectionsIds:[],...(changed?{annotations,isDesignState:true}:{})};
  });
  await nextFrame();
  editor.updateState(state=>{
    syncDrawingStyle({...state,selectionsIds:selection});
    return {toolId:activeTool,selectionsIds:selection};
  });
}
function closeMenus(){
  document.querySelectorAll('.popup').forEach(menu=>menu.hidden=true);
  document.querySelectorAll('[data-panel-toggle]').forEach(button=>button.setAttribute('aria-expanded','false'));
  syncButtons();
}
function chooseTool(tool){
  if(!ready||busy)return;
  finishText();
  textSelection=null;
  const session=operationTools[tool]?(toolSession?.tool===tool?toolSession:{tool,returnTool:toolSession?.returnTool||activeTool}):null;
  toolSession=session;
  activeTool=tool;closeMenus();showStatus('');
  const tab=toolTabs[tool];
  document.body.dataset.mode=tab==='Annotate'?'annotate':tool==='Crop'?'crop':tool==='Rotate'?'rotate':tab.toLowerCase();
  document.body.dataset.options=String(tab!=='Annotate'||tool==='Image');
  document.querySelectorAll('[data-tool]').forEach(button=>button.setAttribute('aria-pressed',String(button.dataset.tool===tool)));
  document.getElementById('shapes').setAttribute('aria-pressed',String(['Rect','Ellipse','Polygon','Line'].includes(tool)));
  document.getElementById('style').setAttribute('aria-pressed',document.body.dataset.options);
  document.body.dataset.operation=String(!!session);
  document.getElementById('toolNavigation').hidden=!session;
  document.getElementById('toolTitle').textContent=operationTools[tool]||'';
  // 切换工具只更新编辑器状态，保留图片、标注和撤销历史。
  editor.updateState(state=>{
    if(session&&!session.snapshot){
      session.snapshot=Object.fromEntries(operationFields.map(key=>[key,state[key]]));
    }
    return {tabId:tab,toolId:tool==='Text'?'Image':tool,selectionsIds:[],pointerCssIcon:tab==='Annotate'?'crosshair':'default'};
  });
  syncButtons();
  syncDrawingStyle();
}
async function finishTool(apply){
  const session=toolSession;
  if(!session?.snapshot)return;
  toolSession=null;
  if(!apply){
    // 更新接口会合并对象；在同一批更新中替换完整快照，避免留下新插入的图片或尺寸字段。
    editor.updateState({annotations:null,adjustments:null,resize:null,finetunesProps:null});
    editor.updateState({...session.snapshot,isDesignState:false,selectionsIds:[]});
  }
  chooseTool(session.returnTool);
  await nextFrame();
  history=await readState();currentKey=stateKey(history);
  syncButtons();send({action:'dirty',value:currentKey!==baseline});
}
function refreshZoom(){
  document.getElementById('zoom').textContent=(document.querySelector('.FIE_topbar-zoom-label')?.textContent||'')+' · 适应窗口';
}

window.startEditor=async function(options){
  document.getElementById('filename').textContent=options.name;
  const source=new Image();
  await new Promise((resolve,reject)=>{source.onload=resolve;source.onerror=reject;source.src=options.source;});
  const {TABS,TOOLS}=FilerobotImageEditor;
  editor=new FilerobotImageEditor(document.getElementById('editor'),{
    source,useBackendTranslations:false,useCloudimage:false,savingPixelRatio:1,previewPixelRatio:window.devicePixelRatio,
    removeSaveButton:true,observePluginContainerSize:true,avoidChangesNotSavedAlertOnLeave:true,
    useZoomPresetsMenu:false,
    defaultSavedImageType:'png',defaultTabId:TABS.ANNOTATE,defaultToolId:TOOLS.RECT,
    tabsIds:[TABS.ANNOTATE,TABS.ADJUST,TABS.RESIZE,TABS.FINETUNE,TABS.FILTERS],
    annotationsCommon:{fill:'#d64c46',stroke:'#d64c46',strokeWidth:4},
    Arrow:{strokeWidth:4},Pen:{strokeWidth:4},Line:{strokeWidth:4},
    Rect:{fill:'transparent'},Ellipse:{fill:'transparent'},Polygon:{fill:'transparent'},
    Text:{text:'',...textStyle,fonts:['Microsoft YaHei'],stroke:'transparent',strokeWidth:0},
    Rotate:{angle:90,componentType:'buttons'},
    theme:{typography:{fontFamily:'"Segoe UI","Microsoft YaHei",sans-serif'},palette:{
      'accent-primary':'#2463ae','accent-primary-active':'#1b5498','bg-primary-active':'#edf4ff','bg-secondary':'#f0f1f3',
      'icons-primary':'#536172','icons-secondary':'#65717f','borders-secondary':'#e5e9ef','borders-primary':'#dce2ea'}},
    translations:window.editorTranslations,
    onSave:()=>window.submit('save'),
    onModify:state=>{history=state;currentKey=stateKey(state);syncButtons();syncDrawingStyle(state);if(ready)send({action:'dirty',value:currentKey!==baseline});}
  });
  editor.render();
  let current;
  for(let attempt=0;attempt<100;attempt++){
    editor.updateState(state=>{
      if(!state.isLoadingGlobally&&state.shownImageDimensions.width>0&&state.shownImageDimensions.height>0&&state.designLayer?.getStage())current=state;
      return {};
    });
    if(current)break;
    await new Promise(resolve=>setTimeout(resolve,50));
  }
  if(!current)throw new Error('编辑画布加载超时');
  baseline=stateKey(current);
  canvasStage=current.designLayer.getStage();
  ready=true;chooseTool('Rect');showStatus('');
  send({action:'ready'});
};

window.imageBounds=function(){
  const layer=canvasStage.findOne('#FIE_design-layer'),transform=layer.getAbsoluteTransform();
  const {clipX:x,clipY:y,clipWidth:width,clipHeight:height}=layer.attrs;
  const points=[[x,y],[x+width,y],[x,y+height],[x+width,y+height]].map(([x,y])=>transform.point({x,y}));
  const left=Math.min(...points.map(p=>p.x)),top=Math.min(...points.map(p=>p.y));
  const bounds=canvasStage.container().getBoundingClientRect();
  return [bounds.x+left,bounds.y+top,Math.max(...points.map(p=>p.x))-left,Math.max(...points.map(p=>p.y))-top];
};

window.submit=async function(action){
  if(!ready||busy)return;
  const toolCompletion=finishTool(true);
  busy=true;closeMenus();document.body.classList.add('busy');syncButtons();showStatus(action==='copy'?'正在复制…':'正在保存…');
  try{
    await toolCompletion;
    await finishText();
    await document.fonts.ready;
    const result=editor.getCurrentImgData({extension:'png'},1);
    submitted=currentKey||stateKey(result.designState);
    send({action:action==='finish'?'save':action,closeAfterSave:action==='finish',data:result.imageData.imageBase64});
  }catch(error){window.completeAction({success:false,message:'无法导出图片，请重试。'});}
};

window.completeAction=function(result){
  busy=false;document.body.classList.remove('busy');syncButtons();
  if(result.success&&result.saved){baseline=submitted;send({action:'dirty',value:false});}
  if(result.name)document.getElementById('filename').textContent=result.name;
  showStatus(result.message,!result.success);
};

document.querySelectorAll('[data-action]').forEach(button=>button.addEventListener('click',()=>window.submit(button.dataset.action)));
document.querySelectorAll('[data-color]').forEach(button=>button.addEventListener('click',()=>activeTool==='Text'?updateTextStyle({fill:button.dataset.color}):updateDrawingStyle({stroke:button.dataset.color})));
document.querySelectorAll('[data-size]').forEach(button=>button.addEventListener('click',()=>updateTextStyle({fontSize:Number(button.dataset.size)})));
document.querySelectorAll('[data-width]').forEach(button=>button.addEventListener('click',()=>updateDrawingStyle({strokeWidth:Number(button.dataset.width)})));
document.querySelectorAll('[data-tool]').forEach(button=>button.addEventListener('click',()=>chooseTool(button.dataset.tool)));
document.querySelectorAll('[data-panel-toggle]').forEach(button=>button.addEventListener('click',()=>{
  const menu=document.getElementById(button.dataset.panelToggle),open=menu.hidden;
  closeMenus();menu.hidden=!open;button.setAttribute('aria-expanded',String(open));refreshZoom();
  syncButtons();
  if(open)menu.querySelector('button').focus();
}));
document.querySelectorAll('[data-native]').forEach(button=>button.addEventListener('click',async()=>{
  if(button.hasAttribute('data-flip')){
    const previous=activeTool;chooseTool('Crop');await nextFrame();
    clickNative(button.dataset.native);await nextFrame();chooseTool(previous);
  }else clickNative(button.dataset.native);
  if(!button.hasAttribute('data-keep-menu'))closeMenus();
  await nextFrame();refreshZoom();
}));
document.getElementById('undo').addEventListener('click',()=>changeHistory(false));
document.getElementById('redo').addEventListener('click',()=>changeHistory(true));
document.getElementById('toolBack').addEventListener('click',()=>finishTool(false));
document.getElementById('toolApply').addEventListener('click',()=>finishTool(true));
document.getElementById('style').addEventListener('click',event=>{
  document.body.dataset.options=String(document.body.dataset.options!=='true');
  event.currentTarget.setAttribute('aria-pressed',document.body.dataset.options);closeMenus();
});
document.getElementById('compare').addEventListener('pointerdown',()=>document.querySelector('.FIE_topbar-center-options button[title="查看原图"]')?.dispatchEvent(new MouseEvent('mousedown',{bubbles:true})));
document.getElementById('compare').addEventListener('pointerup',()=>document.dispatchEvent(new MouseEvent('mouseup',{bubbles:true})));
document.getElementById('compare').addEventListener('pointercancel',()=>document.dispatchEvent(new MouseEvent('mouseup',{bubbles:true})));
document.addEventListener('pointerdown',event=>{
  if(!event.target.closest('.popup,[data-panel-toggle]'))closeMenus();
  if((textSession||activeTool==='Text')&&!event.target.closest('.text-input,#drawingStyle'))finishText();
});
document.getElementById('drawingStyle').addEventListener('mousedown',event=>{
  if(textSession&&event.button===0)event.preventDefault();
});
document.getElementById('textBounds').addEventListener('pointerdown',startTextResize);
document.getElementById('textBounds').addEventListener('pointermove',moveTextResize);
document.getElementById('textBounds').addEventListener('pointerup',()=>finishTextResize());
document.getElementById('textBounds').addEventListener('pointercancel',()=>finishTextResize(true));
document.getElementById('textBounds').addEventListener('lostpointercapture',()=>finishTextResize(true));
document.getElementById('editor').addEventListener('mousedown',event=>{
  if(activeTool==='Text'&&ready&&!busy&&event.button===0&&event.target.closest('.konvajs-content')){
    const bounds=canvasStage.container().getBoundingClientRect(),point={x:event.clientX-bounds.x,y:event.clientY-bounds.y};
    const hit=canvasStage.getIntersection(point);
    textPointer=null;
    const node=textNodeAtPoint(canvasStage,point);
    if(node){event.preventDefault();textPointer={x:event.clientX,y:event.clientY,node};return;}
    if(hit?.attrs.draggable||hit?.findAncestor('Transformer'))return;
    event.preventDefault();event.stopImmediatePropagation();beginText(event);
  }
},true);
document.getElementById('editor').addEventListener('mouseup',event=>{
  if(activeTool==='Text'&&textPointer){
    const moved=Math.hypot(event.clientX-textPointer.x,event.clientY-textPointer.y)>=4;
    if(!moved)textPointer.node.stopDrag();
    textPointer=null;
    if(!moved){event.preventDefault();event.stopImmediatePropagation();beginText(event);}
  }
},true);
document.getElementById('editor').addEventListener('dblclick',event=>{
  if(activeTool==='Text'&&event.target.closest('.konvajs-content'))event.stopImmediatePropagation();
},true);
document.addEventListener('keydown',event=>{
  if(busy){event.preventDefault();event.stopImmediatePropagation();return;}
  if(textResize&&event.key==='Escape'){
    event.preventDefault();event.stopImmediatePropagation();finishTextResize(true);return;
  }
  const menu=document.querySelector('.popup:not([hidden])');
  if(textSession&&!menu&&!event.isComposing&&event.keyCode!==229&&(event.key==='Escape'||(event.key==='Enter'&&!event.shiftKey))){
    event.preventDefault();event.stopImmediatePropagation();finishText(event.key==='Escape');return;
  }
  if(menu&&['Escape','ArrowDown','ArrowUp'].includes(event.key)){
    event.preventDefault();event.stopImmediatePropagation();
    if(event.key==='Escape'){
      const trigger=document.querySelector('[data-panel-toggle="'+menu.id+'"]');closeMenus();trigger.focus();
    }else{
      const items=[...menu.querySelectorAll('button')],index=items.indexOf(document.activeElement),step=event.key==='ArrowDown'?1:-1;
      items[(index+step+items.length)%items.length].focus();
    }
    return;
  }
  if(toolSession&&event.key==='Escape'&&!document.querySelector('.SfxModal-Wrapper [role="dialog"],.FIE_crop-presets-menu')){
    event.preventDefault();event.stopImmediatePropagation();finishTool(false);return;
  }
  if(!(event.ctrlKey||event.metaKey))return;
  const key=event.key.toLowerCase();
  if(!event.target.closest('input,textarea,[contenteditable="true"]')&&(key==='z'||key==='y')){
    event.preventDefault();event.stopImmediatePropagation();changeHistory(key==='y'||event.shiftKey);return;
  }
  let action;
  if(key==='s')action=event.shiftKey?'saveAs':'save';
  if(key==='c'&&event.shiftKey)action='copy';
  if(action){event.preventDefault();event.stopImmediatePropagation();window.submit(action);}
},true);
window.addEventListener('resize',()=>{finishText();positionDrawingStyle();});
window.addEventListener('error',event=>send({action:'error',message:'图片编辑器加载失败，请关闭后重试。',detail:String(event.error?.stack||event.message)}));
window.addEventListener('unhandledrejection',event=>send({action:'error',message:'无法打开这张图片，请关闭后重试。',detail:String(event.reason?.stack||event.reason)}));
