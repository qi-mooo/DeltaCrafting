let previewFile='',previewLoading=false,previewScrubbing=false,previewController=null,previewGeneration=0,previewError='';
const previewSynth=new HarpPreview.PreviewSynth(renderPreview);
function renderPreview(){
 const loaded=!!previewSynth.song;
 $('preview-name').textContent=previewFile?previewFile.split('/').pop():'请选择曲目';
 $('preview-status').textContent=previewError||(previewLoading?'正在读取 MIDI…':loaded?(previewSynth.playing?'试听中':'已暂停 / 停止')+' · '+Math.round(previewSynth.speed*100)+'%':'请从曲目旁点击试听');
 $('preview-play').disabled=!loaded||previewLoading;
 $('preview-play').textContent=previewSynth.playing?'暂停':'播放';
 $('preview-stop').disabled=!loaded&&!previewLoading;
 $('preview-progress').disabled=!loaded||previewLoading;
 $('preview-progress').max=Math.max(1,previewSynth.song?.duration||0);
 if(!previewScrubbing){$('preview-progress').value=previewSynth.elapsed();$('preview-elapsed').textContent=time(previewSynth.elapsed());}
 $('preview-duration').textContent=time(previewSynth.song?.duration||0);
}
function cancelPreview(){previewGeneration++;if(previewController)previewController.abort();previewController=null;previewLoading=false;previewScrubbing=false;previewSynth.stop();}
function closePreview(){cancelPreview();if($('preview-dialog').open)$('preview-dialog').close();}
async function openPreview(song){
 cancelPreview();previewSynth.song=null;previewFile=song.path;previewError='';previewLoading=true;
 const generation=previewGeneration;const controller=new AbortController();previewController=controller;
 if(!$('preview-dialog').open)$('preview-dialog').showModal();renderPreview();
 const timer=setTimeout(()=>controller.abort(),18000);
 try{
  // Unlock audio inside the user gesture, before the network request.
  await previewSynth.unlock();if(generation!==previewGeneration)return;
  const response=await fetch('/api/v1/preview/midi?file='+encodeURIComponent(song.path),{headers:{Authorization:'Bearer '+session},signal:controller.signal,cache:'no-store'});
  if(!response.ok){const d=await response.json();if(response.status===401)throw new Error('登录已失效，请重新登录');throw new Error(errors[d.error]||d.error||'读取曲目失败');}
  const data=await response.arrayBuffer();if(generation!==previewGeneration)return;
  const midi=HarpPreview.parseMidi(data);
  // Read the persisted options without saving or applying anything on Harp.
  previewSynth.load(midi,{speed:state?.speed||100,transpose:state?.transpose||0,startMs:state?.startMs||0});
  await previewSynth.play();
 }catch(e){if(generation===previewGeneration){previewError=e.name==='AbortError'?'读取超时，请重试':e.message;previewSynth.pause();}}
 finally{clearTimeout(timer);if(generation===previewGeneration){previewController=null;previewLoading=false;renderPreview();}}
}
Object.assign(errors,{stop_before_previewing:'请先停止游戏演奏，再开始网页试听',invalid_preview_file:'曲目路径无效',preview_file_not_found:'曲目不存在，请刷新曲库'});
$('preview-selected').onclick=()=>{const song=[...songs,...favorites].find(s=>s.path===selected);if(song)openPreview(song);else message('请先选择曲目，或点击曲目旁的试听按钮');};
$('preview-play').onclick=async()=>{try{previewError='';if(previewSynth.playing)previewSynth.pause();else await previewSynth.play();}catch(e){previewError=e.message;renderPreview();}};
$('preview-stop').onclick=()=>{cancelPreview();renderPreview();};
$('preview-close').onclick=closePreview;
$('preview-dialog').addEventListener('cancel',e=>{e.preventDefault();closePreview();});
$('preview-dialog').addEventListener('close',()=>{if(previewLoading||previewSynth.playing)cancelPreview();});
$('preview-volume').oninput=()=>previewSynth.setVolume(Number($('preview-volume').value)/100);
$('preview-progress').oninput=()=>{previewScrubbing=true;$('preview-elapsed').textContent=time(Number($('preview-progress').value));};
$('preview-progress').onchange=async()=>{previewScrubbing=false;try{await previewSynth.seek(Number($('preview-progress').value));}catch(e){previewError=e.message;}renderPreview();};
$('preview-progress').onpointercancel=()=>{previewScrubbing=false;renderPreview();};
window.addEventListener('pagehide',closePreview);
