const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const vm=require('node:vm');
const nodeTest=require('node:test');
const test=(name,run)=>nodeTest(name,{timeout:500},run);
const source=fs.readFileSync(path.join(__dirname,'../src/editor/editor.js'),'utf8');

function harness(mode='immediate'){
  let ticks=0,exports=0,config;
  const messages=[],nodes=new Map(),stage={};
  const node=()=>({dataset:{},style:{},classList:{toggle(){},add(){},remove(){}},
    addEventListener(){},setAttribute(){},getBoundingClientRect(){return {x:0,width:20};}});
  const document={body:node(),querySelectorAll:()=>[],addEventListener(){},
    getElementById(id){if(!nodes.has(id))nodes.set(id,node());return nodes.get(id);},
    querySelector(selector){return selector.includes('.popup')?null:this.getElementById(selector);}};
  const state={annotations:{},adjustments:{rotation:90},resize:{},finetunes:[],finetunesProps:{},filter:null,
    isLoadingGlobally:false,shownImageDimensions:{width:640,height:480},designLayer:{getStage:()=>stage},
    selectionsIds:[],hasUndo:false,hasRedo:false};
  class Image {set src(value){this.onload();}}
  class Filerobot {
    static TABS={ANNOTATE:'Annotate'};
    static TOOLS={RECT:'Rect'};
    constructor(container,options){config=options;}
    render(){}
    updateState(update){
      if(mode==='unmounted'||(mode==='delayed'&&ticks<2))return;
      const current={...state,isLoadingGlobally:mode==='timeout'||(mode==='delayed'&&ticks<3),
        designLayer:mode==='no-layer'?undefined:mode==='no-stage'?{getStage:()=>null}:state.designLayer,
        shownImageDimensions:mode==='zero-height'?{width:640,height:0}:state.shownImageDimensions};
      const change=typeof update==='function'?update(current):update;
      Object.assign(state,change);
      if(Object.keys(change).length)config.onModify({...current,...change});
    }
    getCurrentImgData(){exports++;return {imageData:{width:640,height:480},designState:state};}
  }
  const window={devicePixelRatio:2,addEventListener(){},chrome:{webview:{postMessage(message){
    messages.push({...message,ticks});
    if(message.action==='ready'){
      assert.equal(vm.runInContext('baseline===currentKey && ready',context),true,'baseline must precede ready');
    }
  }}}};
  const context=vm.createContext({window,document,Image,HTMLImageElement:Image,FilerobotImageEditor:Filerobot,
    setTimeout(callback,ms){assert.equal(ms,50);assert.ok(++ticks<=100,'timeout must be bounded');queueMicrotask(callback);},
    requestAnimationFrame(){}});
  vm.runInContext(source,context);
  return {start:()=>window.startEditor({name:'test.png',source:'test.png'}),messages,
    get exports(){return exports;},get ticks(){return ticks;},
    modify(){config.onModify({...state,adjustments:{rotation:180}});}};
}

test('initialization never exports and establishes actual baseline before ready',async()=>{
  const h=harness();await h.start();
  assert.equal(h.exports,0,'initialization must not export image pixels');
  assert.equal(h.messages.filter(m=>m.action==='ready').length,1);
  assert.equal(h.messages.some(m=>m.action==='dirty'&&m.value),false);
  h.modify();assert.equal(h.messages.at(-1).value,true);
});
test('waits for delayed mount and image readiness',async()=>{
  const h=harness('delayed');await h.start();
  assert.ok(h.messages.find(m=>m.action==='ready').ticks>=3,'must wait for loaded state');
  assert.equal(h.exports,0);
});
for(const mode of ['timeout','unmounted','no-layer','no-stage','zero-height']){
  test('bounded initialization timeout: '+mode,async()=>{
    const h=harness(mode);await assert.rejects(h.start(),/编辑画布加载超时/);
    assert.equal(h.ticks,100);assert.equal(h.exports,0);
    assert.equal(h.messages.some(m=>m.action==='ready'),false);
  });
}
