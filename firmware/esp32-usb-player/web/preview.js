/* Offline MIDI melody preview. Synthesized locally; never submits HID commands. */
(function(root){
'use strict';
function parseMidi(input){
 const bytes=new Uint8Array(input),view=new DataView(bytes.buffer,bytes.byteOffset,bytes.byteLength);
 let pos=0,limit=bytes.length;
 const fail=()=>{throw new Error('MIDI 文件损坏或格式不支持');};
 const need=n=>{if(n<0||pos+n>limit)fail();};
 const u8=()=>{need(1);return bytes[pos++];};
 const u16=()=>{need(2);const v=view.getUint16(pos);pos+=2;return v;};
 const u32=()=>{need(4);const v=view.getUint32(pos);pos+=4;return v;};
 const tag=()=>String.fromCharCode(u8(),u8(),u8(),u8());
 const vlq=()=>{let n=0;for(let i=0;i<4;i++){const b=u8();n=n*128+(b&127);if(!(b&128))return n;}fail();};
 if(bytes.length>512*1024||tag()!=='MThd'||u32()!==6)fail();
 const format=u16(),tracks=u16(),ppq=u16();
 if(format>1||!tracks||tracks>32||(format===0&&tracks!==1)||!ppq||(ppq&32768))fail();
 const events=[],tempos=[{tick:0,tempo:500000,order:-1}],stats=new Map();let order=0;
 for(let track=0;track<tracks;track++){
  limit=bytes.length;if(tag()!=='MTrk')fail();const length=u32();need(length);limit=pos+length;
  let tick=0,running=0,ended=false;
  while(pos<limit){
   tick+=vlq();if(!Number.isSafeInteger(tick))fail();
   let status=u8();if(status<128){if(!running)fail();pos--;status=running;}
   if(status===255){
    running=0;const type=u8(),length=vlq();need(length);
    if(type===81){if(length!==3)fail();const tempo=bytes[pos]*65536+bytes[pos+1]*256+bytes[pos+2];if(!tempo)fail();tempos.push({tick,tempo,order:order++});}
    pos+=length;if(type===47){if(length!==0||pos!==limit)fail();ended=true;break;}
   }else if(status===240||status===247){running=0;const n=vlq();need(n);pos+=n;}
   else if(status>=128&&status<240){
    running=status;const type=status>>4,channel=status&15,a=u8(),b=(type===12||type===13)?0:u8();if(a>127||b>127)fail();
    events.push({tick,track,channel,type,a,b,order:order++});
    if(type===9&&b&&channel!==9){
     const key=track+':'+channel,c=stats.get(key)||{track,channel,count:0,sum:0,low:127,high:0};
     c.count++;c.sum+=a;c.low=Math.min(c.low,a);c.high=Math.max(c.high,a);stats.set(key,c);
    }
   }else fail();
   if(order>150000)throw new Error('MIDI 事件过多');
  }
  if(!ended)fail();
 }
 // A few older library files contain harmless padding bytes after the declared
 // track chunks. The ESP32 parser stops at the declared track count too, so
 // keep browser preview behavior consistent while still validating every
 // declared chunk and rejecting truncation.
 if(!stats.size)fail();
 const candidates=[...stats.values()].sort((a,b)=>a.track-b.track||a.channel-b.channel);
 let best=null,score=-Infinity;
 for(const c of candidates){const avg=c.sum/c.count,s=Math.min(c.count,400)+(avg>=55&&avg<=85?80:0)-Math.max(0,c.high-c.low-37)*4;if(s>score){score=s;best=c;}}
 const timeline=[...events,...tempos].sort((a,b)=>a.tick-b.tick||a.order-b.order);
 let tick=0,ms=0,tempo=500000,duration=0;
 for(const e of timeline){ms+=(e.tick-tick)*tempo/ppq/1000;tick=e.tick;if(ms>3600000)fail();if(e.tempo)tempo=e.tempo;else{e.ms=ms;duration=ms;}}
 const melody=events.filter(e=>e.track===best.track&&e.channel===best.channel).sort((a,b)=>a.ms-b.ms||a.order-b.order);
 const first=melody.find(e=>e.type===9&&e.b).ms,held=new Uint16Array(128),latched=new Uint8Array(128),velocity=new Uint8Array(128);
 const segments=[];let sustain=false,sounding=-1,since=0;
 const close=t=>{if(sounding>=0&&t>since)segments.push({start:since-first,end:t-first,pitch:sounding,velocity:velocity[sounding]||80});};
 for(let i=0;i<melody.length;){
  const at=melody[i].ms,onsets=new Set();
  while(i<melody.length&&melody[i].ms===at){const e=melody[i++],p=e.a;
   if(e.type===9&&e.b){held[p]++;latched[p]=0;velocity[p]=e.b;onsets.add(p);}
   else if(e.type===8||e.type===9){if(held[p])held[p]--;if(!held[p])latched[p]=sustain?1:0;}
   else if(e.type===11&&p===64){sustain=e.b>=64;if(!sustain)latched.fill(0);}
   else if(e.type===11&&(p===120||p===123)){held.fill(0);latched.fill(0);sustain=false;}
   else if(e.type===11&&p===121){latched.fill(0);sustain=false;}
  }
  let high=-1;for(let p=127;p>=0;p--)if(held[p]||latched[p]){high=p;break;}
  if(high!==sounding||onsets.has(high)){close(at);sounding=high;since=at;}
 }
 close(duration);
 return {segments,duration:Math.max(0,duration-first),track:best.track,channel:best.channel,notes:best.count};
}
class PreviewSynth{
 constructor(onchange=()=>{}){this.generation=0;this.onchange=onchange;this.context=null;this.master=null;this.nodes=new Set();this.timer=0;this.song=null;this.position=0;this.anchor=0;this.playing=false;this.speed=1;this.transpose=0;this.volume=.5;this.index=0;}
 async unlock(){
  if(!this.context){const C=root.AudioContext||root.webkitAudioContext;if(!C)throw new Error('此浏览器不支持声音试听');this.context=new C();this.master=this.context.createGain();this.master.gain.value=this.volume;this.master.connect(this.context.destination);}
  if(this.context.state!=='running')await this.context.resume();
  if(this.context.state!=='running')throw new Error('请再次点击试听以开启浏览器声音');
 }
 elapsed(){return this.playing?Math.min(this.song.duration,this.position+(this.context.currentTime-this.anchor)*1000*this.speed):this.position;}
 silence(){clearInterval(this.timer);this.timer=0;for(const n of this.nodes){try{n.osc.stop();}catch{}n.osc.disconnect();n.gain.disconnect();}this.nodes.clear();}
 load(song,{speed=100,transpose=0,startMs=0}={}){this.stop();if(startMs>=song.duration&&startMs)throw new Error('起始时间超过曲目时长');this.song=song;this.speed=speed/100;this.transpose=transpose;this.position=startMs;this.onchange();}
 async play(){const generation=++this.generation;await this.unlock();if(generation!==this.generation||!this.song)return;if(this.position>=this.song.duration)this.position=0;if(this.playing)return;this.playing=true;this.anchor=this.context.currentTime;this.index=this.song.segments.findIndex(n=>n.end>this.position);if(this.index<0)this.index=this.song.segments.length;this.schedule();this.timer=setInterval(()=>this.schedule(),25);this.onchange();}
 schedule(){
  if(!this.playing)return;const now=this.elapsed();
  if(now>=this.song.duration){this.position=this.song.duration;this.playing=false;this.silence();this.onchange();return;}
  const horizon=now+180*this.speed;
  while(this.index<this.song.segments.length&&this.song.segments[this.index].start<horizon){const n=this.song.segments[this.index++];if(n.end<=now)continue;
   const pitch=n.pitch+this.transpose;if(pitch<0||pitch>127)continue;
   const when=Math.max(this.context.currentTime,this.anchor+(Math.max(this.position,n.start)-this.position)/1000/this.speed);
   const end=this.anchor+(n.end-this.position)/1000/this.speed;if(end<=when)continue;
   const osc=this.context.createOscillator(),gain=this.context.createGain();osc.type='triangle';osc.frequency.value=440*Math.pow(2,(pitch-69)/12);
   const peak=.28*(n.velocity/127),attack=Math.min(.008,(end-when)/4),release=Math.min(.025,(end-when)/3);
   gain.gain.setValueAtTime(0,when);gain.gain.linearRampToValueAtTime(peak,when+attack);gain.gain.linearRampToValueAtTime(peak*.6,Math.max(when+attack,end-release));gain.gain.linearRampToValueAtTime(0,end);
   osc.connect(gain);gain.connect(this.master);const node={osc,gain};this.nodes.add(node);osc.onended=()=>{this.nodes.delete(node);osc.disconnect();gain.disconnect();};osc.start(when);osc.stop(end+.005);
  }
  this.onchange();
 }
 pause(){this.generation++;if(this.playing)this.position=this.elapsed();this.playing=false;this.silence();this.onchange();}
 stop(){this.generation++;this.playing=false;this.position=0;this.silence();this.onchange();}
 seek(ms){if(!this.song)return;const was=this.playing;this.pause();this.position=Math.max(0,Math.min(this.song.duration,ms));if(was&&this.position<this.song.duration)return this.play();this.onchange();}
 setVolume(value){this.volume=Math.max(0,Math.min(1,value));if(this.master)this.master.gain.setTargetAtTime(this.volume,this.context.currentTime,.01);}
}
root.HarpPreview={parseMidi,PreviewSynth};
if(typeof module!=='undefined')module.exports=root.HarpPreview;
})(typeof globalThis!=='undefined'?globalThis:this);
